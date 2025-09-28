using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using ObjectKind = Sphene.API.Data.Enum.ObjectKind;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration;
using Sphene.PlayerData.Handlers;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Components;
using Sphene.UI.Handlers;
using Sphene.UI.Styling;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using Sphene.WebAPI.Files.Models;
using Sphene.WebAPI.SignalR.Utils;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Dalamud.Interface.Textures.TextureWraps;

namespace Sphene.UI;

public class CompactUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, Dictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly DrawEntityFactory _drawEntityFactory;
    private readonly FileUploadManager _fileTransferManager;
    private readonly PairManager _pairManager;
    private readonly SelectTagForPairUi _selectGroupForPairUi;
    private readonly SelectPairForTagUi _selectPairsForGroupUi;
    private readonly IpcManager _ipcManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly TopTabMenu _tabMenu;
    private readonly TagHandler _tagHandler;
    private readonly UiSharedService _uiSharedService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly TextureBackupService _textureBackupService;
    private List<IDrawFolder> _drawFolders;
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private Vector2 _lastPosition = Vector2.One;
    private bool _isIncognitoModeActive = false;
    private DateTime _lastIncognitoButtonClick = DateTime.MinValue;
    private readonly HashSet<string> _prePausedPairs = new();
    private readonly HashSet<string> _prePausedSyncshells = new();
    private DateTime _lastReconnectButtonClick = DateTime.MinValue;
    private Vector2 _lastSize = Vector2.One;
    private int _secretKeyIdx = -1;
    private bool _showModalForUserAddition;
    private float _transferPartHeight;
    private bool _wasOpen;
    private float _windowContentWidth;
    
    // Texture conversion fields
    private bool _showConversionPopup = false;
    private bool _showProgressPopup = false;
    private Dictionary<string, string[]> _texturesToConvert = new();
    private Task? _conversionTask;
    private CancellationTokenSource _conversionCancellationTokenSource = new();
    private Progress<(string fileName, int progress)> _conversionProgress = new();
    private int _conversionCurrentFileProgress = 0;
    private string _conversionCurrentFileName = string.Empty;
    private bool _enableBackupBeforeConversion = true;
    private DateTime _conversionStartTime = DateTime.MinValue;
    
    // Backup restore fields
    private Dictionary<string, List<string>>? _cachedBackupsForAnalysis;
    private DateTime _lastBackupAnalysisUpdate = DateTime.MinValue;
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysisForPopup;
    private Task? _restoreTask;
    private CancellationTokenSource _restoreCancellationTokenSource = new();
    private Progress<(string fileName, int current, int total)> _restoreProgress = new();

    public CompactUi(ILogger<CompactUi> logger, UiSharedService uiShared, SpheneConfigService configService, ApiController apiController, PairManager pairManager,
        ServerConfigurationManager serverManager, SpheneMediator mediator, FileUploadManager fileTransferManager,
        TagHandler tagHandler, DrawEntityFactory drawEntityFactory, SelectTagForPairUi selectTagForPairUi, SelectPairForTagUi selectPairForTagUi,
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, DalamudUtilService dalamudUtilService, CharacterAnalyzer characterAnalyzer,
        TextureBackupService textureBackupService)
        : base(logger, mediator, "###SpheneMainUI", performanceCollectorService)
    {
        _uiSharedService = uiShared;
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _fileTransferManager = fileTransferManager;
        _tagHandler = tagHandler;
        _drawEntityFactory = drawEntityFactory;
        _selectGroupForPairUi = selectTagForPairUi;
        _selectPairsForGroupUi = selectPairForTagUi;
        _ipcManager = ipcManager;
        _dalamudUtilService = dalamudUtilService;
        _characterAnalyzer = characterAnalyzer;
        _textureBackupService = textureBackupService;
        
        // Setup conversion progress handler
        _conversionProgress.ProgressChanged += (sender, progress) =>
        {
            _conversionCurrentFileProgress = progress.progress;
            _conversionCurrentFileName = progress.fileName;
        };
        _tabMenu = new TopTabMenu(Mediator, _apiController, _pairManager, _uiSharedService);

        // Initialize incognito mode state from configuration
        _isIncognitoModeActive = _configService.Current.IsIncognitoModeActive;
        _prePausedPairs = new HashSet<string>(_configService.Current.PrePausedPairs);
        _prePausedSyncshells = new HashSet<string>(_configService.Current.PrePausedSyncshells);

        AllowPinning = false;
        AllowClickthrough = false;
        TitleBarButtons = new()
        {
            new TitleBarButton()
            {
                Icon = FontAwesomeIcon.Cog,
                Click = (msg) =>
                {
                    Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
                },
                IconOffset = new(2,1),
                ShowTooltip = () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Open Network Configuration");
                    ImGui.EndTooltip();
                }
            },
            new TitleBarButton()
            {
                Icon = FontAwesomeIcon.Book,
                Click = (msg) =>
                {
                    Mediator.Publish(new UiToggleMessage(typeof(EventViewerUI)));
                },
                IconOffset = new(2,1),
                ShowTooltip = () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Open Network Event Log");
                    ImGui.EndTooltip();
                }
            }
        };

        RefreshDrawFolders();

#if DEBUG
        string dev = "Dev Build";
        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        WindowName = $"Sphene {dev} ({ver.Major}.{ver.Minor}.{ver.Build})###SpheneMainUI";
        Toggle();
#else
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        WindowName = "Sphene " + ver.Major + "." + ver.Minor + "." + ver.Build + "###SpheneMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        Mediator.Subscribe<RefreshUiMessage>(this, (msg) => RefreshIconsOnly());
        Mediator.Subscribe<StructuralRefreshUiMessage>(this, (msg) => RefreshDrawFolders());
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) => _lastBackupAnalysisUpdate = DateTime.MinValue);

        // Make window practically invisible - no decoration, no background, no borders
        Flags |= ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse 
               | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus;

        UpdateSizeConstraints();
        
        // Subscribe to configuration changes to update size constraints when width lock changes
        _configService.ConfigSave += OnConfigurationChanged;
    }

    private void UpdateSizeConstraints()
    {
        if (_configService.Current.IsWidthLocked)
        {
            // When width is locked, set both min and max width to the locked value
            SizeConstraints = new WindowSizeConstraints()
            {
                MinimumSize = new Vector2(_configService.Current.LockedWidth, 400),
                MaximumSize = new Vector2(_configService.Current.LockedWidth, 2000),
            };
        }
        else
        {
            // Normal size constraints when width is not locked
            SizeConstraints = new WindowSizeConstraints()
            {
                MinimumSize = new Vector2(370, 400),
                MaximumSize = new Vector2(800, 2000),
            };
        }
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        UpdateSizeConstraints();
    }

    // Removed ConvertSvgBase64ToPng method - no longer needed

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unsubscribe from configuration changes
            _configService.ConfigSave -= OnConfigurationChanged;
            
            // Dispose all draw folders to clean up event subscriptions
            if (_drawFolders != null)
            {
                foreach (var folder in _drawFolders)
                {
                    folder.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }

    public override void PreDraw()
    {
        // Apply any pending window resize before the window is drawn
        SpheneUIEnhancements.ApplyPendingWindowResize(GetControlPanelTitle());
        base.PreDraw();
    }

    protected override void DrawInternal()
    {
        
        _windowContentWidth = UiSharedService.GetBaseFolderWidth(); // Use consistent width calculation
        if (!_apiController.IsCurrentVersion)
        {
            var ver = _apiController.CurrentClientVersion;
            var unsupported = "UNSUPPORTED VERSION";
            using (_uiSharedService.UidFont.Push())
            {
                var uidTextSize = ImGui.CalcTextSize(unsupported);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(ImGuiColors.DalamudRed, unsupported);
            }
            UiSharedService.ColorTextWrapped($"Your Network client is outdated, current version is {ver.Major}.{ver.Minor}.{ver.Build}. " +
            $"Please update your client to maintain Network compatibility. Open /xlplugins and update the plugin.", ImGuiColors.DalamudRed);
        }

        if (!_ipcManager.Initialized)
        {
            var unsupported = "MISSING CORE COMPONENTS";

            using (_uiSharedService.UidFont.Push())
            {
                var uidTextSize = ImGui.CalcTextSize(unsupported);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(ImGuiColors.DalamudRed, unsupported);
            }
            var penumAvailable = _ipcManager.Penumbra.APIAvailable;
            var glamAvailable = _ipcManager.Glamourer.APIAvailable;

            UiSharedService.ColorTextWrapped($"One or more components essential for Network operation are unavailable. Enable or update the following:", ImGuiColors.DalamudRed);
            using var indent = ImRaii.PushIndent(10f);
            if (!penumAvailable)
            {
                UiSharedService.TextWrapped("Penumbra");
                _uiSharedService.BooleanToColoredIcon(penumAvailable, true);
            }
            if (!glamAvailable)
            {
                UiSharedService.TextWrapped("Glamourer");
                _uiSharedService.BooleanToColoredIcon(glamAvailable, true);
            }
            ImGui.Separator();
        }

        // Single unified card layout with integrated controls and header buttons
#if DEBUG
        // Use reddish background for debug builds
        var debugHeaderBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.2f, 0.2f, 0.6f)); // Reddish with transparency
