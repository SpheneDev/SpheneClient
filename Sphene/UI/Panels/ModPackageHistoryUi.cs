using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.Files;
using Sphene.API.Routes;
using Sphene.FileCache;
using Sphene.PlayerData.Factories;
using Sphene.Services;
using Sphene.Services.ServerConfiguration;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.Utils;
using Sphene.WebAPI.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.UI.Panels;

public class ModPackageHistoryUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiShared;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly FileUploadManager _fileUploadManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory;
    private readonly FileCacheManager _fileCacheManager;
    private readonly SpheneConfigService _configService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ShrinkU.Services.TextureBackupService _backupService;
    private readonly ShrinkU.Services.PenumbraIpc _penumbraIpc;

    private Task<List<ModUploadHistoryEntryDto>?>? _modUploadHistoryTask;
    private Task<List<ModDownloadHistoryEntryDto>?>? _modDownloadHistoryTask;
    private Task<List<ModShareHistoryEntryDto>?>? _modShareHistoryTask;
    private Task<List<ModReceivedHistoryEntryDto>?>? _modReceivedHistoryTask;
    private Task<List<PenumbraModBackupSummaryDto>?>? _backupListTask;
    private Task<PenumbraModBackupDto?>? _selectedBackupTask;

    private PenumbraModBackupSummaryDto? _selectedBackupSummary;
    private readonly HashSet<string> _backupCreateSelectedHashes = new(StringComparer.OrdinalIgnoreCase);
    private string _newBackupName;
    private string _backupStatusText = string.Empty;
    private bool _backupStatusIsError;
    private float? _backupProgress;
    private bool _isBackupBusy;
    private CancellationTokenSource? _backupCts;
    private CancellationTokenSource? _redownloadCts;
    private string _redownloadStatusText = string.Empty;
    private bool _redownloadStatusIsError;
    private float? _redownloadProgress;
    private bool _isRedownloadBusy;
    private int _backupCreateCurrentPage = 1;
    private int _installedBackupCreateCurrentPage = 1;
    private string _installedModFilter = string.Empty;
    private readonly List<(string ModFolderName, string DisplayName)> _installedPenumbraMods = new();
    private readonly HashSet<string> _installedSelectedModFolders = new(StringComparer.Ordinal);

    private int _uploadCurrentPage = 1;
    private int _downloadCurrentPage = 1;
    private int _shareCurrentPage = 1;
    private int _receivedCurrentPage = 1;
    private int _historyItemsPerPage = 25;

    public ModPackageHistoryUi(
        ILogger<ModPackageHistoryUi> logger,
        SpheneMediator mediator,
        UiSharedService uiShared,
        FileTransferOrchestrator fileTransferOrchestrator,
        FileUploadManager fileUploadManager,
        FileDownloadManagerFactory fileDownloadManagerFactory,
        FileCacheManager fileCacheManager,
        SpheneConfigService configService,
        ServerConfigurationManager serverConfigurationManager,
        ShrinkU.Services.TextureBackupService backupService,
        ShrinkU.Services.PenumbraIpc penumbraIpc,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Mod Packages###SpheneModPackageHistory", performanceCollectorService)
    {
        _uiShared = uiShared;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileUploadManager = fileUploadManager;
        _fileDownloadManagerFactory = fileDownloadManagerFactory;
        _fileCacheManager = fileCacheManager;
        _configService = configService;
        _serverConfigurationManager = serverConfigurationManager;
        _backupService = backupService;
        _penumbraIpc = penumbraIpc;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700f, 400f),
            MaximumSize = new Vector2(3000f, 3000f),
        };

        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(900, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;

        _newBackupName = $"Penumbra Backup {DateTime.Now:yyyy-MM-dd HH:mm}";
    }

    private static void DrawPaginationControls(string idSuffix, int totalItems, ref int currentPage, ref int itemsPerPage)
    {
        var totalPages = (int)Math.Ceiling((double)totalItems / itemsPerPage);
        if (totalPages < 1) totalPages = 1;
        if (currentPage > totalPages) currentPage = totalPages;
        if (currentPage < 1) currentPage = 1;

        ImGui.AlignTextToFramePadding();
        ImGui.Text($"Total: {totalItems} items | Page {currentPage} of {totalPages}");
        
        ImGui.SameLine();
        if (ImGui.ArrowButton($"##prev{idSuffix}", ImGuiDir.Left) && currentPage > 1)
        {
            currentPage--;
        }
        
        ImGui.SameLine();
        if (ImGui.ArrowButton($"##next{idSuffix}", ImGuiDir.Right) && currentPage < totalPages)
        {
            currentPage++;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.BeginCombo($"##perPage{idSuffix}", $"{itemsPerPage} per page"))
        {
            if (ImGui.Selectable($"25##{idSuffix}", itemsPerPage == 25)) itemsPerPage = 25;
            if (ImGui.Selectable($"50##{idSuffix}", itemsPerPage == 50)) itemsPerPage = 50;
            if (ImGui.Selectable($"100##{idSuffix}", itemsPerPage == 100)) itemsPerPage = 100;
            ImGui.EndCombo();
        }
    }

    protected override void DrawInternal()
    {
        DrawContent(isEmbedded: false);
    }

    internal void DrawEmbedded()
    {
        DrawContent(isEmbedded: true);
    }

    private void DrawContent(bool isEmbedded)
    {
        if (!isEmbedded)
        {
            _uiShared.BigText("Mod Package History");
            UiSharedService.TextWrapped("Overview of Penumbra mod packages you have uploaded and downloaded via Sphene.");
            ImGui.Separator();
        }
        else
        {
            ImGui.TextDisabled("Upload, download, reshare, and create backups from mod packages.");
            ImGui.Spacing();
        }

        if (ImGui.BeginTabBar("ModHistoryTabs"))
        {
            if (ImGui.BeginTabItem("Upload History"))
            {
                DrawModPackageUploadHistory();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Download History"))
            {
                DrawModPackageDownloadHistory();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Shared History"))
            {
                DrawModPackageShareHistory();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Received History"))
            {
                DrawModPackageReceivedHistory();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Backups"))
            {
                DrawPenumbraBackups();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawModPackageUploadHistory()
    {
        _uiShared.BigText("Uploaded Mod Packages");

        if (!_fileTransferOrchestrator.IsInitialized)
        {
            UiSharedService.ColorTextWrapped("File transfer service is not initialized. Connect to a server first.", ImGuiColors.DalamudYellow);
            return;
        }

        DrawRedownloadStatus();

        if (_uiShared.IconTextButton(FontAwesomeIcon.History, "Load my uploaded mod packages"))
        {
            _modUploadHistoryTask = GetModUploadHistory(CancellationToken.None);
        }

        if (_modUploadHistoryTask != null && !_modUploadHistoryTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped("Loading uploaded mod package history…", ImGuiColors.DalamudGrey);
            return;
        }

        if (_modUploadHistoryTask == null || !_modUploadHistoryTask.IsCompleted)
        {
            return;
        }

        if (!_modUploadHistoryTask.IsCompletedSuccessfully || _modUploadHistoryTask.Result == null)
        {
            UiSharedService.ColorTextWrapped("Failed to load uploaded mod package history. See /xllog for details.", ImGuiColors.DalamudRed);
            return;
        }

        var history = _modUploadHistoryTask.Result;
        if (history.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No uploaded mod packages found for this account.", ImGuiColors.DalamudGrey);
            return;
        }

        DrawPaginationControls("Upload", history.Count, ref _uploadCurrentPage, ref _historyItemsPerPage);

        var startIndex = (_uploadCurrentPage - 1) * _historyItemsPerPage;
        var pagedHistory = history.Skip(startIndex).Take(_historyItemsPerPage).ToList();

        if (!ImGui.BeginTable("ModUploadHistoryTable", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Uploaded", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableHeadersRow();

        foreach (var entry in pagedHistory)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Selectable(string.IsNullOrWhiteSpace(entry.Name) ? entry.Hash : entry.Name, false, ImGuiSelectableFlags.SpanAllColumns);

            if (ImGui.BeginPopupContextItem($"UploadHistoryContext{entry.Hash}{entry.UploadedDate.Ticks}"))
            {
                if (ImGui.MenuItem("Send to..."))
                {
                    Mediator.Publish(new OpenSendPenumbraModWindow(null, entry.Name));
                }
                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Version ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Author ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(entry.Size));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.UploadedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(_isRedownloadBusy))
            {
                if (ImGui.SmallButton($"Re-download##redownload_upload_{entry.Hash}_{entry.UploadedDate.Ticks}"))
                {
                    StartRedownload(entry);
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawModPackageDownloadHistory()
    {
        ImGui.Separator();
        _uiShared.BigText("Downloaded Mod Packages");

        if (!_fileTransferOrchestrator.IsInitialized)
        {
            UiSharedService.ColorTextWrapped("File transfer service is not initialized. Connect to a server first.", ImGuiColors.DalamudYellow);
            return;
        }

        DrawRedownloadStatus();

        if (_uiShared.IconTextButton(FontAwesomeIcon.Download, "Load my downloaded mod packages"))
        {
            _modDownloadHistoryTask = GetModDownloadHistory(CancellationToken.None);
        }

        if (_modDownloadHistoryTask != null && !_modDownloadHistoryTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped("Loading downloaded mod package history…", ImGuiColors.DalamudGrey);
            return;
        }

        if (_modDownloadHistoryTask == null || !_modDownloadHistoryTask.IsCompleted)
        {
            return;
        }

        if (!_modDownloadHistoryTask.IsCompletedSuccessfully || _modDownloadHistoryTask.Result == null)
        {
            UiSharedService.ColorTextWrapped("Failed to load downloaded mod package history. See /xllog for details.", ImGuiColors.DalamudRed);
            return;
        }

        var history = _modDownloadHistoryTask.Result;
        if (history.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No downloaded mod packages found for this account.", ImGuiColors.DalamudGrey);
            return;
        }

        DrawPaginationControls("Download", history.Count, ref _downloadCurrentPage, ref _historyItemsPerPage);

        var startIndex = (_downloadCurrentPage - 1) * _historyItemsPerPage;
        var pagedHistory = history.Skip(startIndex).Take(_historyItemsPerPage).ToList();

        if (!ImGui.BeginTable("ModDownloadHistoryTable", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Downloaded", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableHeadersRow();

        foreach (var entry in pagedHistory)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Selectable(
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Hash : entry.Name,
                false,
                ImGuiSelectableFlags.SpanAllColumns);
            ImGui.SetItemAllowOverlap();

            if (ImGui.BeginPopupContextItem($"DownloadHistoryContext{entry.Hash}{entry.DownloadedAt.Ticks}"))
            {
                if (ImGui.MenuItem("Send to..."))
                {
                    Mediator.Publish(new OpenSendPenumbraModWindow(null, entry.Name));
                }
                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Version ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Author ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(entry.Size));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.DownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(_isRedownloadBusy))
            {
                if (ImGui.SmallButton($"Re-download##redownload_{entry.Hash}_{entry.DownloadedAt.Ticks}"))
                {
                    StartRedownload(entry);
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawRedownloadStatus()
    {
        if (string.IsNullOrWhiteSpace(_redownloadStatusText) && !_isRedownloadBusy)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_redownloadStatusText))
        {
            UiSharedService.ColorTextWrapped(_redownloadStatusText, _redownloadStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey);
        }

        if (_redownloadProgress is >= 0f and <= 1f)
        {
            ImGui.ProgressBar(_redownloadProgress.Value, new Vector2(-1, 0), string.Empty);
        }

        if (_isRedownloadBusy)
        {
            ImGui.Spacing();
            if (ImGui.SmallButton("Cancel re-download"))
            {
                _redownloadCts?.Cancel();
            }
        }

        ImGui.Separator();
    }

    private void StartRedownload(ModDownloadHistoryEntryDto entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Hash))
        {
            return;
        }

        StartRedownload(entry.Hash, entry.Name, entry.Author, entry.Version);
    }

    private void StartRedownload(ModUploadHistoryEntryDto entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Hash))
        {
            return;
        }

        StartRedownload(entry.Hash, entry.Name, entry.Author, entry.Version);
    }

    private void StartRedownload(ModShareHistoryEntryDto entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Hash))
        {
            return;
        }

        StartRedownload(entry.Hash, entry.Name, entry.Author, entry.Version);
    }

    private void StartRedownload(string hash, string? name, string? author, string? version)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        _redownloadCts = _redownloadCts.CancelRecreate();
        var token = _redownloadCts.Token;

        _isRedownloadBusy = true;
        _redownloadStatusIsError = false;
        _redownloadProgress = 0f;

        var displayName = string.IsNullOrWhiteSpace(name) ? hash : name;
        _redownloadStatusText = $"Re-downloading: {displayName}";

        _ = Task.Run(async () => await RedownloadAsync(hash, name, author, version, displayName, token).ConfigureAwait(false), token);
    }

    private async Task RedownloadAsync(string hash, string? name, string? author, string? version, string displayName, CancellationToken token)
    {
        try
        {
            using var fileDownloadManager = _fileDownloadManagerFactory.Create();

            var downloadProgress = new Progress<(long TransferredBytes, long TotalBytes)>(tuple =>
            {
                if (tuple.TotalBytes <= 0)
                {
                    return;
                }

                _redownloadProgress = Math.Clamp((float)tuple.TransferredBytes / tuple.TotalBytes, 0f, 1f);
            });

            var pmpPath = await fileDownloadManager.DownloadPmpToCacheAsync(hash, token, downloadProgress).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pmpPath) || !File.Exists(pmpPath))
            {
                _redownloadStatusIsError = true;
                _redownloadStatusText = $"Re-download failed: {displayName}";
                return;
            }

            token.ThrowIfCancellationRequested();

            var modFolderName = await ResolveRedownloadInstallFolderNameAsync(hash, name, author, version, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(modFolderName))
            {
                _redownloadStatusIsError = true;
                _redownloadStatusText = $"Failed to resolve mod folder name: {displayName}";
                return;
            }

            _redownloadProgress = 0f;
            _redownloadStatusIsError = false;
            _redownloadStatusText = $"Installing: {displayName}";

            var installProgress = new Progress<(string, int, int)>(tuple =>
            {
                if (tuple.Item3 > 0)
                {
                    _redownloadProgress = Math.Clamp((float)tuple.Item2 / tuple.Item3, 0f, 1f);
                }

                _redownloadStatusText = string.IsNullOrWhiteSpace(tuple.Item1)
                    ? $"Installing: {displayName}… ({tuple.Item2}/{tuple.Item3})"
                    : tuple.Item1;
            });

            var ok = await _backupService.RestorePmpAsync(
                modFolderName,
                pmpPath,
                installProgress,
                token,
                cleanupBackupsAfterRestore: false,
                deregisterDuringRestore: true).ConfigureAwait(false);

            if (!ok)
            {
                _redownloadStatusIsError = true;
                _redownloadStatusText = $"Install failed: {displayName}";
                return;
            }

            if (_configService.Current.DeletePenumbraModAfterInstall)
            {
                try { File.Delete(pmpPath); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to delete downloaded PMP after install: {path}", pmpPath);
                }
            }

            _redownloadProgress = 1.0f;
            _redownloadStatusIsError = false;
            _redownloadStatusText = $"Installed: {displayName}";
        }
        catch (OperationCanceledException)
        {
            _redownloadStatusIsError = true;
            _redownloadStatusText = "Re-download cancelled.";
        }
        catch (Exception ex)
        {
            _redownloadStatusIsError = true;
            _redownloadStatusText = "Re-download failed. See /xllog for details.";
            _logger.LogWarning(ex, "Failed to re-download mod package {hash}", hash);
        }
        finally
        {
            _isRedownloadBusy = false;
        }
    }

    private async Task<string> ResolveRedownloadInstallFolderNameAsync(string hash, string? name, string? author, string? version, CancellationToken token)
    {
        var preferred = name?.Trim() ?? string.Empty;
        hash = hash?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(preferred) && !string.IsNullOrWhiteSpace(hash))
        {
            preferred = hash;
        }

        try
        {
            var mods = _penumbraIpc.GetModList();
            if (mods.TryGetValue(preferred, out _))
            {
                return preferred;
            }

            var matches = new List<string>();
            foreach (var kvp in mods)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value) &&
                    string.Equals(kvp.Value.Trim(), preferred, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(kvp.Key))
                {
                    matches.Add(kvp.Key);
                }
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                string? best = null;
                var bestScore = int.MinValue;
                foreach (var folder in matches.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();

                    var score = 0;
                    try
                    {
                        var meta = await _penumbraIpc.GetModMetadataAsync(folder).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(meta?.Name) &&
                            string.Equals(meta.Name.Trim(), preferred, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 2;
                        }

                        if (!string.IsNullOrWhiteSpace(author) &&
                            !string.IsNullOrWhiteSpace(meta?.Author) &&
                            string.Equals(meta.Author.Trim(), author.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            score += 2;
                        }

                        if (!string.IsNullOrWhiteSpace(version) &&
                            !string.IsNullOrWhiteSpace(meta?.Version) &&
                            string.Equals(meta.Version.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            score += 1;
                        }
                    }
                    catch
                    {
                        score -= 1;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = folder;
                    }
                }

                if (!string.IsNullOrWhiteSpace(best))
                {
                    return best;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve Penumbra folder by installed mod list");
        }

        var sanitized = SanitizePenumbraFolderName(preferred);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = !string.IsNullOrWhiteSpace(hash) ? hash : "UnknownMod";
        }

        try
        {
            if (_penumbraIpc.ModExists(sanitized))
            {
                return sanitized;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check whether mod exists in Penumbra: {modFolder}", sanitized);
        }

        try
        {
            var mods = _penumbraIpc.GetModList();
            if (mods.TryGetValue(sanitized, out _))
            {
                return sanitized;
            }

            if (mods.Keys.Any(k => string.Equals(k, sanitized, StringComparison.OrdinalIgnoreCase)))
            {
                return mods.Keys.First(k => string.Equals(k, sanitized, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query Penumbra mod list while resolving folder name");
        }

        if (!string.IsNullOrWhiteSpace(hash) && sanitized.Length > 0)
        {
            try
            {
                var root = _penumbraIpc.ModDirectory ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var candidatePath = Path.Combine(root, sanitized);
                    if (Directory.Exists(candidatePath))
                    {
                        return sanitized;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to probe Penumbra mod directory for folder: {modFolder}", sanitized);
            }
        }

        if (!string.IsNullOrWhiteSpace(hash) && hash.Length >= 8)
        {
            return $"{sanitized}-{hash.Substring(0, 8)}";
        }

        return sanitized;
    }

    private static string SanitizePenumbraFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(c, '_');
        }
        result = result.Trim().TrimEnd('.');
        return result;
    }

    private async Task<List<ModUploadHistoryEntryDto>?> GetModUploadHistory(CancellationToken ct)
    {
        try
        {
            if (!_fileTransferOrchestrator.IsInitialized)
            {
                throw new InvalidOperationException("File transfer service is not initialized");
            }

            var uri = SpheneFiles.ServerFilesModHistoryFullPath(_fileTransferOrchestrator.FilesCdnUri!);
            var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<List<ModUploadHistoryEntryDto>>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get mod upload history");
            throw new InvalidOperationException("Failed to get mod upload history", ex);
        }
    }

    private async Task<List<ModDownloadHistoryEntryDto>?> GetModDownloadHistory(CancellationToken ct)
    {
        try
        {
            if (!_fileTransferOrchestrator.IsInitialized)
            {
                throw new InvalidOperationException("File transfer service is not initialized");
            }

            var uri = SpheneFiles.ServerFilesModDownloadHistoryFullPath(_fileTransferOrchestrator.FilesCdnUri!);
            var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<List<ModDownloadHistoryEntryDto>>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get mod download history");
            throw new InvalidOperationException("Failed to get mod download history", ex);
        }
    }

    private void DrawModPackageShareHistory()
    {
        ImGui.Separator();
        _uiShared.BigText("Shared Mod Packages");

        if (!_fileTransferOrchestrator.IsInitialized)
        {
            UiSharedService.ColorTextWrapped("File transfer service is not initialized. Connect to a server first.", ImGuiColors.DalamudYellow);
            return;
        }

        DrawRedownloadStatus();

        if (_uiShared.IconTextButton(FontAwesomeIcon.Share, "Load my shared mod packages"))
        {
            _modShareHistoryTask = GetModShareHistory(CancellationToken.None);
        }

        if (_modShareHistoryTask != null && !_modShareHistoryTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped("Loading shared mod package history…", ImGuiColors.DalamudGrey);
            return;
        }

        if (_modShareHistoryTask == null || !_modShareHistoryTask.IsCompleted)
        {
            return;
        }

        if (!_modShareHistoryTask.IsCompletedSuccessfully || _modShareHistoryTask.Result == null)
        {
            UiSharedService.ColorTextWrapped("Failed to load shared mod package history. See /xllog for details.", ImGuiColors.DalamudRed);
            return;
        }

        var history = _modShareHistoryTask.Result;
        if (history.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No shared mod packages found for this account.", ImGuiColors.DalamudGrey);
            return;
        }

        DrawPaginationControls("Share", history.Count, ref _shareCurrentPage, ref _historyItemsPerPage);

        var startIndex = (_shareCurrentPage - 1) * _historyItemsPerPage;
        var pagedHistory = history.Skip(startIndex).Take(_historyItemsPerPage).ToList();

        if (!ImGui.BeginTable("ModShareHistoryTable", 7, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Recipient", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Shared At", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableHeadersRow();

        foreach (var entry in pagedHistory)
        {
            var recipientDisplayName = _serverConfigurationManager.GetPreferredUserDisplayName(
                entry.RecipientUID,
                !string.IsNullOrWhiteSpace(entry.RecipientAlias) ? entry.RecipientAlias : entry.RecipientUID);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Selectable(string.IsNullOrWhiteSpace(entry.Name) ? entry.Hash : entry.Name, false, ImGuiSelectableFlags.SpanAllColumns);

            if (ImGui.BeginPopupContextItem($"ShareHistoryContext{entry.Hash}{entry.SharedAt.Ticks}"))
            {
                if (ImGui.MenuItem($"Send again to {(string.IsNullOrWhiteSpace(recipientDisplayName) ? "-" : recipientDisplayName)}"))
                {
                    _ = ReshareModAsync(entry);
                }
                if (ImGui.MenuItem("Send to..."))
                {
                    Mediator.Publish(new OpenSendPenumbraModWindow(null, entry.Name));
                }
                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Version ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Author ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(recipientDisplayName) ? "-" : recipientDisplayName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(entry.Size));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.SharedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(_isRedownloadBusy))
            {
                if (ImGui.SmallButton($"Re-download##redownload_share_{entry.Hash}_{entry.SharedAt.Ticks}"))
                {
                    StartRedownload(entry);
                }
            }
        }

        ImGui.EndTable();
    }

    private void DrawModPackageReceivedHistory()
    {
        ImGui.Separator();
        _uiShared.BigText("Received Mod Packages");

        if (!_fileTransferOrchestrator.IsInitialized)
        {
            UiSharedService.ColorTextWrapped("File transfer service is not initialized. Connect to a server first.", ImGuiColors.DalamudYellow);
            return;
        }

        if (_uiShared.IconTextButton(FontAwesomeIcon.Inbox, "Load my received mod packages"))
        {
            _modReceivedHistoryTask = GetModReceivedHistory(CancellationToken.None);
        }

        if (_modReceivedHistoryTask != null && !_modReceivedHistoryTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped("Loading received mod package history…", ImGuiColors.DalamudGrey);
            return;
        }

        if (_modReceivedHistoryTask == null || !_modReceivedHistoryTask.IsCompleted)
        {
            return;
        }

        if (!_modReceivedHistoryTask.IsCompletedSuccessfully || _modReceivedHistoryTask.Result == null)
        {
            UiSharedService.ColorTextWrapped("Failed to load received mod package history. See /xllog for details.", ImGuiColors.DalamudRed);
            return;
        }

        var history = _modReceivedHistoryTask.Result;
        if (history.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No received mod packages found for this account.", ImGuiColors.DalamudGrey);
            return;
        }

        DrawPaginationControls("Received", history.Count, ref _receivedCurrentPage, ref _historyItemsPerPage);

        var startIndex = (_receivedCurrentPage - 1) * _historyItemsPerPage;
        var pagedHistory = history.Skip(startIndex).Take(_historyItemsPerPage).ToList();

        if (!ImGui.BeginTable("ModReceivedHistoryTable", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Sender", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Received At", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableHeadersRow();

        foreach (var entry in pagedHistory)
        {
            var senderDisplayName = _serverConfigurationManager.GetPreferredUserDisplayName(
                entry.SenderUID,
                !string.IsNullOrWhiteSpace(entry.SenderAlias) ? entry.SenderAlias : entry.SenderUID);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Selectable(string.IsNullOrWhiteSpace(entry.Name) ? entry.Hash : entry.Name, false, ImGuiSelectableFlags.SpanAllColumns);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Version ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Author ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(senderDisplayName) ? "-" : senderDisplayName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(UiSharedService.ByteToString(entry.Size));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }

        ImGui.EndTable();
    }

    private async Task ReshareModAsync(ModShareHistoryEntryDto entry)
    {
        try
        {
            var modInfo = new ModInfoDto(
                entry.Hash,
                entry.Name,
                entry.Author,
                entry.Version,
                entry.Description,
                entry.Website
            );

            var folderName = entry.Name;

            var progress = new Progress<string>(s => _logger.LogInformation(s));

            await _fileUploadManager.ReshareFileAsync(
                entry.Hash,
                entry.RecipientUID,
                folderName,
                modInfo,
                progress,
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("Successfully reshared {Name} to {Recipient}", entry.Name, entry.RecipientUID);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reshare mod {Name}", entry.Name);
        }
    }

    private async Task<List<ModShareHistoryEntryDto>?> GetModShareHistory(CancellationToken ct)
    {
        try
        {
            if (!_fileTransferOrchestrator.IsInitialized)
            {
                throw new InvalidOperationException("File transfer service is not initialized");
            }

            var uri = SpheneFiles.ServerFilesModShareHistoryFullPath(_fileTransferOrchestrator.FilesCdnUri!);
            var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<List<ModShareHistoryEntryDto>>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get mod share history");
            throw new InvalidOperationException("Failed to get mod share history", ex);
        }
    }

    private async Task<List<ModReceivedHistoryEntryDto>?> GetModReceivedHistory(CancellationToken ct)
    {
        try
        {
            if (!_fileTransferOrchestrator.IsInitialized)
            {
                throw new InvalidOperationException("File transfer service is not initialized");
            }

            var uri = SpheneFiles.ServerFilesModReceivedHistoryFullPath(_fileTransferOrchestrator.FilesCdnUri!);
            var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<List<ModReceivedHistoryEntryDto>>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get mod received history");
            throw new InvalidOperationException("Failed to get mod received history", ex);
        }
    }

    private void DrawPenumbraBackups()
    {
        ImGui.Separator();
        _uiShared.BigText("Penumbra Backups");

        if (!_fileTransferOrchestrator.IsInitialized)
        {
            UiSharedService.ColorTextWrapped("File transfer service is not initialized. Connect to a server first.", ImGuiColors.DalamudYellow);
            return;
        }

        DrawBackupStatus();

        using (ImRaii.Disabled(_isBackupBusy))
        {
            DrawBackupCreateSection();
            ImGui.Separator();
            DrawBackupListSection();
        }
    }

    private void DrawBackupStatus()
    {
        if (string.IsNullOrWhiteSpace(_backupStatusText) && _backupProgress == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_backupStatusText))
        {
            var color = _backupStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
            UiSharedService.ColorTextWrapped(_backupStatusText, color);
        }

        if (_backupProgress is { } p)
        {
            ImGui.ProgressBar(p, new Vector2(-1, 0));
        }
    }

    private void DrawBackupCreateSection()
    {
        _uiShared.BigText("Create Backup");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##backupName", "Backup name", ref _newBackupName, 200);

        ImGui.Separator();
        _uiShared.BigText("From Uploaded Mod Packages");

        if (ImGui.Button("Load uploaded mod packages for selection"))
        {
            _modUploadHistoryTask = GetModUploadHistory(CancellationToken.None);
            _backupCreateSelectedHashes.Clear();
        }

        if (_modUploadHistoryTask != null && !_modUploadHistoryTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped("Loading uploaded mod package history…", ImGuiColors.DalamudGrey);
        }
        else if (_modUploadHistoryTask == null || !_modUploadHistoryTask.IsCompletedSuccessfully || _modUploadHistoryTask.Result == null)
        {
            UiSharedService.ColorTextWrapped("Load your uploaded mod packages to create a backup.", ImGuiColors.DalamudGrey);
        }
        else
        {
            var history = _modUploadHistoryTask.Result;
            if (history.Count == 0)
            {
                UiSharedService.ColorTextWrapped("No uploaded mod packages found for this account.", ImGuiColors.DalamudGrey);
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"Selected: {_backupCreateSelectedHashes.Count}/{history.Count}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Select all"))
                {
                    _backupCreateSelectedHashes.Clear();
                    foreach (var entry in history)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Hash))
                        {
                            _backupCreateSelectedHashes.Add(entry.Hash);
                        }
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear"))
                {
                    _backupCreateSelectedHashes.Clear();
                }

                DrawPaginationControls("BackupCreate", history.Count, ref _backupCreateCurrentPage, ref _historyItemsPerPage);

                var startIndex = (_backupCreateCurrentPage - 1) * _historyItemsPerPage;
                var pagedHistory = history.Skip(startIndex).Take(_historyItemsPerPage).ToList();

                if (ImGui.BeginTable("BackupCreateTable", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
                {
                    ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 50);
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("Uploaded", ImGuiTableColumnFlags.WidthFixed, 160);
                    ImGui.TableHeadersRow();

                    foreach (var entry in pagedHistory)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        var selected = _backupCreateSelectedHashes.Contains(entry.Hash);
                        if (ImGui.Checkbox($"##sel_{entry.Hash}", ref selected))
                        {
                            if (selected) _backupCreateSelectedHashes.Add(entry.Hash);
                            else _backupCreateSelectedHashes.Remove(entry.Hash);
                        }

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Name) ? entry.Hash : entry.Name);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(entry.Version ?? "-");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(entry.UploadedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    }

                    ImGui.EndTable();
                }

                using (ImRaii.Disabled(_backupCreateSelectedHashes.Count == 0))
                {
                    if (ImGui.Button("Create backup from selected uploads"))
                    {
                        _ = CreateBackupFromSelectedUploadsAsync();
                    }
                }
            }
        }

        ImGui.Separator();
        _uiShared.BigText("From Installed Penumbra Mods");

        if (ImGui.Button("Load installed Penumbra mods"))
        {
            LoadInstalledPenumbraMods();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##installedModFilter", "Filter installed mods", ref _installedModFilter, 100);

        if (_installedPenumbraMods.Count == 0)
        {
            UiSharedService.ColorTextWrapped("Load your installed Penumbra mods to upload and create a backup.", ImGuiColors.DalamudGrey);
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Selected: {_installedSelectedModFolders.Count}/{_installedPenumbraMods.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Select all##installed"))
        {
            _installedSelectedModFolders.Clear();
            foreach (var mod in _installedPenumbraMods)
            {
                if (!string.IsNullOrWhiteSpace(mod.ModFolderName))
                {
                    _installedSelectedModFolders.Add(mod.ModFolderName);
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##installed"))
        {
            _installedSelectedModFolders.Clear();
        }

        var filteredMods = GetFilteredInstalledPenumbraMods();
        DrawPaginationControls("InstalledBackupCreate", filteredMods.Count, ref _installedBackupCreateCurrentPage, ref _historyItemsPerPage);

        var installedStart = (_installedBackupCreateCurrentPage - 1) * _historyItemsPerPage;
        var pagedInstalled = filteredMods.Skip(installedStart).Take(_historyItemsPerPage).ToList();

        if (ImGui.BeginTable("InstalledBackupCreateTable", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Folder", ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableHeadersRow();

            foreach (var mod in pagedInstalled)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var selected = _installedSelectedModFolders.Contains(mod.ModFolderName);
                if (ImGui.Checkbox($"##inst_{mod.ModFolderName}", ref selected))
                {
                    if (selected) _installedSelectedModFolders.Add(mod.ModFolderName);
                    else _installedSelectedModFolders.Remove(mod.ModFolderName);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mod.DisplayName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mod.ModFolderName);
            }

            ImGui.EndTable();
        }

        using (ImRaii.Disabled(_installedSelectedModFolders.Count == 0))
        {
            if (ImGui.Button("Upload selected installed mods"))
            {
                StartUploadInstalledMods(createBackupAfterUpload: false);
            }

            ImGui.SameLine();
            if (ImGui.Button("Upload and create backup"))
            {
                StartUploadInstalledMods(createBackupAfterUpload: true);
            }
        }
    }

    private void LoadInstalledPenumbraMods()
    {
        try
        {
            var dict = _penumbraIpc.GetModList();
            _installedPenumbraMods.Clear();

            foreach (var kvp in dict)
            {
                var folder = kvp.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    continue;
                }

                var name = kvp.Value ?? string.Empty;
                var displayName = string.IsNullOrWhiteSpace(name) ? folder : name;
                _installedPenumbraMods.Add((folder, displayName));
            }

            _installedPenumbraMods.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            _installedSelectedModFolders.Clear();
            _installedBackupCreateCurrentPage = 1;
        }
        catch (Exception ex)
        {
            _installedPenumbraMods.Clear();
            _installedSelectedModFolders.Clear();
            _backupStatusIsError = true;
            _backupStatusText = "Failed to query Penumbra mod list.";
            _logger.LogWarning(ex, "Failed to query Penumbra mod list");
        }
    }

    private List<(string ModFolderName, string DisplayName)> GetFilteredInstalledPenumbraMods()
    {
        if (_installedPenumbraMods.Count == 0)
        {
            return new List<(string ModFolderName, string DisplayName)>();
        }

        if (string.IsNullOrWhiteSpace(_installedModFilter))
        {
            return _installedPenumbraMods;
        }

        var filter = _installedModFilter.Trim();
        return _installedPenumbraMods
            .Where(m => m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) || m.ModFolderName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void StartUploadInstalledMods(bool createBackupAfterUpload)
    {
        if (_isBackupBusy)
        {
            return;
        }

        _backupCts = _backupCts.CancelRecreate();
        var token = _backupCts.Token;

        _isBackupBusy = true;
        _backupStatusIsError = false;
        _backupProgress = 0f;
        _backupStatusText = createBackupAfterUpload ? "Uploading installed mods and creating backup…" : "Uploading installed mods…";

        _ = Task.Run(async () =>
        {
            try
            {
                await UploadInstalledModsAsync(token, createBackupAfterUpload).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _backupStatusText = "Operation cancelled.";
                _backupStatusIsError = false;
            }
            catch (Exception ex)
            {
                _backupStatusText = "Operation failed. See /xllog for details.";
                _backupStatusIsError = true;
                _logger.LogWarning(ex, "Failed to upload installed mods");
            }
            finally
            {
                _backupProgress = null;
                _isBackupBusy = false;
            }
        });
    }

    private async Task UploadInstalledModsAsync(CancellationToken token, bool createBackupAfterUpload)
    {
        if (_installedSelectedModFolders.Count == 0)
        {
            _backupStatusText = "No installed mods selected.";
            _backupStatusIsError = true;
            return;
        }

        var selectedFolders = _installedSelectedModFolders.ToList();
        selectedFolders.Sort(StringComparer.OrdinalIgnoreCase);

        var entriesByHash = new Dictionary<string, PenumbraModBackupEntryDto>(StringComparer.OrdinalIgnoreCase);
        var modFolderNamesByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modInfos = new List<ModInfoDto>();

        for (var i = 0; i < selectedFolders.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var folder = selectedFolders[i];

            _backupProgress = selectedFolders.Count > 0 ? 0.05f + 0.65f * (float)i / selectedFolders.Count : 0.05f;
            _backupStatusText = $"Preparing mod package {i + 1}/{selectedFolders.Count}: {folder}";

            var pmpBackups = await _backupService.GetPmpBackupsForModAsync(folder).ConfigureAwait(false);
            var pmpPath = GetLatestExistingFile(pmpBackups);
            if (string.IsNullOrWhiteSpace(pmpPath))
            {
                var backupProgress = new Progress<(string, int, int)>(p =>
                {
                    if (string.IsNullOrWhiteSpace(p.Item1))
                    {
                        return;
                    }

                    _backupStatusText = p.Item1;
                });

                _backupStatusText = $"Creating mod package {i + 1}/{selectedFolders.Count}: {folder}";
                var created = await _backupService.CreateFullModBackupAsync(folder, backupProgress, token).ConfigureAwait(false);
                if (!created)
                {
                    _backupStatusIsError = true;
                    _backupStatusText = $"Failed to create mod package: {folder}";
                    return;
                }

                token.ThrowIfCancellationRequested();

                pmpBackups = await _backupService.GetPmpBackupsForModAsync(folder).ConfigureAwait(false);
                pmpPath = GetLatestExistingFile(pmpBackups);
            }

            if (string.IsNullOrWhiteSpace(pmpPath))
            {
                _backupStatusIsError = true;
                _backupStatusText = $"No .pmp file was found for: {folder}";
                return;
            }

            token.ThrowIfCancellationRequested();

            var hash = Crypto.GetFileHash(pmpPath);
            if (string.IsNullOrWhiteSpace(hash))
            {
                _backupStatusIsError = true;
                _backupStatusText = $"Failed to hash .pmp: {folder}";
                return;
            }

            if (entriesByHash.ContainsKey(hash))
            {
                continue;
            }

            if (_fileCacheManager.GetFileCacheByHash(hash) == null)
            {
                var cacheTarget = Path.Combine(_configService.Current.CacheFolder, hash + ".pmp");
                Directory.CreateDirectory(_configService.Current.CacheFolder);
                File.Copy(pmpPath, cacheTarget, overwrite: true);

                token.ThrowIfCancellationRequested();

                var entry = _fileCacheManager.CreateCacheEntry(cacheTarget);
                if (entry == null)
                {
                    _backupStatusIsError = true;
                    _backupStatusText = $"Failed to register the backup in cache: {folder}";
                    return;
                }
            }

            var metadata = await _penumbraIpc.GetModMetadataAsync(folder).ConfigureAwait(false);
            var displayName = string.IsNullOrWhiteSpace(metadata?.Name) ? folder : metadata!.Name;

            entriesByHash[hash] = new PenumbraModBackupEntryDto(
                hash,
                folder,
                displayName,
                metadata?.Author,
                metadata?.Version,
                metadata?.Description,
                metadata?.Website);

            modFolderNamesByHash[hash] = folder;
            modInfos.Add(new ModInfoDto(hash, displayName, metadata?.Author, metadata?.Version, metadata?.Description, metadata?.Website));
        }

        if (entriesByHash.Count == 0)
        {
            _backupStatusText = "No mods selected.";
            _backupStatusIsError = true;
            return;
        }

        _backupProgress = 0.75f;
        _backupStatusText = $"Uploading {entriesByHash.Count} mod package(s)…";

        var uploadProgress = new Progress<string>(s =>
        {
            if (!string.IsNullOrWhiteSpace(s))
            {
                _backupStatusText = s;
            }
        });

        var recipients = new List<UserData>();
        var uploadResult = await _fileUploadManager.UploadFilesForUsers(entriesByHash.Keys.ToList(), recipients, uploadProgress, token, modFolderNamesByHash, modInfos).ConfigureAwait(false);
        if (uploadResult.LocallyMissingHashes.Count > 0 || uploadResult.ForbiddenHashes.Count > 0 || uploadResult.FailedHashes.Count > 0)
        {
            var all = uploadResult.LocallyMissingHashes
                .Concat(uploadResult.ForbiddenHashes)
                .Concat(uploadResult.FailedHashes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _backupStatusIsError = true;
            _backupStatusText = "Upload failed or forbidden for: " + string.Join(", ", all);
            return;
        }

        List<string> skippedFolders = [];
        if (uploadResult.SkippedTooLargeHashes.Count > 0)
        {
            foreach (var hash in uploadResult.SkippedTooLargeHashes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (modFolderNamesByHash.TryGetValue(hash, out var folder) && !string.IsNullOrWhiteSpace(folder))
                {
                    skippedFolders.Add(folder);
                }

                entriesByHash.Remove(hash);
                modFolderNamesByHash.Remove(hash);
            }
        }

        if (!createBackupAfterUpload)
        {
            _backupProgress = 1.0f;
            _backupStatusText = skippedFolders.Count > 0
                ? $"Upload completed. Skipped {skippedFolders.Count} mod package(s) over 1GB: {string.Join(", ", skippedFolders)}"
                : "Upload completed.";
            return;
        }

        if (entriesByHash.Count == 0)
        {
            _backupStatusIsError = true;
            _backupStatusText = skippedFolders.Count > 0
                ? $"No mod package could be uploaded. Skipped {skippedFolders.Count} mod package(s) over 1GB: {string.Join(", ", skippedFolders)}"
                : "No mod package could be uploaded.";
            return;
        }

        _backupProgress = 0.92f;
        _backupStatusText = "Creating backup…";

        var dto = new PenumbraModBackupCreateDto(_newBackupName ?? string.Empty, entriesByHash.Values.ToList());
        var uri = SpheneFiles.ServerFilesPenumbraBackupsCreateFullPath(_fileTransferOrchestrator.FilesCdnUri!);
        var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Post, uri, dto, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<PenumbraModBackupCreateResultDto>(stream, cancellationToken: token).ConfigureAwait(false);

        if (result == null)
        {
            _backupStatusText = "Backup created, but server returned no result.";
            _backupProgress = 1.0f;
            return;
        }

        _backupProgress = 1.0f;
        _backupStatusText = result.IsComplete
            ? $"Backup created (complete): {result.BackupId}"
            : $"Backup created, but {result.MissingHashes.Count} file(s) are missing on server.";

        _backupListTask = GetBackupListAsync(CancellationToken.None);
    }

    private static string? GetLatestExistingFile(IEnumerable<string> filePaths)
    {
        string? best = null;
        DateTime bestTime = DateTime.MinValue;

        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            var writeTime = File.GetLastWriteTimeUtc(path);
            if (best == null || writeTime > bestTime)
            {
                best = path;
                bestTime = writeTime;
            }
        }

        return best;
    }

    private void DrawBackupListSection()
    {
        _uiShared.BigText("Your Backups");

        if (_uiShared.IconTextButton(FontAwesomeIcon.Sync, "Refresh backup list"))
        {
            _backupListTask = GetBackupListAsync(CancellationToken.None);
            _selectedBackupSummary = null;
            _selectedBackupTask = null;
        }

        if (_backupListTask != null && !_backupListTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped("Loading backups…", ImGuiColors.DalamudGrey);
            return;
        }

        if (_backupListTask == null || !_backupListTask.IsCompletedSuccessfully || _backupListTask.Result == null)
        {
            UiSharedService.ColorTextWrapped("No backup list loaded yet.", ImGuiColors.DalamudGrey);
            return;
        }

        var backups = _backupListTask.Result;
        if (backups.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No backups found for this account.", ImGuiColors.DalamudGrey);
            return;
        }

        if (!ImGui.BeginTable("BackupListTable", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mods", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Complete", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Created", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 220);
        ImGui.TableHeadersRow();

        foreach (var backup in backups)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isSelected = _selectedBackupSummary?.BackupId == backup.BackupId;
            if (ImGui.Selectable($"{backup.BackupName}##{backup.BackupId}", isSelected))
            {
                _selectedBackupSummary = backup;
                _selectedBackupTask = GetBackupAsync(backup.BackupId, CancellationToken.None);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(backup.ModCount.ToString());

            ImGui.TableNextColumn();
            UiSharedService.ColorTextWrapped(backup.IsComplete ? "Yes" : "No", backup.IsComplete ? ImGuiColors.HealerGreen : ImGuiColors.DalamudOrange);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(backup.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Open##open_{backup.BackupId}"))
            {
                _selectedBackupSummary = backup;
                _selectedBackupTask = GetBackupAsync(backup.BackupId, CancellationToken.None);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Delete##del_{backup.BackupId}"))
            {
                _selectedBackupSummary = backup;
                _ = DeleteBackupAsync(backup.BackupId);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Restore##restore_{backup.BackupId}"))
            {
                _selectedBackupSummary = backup;
                _ = RestoreSelectedBackupAsync();
            }
        }

        ImGui.EndTable();

        DrawSelectedBackupDetails();
    }

    private void DrawSelectedBackupDetails()
    {
        if (_selectedBackupTask != null && !_selectedBackupTask.IsCompleted)
        {
            UiSharedService.ColorTextWrapped("Loading backup details…", ImGuiColors.DalamudGrey);
            return;
        }

        if (_selectedBackupTask == null || !_selectedBackupTask.IsCompletedSuccessfully || _selectedBackupTask.Result == null)
        {
            return;
        }

        var selectedBackup = _selectedBackupTask.Result;
        if (selectedBackup == null)
        {
            return;
        }

        ImGui.Separator();
        _uiShared.BigText("Backup Details");
        ImGui.TextUnformatted($"{selectedBackup.BackupName} ({selectedBackup.Mods.Count} mods)");

        if (!ImGui.BeginTable("BackupDetailsTable", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Folder", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Hash", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableHeadersRow();

        foreach (var mod in selectedBackup.Mods)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(mod.Name) ? mod.ModFolderName : mod.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.ModFolderName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.Version ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(mod.Hash) ? "-" : (mod.Hash.Length > 10 ? mod.Hash[..10] : mod.Hash));
        }

        ImGui.EndTable();
    }

    private async Task<List<PenumbraModBackupSummaryDto>?> GetBackupListAsync(CancellationToken ct)
    {
        var uri = SpheneFiles.ServerFilesPenumbraBackupsListFullPath(_fileTransferOrchestrator.FilesCdnUri!);
        var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<PenumbraModBackupSummaryDto>>(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<PenumbraModBackupDto?> GetBackupAsync(Guid backupId, CancellationToken ct)
    {
        var uri = SpheneFiles.ServerFilesPenumbraBackupsGetFullPath(_fileTransferOrchestrator.FilesCdnUri!, backupId);
        var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<PenumbraModBackupDto>(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task CreateBackupFromSelectedUploadsAsync()
    {
        _backupCts = _backupCts.CancelRecreate();
        var token = _backupCts.Token;

        _isBackupBusy = true;
        _backupStatusIsError = false;
        _backupProgress = null;
        _backupStatusText = "Creating backup…";

        try
        {
            if (_modUploadHistoryTask == null || !_modUploadHistoryTask.IsCompletedSuccessfully || _modUploadHistoryTask.Result == null)
            {
                throw new InvalidOperationException("Upload history is not loaded.");
            }

            var selected = new List<PenumbraModBackupEntryDto>();
            foreach (var entry in _modUploadHistoryTask.Result)
            {
                if (!_backupCreateSelectedHashes.Contains(entry.Hash))
                {
                    continue;
                }

                var folder = string.IsNullOrWhiteSpace(entry.Name) ? entry.Hash : entry.Name;
                selected.Add(new PenumbraModBackupEntryDto(
                    entry.Hash,
                    folder,
                    string.IsNullOrWhiteSpace(entry.Name) ? folder : entry.Name,
                    entry.Author,
                    entry.Version,
                    entry.Description,
                    entry.Website));
            }

            if (selected.Count == 0)
            {
                _backupStatusText = "No uploads selected.";
                _backupStatusIsError = true;
                return;
            }

            var dto = new PenumbraModBackupCreateDto(_newBackupName ?? string.Empty, selected);
            var uri = SpheneFiles.ServerFilesPenumbraBackupsCreateFullPath(_fileTransferOrchestrator.FilesCdnUri!);
            var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Post, uri, dto, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<PenumbraModBackupCreateResultDto>(stream, cancellationToken: token).ConfigureAwait(false);

            if (result == null)
            {
                _backupStatusText = "Backup created, but server returned no result.";
                return;
            }

            if (result.IsComplete)
            {
                _backupStatusText = $"Backup created (complete): {result.BackupId}";
            }
            else
            {
                _backupStatusText = $"Backup created, but {result.MissingHashes.Count} file(s) are missing on server.";
            }

            _backupListTask = GetBackupListAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _backupStatusText = "Backup creation cancelled.";
            _backupStatusIsError = true;
        }
        catch (Exception ex)
        {
            _backupStatusText = "Failed to create backup. See /xllog for details.";
            _backupStatusIsError = true;
            _logger.LogWarning(ex, "Failed to create backup");
        }
        finally
        {
            _backupProgress = null;
            _isBackupBusy = false;
        }
    }

    private async Task DeleteBackupAsync(Guid backupId)
    {
        _backupCts = _backupCts.CancelRecreate();
        var token = _backupCts.Token;

        _isBackupBusy = true;
        _backupStatusIsError = false;
        _backupProgress = null;
        _backupStatusText = "Deleting backup…";

        try
        {
            var uri = SpheneFiles.ServerFilesPenumbraBackupsDeleteFullPath(_fileTransferOrchestrator.FilesCdnUri!, backupId);
            var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Post, uri, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            _backupStatusText = "Backup deleted.";
            _backupListTask = GetBackupListAsync(CancellationToken.None);
            _selectedBackupSummary = null;
            _selectedBackupTask = null;
        }
        catch (OperationCanceledException)
        {
            _backupStatusText = "Backup deletion cancelled.";
            _backupStatusIsError = true;
        }
        catch (Exception ex)
        {
            _backupStatusText = "Failed to delete backup. See /xllog for details.";
            _backupStatusIsError = true;
            _logger.LogWarning(ex, "Failed to delete backup {backupId}", backupId);
        }
        finally
        {
            _isBackupBusy = false;
        }
    }

    private async Task RestoreSelectedBackupAsync()
    {
        if (_selectedBackupSummary == null)
        {
            return;
        }

        _backupCts = _backupCts.CancelRecreate();
        var token = _backupCts.Token;

        _isBackupBusy = true;
        _backupStatusIsError = false;
        _backupProgress = 0f;
        _backupStatusText = "Preparing restore…";

        try
        {
            using var fileDownloadManager = _fileDownloadManagerFactory.Create();
            var backup = await GetBackupAsync(_selectedBackupSummary.BackupId, token).ConfigureAwait(false);
            if (backup == null)
            {
                _backupStatusIsError = true;
                _backupStatusText = "Backup not found.";
                return;
            }

            var total = backup.Mods.Count;
            for (var i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var mod = backup.Mods[i];
                var index = i;
                var modFolderName = string.IsNullOrWhiteSpace(mod.ModFolderName) ? mod.Hash : mod.ModFolderName;
                _backupStatusText = $"Downloading {i + 1}/{total}: {modFolderName}";
                _backupProgress = total > 0 ? (float)i / total : 0f;

                var downloadProgress = new Progress<(long TransferredBytes, long TotalBytes)>(tuple =>
                {
                    if (tuple.TotalBytes <= 0)
                    {
                        return;
                    }
                    _backupProgress = (float)index / total + (float)tuple.TransferredBytes / tuple.TotalBytes / total;
                });

                var pmpPath = await fileDownloadManager.DownloadPmpToCacheAsync(mod.Hash, token, downloadProgress).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(pmpPath) || !File.Exists(pmpPath))
                {
                    _backupStatusIsError = true;
                    _backupStatusText = $"Failed to download: {modFolderName}";
                    return;
                }

                _backupStatusText = $"Installing {i + 1}/{total}: {modFolderName}";
                var installProgress = new Progress<(string, int, int)>(tuple =>
                {
                    _backupStatusText = string.IsNullOrWhiteSpace(tuple.Item1)
                        ? $"Installing {modFolderName}… ({tuple.Item2}/{tuple.Item3})"
                        : tuple.Item1;
                });

                var cleanup = _configService.Current.DeletePenumbraModAfterInstall;
                var ok = await _backupService.RestorePmpAsync(modFolderName, pmpPath, installProgress, token, cleanupBackupsAfterRestore: cleanup, deregisterDuringRestore: true).ConfigureAwait(false);
                if (!ok)
                {
                    _backupStatusIsError = true;
                    _backupStatusText = $"Failed to install: {modFolderName}";
                    return;
                }
            }

            _backupProgress = 1.0f;
            _backupStatusText = "Backup restored successfully.";
        }
        catch (OperationCanceledException)
        {
            _backupStatusText = "Restore cancelled.";
            _backupStatusIsError = true;
        }
        catch (Exception ex)
        {
            _backupStatusText = "Restore failed. See /xllog for details.";
            _backupStatusIsError = true;
            _logger.LogWarning(ex, "Failed to restore backup");
        }
        finally
        {
            _isBackupBusy = false;
        }
    }
}
