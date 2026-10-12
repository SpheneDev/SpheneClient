using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.Files;
using Sphene.API.Routes;
using Sphene.PlayerData.Factories;
using Sphene.Services;
using Sphene.Services.ServerConfiguration;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class ModPackageHistoryUi : WindowMediatorSubscriberBase
{
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly FileUploadManager _fileUploadManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory;
    private readonly SpheneConfigService _configService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ShrinkU.Services.TextureBackupService _backupService;
    private readonly ShrinkU.Services.PenumbraIpc _penumbraIpc;
    private readonly SpheneProfileManager _spheneProfileManager;

    private Task<List<ModUploadHistoryEntryDto>?>? _modUploadHistoryTask;
    private Task<List<ModDownloadHistoryEntryDto>?>? _modDownloadHistoryTask;
    private Task<List<ModShareHistoryEntryDto>?>? _modShareHistoryTask;
    private Task<List<ModReceivedHistoryEntryDto>?>? _modReceivedHistoryTask;

    private CancellationTokenSource? _redownloadCts;
    private string _redownloadStatusText = string.Empty;
    private bool _redownloadStatusIsError;
    private float? _redownloadProgress;
    private bool _isRedownloadBusy;
    // Lazily resolved local metadata (artwork) + a short-lived displayName→folder
    // map so history rows can reuse locally known mod images.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<ShrinkU.Services.PenumbraIpc.ModMetadata?>> _modMetaTasks
        = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _localModNameToFolder;
    private DateTime _localModMapNextRefreshUtc = DateTime.MinValue;
    // Last decoded avatar bytes per user — a new array instance means the
    // profile was (re)fetched and the cached texture must be evicted.
    private readonly Dictionary<string, byte[]> _avatarBytes = new(StringComparer.Ordinal);

    private int _activityCurrentPage = 1;
    private int _historyItemsPerPage = 25;
    private ActivityKind? _activityKindFilter;
    private string _activityTextFilter = string.Empty;

    public ModPackageHistoryUi(
        ILogger<ModPackageHistoryUi> logger,
        SpheneMediator mediator,
        FileTransferOrchestrator fileTransferOrchestrator,
        FileUploadManager fileUploadManager,
        FileDownloadManagerFactory fileDownloadManagerFactory,
        SpheneConfigService configService,
        ServerConfigurationManager serverConfigurationManager,
        ShrinkU.Services.TextureBackupService backupService,
        ShrinkU.Services.PenumbraIpc penumbraIpc,
        SpheneProfileManager spheneProfileManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Mod Packages###SpheneModPackageHistory", performanceCollectorService)
    {
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileUploadManager = fileUploadManager;
        _fileDownloadManagerFactory = fileDownloadManagerFactory;
        _configService = configService;
        _serverConfigurationManager = serverConfigurationManager;
        _backupService = backupService;
        _penumbraIpc = penumbraIpc;
        _spheneProfileManager = spheneProfileManager;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700f, 400f),
            MaximumSize = new Vector2(3000f, 3000f),
        };

        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(900, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
    }

    private static void DrawPaginationControls(string idSuffix, int totalItems, ref int currentPage, ref int itemsPerPage)
    {
        var totalPages = (int)Math.Ceiling((double)totalItems / itemsPerPage);
        if (totalPages < 1) totalPages = 1;
        if (currentPage > totalPages) currentPage = totalPages;
        if (currentPage < 1) currentPage = 1;

        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain($"Total: {totalItems} items | Page {currentPage} of {totalPages}");
        
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
            if (SpheneUI.Controls.Selectable($"25##{idSuffix}", itemsPerPage == 25)) itemsPerPage = 25;
            if (SpheneUI.Controls.Selectable($"50##{idSuffix}", itemsPerPage == 50)) itemsPerPage = 50;
            if (SpheneUI.Controls.Selectable($"100##{idSuffix}", itemsPerPage == 100)) itemsPerPage = 100;
            ImGui.EndCombo();
        }
    }

    /// <summary>Which history source an activity row came from.</summary>
    private enum ActivityKind
    {
        Sent,
        Received,
        Downloaded,
        Uploaded
    }

    /// <summary>One normalized row across the four history sources.</summary>
    private sealed record ActivityRow(
        ActivityKind Kind,
        string Id,
        string Hash,
        string? Name,
        string? Author,
        string? Version,
        long Size,
        DateTime Timestamp,
        string? OtherUid,
        string? OtherAlias,
        object Entry);

    internal void DrawActivityPage() => DrawActivity();

    protected override void DrawInternal()
    {
        if (SpheneUI.Layout.BeginScrollArea("modhistory-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawActivity();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    /// <summary>Badge style per activity kind — icon is also the row's fallback glyph.</summary>
    private static (string Label, FontAwesomeIcon Icon, Vector4 Color) KindStyle(ActivityKind kind) => kind switch
    {
        ActivityKind.Sent => ("SENT", FontAwesomeIcon.ShareAlt, SpheneUI.Palette.EtherealPurple),
        ActivityKind.Received => ("RECEIVED", FontAwesomeIcon.Inbox, SpheneUI.Palette.NetworkActive),
        ActivityKind.Downloaded => ("DOWNLOADED", FontAwesomeIcon.CloudDownloadAlt, SpheneUI.Palette.CrystalBlue),
        ActivityKind.Uploaded => ("UPLOADED", FontAwesomeIcon.CloudUploadAlt, SpheneUI.Palette.SpheneGold),
        _ => ("UNKNOWN", FontAwesomeIcon.Box, SpheneUI.Palette.NetworkInactive)
    };

    /// <summary>Header row of a history page — title, description and a right-aligned refresh button.</summary>
    private void DrawHistoryPageHeader(string title, string description, Action onRefresh)
    {
        var scale = ImGuiHelpers.GlobalScale;
        SpheneUI.Text.Big(title);
        ImGui.SameLine();
        var refreshWidth = ImGui.CalcTextSize("Refresh").X + 28f * scale;
        SpheneUI.Controls.DockToRightEdge(refreshWidth);
        using (ImRaii.Disabled(!_fileTransferOrchestrator.IsInitialized))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.RedoAlt, "Refresh"))
            {
                onRefresh();
            }
        }
        SpheneUI.Text.MutedWrapped(description);
        SpheneUI.Layout.VerticalSpace();
    }

    private bool DrawTransferNotReady()
    {
        if (_fileTransferOrchestrator.IsInitialized)
        {
            return false;
        }
        SpheneUI.Layout.Callout("File transfer service is not initialized. Connect to a server first.",
            FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
        return true;
    }

    /// <summary>
    /// One rich history entry: thumbnail slot (local mod artwork when the mod
    /// still exists in Penumbra, page icon otherwise), title + meta line,
    /// optional right-click context menu and trailing widgets (date, actions)
    /// drawn by the caller at the right edge.
    /// </summary>
    private void DrawHistoryEntryRow(string id, string title, string meta,
        FontAwesomeIcon icon, float trailingReserve, string? localFolderName = null,
        UserData? fallbackUser = null, Vector4? accent = null, Action? contextMenu = null,
        Action? trailing = null)
    {
        IDalamudTextureWrap? thumb = null;
        var localMeta = localFolderName == null ? null : MetaOf(localFolderName);
        if (localMeta?.ImagePath is { } imgPath)
        {
            thumb = SpheneUI.Media.Texture($"modthumb:{localFolderName}", () => ReadModImage(imgPath));
        }
        // No local artwork → the other party's avatar is more informative
        // than a bare page icon.
        thumb ??= fallbackUser == null ? null : AvatarFor(fallbackUser);
        var row = ModListRows.Draw(id, title, meta, thumb, icon,
            selected: false, accent ?? SpheneUI.Palette.EtherealPurple, trailingReserve: trailingReserve);
        if (contextMenu != null && SpheneUI.Popups.BeginContextStyled($"hctx_{id}"))
        {
            contextMenu();
            SpheneUI.Popups.EndStyled();
        }
        if (trailing != null)
        {
            ImGui.SetCursorScreenPos(row.TrailingPos);
            trailing();
            ImGui.SetCursorScreenPos(row.RowEnd);
        }
    }

    /// <summary>
    /// The user's Sphene profile picture as a cached texture — null while the
    /// profile or texture is still loading. Evicts the cache entry when the
    /// profile data instance changes (avatar updated/refetched).
    /// </summary>
    private IDalamudTextureWrap? AvatarFor(UserData userData)
    {
        var uid = userData.UID;
        if (string.IsNullOrWhiteSpace(uid)) return null;
        var bytes = _spheneProfileManager.GetSpheneProfile(userData).ImageData.Value;
        if (bytes.Length == 0) return null;
        var key = $"modshare:avatar:{uid}";
        if (!_avatarBytes.TryGetValue(uid, out var cached) || !ReferenceEquals(cached, bytes))
        {
            _avatarBytes[uid] = bytes;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Texture(key, bytes);
    }

    /// <summary>Requests local mod metadata once per folder; returns it once resolved.</summary>
    private ShrinkU.Services.PenumbraIpc.ModMetadata? MetaOf(string modFolderName)
    {
        if (string.IsNullOrWhiteSpace(modFolderName)) return null;
        var task = _modMetaTasks.GetOrAdd(modFolderName,
            f => Task.Run(() => _penumbraIpc.GetModMetadataAsync(f)));
        return task is { IsCompletedSuccessfully: true } ? task.Result : null;
    }

    /// <summary>Loads the mod's artwork bytes for the texture cache; null when absent/unreadable.</summary>
    private static byte[]? ReadModImage(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a history entry's display name to a local Penumbra mod folder.
    /// The map refreshes at most every 10 seconds — the IPC list call never
    /// runs per frame.
    /// </summary>
    private string? ResolveLocalFolderForName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        if (DateTime.UtcNow >= _localModMapNextRefreshUtc)
        {
            _localModMapNextRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            try
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _penumbraIpc.GetModList())
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value) && !string.IsNullOrWhiteSpace(kv.Key))
                    {
                        map[kv.Value] = kv.Key;
                    }
                }
                _localModNameToFolder = map;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to refresh local mod map for history thumbnails");
            }
        }
        return _localModNameToFolder != null
            && _localModNameToFolder.TryGetValue(displayName, out var folder) ? folder : null;
    }

    /// <summary>
    /// The unified activity feed — merges all four history sources into one
    /// chronological list with kind chips, text search and per-kind actions.
    /// </summary>
    private void DrawActivity()
    {
        var scale = ImGuiHelpers.GlobalScale;

        DrawHistoryPageHeader("Mod Activity",
            "Everything you sent, received, uploaded and downloaded — newest first. Right-click a row for actions.",
            RefreshAllHistories);

        if (DrawTransferNotReady())
        {
            return;
        }

        DrawRedownloadStatus();

        // All four sources fetch lazily and independently — the feed shows
        // whatever has resolved already.
        _modUploadHistoryTask ??= GetModUploadHistory(CancellationToken.None);
        _modDownloadHistoryTask ??= GetModDownloadHistory(CancellationToken.None);
        _modShareHistoryTask ??= GetModShareHistory(CancellationToken.None);
        _modReceivedHistoryTask ??= GetModReceivedHistory(CancellationToken.None);

        var failedSources = FailedHistorySources();
        if (failedSources.Count > 0)
        {
            SpheneUI.Layout.Callout(
                $"Failed to load the {string.Join(", ", failedSources)} history — hit Refresh to retry.",
                FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudRed);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        ImGui.SetNextItemWidth(200f * scale);
        if (SpheneUI.Controls.InputTextWithHint("##activityTextFilter",
                "Search name, author or user…", ref _activityTextFilter, 128))
        {
            _activityCurrentPage = 1;
        }
        ImGui.SameLine(0f, SpheneUI.Spacing.X12.Scaled);
        ActivityKindChip("All", null);
        foreach (var kind in Enum.GetValues<ActivityKind>())
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            ActivityKindChip(KindStyle(kind).Label, kind);
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        var stillLoading = IsAnyHistoryLoading();
        if (stillLoading)
        {
            SpheneUI.Text.Muted("Loading history…");
        }

        var feed = BuildFilteredFeed();
        if (feed.Count == 0)
        {
            if (!stillLoading && failedSources.Count == 0)
            {
                SpheneUI.Layout.Callout(
                    "No mod activity yet — packages you send or receive appear here.",
                    FontAwesomeIcon.Inbox, ImGuiColors.DalamudGrey);
            }
            return;
        }

        DrawPaginationControls("Activity", feed.Count, ref _activityCurrentPage, ref _historyItemsPerPage);

        var startIndex = (_activityCurrentPage - 1) * _historyItemsPerPage;
        foreach (var row in feed.Skip(startIndex).Take(_historyItemsPerPage))
        {
            DrawActivityRow(row);
        }
    }

    private void RefreshAllHistories()
    {
        _modUploadHistoryTask = GetModUploadHistory(CancellationToken.None);
        _modDownloadHistoryTask = GetModDownloadHistory(CancellationToken.None);
        _modShareHistoryTask = GetModShareHistory(CancellationToken.None);
        _modReceivedHistoryTask = GetModReceivedHistory(CancellationToken.None);
    }

    /// <summary>Names of the history sources whose fetch task failed.</summary>
    private List<string> FailedHistorySources()
    {
        var failed = new List<string>(4);
        if (IsFailed(_modUploadHistoryTask)) failed.Add("uploaded");
        if (IsFailed(_modDownloadHistoryTask)) failed.Add("downloaded");
        if (IsFailed(_modShareHistoryTask)) failed.Add("shared");
        if (IsFailed(_modReceivedHistoryTask)) failed.Add("received");
        return failed;

        static bool IsFailed<T>(Task<List<T>?>? task)
            => task is { IsCompleted: true } && (!task.IsCompletedSuccessfully || task.Result == null);
    }

    private bool IsAnyHistoryLoading()
        => _modUploadHistoryTask is not { IsCompleted: true }
           || _modDownloadHistoryTask is not { IsCompleted: true }
           || _modShareHistoryTask is not { IsCompleted: true }
           || _modReceivedHistoryTask is not { IsCompleted: true };

    /// <summary>Radio-style filter chip — selecting a kind deselects the others via "All".</summary>
    private void ActivityKindChip(string label, ActivityKind? kind)
    {
        var active = _activityKindFilter == kind;
        if (SpheneUI.Controls.ToggleChip(label, ref active))
        {
            _activityKindFilter = active ? kind : null;
            _activityCurrentPage = 1;
        }
    }

    /// <summary>Merges resolved history tasks into the sorted, filtered feed.</summary>
    private List<ActivityRow> BuildFilteredFeed()
    {
        var feed = new List<ActivityRow>();

        if (_modShareHistoryTask is { IsCompletedSuccessfully: true } && _modShareHistoryTask.Result != null)
        {
            foreach (var e in _modShareHistoryTask.Result)
            {
                feed.Add(new ActivityRow(ActivityKind.Sent,
                    $"s_{e.Hash}_{e.SharedAt.Ticks}_{e.RecipientUID}",
                    e.Hash ?? string.Empty, e.Name, e.Author, e.Version, e.Size, e.SharedAt,
                    e.RecipientUID, e.RecipientAlias, e));
            }
        }

        if (_modReceivedHistoryTask is { IsCompletedSuccessfully: true } && _modReceivedHistoryTask.Result != null)
        {
            foreach (var e in _modReceivedHistoryTask.Result)
            {
                feed.Add(new ActivityRow(ActivityKind.Received,
                    $"r_{e.Hash}_{e.ReceivedAt.Ticks}_{e.SenderUID}",
                    e.Hash ?? string.Empty, e.Name, e.Author, e.Version, e.Size, e.ReceivedAt,
                    e.SenderUID, e.SenderAlias, e));
            }
        }

        if (_modDownloadHistoryTask is { IsCompletedSuccessfully: true } && _modDownloadHistoryTask.Result != null)
        {
            foreach (var e in _modDownloadHistoryTask.Result)
            {
                feed.Add(new ActivityRow(ActivityKind.Downloaded,
                    $"d_{e.Hash}_{e.DownloadedAt.Ticks}",
                    e.Hash ?? string.Empty, e.Name, e.Author, e.Version, e.Size, e.DownloadedAt,
                    null, null, e));
            }
        }

        if (_modUploadHistoryTask is { IsCompletedSuccessfully: true } && _modUploadHistoryTask.Result != null)
        {
            foreach (var e in _modUploadHistoryTask.Result)
            {
                feed.Add(new ActivityRow(ActivityKind.Uploaded,
                    $"u_{e.Hash}_{e.UploadedDate.Ticks}",
                    e.Hash ?? string.Empty, e.Name, e.Author, e.Version, e.Size, e.UploadedDate,
                    null, null, e));
            }
        }

        feed.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));

        var text = _activityTextFilter.Trim();
        var hasText = !string.IsNullOrEmpty(text);
        if (_activityKindFilter == null && !hasText)
        {
            return feed;
        }

        var filtered = new List<ActivityRow>(feed.Count);
        foreach (var row in feed)
        {
            if (_activityKindFilter != null && row.Kind != _activityKindFilter.Value)
            {
                continue;
            }

            if (hasText && !MatchesActivityText(row, text))
            {
                continue;
            }

            filtered.Add(row);
        }

        return filtered;
    }

    private bool MatchesActivityText(ActivityRow row, string text)
    {
        if (row.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
            row.Author?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
            row.Hash.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(row.OtherUid) || !string.IsNullOrWhiteSpace(row.OtherAlias))
        {
            var otherName = _serverConfigurationManager.GetPreferredUserDisplayName(
                row.OtherUid ?? string.Empty,
                !string.IsNullOrWhiteSpace(row.OtherAlias) ? row.OtherAlias : row.OtherUid);
            if (!string.IsNullOrWhiteSpace(otherName) &&
                otherName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One activity row: kind-coloured accent + icon, local artwork or the
    /// other party's avatar, kind badge + date + re-download in the trailing
    /// zone, per-kind context menu.
    /// </summary>
    private void DrawActivityRow(ActivityRow row)
    {
        var scale = SpheneUI.Scale;
        var (badge, icon, color) = KindStyle(row.Kind);
        var date = row.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var hasOther = !string.IsNullOrWhiteSpace(row.OtherUid) || !string.IsNullOrWhiteSpace(row.OtherAlias);
        var otherName = hasOther
            ? _serverConfigurationManager.GetPreferredUserDisplayName(
                row.OtherUid ?? string.Empty,
                !string.IsNullOrWhiteSpace(row.OtherAlias) ? row.OtherAlias : row.OtherUid)
            : null;
        var whoText = row.Kind switch
        {
            ActivityKind.Sent when !string.IsNullOrWhiteSpace(otherName) => $"→ {otherName}",
            ActivityKind.Received when !string.IsNullOrWhiteSpace(otherName) => $"from {otherName}",
            _ => null
        };

        var meta = ModListRows.Meta(whoText, row.Author,
            row.Version is { } v ? $"v{v}" : null,
            UiSharedService.ByteToString(row.Size));

        var canRedownload = row.Kind is ActivityKind.Sent or ActivityKind.Downloaded or ActivityKind.Uploaded;
        var trailW = ImGui.CalcTextSize(badge).X + 14f * scale
            + ImGui.CalcTextSize(date).X + 12f * scale
            + (canRedownload ? 110f * scale : 0f);

        DrawHistoryEntryRow(row.Id,
            string.IsNullOrWhiteSpace(row.Name) ? row.Hash : row.Name!,
            meta, icon, trailW,
            localFolderName: ResolveLocalFolderForName(row.Name),
            fallbackUser: hasOther ? new UserData(row.OtherUid ?? string.Empty, row.OtherAlias) : null,
            accent: color,
            contextMenu: () => DrawActivityContextMenu(row, otherName),
            trailing: () =>
            {
                SpheneUI.Layout.Badge(badge, color);
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Muted(date);
                if (canRedownload)
                {
                    ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                    using (ImRaii.Disabled(_isRedownloadBusy))
                    {
                        if (SpheneUI.Controls.SmallButton($"Re-download##{row.Id}"))
                        {
                            StartRedownload(row.Entry);
                        }
                    }
                }
            });
    }

    /// <summary>Per-kind row actions — reshare targets the recorded recipient, "Send to…" opens the send flow.</summary>
    private void DrawActivityContextMenu(ActivityRow row, string? otherName)
    {
        if (row.Entry is ModShareHistoryEntryDto share &&
            ImGui.MenuItem($"Send again to {(string.IsNullOrWhiteSpace(otherName) ? "-" : otherName)}"))
        {
            _ = ReshareModAsync(share);
        }

        if (ImGui.MenuItem("Send to..."))
        {
            Mediator.Publish(new OpenSendPenumbraModWindow(null, row.Name));
        }
    }



    private void DrawRedownloadStatus()
    {
        if (string.IsNullOrWhiteSpace(_redownloadStatusText) && !_isRedownloadBusy)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_redownloadStatusText))
        {
            SpheneUI.Text.ColoredWrapped(_redownloadStatusText, _redownloadStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey);
        }

        if (_redownloadProgress is >= 0f and <= 1f)
        {
            SpheneUI.Controls.ProgressBar(_redownloadProgress.Value, new Vector2(-1, 0), string.Empty);
        }

        if (_isRedownloadBusy)
        {
            SpheneUI.Layout.VerticalSpace();
            if (SpheneUI.Controls.SmallButton("Cancel re-download"))
            {
                _redownloadCts?.Cancel();
            }
        }

        SpheneUI.Layout.Separator();
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

    private void StartRedownload(object entry)
    {
        switch (entry)
        {
            case ModUploadHistoryEntryDto upload: StartRedownload(upload); break;
            case ModShareHistoryEntryDto share: StartRedownload(share); break;
            case ModDownloadHistoryEntryDto download: StartRedownload(download); break;
        }
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

}