#endif
        SpheneUIEnhancements.DrawSpheneCard(GetControlPanelTitle(), () => {
            // Network Identity Section
            ImGui.TextColored(SpheneColors.SpheneGold, "Regulator ID");
            ImGui.Separator();
            DrawUIDContent();
            
            ImGui.Spacing();
            ImGui.Spacing();
            
            // Server Status Section
            ImGui.TextColored(SpheneColors.SpheneGold, "Server & Character Status");
            ImGui.Separator();
            DrawServerStatusContent();
            
            ImGui.Spacing();
            ImGui.Spacing();
            

            
            if (_apiController.ServerState is ServerState.Connected)
            {
                ImGui.Spacing();
                ImGui.Spacing();
                
                // Navigation Section
                ImGui.TextColored(SpheneColors.SpheneGold, "Navigation");
                ImGui.Separator();
                _tabMenu.Draw();
                
                ImGui.Spacing();
                ImGui.Spacing();
                
                // Connected Pairs Section
                var onlineCount = _pairManager.DirectPairs.Count(p => p.IsOnline);
                var totalCount = _pairManager.DirectPairs.Count;
                var onlineText = $"({onlineCount} / {totalCount}) online";
                var onlineTextSize = ImGui.CalcTextSize(onlineText);
                
                ImGui.TextColored(SpheneColors.SpheneGold, "Connected Pairs");
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - onlineTextSize.X);
                ImGui.TextColored(SpheneColors.SpheneGold, onlineText);
                ImGui.Separator();
                using (ImRaii.PushId("pairlist")) DrawPairs();
                

                float pairlistEnd = ImGui.GetCursorPosY();
                using (ImRaii.PushId("transfers")) DrawTransfers();
                _transferPartHeight = ImGui.GetCursorPosY() - pairlistEnd - ImGui.GetTextLineHeight();
                using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
                using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
            }
        }, false, null, () => {
            // Settings button
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 3.0f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 12.0f);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Cog))
            {
                Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Open Network Configuration");
                ImGui.EndTooltip();
            }
            
            // Close button
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 0.0f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 5.0f);
            if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
            {
                IsOpen = false;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Close Sphene");
                ImGui.EndTooltip();
            }
        }, true, SizeConstraints?.MinimumSize, SizeConstraints?.MaximumSize,
#if DEBUG
        debugHeaderBg,
#else
        0,
