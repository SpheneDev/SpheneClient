using Sphene.API.Data;
using Sphene.API.Dto.Files;
using Sphene.API.Routes;
using Sphene.FileCache;
using Sphene.SpheneConfiguration;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI;
using Sphene.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Sphene.WebAPI.Files;

public sealed class FileUploadManager : DisposableMediatorSubscriberBase
{
    public sealed record UploadFilesForUsersResult(
        List<string> LocallyMissingHashes,
        List<string> ForbiddenHashes,
        List<string> SkippedTooLargeHashes,
        List<string> FailedHashes);

    private enum UploadSingleFileResult
    {
        Success,
        SkippedTooLarge,
        Failed,
    }

    private readonly FileCacheManager _fileDbManager;
    private readonly SpheneConfigService _SpheneConfigService;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ServerConfigurationManager _serverManager;
    private readonly HashSet<string> _modUploadHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _verifiedUploadedHashes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _uploadCancellationTokenSource = new();
    private DateTime _lastHashCleanup = DateTime.UtcNow;

    public FileUploadManager(ILogger<FileUploadManager> logger, SpheneMediator mediator,
        SpheneConfigService SpheneConfigService,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileDbManager,
        ServerConfigurationManager serverManager) : base(logger, mediator)
    {
        _SpheneConfigService = SpheneConfigService;
        _orchestrator = orchestrator;
        _fileDbManager = fileDbManager;
        _serverManager = serverManager;

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            Reset();
        });
    }

    public List<FileTransfer> CurrentUploads { get; } = [];
    public bool IsUploading => CurrentUploads.Count > 0;
    public bool IsInitialized => _orchestrator.IsInitialized;

    private const long MaxUploadSizeBytes = 1024L * 1024L * 1024L;

    public bool IsPenumbraModUpload(string hash)
    {
        return _modUploadHashes.Contains(hash);
    }

    public bool CancelUpload()
    {
        if (CurrentUploads.Any())
        {
            Logger.LogDebug("Cancelling current upload");
            _uploadCancellationTokenSource?.Cancel();
            _uploadCancellationTokenSource?.Dispose();
            _uploadCancellationTokenSource = null;
            CurrentUploads.Clear();
            _modUploadHashes.Clear();
            return true;
        }

        return false;
    }

    public async Task DeleteAllFiles()
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        await _orchestrator.SendRequestAsync(HttpMethod.Post, SpheneFiles.ServerFilesDeleteAllFullPath(_orchestrator.FilesCdnUri!)).ConfigureAwait(false);
    }

    public async Task<List<string>> UploadFiles(List<string> hashesToUpload, IProgress<string> progress, CancellationToken? ct = null)
    {
        Logger.LogDebug("Trying to upload files");
        
        // Clean up old verified hashes periodically to prevent memory growth
        if (DateTime.UtcNow - _lastHashCleanup > TimeSpan.FromHours(1))
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-24);
            var keysToRemove = _verifiedUploadedHashes.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();
            foreach (var key in keysToRemove)
            {
                _verifiedUploadedHashes.Remove(key);
            }
            _lastHashCleanup = DateTime.UtcNow;
            Logger.LogDebug("Cleaned up {count} old verified upload hashes", keysToRemove.Count);
        }
        
        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Any())
        {
            return locallyMissingFiles;
        }

        progress.Report($"Starting upload for {filesPresentLocally.Count} files");

        var filesToUpload = await FilesSend([.. filesPresentLocally], [], ct ?? CancellationToken.None).ConfigureAwait(false);

        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)];
        }

        Task uploadTask = Task.CompletedTask;
        int i = 1;
        foreach (var file in filesToUpload)
        {
            progress.Report($"Uploading file {i++}/{filesToUpload.Count}. Please wait until the upload is completed.");
            Logger.LogDebug("[{hash}] Compressing", file);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, ct ?? CancellationToken.None).ConfigureAwait(false);
            Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
            await uploadTask.ConfigureAwait(false);
            uploadTask = UploadFile(data.Item2, file.Hash, false, ct ?? CancellationToken.None);
            (ct ?? CancellationToken.None).ThrowIfCancellationRequested();
        }

        await uploadTask.ConfigureAwait(false);

        return [];
    }

    public async Task<CharacterData> UploadFiles(CharacterData data, List<UserData> visiblePlayers)
    {
        CancelUpload();

        _uploadCancellationTokenSource = new CancellationTokenSource();
        var uploadToken = _uploadCancellationTokenSource.Token;
        Logger.LogDebug("Sending Character data {hash} to service {url}", data.DataHash.Value, _serverManager.CurrentApiUrl);

        HashSet<string> unverifiedUploads = GetUnverifiedFiles(data);
        if (unverifiedUploads.Any())
        {
            await UploadUnverifiedFiles(unverifiedUploads, visiblePlayers, uploadToken).ConfigureAwait(false);
            Logger.LogDebug("Upload complete for {hash}", data.DataHash.Value);
        }

        foreach (var kvp in data.FileReplacements)
        {
            data.FileReplacements[kvp.Key].RemoveAll(i => _orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, i.Hash, StringComparison.OrdinalIgnoreCase)));
        }

        return data;
    }

    public async Task<bool> ReshareFileAsync(string hash, string recipientUid, string modFolderName, ModInfoDto modInfo, IProgress<string> progress, CancellationToken ct)
    {
        Logger.LogDebug("Resharing file {hash} to {recipient}", hash, recipientUid);

        // 1. Try to tell server to send it
        var filesToUpload = await FilesSend([hash], [recipientUid], ct,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { hash, modFolderName } },
            [modInfo]).ConfigureAwait(false);

        // 2. If server has it (returns empty list), we are done.
        if (filesToUpload.Count == 0)
        {
            return true;
        }

        // 3. If server needs it, check if we have it locally
        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            throw new InvalidOperationException("File is forbidden on server.");
        }

        // We only expect one file here
        var fileToUpload = filesToUpload[0];

        var localCache = _fileDbManager.GetFileCacheByHash(fileToUpload.Hash);
        if (localCache == null)
        {
            // Server needs it, but we don't have it.
            return false;
        }

        // 4. We have it locally, upload it.
        progress.Report("File missing on server, uploading from local cache...");

        var addedUploadItem = false;
        try
        {
            if (CurrentUploads.All(u => !string.Equals(u.Hash, fileToUpload.Hash, StringComparison.Ordinal)))
            {
                CurrentUploads.Add(new UploadFileTransfer(fileToUpload)
                {
                    Total = new FileInfo(localCache.ResolvedFilepath).Length,
                });
                addedUploadItem = true;
            }

            _modUploadHashes.Add(fileToUpload.Hash);

            Logger.LogDebug("[{hash}] Compressing for reshare", fileToUpload.Hash);
            var data = await _fileDbManager.GetCompressedFileData(fileToUpload.Hash, ct).ConfigureAwait(false);

            var uploadItem = CurrentUploads.FirstOrDefault(e => string.Equals(e.Hash, data.Item1, StringComparison.Ordinal));
            if (uploadItem != null)
            {
                uploadItem.Total = data.Item2.Length;
            }

            await UploadFile(data.Item2, fileToUpload.Hash, true, ct).ConfigureAwait(false);

            progress.Report("Upload completed, notifying recipient...");

            var remaining = await FilesSend([hash], [recipientUid], ct,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { hash, modFolderName } },
                [modInfo]).ConfigureAwait(false);

            if (remaining.Exists(f => f.IsForbidden))
            {
                throw new InvalidOperationException("File is forbidden on server.");
            }

            return remaining.Count == 0;
        }
        finally
        {
            _modUploadHashes.Remove(fileToUpload.Hash);
            if (addedUploadItem)
            {
                CurrentUploads.RemoveAll(u => string.Equals(u.Hash, fileToUpload.Hash, StringComparison.Ordinal));
            }
        }
    }

    public async Task<UploadFilesForUsersResult> UploadFilesForUsers(List<string> hashesToUpload, List<UserData> recipients, IProgress<string> progress, CancellationToken ct, Dictionary<string, string>? modFolderNames = null, List<ModInfoDto>? modInfo = null)
    {
        Logger.LogDebug("Trying to upload files for {count} recipients", recipients?.Count ?? 0);

        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Any())
        {
            return new UploadFilesForUsersResult(locallyMissingFiles, [], [], []);
        }

        progress.Report($"Starting upload for {filesPresentLocally.Count} files");

        var uids = (recipients ?? []).Select(r => r.UID).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToList();
        var filesToUpload = await FilesSend([.. filesPresentLocally], uids, ct, modFolderNames, modInfo).ConfigureAwait(false);

        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return new UploadFilesForUsersResult([], [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)], [], []);
        }

        var uniqueFilesToUpload = new List<UploadFileDto>(filesToUpload.Count);
        var seenUploadHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in filesToUpload)
        {
            if (file.IsForbidden)
            {
                continue;
            }

            var hash = file.Hash ?? string.Empty;
            if (string.IsNullOrWhiteSpace(hash) || !seenUploadHashes.Add(hash))
            {
                continue;
            }

            uniqueFilesToUpload.Add(file);
        }

        foreach (var file in uniqueFilesToUpload)
        {
            try
            {
                if (CurrentUploads.All(u => !string.Equals(u.Hash, file.Hash, StringComparison.Ordinal)))
                {
                    CurrentUploads.Add(new UploadFileTransfer(file)
                    {
                        Total = new FileInfo(_fileDbManager.GetFileCacheByHash(file.Hash)!.ResolvedFilepath).Length,
                    });
                }

                if (modFolderNames != null && modFolderNames.ContainsKey(file.Hash))
                {
                    _modUploadHashes.Add(file.Hash);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tried to request file {hash} but file was not present", file.Hash);
            }
        }

        var skippedTooLarge = new List<string>();
        var failed = new List<string>();

        Task<UploadSingleFileResult>? uploadTask = null;
        string? uploadTaskHash = null;
        int i = 1;
        foreach (var file in uniqueFilesToUpload)
        {
            progress.Report($"Uploading file {i++}/{uniqueFilesToUpload.Count}. Please wait until the upload is completed.");
            var cacheEntry = _fileDbManager.GetFileCacheByHash(file.Hash);
            var localPath = cacheEntry?.ResolvedFilepath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                var fileSizeBytes = new FileInfo(localPath).Length;
                if (fileSizeBytes >= MaxUploadSizeBytes)
                {
                    if (uploadTask != null && uploadTaskHash != null)
                    {
                        var result = await uploadTask.ConfigureAwait(false);
                        AddUploadResult(uploadTaskHash, result, skippedTooLarge, failed);
                        uploadTask = null;
                        uploadTaskHash = null;
                    }

                    skippedTooLarge.Add(file.Hash);
                    Logger.LogWarning("[{hash}] Skipping upload: file is {size} and exceeds 1GB", file.Hash, UiSharedService.ByteToString(fileSizeBytes));
                    continue;
                }
            }

            Logger.LogDebug("[{hash}] Compressing", file.Hash);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, ct).ConfigureAwait(false);
            Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);

            if (uploadTask != null && uploadTaskHash != null)
            {
                var previousResult = await uploadTask.ConfigureAwait(false);
                AddUploadResult(uploadTaskHash, previousResult, skippedTooLarge, failed);
            }

            var uploadItem = CurrentUploads.FirstOrDefault(e => string.Equals(e.Hash, data.Item1, StringComparison.Ordinal));
            if (uploadItem != null) uploadItem.Total = data.Item2.Length;
            uploadTaskHash = file.Hash;
            uploadTask = UploadFileForUsersAsync(data.Item2, file.Hash, postProgress: true, ct);
            ct.ThrowIfCancellationRequested();
        }

        if (uploadTask != null && uploadTaskHash != null)
        {
            var lastResult = await uploadTask.ConfigureAwait(false);
            AddUploadResult(uploadTaskHash, lastResult, skippedTooLarge, failed);
        }

        var failedSet = new HashSet<string>(failed, StringComparer.Ordinal);
        var skippedTooLargeSet = new HashSet<string>(skippedTooLarge, StringComparer.Ordinal);
        var successfulHashes = new List<string>(uniqueFilesToUpload.Count);
        foreach (var file in uniqueFilesToUpload)
        {
            if (failedSet.Contains(file.Hash) || skippedTooLargeSet.Contains(file.Hash))
            {
                continue;
            }

            successfulHashes.Add(file.Hash);
        }

        if (successfulHashes.Count > 0 && uids.Count > 0)
        {
            var remaining = await FilesSend(successfulHashes, uids, ct, modFolderNames, modInfo).ConfigureAwait(false);
            foreach (var file in remaining)
            {
                if (file.IsForbidden)
                {
                    continue;
                }

                if (!failedSet.Contains(file.Hash))
                {
                    failed.Add(file.Hash);
                    failedSet.Add(file.Hash);
                }
            }
        }

        CurrentUploads.Clear();
        _modUploadHashes.Clear();

        return new UploadFilesForUsersResult([], [], skippedTooLarge, failed);
    }

    private static void AddUploadResult(string hash, UploadSingleFileResult result, List<string> skippedTooLarge, List<string> failed)
    {
        switch (result)
        {
            case UploadSingleFileResult.SkippedTooLarge:
                skippedTooLarge.Add(hash);
                break;
            case UploadSingleFileResult.Failed:
                failed.Add(hash);
                break;
        }
    }

    private async Task<UploadSingleFileResult> UploadFileForUsersAsync(byte[] compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken)
    {
        if (!_orchestrator.IsInitialized)
        {
            return UploadSingleFileResult.Failed;
        }

        if (compressedFile.LongLength >= MaxUploadSizeBytes)
        {
            Logger.LogWarning("[{hash}] Skipping upload: compressed payload is {size} and exceeds 1GB", fileHash, UiSharedService.ByteToString(compressedFile.LongLength));
            return UploadSingleFileResult.SkippedTooLarge;
        }

        if (uploadToken.IsCancellationRequested)
        {
            return UploadSingleFileResult.Failed;
        }

        var tryMungedFirst = _SpheneConfigService.Current.UseAlternativeFileUpload;
        if (tryMungedFirst)
        {
            return await UploadFileForUsersStreamAsync(compressedFile, fileHash, munged: true, postProgress, uploadToken).ConfigureAwait(false);
        }

        var primary = await UploadFileForUsersStreamAsync(compressedFile, fileHash, munged: false, postProgress, uploadToken).ConfigureAwait(false);
        if (primary == UploadSingleFileResult.Success)
        {
            return primary;
        }

        if (primary == UploadSingleFileResult.SkippedTooLarge)
        {
            return primary;
        }

        var bufferCopy = new byte[compressedFile.Length];
        Buffer.BlockCopy(compressedFile, 0, bufferCopy, 0, compressedFile.Length);
        return await UploadFileForUsersStreamAsync(bufferCopy, fileHash, munged: true, postProgress, uploadToken).ConfigureAwait(false);
    }

    private async Task<UploadSingleFileResult> UploadFileForUsersStreamAsync(byte[] compressedFile, string fileHash, bool munged, bool postProgress, CancellationToken uploadToken)
    {
        try
        {
            if (munged)
            {
                FileDownloadManager.MungeBuffer(compressedFile.AsSpan());
            }

            using var ms = new MemoryStream(compressedFile);

            Progress<UploadProgress>? prog = !postProgress ? null : new((prog) =>
            {
                try
                {
                    var uploadItem = CurrentUploads.FirstOrDefault(f => string.Equals(f.Hash, fileHash, StringComparison.Ordinal));
                    if (uploadItem != null) uploadItem.Transferred = prog.Uploaded;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[{hash}] Could not set upload progress", fileHash);
                }
            });

            var streamContent = new ProgressableStreamContent(ms, prog);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            HttpResponseMessage response;
            if (!munged)
            {
                response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, SpheneFiles.ServerFilesUploadFullPath(_orchestrator.FilesCdnUri!, fileHash), streamContent, uploadToken).ConfigureAwait(false);
            }
            else
            {
                response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, SpheneFiles.ServerFilesUploadMunged(_orchestrator.FilesCdnUri!, fileHash), streamContent, uploadToken).ConfigureAwait(false);
            }

            Logger.LogDebug("[{hash}] Upload Status: {status}", fileHash, response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                _verifiedUploadedHashes[fileHash] = DateTime.UtcNow;
                return UploadSingleFileResult.Success;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
            {
                Logger.LogWarning("[{hash}] Skipping upload: server rejected payload as too large ({status})", fileHash, response.StatusCode);
                return UploadSingleFileResult.SkippedTooLarge;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var content = string.Empty;
                try
                {
                    content = await response.Content.ReadAsStringAsync(uploadToken).ConfigureAwait(false);
                }
                catch
                {
                    content = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(content) && content.Contains("too large", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning("[{hash}] Skipping upload: server rejected payload as too large ({status})", fileHash, response.StatusCode);
                    return UploadSingleFileResult.SkippedTooLarge;
                }
            }

            Logger.LogWarning("[{hash}] Upload failed with status {status}", fileHash, response.StatusCode);
            return UploadSingleFileResult.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{hash}] Upload failed", fileHash);
            return UploadSingleFileResult.Failed;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Reset();
    }

    private async Task<List<UploadFileDto>> FilesSend(List<string> hashes, List<string> uids, CancellationToken ct, Dictionary<string, string>? modFolderNames = null, List<ModInfoDto>? modInfo = null)
    {
        Logger.LogDebug("FilesSend called - IsInitialized: {isInitialized}, FilesCdnUri: {filesCdnUri}", _orchestrator.IsInitialized, _orchestrator.FilesCdnUri);
        Logger.LogDebug("Server connection status: {serverState}", _serverManager.CurrentServer?.ServerName ?? "No server");
        if (!_orchestrator.IsInitialized) 
        {
            Logger.LogError("[DEBUG] FileTransferManager is not initialized! FilesCdnUri: {filesCdnUri}, Server: {server}", _orchestrator.FilesCdnUri, _serverManager.CurrentServer?.ServerName ?? "No server");
            throw new InvalidOperationException("FileTransferManager is not initialized");
        }
        FilesSendDto filesSendDto = new()
        {
            FileHashes = hashes,
            UIDs = uids,
            ModFolderNames = modFolderNames,
            ModInfo = modInfo
        };
        var response = await _orchestrator.SendRequestAsync(HttpMethod.Post, SpheneFiles.ServerFilesFilesSendFullPath(_orchestrator.FilesCdnUri!), filesSendDto, ct).ConfigureAwait(false);

        string raw = string.Empty;
        try
        {
            raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "FilesSend failed to read response body (status {status})", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            var preview = raw ?? string.Empty;
            if (preview.Length > 2000)
            {
                preview = preview[..2000];
            }

            Logger.LogWarning("FilesSend failed with status {status}. Body: {body}", response.StatusCode, preview);
            var exceptionPreview = preview;
            if (string.IsNullOrWhiteSpace(exceptionPreview))
            {
                exceptionPreview = "<empty>";
            }
            else if (exceptionPreview.Length > 500)
            {
                exceptionPreview = exceptionPreview[..500];
            }

            throw new InvalidOperationException($"FilesSend failed with status {(int)response.StatusCode} ({response.StatusCode}). Body: {exceptionPreview}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<UploadFileDto>>(raw) ?? [];
        }
        catch (Exception ex)
        {
            var preview = raw;
            if (preview.Length > 2000)
            {
                preview = preview[..2000];
            }

            Logger.LogWarning(ex, "FilesSend returned invalid JSON. Body: {body}", preview);
            throw new InvalidOperationException($"FilesSend returned invalid JSON (status {(int)response.StatusCode} ({response.StatusCode})).", ex);
        }
    }

    private HashSet<string> GetUnverifiedFiles(CharacterData data)
    {
        HashSet<string> unverifiedUploadHashes = new(StringComparer.Ordinal);
        foreach (var item in data.FileReplacements.SelectMany(c => c.Value.Where(f => string.IsNullOrEmpty(f.FileSwapPath)).Select(v => v.Hash).Distinct(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList())
        {
            if (!_verifiedUploadedHashes.TryGetValue(item, out var verifiedTime))
            {
                verifiedTime = DateTime.MinValue;
            }

            if (verifiedTime < DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10)))
            {
                Logger.LogTrace("Verifying {item}, last verified: {date}", item, verifiedTime);
                unverifiedUploadHashes.Add(item);
            }
        }

        return unverifiedUploadHashes;
    }

    private void Reset()
    {
        _uploadCancellationTokenSource?.Cancel();
        _uploadCancellationTokenSource?.Dispose();
        _uploadCancellationTokenSource = null;
        CurrentUploads.Clear();
        _modUploadHashes.Clear();
        _verifiedUploadedHashes.Clear();
    }

    private async Task UploadFile(byte[] compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        Logger.LogInformation("[{hash}] Uploading {size}", fileHash, UiSharedService.ByteToString(compressedFile.Length));

        if (uploadToken.IsCancellationRequested) return;

        try
        {
            await UploadFileStream(compressedFile, fileHash, _SpheneConfigService.Current.UseAlternativeFileUpload, postProgress, uploadToken).ConfigureAwait(false);
            _verifiedUploadedHashes[fileHash] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            if (!_SpheneConfigService.Current.UseAlternativeFileUpload && ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "[{hash}] Error during file upload, trying alternative file upload", fileHash);
                await UploadFileStream(compressedFile, fileHash, munged: true, postProgress, uploadToken).ConfigureAwait(false);
            }
            else
            {
                Logger.LogWarning(ex, "[{hash}] File upload cancelled", fileHash);
            }
        }
    }

    private async Task UploadFileStream(byte[] compressedFile, string fileHash, bool munged, bool postProgress, CancellationToken uploadToken)
    {
        if (munged)
        {
            FileDownloadManager.MungeBuffer(compressedFile.AsSpan());
        }

        using var ms = new MemoryStream(compressedFile);

        Progress<UploadProgress>? prog = !postProgress ? null : new((prog) =>
        {
            try
            {
                var uploadItem = CurrentUploads.FirstOrDefault(f => string.Equals(f.Hash, fileHash, StringComparison.Ordinal));
                if (uploadItem != null) uploadItem.Transferred = prog.Uploaded;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{hash}] Could not set upload progress", fileHash);
            }
        });

        var streamContent = new ProgressableStreamContent(ms, prog);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        if (!munged)
            response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, SpheneFiles.ServerFilesUploadFullPath(_orchestrator.FilesCdnUri!, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        else
            response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, SpheneFiles.ServerFilesUploadMunged(_orchestrator.FilesCdnUri!, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        Logger.LogDebug("[{hash}] Upload Status: {status}", fileHash, response.StatusCode);
    }

    private async Task UploadUnverifiedFiles(HashSet<string> unverifiedUploadHashes, List<UserData> visiblePlayers, CancellationToken uploadToken)
    {
        unverifiedUploadHashes = unverifiedUploadHashes.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);

        Logger.LogDebug("Verifying {count} files", unverifiedUploadHashes.Count);
        var filesToUpload = await FilesSend([.. unverifiedUploadHashes], visiblePlayers.Select(p => p.UID).ToList(), uploadToken).ConfigureAwait(false);

        foreach (var file in filesToUpload.Where(f => !f.IsForbidden).DistinctBy(f => f.Hash))
        {
            try
            {
                CurrentUploads.Add(new UploadFileTransfer(file)
                {
                    Total = new FileInfo(_fileDbManager.GetFileCacheByHash(file.Hash)!.ResolvedFilepath).Length,
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tried to request file {hash} but file was not present", file.Hash);
            }
        }

        foreach (var file in filesToUpload.Where(c => c.IsForbidden))
        {
            if (_orchestrator.ForbiddenTransfers.TrueForAll(f => !string.Equals(f.Hash, file.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new UploadFileTransfer(file)
                {
                    LocalFile = _fileDbManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath ?? string.Empty,
                });
            }

            _verifiedUploadedHashes[file.Hash] = DateTime.UtcNow;
        }

        var totalSize = CurrentUploads.Sum(c => c.Total);
        Logger.LogDebug("Compressing and uploading files");
        var uploadTasks = new List<Task>();
        var maxParallelUploads = _SpheneConfigService.Current.ParallelDownloads; // Reuse the parallel downloads setting
        using var semaphore = new SemaphoreSlim(maxParallelUploads, maxParallelUploads);
        
        foreach (var file in CurrentUploads.Where(f => f.CanBeTransferred && !f.IsTransferred).ToList())
        {
            var uploadTask = Task.Run(async () =>
            {
                await semaphore.WaitAsync(uploadToken).ConfigureAwait(false);
                try
                {
                    Logger.LogDebug("[{hash}] Compressing", file);
                    var data = await _fileDbManager.GetCompressedFileData(file.Hash, uploadToken).ConfigureAwait(false);
                    var uploadItem = CurrentUploads.FirstOrDefault(e => string.Equals(e.Hash, data.Item1, StringComparison.Ordinal));
                    if (uploadItem != null) uploadItem.Total = data.Item2.Length;
                    Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
                    await UploadFile(data.Item2, file.Hash, true, uploadToken).ConfigureAwait(false);
                    uploadToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    semaphore.Release();
                }
            }, uploadToken);
            
            uploadTasks.Add(uploadTask);
        }

        if (uploadTasks.Any())
        {
            await Task.WhenAll(uploadTasks).ConfigureAwait(false);

            var compressedSize = CurrentUploads.Sum(c => c.Total);
            Logger.LogDebug("Upload complete, compressed {size} to {compressed}", UiSharedService.ByteToString(totalSize), UiSharedService.ByteToString(compressedSize));
        }

        foreach (var file in unverifiedUploadHashes.Where(c => !CurrentUploads.Exists(u => string.Equals(u.Hash, c, StringComparison.Ordinal))))
        {
            _verifiedUploadedHashes[file] = DateTime.UtcNow;
        }

        CurrentUploads.Clear();
    }
}
