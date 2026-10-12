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
using Sphene.Services;
using Sphene.Services.Mediator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sphene.WebAPI.Files;
using Sphene.API.Routes;
using System.Text.Json;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class PenumbraReceiveModUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private readonly ShrinkU.Services.PenumbraIpc _shrinkuPenumbraIpc;
    private readonly SpheneProfileManager _spheneProfileManager;

    private readonly List<FileTransferNotificationDto> _pendingNotifications = new();
    private FileTransferNotificationDto? _selectedNotification;
    private string _statusText = string.Empty;
    private bool _statusIsError;
    private bool _isInstalling;
    private float? _progress;
    private string? _installedVersion;
    private FileTransferNotificationDto? _lastSelectedNotification;
    private string _pendingFilter = string.Empty;
    
    private readonly HashSet<FileTransferNotificationDto> _selectedForBatch = new();
    private readonly Queue<FileTransferNotificationDto> _installQueue = new();
    private readonly System.Threading.Lock _pendingLock = new();
    private int _batchTotal;
    private int _batchCurrent;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool IsInstalled, string? Version)> _modStatusCache = new(StringComparer.Ordinal);
    // Lazily resolved metadata for locally installed mods (thumbnails, author).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<ShrinkU.Services.PenumbraIpc.ModMetadata?>> _modMetaTasks
        = new(StringComparer.OrdinalIgnoreCase);
    // Last decoded avatar bytes per user — a new array instance means the
    // profile was (re)fetched and the cached texture must be evicted.
    private readonly Dictionary<string, byte[]> _avatarBytes = new(StringComparer.Ordinal);

    // History Tab State


    public PenumbraReceiveModUi(
        ILogger<PenumbraReceiveModUi> logger,
        SpheneMediator mediator,
        UiSharedService uiSharedService,
        PerformanceCollectorService performanceCollectorService,
        ShrinkU.Services.PenumbraIpc shrinkuPenumbraIpc,
        SpheneProfileManager spheneProfileManager)
        : base(logger, mediator, "Receive Mods###SpheneReceiveMods", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _shrinkuPenumbraIpc = shrinkuPenumbraIpc;
        _spheneProfileManager = spheneProfileManager;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800f, 500f),
            MaximumSize = new Vector2(3000f, 3000f),
        };
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(900, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;

        Mediator.Subscribe<OpenPenumbraReceiveModWindow>(this, OnOpenMessage);
        Mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnCompletedMessage);
        Mediator.Subscribe<PenumbraModTransferProgressMessage>(this, OnProgressMessage);
        Mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnTransferAvailableMessage);
    }

    private void OnTransferAvailableMessage(PenumbraModTransferAvailableMessage message)
    {
        lock (_pendingLock)
        {
            if (_pendingNotifications.Any(n => string.Equals(n.Hash, message.Notification.Hash, StringComparison.Ordinal)))
            {
                return;
            }

            _pendingNotifications.Add(message.Notification);
        }
        UpdateModStatusCache();
    }

    private void OnOpenMessage(OpenPenumbraReceiveModWindow message)
    {
        lock (_pendingLock)
        {
            _pendingNotifications.Clear();
            _pendingNotifications.AddRange(message.Notifications);
            _selectedNotification = _pendingNotifications.FirstOrDefault();
            _selectedForBatch.Clear();
            _installQueue.Clear();
            _batchTotal = 0;
            _batchCurrent = 0;
        }

        if (string.IsNullOrWhiteSpace(_statusText))
        {
            _statusText = "Ready to install Penumbra mod package.";
        }

        _statusIsError = false;
        _pendingFilter = string.Empty;
        
        _modStatusCache.Clear();
        UpdateModStatusCache();
    }

    private void UpdateModStatusCache()
    {
        List<string?> itemsToCheck;
        lock (_pendingLock)
        {
            itemsToCheck = _pendingNotifications
                .Select(n => n.ModFolderName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        _ = Task.Run(async () =>
        {
            foreach (var folder in itemsToCheck)
            {
                if (folder == null)
                {
                    continue;
                }
                
                try
                {
                    if (_shrinkuPenumbraIpc.ModExists(folder))
                    {
                        var meta = await _shrinkuPenumbraIpc.GetModMetadataAsync(folder).ConfigureAwait(false);
                        _modStatusCache[folder] = (true, meta?.Version);
                    }
                    else
                    {
                        _modStatusCache[folder] = (false, null);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update mod status cache for folder {Folder}", folder);
                }
            }
        });
    }

    private void StartBatchInstall()
    {
        List<FileTransferNotificationDto> itemsToInstall;
        lock (_pendingLock)
        {
            if (_installQueue.Count > 0 || _isInstalling) return;

            itemsToInstall = _pendingNotifications
                .Where(item => _selectedForBatch.Contains(item))
                .ToList();

            foreach (var item in itemsToInstall)
            {
                _installQueue.Enqueue(item);
            }

            if (_installQueue.Count == 0) return;

            _batchTotal = _installQueue.Count;
            _batchCurrent = 0;
        }
        
        ProcessNextBatchItem();
    }

    private void ProcessNextBatchItem()
    {
        if (_installQueue.Count == 0)
        {
            _isInstalling = false;
            _statusText = "Batch installation completed.";
            return;
        }

        var next = _installQueue.Peek();
        _batchCurrent++;
        _isInstalling = true;
        _statusText = $"Batch installing {_batchCurrent}/{_batchTotal}: {next.ModFolderName ?? "Unknown"}…";
        _progress = 0f;

        Mediator.Publish(new InstallReceivedPenumbraModMessage(next));
    }

    private void BatchDiscard()
    {
        List<FileTransferNotificationDto> toRemove;
        bool closeWindow;
        lock (_pendingLock)
        {
            toRemove = _pendingNotifications.Where(n => _selectedForBatch.Contains(n)).ToList();
            foreach (var item in toRemove)
            {
                _pendingNotifications.Remove(item);
                _selectedForBatch.Remove(item);
            }

            if (_pendingNotifications.Count == 0)
            {
                closeWindow = true;
                _selectedNotification = null;
            }
            else
            {
                closeWindow = false;
                if (_selectedNotification != null && !_pendingNotifications.Contains(_selectedNotification))
                {
                    _selectedNotification = _pendingNotifications.FirstOrDefault();
                }
            }
        }

        foreach (var item in toRemove)
        {
            Mediator.Publish(new PenumbraModTransferDiscardedMessage(item));
            if (!string.IsNullOrEmpty(item.Hash) && !string.IsNullOrEmpty(item.Sender?.UID))
            {
                Mediator.Publish(new FileTransferAckMessage(item.Hash, item.Sender.UID));
            }
        }

        if (closeWindow)
        {
            IsOpen = false;
        }
    }

    private void DiscardAll()
    {
        List<FileTransferNotificationDto> toRemove;
        lock (_pendingLock)
        {
            toRemove = _pendingNotifications.ToList();
            _pendingNotifications.Clear();
            _selectedForBatch.Clear();
            _installQueue.Clear();
            _selectedNotification = null;
            _batchTotal = 0;
            _batchCurrent = 0;
        }

        foreach (var item in toRemove)
        {
            Mediator.Publish(new PenumbraModTransferDiscardedMessage(item));
            if (!string.IsNullOrEmpty(item.Hash) && !string.IsNullOrEmpty(item.Sender?.UID))
            {
                Mediator.Publish(new FileTransferAckMessage(item.Hash, item.Sender.UID));
            }
        }

        IsOpen = false;
    }

    private void SelectAll()
    {
        lock (_pendingLock)
        {
            _selectedForBatch.Clear();
            foreach (var item in _pendingNotifications)
            {
                _selectedForBatch.Add(item);
            }
        }
    }

    private void SelectInstalled()
    {
        lock (_pendingLock)
        {
            _selectedForBatch.Clear();
            foreach (var item in _pendingNotifications)
            {
                if (IsInstalled(item))
                {
                    _selectedForBatch.Add(item);
                }
            }
        }
    }

    private void SelectNew()
    {
        lock (_pendingLock)
        {
            _selectedForBatch.Clear();
            foreach (var item in _pendingNotifications)
            {
                if (!IsInstalled(item))
                {
                    _selectedForBatch.Add(item);
                }
            }
        }
    }

    private bool IsInstalled(FileTransferNotificationDto notification)
    {
        var name = notification.ModFolderName ?? string.Empty;
        return !string.IsNullOrWhiteSpace(name) &&
               _modStatusCache.TryGetValue(name, out var status) &&
               status.IsInstalled;
    }

    private void OnCompletedMessage(PenumbraModTransferCompletedMessage message)
    {
        var modFolderName = message.Notification.ModFolderName ?? string.Empty;
        if (message.Success)
        {
            _statusText = string.IsNullOrWhiteSpace(modFolderName)
                ? "Installed Penumbra mod from received package."
                : $"Installed Penumbra mod '{modFolderName}' from received package.";
            _statusIsError = false;

            bool closeWindow;
            lock (_pendingLock)
            {
                _pendingNotifications.RemoveAll(n =>
                    string.Equals(n.Hash, message.Notification.Hash, StringComparison.Ordinal));
                _selectedForBatch.RemoveWhere(n =>
                    string.Equals(n.Hash, message.Notification.Hash, StringComparison.Ordinal));

                if (_selectedNotification != null &&
                    string.Equals(_selectedNotification.Hash, message.Notification.Hash, StringComparison.Ordinal))
                {
                    _selectedNotification = _pendingNotifications.FirstOrDefault();
                }

                closeWindow = _pendingNotifications.Count == 0;
            }

            if (closeWindow)
            {
                IsOpen = false;
            }
        }
        else
        {
            _statusText = string.IsNullOrWhiteSpace(modFolderName)
                ? "Failed to install Penumbra mod from package."
                : $"Failed to install Penumbra mod '{modFolderName}' from package.";
            _statusIsError = true;
        }

        if (_installQueue.Count > 0 &&
            string.Equals(_installQueue.Peek().Hash, message.Notification.Hash, StringComparison.Ordinal))
        {
            _installQueue.Dequeue();
            if (_installQueue.Count > 0)
            {
                ProcessNextBatchItem();
                return;
            }
        }

        if (_installQueue.Count == 0)
        {
            _isInstalling = false;
            _progress = 1.0f;
        }

        UpdateModStatusCache();
    }

    private void OnProgressMessage(PenumbraModTransferProgressMessage message)
    {
        if (_selectedNotification == null ||
            !string.Equals(_selectedNotification.Hash, message.Notification.Hash, StringComparison.Ordinal))
        {
            return;
        }

        _statusText = message.Status;
        _statusIsError = false;
        _progress = message.Progress;
    }

    protected override void DrawInternal()
    {
        if (SpheneUI.Layout.BeginScrollArea("ReceiveModsRootWindow",
                ImGui.GetContentRegionAvail(), contentPadding: Vector2.Zero))
        {
            DrawPendingTab(isEmbedded: false);
        }
        SpheneUI.Layout.EndScrollArea();
    }

    internal void DrawEmbedded()
    {
        if (SpheneUI.Layout.BeginScrollArea("ReceiveModsRootEmbedded",
                ImGui.GetContentRegionAvail(), contentPadding: Vector2.Zero))
        {
            DrawPendingTab(isEmbedded: true);
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawPendingTab(bool isEmbedded)
    {
        var scale = ImGuiHelpers.GlobalScale;

        List<FileTransferNotificationDto> pending;
        FileTransferNotificationDto? currentSelected;
        lock (_pendingLock)
        {
            if (_selectedNotification != null && !_pendingNotifications.Contains(_selectedNotification))
            {
                _selectedNotification = _pendingNotifications.FirstOrDefault();
            }
            if (_selectedNotification == null && _pendingNotifications.Count > 0)
            {
                _selectedNotification = _pendingNotifications.FirstOrDefault();
            }

            pending = _pendingNotifications.ToList();
            currentSelected = _selectedNotification;
        }

        List<FileTransferNotificationDto> displayed;
        if (string.IsNullOrWhiteSpace(_pendingFilter))
        {
            displayed = pending;
        }
        else
        {
            var filter = _pendingFilter.Trim();
            displayed = new List<FileTransferNotificationDto>(pending.Count);
            foreach (var notif in pending)
            {
                var name = notif.ModFolderName ?? string.Empty;
                var senderUid = notif.Sender?.UID ?? string.Empty;
                var senderAliasOrUid = notif.Sender?.AliasOrUID ?? string.Empty;
                var sender = _uiSharedService.GetPreferredUserDisplayName(senderUid, senderAliasOrUid);
                if (name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    sender.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    senderAliasOrUid.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    senderUid.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    displayed.Add(notif);
                }
            }
        }

        // Detect selection change to fetch installed version
        if (currentSelected != _lastSelectedNotification)
        {
            _lastSelectedNotification = currentSelected;
            _installedVersion = null;
            if (currentSelected != null)
            {
                var folder = currentSelected.ModFolderName;
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    _ = Task.Run(async () =>
                    {
                        var meta = await _shrinkuPenumbraIpc.GetModMetadataAsync(folder).ConfigureAwait(false);
                        _installedVersion = meta?.Version;
                    });
                }
            }
        }

        var accent = SpheneUI.Palette.NetworkActive;
        var colBottom = ImGui.GetCursorScreenPos().Y + ImGui.GetContentRegionAvail().Y;

        if (ImGui.BeginTable("MainLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Inbox", ImGuiTableColumnFlags.WidthStretch, 0.40f);
            ImGui.TableSetupColumn("ModDetails", ImGuiTableColumnFlags.WidthStretch);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            // LEFT COLUMN: Inbox card — filter, select-toolbar, scroll list, action footer.
            var buttonHeight = ImGui.GetFrameHeightWithSpacing();
            var footerReserve = buttonHeight + ImGui.GetStyle().ItemSpacing.Y * 3f
                + SpheneUI.Spacing.X8.Scaled;

            SpheneUI.Layout.SectionCard(() =>
            {
                ImGui.SetNextItemWidth(-1);
                SpheneUI.Controls.InputTextWithHint("##pendingFilter", "Filter mods or sender…", ref _pendingFilter, 64);

                var hasPending = pending.Count > 0;
                using (ImRaii.Disabled(_isInstalling || !hasPending))
                {
                    SpheneUI.Text.Muted("Select:");
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    if (SpheneUI.Controls.SmallButton("All")) SelectAll();
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    if (SpheneUI.Controls.SmallButton("Installed")) SelectInstalled();
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    if (SpheneUI.Controls.SmallButton("New")) SelectNew();
                }
                ImGui.SameLine();
                SpheneUI.Controls.DockToRightEdge(ImGui.CalcTextSize("Discard all").X + SpheneUI.Spacing.X16.Scaled);
                using (ImRaii.Disabled(_isInstalling || !hasPending))
                {
                    if (SpheneUI.Controls.SmallButton($"Discard all ({pending.Count})"))
                    {
                        DiscardAll();
                    }
                }

                var listHeight = MathF.Max(80f * scale,
                    colBottom - ImGui.GetCursorScreenPos().Y - footerReserve);
                if (SpheneUI.Layout.BeginScrollArea("LeftList", new Vector2(0f, listHeight)))
                {
                    if (_pendingNotifications.Count == 0)
                    {
                        SpheneUI.Text.Muted("No packages pending — mods shared with you appear here.");
                    }
                    else if (displayed.Count == 0)
                    {
                        SpheneUI.Text.Muted("No matches.");
                    }
                    else
                    {
                        void DrawNotificationEntry(FileTransferNotificationDto notif)
                        {
                            using var id = ImRaii.PushId(notif.Hash ?? string.Empty);

                            var name = notif.ModFolderName ?? "Unknown";
                            var modInfo = notif.ModInfo?.FirstOrDefault();
                            var senderName = _uiSharedService.GetPreferredUserDisplayName(
                                notif.Sender?.UID ?? string.Empty, notif.Sender?.AliasOrUID ?? string.Empty);

                            Vector4 color;
                            var icon = FontAwesomeIcon.Plus;
                            var statusTip = "New mod";

                            if (_modStatusCache.TryGetValue(name, out var status) && status.IsInstalled)
                            {
                                var receivedVer = modInfo?.Version;
                                if (receivedVer != null && status.Version != null)
                                {
                                    var comparison = CompareVersions(receivedVer, status.Version);
                                    if (comparison > 0)
                                    {
                                        color = ImGuiColors.DalamudYellow;
                                        icon = FontAwesomeIcon.ArrowCircleUp;
                                        statusTip = $"Upgrade: {status.Version} → {receivedVer}";
                                    }
                                    else if (comparison < 0)
                                    {
                                        color = ImGuiColors.DalamudOrange;
                                        icon = FontAwesomeIcon.ArrowCircleDown;
                                        statusTip = $"Downgrade: {status.Version} → {receivedVer}";
                                    }
                                    else
                                    {
                                        color = ImGuiColors.HealerGreen;
                                        icon = FontAwesomeIcon.Check;
                                        statusTip = $"Installed: {status.Version}";
                                    }
                                }
                                else
                                {
                                    color = ImGuiColors.HealerGreen;
                                    icon = FontAwesomeIcon.Check;
                                    statusTip = "Already installed";
                                }
                            }
                            else
                            {
                                color = ImGuiColors.TankBlue;
                            }

                            var meta = MetaOf(name);
                            var thumb = meta?.ImagePath is { } imgPath
                                ? SpheneUI.Media.Texture($"modthumb:{name}", () => ReadModImage(imgPath))
                                : null;
                            // Packages are usually not installed yet → fall back
                            // to the sender's avatar instead of a bare icon.
                            thumb ??= notif.Sender == null ? null : AvatarFor(notif.Sender);
                            var title = string.IsNullOrWhiteSpace(modInfo?.Name) ? name : modInfo!.Name;
                            var metaText = ModListRows.Meta(
                                string.IsNullOrWhiteSpace(senderName) ? "Unknown sender" : senderName,
                                modInfo?.Version is { } v ? $"v{v}" : null,
                                modInfo?.Author);

                            var trailW = ImGui.GetFrameHeight() + 26f * scale;
                            var row = ModListRows.Draw(notif.Hash ?? name, title, metaText,
                                thumb, FontAwesomeIcon.BoxOpen, currentSelected == notif,
                                accent, trailingReserve: trailW);

                            // trailing: status icon + batch checkbox (drawn after the row →
                            // the checkbox wins input inside its own rect)
                            ImGui.SetCursorScreenPos(row.TrailingPos);
                            using (SpheneUI.IconFont.Push())
                            {
                                using (ImRaii.PushColor(ImGuiCol.Text, color))
                                {
                                    SpheneUI.Text.Plain(icon.ToIconString());
                                }
                            }
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip(statusTip);
                            ImGui.SameLine(0f, 6f * scale);

                            bool isChecked;
                            lock (_pendingLock)
                            {
                                isChecked = _selectedForBatch.Contains(notif);
                            }
                            if (SpheneUI.Controls.Checkbox("##check", ref isChecked))
                            {
                                lock (_pendingLock)
                                {
                                    if (isChecked) _selectedForBatch.Add(notif);
                                    else _selectedForBatch.Remove(notif);
                                }
                            }
                            ImGui.SetCursorScreenPos(row.RowEnd);

                            if (row.Clicked)
                            {
                                lock (_pendingLock)
                                {
                                    _selectedNotification = notif;
                                }
                            }
                        }

                        var senderGroups = new Dictionary<string, List<FileTransferNotificationDto>>(StringComparer.Ordinal);
                        var senderLabels = new Dictionary<string, string>(StringComparer.Ordinal);

                        foreach (var notif in displayed)
                        {
                            var uid = notif.Sender?.UID ?? string.Empty;
                            var aliasOrUid = notif.Sender?.AliasOrUID ?? string.Empty;
                            var key = string.IsNullOrWhiteSpace(uid) ? (string.IsNullOrWhiteSpace(aliasOrUid) ? "Unknown" : aliasOrUid) : uid;
                            if (!senderGroups.TryGetValue(key, out var list))
                            {
                                list = new List<FileTransferNotificationDto>();
                                senderGroups[key] = list;
                                var label = _uiSharedService.GetPreferredUserDisplayName(uid, aliasOrUid);
                                senderLabels[key] = string.IsNullOrWhiteSpace(label) ? "Unknown" : label;
                            }
                            list.Add(notif);
                        }

                        if (senderGroups.Count > 1)
                        {
                            foreach (var key in senderGroups.Keys.OrderBy(k => senderLabels[k], StringComparer.OrdinalIgnoreCase))
                            {
                                var senderLabel = senderLabels[key];
                                var list = senderGroups[key];

                                var containsSelected = false;
                                if (currentSelected != null)
                                {
                                    foreach (var n in list)
                                    {
                                        if (ReferenceEquals(n, currentSelected))
                                        {
                                            containsSelected = true;
                                            break;
                                        }
                                    }
                                }

                                var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow;
                                if (containsSelected) flags |= ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Selected;

                                using var id = ImRaii.PushId("sender" + key);
                                var isOpen = ImGui.TreeNodeEx($"{senderLabel} ({list.Count})##node", flags);
                                if (isOpen)
                                {
                                    foreach (var notif in list.OrderBy(n => n.ModFolderName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                                    {
                                        DrawNotificationEntry(notif);
                                    }
                                    ImGui.TreePop();
                                }
                            }
                        }
                        else
                        {
                            foreach (var notif in displayed.OrderBy(n => n.ModFolderName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                            {
                                DrawNotificationEntry(notif);
                            }
                        }
                    }
                }
                SpheneUI.Layout.EndScrollArea(SpheneUI.Layout.NavRailBandColor());

                SpheneUI.Layout.Separator();

                var selectedCount = _selectedForBatch.Count;
                using (ImRaii.Disabled(_isInstalling || selectedCount == 0))
                {
                    if (SpheneUI.Controls.Button($"Install selected ({selectedCount})"))
                    {
                        StartBatchInstall();
                    }
                    ImGui.SameLine();
                    if (SpheneUI.Controls.SmallButton($"Discard selected ({selectedCount})"))
                    {
                        BatchDiscard();
                    }
                }
            }, title: $"Inbox ({pending.Count})", accentColor: accent);

            ImGui.TableSetColumnIndex(1);

            // RIGHT COLUMN: Package details card
            SpheneUI.Layout.SectionCard(() =>
            {
                DrawRightPanel(scale, isEmbedded, colBottom);
            }, title: "Package Details", accentColor: accent);

            ImGui.EndTable();
        }
    }

    private void DrawRightPanel(float scale, bool isEmbedded, float colBottom)
    {
        var statusHeight = 60f * scale;
        var buttonAreaHeight = ImGui.GetFrameHeight() + SpheneUI.Spacing.X16.Scaled;
        var contentHeight = colBottom - ImGui.GetCursorScreenPos().Y - statusHeight - buttonAreaHeight;

        if (contentHeight < 100f * scale) contentHeight = 100f * scale;

        if (SpheneUI.Layout.BeginScrollArea("MainContent", new Vector2(0f, contentHeight)))
        {
            if (_selectedNotification == null)
            {
                SpheneUI.Layout.Callout("Select a package on the left to review its details.",
                    FontAwesomeIcon.Inbox, ImGuiColors.DalamudGrey);
            }
            else
            {
                var senderUid = _selectedNotification.Sender?.UID ?? string.Empty;
                var senderAliasOrUid = _selectedNotification.Sender?.AliasOrUID ?? string.Empty;
                var sender = _uiSharedService.GetPreferredUserDisplayName(senderUid, senderAliasOrUid);
                var modFolderName = _selectedNotification.ModFolderName ?? string.Empty;
                var modInfo = _selectedNotification.ModInfo?.FirstOrDefault();
                var isInstalled = _shrinkuPenumbraIpc.ModExists(modFolderName);

                // Local artwork is only known once the mod exists in Penumbra —
                // then the resolved meta.json Image renders as the package icon.
                var localMeta = isInstalled ? MetaOf(modFolderName) : null;
                var thumbWrap = localMeta?.ImagePath is { } imgPath
                    ? SpheneUI.Media.Texture($"modthumb:{modFolderName}", () => ReadModImage(imgPath))
                    : null;
                var thumbSize = 64f * scale;
                if (thumbWrap != null)
                {
                    SpheneUI.Media.Image(thumbWrap, new Vector2(thumbSize), ImageFit.Cover,
                        rounding: SpheneUI.Spacing.X2, border: true);
                }
                else
                {
                    SpheneUI.Media.Placeholder(new Vector2(thumbSize), FontAwesomeIcon.BoxOpen);
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                using (ImRaii.Group())
                {
                    SpheneUI.Text.Big(string.IsNullOrWhiteSpace(modInfo?.Name ?? modFolderName)
                        ? "<Unknown>" : modInfo?.Name ?? modFolderName);
                    var subLine = ModListRows.Meta(modInfo?.Author,
                        modInfo?.Version is { } dv ? $"v{dv}" : null);
                    if (!string.IsNullOrEmpty(subLine))
                    {
                        SpheneUI.Text.Muted(subLine);
                    }
                    if (!string.IsNullOrWhiteSpace(modInfo?.Website))
                    {
                        SpheneUI.Text.Muted(modInfo.Website);
                    }
                }

                SpheneUI.Layout.VerticalSpace();
                // Sender chip: profile picture + resolved display name.
                var senderAvatarSize = 22f * scale;
                SpheneUI.Text.Muted("Sender");
                ImGui.SameLine(90f * scale);
                using (ImRaii.Group())
                {
                    var senderAvatarY = ImGui.GetCursorScreenPos().Y;
                    ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X,
                        senderAvatarY + (ImGui.GetTextLineHeight() - senderAvatarSize) * 0.5f));
                    SpheneUI.Media.Avatar(_selectedNotification.Sender == null
                        ? null : AvatarFor(_selectedNotification.Sender), senderAvatarSize);
                    ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, senderAvatarY));
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    SpheneUI.Text.Colored(string.IsNullOrWhiteSpace(sender) ? "Unknown" : sender,
                        ImGuiColors.HealerGreen);
                }
                DrawDetailRow("Mod folder", string.IsNullOrWhiteSpace(modFolderName) ? "<Unknown>" : modFolderName, ImGuiColors.DalamudYellow);

                if (!string.IsNullOrWhiteSpace(modInfo?.Description))
                {
                    SpheneUI.Layout.VerticalSpace();
                    SpheneUI.Text.MutedWrapped(modInfo.Description.Trim());
                }

                // Status badge — the same signal the inbox icons carry.
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var receivedVer0 = modInfo?.Version;
                if (!isInstalled)
                {
                    SpheneUI.Layout.Badge("NEW", ImGuiColors.TankBlue);
                    ImGui.SameLine();
                    SpheneUI.Text.Muted("Not installed in Penumbra yet.");
                }
                else if (receivedVer0 != null && _installedVersion != null)
                {
                    var comparison = CompareVersions(receivedVer0, _installedVersion);
                    if (comparison > 0)
                    {
                        SpheneUI.Layout.Badge("UPGRADE", ImGuiColors.HealerGreen);
                        ImGui.SameLine();
                        SpheneUI.Text.Muted($"Installed {_installedVersion} → Received {receivedVer0}");
                    }
                    else if (comparison < 0)
                    {
                        SpheneUI.Layout.Badge("DOWNGRADE", ImGuiColors.DalamudOrange);
                        ImGui.SameLine();
                        SpheneUI.Text.Muted($"Installed {_installedVersion} is newer than {receivedVer0}");
                    }
                    else
                    {
                        SpheneUI.Layout.Badge("INSTALLED", ImGuiColors.DalamudGrey);
                        ImGui.SameLine();
                        SpheneUI.Text.Muted($"Same version installed: {_installedVersion}");
                    }
                }
                else
                {
                    SpheneUI.Layout.Badge("INSTALLED", ImGuiColors.DalamudGrey);
                    ImGui.SameLine();
                    SpheneUI.Text.Muted("Already in Penumbra — installing overwrites it.");
                }

                if (isInstalled)
                {
                    SpheneUI.Layout.VerticalSpace();
                    SpheneUI.Layout.Callout(
                        $"Already installed as '{modFolderName}' — installing again overwrites the existing mod.",
                        FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudOrange);
                }

                SpheneUI.Layout.VerticalSpace();
                SpheneUI.Layout.Callout(
                    "Only install mods from people you fully trust — invalid or malicious mods can break your game.",
                    FontAwesomeIcon.ShieldAlt, ImGuiColors.DalamudYellow);
            }
        }
        SpheneUI.Layout.EndScrollArea(SpheneUI.Layout.NavRailBandColor());

        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace();

        // Button Area
        if (_selectedNotification != null)
        {
            var isInstalled = _shrinkuPenumbraIpc.ModExists(_selectedNotification.ModFolderName ?? string.Empty);
            var isUpgrade = false;
            
            if (isInstalled && _installedVersion != null && _selectedNotification.ModInfo?.FirstOrDefault()?.Version is { } receivedVer)
            {
                 isUpgrade = CompareVersions(receivedVer, _installedVersion) > 0;
            }

            using (ImRaii.Disabled(_isInstalling))
            {
                var btnText = isInstalled
                    ? (isUpgrade ? "Upgrade" : "Reinstall")
                    : "Download and install";

                if (SpheneUI.Controls.Button(btnText))
                {
                    _statusIsError = false;
                    _statusText = "Starting download and installation…";
                    _isInstalling = true;
                    Mediator.Publish(new InstallReceivedPenumbraModMessage(_selectedNotification!));
                }
            }

            ImGui.SameLine();
            if (SpheneUI.Controls.SmallButton("Discard"))
            {
                FileTransferNotificationDto? toDiscard;
                bool closeWindow;
                lock (_pendingLock)
                {
                    toDiscard = _selectedNotification;
                    if (toDiscard != null)
                    {
                        _pendingNotifications.Remove(toDiscard);
                        _selectedNotification = _pendingNotifications.FirstOrDefault();
                        closeWindow = _pendingNotifications.Count == 0;
                    }
                    else
                    {
                        closeWindow = false;
                    }
                }

                if (toDiscard != null)
                {
                    Mediator.Publish(new PenumbraModTransferDiscardedMessage(toDiscard));
                    if (!string.IsNullOrEmpty(toDiscard.Hash) && !string.IsNullOrEmpty(toDiscard.Sender?.UID))
                    {
                        Mediator.Publish(new FileTransferAckMessage(toDiscard.Hash, toDiscard.Sender.UID));
                    }
                }

                if (closeWindow)
                {
                    IsOpen = false;
                }
            }

            if (!isEmbedded)
            {
                ImGui.SameLine();
                if (SpheneUI.Controls.SmallButton("Close"))
                {
                    IsOpen = false;
                }
            }
        }
        else
        {
            if (!isEmbedded && SpheneUI.Controls.SmallButton("Close"))
            {
                IsOpen = false;
            }
        }

        // Status Area — batch progress and the latest transfer message.
        using (ImRaii.Child("Status", new Vector2(0f, statusHeight), false))
        {
            if (_progress.HasValue || !string.IsNullOrWhiteSpace(_statusText))
            {
                SpheneUI.Text.Muted("Status");
            }
            if (_progress.HasValue)
            {
                var clamped = Math.Clamp(_progress.Value, 0f, 1f);
                var barWidth = ImGui.GetContentRegionAvail().X;
                var cursor = ImGui.GetCursorPos();
                var barSize = new Vector2(barWidth, 20f * scale);
                SpheneUI.Controls.ProgressBar(clamped, barSize);
                ImGui.SetCursorPos(cursor + new Vector2(0f, barSize.Y + 4f * scale));
            }

            if (!string.IsNullOrWhiteSpace(_statusText))
            {
                var color = _statusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
                SpheneUI.Text.Colored(_statusText, color);
            }
        }
    }

    private static void DrawDetailRow(string label, string value, Vector4? valueColor = null)
    {
        var labelWidth = 90f * ImGuiHelpers.GlobalScale;
        SpheneUI.Text.Muted(label);
        ImGui.SameLine(labelWidth);
        if (valueColor.HasValue)
        {
            SpheneUI.Text.Colored(value, valueColor.Value);
        }
        else
        {
            SpheneUI.Text.Plain(value);
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
            f => Task.Run(() => _shrinkuPenumbraIpc.GetModMetadataAsync(f)));
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

    private static int CompareVersions(string v1, string v2)
    {
        // Try standard version parsing first (e.g. 1.0.0.0)
        if (Version.TryParse(v1, out var ver1) && Version.TryParse(v2, out var ver2))
        {
            return ver1.CompareTo(ver2);
        }

        // Try to strip 'v' prefix if present
        var v1Clean = v1?.TrimStart('v', 'V');
        var v2Clean = v2?.TrimStart('v', 'V');

        if (Version.TryParse(v1Clean, out var ver1Clean) && Version.TryParse(v2Clean, out var ver2Clean))
        {
            return ver1Clean.CompareTo(ver2Clean);
        }

        // Fallback to string comparison
        return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
    }
}