#endif
        _configService.Current.IsWidthLocked, // isWidthLocked
         () => {
             _configService.Current.IsWidthLocked = !_configService.Current.IsWidthLocked;
             if (_configService.Current.IsWidthLocked)
             {
                 // Store current CompactUI window width when locking - use _lastSize for consistency
                 _configService.Current.LockedWidth = _lastSize.X;
             }
             _configService.Save();
             UpdateSizeConstraints();
         }, // onLockToggle
         _configService.Current.IsWidthLocked ? "Unlock window width" : "Lock window width" // lockTooltip
        );

        if (_configService.Current.OpenPopupOnAdd && _pairManager.LastAddedUser != null)
        {
            _lastAddedUser = _pairManager.LastAddedUser;
            _pairManager.LastAddedUser = null;
            ImGui.OpenPopup("Set Notes for New User");
            _showModalForUserAddition = true;
            _lastAddedUserComment = string.Empty;
        }

        if (ImGui.BeginPopupModal("Set Notes for New User", ref _showModalForUserAddition, UiSharedService.PopupWindowFlags))
        {
            if (_lastAddedUser == null)
            {
                _showModalForUserAddition = false;
            }
            else
            {
                UiSharedService.TextWrapped($"You have successfully added {_lastAddedUser.UserData.AliasOrUID}. Set a local note for the user in the field below:");
                ImGui.InputTextWithHint("##noteforuser", $"Note for {_lastAddedUser.UserData.AliasOrUID}", ref _lastAddedUserComment, 100);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save Note"))
                {
                    _serverManager.SetNoteForUid(_lastAddedUser.UserData.UID, _lastAddedUserComment);
                    _lastAddedUser = null;
                    _lastAddedUserComment = string.Empty;
                    _showModalForUserAddition = false;
                }
            }
            UiSharedService.SetScaledWindowSize(275);
            ImGui.EndPopup();
        }

        // Texture Conversion Popup
        DrawTextureConversionPopup();

        // Track window size changes for mediator notifications
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (_lastSize != size || _lastPosition != pos)
        {
            _lastSize = size;
            _lastPosition = pos;
            
            // If width lock is enabled and width has changed, update the locked width
            if (_configService.Current.IsWidthLocked && Math.Abs(_lastSize.X - _configService.Current.LockedWidth) > 1f)
            {
                _configService.Current.LockedWidth = _lastSize.X;
                _configService.Save();
                UpdateSizeConstraints();
            }
            
            Mediator.Publish(new CompactUiChange(_lastSize, _lastPosition));
        }
    }

    private void DrawPairs()
    {
        var ySize = _transferPartHeight == 0
            ? 1
            : (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y
                + ImGui.GetTextLineHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetStyle().WindowBorderSize) - _transferPartHeight - ImGui.GetCursorPosY() - 15; // add some Space

        ImGui.BeginChild("list", new Vector2(_windowContentWidth, ySize), border: false, ImGuiWindowFlags.NoScrollbar);

        foreach (var item in _drawFolders)
        {
            item.Draw();
        }

        ImGui.EndChild();
    }
    private void DrawServerStatus()
    {
        DrawServerStatusContent();
    }

    private void DrawServerStatusContent()
    {
        // Draw Sphene-themed status indicator
        var connectionStatus = _apiController.ServerState == ServerState.Connected ? "Connected" : "Disconnected";
        var statusColor = _apiController.ServerState == ServerState.Connected 
            ? SpheneColors.CrystalBlue 
            : SpheneColors.NetworkDisconnected;
        
        // Draw Sphene-themed status indicator with proper alignment
        SpheneUIEnhancements.DrawSpheneStatusIndicator(connectionStatus, _apiController.ServerState == ServerState.Connected);
        
        // Always show reconnect button with proper alignment
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        
        var reconnectCurrentTime = DateTime.Now;
        var reconnectTimeSinceLastClick = reconnectCurrentTime - _lastReconnectButtonClick;
        var isReconnectButtonDisabled = reconnectTimeSinceLastClick.TotalSeconds < 5.0;
        
        var reconnectColor = isReconnectButtonDisabled ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudWhite;
        ImGui.PushStyleColor(ImGuiCol.Text, reconnectColor);
        ImGui.PushFont(UiBuilder.IconFont);
        
        using (ImRaii.Disabled(isReconnectButtonDisabled))
        {
            if (ImGui.Button(FontAwesomeIcon.Redo.ToIconString(), new Vector2(22, 22)))
            {
                _lastReconnectButtonClick = reconnectCurrentTime;
                _ = Task.Run(() => _apiController.CreateConnectionsAsync());
            }
        }
        
        ImGui.PopFont();
        ImGui.PopStyleColor();
        
        var reconnectTooltipText = "Reconnect to the Sphene Network";
        if (isReconnectButtonDisabled)
        {
            var reconnectRemainingSeconds = Math.Ceiling(5.0 - reconnectTimeSinceLastClick.TotalSeconds);
            reconnectTooltipText += $"\nCooldown: {reconnectRemainingSeconds} seconds remaining";
        }
        UiSharedService.AttachToolTip(reconnectTooltipText);
        
        // Incognito Mode button next to reconnect
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        
        var currentTime = DateTime.Now;
        var timeSinceLastClick = currentTime - _lastIncognitoButtonClick;
        var isButtonDisabled = timeSinceLastClick.TotalSeconds < 5.0;
        
        if (_isIncognitoModeActive)
        {
            // Resume mode - use play icon
            var resumeIcon = FontAwesomeIcon.Play;
            var resumeColor = isButtonDisabled ? ImGuiColors.DalamudGrey : ImGuiColors.ParsedGreen;
            ImGui.PushStyleColor(ImGuiCol.Text, resumeColor);
            ImGui.PushFont(UiBuilder.IconFont);
            
            using (ImRaii.Disabled(isButtonDisabled))
            {
                if (ImGui.Button(resumeIcon.ToIconString(), new Vector2(22, 22)))
                {
                    _lastIncognitoButtonClick = currentTime;
                    _ = Task.Run(() => HandleIncognitoModeToggle());
                }
            }
            
            ImGui.PopFont();
            ImGui.PopStyleColor();
        }
        else
        {
            // Incognito Mode - use red heart icon
            var incognitoIcon = FontAwesomeIcon.Heart;
            var incognitoColor = isButtonDisabled ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudRed;
            ImGui.PushStyleColor(ImGuiCol.Text, incognitoColor);
            ImGui.PushFont(UiBuilder.IconFont);
            
            using (ImRaii.Disabled(isButtonDisabled))
            {
                if (ImGui.Button(incognitoIcon.ToIconString(), new Vector2(22, 22)))
                {
                    _lastIncognitoButtonClick = currentTime;
                    _ = Task.Run(() => HandleIncognitoModeToggle());
                }
            }
            
            ImGui.PopFont();
            ImGui.PopStyleColor();
        }
        var tooltipText = _isIncognitoModeActive ? "Resume - Unpause all pairs and reconnect syncshells" : "Incognito Mode - Pause all pairs and syncshells except party members";
        
        if (isButtonDisabled)
        {
            var remainingSeconds = Math.Ceiling(5.0 - timeSinceLastClick.TotalSeconds);
            tooltipText += $"\nCooldown: {remainingSeconds} seconds remaining";
        }
        
        UiSharedService.AttachToolTip(tooltipText);
        
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(10, 0));
        
        // One-click Texture Conversion Button
        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.FileArchive, 22f))
        {
            _showConversionPopup = true;
        }
        UiSharedService.AttachToolTip("One-click texture conversion\nConvert all non-BC7 textures to reduce size");
        
        // Compact Texture Size indicator
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        DrawCompactTextureIndicator();
        
        if (_apiController.ServerState == ServerState.Connected)
        {
            var userCount = _apiController.OnlineUsers;
            var shardInfo = _apiController.ServerInfo?.ShardName ?? "Unknown";
            
            // ImGui.Text($"Users Online: {userCount}");
            // ImGui.Text($"Shard: {shardInfo}");
        }
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.CurrentUploads.ToList();
        var currentDownloads = _currentDownloads.SelectMany(d => d.Value.Values).ToList();
        
        // Only show upload progress if there are active uploads
        if (currentUploads.Any())
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Upload);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);
            
            var totalUploads = currentUploads.Count;
            var doneUploads = currentUploads.Count(c => c.IsTransferred);
            var totalUploaded = currentUploads.Sum(c => c.Transferred);
            var totalToUpload = currentUploads.Sum(c => c.Total);
            
            var uploadProgress = totalToUpload > 0 ? (float)totalUploaded / totalToUpload : 0f;
            var uploadText = $"{doneUploads}/{totalUploads} ({UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)})";
            
            SpheneUIEnhancements.DrawSpheneProgressBar("Transmitting", uploadProgress, uploadText, SpheneColors.TransmissionActive);
        }

        // Only show download progress if there are active downloads
        if (currentDownloads.Any())
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Download);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);
            
            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);
            
            var downloadProgress = totalToDownload > 0 ? (float)totalDownloaded / totalToDownload : 0f;
            var downloadText = $"{doneDownloads}/{totalDownloads} ({UiSharedService.ByteToString(totalDownloaded)}/{UiSharedService.ByteToString(totalToDownload)})";
            
            SpheneUIEnhancements.DrawSpheneProgressBar("Receiving", downloadProgress, downloadText, SpheneColors.ReceptionActive);
        }
    }

    private void DrawUIDHeader()
    {
        DrawUIDContent();
    }

    private void DrawUIDContent()
    {
        var uidText = GetUidText();
        
        using (_uiSharedService.UidFont.Push())
        {
            var uidTextSize = ImGui.CalcTextSize(uidText);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (uidTextSize.X / 2));
            ImGui.TextColored(GetUidColor(), uidText);
        }

        if (_apiController.ServerState is ServerState.Connected)
        {
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(_apiController.DisplayName);
            }
            UiSharedService.AttachToolTip("Click to copy");

            if (!string.Equals(_apiController.DisplayName, _apiController.UID, StringComparison.Ordinal))
            {
                var origTextSize = ImGui.CalcTextSize(_apiController.UID);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (origTextSize.X / 2));
                ImGui.TextColored(GetUidColor(), _apiController.UID);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(_apiController.UID);
                }
                UiSharedService.AttachToolTip("Click to copy");
            }
        }
        else
        {
            UiSharedService.ColorTextWrapped(GetServerError(), GetUidColor());
        }
    }

    private IEnumerable<IDrawFolder> GetDrawFolders()
    {
        List<IDrawFolder> drawFolders = [];

        var allPairs = _pairManager.PairsWithGroups
            .ToDictionary(k => k.Key, k => k.Value);
        var filteredPairs = allPairs
            .Where(p =>
            {
                if (_tabMenu.Filter.IsNullOrEmpty()) return true;
                return p.Key.UserData.AliasOrUID.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ||
                       (p.Key.GetNote()?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (p.Key.PlayerName?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToDictionary(k => k.Key, k => k.Value);

        string? AlphabeticalSort(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (_configService.Current.ShowCharacterNameInsteadOfNotesForVisible && !string.IsNullOrEmpty(u.Key.PlayerName)
                    ? (_configService.Current.PreferNotesOverNamesForVisible ? u.Key.GetNote() : u.Key.PlayerName)
                    : (u.Key.GetNote() ?? u.Key.UserData.AliasOrUID));
        bool FilterOnlineOrPausedSelf(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (u.Key.IsOnline || (!u.Key.IsOnline && !_configService.Current.ShowOfflineUsersSeparately))
                && !u.Key.UserPair.OwnPermissions.IsPaused()
                && (!_configService.Current.ShowVisibleUsersSeparately || !u.Key.IsVisible);
        bool FilterPausedUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.UserPair.OwnPermissions.IsPaused();
        Dictionary<Pair, List<GroupFullInfoDto>> BasicSortedDictionary(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.OrderByDescending(u => u.Key.IsVisible)
                .ThenByDescending(u => u.Key.IsOnline)
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(u => u.Key, u => u.Value);
        ImmutableList<Pair> ImmutablePairList(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.Select(k => k.Key).ToImmutableList();
        bool FilterVisibleUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsVisible
                && (_configService.Current.ShowSyncshellUsersInVisible || !(!_configService.Current.ShowSyncshellUsersInVisible && !u.Key.IsDirectlyPaired));
        bool FilterTagusers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, string tag)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && _tagHandler.HasTag(u.Key.UserData.UID, tag);
        bool FilterGroupUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, GroupFullInfoDto group)
            => u.Value.Exists(g => string.Equals(g.GID, group.GID, StringComparison.Ordinal));
        bool FilterNotTaggedUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && !_tagHandler.HasAnyTag(u.Key.UserData.UID);
        bool FilterOfflineUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => ((u.Key.IsDirectlyPaired && _configService.Current.ShowSyncshellOfflineUsersSeparately)
                || !_configService.Current.ShowSyncshellOfflineUsersSeparately)
                && (!u.Key.IsOneSidedPair || u.Value.Any()) && !u.Key.IsOnline && !u.Key.UserPair.OwnPermissions.IsPaused();
        bool FilterOfflineSyncshellUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (!u.Key.IsDirectlyPaired && !u.Key.IsOnline && !u.Key.UserPair.OwnPermissions.IsPaused());


        if (_configService.Current.ShowVisibleUsersSeparately)
        {
            var allVisiblePairs = ImmutablePairList(allPairs
                .Where(FilterVisibleUsers));
            var filteredVisiblePairs = BasicSortedDictionary(filteredPairs
                .Where(FilterVisibleUsers));

            drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomVisibleTag, filteredVisiblePairs, allVisiblePairs));
        }

        List<IDrawFolder> groupFolders = new();
        foreach (var group in _pairManager.GroupPairs.Select(g => g.Key).OrderBy(g => g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            var allGroupPairs = ImmutablePairList(allPairs
                .Where(u => FilterGroupUsers(u, group)));

            var filteredGroupPairs = filteredPairs
                .Where(u => FilterGroupUsers(u, group) && FilterOnlineOrPausedSelf(u))
                .OrderByDescending(u => u.Key.IsOnline)
                .ThenBy(u =>
                {
                    if (string.Equals(u.Key.UserData.UID, group.OwnerUID, StringComparison.Ordinal)) return 0;
                    if (group.GroupPairUserInfos.TryGetValue(u.Key.UserData.UID, out var info))
                    {
                        if (info.IsModerator()) return 1;
                        if (info.IsPinned()) return 2;
                    }
                    return u.Key.IsVisible ? 3 : 4;
                })
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(k => k.Key, k => k.Value);

            groupFolders.Add(_drawEntityFactory.CreateDrawGroupFolder(group, filteredGroupPairs, allGroupPairs, _configService.Current.GroupUpSyncshells));
        }

        if (_configService.Current.GroupUpSyncshells)
            drawFolders.Add(new DrawGroupedGroupFolder(groupFolders, _tagHandler, _uiSharedService));
        else
            drawFolders.AddRange(groupFolders);

        var tags = _tagHandler.GetAllTagsSorted();
        foreach (var tag in tags)
        {
            var allTagPairs = ImmutablePairList(allPairs
                .Where(u => FilterTagusers(u, tag)));
            var filteredTagPairs = BasicSortedDictionary(filteredPairs
                .Where(u => FilterTagusers(u, tag) && FilterOnlineOrPausedSelf(u)));

            drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(tag, filteredTagPairs, allTagPairs));
        }

        var allOnlineNotTaggedPairs = ImmutablePairList(allPairs
            .Where(FilterNotTaggedUsers));
        var onlineNotTaggedPairs = BasicSortedDictionary(filteredPairs
            .Where(u => FilterNotTaggedUsers(u) && FilterOnlineOrPausedSelf(u)));

        drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder((_configService.Current.ShowOfflineUsersSeparately ? TagHandler.CustomOnlineTag : TagHandler.CustomAllTag),
            onlineNotTaggedPairs, allOnlineNotTaggedPairs));

        // Add paused users folder
        var allPausedPairs = ImmutablePairList(allPairs
            .Where(FilterPausedUsers));
        var filteredPausedPairs = BasicSortedDictionary(filteredPairs
            .Where(FilterPausedUsers));

        drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomPausedTag, filteredPausedPairs, allPausedPairs));

        if (_configService.Current.ShowOfflineUsersSeparately)
        {
            var allOfflinePairs = ImmutablePairList(allPairs
                .Where(FilterOfflineUsers));
            var filteredOfflinePairs = BasicSortedDictionary(filteredPairs
                .Where(FilterOfflineUsers));

            drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomOfflineTag, filteredOfflinePairs, allOfflinePairs));
            if (_configService.Current.ShowSyncshellOfflineUsersSeparately)
            {
                var allOfflineSyncshellUsers = ImmutablePairList(allPairs
                    .Where(FilterOfflineSyncshellUsers));
                var filteredOfflineSyncshellUsers = BasicSortedDictionary(filteredPairs
                    .Where(FilterOfflineSyncshellUsers));

                drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomOfflineSyncshellTag,
                    filteredOfflineSyncshellUsers,
                    allOfflineSyncshellUsers));
            }
        }

        drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomUnpairedTag,
            BasicSortedDictionary(filteredPairs.Where(u => u.Key.IsOneSidedPair)),
            ImmutablePairList(allPairs.Where(u => u.Key.IsOneSidedPair))));

        return drawFolders;
    }

    private string GetControlPanelTitle()
    {
#if DEBUG
        // Get build timestamp from assembly metadata
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var buildTimestamp = assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => attr.Key == "BuildTimestamp")?.Value;
        
        if (!string.IsNullOrEmpty(buildTimestamp))
        {
            return $"Sphene Control Panel - {buildTimestamp}";
        }
        
        return "Sphene Control Panel - Built: Dev Build";
