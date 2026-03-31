using Dalamud.Utility;
using K4os.Compression.LZ4.Legacy;
using Sphene.API.Data;
using Sphene.API.Dto.Files;
using Sphene.API.Routes;
using Sphene.FileCache;
using Sphene.PlayerData.Handlers;
using Sphene.SpheneConfiguration;
using Sphene.Services.Mediator;
using Sphene.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Reflection;

namespace Sphene.WebAPI.Files;

public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private readonly Dictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCompactor _fileCompactor;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly List<ThrottledStream> _activeDownloadStreams;
    private readonly HttpClient _directDownloadHttpClient;
    private readonly SpheneConfigService _configService;

    public FileDownloadManager(ILogger<FileDownloadManager> logger, SpheneMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, SpheneConfigService configService) : base(logger, mediator)
    {
        _downloadStatus = new Dictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _configService = configService;
        _activeDownloadStreams = [];
        _directDownloadHttpClient = new HttpClient();
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _directDownloadHttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sphene", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
        _directDownloadHttpClient.Timeout = TimeSpan.FromSeconds(300);

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            if (!_activeDownloadStreams.Any()) return;
            var newLimit = _orchestrator.DownloadLimitPerSlot();
            Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
            foreach (var stream in _activeDownloadStreams)
            {
                stream.BandwidthLimit = newLimit;
            }
        });
    }

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    public bool IsDownloading => !CurrentDownloads.Any();

    public static void MungeBuffer(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; ++i)
        {
            buffer[i] ^= 42;
        }
    }

    public async Task<string?> DownloadPmpToCacheAsync(string hash, CancellationToken ct, IProgress<(long TransferredBytes, long TotalBytes)>? progress = null, string? destinationPath = null)
    {
        if (string.IsNullOrWhiteSpace(hash)) throw new ArgumentException("hash must not be null or empty", nameof(hash));
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        var sizeInfo = await FilesGetSizes([hash], ct).ConfigureAwait(false);
        var dto = sizeInfo.FirstOrDefault(d => string.Equals(d.Hash, hash, StringComparison.OrdinalIgnoreCase));
        if (dto == null || dto.IsForbidden || dto.Size <= 0 || string.IsNullOrWhiteSpace(dto.Url))
        {
            Logger.LogWarning("DownloadPmpToCacheAsync: file {hash} not available or forbidden", hash);
            return null;
        }

        var transfer = new DownloadFileTransfer(dto);
        if (_configService.Current.UseSpheneCdnDirectDownloads && transfer.DirectDownloadUri != null)
        {
            try
            {
                Logger.LogDebug("Downloading {hash} via SpheneCDN direct url: {url}", hash, transfer.DirectDownloadUri);
                var compressedBytes = await DownloadCompressedDirectAsync(transfer.DirectDownloadUri, ct, bytes => progress?.Report((bytes, transfer.Total))).ConfigureAwait(false);
                var decompressedFile = LZ4Wrapper.Unwrap(compressedBytes);
                var filePath = !string.IsNullOrWhiteSpace(destinationPath)
                    ? destinationPath
                    : _fileDbManager.GetCacheFilePath(hash, "pmp");
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await _fileCompactor.WriteAllBytesAsync(filePath, decompressedFile, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(destinationPath))
                {
                    PersistFileToStorage(hash, filePath);
                }

                Logger.LogInformation("Download sources summary: objectStorage={objectStorageFiles} fileServer={fileServerFiles}", 1, 0);
                return filePath;
            }
            catch (Exception ex)
            {
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden })
                {
                    Logger.LogDebug(ex, "SpheneCDN direct download not available for {hash}, falling back to file server", hash);
                }
                else
                {
                    Logger.LogInformation(ex, "SpheneCDN direct download failed for {hash}, falling back to file server", hash);
                }
            }
        }
        var downloadGroup = transfer.DownloadUri.Host + ":" + transfer.DownloadUri.Port;

        _downloadStatus[downloadGroup] = new FileDownloadStatus
        {
            DownloadStatus = DownloadStatus.Initializing,
            TotalBytes = transfer.Total,
            TotalFiles = 1,
            TransferredBytes = 0,
            TransferredFiles = 0
        };

        Guid requestId;
        var blockFile = string.Empty;

        try
        {
            Uri? selectedBaseUri = null;
            HttpResponseMessage? requestIdResponse = null;
            Exception? lastException = null;
            foreach (var baseUri in GetFileServerCandidateBaseUris(transfer))
            {
                try
                {
                    requestIdResponse = await _orchestrator.SendRequestAsync(HttpMethod.Post, SpheneFiles.RequestEnqueueFullPath(baseUri), new List<string> { hash }, ct).ConfigureAwait(false);
                    requestIdResponse.EnsureSuccessStatusCode();
                    selectedBaseUri = baseUri;
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (selectedBaseUri == null || requestIdResponse == null)
            {
                throw new HttpRequestException("Failed to enqueue download request on all file server endpoints", lastException);
            }

            var requestContent = await requestIdResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            requestId = Guid.Parse(requestContent.Trim('"'));
            Logger.LogDebug("Downloading {hash} via file server enqueue base url: {baseUrl} (requestId={requestId})", hash, selectedBaseUri, requestId);

            blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");

            Progress<long> internalProgress = new((bytesDownloaded) =>
            {
                try
                {
                    if (_downloadStatus.TryGetValue(downloadGroup, out var status))
                    {
                        status.TransferredBytes += bytesDownloaded;
                        progress?.Report((status.TransferredBytes, status.TotalBytes));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not set PMP download progress");
                }
            });

            await DownloadAndMungeFileHttpClient(downloadGroup, requestId, [transfer], selectedBaseUri, blockFile, internalProgress, ct).ConfigureAwait(false);

            if (_downloadStatus.TryGetValue(downloadGroup, out var statusAfterDownload))
            {
                statusAfterDownload.TransferredFiles = 1;
                statusAfterDownload.DownloadStatus = DownloadStatus.Decompressing;
            }

            using var fileBlockStream = File.OpenRead(blockFile);
            if (fileBlockStream.Length <= 0)
            {
                Logger.LogWarning("DownloadPmpToCacheAsync: empty block file for {hash}", hash);
                return null;
            }

            (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);
            if (!string.Equals(fileHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("DownloadPmpToCacheAsync: hash mismatch in block header, expected {expected}, got {actual}", hash, fileHash);
                return null;
            }

            var compressedFileContent = new byte[fileLengthBytes];
            var readBytes = await fileBlockStream.ReadAsync(compressedFileContent, 0, compressedFileContent.Length, CancellationToken.None).ConfigureAwait(false);
            if (readBytes != fileLengthBytes)
            {
                throw new EndOfStreamException();
            }

            MungeBuffer(compressedFileContent);
            var decompressedFile = LZ4Wrapper.Unwrap(compressedFileContent);
            var filePath = !string.IsNullOrWhiteSpace(destinationPath) 
                ? destinationPath 
                : _fileDbManager.GetCacheFilePath(hash, "pmp");
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await _fileCompactor.WriteAllBytesAsync(filePath, decompressedFile, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                PersistFileToStorage(hash, filePath);
            }

            Logger.LogInformation("Download sources summary: objectStorage={objectStorageFiles} fileServer={fileServerFiles}", 0, 1);
            return filePath;
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("DownloadPmpToCacheAsync: cancelled for {hash}", hash);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "DownloadPmpToCacheAsync: error while downloading PMP {hash}", hash);
            return null;
        }
        finally
        {
            if (!string.IsNullOrEmpty(blockFile))
            {
                try
                {
                    File.Delete(blockFile);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to delete temporary block file {file}", blockFile);
                }
            }

            _downloadStatus.Remove(downloadGroup);
        }
    }

    public async Task<bool> IsFileAvailableAsync(string hash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hash)) throw new ArgumentException("hash must not be null or empty", nameof(hash));
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        var sizeInfo = await FilesGetSizes([hash], ct).ConfigureAwait(false);
        var dto = sizeInfo.FirstOrDefault(d => string.Equals(d.Hash, hash, StringComparison.OrdinalIgnoreCase));
        return dto != null && !dto.IsForbidden && dto.Size > 0 && !string.IsNullOrWhiteSpace(dto.Url);
    }

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
    }

    public async Task DownloadFiles(GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
    {
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(gameObject, fileReplacementDto, ct).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            Mediator.Publish(new DownloadFinishedMessage(gameObject));
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        foreach (var stream in _activeDownloadStreams.ToList())
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // do nothing
                //
            }
        }
        base.Dispose(disposing);
    }

    private static byte MungeByte(int byteOrEof)
    {
        if (byteOrEof == -1)
        {
            throw new EndOfStreamException();
        }

        return (byte)(byteOrEof ^ 42);
    }

    private static (string fileHash, long fileLengthBytes) ReadBlockFileHeader(FileStream fileBlockStream)
    {
        List<char> hashName = [];
        List<char> fileLength = [];
        var separator = (char)MungeByte(fileBlockStream.ReadByte());
        if (separator != '#') throw new InvalidDataException("Data is invalid, first char is not #");

        bool readHash = false;
        while (true)
        {
            int readByte = fileBlockStream.ReadByte();
            if (readByte == -1)
                throw new EndOfStreamException();

            var readChar = (char)MungeByte(readByte);
            if (readChar == ':')
            {
                readHash = true;
                continue;
            }
            if (readChar == '#') break;
            if (!readHash) hashName.Add(readChar);
            else fileLength.Add(readChar);
        }
        return (string.Join("", hashName), long.Parse(string.Join("", fileLength)));
    }

    private async Task DownloadAndMungeFileHttpClient(string downloadGroup, Guid requestId, List<DownloadFileTransfer> fileTransfer, Uri baseUri, string tempPath, IProgress<long> progress, CancellationToken ct)
    {
        Logger.LogDebug("GUID {requestId} on server {uri} for files {files}", requestId, baseUri, string.Join(", ", fileTransfer.Select(c => c.Hash).ToList()));

        await WaitForDownloadReady(fileTransfer, requestId, ct).ConfigureAwait(false);

        _downloadStatus[downloadGroup].DownloadStatus = DownloadStatus.Downloading;

        HttpResponseMessage response = null!;
        var requestUrl = SpheneFiles.CacheGetFullPath(baseUri, requestId);

        Logger.LogDebug("Downloading {requestUrl} for request {id}", requestUrl, requestId);
        try
        {
            response = await _orchestrator.SendRequestAsync(HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {requestUrl}, HttpStatusCode: {code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }

            var fallbackBaseUri = fileTransfer[0].FallbackDownloadUri ?? _orchestrator.FilesCdnFallbackUri;
            if (fallbackBaseUri != null && fallbackBaseUri != baseUri)
            {
                var fallbackRequestUrl = SpheneFiles.CacheGetFullPath(fallbackBaseUri, requestId);
                Logger.LogWarning("Retrying download via fallback file server: {fallbackRequestUrl}", fallbackRequestUrl);
                response = await _orchestrator.SendRequestAsync(HttpMethod.Get, fallbackRequestUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            else
            {
                throw;
            }
        }

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(tempPath);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                var buffer = new byte[bufferSize];

                var bytesRead = 0;
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download of {id} with a speed limit of {limit} to {tempPath}", requestId, limit, tempPath);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                _activeDownloadStreams.Add(stream);
                while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    MungeBuffer(buffer.AsSpan(0, bytesRead));

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    progress.Report(bytesRead);
                }

                Logger.LogDebug("{requestUrl} downloaded to {tempPath}", requestUrl, tempPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                if (!tempPath.IsNullOrEmpty())
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            if (stream != null)
            {
                _activeDownloadStreams.Remove(stream);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", gameObjectHandler.Name);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto));
            }
        }

        CurrentDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred).ToList();

        return CurrentDownloads;
    }

    private async Task DownloadFilesInternal(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        long downloadedViaObjectStorage = 0;
        long downloadedViaFileServer = 0;
        var useSpheneCdnDirectDownloads = _configService.Current.UseSpheneCdnDirectDownloads;

        var downloadGroups = CurrentDownloads.GroupBy(f =>
        {
            var uri = (useSpheneCdnDirectDownloads ? f.DirectDownloadUri : null) ?? f.DownloadUri;
            var hostKey = uri.Host + ":" + uri.Port;
            if (useSpheneCdnDirectDownloads && f.DirectDownloadUri != null && !string.IsNullOrWhiteSpace(f.Hash))
            {
                var prefix = char.ToUpperInvariant(f.Hash[0]);
                return hostKey + ":r2:" + prefix;
            }

            return hostKey;
        }, StringComparer.Ordinal);

        foreach (var downloadGroup in downloadGroups)
        {
            _downloadStatus[downloadGroup.Key] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = downloadGroup.Sum(c => c.Total),
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        await Parallel.ForEachAsync(downloadGroups, new ParallelOptions()
        {
            MaxDegreeOfParallelism = downloadGroups.Count(),
            CancellationToken = ct,
        },
        async (fileGroup, token) =>
        {
            var fileGroupList = fileGroup.ToList();
            if (await TryDownloadGroupDirectAsync(fileGroup.Key, fileGroupList, fileReplacement, token).ConfigureAwait(false))
            {
                Interlocked.Add(ref downloadedViaObjectStorage, fileGroupList.Count);
                return;
            }

            // let server predownload files
            Uri? selectedBaseUri = null;
            HttpResponseMessage? requestIdResponse = null;
            Exception? lastException = null;
            foreach (var baseUri in GetFileServerCandidateBaseUris(fileGroupList[0]))
            {
                try
                {
                    requestIdResponse = await _orchestrator.SendRequestAsync(HttpMethod.Post, SpheneFiles.RequestEnqueueFullPath(baseUri),
                        fileGroupList.Select(c => c.Hash), token).ConfigureAwait(false);
                    requestIdResponse.EnsureSuccessStatusCode();
                    selectedBaseUri = baseUri;
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (selectedBaseUri == null || requestIdResponse == null)
            {
                throw new HttpRequestException("Failed to enqueue download request on all file server endpoints", lastException);
            }

            var requestContent = await requestIdResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            Logger.LogDebug("Sent request for {n} files on server {uri} with result {result}", fileGroupList.Count, selectedBaseUri, requestContent);

            Guid requestId = Guid.Parse(requestContent.Trim('"'));

            Logger.LogDebug("GUID {requestId} for {n} files on server {uri}", requestId, fileGroupList.Count, selectedBaseUri);

            var blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
            FileInfo fi = new(blockFile);
            try
            {
                _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.WaitingForSlot;
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                _downloadStatus[fileGroup.Key].DownloadStatus = DownloadStatus.WaitingForQueue;
                Progress<long> progress = new((bytesDownloaded) =>
                {
                    try
                    {
                        if (!_downloadStatus.TryGetValue(fileGroup.Key, out FileDownloadStatus? value)) return;
                        value.TransferredBytes += bytesDownloaded;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Could not set download progress");
                    }
                });
                await DownloadAndMungeFileHttpClient(fileGroup.Key, requestId, fileGroupList, selectedBaseUri, blockFile, progress, token).ConfigureAwait(false);
                Interlocked.Add(ref downloadedViaFileServer, fileGroupList.Count);
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("{dlName}: Detected cancellation of download, partially extracting files for {id}", fi.Name, gameObjectHandler);
            }
            catch (Exception ex)
            {
                _orchestrator.ReleaseDownloadSlot();
                File.Delete(blockFile);
                Logger.LogError(ex, "{dlName}: Error during download of {id}", fi.Name, requestId);
                ClearDownload();
                return;
            }

            FileStream? fileBlockStream = null;
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var status))
                {
                    status.TransferredFiles = 1;
                    status.DownloadStatus = DownloadStatus.Decompressing;
                }
                fileBlockStream = File.OpenRead(blockFile);
                while (fileBlockStream.Position < fileBlockStream.Length)
                {
                    (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);

                    try
                    {
                        var fileExtension = fileReplacement.First(f => string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                        var filePath = _fileDbManager.GetCacheFilePath(fileHash, fileExtension);
                        Logger.LogDebug("{dlName}: Decompressing {file}:{le} => {dest}", fi.Name, fileHash, fileLengthBytes, filePath);

                        byte[] compressedFileContent = new byte[fileLengthBytes];
                        var readBytes = await fileBlockStream.ReadAsync(compressedFileContent, CancellationToken.None).ConfigureAwait(false);
                        if (readBytes != fileLengthBytes)
                        {
                            throw new EndOfStreamException();
                        }
                        MungeBuffer(compressedFileContent);

                        var decompressedFile = LZ4Wrapper.Unwrap(compressedFileContent);
                        await _fileCompactor.WriteAllBytesAsync(filePath, decompressedFile, CancellationToken.None).ConfigureAwait(false);

                        PersistFileToStorage(fileHash, filePath);
                    }
                    catch (EndOfStreamException)
                    {
                        Logger.LogWarning("{dlName}: Failure to extract file {fileHash}, stream ended prematurely", fi.Name, fileHash);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning(e, "{dlName}: Error during decompression", fi.Name);
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Logger.LogDebug("{dlName}: Failure to extract file header data, stream ended", fi.Name);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{dlName}: Error during block file read", fi.Name);
            }
            finally
            {
                _orchestrator.ReleaseDownloadSlot();
                if (fileBlockStream != null)
                    await fileBlockStream.DisposeAsync().ConfigureAwait(false);
                File.Delete(blockFile);
            }
        }).ConfigureAwait(false);

        Logger.LogInformation("Download sources summary: objectStorage={objectStorageFiles} fileServer={fileServerFiles}", downloadedViaObjectStorage, downloadedViaFileServer);
        Logger.LogDebug("Download end: {id}", gameObjectHandler);

        ClearDownload();
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes, CancellationToken ct)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");
        HttpResponseMessage response;
        try
        {
            response = await _orchestrator.SendRequestAsync(HttpMethod.Get, SpheneFiles.ServerFilesGetSizesFullPath(_orchestrator.FilesCdnUri!), hashes, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (_orchestrator.FilesCdnFallbackUri != null)
        {
            Logger.LogWarning(ex, "FilesGetSizes failed against primary file server, retrying fallback: {fallback}", _orchestrator.FilesCdnFallbackUri);
            response = await _orchestrator.SendRequestAsync(HttpMethod.Get, SpheneFiles.ServerFilesGetSizesFullPath(_orchestrator.FilesCdnFallbackUri), hashes, ct).ConfigureAwait(false);
        }

        if (_orchestrator.FilesCdnFallbackUri != null)
        {
            Logger.LogDebug("FilesGetSizes completed. primary={primary}, fallback={fallback}", _orchestrator.FilesCdnUri, _orchestrator.FilesCdnFallbackUri);
        }

        return await response.Content.ReadFromJsonAsync<List<DownloadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private async Task<byte[]> DownloadCompressedDirectAsync(Uri directUri, CancellationToken ct, Action<long>? progress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, directUri);
        using var response = await _directDownloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                Logger.LogDebug("Direct download not available for {url}, status={status}", directUri, response.StatusCode);
            }
            else
            {
                Logger.LogInformation("Direct download failed for {url}, status={status}", directUri, response.StatusCode);
            }
            throw new HttpRequestException($"Direct download failed: {directUri}", null, response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[65536];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                totalRead += read;
                progress?.Invoke(totalRead);
            }

            return ms.ToArray();
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> TryDownloadGroupDirectAsync(string downloadGroupKey, List<DownloadFileTransfer> fileTransfers, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        if (!_configService.Current.UseSpheneCdnDirectDownloads)
        {
            return false;
        }

        if (fileTransfers.Count == 0)
        {
            return true;
        }

        if (fileTransfers.Any(t => t.DirectDownloadUri == null))
        {
            return false;
        }

        if (!_downloadStatus.TryGetValue(downloadGroupKey, out var status))
        {
            return false;
        }

        var slotAcquired = false;
        try
        {
            status.DownloadStatus = DownloadStatus.WaitingForSlot;
            await _orchestrator.WaitForDownloadSlotAsync(ct).ConfigureAwait(false);
            slotAcquired = true;
            status.DownloadStatus = DownloadStatus.Downloading;

            foreach (var transfer in fileTransfers)
            {
                ct.ThrowIfCancellationRequested();
                var directUri = transfer.DirectDownloadUri!;
                Logger.LogDebug("Downloading {hash} via SpheneCDN direct url: {url}", transfer.Hash, directUri);
                long lastProgress = 0;
                var compressed = await DownloadCompressedDirectAsync(directUri, ct, total =>
                {
                    var delta = total - lastProgress;
                    if (delta <= 0) return;
                    lastProgress = total;
                    status.TransferredBytes += delta;
                }).ConfigureAwait(false);

                status.DownloadStatus = DownloadStatus.Decompressing;

                var replacement = fileReplacement.FirstOrDefault(f => string.Equals(f.Hash, transfer.Hash, StringComparison.OrdinalIgnoreCase));
                if (replacement == null || replacement.GamePaths == null || replacement.GamePaths.Length == 0)
                {
                    Logger.LogWarning("Direct download: no game path mapping found for {hash}, skipping", transfer.Hash);
                    continue;
                }

                var fileExtension = replacement.GamePaths[0].Split(".")[^1];
                var filePath = _fileDbManager.GetCacheFilePath(transfer.Hash, fileExtension);
                var decompressed = LZ4Wrapper.Unwrap(compressed);
                await _fileCompactor.WriteAllBytesAsync(filePath, decompressed, CancellationToken.None).ConfigureAwait(false);
                PersistFileToStorage(transfer.Hash, filePath);
            }

            status.TransferredFiles = 1;
            return true;
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden })
            {
                Logger.LogDebug(ex, "Direct download not available for group {group}, falling back to server flow", downloadGroupKey);
            }
            else
            {
                Logger.LogInformation(ex, "Direct download failed for group {group}, falling back to server flow", downloadGroupKey);
            }
            return false;
        }
        finally
        {
            if (slotAcquired)
            {
                _orchestrator.ReleaseDownloadSlot();
            }
        }
    }

    private List<Uri> GetFileServerCandidateBaseUris(DownloadFileTransfer transfer)
    {
        var candidates = new List<Uri>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Uri? uri)
        {
            if (uri == null) return;
            var key = uri.ToString();
            if (seen.Add(key))
            {
                candidates.Add(uri);
            }
        }

        Add(transfer.DownloadUri);
        Add(transfer.FallbackDownloadUri);
        Add(_orchestrator.FilesCdnFallbackUri);

        return candidates;
    }

    private void PersistFileToStorage(string fileHash, string filePath)
    {
        var fi = new FileInfo(filePath);
        Func<DateTime> RandomDayInThePast()
        {
            DateTime start = new(1995, 1, 1, 1, 1, 1, DateTimeKind.Local);
            Random gen = new();
            int range = (DateTime.Today - start).Days;
            return () => start.AddDays(gen.Next(range));
        }

        fi.CreationTime = RandomDayInThePast().Invoke();
        fi.LastAccessTime = DateTime.Today;
        fi.LastWriteTime = RandomDayInThePast().Invoke();
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath);
            if (entry != null && !string.Equals(entry.Hash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Hash mismatch after extracting, got {hash}, expected {expectedHash}, deleting file", entry.Hash, fileHash);
                File.Delete(filePath);
                _fileDbManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry");
        }
    }

    private async Task WaitForDownloadReady(List<DownloadFileTransfer> downloadFileTransfer, Guid requestId, CancellationToken downloadCt)
    {
        bool alreadyCancelled = false;
        try
        {
            CancellationTokenSource localTimeoutCts = new();
            localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationTokenSource composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);

            while (!_orchestrator.IsDownloadReady(requestId))
            {
                try
                {
                    await Task.Delay(250, composite.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (downloadCt.IsCancellationRequested) throw;

                    var req = await _orchestrator.SendRequestAsync(HttpMethod.Get, SpheneFiles.RequestCheckQueueFullPath(downloadFileTransfer[0].DownloadUri, requestId),
                        downloadFileTransfer.Select(c => c.Hash).ToList(), downloadCt).ConfigureAwait(false);
                    req.EnsureSuccessStatusCode();
                    localTimeoutCts.Dispose();
                    composite.Dispose();
                    localTimeoutCts = new();
                    localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);
                }
            }

            localTimeoutCts.Dispose();
            composite.Dispose();

            Logger.LogDebug("Download {requestId} ready", requestId);
        }
        catch (TaskCanceledException)
        {
            try
            {
                await _orchestrator.SendRequestAsync(HttpMethod.Get, SpheneFiles.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                alreadyCancelled = true;
            }
            catch
            {
                // ignore whatever happens here
            }

            throw;
        }
        finally
        {
            if (downloadCt.IsCancellationRequested && !alreadyCancelled)
            {
                try
                {
                    await _orchestrator.SendRequestAsync(HttpMethod.Get, SpheneFiles.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                }
                catch
                {
                    // ignore whatever happens here
                }
            }
            _orchestrator.ClearDownloadRequest(requestId);
        }
    }
}
