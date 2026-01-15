using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Files;
using Sphene.Services;
using Sphene.Services.Mediator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sphene.WebAPI.Files;
using Sphene.API.Routes;
using System.Text.Json;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.UI.Panels;

public class PenumbraReceiveModUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private readonly ShrinkU.Services.PenumbraIpc _shrinkuPenumbraIpc;

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

    // History Tab State


    public PenumbraReceiveModUi(
        ILogger<PenumbraReceiveModUi> logger,
        SpheneMediator mediator,
        UiSharedService uiSharedService,
        PerformanceCollectorService performanceCollectorService,
        ShrinkU.Services.PenumbraIpc shrinkuPenumbraIpc)
        : base(logger, mediator, "Receive Mods###SpheneReceiveMods", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _shrinkuPenumbraIpc = shrinkuPenumbraIpc;

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
        if (ImGui.BeginTabBar("PenumbraReceiveModTabs"))
        {
            if (ImGui.BeginTabItem("Pending"))
            {
                DrawPendingTab(isEmbedded: false);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    internal void DrawEmbedded()
    {
        using (ImRaii.Child("ReceiveModsRootEmbedded", new Vector2(0f, 0f), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawPendingTab(isEmbedded: true);
        }
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

        if (ImGui.BeginTable("MainLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("ModList", ImGuiTableColumnFlags.WidthFixed, 250f * scale);
            ImGui.TableSetupColumn("ModDetails", ImGuiTableColumnFlags.WidthStretch);
            
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            // LEFT COLUMN: List
            var buttonHeight = ImGui.GetFrameHeightWithSpacing();
            var listHeight = -(buttonHeight * 2f + (ImGui.GetStyle().ItemSpacing.Y * 2f));

            using (ImRaii.Child("LeftList", new Vector2(0f, listHeight), false))
            {
                UiSharedService.ColorText($"Received Mods ({displayed.Count})", ImGuiColors.DalamudWhite);
                ImGui.Separator();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##pendingFilter", "Filter mods or sender…", ref _pendingFilter, 64);
                ImGui.Spacing();

                if (_pendingNotifications.Count == 0)
                {
                    ImGui.TextDisabled("No mods pending.");
                }
                else if (displayed.Count == 0)
                {
                    ImGui.TextDisabled("No matches.");
                }
                else
                {
                    void DrawNotificationEntry(FileTransferNotificationDto notif)
                    {
                        using var id = ImRaii.PushId(notif.Hash ?? string.Empty);

                        bool isChecked;
                        lock (_pendingLock)
                        {
                            isChecked = _selectedForBatch.Contains(notif);
                        }
                        if (ImGui.Checkbox("##check", ref isChecked))
                        {
                            lock (_pendingLock)
                            {
                                if (isChecked) _selectedForBatch.Add(notif);
                                else _selectedForBatch.Remove(notif);
                            }
                        }
                        ImGui.SameLine();

                        var name = notif.ModFolderName ?? "Unknown";

                        Vector4 color;
                        var icon = FontAwesomeIcon.Plus;
                        var tooltip = string.Empty;

                        if (_modStatusCache.TryGetValue(name, out var status) && status.IsInstalled)
                        {
                            var receivedVer = notif.ModInfo?.FirstOrDefault()?.Version;
                            if (receivedVer != null && status.Version != null)
                            {
                                var comparison = CompareVersions(receivedVer, status.Version);
                                if (comparison > 0)
                                {
                                    color = ImGuiColors.DalamudYellow;
                                    icon = FontAwesomeIcon.ArrowCircleUp;
                                    tooltip = $"Upgrade available: {status.Version} -> {receivedVer}";
                                }
                                else if (comparison < 0)
                                {
                                    color = ImGuiColors.DalamudOrange;
                                    icon = FontAwesomeIcon.ArrowCircleDown;
                                    tooltip = $"Downgrade warning: {status.Version} -> {receivedVer}";
                                }
                                else
                                {
                                    color = ImGuiColors.HealerGreen;
                                    icon = FontAwesomeIcon.Check;
                                    tooltip = $"Same version installed: {status.Version}";
                                }
                            }
                            else
                            {
                                color = ImGuiColors.HealerGreen;
                                icon = FontAwesomeIcon.Check;
                                tooltip = "Mod is already installed";
                            }
                        }
                        else
                        {
                            color = ImGuiColors.TankBlue;
                            tooltip = "New mod";
                        }

                        using (ImRaii.PushColor(ImGuiCol.Text, color))
                        {
                            using (_uiSharedService.IconFont.Push())
                            {
                                ImGui.TextUnformatted(icon.ToIconString());
                            }
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip(tooltip);
                            }
                            ImGui.SameLine();

                            var isSelected = currentSelected == notif;
                            if (ImGui.Selectable($"{name}##select", isSelected))
                            {
                                lock (_pendingLock)
                                {
                                    _selectedNotification = notif;
                                }
                            }
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip(tooltip);
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

            ImGui.Separator();

            var hasPending = pending.Count > 0;
            using (ImRaii.Disabled(_isInstalling || !hasPending))
            {
                if (ImGui.Button("Select All"))
                {
                    SelectAll();
                }
                ImGui.SameLine();
                if (ImGui.Button("Select Installed"))
                {
                    SelectInstalled();
                }
                ImGui.SameLine();
                if (ImGui.Button("Select New"))
                {
                    SelectNew();
                }
            }

            var selectedCount = _selectedForBatch.Count;
            using (ImRaii.Disabled(_isInstalling))
            {
                using (ImRaii.Disabled(selectedCount == 0))
                {
                    if (ImGui.Button($"Install Selected ({selectedCount})"))
                    {
                        StartBatchInstall();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"Discard Selected ({selectedCount})"))
                    {
                        BatchDiscard();
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!hasPending))
                {
                    if (ImGui.Button($"Discard All ({pending.Count})"))
                    {
                        DiscardAll();
                    }
                }
            }

            ImGui.TableSetColumnIndex(1);

            // RIGHT COLUMN: Details
            using (ImRaii.Child("RightDetails", new Vector2(0f, 0f), false))
            {
                DrawRightPanel(scale, isEmbedded);
            }

            ImGui.EndTable();
        }
    }

    private void DrawRightPanel(float scale, bool isEmbedded)
    {
        var statusHeight = 90f * scale;
        var buttonAreaHeight = ImGui.GetFrameHeight() + (20f * scale); 
        var contentHeight = ImGui.GetContentRegionAvail().Y - statusHeight - buttonAreaHeight;
        
        if (contentHeight < 100f * scale) contentHeight = 100f * scale;

        using (ImRaii.Child("MainContent", new Vector2(0f, contentHeight), false))
        {
            if (_selectedNotification == null)
            {
                UiSharedService.ColorText("No mod selected.", ImGuiColors.DalamudRed);
            }
            else
            {
                var senderUid = _selectedNotification.Sender?.UID ?? string.Empty;
                var senderAliasOrUid = _selectedNotification.Sender?.AliasOrUID ?? string.Empty;
                var sender = _uiSharedService.GetPreferredUserDisplayName(senderUid, senderAliasOrUid);
                var modFolderName = _selectedNotification.ModFolderName ?? string.Empty;
                var modInfo = _selectedNotification.ModInfo?.FirstOrDefault();

                UiSharedService.ColorText("Mod Details", ImGuiColors.DalamudWhite);
                ImGui.Separator();
                ImGui.Spacing();

                var detailsHeight = modInfo != null ? 150f * scale : 80f * scale;
                using (ImRaii.Child("Details", new Vector2(0f, detailsHeight), true))
                {
                    ImGui.TextUnformatted("Sender");
                    ImGui.SameLine(140f * scale);
                    ImGui.TextColored(ImGuiColors.HealerGreen, string.IsNullOrWhiteSpace(sender) ? "Unknown" : sender);

                    var name = modInfo?.Name ?? modFolderName;
                    ImGui.TextUnformatted("Mod");
                    ImGui.SameLine(140f * scale);
                    ImGui.TextColored(ImGuiColors.DalamudYellow, string.IsNullOrWhiteSpace(name) ? "<Unknown>" : name);

                    if (modInfo != null)
                    {
                        if (!string.IsNullOrWhiteSpace(modInfo.Author))
                        {
                            ImGui.TextUnformatted("Author");
                            ImGui.SameLine(140f * scale);
                            ImGui.TextUnformatted(modInfo.Author);
                        }
                        if (!string.IsNullOrWhiteSpace(modInfo.Version))
                        {
                            ImGui.TextUnformatted("Version");
                            ImGui.SameLine(140f * scale);
                            ImGui.TextUnformatted(modInfo.Version);
                        }
                        if (!string.IsNullOrWhiteSpace(modInfo.Website))
                        {
                            ImGui.TextUnformatted("Website");
                            ImGui.SameLine(140f * scale);
                            ImGui.TextUnformatted(modInfo.Website);
                        }
                    }
                }

                ImGui.Spacing();

                var isInstalled = _shrinkuPenumbraIpc.ModExists(modFolderName);

                if (isInstalled)
                {
                    ImGui.Spacing();
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
                    {
                        UiSharedService.TextWrapped($"This mod is already installed in Penumbra ('{modFolderName}'). Installing it again will overwrite the existing mod.");
                    }

                    if (_installedVersion != null && modInfo?.Version != null)
                    {
                        var installedVer = _installedVersion;
                        var receivedVer = modInfo.Version;
                        var comparison = CompareVersions(receivedVer, installedVer);

                        ImGui.Spacing();
                        if (comparison > 0)
                        {
                            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
                            {
                                UiSharedService.TextWrapped($"Upgrade available! Installed: {installedVer} → Received: {receivedVer}");
                            }
                        }
                        else if (comparison < 0)
                        {
                            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
                            {
                                UiSharedService.TextWrapped($"Warning: The received version ({receivedVer}) is older than the installed version ({installedVer}).");
                            }
                        }
                        else
                        {
                            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                            {
                                UiSharedService.TextWrapped($"Same version installed: {installedVer}");
                            }
                        }
                    }
                }

                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
                {
                    UiSharedService.TextWrapped("Only install mods from people you fully trust. Invalid or malicious mods can break your game or cause unexpected issues.");
                }

                ImGui.Spacing();
                UiSharedService.TextWrapped("Do you want to download and install this Penumbra mod package now?");
            }
        }

        ImGui.Separator();
        ImGui.Spacing();

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

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Download, btnText))
                {
                    _statusIsError = false;
                    _statusText = "Starting download and installation…";
                    _isInstalling = true;
                    Mediator.Publish(new InstallReceivedPenumbraModMessage(_selectedNotification!));
                }
            }

            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Discard"))
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
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.TimesCircle, "Close"))
                {
                    IsOpen = false;
                }
            }
        }
        else
        {
            if (!isEmbedded && _uiSharedService.IconTextButton(FontAwesomeIcon.TimesCircle, "Close"))
            {
                IsOpen = false;
            }
        }

        // Status Area
        using (ImRaii.Child("Status", new Vector2(0f, statusHeight), false))
        {
            UiSharedService.ColorText("Status", ImGuiColors.DalamudWhite);

            if (_progress.HasValue)
            {
                var clamped = Math.Clamp(_progress.Value, 0f, 1f);
                var barWidth = ImGui.GetContentRegionAvail().X;
                var cursor = ImGui.GetCursorPos();
                var barSize = new Vector2(barWidth, 20f * scale);
                ImGui.ProgressBar(clamped, barSize);
                ImGui.SetCursorPos(cursor + new Vector2(0f, barSize.Y + 4f * scale));
            }

            if (!string.IsNullOrWhiteSpace(_statusText))
            {
                var color = _statusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
                UiSharedService.ColorText(_statusText, color);
            }
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