#else
        return "Sphene Control Panel";
#endif
    }

    private void RefreshDrawFolders()
    {
        // Dispose old draw folders to clean up event subscriptions
        if (_drawFolders != null)
        {
            foreach (var folder in _drawFolders)
            {
                folder.Dispose();
            }
        }
        
        // Create new draw folders
        _drawFolders = GetDrawFolders().ToList();
    }

    private void RefreshIconsOnly()
    {
        // Only refresh icons without recreating DrawUserPair instances
        if (_drawFolders != null)
        {
            foreach (var folder in _drawFolders)
            {
                folder.RefreshIcons();
            }
        }
    }

    private string GetServerError()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => "Attempting to connect to the server.",
            ServerState.Reconnecting => "Connection to server interrupted, attempting to reconnect to the server.",
            ServerState.Disconnected => "You are currently disconnected from the Sphene server.",
            ServerState.Disconnecting => "Disconnecting from the server",
            ServerState.Unauthorized => "Server Response: " + _apiController.AuthFailureMessage,
            ServerState.Offline => "Your selected Sphene server is currently offline.",
            ServerState.VersionMisMatch =>
                "Your plugin or the server you are connecting to is out of date. Please update your plugin now. If you already did so, contact the server provider to update their server to the latest version.",
            ServerState.RateLimited => "You are rate limited for (re)connecting too often. Disconnect, wait 10 minutes and try again.",
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => "You have no secret key set for this current character. Open Settings -> Service Settings and set a secret key for the current character. You can reuse the same secret key for multiple characters.",
            ServerState.MultiChara => "Your Character Configuration has multiple characters configured with same name and world. You will not be able to connect until you fix this issue. Remove the duplicates from the configuration in Settings -> Service Settings -> Character Management and reconnect manually after.",
            ServerState.OAuthMisconfigured => "OAuth2 is enabled but not fully configured, verify in the Settings -> Service Settings that you have OAuth2 connected and, importantly, a UID assigned to your current character.",
            ServerState.OAuthLoginTokenStale => "Your OAuth2 login token is stale and cannot be used to renew. Go to the Settings -> Service Settings and unlink then relink your OAuth2 configuration.",
            ServerState.NoAutoLogon => "This character has automatic login into Sphene disabled. Press the connect button to connect to Sphene.",
            _ => string.Empty
        };
    }

    private Vector4 GetUidColor()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => ImGuiColors.DalamudRed,
            ServerState.Connected => ImGuiColors.ParsedGreen,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => ImGuiColors.DalamudRed,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => ImGuiColors.DalamudRed,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            ServerState.OAuthMisconfigured => ImGuiColors.DalamudRed,
            ServerState.OAuthLoginTokenStale => ImGuiColors.DalamudRed,
            ServerState.NoAutoLogon => ImGuiColors.DalamudYellow,
            _ => ImGuiColors.DalamudRed
        };
    }

    private string GetUidText()
    {
        return _apiController.ServerState switch
        {
            ServerState.Reconnecting => "Reconnecting",
            ServerState.Connecting => "Connecting",
            ServerState.Disconnected => "Disconnected",
            ServerState.Disconnecting => "Disconnecting",
            ServerState.Unauthorized => "Unauthorized",
            ServerState.VersionMisMatch => "Version mismatch",
            ServerState.Offline => "Unavailable",
            ServerState.RateLimited => "Rate Limited",
            ServerState.NoSecretKey => "No Secret Key",
            ServerState.MultiChara => "Duplicate Characters",
            ServerState.OAuthMisconfigured => "Misconfigured OAuth2",
            ServerState.OAuthLoginTokenStale => "Stale OAuth2",
            ServerState.NoAutoLogon => "Auto Login disabled",
            ServerState.Connected => _apiController.DisplayName,
            _ => string.Empty
        };
    }



    private void UiSharedService_GposeEnd()
    {
        IsOpen = _wasOpen;
    }

    private void UiSharedService_GposeStart()
    {
        _wasOpen = IsOpen;
        IsOpen = false;
    }

    private async Task<bool> IsPlayerInCurrentPartyAsync(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return false;
        
        var partyMembers = await _dalamudUtilService.RunOnFrameworkThread(() => _dalamudUtilService.GetPartyMemberNames());
        return partyMembers.Contains(playerName, StringComparer.OrdinalIgnoreCase);
    }

    private async Task HandleIncognitoModeToggle()
    {
        try
        {
            _logger.LogInformation("Incognito mode toggle clicked, current state: {state}", _isIncognitoModeActive);
            
            if (_isIncognitoModeActive)
            {
                // Resume: Unpause only user pairs that were paused by incognito mode (not pre-existing paused pairs)
                var allUserPairs = _pairManager.DirectPairs.ToList();
                _logger.LogInformation("Resuming {count} user pairs", allUserPairs.Count);
                foreach (var pair in allUserPairs)
                {
                    if (pair.UserPair != null && pair.UserPair.OwnPermissions.IsPaused())
                    {
                        // Only unpause if this pair was NOT already paused before incognito mode
                        if (!_prePausedPairs.Contains(pair.UserData.UID))
                        {
                            var permissions = pair.UserPair.OwnPermissions;
                            permissions.SetPaused(false);
                            await _apiController.UserSetPairPermissions(new(pair.UserData, permissions));
                            _logger.LogInformation("Unpaused pair (was paused by incognito): {uid}", pair.UserData.UID);
                        }
                        else
                        {
                            _logger.LogInformation("Keeping pair paused (was already paused before incognito): {uid}", pair.UserData.UID);
                        }
                    }
                }
                
                // Clear the tracking set for next incognito session
                _prePausedPairs.Clear();
                
                // Unpause syncshells that were paused during incognito mode (but not those that were already paused)
                var groupPairs = _pairManager.GroupPairs.ToList();
                var syncshellsToUnpause = new Dictionary<string, Sphene.API.Data.Enum.GroupUserPreferredPermissions>(StringComparer.Ordinal);
                
                foreach (var groupPair in groupPairs)
                {
                    var group = groupPair.Key;
                    
                    // Only unpause if this syncshell was NOT already paused before incognito mode
                    if (group.GroupUserPermissions.IsPaused() && !_prePausedSyncshells.Contains(group.Group.GID))
                    {
                        var unpausedPermissions = group.GroupUserPermissions;
                        unpausedPermissions.SetPaused(false);
                        syncshellsToUnpause[group.Group.GID] = unpausedPermissions;
                        _logger.LogInformation("Will unpause group (was paused by incognito): {gid}", group.Group.GID);
                    }
                    else if (_prePausedSyncshells.Contains(group.Group.GID))
                    {
                        _logger.LogInformation("Keeping group paused (was already paused before incognito): {gid}", group.Group.GID);
                    }
                }
                
                // Apply bulk unpause to syncshells
                if (syncshellsToUnpause.Count > 0)
                {
                    await _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), syncshellsToUnpause));
                    _logger.LogInformation("Unpaused {count} syncshells after incognito mode", syncshellsToUnpause.Count);
                }
                
                // Clear the tracking set for next incognito session
                _prePausedSyncshells.Clear();
                
                _isIncognitoModeActive = false;
                _logger.LogInformation("Incognito mode deactivated");
                
                // Save configuration
                _configService.Current.IsIncognitoModeActive = _isIncognitoModeActive;
                _configService.Current.PrePausedPairs = new HashSet<string>(_prePausedPairs);
                _configService.Current.PrePausedSyncshells = new HashSet<string>(_prePausedSyncshells);
                _configService.Save();
            }
            else
            {
                // Incognito Mode: Pause user pairs that are NOT in current party
                var allUserPairs = _pairManager.DirectPairs.ToList();
                var partyMembers = await _dalamudUtilService.RunOnFrameworkThread(() => _dalamudUtilService.GetPartyMemberNames());
                _logger.LogInformation("Emergency stop: Checking {count} user pairs against party members: [{members}]", 
                    allUserPairs.Count, string.Join(", ", partyMembers));
                
                // Clear previous tracking and record currently paused pairs
                _prePausedPairs.Clear();
                _prePausedSyncshells.Clear();
                
                foreach (var pair in allUserPairs)
                {
                    if (pair.UserPair != null)
                    {
                        // Track pairs that are already paused before we start incognito mode
                        if (pair.UserPair.OwnPermissions.IsPaused())
                        {
                            _prePausedPairs.Add(pair.UserData.UID);
                            _logger.LogInformation("Tracked pre-paused pair: {uid}", pair.UserData.UID);
                        }
                        else
                        {
                            var playerName = pair.PlayerName;
                            if (!await IsPlayerInCurrentPartyAsync(playerName))
                            {
                                var permissions = pair.UserPair.OwnPermissions;
                                permissions.SetPaused(true);
                                await _apiController.UserSetPairPermissions(new(pair.UserData, permissions));
                                _logger.LogInformation("Paused pair (not in party): {uid} - {playerName}", pair.UserData.UID, playerName);
                            }
                            else
                            {
                                _logger.LogInformation("Skipped pausing pair (in party): {uid} - {playerName}", pair.UserData.UID, playerName);
                            }
                        }
                    }
                }

                // Pause syncshells/groups where no members are in current party
                var groupPairs = _pairManager.GroupPairs.ToList();
                _logger.LogInformation("Incognito mode: Checking {count} groups for non-party members", groupPairs.Count);
                
                var syncshellsToPause = new Dictionary<string, Sphene.API.Data.Enum.GroupUserPreferredPermissions>(StringComparer.Ordinal);
                
                foreach (var groupPair in groupPairs)
                {
                    var group = groupPair.Key;
                    var pairsInGroup = groupPair.Value;
                    
                    // Check if any member of this group is in the current party
                    bool hasPartyMember = false;
                    foreach (var pair in pairsInGroup)
                    {
                        if (await IsPlayerInCurrentPartyAsync(pair.PlayerName))
                        {
                            hasPartyMember = true;
                            break;
                        }
                    }
                    
                    if (!hasPartyMember)
                    {
                        // Store current pause state and pause the syncshell
                        if (!group.GroupUserPermissions.IsPaused())
                        {
                            _prePausedSyncshells.Add(group.Group.GID);
                        }
                        var pausedPermissions = group.GroupUserPermissions;
                        pausedPermissions.SetPaused(true);
                        syncshellsToPause[group.Group.GID] = pausedPermissions;
                        _logger.LogInformation("Will pause group (no party members): {gid}", group.Group.GID);
                    }
                    else
                    {
                        _logger.LogInformation("Keeping group active (has party members): {gid}", group.Group.GID);
                    }
                }
                
                // Apply bulk pause to syncshells
                if (syncshellsToPause.Count > 0)
                {
                    await _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), syncshellsToPause));
                    _logger.LogInformation("Paused {count} syncshells for incognito mode", syncshellsToPause.Count);
                }
                
                _isIncognitoModeActive = true;
                _logger.LogInformation("Incognito mode activated");
                
                // Save configuration
                _configService.Current.IsIncognitoModeActive = _isIncognitoModeActive;
                _configService.Current.PrePausedPairs = new HashSet<string>(_prePausedPairs);
                _configService.Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during incognito mode toggle");
        }
    }

    private long GetCurrentTextureSize()
    {
        if (_characterAnalyzer.LastAnalysis == null || _characterAnalyzer.LastAnalysis.Count == 0)
            return 0;

        long totalSize = 0;
        foreach (var objectKindData in _characterAnalyzer.LastAnalysis.Values)
        {
            foreach (var fileEntry in objectKindData.Values)
            {
                if (fileEntry.FileType.Equals("tex", StringComparison.OrdinalIgnoreCase))
                {
                    totalSize += fileEntry.OriginalSize;
                }
            }
        }
        return totalSize;
    }

    private (Vector4 color, string text, FontAwesomeIcon icon) GetBc7ConversionIndicator()
    {
        var textureSizeBytes = GetCurrentTextureSize();
        var textureSizeMB = textureSizeBytes / (1024.0 * 1024.0);

        if (textureSizeMB <= 300)
        {
            return (ImGuiColors.HealerGreen, $"Texture Size: Optimal ({textureSizeMB:F1} MB)", FontAwesomeIcon.CheckCircle);
        }
        else if (textureSizeMB <= 600)
        {
            return (ImGuiColors.DalamudYellow, $"Texture Size: Large ({textureSizeMB:F1} MB)", FontAwesomeIcon.ExclamationTriangle);
        }
        else
        {
            return (ImGuiColors.DalamudRed, $"Texture Size: Very Large ({textureSizeMB:F1} MB)", FontAwesomeIcon.ExclamationCircle);
        }
    }

    private void DrawCompactTextureIndicator()
    {
        var (color, _, icon) = GetBc7ConversionIndicator();
        var textureSizeBytes = GetCurrentTextureSize();
        var textureSizeMB = textureSizeBytes / (1024.0 * 1024.0);
        
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        _uiSharedService.IconText(icon);
        ImGui.SameLine(0, 2);
        ImGui.Text($"{textureSizeMB:F0}MB");
        ImGui.PopStyleColor();
        
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(SpheneColors.SpheneGold, "Texture Size");
            ImGui.Separator();
            ImGui.Text($"Current: {textureSizeMB:F0} MB");
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.ParsedGreen, "• 0-300 MB: Optimal");
            ImGui.TextColored(ImGuiColors.DalamudYellow, "• 300-600 MB: Large");
            ImGui.TextColored(ImGuiColors.DalamudRed, "• 600+ MB: Very Large");
            ImGui.Spacing();
            ImGui.Text("Use Data Analysis to optimize textures.");
            ImGui.Spacing();
            ImGui.TextColored(SpheneColors.SpheneGold, "💡 Tip: Click the archive button for quick conversion!");
            ImGui.EndTooltip();
        }
    }

    private void DrawTextureConversionPopup()
    {
        if (_showConversionPopup)
        {
            ImGui.OpenPopup("Texture Conversion");
        }

        // Open progress popup when needed
        if (_showProgressPopup)
        {
            ImGui.OpenPopup("BC7 Conversion in Progress");
            _showProgressPopup = false;
        }

        // Conversion progress modal
        if (_conversionTask != null && !_conversionTask.IsCompleted)
        {
            if (ImGui.BeginPopupModal("BC7 Conversion in Progress", ImGuiWindowFlags.NoResize))
            {
                // Title and overall progress
                ImGui.TextColored(SpheneColors.SpheneGold, "Converting Textures to BC7 Format");
                ImGui.Separator();
                
                // Progress statistics
                var totalFiles = _texturesToConvert.Count;
                var currentFile = _conversionCurrentFileProgress;
                var progressPercentage = totalFiles > 0 ? (float)currentFile / totalFiles : 0f;
                var remainingFiles = totalFiles - currentFile;
                
                ImGui.Text($"Progress: {currentFile} / {totalFiles} files");
                ImGui.Text($"Remaining: {remainingFiles} files");
                ImGui.Text($"Percentage: {progressPercentage * 100:F1}%");
                
                // Estimated time remaining
                if (currentFile > 0 && _conversionStartTime != DateTime.MinValue)
                {
                    var elapsed = DateTime.Now - _conversionStartTime;
                    var avgTimePerFile = elapsed.TotalSeconds / currentFile;
                    var estimatedRemainingSeconds = avgTimePerFile * remainingFiles;
                    var estimatedRemaining = TimeSpan.FromSeconds(estimatedRemainingSeconds);
                    
                    ImGui.Text($"Elapsed: {elapsed:mm\\:ss}");
                    if (remainingFiles > 0)
                    {
                        ImGui.Text($"Estimated remaining: {estimatedRemaining:mm\\:ss}");
                    }
                }
                
                // Progress bar
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, SpheneColors.SpheneGold);
                ImGui.ProgressBar(progressPercentage, new Vector2(-1, 0), $"{progressPercentage * 100:F1}%");
                ImGui.PopStyleColor();
                
                ImGui.Spacing();
                
                // Current file information
                if (!string.IsNullOrEmpty(_conversionCurrentFileName))
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey3, "Currently processing:");
                    
                    // Extract filename from full path
                    var fileName = Path.GetFileName(_conversionCurrentFileName);
                    var directory = Path.GetDirectoryName(_conversionCurrentFileName);
                    
                    ImGui.Text($"File: {fileName}");
                    if (!string.IsNullOrEmpty(directory))
                    {
                        ImGui.TextColored(ImGuiColors.DalamudGrey, $"Path: {directory}");
                    }
                    
                    // Try to get file size if file exists
                    try
                    {
                        if (File.Exists(_conversionCurrentFileName))
                        {
                            var fileInfo = new FileInfo(_conversionCurrentFileName);
                            var sizeInMB = fileInfo.Length / (1024.0 * 1024.0);
                            ImGui.TextColored(ImGuiColors.DalamudGrey, $"Size: {sizeInMB:F2} MB");
                        }
                    }
                    catch
                    {
                        // Ignore file access errors
                    }
                }
                
                ImGui.Spacing();
                ImGui.Separator();
                
                // Cancel button
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel Conversion"))
                {
                    _conversionCancellationTokenSource.Cancel();
                }
                
                UiSharedService.SetScaledWindowSize(600);
                ImGui.EndPopup();
            }
        }
        else if (_conversionTask != null && _conversionTask.IsCompleted && _texturesToConvert.Count > 0)
        {
            _conversionTask = null;
            _texturesToConvert.Clear();
        }

        // Main conversion popup
        if (ImGui.BeginPopupModal("Texture Conversion", ref _showConversionPopup, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // Cache the analysis when popup opens, similar to DataAnalysisUi approach
            if (_cachedAnalysisForPopup == null)
            {
                _cachedAnalysisForPopup = _characterAnalyzer.LastAnalysis?.DeepClone();
                _cachedBackupsForAnalysis = null; // Invalidate backup cache when analysis changes
            }
            
            var analysis = _cachedAnalysisForPopup;
            
            if (analysis == null || !analysis.Any())
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "No character data available");
                ImGui.Text("Please ensure you have a character loaded and analyzed.");
                
                // Still show backup management even without analysis
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextUnformatted("Backup Management:");
                
                var allBackups = _textureBackupService.GetBackupsByOriginalFile();
                
                if (allBackups.Count > 0)
                {
                    UiSharedService.ColorTextWrapped($"Found {allBackups.Count} total backup file(s). Note: Without character analysis, all backups are shown.", ImGuiColors.DalamudYellow);
                    
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
                    {
                        var backupDirectory = _textureBackupService.GetBackupDirectory();
                        if (Directory.Exists(backupDirectory))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
                        }
                    }
                }
                else
                {
                    UiSharedService.ColorTextWrapped("No texture backups found.", ImGuiColors.DalamudGrey);
                }
                
                if (ImGui.Button("Close"))
                {
                    _showConversionPopup = false;
                    _cachedAnalysisForPopup = null; // Clear cached analysis when popup closes
                }
                ImGui.EndPopup();
                return;
            }

            // Get texture data from analysis
            var textureData = GetTextureDataFromAnalysis(analysis);
            var nonBc7Textures = textureData.Where(t => !string.Equals(t.Format, "BC7", StringComparison.Ordinal)).ToList();
            var totalTextures = textureData.Count;
            var bc7Textures = totalTextures - nonBc7Textures.Count;

            ImGui.TextColored(SpheneColors.SpheneGold, "Texture Conversion Overview");
            ImGui.Separator();

            // Statistics
            ImGui.Text($"Total textures: {totalTextures}");
            ImGui.Text($"Already BC7: {bc7Textures}");
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Can be converted: {nonBc7Textures.Count}");

            // Backup restore functionality - always show regardless of conversion needs
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted("Backup Management:");
            
            var availableBackups = GetCachedBackupsForCurrentAnalysis();
            if (availableBackups.Count > 0)
            {
                UiSharedService.ColorTextWrapped($"Found {availableBackups.Count} backup(s) for current textures.", ImGuiColors.ParsedGreen);
                
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, "Restore Textures"))
                {
                    StartTextureRestore(availableBackups);
                }
                
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
                {
                    var backupDirectory = _textureBackupService.GetBackupDirectory();
                    if (Directory.Exists(backupDirectory))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
                    }
                }
                
                // BC7 conversion option after restore
                ImGui.Spacing();
                UiSharedService.ColorText("After Restore Options:", ImGuiColors.DalamudYellow);
                if (nonBc7Textures.Count > 0)
                {
                    UiSharedService.ColorTextWrapped($"After restoring textures, you can convert {nonBc7Textures.Count} non-BC7 textures to reduce size.", ImGuiColors.DalamudGrey);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileArchive, $"Convert to BC7 ({nonBc7Textures.Count} textures)"))
                    {
                        StartTextureConversion(nonBc7Textures);
                        _showConversionPopup = false;
                        _cachedAnalysisForPopup = null;
                        _showProgressPopup = true;
                    }
                }
                else
                {
                    UiSharedService.ColorTextWrapped("All current textures are already BC7 optimized.", ImGuiColors.ParsedGreen);
                }
            }
            else
            {
                UiSharedService.ColorTextWrapped("No backups found for current textures.", ImGuiColors.DalamudGrey);
                
                // Still show BC7 conversion option even without backups
                if (nonBc7Textures.Count > 0)
                {
                    ImGui.Spacing();
                    UiSharedService.ColorText("Texture Optimization:", ImGuiColors.DalamudYellow);
                    UiSharedService.ColorTextWrapped($"You can convert {nonBc7Textures.Count} non-BC7 textures to reduce size.", ImGuiColors.DalamudGrey);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileArchive, $"Convert to BC7 ({nonBc7Textures.Count} textures)"))
                    {
                        StartTextureConversion(nonBc7Textures);
                        _showConversionPopup = false;
                        _cachedAnalysisForPopup = null;
                        _showProgressPopup = true;
                    }
                }
            }

            if (nonBc7Textures.Count > 0)
            {
                var totalSizeMB = nonBc7Textures.Sum(t => t.OriginalSize) / (1024.0 * 1024.0);
                ImGui.Text($"Total size to convert: {totalSizeMB:F1} MB");

                ImGui.Spacing();
                ImGui.Checkbox("Create backup before conversion", ref _enableBackupBeforeConversion);
                if (_enableBackupBeforeConversion)
                {
                    UiSharedService.ColorTextWrapped("Backups will be created automatically for revert functionality.", ImGuiColors.ParsedGreen);
                }

                ImGui.Spacing();
                UiSharedService.ColorText("Conversion Info:", ImGuiColors.DalamudYellow);
                UiSharedService.ColorTextWrapped("• BC7 conversion reduces texture size significantly\n• Some textures may show visual artifacts\n• Original textures are backed up for restoration\n• Process may take time depending on texture count", ImGuiColors.DalamudGrey);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextUnformatted("Texture Conversion:");
                
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileArchive, $"Convert {nonBc7Textures.Count} textures to BC7"))
                {
                    StartTextureConversion(nonBc7Textures);
                    _showConversionPopup = false;
                    _cachedAnalysisForPopup = null; // Clear cached analysis when popup closes
                    _showProgressPopup = true;
                }
            }
            else
            {
                ImGui.TextColored(ImGuiColors.ParsedGreen, "All textures are already optimized!");
            }

            if (ImGui.Button("Close"))
            {
                _showConversionPopup = false;
                _cachedAnalysisForPopup = null; // Clear cached analysis when popup closes
            }

            UiSharedService.SetScaledWindowSize(400);
            ImGui.EndPopup();
        }
    }

    private List<(string Format, long OriginalSize, List<string> FilePaths)> GetTextureDataFromAnalysis(Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> analysis)
    {
        var textureData = new List<(string Format, long OriginalSize, List<string> FilePaths)>();
        var totalFiles = 0;
        var textureFiles = 0;

        foreach (var objectKindData in analysis.Values)
        {
            foreach (var fileData in objectKindData.Values)
            {
                totalFiles++;
                if (fileData.FilePaths != null && fileData.FilePaths.Count > 0)
                {
                    // Check if this is a texture file (has format information)
                    if (fileData.Format != null && !string.IsNullOrEmpty(fileData.Format.Value))
                    {
                        textureFiles++;
                        textureData.Add((fileData.Format.Value, fileData.OriginalSize, fileData.FilePaths));
                    }
                }
            }
        }
        return textureData;
    }

   private async void StartTextureConversion(List<(string Format, long OriginalSize, List<string> FilePaths)> textureData)
    {
        _texturesToConvert.Clear();

        // Prepare conversion dictionary
        foreach (var texture in textureData)
        {
            if (texture.FilePaths.Count > 0)
            {
                var primaryPath = texture.FilePaths.First();
                var duplicatePaths = texture.FilePaths.Skip(1).ToArray();
                _texturesToConvert[primaryPath] = duplicatePaths;
            }
        }

        _logger.LogDebug("Starting texture conversion for {Count} textures", _texturesToConvert.Count);

        _conversionStartTime = DateTime.Now;
        _conversionCancellationTokenSource = new CancellationTokenSource();

        if (_enableBackupBeforeConversion)
        {
            StartBackupAndConversion();
        }
        else
        {
            _conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, _conversionCancellationTokenSource.Token);
        }
    }

    private void StartBackupAndConversion()
    {
        _conversionTask = Task.Run(async () =>
        {
            try
            {
                // Create backups first
                var allTexturePaths = _texturesToConvert.SelectMany(kvp => new[] { kvp.Key }.Concat(kvp.Value)).ToArray();
                _logger.LogDebug("Starting backup for {Count} texture paths", allTexturePaths.Length);
                
                var backupProgress = new Progress<(string fileName, int current, int total)>();
                await _textureBackupService.BackupTexturesAsync(allTexturePaths, backupProgress, _conversionCancellationTokenSource.Token);

                // Start conversion after backup is complete
                _logger.LogDebug("Backup completed, starting texture conversion");
                var conversionTask = _ipcManager.Penumbra.ConvertTextureFiles(_logger, _texturesToConvert, _conversionProgress, _conversionCancellationTokenSource.Token);
                await conversionTask;
                
                _logger.LogDebug("Texture conversion completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during backup and conversion process");
            }
        }, _conversionCancellationTokenSource.Token);
    }
    
    private Dictionary<string, List<string>> GetCachedBackupsForCurrentAnalysis()
    {
        // Cache for 5 seconds to avoid excessive recalculation
        if (_cachedBackupsForAnalysis != null && 
            DateTime.Now - _lastBackupAnalysisUpdate < TimeSpan.FromSeconds(5))
        {
            return _cachedBackupsForAnalysis;
        }

        _cachedBackupsForAnalysis = GetBackupsForCurrentAnalysis();
        _lastBackupAnalysisUpdate = DateTime.Now;
        return _cachedBackupsForAnalysis;
    }

    private Dictionary<string, List<string>> GetBackupsForCurrentAnalysis()
    {
        try
        {
            // Use cached analysis if available (for popup), otherwise use current analysis
            var analysis = _cachedAnalysisForPopup ?? _characterAnalyzer.LastAnalysis;
            if (analysis == null || !analysis.Any()) 
            {
                _logger.LogDebug("No character analysis available for backup filtering (cached: {cached}, current: {current})", 
                    _cachedAnalysisForPopup != null, _characterAnalyzer.LastAnalysis != null);
                return new Dictionary<string, List<string>>();
            }
            
            // Get all available backups
            var allBackups = _textureBackupService.GetBackupsByOriginalFile();
            _logger.LogDebug("Found {count} total backup files", allBackups.Count);
            
            var filteredBackups = new Dictionary<string, List<string>>();
            
            // Get all texture filenames from current analysis
            var currentTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textureData = GetTextureDataFromAnalysis(analysis);
            _logger.LogDebug("Found {count} texture entries in current analysis", textureData.Count);
            
            foreach (var texture in textureData)
            {
                foreach (var filePath in texture.FilePaths)
                {
                    var fileName = Path.GetFileName(filePath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        currentTextureNames.Add(fileName);
                        _logger.LogDebug("Added texture filename to filter: {fileName}", fileName);
                    }
                }
            }
            
            _logger.LogDebug("Total unique texture filenames in analysis: {count}", currentTextureNames.Count);
            
            // Filter backups to only include those for currently loaded textures
            foreach (var backup in allBackups)
            {
                if (currentTextureNames.Contains(backup.Key))
                {
                    filteredBackups[backup.Key] = backup.Value;
                    _logger.LogDebug("Found matching backup for texture: {fileName} with {backupCount} backup files", backup.Key, backup.Value.Count);
                }
            }
            
            _logger.LogDebug("Filtered backups result: {count} textures have backups", filteredBackups.Count);
            return filteredBackups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get backups for current analysis");
            return new Dictionary<string, List<string>>();
        }
    }
    
    private void StartTextureRestore(Dictionary<string, List<string>> availableBackups)
    {
        _restoreCancellationTokenSource = _restoreCancellationTokenSource.CancelRecreate();
        
        _restoreTask = Task.Run(async () =>
        {
            try
            {
                // Create mapping of backup files to their target locations
                var backupToTargetMap = new Dictionary<string, string>();
                
                foreach (var kvp in availableBackups)
                {
                    var originalFileName = kvp.Key;
                    var backupFiles = kvp.Value;
                    
                    // Use the most recent backup (first in the list since they're ordered by creation time)
                    if (backupFiles.Count > 0)
                    {
                        var mostRecentBackup = backupFiles.First();
                        
                        // Try to find the current location of this texture in the loaded data
                        var targetPath = FindCurrentTextureLocation(originalFileName);
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            backupToTargetMap[mostRecentBackup] = targetPath;
                            _logger.LogDebug("Will restore {backup} to {target}", mostRecentBackup, targetPath);
                        }
                        else
                        {
                            _logger.LogWarning("Could not find current location for texture: {originalFileName}", originalFileName);
                        }
                    }
                }
                
                if (backupToTargetMap.Count > 0)
                {
                    _logger.LogDebug("Starting texture restore for {count} texture(s) from current analysis", backupToTargetMap.Count);
                    var results = await _textureBackupService.RestoreTexturesAsync(backupToTargetMap, deleteBackupsAfterRestore: true, _restoreProgress, _restoreCancellationTokenSource.Token);
                    
                    var successCount = results.Values.Count(success => success);
                    _logger.LogInformation("Texture restore completed: {successCount}/{totalCount} textures restored successfully. Backup files have been automatically deleted.", successCount, results.Count);
                    
                    // Invalidate backup cache after restore
                    if (successCount > 0)
                    {
                        _cachedBackupsForAnalysis = null;
                        
                        // Trigger character redraw to reload restored textures in Penumbra
                        try
                        {
                            await _ipcManager.Penumbra.RedrawPlayerAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to trigger character redraw after texture restore");
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("No textures from current analysis could be mapped for restoration");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during texture restore process");
            }
        }, _restoreCancellationTokenSource.Token);
    }
    
    private string FindCurrentTextureLocation(string originalFileName)
    {
        try
        {
            var analysis = _characterAnalyzer.LastAnalysis;
            if (analysis == null) return string.Empty;
            
            var textureData = GetTextureDataFromAnalysis(analysis);
            foreach (var texture in textureData)
            {
                foreach (var filePath in texture.FilePaths)
                {
                    var fileName = Path.GetFileName(filePath);
                    if (string.Equals(fileName, originalFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return filePath;
                    }
                }
            }
            
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find current texture location for {fileName}", originalFileName);
            return string.Empty;
        }
    }
}
