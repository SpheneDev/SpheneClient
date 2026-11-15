using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
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
using Sphene.UI.Design;
using Sphene.UI.Hub;
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
using ShrinkU.Helpers;
using System.Numerics;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Dalamud.Interface.Textures.TextureWraps;
using ShrinkU.Configuration;

namespace Sphene.UI.Panels;

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
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly AreaBoundSyncshellService _areaBoundSyncshellService;
    private readonly SpheneProfileManager _spheneProfileManager;
    private byte[] _lastAvatarBytes = [];
    private byte[] _lastRowImageBytes = [];
    private bool _titleBarPressed;
    private List<IDrawFolder> _drawFolders = new();
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private Vector2 _lastPosition = Vector2.One;
    private bool _isIncognitoModeActive = false;
    private DateTime _lastIncognitoButtonClick = DateTime.MinValue;
    private bool _hadAvailableAreaSyncshells = false;
    private DateTime _areaSyncshellAttentionUntilUtc = DateTime.MinValue;
    private int _selectedSecretKeyForNewCharacter = -1;
    private string _customSecretKeyInput = string.Empty;
    private readonly HashSet<string> _prePausedPairs;
    private readonly HashSet<string> _prePausedSyncshells;
    private readonly System.Threading.Lock _pendingModSharingLock = new();
    private readonly HashSet<string> _pendingModSharingHashes = new(StringComparer.Ordinal);
    private Sphene.Services.UpdateInfo? _updateBannerInfo;
    private DateTime _lastReconnectButtonClick = DateTime.MinValue;
 private Vector2 _lastSize = Vector2.One;
    // One-time check to correct persisted width below minimum
    private bool _widthCorrectionChecked = false;
    private const string ControlPanelTitle = "Sphene Control Panel";
    // Stable fold-state key for the profile title bar collapse animation
    private const string ProfileFoldId = "###SpheneMainUI";

    /// <summary>
    /// Width the folded bar shrinks to: the hub-width identity card plus its
    /// insets, the gap, and the close/fold buttons with right pad · unscaled,
    /// as <see cref="SpheneUI.Chrome.UpdateCollapse"/> expects.
    /// </summary>
    private static float FoldedBarWidthUnscaled
        => (SpheneHubUi.ProfileCardWidth + 14f * SpheneUI.Scale + 64f) / SpheneUI.Scale;
    // Unscaled height of the profile title bar (34 avatar + 2*6 padding)
    private const float ProfileBarHeight = 46f;
    /// <summary>Horizontal inset for header content rows · the window runs
    /// edge to edge, so content keeps its own breathing room.</summary>
    private const float HeaderContentPadX = 14f;
#if IS_TEST_BUILD
    private const string TestServerDisclaimerPopupName = "Test Server Disclaimer";
    private bool _showTestServerDisclaimerPopup;
    private DateTime _testServerDisclaimerOpenedAt = DateTime.MinValue;
#endif
    
    private bool _showModalForUserAddition;
    private float _transferPartHeight;
    private readonly TransferRateMeter _uploadRateMeter = new();
    private readonly TransferRateMeter _downloadRateMeter = new();

    // ---- profile popout: grows the window's own surface sideways instead of
    // docking a separate window, so the reveal can never detach or overlap ----
    private const float PopoutPanelWidth = 352f;
    private const float PopoutSlideSeconds = 0.18f;
    private const double PopoutDismissGraceSeconds = 0.2;
    private const float PopoutSwapCoverSeconds = 0.07f;
    private const float PopoutSwapRevealSeconds = 0.16f;
    private Pair? _popoutPair;
    private Pair? _popoutWarmPair;
    // the pair actually drawn lags the activation target while a swap fade runs
    private Pair? _popoutContentPair;
    private float _popoutSwapCover;
    private string? _popoutKey;
    private float _popoutOpenT;
    private float _popoutGrown;
    private bool _popoutDismissed;
    private bool _popoutForceClose;
    private bool _popoutLeft;
    private bool _popoutSideDecided;
    private DateTime _popoutGraceUntil = DateTime.MinValue;
    private Vector2 _popoutPanelMin;
    private Vector2 _popoutPanelMax;
    private byte[] _popoutAvatarBytes = [];
    private byte[] _popoutBannerBytes = [];
    private bool _wasOpen;
    private bool _aprilFoolsHyperSyncEnabled;
    private DateTime _aprilFoolsHyperSyncActivatedAt = DateTime.MinValue;


    private readonly ShrinkU.Services.TextureConversionService _shrinkuConversionService;
    private readonly ShrinkUConfigService _shrinkuConfigService;

    public CompactUi(ILogger<CompactUi> logger, SpheneConfigService configService, ApiController apiController, PairManager pairManager,
        ServerConfigurationManager serverManager, SpheneMediator mediator, FileUploadManager fileTransferManager,
        TagHandler tagHandler, DrawEntityFactory drawEntityFactory, SelectTagForPairUi selectTagForPairUi, SelectPairForTagUi selectPairForTagUi,
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, DalamudUtilService dalamudUtilService, CharacterAnalyzer characterAnalyzer,
        AreaBoundSyncshellService areaBoundSyncshellService,
        ShrinkU.Services.TextureConversionService shrinkuConversionService,
        ShrinkUConfigService shrinkuConfigService,
        SpheneProfileManager spheneProfileManager)
        : base(logger, mediator, "###SpheneMainUI", performanceCollectorService)
    {
        _prePausedPairs = new(StringComparer.Ordinal);
        _prePausedSyncshells = new(StringComparer.Ordinal);
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
        _shrinkuConversionService = shrinkuConversionService;
        _shrinkuConfigService = shrinkuConfigService;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        _spheneProfileManager = spheneProfileManager;
        _tabMenu = new TopTabMenu(Mediator, _apiController, _pairManager);

        Mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnModSharingTransferAvailable);
        Mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnModSharingTransferCompleted);
        Mediator.Subscribe<PenumbraModTransferDiscardedMessage>(this, OnModSharingTransferDiscarded);
        Mediator.Subscribe<DisconnectedMessage>(this, _ => { ClearPendingModSharing(); _popoutPair = null; _popoutForceClose = true; });

        Mediator.Subscribe<ProfilePopoutPreload>(this, (msg) => _popoutWarmPair = msg.Pair);

        Mediator.Subscribe<ProfilePopoutToggle>(this, (msg) =>
        {
            if (msg.Pair != null)
            {
                _popoutPair = msg.Pair;
                _popoutDismissed = false;
                _popoutForceClose = false;
            }
            else
            {
                _popoutDismissed = true;
                _popoutGraceUntil = DateTime.UtcNow.AddSeconds(PopoutDismissGraceSeconds);
            }
        });

        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (_popoutKey != null
                && (msg.UserData == null || string.Equals(msg.UserData.UID, _popoutContentPair?.UserData.UID, StringComparison.Ordinal)))
            {
                SpheneUI.Media.Evict(_popoutKey);
                SpheneUI.Media.Evict(_popoutKey + ":banner");
            }
        });

        Mediator.Subscribe<CompactUiStickToSettingsMessage>(this, msg =>
        {
            if (msg.Enabled && !_stickEnabled)
            {
                _preStickPos = _lastPosition;
            }
            else if (!msg.Enabled && _stickEnabled)
            {
                _restoreAfterUnstick = true;
            }
            _stickEnabled = msg.Enabled;
            _settingsPos = msg.SettingsPos;
            _settingsSize = msg.SettingsSize;
        });

        Mediator.Unsubscribe<UiToggleMessage>(this);
        Mediator.Subscribe<UiToggleMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                if (_stickEnabled)
                {
                    return;
                }
                Toggle();
            }
        });

        Mediator.Subscribe<FrameworkUpdateMessage>(this, (_) =>
        {
            if (_stickEnabled && !IsOpen)
                IsOpen = true;
        });

        // Initialize incognito mode state from configuration
        _isIncognitoModeActive = _configService.Current.IsIncognitoModeActive;
        _prePausedPairs = new HashSet<string>(_configService.Current.PrePausedPairs, StringComparer.Ordinal);
        _prePausedSyncshells = new HashSet<string>(_configService.Current.PrePausedSyncshells, StringComparer.Ordinal);

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
                    using (SpheneUI.Theme.TooltipScope())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Open Network Configuration");
                        ImGui.EndTooltip();
                    }
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
                    using (SpheneUI.Theme.TooltipScope())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("Open Network Event Log");
                        ImGui.EndTooltip();
                    }
                }
            }
        };

        RefreshDrawFolders();

#if IS_TEST_BUILD
        string dev = "Dev/Test Build";
        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        var revision = ver.Revision < 0 ? 0 : ver.Revision;
        WindowName = $"Sphene {dev} ({ver.Major}.{ver.Minor}.{ver.Build}.{revision})###SpheneMainUI";
        Toggle();
#else
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var major = ver?.Major ?? 0;
        var minor = ver?.Minor ?? 0;
        var build = ver?.Build ?? 0;
        var revision = ver?.Revision ?? 0;
        if (revision < 0) revision = 0;
        WindowName = "Sphene " + major + "." + minor + "." + build + "." + revision + "###SpheneMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => { _logger.LogDebug("SwitchToIntroUiMessage received, closing CompactUI"); IsOpen = false; });
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        Mediator.Subscribe<RefreshUiMessage>(this, (msg) => RefreshIconsOnly());
        Mediator.Subscribe<StructuralRefreshUiMessage>(this, (msg) => RefreshDrawFolders());
        Mediator.Subscribe<QueryWindowOpenStateMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                msg.Respond(IsOpen);
            }
        });
        Mediator.Subscribe<ShowUpdateNotificationMessage>(this, (msg) => _updateBannerInfo = msg.UpdateInfo);

        // Configure base window flags
        Flags |= ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        // Enforce minimum window size for control panel
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(425, 400)
        };

        // End of constructor
    }

    // ShrinkU helpers for storage info and cleanup
    protected override void Dispose(bool disposing)
    {
        if (disposing && _drawFolders != null)
        {
            // Dispose all draw folders to clean up event subscriptions
            foreach (var folder in _drawFolders)
            {
                folder.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private IDisposable? _themeScope;
    private bool _stickEnabled;
    private Vector2 _settingsPos;
    private Vector2 _settingsSize;
    private Vector2 _preStickPos;
    private bool _restoreAfterUnstick;

    protected override bool AppliesOwnThemeScope => true;

    public override void PreDraw()
    {
        if (_stickEnabled && !IsOpen)
        {
            IsOpen = true;
        }
        var settingsOpen = false;
        Mediator.Publish(new QueryWindowOpenStateMessage(typeof(SettingsUi), state => settingsOpen = state));
        if (_stickEnabled && !settingsOpen)
        {
            _stickEnabled = false;
            _restoreAfterUnstick = true;
        }
        // Apply CompactUI theme before the window is drawn
        _themeScope = SpheneUI.Theme.CompactScope(WindowName);

        // ImGui forces WindowRounding to 0 once the window owns a secondary
        // viewport (dragged onto another monitor) · its native frame would
        // render square there. The surface and border are drawn manually
        // instead, using the style radius which stays correct.
        Flags |= ImGuiWindowFlags.NoBackground;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        // Content runs edge to edge horizontally · the title bar and the
        // lower area both reach the window borders (content region = window rect)
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(0f, SpheneTheme.Current.CompactWindowPadding.Y));

        // Update window flags based on current theme settings
        if (!SpheneTheme.Current.CompactShowImGuiHeader)
        {
            // The custom profile bar acts as the title bar, including its own
            // fold · collapsed height is exactly the bar so nothing shows below
            Flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
            SpheneUI.Chrome.UpdateCollapse(this, ProfileFoldId, ProfileBarHeight,
                FoldedBarWidthUnscaled);
        }
        else
        {
            Flags &= ~(ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse);
        }
        
        if (_stickEnabled)
        {
            var pad = 10.0f * ImGuiHelpers.GlobalScale;
            ImGui.SetNextWindowPos(new Vector2(_settingsPos.X + _settingsSize.X + pad, _settingsPos.Y), ImGuiCond.Always);
            Flags |= ImGuiWindowFlags.NoMove;
        }
        else
        {
            if (_restoreAfterUnstick)
            {
                if (_preStickPos.X > 2.0f && _preStickPos.Y > 2.0f)
                {
                    ImGui.SetNextWindowPos(_preStickPos, ImGuiCond.Always);
                }
                _restoreAfterUnstick = false;
                _preStickPos = Vector2.Zero;
            }
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        
        // Apply any pending window resize before the window is drawn
        UiSharedService.ApplyPendingWindowResize(ControlPanelTitle);
        SchedulePopoutGeometry();
        base.PreDraw();
    }

    public override void PostDraw()
    {
        // Dispose theme scope after drawing (pops the two vars pushed in
        // PreDraw · WindowPadding and WindowBorderSize · then the scope's own)
        ImGui.PopStyleVar(2);
        _themeScope?.Dispose();
        _themeScope = null;
        base.PostDraw();
    }

    private void OnModSharingTransferAvailable(PenumbraModTransferAvailableMessage message)
    {
        var hash = message.Notification.Hash;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        _pendingModSharingLock.Enter();
        try
        {
            _pendingModSharingHashes.Add(hash);
        }
        finally
        {
            _pendingModSharingLock.Exit();
        }
    }

#if IS_TEST_BUILD
    /// <summary>First-connect hub hint - dismissed permanently once the user
    /// opened the hub via the card or tapped the chip's close.</summary>
    private void MarkHubHintSeen()
    {
        if (_configService.Current.HubOnboardingHintSeen) return;
        _configService.Current.HubOnboardingHintSeen = true;
        _configService.Save();
    }

    private void DrawTestServerToggleButton()
    {
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Bug, null, null, null, null, ButtonStyleKeys.Compact_TestServer))
        {
            ToggleTestServerConnection();
        }

        var isEnabled = _configService.Current.UseTestServerOverride;
        var tooltip = isEnabled
            ? "Test Server: Enabled\nClick to switch back to the main server."
            : "Test Server: Disabled\nClick to switch to the test server.\nA disclaimer will be shown once.";
        SpheneUI.AttachTooltip(tooltip);
    }

    private void ToggleTestServerConnection()
    {
        var isEnabled = _configService.Current.UseTestServerOverride;
        if (isEnabled)
        {
            _configService.Current.UseTestServerOverride = false;
            _configService.Save();
            _ = _apiController.CreateConnectionsAsync();
            return;
        }

        if (_configService.Current.HasAcceptedTestServerDisclaimer)
        {
            EnableTestServerOverrideAndReconnect();
            return;
        }

        _testServerDisclaimerOpenedAt = DateTime.UtcNow;
        _showTestServerDisclaimerPopup = true;
        ImGui.OpenPopup(TestServerDisclaimerPopupName);
    }

    private void DrawTestServerDisclaimerPopup()
    {
        if (_showTestServerDisclaimerPopup)
        {
            ImGui.OpenPopup(TestServerDisclaimerPopupName);
        }

        using (SpheneUI.Theme.ContextMenuScope())
        {
            var isOpen = _showTestServerDisclaimerPopup;
            if (ImGui.BeginPopupModal(TestServerDisclaimerPopupName, ref isOpen,
                    SpheneUI.Popups.WindowFlags | ImGuiWindowFlags.NoTitleBar))
            {
                var elapsedSeconds = (DateTime.UtcNow - _testServerDisclaimerOpenedAt).TotalSeconds;
                var remainingSeconds = Math.Max(0.0, 20.0 - elapsedSeconds);
                var canConfirm = remainingSeconds <= 0.0;

                SpheneUI.Chrome.TitleBar("tsd", "Test Server Disclaimer",
                    FontAwesomeIcon.Flask, SpheneUI.Palette.SpheneGold,
                    rightButtons:
                    [
                        new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.Times, "Close", () => isOpen = false),
                    ]);
                SpheneUI.Text.Wrapped("You are about to connect to the Test Server.");

                ImGui.Spacing();
                SpheneUI.Layout.Callout(
                    "A new account is created automatically on the Test Server - anything you do there has no effect on your main server account.",
                    FontAwesomeIcon.ExclamationTriangle, SpheneTheme.Current.CompactServerStatusWarning, "Separate Account");
                SpheneUI.Layout.Callout(
                    "Found a bug? Please report it on Discord so we can fix it for everyone.",
                    FontAwesomeIcon.InfoCircle, SpheneUI.Palette.CrystalBlue, "Report Issues");

                ImGui.Spacing();
                if (SpheneBrandIcons.IconTextButton(SpheneBrandIcons.Brand.Discord, "Discord",
                        "tsd-discord", tooltip: "Open the Sphene Discord community",
                        height: 30f * SpheneUI.Scale))
                {
                    Util.OpenLink("https://discord.gg/GbnwsP2XsF");
                }

                ImGui.Spacing();
                if (!canConfirm)
                {
                    SpheneUI.Text.ColoredWrapped($"Please wait {Math.Ceiling(remainingSeconds)} seconds before confirming.", SpheneTheme.Current.CompactServerStatusWarning);
                }

                using (ImRaii.Disabled(!canConfirm))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "I Understand, Connect"))
                    {
                        _configService.Current.HasAcceptedTestServerDisclaimer = true;
                        _configService.Save();
                        EnableTestServerOverrideAndReconnect();
                        isOpen = false;
                    }
                }

                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
                {
                    isOpen = false;
                }

                SpheneUI.Layout.SetScaledWindowSize(430, 330);
                ImGui.EndPopup();
            }

            _showTestServerDisclaimerPopup = isOpen;
        }
    }

    private void EnableTestServerOverrideAndReconnect()
    {
        if (string.IsNullOrWhiteSpace(_configService.Current.TestServerApiUrl))
        {
            _configService.Current.TestServerApiUrl = "ws://test.sphene.online:6000";
        }

        _configService.Current.UseTestServerOverride = true;
        _configService.Save();
        _ = _apiController.CreateConnectionsAsync();
    }
#endif

    private void OnModSharingTransferCompleted(PenumbraModTransferCompletedMessage message)
    {
        var hash = message.Notification.Hash;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        _pendingModSharingLock.Enter();
        try
        {
            _pendingModSharingHashes.Remove(hash);
        }
        finally
        {
            _pendingModSharingLock.Exit();
        }
    }

    private void OnModSharingTransferDiscarded(PenumbraModTransferDiscardedMessage message)
    {
        var hash = message.Notification.Hash;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        _pendingModSharingLock.Enter();
        try
        {
            _pendingModSharingHashes.Remove(hash);
        }
        finally
        {
            _pendingModSharingLock.Exit();
        }
    }

    private void ClearPendingModSharing()
    {
        _pendingModSharingLock.Enter();
        try
        {
            _pendingModSharingHashes.Clear();
        }
        finally
        {
            _pendingModSharingLock.Exit();
        }
    }

    private int GetPendingModSharingCount()
    {
        _pendingModSharingLock.Enter();
        try
        {
            return _pendingModSharingHashes.Count;
        }
        finally
        {
            _pendingModSharingLock.Exit();
        }
    }

    protected override void DrawInternal()
    {
        // The profile popout widens this window on one side. The whole body
        // renders into a child region pinned to the pre-popout rect · the
        // grown strip is pure overlay, so existing content can never move
        // or reflow while the window animates.
        var popoutGrown = UpdatePopoutGrowth();

        var wpos = ImGui.GetWindowPos();
        var wsize = ImGui.GetWindowSize();

        // Surface, blur and ambience cover the grown window · the popout
        // strip shares the same chrome as the rest of the window
        var theme = SpheneTheme.Current;
        SpheneUI.Chrome.WindowBlur(theme.CompactEnableWindowBlur ? theme.CompactWindowBlurStrength : 0f);
        var surfaceDrawList = ImGui.GetWindowDrawList();
        var surfaceRounding = ImGui.GetStyle().WindowRounding;
        SpheneUI.Chrome.DrawWindowSurface(surfaceDrawList, wpos, wpos + wsize, surfaceRounding);

        var bodyMin = new Vector2(wpos.X + (_popoutLeft ? popoutGrown : 0f), wpos.Y);
        ImGui.SetCursorScreenPos(bodyMin);
        if (ImGui.BeginChild("##compactBody", wsize - new Vector2(popoutGrown, 0f), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground))
        {
            DrawInternalBody();
        }
        ImGui.EndChild();

        if (popoutGrown > 0.5f && _popoutPair != null)
        {
            DrawPopoutPanel(popoutGrown);
        }

        // Window border ring above all content · the flush title bar fill
        // reaches the window edge and would otherwise cover the ring
        var borderSize = SpheneTheme.Current.CompactWindowBorderSize;
        if (borderSize > 0f)
        {
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetWindowPos();
            drawList.AddRect(pos, pos + ImGui.GetWindowSize(),
                ImGui.GetColorU32(ImGuiCol.Border), ImGui.GetStyle().WindowRounding,
                ImDrawFlags.RoundCornersAll, borderSize);
        }
    }

    private void DrawInternalBody()
    {
        // The profile bar doubles as the title bar in custom-header mode
        var customHeader = !SpheneTheme.Current.CompactShowImGuiHeader;
        if (customHeader)
        {
            DrawProfileTitleBar();
        }
        var foldAmount = customHeader ? SpheneUI.Chrome.CollapseAmount(ProfileFoldId) : 0f;
        if (foldAmount >= 1f)
        {
            DrawHyperSyncPopups();
            return;
        }
        using var contentFade = ImRaii.PushStyle(ImGuiStyleVar.Alpha,
            ImGui.GetStyle().Alpha * (1f - MathF.Min(1f, foldAmount * 1.4f)), foldAmount > 0f);
        DrawHyperSyncPopups();

        if (!_apiController.IsCurrentVersion)
        {
            var ver = _apiController.CurrentClientVersion;
            var unsupported = "UNSUPPORTED VERSION";
            using (SpheneUI.UidFont.Push())
            {
                var uidTextSize = ImGui.CalcTextSize(unsupported);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(SpheneTheme.Current.CompactServerStatusWarning, unsupported);
            }
            SpheneUI.Text.ColoredWrapped($"Your Network client is outdated, current version is {ver.Major}.{ver.Minor}.{ver.Build}. " +
            $"Please update your client to maintain Network compatibility. Open /xlplugins and update the plugin.", SpheneTheme.Current.CompactServerStatusError);
        }

        if (!_ipcManager.Initialized)
        {
            var unsupported = "Limited functionality";

            using (SpheneUI.UidFont.Push())
            {
                var uidTextSize = ImGui.CalcTextSize(unsupported);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(SpheneTheme.Current.CompactServerStatusError, unsupported);
            }
            var penumAvailable = _ipcManager.Penumbra.APIAvailable;
            var glamAvailable = _ipcManager.Glamourer.APIAvailable;

            SpheneUI.Text.ColoredWrapped("Some features are currently unavailable. To enable full synchronization, please make sure these plugins are enabled and up to date:", SpheneTheme.Current.CompactServerStatusWarning);
            using var indent = ImRaii.PushIndent(10f);
            if (!penumAvailable)
            {
                SpheneUI.Text.Wrapped("Penumbra (required)");
                SpheneUI.Icons.BoolIcon(penumAvailable, true);
            }
            if (!glamAvailable)
            {
                SpheneUI.Text.Wrapped("Glamourer (required)");
                SpheneUI.Icons.BoolIcon(glamAvailable, true);
            }
            ImGui.Separator();
        }

        // Main content area
        // Network Identity & Status Section
        ImGui.Spacing();

        var style = ImGui.GetStyle();
        // With the custom header the profile lives in the title bar,
        // with the native ImGui header it stays inside the header card.
        DrawHeaderCard(style, withProfile: !customHeader);

        if (!_apiController.HasEverConnected
            && !_apiController.IsConnected
            && _apiController.ServerState is ServerState.Connecting or ServerState.Reconnecting or ServerState.Disconnected or ServerState.Offline)
        {
            if (_apiController.IsConnectionPaused)
            {
                DrawManuallyDisconnectedScreen();
                return;
            }

            ImGui.Spacing();
            using var disabled = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.85f);
            static void CenterDisabledLine(string text)
            {
                var startX = ImGui.GetCursorPosX();
                var avail = ImGui.GetContentRegionAvail().X;
                var size = ImGui.CalcTextSize(text);
                ImGui.SetCursorPosX(startX + MathF.Max(0, (avail - size.X) * 0.5f));
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(text);
            }

            static void CenterDisabledWrapped(string text)
            {
                var startX = ImGui.GetCursorPosX();
                var avail = ImGui.GetContentRegionAvail().X;
                if (avail <= 1)
                {
                    ImGui.TextDisabled(text);
                    return;
                }

                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    ImGui.TextDisabled(string.Empty);
                    return;
                }

                var current = string.Empty;
                foreach (var word in words)
                {
                    var candidate = string.IsNullOrEmpty(current) ? word : current + " " + word;
                    if (ImGui.CalcTextSize(candidate).X <= avail)
                    {
                        current = candidate;
                        continue;
                    }

                    if (!string.IsNullOrEmpty(current))
                    {
                        ImGui.SetCursorPosX(startX);
                        CenterDisabledLine(current);
                    }
                    current = word;
                }

                if (!string.IsNullOrEmpty(current))
                {
                    ImGui.SetCursorPosX(startX);
                    CenterDisabledLine(current);
                }
            }

            var status = _apiController.ServerState switch
            {
                ServerState.Offline => "Server is not reachable...",
                ServerState.Disconnected => "Unable to connect. Retrying...",
                ServerState.Reconnecting => "Connecting to server...",
                _ => "Connecting to server...",
            };
            var reason = "Sphene is trying to establish a connection. The server may be offline, restarting, or undergoing maintenance.";
            var elapsed = _apiController.InitialConnectElapsed;
            var elapsedText = elapsed.TotalSeconds >= 1 ? $"Elapsed: {elapsed.Minutes:00}:{elapsed.Seconds:00}" : "Elapsed: 00:00";

            ImGui.Separator();
            ImGui.Spacing();

            var title = "Connecting";
            var titleSize = ImGui.CalcTextSize(title);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) * 0.5f - titleSize.X * 0.5f);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, title);

            ImGui.Spacing();

            var centerX = (ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) * 0.5f;
            var spinnerSize = ImGui.GetTextLineHeight();
            ImGui.SetCursorPosX(centerX - spinnerSize * 0.5f);
            using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactServerStatusWarning))
            {
                DrawRotatingSpinnerIcon(1.0f);
            }

            using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactText))
            {
                CenterDisabledLine(status);
            }

            ImGuiHelpers.ScaledDummy(4f);
            CenterDisabledLine(elapsedText);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var contentMin = ImGui.GetWindowContentRegionMin().X;
            var contentMax = ImGui.GetWindowContentRegionMax().X;
            var contentWidth = contentMax - contentMin;
            var boxWidth = MathF.Min(contentWidth, 360f * ImGuiHelpers.GlobalScale);
            ImGui.SetCursorPosX(contentMin + MathF.Max(0, (contentWidth - boxWidth) * 0.5f));
            using (ImRaii.Child("initial-connect-hold-box", new Vector2(boxWidth, 0), false))
            {
                ImGui.Spacing();
                var headerPad = 4f * ImGuiHelpers.GlobalScale;
                var header = "What's happening";
                var headerSize = ImGui.CalcTextSize(header);
                var headerAvail = ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (headerAvail - headerSize.X) * 0.5f));
                ImGui.AlignTextToFramePadding();
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(headerPad, ImGui.GetStyle().FramePadding.Y)))
                using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactHeaderText))
                {
                    ImGui.TextUnformatted(header);
                }
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(6f);
                CenterDisabledWrapped(reason);

                ImGuiHelpers.ScaledDummy(10f);
                var showDiscordHint = elapsed.TotalSeconds >= 60;
                var hint = showDiscordHint
                    ? "If this takes longer than a minute, check Discord announcements for maintenance updates."
                    : "If this continues, it may be related to maintenance or a server restart.";
                var header2 = "What you can do";
                var header2Size = ImGui.CalcTextSize(header2);
                headerAvail = ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (headerAvail - header2Size.X) * 0.5f));
                ImGui.AlignTextToFramePadding();
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(headerPad, ImGui.GetStyle().FramePadding.Y)))
                using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactHeaderText))
                {
                    ImGui.TextUnformatted(header2);
                }
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(6f);
                CenterDisabledWrapped(hint);
            }
            return;
        }

        if (_apiController.HasEverConnected
            && !_apiController.IsConnected
            && _apiController.IsConnectionPaused
            && _apiController.ServerState is ServerState.Disconnected or ServerState.Offline)
        {
            DrawManuallyDisconnectedScreen();
            return;
        }

        var currentServer = _serverManager.CurrentServer;
        bool isCharConfigured = true;
        string youName = string.Empty;
        uint youWorld = 0;
        if (currentServer != null)
        {
            youName = _dalamudUtilService.GetPlayerName();
            youWorld = _dalamudUtilService.GetHomeWorldId();
            isCharConfigured = currentServer.Authentications.Exists(a =>
                string.Equals(a.CharacterName, youName, StringComparison.Ordinal) && a.WorldId == youWorld);
        }
        bool showUnconfiguredSetup = _apiController.DisplayServerState == ServerState.NoAutoLogon && !isCharConfigured;

        // 4. Error Text (if any) - Displayed below the header block
        if (_apiController.DisplayServerState != ServerState.Connected && !showUnconfiguredSetup)
        {
             ImGui.Spacing();
             if (_apiController.IsPostGraceUiHoldActive)
             {
                 // Avoid duplicate messaging; the recovery screen below has a more detailed and centered presentation
             }
             else if (_apiController.DisplayServerState == ServerState.Reconnecting && _apiController.IsTransientDisconnectInProgress)
             {
                 var dots = (int)(ImGui.GetTime() * 2.0) % 4;
                 var text = "Reconnecting" + new string('.', dots);
                 var textSize = ImGui.CalcTextSize(text);
                 var avail = ImGui.GetContentRegionAvail().X;
                 ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (avail - textSize.X) * 0.5f));
                 ImGui.AlignTextToFramePadding();
                 ImGui.TextDisabled(text);
             }
             else
             {
                 SpheneUI.Text.ColoredWrapped(GetServerError(), GetServerStatusColor());
             }
        }

        // Hard gate: if we are in post-grace UI hold, show loading and skip building the list entirely this frame
        if (_apiController.IsPostGraceUiHoldActive)
        {
            ImGui.Spacing();
            using var disabled = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.85f);
            static void CenterDisabledLine(string text)
            {
                var startX = ImGui.GetCursorPosX();
                var avail = ImGui.GetContentRegionAvail().X;
                var size = ImGui.CalcTextSize(text);
                if (size.X <= avail)
                {
                    ImGui.SetCursorPosX(startX + MathF.Max(0, (avail - size.X) * 0.5f));
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled(text);
                    return;
                }

                ImGui.SetCursorPosX(startX);
                ImGui.PushStyleColor(ImGuiCol.Text, SpheneTheme.Current.CompactTextSecondary);
                ImGui.PushTextWrapPos(startX + avail);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.PopStyleColor();
            }
            
            static void CenterDisabledWrapped(string text)
            {
                var startX = ImGui.GetCursorPosX();
                var avail = ImGui.GetContentRegionAvail().X;
                if (avail <= 1)
                {
                    ImGui.TextDisabled(text);
                    return;
                }

                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    ImGui.TextDisabled(string.Empty);
                    return;
                }

                var current = string.Empty;
                foreach (var word in words)
                {
                    var candidate = string.IsNullOrEmpty(current) ? word : current + " " + word;
                    if (ImGui.CalcTextSize(candidate).X <= avail)
                    {
                        current = candidate;
                        continue;
                    }

                    if (!string.IsNullOrEmpty(current))
                    {
                        ImGui.SetCursorPosX(startX);
                        CenterDisabledLine(current);
                    }
                    current = word;
                }

                if (!string.IsNullOrEmpty(current))
                {
                    ImGui.SetCursorPosX(startX);
                    CenterDisabledLine(current);
                }
            }

            var stage = _apiController.PostGraceUiHoldStageValue;
            var status = stage switch
            {
                2 => "Connected. Reloading required data...",
                3 => "Refreshing connection...",
                _ => "Trying to reconnect...",
            };
            var reason = string.IsNullOrWhiteSpace(_apiController.PostGraceUiHoldReason)
                ? "Connection issues detected. Recovering session state."
                : _apiController.PostGraceUiHoldReason;
            var sub = stage switch
            {
                2 => "Loading required data from the server.",
                3 => "Finalizing sync.",
                _ => string.Empty,
            };
            var elapsed = _apiController.PostGraceUiHoldElapsed;
            var elapsedText = elapsed.TotalSeconds >= 1 ? $"Elapsed: {elapsed.Minutes:00}:{elapsed.Seconds:00}" : "Elapsed: 00:00";

            ImGui.Separator();
            ImGui.Spacing();

            var title = "Connection recovery";
            var titleSize = ImGui.CalcTextSize(title);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) * 0.5f - titleSize.X * 0.5f);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, title);

            ImGui.Spacing();

            var centerX = (ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) * 0.5f;
            var spinnerSize = ImGui.GetTextLineHeight();
            ImGui.SetCursorPosX(centerX - spinnerSize * 0.5f);
            using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactServerStatusWarning))
            {
                DrawRotatingSpinnerIcon(1.0f);
            }

            using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactText))
            {
                CenterDisabledLine(status);
            }

            ImGuiHelpers.ScaledDummy(4f);
            CenterDisabledLine(elapsedText);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var contentMin = ImGui.GetWindowContentRegionMin().X;
            var contentMax = ImGui.GetWindowContentRegionMax().X;
            var contentWidth = contentMax - contentMin;
            var boxWidth = MathF.Min(contentWidth, 360f * ImGuiHelpers.GlobalScale);
            ImGui.SetCursorPosX(contentMin + MathF.Max(0, (contentWidth - boxWidth) * 0.5f));
            using (ImRaii.Child("post-grace-hold-box", new Vector2(boxWidth, 0), false))
            {
                ImGui.Spacing();
                var headerPad = 4f * ImGuiHelpers.GlobalScale;
                var header1 = "What's happening";
                var header1Size = ImGui.CalcTextSize(header1);
                var headerAvail = ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (headerAvail - header1Size.X) * 0.5f));
                ImGui.AlignTextToFramePadding();
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(headerPad, ImGui.GetStyle().FramePadding.Y)))
                using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactHeaderText))
                {
                    ImGui.TextUnformatted(header1);
                }
                ImGui.Separator();
                ImGui.Spacing();
                CenterDisabledWrapped(reason);
                ImGuiHelpers.ScaledDummy(6f);
                if (!string.IsNullOrWhiteSpace(sub))
                {
                    CenterDisabledWrapped(sub);
                }

                ImGuiHelpers.ScaledDummy(10f);
                var showDiscordHint = elapsed.TotalSeconds >= 30;
                var hint = showDiscordHint
                    ? "If this takes longer than a minute, check Discord announcements for maintenance updates."
                    : "If this continues, it may be related to maintenance or a server restart.";
                ImGuiHelpers.ScaledDummy(4f);
                var header2 = "What you can do";
                var header2Size = ImGui.CalcTextSize(header2);
                headerAvail = ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (headerAvail - header2Size.X) * 0.5f));
                ImGui.AlignTextToFramePadding();
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(headerPad, ImGui.GetStyle().FramePadding.Y)))
                using (ImRaii.PushColor(ImGuiCol.Text, SpheneTheme.Current.CompactHeaderText))
                {
                    ImGui.TextUnformatted(header2);
                }
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(6f);
                CenterDisabledWrapped(hint);
                if (ImGui.IsItemHovered())
                {
                    using (SpheneUI.Theme.TooltipScope())
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted("Tip");
                        ImGui.Separator();
                        ImGui.TextWrapped("If your community runs this server, maintenance notices are typically posted in a Discord #announcements channel.");
                        ImGui.EndTooltip();
                    }
                }
            }
            return;
        }
        
        
        if (_apiController.DisplayServerState is ServerState.Connected || showUnconfiguredSetup)
        {            
            // Navigation / Buttons
            _tabMenu.Draw(GetPendingModSharingCount(), false);

            if (!isCharConfigured && currentServer != null)
            {
                // Character not configured: show setup block in place of the pair list
                // Note: rename/world changes are handled automatically by CharacterIdentityService,
                // so only the "completely new character" case needs UI here.

                var worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)youWorld, out var wn) ? wn : youWorld.ToString();
                var charDisplay = $"{youName} @ {worldName}";

                static void CenterLine(string text, Vector4 color)
                {
                    var size = ImGui.CalcTextSize(text).X;
                    var avail = ImGui.GetContentRegionAvail().X;
                    ImGui.SetCursorPosX(MathF.Max(0, (avail - size) / 2));
                    SpheneUI.Text.Colored(text, color);
                }

                CenterLine("Your Character", SpheneTheme.Current.CompactHeaderText);
                ImGuiHelpers.ScaledDummy(2);
                ImGui.Separator();
                CenterLine(charDisplay, SpheneTheme.Current.CompactText);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(2);
                CenterLine("is not yet synced to Sphene.", SpheneTheme.Current.CompactHeaderText);
                CenterLine("Add it below to start syncing.", SpheneTheme.Current.CompactHeaderText);
                ImGuiHelpers.ScaledDummy(5);
                ImGui.Separator();

                if (!currentServer.UseOAuth2 && currentServer.SecretKeys.Any())
                {
                    var keys = currentServer.SecretKeys.OrderBy(u => u.Key).ToDictionary(k => k.Key, k => k.Value);
                    var initialKey = keys.ContainsKey(_selectedSecretKeyForNewCharacter)
                        ? keys.First(f => f.Key == _selectedSecretKeyForNewCharacter)
                        : keys.First();
                    if (_selectedSecretKeyForNewCharacter == -1 || !keys.ContainsKey(_selectedSecretKeyForNewCharacter))
                    {
                        _selectedSecretKeyForNewCharacter = initialKey.Key;
                    }
                    CenterLine("Secret Key:", SpheneTheme.Current.CompactTextSecondary);
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    SpheneUI.Controls.Combo("##compactNewCharKey", keys,
                        (w) => w.Value.FriendlyName,
                        (w) => { _selectedSecretKeyForNewCharacter = w.Key; },
                        initialKey);
                    ImGuiHelpers.ScaledDummy(3);
                    CenterLine("or enter a new key:", SpheneTheme.Current.CompactTextSecondary);
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    ImGui.InputTextWithHint("##customSecretKey", "enter secret key here", ref _customSecretKeyInput, 255);
                    ImGuiHelpers.ScaledDummy(3);
                }

                var btnWidth2 = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.User, "Add current character");
                var availWidthBtn = ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(MathF.Max(0, (availWidthBtn - btnWidth2) / 2));
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.User, "Add current character"))
                {
                    int keyToUse = -1;
                    if (!currentServer.UseOAuth2)
                    {
                        if (!string.IsNullOrWhiteSpace(_customSecretKeyInput))
                        {
                            var friendlyName = _customSecretKeyInput.Length > 10
                                ? _customSecretKeyInput[..10] + "..."
                                : _customSecretKeyInput;
                            keyToUse = _serverManager.AddSecretKey(-1, _customSecretKeyInput.Trim(), friendlyName);
                            _customSecretKeyInput = string.Empty;
                        }
                        else
                        {
                            keyToUse = _selectedSecretKeyForNewCharacter;
                        }
                    }
                    _serverManager.AddCurrentCharacterToServer(-1, keyToUse);
                    _ = _apiController.CreateConnectionsAsync();
                }
            }
            else
            {
                // Connected Pairs Section
                var onlineCount = _pairManager.DirectPairs.Count(p => !p.IsEffectivelyOffline);
                var totalCount = _pairManager.DirectPairs.Count;
                var onlineText = $"Connected Pairs · {onlineCount}/{totalCount} online";
                DrawSectionDivider(onlineText);
                
                using (ImRaii.PushId("pairlist")) DrawPairs();
                
                float pairlistEnd = ImGui.GetCursorPosY();
                using (ImRaii.PushIndent(SpheneTheme.Current.CompactPairListContentPadding))
                    using (ImRaii.PushId("transfers")) DrawTransfers();
                // reserve exactly the consumed height · the trailing item
                // spacing is dead space the list may reuse
                _transferPartHeight = ImGui.GetCursorPosY() - pairlistEnd
                    - ImGui.GetStyle().ItemSpacing.Y;
                using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
                using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
            }
        }
        
        // Draw update notification at bottom if available or forced by theme
        if (Sphene.UI.Design.SpheneTheme.Current.ForceShowUpdateHint || _updateBannerInfo?.IsUpdateAvailable == true)
        {
            using (ImRaii.PushId("update-hint-footer"))
            {
                var hintTheme = Sphene.UI.Design.SpheneTheme.Current;
                var accent = hintTheme.CompactUpdateHintColor;
                var lineH = ImGui.GetTextLineHeight();
                var h = MathF.Max(hintTheme.CompactUpdateHintHeight, lineH + SpheneUI.Spacing.X4.Scaled);
                var pad = hintTheme.CompactUpdateHintPaddingY;
                var drawList = ImGui.GetWindowDrawList();

                using (ImRaii.PushIndent(hintTheme.CompactPairListContentPadding))
                {
                    var bandMin = ImGui.GetCursorScreenPos()
                        + new Vector2(0f, pad);
                    var bandW = MathF.Max(1f, SpheneUI.Controls.BoundedAvailX()
                        - hintTheme.CompactPairListContentPadding);
                    var bandMax = bandMin + new Vector2(bandW, h);
                    var bandR = MathF.Min(h * 0.5f, hintTheme.CompactChildRounding);

                    // whole band is the button
                    ImGui.SetCursorScreenPos(bandMin);
                    var clicked = ImGui.InvisibleButton("##updatehintband",
                        new Vector2(bandW, h));
                    var bandHover = ImGui.IsItemHovered();
                    if (bandHover) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (clicked)
                    {
                        _logger.LogDebug("Update details button clicked, toggling UpdateNotificationUi");
                        Mediator.Publish(new UiToggleMessage(typeof(UpdateNotificationUi)));
                    }

                    drawList.AddRectFilled(bandMin, bandMax,
                        SpheneUI.ColorUtils.ToU32(
                            SpheneUI.ColorUtils.WithAlpha(accent, bandHover ? 0.16f : 0.10f)), bandR);
                    drawList.AddRect(bandMin, bandMax,
                        SpheneUI.ColorUtils.ToU32(
                            SpheneUI.ColorUtils.WithAlpha(accent, bandHover ? 0.45f : 0.28f)), bandR);

                    var contentY = bandMin.Y + MathF.Max(0f, (h - lineH) * 0.5f);
                    var iconX = bandMin.X + SpheneUI.Spacing.X8.Scaled;
                    ImGui.SetCursorScreenPos(new Vector2(iconX, contentY));
                    SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle, accent);
                    var iconW = SpheneUI.Icons.Size(FontAwesomeIcon.InfoCircle).X;

                    var hintIconW = SpheneUI.Icons.Size(FontAwesomeIcon.Download).X;
                    var hintTextW = ImGui.CalcTextSize("Open Details").X;
                    var hintW = hintIconW + SpheneUI.Spacing.Inline.Scaled + hintTextW;

                    var updateText = _updateBannerInfo?.LatestVersion != null
                        ? _updateBannerInfo.IsTestBuildUpdate
                            ? $"Testbuild update available: {_updateBannerInfo.LatestVersion}"
                            : $"Update available: {_updateBannerInfo.LatestVersion}"
                        : "Update available";

                    var textX = iconX + iconW + SpheneUI.Spacing.Inline.Scaled;
                    var textMaxX = bandMax.X - SpheneUI.Spacing.X8.Scaled - hintW;
                    ImGui.SetCursorScreenPos(new Vector2(textX, contentY));
                    SpheneUI.Text.Colored(
                        SpheneUI.Text.Ellipsize(updateText, MathF.Max(0f, textMaxX - textX)),
                        accent);

                    var hintX = bandMax.X - SpheneUI.Spacing.X8.Scaled - hintW;
                    ImGui.SetCursorScreenPos(new Vector2(hintX, contentY));
                    SpheneUI.Icons.Icon(FontAwesomeIcon.Download, accent);
                    ImGui.SameLine(0, SpheneUI.Spacing.Inline.Scaled);
                    SpheneUI.Text.Colored("Open Details", accent);

                    ImGui.SetCursorScreenPos(new Vector2(bandMin.X, bandMax.Y + pad));
                }
            }
        }
        
        if (_configService.Current.OpenPopupOnAdd && _pairManager.LastAddedUser != null)
        {
            _lastAddedUser = _pairManager.LastAddedUser;
            _pairManager.LastAddedUser = null;
            ImGui.OpenPopup("Set Notes for New User");
            _showModalForUserAddition = true;
            _lastAddedUserComment = string.Empty;
        }

        using (SpheneUI.Theme.ContextMenuScope())
        {
            if (ImGui.BeginPopupModal("Set Notes for New User", ref _showModalForUserAddition, SpheneUI.Popups.WindowFlags))
            {
                if (_lastAddedUser == null)
                {
                    _showModalForUserAddition = false;
                }
                else
                {
                    SpheneUI.Text.Wrapped($"You have successfully added {_lastAddedUser.UserData.PreferredName}. Set a local note for the user in the field below:");
                    ImGui.InputTextWithHint("##noteforuser", $"Note for {_lastAddedUser.UserData.PreferredName}", ref _lastAddedUserComment, 100);
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Save Note"))
                    {
                        _serverManager.SetNoteForUid(_lastAddedUser.UserData.UID, _lastAddedUserComment);
                        _lastAddedUser = null;
                        _lastAddedUserComment = string.Empty;
                        _showModalForUserAddition = false;
                    }
                }
                SpheneUI.Layout.SetScaledWindowSize(275);
                ImGui.EndPopup();
            }
        }

#if IS_TEST_BUILD
        DrawTestServerDisclaimerPopup();
#endif


        // Track window size changes for mediator notifications
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        // Apply width correction once: if persisted width is below 370 (unscaled), bump to 371
        if (!_widthCorrectionChecked)
        {
            try
            {
                var unscaledWidth = size.X / ImGuiHelpers.GlobalScale;
                if (unscaledWidth < 370f)
                {
                    var correctedWidth = 370f * ImGuiHelpers.GlobalScale;
                    ImGui.SetWindowSize(new Vector2(correctedWidth, size.Y));
                    size = new Vector2(correctedWidth, size.Y);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to apply width correction for CompactUI window");
            }
            finally { _widthCorrectionChecked = true; }
        }
        
        // Separate handling for size and position changes
        var sizeChanged = _lastSize != size;
        var positionChanged = !_stickEnabled && _lastPosition != pos;
        
        if (sizeChanged || positionChanged)
        {
            _lastSize = size;
            if (!_stickEnabled)
                _lastPosition = pos;

            // publish the live rect · the tracked position freezes while stuck
            Mediator.Publish(new CompactUiChange(size, pos));
        }
    }

    /// <summary>
    /// Muted centered section label flanked by two hairlines matching the
    /// separator color · the quiet divider look used above the pair list.
    /// </summary>
    private static void DrawSectionDivider(string text)
    {
        var drawList = ImGui.GetWindowDrawList();
        var textSize = ImGui.CalcTextSize(text);
        var avail = ImGui.GetContentRegionAvail().X;
        var gap = SpheneUI.Spacing.X8.Scaled;
        var minWing = 20f * SpheneUI.Scale;
        var cursor = ImGui.GetCursorScreenPos();
        var lineY = cursor.Y + textSize.Y * 0.5f;
        var wing = MathF.Max(0f, (avail - textSize.X - gap * 2f) * 0.5f);
        if (wing > minWing)
        {
            var sep = ImGui.GetColorU32(ImGuiCol.Separator);
            drawList.AddLine(new Vector2(cursor.X, lineY),
                new Vector2(cursor.X + wing, lineY), sep);
            drawList.AddLine(new Vector2(cursor.X + avail - wing, lineY),
                new Vector2(cursor.X + avail, lineY), sep);
        }
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - textSize.X) * 0.5f));
        SpheneUI.Text.Muted(text);
    }

    private void DrawPairs()
    {
        // Reserve additional space for update notification if available
        var updateNotificationHeight =
            (Sphene.UI.Design.SpheneTheme.Current.ForceShowUpdateHint || _updateBannerInfo?.IsUpdateAvailable == true)
                ? Sphene.UI.Design.SpheneTheme.Current.CompactUpdateHintHeight
                    + Sphene.UI.Design.SpheneTheme.Current.CompactUpdateHintPaddingY * 2
                    + ImGui.GetStyle().ItemSpacing.Y
                : 0f;

        var style = ImGui.GetStyle();
        // Transfer height can drift negative when nothing is drawn; never grow past the window edge
        var footerReserved = MathF.Max(0f, _transferPartHeight) + updateNotificationHeight;
        var windowSize = ImGui.GetWindowSize();
        // Reach the inner window edge; when a footer follows, keep window padding below the list
        var ySize = Math.Abs(_transferPartHeight) < 0.0001f
            ? 1
            : MathF.Max(1f, windowSize.Y - ImGui.GetCursorPosY()
                - style.WindowBorderSize
                - footerReserved
                - (footerReserved > 0f ? style.WindowPadding.Y : 0f));

        // The list content insets by the configured horizontal padding: the
        // child stops short on the right so button docks gain a real margin,
        // and the indent carries the left side.
        var listPadX = SpheneTheme.Current.CompactPairListContentPadding;
        var xSize = MathF.Max(1f, ImGui.GetContentRegionAvail().X - listPadX);
        if (SpheneUI.Layout.BeginScrollArea("list", new Vector2(xSize, ySize),
                noPadding: true, contentInset: listPadX))
        {
            foreach (var item in _drawFolders)
            {
                item.Draw();
            }
        }
        // The fade always spans the window's inner edge; only the separators
        // get an adjustable padding. WindowPadding must not affect it.
        SpheneUI.Layout.EndScrollArea(edgePaddingX: 0f);
    }
    



    private void DrawModeControls()
    {
        ImGui.BeginGroup();
        ImGui.TextColored(SpheneTheme.Current.CompactTextSecondary, "Mode:");
        ImGui.SameLine();
        var currentTime = DateTime.UtcNow;
        var timeSinceLastClick = currentTime - _lastIncognitoButtonClick;
        var isButtonDisabled = timeSinceLastClick.TotalSeconds < 5.0;
        if (_isIncognitoModeActive)
        {
            var resumeColor = isButtonDisabled ? SpheneTheme.Current.CompactTextSecondary : SpheneTheme.Current.CompactServerStatusConnected;
            ImGui.PushStyleColor(ImGuiCol.Text, resumeColor);
            using (ImRaii.Disabled(isButtonDisabled))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Play, null, null, null, null, ButtonStyleKeys.Compact_IncognitoOff))
                {
                    _lastIncognitoButtonClick = currentTime;
                    _ = Task.Run(() => HandleIncognitoModeToggle());
                }
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                var tooltipText = "Resume Normal Mode\nUnpause all pairs and reconnect syncshells";
                if (isButtonDisabled)
                {
                    var remainingSeconds = Math.Ceiling(5.0 - timeSinceLastClick.TotalSeconds);
                    tooltipText += $"\nCooldown: {remainingSeconds} seconds remaining";
                }
                SpheneUI.AttachTooltip(tooltipText);
            }
            ImGui.SameLine();
            ImGui.TextColored(SpheneTheme.Current.CompactServerStatusConnected, "Incognito");
        }
        else
        {
            var heartColor = isButtonDisabled ? SpheneTheme.Current.CompactTextSecondary : SpheneTheme.Current.CompactServerStatusError;
            ImGui.PushStyleColor(ImGuiCol.Text, heartColor);
            using (ImRaii.Disabled(isButtonDisabled))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Heart, null, null, null, null, ButtonStyleKeys.Compact_IncognitoOn))
                {
                    _lastIncognitoButtonClick = currentTime;
                    _ = Task.Run(() => HandleIncognitoModeToggle());
                }
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                var tooltipText = "Enter Incognito Mode\nPause all pairs and syncshells except party members";
                if (isButtonDisabled)
                {
                    var remainingSeconds = Math.Ceiling(5.0 - timeSinceLastClick.TotalSeconds);
                    tooltipText += $"\nCooldown: {remainingSeconds} seconds remaining";
                }
                SpheneUI.AttachTooltip(tooltipText);
            }
            ImGui.SameLine();
            ImGui.TextColored(SpheneTheme.Current.CompactText, "Normal");
        }
        ImGui.EndGroup();
    }

    private void DrawTextureManagement()
    {
        // docks to the same inset the card padding keeps on the left
        var targetRightEdge = ImGui.GetWindowContentRegionMax().X - HeaderContentPadX;

        var conversionButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowsToEye);
        var buttonWidth = conversionButtonSize.X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var textureLabelWidth = ImGui.CalcTextSize("VRAM:").X;
        var textureSizeBytes = GetCurrentTextureSize();
        var textureSizeMB = textureSizeBytes / (1024.0 * 1024.0);
        var textureIndicatorText = $"{textureSizeMB:F0}MB";
        
        // Measure the icon width accurately based on the current indicator
        var (_, _, icon) = GetBc7ConversionIndicator();
        // If spinner is active, it uses LineHeight. If icon, measure it.
        var iconWidth = _shrinkuConfigService.Current.TextureProcessingMode == TextureProcessingMode.Automatic && _shrinkuConversionService.IsConverting
            ? ImGui.GetTextLineHeight()
            : UiBuilder.IconFont.GetCharAdvance((ushort)icon); 
            
        var textWidth = ImGui.CalcTextSize(textureIndicatorText).X;
        var textureIndicatorWidth = iconWidth + textWidth + 2f; // +2f for the SameLine(0, 2)
        var totalRightContentWidth = textureLabelWidth + buttonWidth + textureIndicatorWidth + (spacing * 2);
        
        var startX = targetRightEdge - totalRightContentWidth;
        
        // Ensure we don't overlap with left content
        if (startX < ImGui.GetCursorPosX()) startX = ImGui.GetCursorPosX() + spacing;
        
        ImGui.SameLine(startX);
        ImGui.BeginGroup();
        ImGui.TextColored(SpheneTheme.Current.CompactTextSecondary, "VRAM:");
        ImGui.SameLine();
        using (ImRaii.Disabled(_shrinkuConversionService.IsConverting))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowsToEye, null, null, null, null, ButtonStyleKeys.Compact_Conversion) && !_shrinkuConversionService.IsConverting)
            {
                Mediator.Publish(new OpenTextureConversionMessage());
            }
        }
        SpheneUI.AttachTooltip(_shrinkuConversionService.IsConverting
            ? "Texture Conversion\nDisabled while background conversion is running"
            : "Texture Conversion\nOptimize and convert textures to BC7 format");
        ImGui.SameLine();
        DrawCompactTextureIndicator();
        ImGui.EndGroup();
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.CurrentUploads
            .Where(u => u != null && !string.IsNullOrWhiteSpace(u.Hash) && !_fileTransferManager.IsPenumbraModUpload(u.Hash))
            .ToList();
        var currentDownloads = _currentDownloads.Values
            .Where(d => d != null)
            .SelectMany(d => d.Values)
            .Where(v => v != null)
            .ToList();
        var theme = Sphene.UI.Design.SpheneTheme.Current;
        var drewRow = false;

        // Only show upload progress if there are active uploads
        if (currentUploads.Any())
        {
            var totalUploads = currentUploads.Count;
            var doneUploads = currentUploads.Count(c => c.IsTransferred);
            var totalUploaded = currentUploads.Sum(c => c.Transferred);
            var totalToUpload = currentUploads.Sum(c => c.Total);

            var uploadProgress = totalToUpload > 0 ? (float)totalUploaded / totalToUpload : 0f;
            var uploadText = $"{doneUploads}/{totalUploads} ({UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)})";
            var uploadRate = FormatTransferRate(_uploadRateMeter.Sample(totalUploaded));

            DrawTransferRow(FontAwesomeIcon.Upload, uploadProgress, uploadText, uploadRate, true);
            drewRow = true;
        }
        else _uploadRateMeter.Reset();

        // Only show download progress if there are active downloads
        if (currentDownloads.Any())
        {
            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);

            var downloadProgress = totalToDownload > 0 ? (float)totalDownloaded / totalToDownload : 0f;
            var downloadText = $"{doneDownloads}/{totalDownloads} ({UiSharedService.ByteToString(totalDownloaded)}/{UiSharedService.ByteToString(totalToDownload)})";
            var downloadRate = FormatTransferRate(_downloadRateMeter.Sample(totalDownloaded));

            DrawTransferRow(FontAwesomeIcon.Download, downloadProgress, downloadText, downloadRate, false);
            drewRow = true;
        }
        else _downloadRateMeter.Reset();

        // Show transmission preview in-place when enabled
        if (theme.ShowTransmissionPreview)
        {
            if (!currentUploads.Any())
            {
                DrawTransferRow(FontAwesomeIcon.Upload,
                    theme.TransmissionPreviewUploadFill / 100.0f, null, null, true);
                drewRow = true;
            }

            if (!currentDownloads.Any())
            {
                DrawTransferRow(FontAwesomeIcon.Download,
                    theme.TransmissionPreviewDownloadFill / 100.0f, null, null, false);
                drewRow = true;
            }
        }

        // keep the last bar off the card's bottom edge
        if (drewRow)
            ImGui.Dummy(new Vector2(1f, SpheneUI.Spacing.X8.Scaled));
    }

    /// <summary>EMA-smoothed byte-rate tracker; re-seeds whenever the transfer
    /// set changes so stale totals can't produce a spike.</summary>
    private sealed class TransferRateMeter
    {
        private const double MinSampleInterval = 0.1;
        private const double SmoothingPerSecond = 4.0;
        private long _lastTotal;
        private double _lastTime = -1.0;

        public float Rate { get; private set; }

        public float Sample(long totalBytes)
        {
            var now = ImGui.GetTime();
            if (_lastTime < 0.0)
            {
                _lastTime = now;
                _lastTotal = totalBytes;
                return Rate;
            }

            var dt = now - _lastTime;
            if (dt < MinSampleInterval) return Rate;
            var instant = MathF.Max(0f, (float)((totalBytes - _lastTotal) / dt));
            Rate += (instant - Rate) * MathF.Min(1f, (float)(dt * SmoothingPerSecond));
            _lastTotal = totalBytes;
            _lastTime = now;
            return Rate;
        }

        public void Reset()
        {
            _lastTime = -1.0;
            _lastTotal = 0;
            Rate = 0f;
        }
    }

    private static string? FormatTransferRate(float bytesPerSecond)
        => bytesPerSecond > 512f
            ? $"{UiSharedService.ByteToString((long)bytesPerSecond)}/s"
            : null;

    private static void DrawTransferRow(FontAwesomeIcon icon, float progress,
        string? detail, string? rateText, bool isUpload)
    {
        var theme = Sphene.UI.Design.SpheneTheme.Current;
        var barH = MathF.Max(2f, theme.SeparateTransmissionBarStyles
            ? (isUpload ? theme.UploadTransmissionBarHeight : theme.DownloadTransmissionBarHeight)
            : theme.CompactTransmissionBarHeight);
        var lineH = ImGui.GetTextLineHeight();
        progress = Math.Clamp(progress, 0f, 1f);

        // the glyph carries direction · icon column beside the bar, no label line
        var iconH = lineH;
        var rowH = MathF.Max(barH, iconH);
        var gap = SpheneUI.Spacing.X8.Scaled;
        // Indent only pulls the left edge in · subtract the list padding again
        // for the right bound so the bar aligns with pair rows
        var availX = SpheneUI.Controls.BoundedAvailX()
            - theme.CompactPairListContentPadding;

        var pct = $"{progress * 100f:F0}%";
        // rate and percent live beside the bar so they stay visible at any
        // bar height · the in-bar overlay only carries the byte detail
        var sideText = rateText != null ? $"{rateText} · {pct}" : pct;
        var sideW = ImGui.CalcTextSize(sideText).X;
        var overlay = barH >= lineH ? detail : null;

        var barW = MathF.Max(2f, (theme.AutoTransmissionBarWidth
            ? MathF.Max(50f, availX)
            : MathF.Min(theme.CompactTransmissionBarWidth, availX))
            - iconH - gap - gap - sideW);

        var rowMin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(iconH + gap + barW + gap + sideW, rowH));

        var lone = detail ?? rateText;
        var tip = detail != null && rateText != null
            ? $"{detail} · {rateText} · {pct}"
            : lone != null ? $"{lone} · {pct}" : pct;
        SpheneUI.AttachTooltip(tip);

        ImGui.SetCursorScreenPos(new Vector2(rowMin.X, rowMin.Y + (rowH - iconH) * 0.5f));
        SpheneUI.Icons.Icon(icon, theme.CompactTextSecondary);
        ImGui.SetCursorScreenPos(new Vector2(rowMin.X + iconH + gap,
            rowMin.Y + (rowH - barH) * 0.5f));
        SpheneUI.Controls.TransmissionBar(progress, new Vector2(barW, barH), overlay, isUpload);
        ImGui.SetCursorScreenPos(new Vector2(rowMin.X + iconH + gap + barW + gap,
            rowMin.Y + (rowH - lineH) * 0.5f));
        SpheneUI.Text.Muted(sideText);

        // TransmissionBar advances the cursor itself · restore the row bottom
        ImGui.SetCursorScreenPos(new Vector2(rowMin.X, rowMin.Y + rowH));
    }

    /// <summary>
    /// Advances the popout animation and schedules the window's geometry via
    /// SetNextWindowPos/Size so BOTH axes land atomically in the same Begin  · 
    /// exactly how the fold drives window.Position/Size in PreDraw. Geometry
    /// must never be written mid-frame: SetWindowPos applies immediately but
    /// SetWindowSize defers one frame, splitting the axes slides the content.
    /// Runs in PreDraw; the applied rect is reconstructed from the tracked
    /// <see cref="_lastPosition"/>/<see cref="_lastSize"/> minus the grown
    /// value carried by the previous frame · drags self-correct.
    /// </summary>
    private void SchedulePopoutGeometry()
    {
        var wantOpen = PopoutWantsOpen();
        var active = wantOpen || _popoutOpenT > 0f || _popoutGrown > 0.001f || _popoutSideDecided;
        if (!active || _lastSize.X <= 1f)
        {
            return;
        }

        var dt = ImGui.GetIO().DeltaTime;
        _popoutOpenT = Math.Clamp(_popoutOpenT + (wantOpen ? dt : -dt) / PopoutSlideSeconds, 0f, 1f);

        // pick the side once, while the strip still has zero width · sticking
        // owns the position, so only right growth is possible then
        if (!_popoutSideDecided)
        {
            var workPos = ImGui.GetMainViewport().WorkPos;
            var workMax = workPos + ImGui.GetMainViewport().WorkSize;
            var panelW = PopoutPanelWidth * SpheneUI.Scale;
            var fitsRight = _lastPosition.X + _lastSize.X + panelW <= workMax.X;
            var fitsLeft = !_stickEnabled && _lastPosition.X - panelW >= workPos.X;
            _popoutLeft = !_stickEnabled && (_configService.Current.ProfilePopoutRight
                ? !fitsRight && fitsLeft
                : fitsLeft || !fitsRight);
            _popoutSideDecided = true;
        }

        var eased = _popoutOpenT * _popoutOpenT * (3f - 2f * _popoutOpenT);
        // ImGui floors window pos/size to whole pixels · schedule the strip as
        // an integer so the applied rect is exact: the body lands back on
        // baseX with zero sub-pixel drift and the base stays self-consistent
        // frame over frame (a fractional grown would ratchet the base left).
        var grown = MathF.Ceiling(PopoutPanelWidth * SpheneUI.Scale * eased);
        // _lastPosition/_lastSize are captured inside the body child · they ARE
        // the pinned base rect, the strip never leaks into them
        var baseW = MathF.Round(_lastSize.X);
        var baseX = MathF.Round(_lastPosition.X);
        var baseY = MathF.Round(_lastPosition.Y);
        if (_popoutLeft)
        {
            ImGui.SetNextWindowPos(new Vector2(baseX - grown, baseY), ImGuiCond.Always);
        }
        var nextSizeX = baseW + grown;
        ImGui.SetNextWindowSize(new Vector2(nextSizeX, _lastSize.Y), ImGuiCond.Always);
        _popoutGrown = grown;
    }

    /// <summary>True while the popout pair should stay expanded. Dismissal is
    /// evaluated in <see cref="UpdatePopoutGrowth"/> where the window rect is valid.</summary>
    private bool PopoutWantsOpen()
        => _popoutPair != null && !_popoutForceClose
            && (!_popoutDismissed || DateTime.UtcNow < _popoutGraceUntil);

    /// <summary>
    /// Reads the strip width applied at this frame's Begin, places the panel
    /// rect against it and releases the pair once nothing is left. Returns
    /// the grown strip width in pixels.
    /// </summary>
    private float UpdatePopoutGrowth()
    {
        WarmPopoutContent();

        // folding re-owns the window geometry · the popout cannot outlive it
        if (SpheneUI.Chrome.CollapseAmount(ProfileFoldId) > 0f)
        {
            _popoutPair = null;
            _popoutWarmPair = null;
            _popoutContentPair = null;
            _popoutSwapCover = 0f;
            _popoutKey = null;
            _popoutOpenT = 0f;
            _popoutGrown = 0f;
            _popoutSideDecided = false;
            return 0f;
        }

        var drawn = _popoutGrown;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        _popoutPanelMin = _popoutLeft
            ? pos
            : new Vector2(pos.X + size.X - drawn, pos.Y);
        _popoutPanelMax = _popoutPanelMin + new Vector2(drawn, size.Y);

        if (_popoutPair != null && !_popoutForceClose)
        {
            var mouseInPanel = drawn > 0.5f
                && ImGui.IsMouseHoveringRect(_popoutPanelMin, _popoutPanelMax);
            // right-click activates rows, so only left/middle clicks dismiss
            var anyClick = ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                || ImGui.IsMouseClicked(ImGuiMouseButton.Middle);
            if (anyClick && !mouseInPanel)
            {
                _popoutForceClose = true;
            }
            else if (!ImGui.IsMouseHoveringRect(pos, pos + size))
            {
                // the strip is part of the grown window · leaving the window
                // region starts the grace countdown, re-entering cancels it
                if (!_popoutDismissed)
                {
                    _popoutDismissed = true;
                    _popoutGraceUntil = DateTime.UtcNow.AddSeconds(PopoutDismissGraceSeconds);
                }
            }
            else
            {
                _popoutDismissed = false;
            }
        }

        // pair switch while open: a surface-colored cover fades in, the card
        // swaps to the new pair under full cover, then the cover fades back
        // out · the strip itself never re-runs its growth animation
        var dt = ImGui.GetIO().DeltaTime;
        if (_popoutPair != null && !ReferenceEquals(_popoutContentPair, _popoutPair))
        {
            if (_popoutContentPair == null || drawn <= 1f)
            {
                _popoutContentPair = _popoutPair;
                _popoutKey = $"popout:{_popoutPair.UserData.UID}";
            }
            else if ((_popoutSwapCover += dt / PopoutSwapCoverSeconds) >= 1f)
            {
                _popoutSwapCover = 1f;
                _popoutContentPair = _popoutPair;
                _popoutKey = $"popout:{_popoutPair.UserData.UID}";
            }
        }
        else
        {
            _popoutSwapCover = MathF.Max(0f, _popoutSwapCover - dt / PopoutSwapRevealSeconds);
        }

        if (!PopoutWantsOpen() && _popoutOpenT <= 0f && drawn <= 0.01f)
        {
            _popoutPair = null;
            _popoutWarmPair = null;
            _popoutContentPair = null;
            _popoutSwapCover = 0f;
            _popoutKey = null;
            _popoutSideDecided = false;
            _popoutGrown = 0f;
        }
        return drawn;
    }

    /// <summary>
    /// Pre-warms the popout's data path while the hover delay is still
    /// running: the profile fetch starts on hover-arm, and avatar/banner
    /// textures decode under the same cache keys the open panel uses  · 
    /// growth then only reveals content instead of loading it.
    /// </summary>
    private void WarmPopoutContent()
    {
        var warm = _popoutWarmPair ?? _popoutPair;
        if (warm == null)
        {
            return;
        }
        var profile = _spheneProfileManager.GetSpheneProfile(warm.UserData);
        var key = $"popout:{warm.UserData.UID}";
        ResolvePopoutTexture(key, profile.ImageData.Value, ref _popoutAvatarBytes);
        ResolvePopoutTexture(key + ":banner", profile.BannerImageData.Value, ref _popoutBannerBytes);
    }

    private void DrawPopoutPanel(float grown)
    {
        var pair = _popoutContentPair;
        if (pair == null) return;

        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var fullW = PopoutPanelWidth * scale;
        var pad = SpheneTheme.Current.CompactWindowPadding.X;
        // the card's seam edge stays pinned to the content edge · the moving
        // window border reveals it instead of the card sliding
        var cardMin = new Vector2(
            _popoutLeft ? _popoutPanelMax.X - fullW : _popoutPanelMin.X,
            _popoutPanelMin.Y);

        drawList.PushClipRect(_popoutPanelMin, _popoutPanelMax, true);
        ImGui.SetCursorScreenPos(cardMin + new Vector2(pad, pad));
        SpheneUI.ContentBounds.Push(new Vector2(cardMin.X + pad, cardMin.X + fullW - pad));

        var spheneProfile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        var friendTheme = spheneProfile.ProfileTheme.Value;
        var key = _popoutKey!;
        var avatarWrap = ResolvePopoutTexture(key, spheneProfile.ImageData.Value, ref _popoutAvatarBytes);
        var bannerWrap = ResolvePopoutTexture(key + ":banner", spheneProfile.BannerImageData.Value, ref _popoutBannerBytes);

        // The banner bleeds to the full popout panel · its outer top corner
        // shares the window's rounding; the clip above keeps the reveal edge.
        var host = new SpheneUI.Profile.HeroHost(cardMin,
            new Vector2(cardMin.X + fullW, _popoutPanelMax.Y),
            ImGui.GetStyle().WindowRounding,
            _popoutLeft ? ImDrawFlags.RoundCornersTopLeft : ImDrawFlags.RoundCornersTopRight);
        var presence = pair.IsMutuallyVisible || pair.IsOnline
            ? SpheneUI.Palette.NetworkActive
            : (Vector4?)null;

        if (_spheneProfileManager.IsProfileLoading(pair.UserData))
        {
            SpheneUI.Profile.HeroSkeleton(SpheneTheme.Current.AccentCyan, statsRows: 2,
                host: host);
        }
        else
        {
            SpheneUI.Profile.Hero(
            pair.UserData.PreferredName,
            string.IsNullOrWhiteSpace(pair.UserData.Alias) ? string.Empty : "@" + pair.UserData.Alias,
            spheneProfile, avatarWrap, bannerWrap, friendTheme,
            SpheneTheme.Current.AccentCyan, SpheneTheme.Current.AccentBlue,
            isNsfw: spheneProfile.IsNSFW,
            actions:
            [
                new SpheneUI.Profile.HeroAction(FontAwesomeIcon.ArrowRight, "Profile",
                    "Open the full profile in the Sphene hub", () =>
                    {
                        _popoutForceClose = true;
                        Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, Friend: pair));
                    }),
            ],
            drawStats: w => DrawPopoutMeta(pair, w),
            host: host, presence: presence);
        }

        SpheneUI.ContentBounds.Pop();
        drawList.PopClipRect();

        // seam hairline fades in once the grown column has real width
        var seamWidth = 40f * scale;
        if (grown > seamWidth)
        {
            var fade = MathF.Min(1f, grown / seamWidth - 1f);
            var seamX = _popoutLeft ? _popoutPanelMax.X : _popoutPanelMin.X;
            drawList.AddLine(
                new Vector2(seamX, _popoutPanelMin.Y + 4f * scale),
                new Vector2(seamX, _popoutPanelMax.Y - 4f * scale),
                SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.08f * fade)), 1f);
        }

        // pair-swap cover: dips the strip toward the window surface while the
        // card content changes underneath · the swap lands at full cover
        if (_popoutSwapCover > 0.001f)
        {
            var cover = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            cover.W *= _popoutSwapCover;
            drawList.AddRectFilled(_popoutPanelMin, _popoutPanelMax,
                SpheneUI.ColorUtils.ToU32(cover), ImGui.GetStyle().WindowRounding,
                _popoutLeft ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersRight);
        }
    }

    private void DrawPopoutMeta(Pair pair, float metaW)
    {
        // ImGui resets the cursor X to the window's content edge after each
        // item · re-anchor it before every line so the meta rows stay on the
        // avatar's left edge.
        var metaX = ImGui.GetCursorPosX();
        var online = pair.IsMutuallyVisible || pair.IsOnline;
        var statusText = "● " + (pair.IsMutuallyVisible ? "Visible" : pair.IsOnline ? "Online" : "Offline")
            + (pair.IsMutuallyVisible && !string.IsNullOrEmpty(pair.PlayerName) ? $" · {pair.PlayerName}" : string.Empty);
        var shownStatus = SpheneUI.Text.Ellipsize(statusText, metaW);
        ImGui.SetCursorPosX(metaX);
        SpheneUI.Text.Colored(shownStatus,
            online ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted);
        if (shownStatus.Length != statusText.Length)
        {
            SpheneUI.AttachTooltip(statusText);
        }

        var pairingLine = string.Empty;
        void Part(string? s)
        {
            if (!string.IsNullOrEmpty(s))
            {
                pairingLine = pairingLine.Length == 0 ? s : pairingLine + " · " + s;
            }
        }
        Part(pair.UserPair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional
            ? "Direct pair" : null);
        var groupCount = pair.UserPair.Groups.Count;
        Part(groupCount > 0 ? $"via {groupCount} syncshell{(groupCount > 1 ? "s" : string.Empty)}" : null);
        var ownPaused = pair.UserPair.OwnPermissions.IsPaused();
        var otherPaused = pair.UserPair.OtherPermissions.IsPaused();
        Part(ownPaused && otherPaused ? "You paused · they paused"
            : ownPaused ? "You paused"
            : otherPaused ? "They paused" : null);
        if (pairingLine.Length > 0)
        {
            var shown = SpheneUI.Text.Ellipsize(pairingLine, metaW);
            ImGui.SetCursorPosX(metaX);
            SpheneUI.Text.Muted(shown);
            if (shown.Length != pairingLine.Length)
            {
                SpheneUI.AttachTooltip(pairingLine);
            }
        }

        var note = _serverManager.GetNoteForUid(pair.UserData.UID);
        if (!string.IsNullOrEmpty(note))
        {
            var noteLine = $"Your note: {note}";
            var shown = SpheneUI.Text.Ellipsize(noteLine, metaW);
            ImGui.SetCursorPosX(metaX);
            SpheneUI.Text.Muted(shown);
            if (shown.Length != noteLine.Length)
            {
                SpheneUI.AttachTooltip(noteLine);
            }
        }
    }

    private static IDalamudTextureWrap? ResolvePopoutTexture(string key, byte[] data, ref byte[] cache)
    {
        if (!data.SequenceEqual(cache))
        {
            cache = data;
            SpheneUI.Media.Evict(key);
        }
        return data.Length == 0 ? null : SpheneUI.Media.Texture(key, cache);
    }

    private void DrawProfileHeader(float width)
    {
        // Hub-style profile card flush with the header card edges:
        // round avatar + presence dot, name, status. Opens the Sphene Hub on click.
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var cardH = ProfileBarHeight * scale;
        var cardMin = ImGui.GetCursorScreenPos();
        var cardMax = new Vector2(cardMin.X + width, cardMin.Y + cardH);
        // matches the header card's outer rounding so the strip merges with its top edge
        var cornerRadius = 10.0f;

        // strip hover drives the backdrop anim only · clicks belong to the
        // same row backdrop viewers see in their friends list · idle layers
        // only, the card hover feedback lives on the identity card itself
        var profile = CompactProfile();
        var theme = profile?.ProfileTheme.Value ?? SpheneProfileTheme.Default;
        var accent = theme.AccentColor(SpheneUI.Colors.Accent);
        SpheneUI.Profile.RowBackdrop(drawList, cardMin, cardMax, accent, false,
            0f, theme, GetCompactRowImage(profile), cornerRadius,
            ImDrawFlags.RoundCornersTop);

        drawList.AddRect(cardMin, cardMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.10f)),
            cornerRadius, ImDrawFlags.RoundCornersTop);

        var idRect = DrawIdentityStrip(drawList, cardMin, cardMax, cornerRadius, profile, accent);
        // the corner icon is its own hit zone · card clicks skip it
        var iconHovered = ImGui.IsMouseHoveringRect(idRect.IconMin, idRect.IconMax)
            && ImGui.IsWindowHovered();
        if (ImGui.IsMouseHoveringRect(idRect.Min, idRect.Max) && !iconHovered
            && ImGui.IsWindowHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                MarkHubHintSeen();
                Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Home));
            }
        }

        // move the layout cursor below the card so the card body continues underneath
        ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMax.Y));
    }

    /// <summary>
    /// Shared identity strip for the compact header card and the title bar:
    /// the hub's identity card, inset into the caller's area and sized to
    /// its content, so the banner backdrop keeps breathing around the
    /// darker container. Click and bar-hover handling stay caller-owned  · 
    /// the card's own rect decides its sheen and tooltip.
    /// </summary>
    private (Vector2 Min, Vector2 Max, Vector2 IconMin, Vector2 IconMax) DrawIdentityStrip(ImDrawListPtr drawList,
        Vector2 areaMin, Vector2 areaMax, float outerRadius,
        SpheneProfileData? profile, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var connected = _apiController.IsConnected;
        var paused = _apiController.IsConnectionPaused;
        var connColor = !connected ? SpheneTheme.Current.CompactServerStatusError
            : paused ? SpheneTheme.Current.CompactServerStatusWarning
            : SpheneTheme.Current.CompactServerStatusConnected;
        var connText = !connected ? "Offline" : paused ? "Paused" : "Online";

        var (statusLine, statusColor) = ProfileStatusLine(profile);
        var padX = 6f * scale;
        var content = new SpheneUI.Profile.IdentityContent(
            GetCompactAvatarTexture(profile),
            SpheneUI.Profile.OwnName(_apiController.OwnDisplayName, _apiController.OwnAlias),
            accent, statusLine, statusColor,
            connColor, connText, _apiController.OwnAlias, accent);

        // the hub dictates the card width · the compact strip only clamps to
        // the space left of the bar buttons
        var inset = 4f * scale;
        var cardMin = areaMin + new Vector2(inset, inset);
        var cardMax = new Vector2(cardMin.X + MathF.Min(
                SpheneHubUi.ProfileCardWidth,
                MathF.Max(0f, areaMax.X - cardMin.X - inset)),
            areaMax.Y - inset);
        var cardRadius = MathF.Max(0f, outerRadius - inset);
        var cardHovered = ImGui.IsMouseHoveringRect(cardMin, cardMax)
            && ImGui.IsWindowHovered();

        // the area-syncshell status glyph lives in the card's right corner  · 
        // reserve its strip so name/status ellipsize before it
        var iconSize = ImGui.GetTextLineHeight();
        var iconPadX = 8f * scale;
        var trailingReserve = iconSize + iconPadX;
        SpheneUI.Profile.IdentityCard(drawList, cardMin, cardMax, padX, content, cardHovered,
            cardRadius, ImDrawFlags.RoundCornersAll, " · click to open the Sphene Hub",
            trailingReserve);

        var iconMin = new Vector2(cardMax.X - iconPadX - iconSize,
            cardMin.Y + (cardMax.Y - cardMin.Y - iconSize) * 0.5f);
        var iconMax = iconMin + new Vector2(iconSize, iconSize);
        DrawAreaSyncshellStatusIcon(drawList, iconMin, iconMax, accent);
        return (cardMin, cardMax, iconMin, iconMax);
    }

    /// <summary>
    /// The area-syncshell location glyph inside the identity card's right
    /// corner: dim when nothing exists, accent-lit while syncshells are
    /// joinable (pulsing during the attention window, glowing steadily after),
    /// green once connected. Click opens the area syncshell selection.
    /// </summary>
    private void DrawAreaSyncshellStatusIcon(ImDrawListPtr drawList, Vector2 iconMin,
        Vector2 iconMax, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var hasAreaSyncshells = _areaBoundSyncshellService.HasAvailableAreaSyncshells();
        var hasAnyHere = _areaBoundSyncshellService.HasAnyAreaSyncshellsInCurrentLocation();
        var isInHere = _areaBoundSyncshellService.IsInAreaSyncshellInCurrentLocation();
        if (hasAreaSyncshells && !_hadAvailableAreaSyncshells)
        {
            _areaSyncshellAttentionUntilUtc = DateTime.UtcNow.AddSeconds(20);
        }
        _hadAvailableAreaSyncshells = hasAreaSyncshells;

        var now = DateTime.UtcNow;
        var drawAttention = hasAreaSyncshells && !isInHere
            && now < _areaSyncshellAttentionUntilUtc;

        // A real item owns the hit zone · hover, click and the themed tooltip.
        ImGui.SetCursorScreenPos(iconMin);
        ImGui.PushID((int)(iconMin.X + iconMin.Y));
        ImGui.InvisibleButton("##areasync", iconMax - iconMin);
        ImGui.PopID();
        var iconHovered = ImGui.IsItemHovered();

        Vector4 iconColor;
        if (isInHere)
        {
            iconColor = SpheneUI.ColorUtils.WithAlpha(
                Vector4.Lerp(SpheneTheme.Current.CompactServerStatusConnected,
                    Vector4.One, 0.25f), iconHovered ? 1f : 0.95f);
        }
        else if (hasAreaSyncshells)
        {
            // Joinable glows in a brightened accent · only the dot blinks
            // after the attention window.
            iconColor = drawAttention
                ? Vector4.Lerp(accent, SpheneTheme.Current.CompactServerStatusWarning,
                    0.5f + 0.5f * MathF.Sin((float)now.TimeOfDay.TotalSeconds * 6.0f))
                : SpheneUI.ColorUtils.WithAlpha(
                    Vector4.Lerp(accent, Vector4.One, iconHovered ? 0.6f : 0.45f), 1f);
        }
        else
        {
            iconColor = SpheneUI.ColorUtils.WithAlpha(
                SpheneTheme.Current.CompactTextSecondary, iconHovered ? 0.65f : 0.40f);
        }

        // Bare glyph · no background. Active states get a soft bloom drawn as
        // low-alpha offset copies behind the main glyph.
        var glyph = FontAwesomeIcon.MapMarkerAlt.ToIconString();
        var glow = isInHere || hasAreaSyncshells;
        using (SpheneUI.Icons.PushFont())
        {
            var glyphSize = ImGui.CalcTextSize(glyph);
            var glyphPos = iconMin + (iconMax - iconMin - glyphSize) * 0.5f;
            if (glow)
            {
                var glowCol = SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(iconColor, drawAttention ? 0.30f : 0.20f));
                var off = 1.3f * scale;
                drawList.AddText(glyphPos + new Vector2(-off, 0f), glowCol, glyph);
                drawList.AddText(glyphPos + new Vector2(off, 0f), glowCol, glyph);
                drawList.AddText(glyphPos + new Vector2(0f, -off), glowCol, glyph);
                drawList.AddText(glyphPos + new Vector2(0f, off), glowCol, glyph);
            }
            drawList.AddText(glyphPos, SpheneUI.ColorUtils.ToU32(iconColor), glyph);
        }

        // The small indicator dot is the only blinking part · it keeps a slow
        // pulse while a syncshell is joinable but not joined.
        if (hasAreaSyncshells && !isInHere)
        {
            var pulseA = drawAttention
                ? 0.55f + 0.45f * MathF.Sin((float)now.TimeOfDay.TotalSeconds * 6.0f)
                : 0.55f + 0.30f * MathF.Sin((float)now.TimeOfDay.TotalSeconds * 2.2f);
            var badgeCenter = new Vector2(iconMax.X - 1f * scale, iconMin.Y + 1.5f * scale);
            var badgeR = 3.5f * scale;
            drawList.AddCircleFilled(badgeCenter, badgeR,
                ImGui.ColorConvertFloat4ToU32(SpheneUI.ColorUtils.WithAlpha(
                    SpheneTheme.Current.CompactServerStatusWarning, pulseA)));
            drawList.AddCircle(badgeCenter, badgeR,
                ImGui.ColorConvertFloat4ToU32(SpheneTheme.Current.CompactBorder), 12, 1f * scale);
        }

        if (iconHovered)
        {
            SpheneUI.AttachTooltip(isInHere
                ? "Connected to an Area Syncshell here · click to open selection"
                : hasAreaSyncshells
                    ? "Open Area Syncshell Selection"
                    : !hasAnyHere
                        ? "No Area Syncshells found"
                        : "No Area Syncshells available");
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && (hasAreaSyncshells || isInHere))
        {
            _areaBoundSyncshellService.TriggerAreaSyncshellSelection();
        }
    }

    /// <summary>
    /// The compact title bar: flush profile strip that drags the window and
    /// opens the Sphene Hub on click, with the header buttons on the right.
    /// Replaces the window's native title bar in custom-header mode.
    /// </summary>
    private void DrawProfileTitleBar()
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var windowPos = ImGui.GetWindowPos();
        var windowW = ImGui.GetWindowSize().X;
        var barMin = windowPos;
        var barH = ProfileBarHeight * scale;
        var barMax = new Vector2(barMin.X + windowW, barMin.Y + barH);
        var rounding = MathF.Max(style.WindowRounding, SpheneTheme.Current.CompactHeaderRounding);
        // Folded: the bar IS the whole window · its bottom edge is the window's
        // bottom edge, so the corners round all the way around
        var folded = SpheneUI.Chrome.CollapseAmount(ProfileFoldId) >= 1f;
        var barCorners = folded ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersTop;

        // flush header background covering the window's top edge
        drawList.AddRectFilled(barMin, barMax,
            SpheneUI.ColorUtils.ToU32(SpheneTheme.Current.CompactHeaderBg), rounding,
            barCorners);
        DrawAprilFoolsOverlay(drawList);

        // Buttons first · the item-hover check below must already see them.
        var buttonsLeftX = DrawTitleBarButtons(barMin, barMax);

        // same row backdrop viewers see in their friends list: animated
        // row image fading right plus the owner's accent + effect layers
        var profile = CompactProfile();
        var theme = profile?.ProfileTheme.Value ?? SpheneProfileTheme.Default;
        var accent = theme.AccentColor(SpheneUI.Colors.Accent);

        // Nothing claims presses on the empty bar · ImGui's native window move
        // handles dragging at Begin time (lag-free). We only track the press
        // for click = open hub, gated to presses that start on free bar space.
        // no bar-wide hover effect · feedback is scoped to the identity card
        SpheneUI.Profile.RowBackdrop(drawList, barMin, barMax, accent, false,
            0f, theme, GetCompactRowImage(profile), rounding, barCorners);

        // Accent edge under the bar: a thin strip can't carry the window's
        // corner radius (ImGui clamps rounding to half the rect height and
        // the corners poke past the folded silhouette), so clip to the bottom
        // band and stamp the whole rounded bar shape · the visible part
        // follows the corner curve exactly. Expanded = flat strip.
        var accentLineH = 1.0f * scale;
        drawList.PushClipRect(new Vector2(barMin.X, barMax.Y - accentLineH), barMax, true);
        drawList.AddRectFilled(barMin, barMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            rounding, barCorners);
        drawList.PopClipRect();

        var idRect = DrawIdentityStrip(drawList, barMin,
            new Vector2(buttonsLeftX - 6f * scale, barMax.Y), rounding, profile, accent);

        // only the identity card itself opens the hub · the rest of the bar
        // stays pure drag space, and the corner icon is its own hit zone
        var inCard = ImGui.IsMouseHoveringRect(idRect.Min, idRect.Max)
            && !ImGui.IsMouseHoveringRect(idRect.IconMin, idRect.IconMax)
            && ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered();
        if (inCard)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (inCard && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _titleBarPressed = true;
        }
        if (_titleBarPressed && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            _titleBarPressed = false;
            if (inCard && ImGui.GetIO().MouseDragMaxDistanceSqr[(int)ImGuiMouseButton.Left] < 64f)
            {
                MarkHubHintSeen();
                Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Home));
            }
        }

        // first-connect onboarding: radar ping around the identity card plus
        // a pill callout underneath until the user opened the hub once. Pure
        // overlay - nothing on screen is blocked or disabled.
        var showHubHint = _apiController.IsConnected
            && !_configService.Current.HubOnboardingHintSeen;
        var hintT = (float)ImGui.GetTime();
        if (showHubHint)
        {
            var inset = 2f * scale;

            // expanding ping rings - three phased waves radiating off the card
            for (var ring = 0; ring < 3; ring++)
            {
                var phase = (hintT * 0.8f + ring * (1f / 3f)) % 1f;
                var pad = inset + phase * 16f * scale;
                drawList.AddRect(idRect.Min - new Vector2(pad), idRect.Max + new Vector2(pad),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, (1f - phase) * 0.55f)),
                    rounding + pad, ImDrawFlags.None, MathF.Max(1f, (2.5f - phase) * scale));
            }

            // accent wash + bright steady ring so the card stays lit
            // between pings
            var pulse = 0.5f + 0.5f * MathF.Sin(hintT * 4f);
            drawList.AddRectFilled(idRect.Min, idRect.Max,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.04f + 0.05f * pulse)), rounding);
            drawList.AddRect(idRect.Min - new Vector2(inset), idRect.Max + new Vector2(inset),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.6f + 0.4f * pulse)),
                rounding + inset, ImDrawFlags.None, 2f * scale);
        }

        ImGui.SetCursorScreenPos(new Vector2(
            windowPos.X + style.WindowPadding.X, barMax.Y + 6f * scale));

        if (showHubHint && !SpheneUI.Chrome.FoldCollapsed(ProfileFoldId))
        {
            var chipMin = new Vector2(idRect.Min.X, barMax.Y + 6f * scale);
            ImGui.SetCursorScreenPos(chipMin);
            var chipH = ImGui.GetFrameHeight() + 4f * scale;
            var chipW = ImGui.GetContentRegionAvail().X;
            var chipMax = new Vector2(chipMin.X + chipW, chipMin.Y + chipH);
            var chipRound = chipH * 0.5f;
            var chipPulse = 0.7f + 0.3f * MathF.Sin(hintT * 4f);
            drawList.AddRectFilled(chipMin, chipMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.16f + 0.10f * chipPulse)),
                chipRound);
            drawList.AddRect(chipMin, chipMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.5f + 0.3f * chipPulse)),
                chipRound);

            // bouncing arrow - drawlist decoration, eats no layout space
            var arrowW = ImGui.GetFrameHeight();
            using (SpheneUI.Icons.PushFont())
            {
                var arrow = FontAwesomeIcon.ArrowUp.ToIconString();
                var arrowSize = ImGui.CalcTextSize(arrow);
                var bounce = -MathF.Abs(MathF.Sin(hintT * 4f)) * 3f * scale;
                drawList.AddText(new Vector2(chipMin.X + 8f * scale,
                        chipMin.Y + (chipH - arrowSize.Y) * 0.5f + bounce),
                    SpheneUI.ColorUtils.ToU32(accent), arrow);
            }

            ImGui.SetCursorScreenPos(new Vector2(chipMin.X + 8f * scale + arrowW,
                chipMin.Y + (chipH - ImGui.GetTextLineHeight()) * 0.5f));
            SpheneUI.Text.Colored("Tap your card to open the Sphene Hub", SpheneUI.Colors.TextPrimary);

            var closeW = 22f;
            ImGui.SetCursorScreenPos(new Vector2(chipMax.X - closeW - 4f * scale, chipMin.Y));
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Times, null, null, null, null, ButtonStyleKeys.Compact_Close))
            {
                MarkHubHintSeen();
            }
            SpheneUI.AttachTooltip("Got it");

            ImGui.SetCursorScreenPos(new Vector2(
                windowPos.X + style.WindowPadding.X, chipMax.Y + 4f * scale));
        }
    }

    /// <summary>Right-aligned title-bar buttons; returns the leftmost occupied X.</summary>
    private float DrawTitleBarButtons(Vector2 barMin, Vector2 barMax)
    {
        var buttonSpacing = 8.0f;
        var buttonWidth = 22.0f;
        var buttonHeight = ImGui.GetFrameHeight();
        var buttonY = barMin.Y + (barMax.Y - barMin.Y - buttonHeight) * 0.5f;
        var currentX = barMax.X - 12.0f - buttonWidth;

        ImGui.SetCursorScreenPos(new Vector2(currentX, buttonY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Times, null, null, null, null, ButtonStyleKeys.Compact_Close))
        {
            _logger.LogDebug("Close button clicked, closing CompactUI");
            IsOpen = false;
        }
        SpheneUI.AttachTooltip("Close Sphene");

        currentX -= (buttonWidth + buttonSpacing);

        // Collapse/expand · folds the window down to just this bar
        var folded = SpheneUI.Chrome.FoldCollapsed(ProfileFoldId);
        ImGui.SetCursorScreenPos(new Vector2(currentX, buttonY));
        if (SpheneUI.Icons.Button(folded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp))
        {
            SpheneUI.Chrome.ToggleFold(ProfileFoldId);
        }
        SpheneUI.AttachTooltip(folded ? "Expand" : "Collapse");

        // folded bar shrinks to identity card + close/fold · the auxiliary
        // buttons fade out with the collapse animation and, while folding,
        // no longer reserve bar space so the card keeps its hub width
        var foldAmount = SpheneUI.Chrome.CollapseAmount(ProfileFoldId);
        var essentialLeft = currentX;
        if (foldAmount >= 1f)
        {
            return essentialLeft;
        }
        using var fadeAux = ImRaii.PushStyle(ImGuiStyleVar.Alpha,
            ImGui.GetStyle().Alpha * (1f - foldAmount), foldAmount > 0f);

        // fading buttons may slide under the identity card as the window
        // shrinks · clip them at its right edge instead of overdrawing it
        var buttonDrawList = ImGui.GetWindowDrawList();
        if (foldAmount > 0f)
        {
            var cardW = MathF.Min(SpheneHubUi.ProfileCardWidth, MathF.Max(0f,
                barMax.X - barMin.X - 14f * SpheneUI.Scale - 64f));
            buttonDrawList.PushClipRect(
                new Vector2(barMin.X + 6f * SpheneUI.Scale + cardW, barMin.Y),
                barMax, true);
        }

        currentX -= (buttonWidth + buttonSpacing);
        ImGui.SetCursorScreenPos(new Vector2(currentX, buttonY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Cog, null, null, null, null, ButtonStyleKeys.Compact_Settings))
        {
            Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
        }
        SpheneUI.AttachTooltip("Open Network Configuration");

        currentX -= (buttonWidth + buttonSpacing);

        var reconnectCurrentTime = DateTime.UtcNow;
        var reconnectTimeSinceLastClick = reconnectCurrentTime - _lastReconnectButtonClick;
        var isReconnectButtonDisabled = reconnectTimeSinceLastClick.TotalSeconds < 5.0;

        ImGui.SetCursorScreenPos(new Vector2(currentX, buttonY));
        using (ImRaii.Disabled(isReconnectButtonDisabled))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Redo, null, null, null, null, ButtonStyleKeys.Compact_Reconnect))
            {
                _lastReconnectButtonClick = reconnectCurrentTime;
                _ = Task.Run(() => _apiController.CreateConnectionsAsync(forceCharacterDataReload: true));
            }
        }
        SpheneUI.AttachTooltip(isReconnectButtonDisabled
            ? $"Reconnect Cooldown: {Math.Ceiling(5.0 - reconnectTimeSinceLastClick.TotalSeconds)}s"
            : "Reconnect to the Sphene Network");

        currentX -= (buttonWidth + buttonSpacing);

#if IS_TEST_BUILD
        ImGui.SetCursorScreenPos(new Vector2(currentX, buttonY));
        DrawTestServerToggleButton();
        currentX -= (buttonWidth + buttonSpacing);
#endif

        ImGui.SetCursorScreenPos(new Vector2(currentX, buttonY));
        if (_apiController.IsConnected)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Unlink, null, null, null, null, ButtonStyleKeys.Compact_Disconnect) && _serverManager.CurrentServer != null)
            {
                _serverManager.CurrentServer.FullPause = true;
                _serverManager.Save();
                _ = _apiController.CreateConnectionsAsync();
            }
            SpheneUI.AttachTooltip("Disconnect from Server");
        }
        else
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Link, null, null, null, null, ButtonStyleKeys.Compact_Connect) && _serverManager.CurrentServer != null)
            {
                _serverManager.CurrentServer.FullPause = false;
                _serverManager.Save();
                _ = _apiController.CreateConnectionsAsync();
            }
            SpheneUI.AttachTooltip("Connect to Server");
        }

        if (foldAmount > 0f)
        {
            buttonDrawList.PopClipRect();
        }
        // while folding, the card grows against the close/fold edge · the
        // fading aux buttons stop claiming bar width
        return foldAmount > 0f ? essentialLeft : currentX;
    }

    private void DrawAprilFoolsButton()
    {
        var nowLocal = DateTime.Now;
        if (nowLocal.Month != 4 || nowLocal.Day != 1)
        {
            return;
        }

        var t = (float)ImGui.GetTime();
        var pulse = 0.65f + 0.35f * MathF.Sin(t * 3.0f);
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.55f, 0.85f, 1.0f, pulse)))
        {
            var buttonText = _aprilFoolsHyperSyncEnabled ? "Disable HyperSync™" : "Enable HyperSync™";
            if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Rocket, buttonText))
            {
                var wasEnabled = _aprilFoolsHyperSyncEnabled;
                _aprilFoolsHyperSyncEnabled = !_aprilFoolsHyperSyncEnabled;
                _aprilFoolsHyperSyncActivatedAt = DateTime.Now;
                ImGui.OpenPopup(wasEnabled ? "HyperSyncDisabled" : "HyperSync");
            }
        }
        SpheneUI.AttachTooltip(_aprilFoolsHyperSyncEnabled ? "Click to disable HyperSync™ (Beta)" : "Click to enable HyperSync™ (Beta)");
    }

    private (string Text, Vector4 Color) ProfileStatusLine(SpheneProfileData? profile)
    {
        if (_apiController.DisplayServerState != ServerState.Connected)
        {
            return (GetUidText(), GetServerStatusColor());
        }
        var text = SpheneUI.Profile.StatusLineText(profile, out var color);
        return (text, color);
    }

    private IDalamudTextureWrap? GetCompactAvatarTexture(SpheneProfileData? profile)
    {
        if (profile == null)
        {
            return null;
        }
        var bytes = profile.ImageData.Value;
        if (bytes.Length == 0)
        {
            return null;
        }
        if (!bytes.SequenceEqual(_lastAvatarBytes))
        {
            _lastAvatarBytes = bytes;
            SpheneUI.Media.Evict(CompactAvatarKey);
        }
        return SpheneUI.Media.Texture(CompactAvatarKey, bytes);
    }

    private SpheneUI.Media.AnimatedImage? GetCompactRowImage(SpheneProfileData? profile)
    {
        var bytes = profile?.RowImageData.Value;
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }
        var key = CompactRowImageKey;
        if (!bytes.SequenceEqual(_lastRowImageBytes))
        {
            _lastRowImageBytes = bytes;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Animated(key, bytes);
    }

    private SpheneProfileData? CompactProfile()
        => string.IsNullOrEmpty(_apiController.UID)
            ? null
            : _spheneProfileManager.GetSpheneProfile(new Sphene.API.Data.UserData(_apiController.UID));

    private string CompactAvatarKey => $"compactui:avatar:{_apiController.UID}";
    private string CompactRowImageKey => $"compactui:rowimage:{_apiController.UID}";

    private void DrawManuallyDisconnectedScreen()
    {
        ImGui.Spacing();
        var pausedTitle = "Manually Disconnected";
        var pausedTitleSize = ImGui.CalcTextSize(pausedTitle);
        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) * 0.5f - pausedTitleSize.X * 0.5f);
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Colored(pausedTitle, SpheneTheme.Current.CompactServerStatusWarning);
        ImGui.Spacing();
        var pausedMessage = "You manually disconnected from the Sphene server. Press the Connect button to reconnect.";
        var pausedContentMin = ImGui.GetWindowContentRegionMin().X;
        var pausedContentMax = ImGui.GetWindowContentRegionMax().X;
        var pausedContentWidth = pausedContentMax - pausedContentMin;
        var pausedMsgBoxWidth = MathF.Min(pausedContentWidth, 380f * ImGuiHelpers.GlobalScale);
        var padding = ImGui.GetStyle().WindowPadding.X * 2;
        var availWidth = pausedMsgBoxWidth - padding;
        var textSize = ImGui.CalcTextSize(pausedMessage, wrapWidth: availWidth);
        var startX = pausedContentMin + MathF.Max(0, (pausedContentWidth - pausedMsgBoxWidth) * 0.5f) + MathF.Max(0, (pausedMsgBoxWidth - textSize.X) * 0.5f);
        ImGui.SetCursorPosX(startX);
        SpheneUI.Text.ColoredWrapped(pausedMessage, SpheneTheme.Current.CompactTextSecondary, startX + availWidth);
        ImGui.Spacing();
        var btnWidth = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Link, "Connect");
        ImGui.SetCursorPosX(pausedContentMin + MathF.Max(0, (pausedContentWidth - btnWidth) * 0.5f));
        if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Link, "Connect") && _serverManager.CurrentServer != null)
        {
            _serverManager.CurrentServer.FullPause = false;
            _serverManager.Save();
            _ = _apiController.CreateConnectionsAsync();
        }
        SpheneUI.AttachTooltip("Reconnect to the Sphene server");
    }

    private void DrawHeaderCard(ImGuiStylePtr style, bool withProfile)
    {
        // Calculate sizes first to determine layout
        var availableWidth = ImGui.GetContentRegionAvail().X;

        // Auto-Height Layout using Groups and Channels
        // This ensures the card height perfectly matches the content
        var drawList = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorPos();
        var startScreenPos = ImGui.GetCursorScreenPos();
        var padding = new Vector2(HeaderContentPadX, 10);

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1); // Draw content on top

        ImGui.BeginGroup();

        // Ensure the group spans the full width
        // We use a dummy to force the width, but we MUST reset the cursor so content draws ON TOP of it (layout-wise)
        var groupStartPos = ImGui.GetCursorPos();
        ImGui.Dummy(new Vector2(availableWidth, 0));
        ImGui.SetCursorPos(groupStartPos);

        // 1. Profile card flush with the card edges (opens the Sphene Hub on click)
        if (withProfile)
        {
            DrawProfileHeader(availableWidth);
        }

        // Controls sit inside the card padding below the profile strip
        ImGui.SetCursorScreenPos(new Vector2(startScreenPos.X + padding.X,
            ImGui.GetCursorScreenPos().Y + padding.Y));
        DrawAprilFoolsButton();

        // Mode Controls (Left)
        DrawModeControls();

        // Texture Management (Right)
        DrawTextureManagement();

        ImGui.EndGroup();

        // Determine bounds of the content
        var groupMax = ImGui.GetItemRectMax();

        // Draw Background
        drawList.ChannelsSetCurrent(0); // Draw background behind

        // Expand background to include padding
        // We strictly use availableWidth for the width to ensure it doesn't expand if content overflows
        // This fixes the issue where the card becomes wider than the header if the content is too long

        var bgMin = startScreenPos;
        // Force the width to match the calculated available width exactly
        var bgMax = new Vector2(bgMin.X + availableWidth, groupMax.Y + padding.Y);

        var cardRounding = MathF.Min(SpheneTheme.Current.CompactChildRounding,
            (bgMax.Y - bgMin.Y) * 0.5f);
        drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(SpheneTheme.Current.CompactControlPanelBg), cardRounding);
        drawList.AddRect(bgMin, bgMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneTheme.Current.CompactBorder, 0.22f)), cardRounding);
        
        drawList.ChannelsMerge();
        
        // Advance cursor past the card
        // We need to set the cursor to the bottom of the card (bgMax.Y) plus spacing
        ImGui.SetCursorPosY(startPos.Y + (bgMax.Y - bgMin.Y) + style.ItemSpacing.Y);
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
                       (p.Key.UserData.DisplayName?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (p.Key.GetNote()?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (p.Key.PlayerName?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToDictionary(k => k.Key, k => k.Value);

        // the row shows the display name · sort by the same value so the
        // visible order matches what the eye scans
        string? AlphabeticalSort(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.UserData.PreferredName;
        bool FilterOnlineOrPausedSelf(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (!u.Key.IsEffectivelyOffline || (!u.Key.IsEffectivelyOffline && !_configService.Current.ShowOfflineUsersSeparately))
                && !u.Key.UserPair.OwnPermissions.IsPaused();
        bool FilterPausedUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.UserPair.OwnPermissions.IsPaused() && (!u.Key.IsMutuallyVisible || !_configService.Current.ShowVisibleUsersSeparately);
        Dictionary<Pair, List<GroupFullInfoDto>> BasicSortedDictionary(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.OrderByDescending(u => u.Key.IsMutuallyVisible)
                .ThenByDescending(u => !u.Key.IsEffectivelyOffline && u.Key.IsMutuallyVisible)
                .ThenByDescending(u => !u.Key.IsEffectivelyOffline)
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(u => u.Key, u => u.Value);
        ImmutableList<Pair> ImmutablePairList(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.Select(k => k.Key).ToImmutableList();
        bool FilterVisibleUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsMutuallyVisible && !u.Key.IsEffectivelyOffline
                && (_configService.Current.ShowSyncshellUsersInVisible || !(!_configService.Current.ShowSyncshellUsersInVisible && !u.Key.IsDirectlyPaired))
                && (!_configService.Current.ShowVisibleSyncshellUsersOnlyInSyncshells || u.Key.IsDirectlyPaired);
        bool FilterTagusers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, string tag)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && _tagHandler.HasTag(u.Key.UserData.UID, tag) && (!u.Key.IsMutuallyVisible || !_configService.Current.ShowVisibleUsersSeparately);
        bool FilterGroupUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, GroupFullInfoDto group)
        {
            // Check if user is a member of this group
            if (u.Value.Exists(g => string.Equals(g.GID, group.GID, StringComparison.Ordinal)))
                return true;
            
            // For visible users, also check if they are actual members of this syncshell
            if (u.Key.IsMutuallyVisible && group.GroupPairUserInfos.ContainsKey(u.Key.UserData.UID))
                return true;
            
            // For area-bound syncshells (where GroupPairUserInfos is empty), also include visible users
            // This allows visible users to be shown in area-bound syncshells even if they're not permanent members
            if (group.GroupPairUserInfos.Count == 0 && u.Key.IsMutuallyVisible && _areaBoundSyncshellService.IsAreaBoundSyncshell(group.Group.GID))
                return true;
            
            return false;
        }
        bool FilterNotTaggedUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && !_tagHandler.HasAnyTag(u.Key.UserData.UID) && (!u.Key.IsMutuallyVisible || !_configService.Current.ShowVisibleUsersSeparately);
        bool FilterOfflineUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && u.Key.IsEffectivelyOffline && !u.Key.UserPair.OwnPermissions.IsPaused() && (!u.Key.IsMutuallyVisible || !_configService.Current.ShowVisibleUsersSeparately);
        bool FilterOfflineSyncshellUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (!u.Key.IsDirectlyPaired && u.Key.IsEffectivelyOffline && !u.Key.UserPair.OwnPermissions.IsPaused());


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
                .OrderByDescending(u => u.Key.IsMutuallyVisible)
                .ThenByDescending(u => !u.Key.IsEffectivelyOffline && u.Key.IsMutuallyVisible)
                .ThenByDescending(u => !u.Key.IsEffectivelyOffline)
                .ThenBy(u =>
                {
                    if (string.Equals(u.Key.UserData.UID, group.OwnerUID, StringComparison.Ordinal)) return 0;
                    if (group.GroupPairUserInfos.TryGetValue(u.Key.UserData.UID, out var info))
                    {
                        if (info.IsModerator()) return 1;
                        if (info.IsPinned()) return 2;
                    }
                    return 3;
                })
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(k => k.Key, k => k.Value);

            groupFolders.Add(_drawEntityFactory.CreateDrawGroupFolder(group, filteredGroupPairs, allGroupPairs, _configService.Current.GroupUpSyncshells));
        }

        if (_configService.Current.GroupUpSyncshells)
            drawFolders.Add(new DrawGroupedGroupFolder(groupFolders, _tagHandler));
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

        // Removed one-sided individual pairs from main list; they're handled via Pair Requests panel

        return drawFolders;
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
        return _apiController.DisplayServerState switch
        {
            ServerState.Connecting => "Attempting to connect to the server.",
            ServerState.Reconnecting => "Connection to server interrupted, attempting to reconnect to the server.",
            ServerState.Disconnected => _apiController.IsConnectionPaused
                ? "You manually disconnected from the Sphene server last time. Please Press Connect to reconnect."
                : "You are currently disconnected from the Sphene server.",
            ServerState.Disconnecting => "Disconnecting from the server",
            ServerState.Unauthorized => "Server Response: " + _apiController.AuthFailureMessage,
            ServerState.Offline => "Your selected Sphene server is currently offline.",
            ServerState.VersionMisMatch =>
                "Your plugin or the server you are connecting to is out of date. Please update your plugin now. If you already did so, contact the server provider to update their server to the latest version.",
            ServerState.RateLimited => "You are rate limited for (re)connecting too often. Disconnect, wait 10 minutes and try again.",
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => "You have no secret key set for this current character. Open Settings -> Network Configuration and set a secret key for the current character. You can reuse the same secret key for multiple characters.",
            ServerState.MultiChara => "Your Character Configuration has multiple characters configured with same name and world. You will not be able to connect until you fix this issue. Remove the duplicates from the configuration in Settings -> Network Configuration -> Authentication & Characters and reconnect manually after.",
            ServerState.OAuthMisconfigured => "OAuth2 is enabled but not fully configured, verify in the Settings -> Network Configuration that you have OAuth2 connected and, importantly, an account assigned to your current character.",
            ServerState.OAuthLoginTokenStale => "Your OAuth2 login token is stale and cannot be used to renew. Go to the Settings -> Network Configuration and unlink then relink your OAuth2 configuration.",
            ServerState.NoAutoLogon => "This character has automatic login into Sphene disabled. Press the connect button to connect to Sphene.",
            _ => string.Empty
        };
    }

    

    private Vector4 GetServerStatusColor()
    {
        return _apiController.DisplayServerState switch
        {
            ServerState.Connecting => SpheneTheme.Current.CompactServerStatusWarning,
            ServerState.Reconnecting => SpheneTheme.Current.CompactServerStatusError,
            ServerState.Connected => SpheneTheme.Current.CompactServerStatusConnected,
            ServerState.Disconnected => SpheneTheme.Current.CompactServerStatusWarning,
            ServerState.Disconnecting => SpheneTheme.Current.CompactServerStatusWarning,
            ServerState.Unauthorized => SpheneTheme.Current.CompactServerStatusError,
            ServerState.VersionMisMatch => SpheneTheme.Current.CompactServerStatusError,
            ServerState.Offline => SpheneTheme.Current.CompactServerStatusError,
            ServerState.RateLimited => SpheneTheme.Current.CompactServerStatusWarning,
            ServerState.NoSecretKey => SpheneTheme.Current.CompactServerStatusWarning,
            ServerState.MultiChara => SpheneTheme.Current.CompactServerStatusWarning,
            ServerState.OAuthMisconfigured => SpheneTheme.Current.CompactServerStatusError,
            ServerState.OAuthLoginTokenStale => SpheneTheme.Current.CompactServerStatusError,
            ServerState.NoAutoLogon => SpheneTheme.Current.CompactServerStatusWarning,
            _ => SpheneTheme.Current.CompactServerStatusError
        };
    }

    private string GetUidText()
    {
        return _apiController.DisplayServerState switch
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
        _logger.LogDebug("Gpose/Cutscene end: restoring CompactUI IsOpen to {state}", _wasOpen);
        IsOpen = _wasOpen;
    }

    private void UiSharedService_GposeStart()
    {
        _wasOpen = IsOpen;
        _logger.LogDebug("Gpose/Cutscene start: closing CompactUI. Previous IsOpen={state}", _wasOpen);
        IsOpen = false;
    }

    private async Task<bool> IsPlayerInCurrentPartyAsync(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return false;
        
        var partyMembers = await _dalamudUtilService.RunOnFrameworkThread(() => _dalamudUtilService.GetPartyMemberNames()).ConfigureAwait(false);
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
                            await _apiController.UserSetPairPermissions(new(pair.UserData, permissions)).ConfigureAwait(false);
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
                    var pairsInGroup = groupPair.Value;
                    
                    // Only unpause if this syncshell was NOT already paused before incognito mode
                    if (group.GroupUserPermissions.IsPaused() && !_prePausedSyncshells.Contains(group.Group.GID))
                    {
                        var unpausedPermissions = group.GroupUserPermissions;
                        unpausedPermissions.SetPaused(false);
                        syncshellsToUnpause[group.Group.GID] = unpausedPermissions;
                        _logger.LogInformation("Will unpause group (was paused by incognito): {gid}", group.Group.GID);

                        // Also unpause individual pairs within this syncshell that were paused by incognito
                        foreach (var pair in pairsInGroup)
                        {
                            if (pair.UserPair != null && pair.UserPair.OwnPermissions.IsPaused() && !_prePausedPairs.Contains(pair.UserData.UID))
                            {
                                var permissions = pair.UserPair.OwnPermissions;
                                permissions.SetPaused(false);
                                await _apiController.UserSetPairPermissions(new(pair.UserData, permissions)).ConfigureAwait(false);
                                _logger.LogInformation("Unpaused pair in syncshell (was paused by incognito): {uid} - {playerName}", pair.UserData.UID, pair.PlayerName);
                            }
                        }
                    }
                    else if (_prePausedSyncshells.Contains(group.Group.GID))
                    {
                        _logger.LogInformation("Keeping group paused (was already paused before incognito): {gid}", group.Group.GID);
                    }
                }
                
                // Apply bulk unpause to syncshells
                if (syncshellsToUnpause.Count > 0)
                {
                    await _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), syncshellsToUnpause)).ConfigureAwait(false);
                    _logger.LogInformation("Unpaused {count} syncshells after incognito mode", syncshellsToUnpause.Count);
                }
                
                // Clear the tracking set for next incognito session
                _prePausedSyncshells.Clear();
                
                _isIncognitoModeActive = false;
                _logger.LogInformation("Incognito mode deactivated");
                
                // Save configuration
                _configService.Current.IsIncognitoModeActive = _isIncognitoModeActive;
                _configService.Current.PrePausedPairs = new HashSet<string>(_prePausedPairs, StringComparer.Ordinal);
                _configService.Current.PrePausedSyncshells = new HashSet<string>(_prePausedSyncshells, StringComparer.Ordinal);
                _configService.Save();
            }
            else
            {
                // Incognito Mode: Pause user pairs that are NOT in current party
                var allUserPairs = _pairManager.DirectPairs.ToList();
        var partyMembers = await _dalamudUtilService.RunOnFrameworkThread(() => _dalamudUtilService.GetPartyMemberNames()).ConfigureAwait(false);
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
                            var inParty = !string.IsNullOrEmpty(playerName) && await IsPlayerInCurrentPartyAsync(playerName).ConfigureAwait(false);
                            if (!inParty)
                            {
                                var permissions = pair.UserPair.OwnPermissions;
                                permissions.SetPaused(true);
                                await _apiController.UserSetPairPermissions(new(pair.UserData, permissions)).ConfigureAwait(false);
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
                        var memberName = pair.PlayerName;
                        if (!string.IsNullOrEmpty(memberName) && await IsPlayerInCurrentPartyAsync(memberName).ConfigureAwait(false))
                        {
                            hasPartyMember = true;
                            break;
                        }
                    }
                    
                    if (!hasPartyMember)
                    {
                        // Store current pause state and pause the syncshell
                        if (group.GroupUserPermissions.IsPaused())
                        {
                            _prePausedSyncshells.Add(group.Group.GID);
                        }
                        var pausedPermissions = group.GroupUserPermissions;
                        pausedPermissions.SetPaused(true);
                        syncshellsToPause[group.Group.GID] = pausedPermissions;
                        _logger.LogInformation("Will pause group (no party members): {gid}", group.Group.GID);

                        // Also pause individual pairs within this syncshell that are not directly paired
                        foreach (var pair in pairsInGroup)
                        {
                            if (pair.UserPair != null && !pair.UserPair.OwnPermissions.IsPaused() && !_prePausedPairs.Contains(pair.UserData.UID))
                            {
                                var permissions = pair.UserPair.OwnPermissions;
                                permissions.SetPaused(true);
                                await _apiController.UserSetPairPermissions(new(pair.UserData, permissions)).ConfigureAwait(false);
                                _logger.LogInformation("Paused pair in syncshell (not in party): {uid} - {playerName}", pair.UserData.UID, pair.PlayerName);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Keeping group active (has party members): {gid}", group.Group.GID);
                    }
                }
                
                // Apply bulk pause to syncshells
                if (syncshellsToPause.Count > 0)
                {
                    await _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), syncshellsToPause)).ConfigureAwait(false);
                    _logger.LogInformation("Paused {count} syncshells for incognito mode", syncshellsToPause.Count);
                }
                
                _isIncognitoModeActive = true;
                _configService.Current.IsIncognitoModeActive = _isIncognitoModeActive;
                _pairManager.EnforceVanillaForPausedPairs("IncognitoModeActivated");
                _pairManager.CancelAllPendingOfflineGrace();
                _logger.LogInformation("Incognito mode activated");
                
                // Save configuration
                _configService.Current.PrePausedPairs = new HashSet<string>(_prePausedPairs, StringComparer.Ordinal);
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
            return (SpheneTheme.Current.CompactServerStatusConnected, $"Texture Size: Optimal ({textureSizeMB:F1} MB)", FontAwesomeIcon.CheckCircle);
        }
        else if (textureSizeMB <= 600)
        {
            return (SpheneTheme.Current.CompactServerStatusWarning, $"Texture Size: Large ({textureSizeMB:F1} MB)", FontAwesomeIcon.ExclamationTriangle);
        }
        else
        {
            return (SpheneTheme.Current.CompactServerStatusError, $"Texture Size: Very Large ({textureSizeMB:F1} MB)", FontAwesomeIcon.ExclamationCircle);
        }
    }

    private static void DrawRotatingSpinnerIcon(float scale = 1.0f)
    {
        // Size matches the current text line height for perfect inline alignment
        var lineHeight = ImGui.GetTextLineHeight() * scale;
        var size = new Vector2(lineHeight, lineHeight);
        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + size.X * 0.5f, pos.Y + size.Y * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        var theme = SpheneTheme.Current;
        var color = theme.TextPrimary;
        var u32 = ImGui.ColorConvertFloat4ToU32(color);
        var t = (float)ImGui.GetTime();
        var radius = size.X * 0.45f;
        var thickness = Math.Max(2.0f * ImGuiHelpers.GlobalScale, size.X * 0.08f);
        var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.25f));
        drawList.AddCircle(center, radius, bgColor, 48, thickness);

        var speed = 3.0f;
        var baseAngle = t * speed;
        var arcLen = 1.35f;
        drawList.PathClear();
        drawList.PathArcTo(center, radius, baseAngle, baseAngle + arcLen, 32);
        drawList.PathStroke(u32, ImDrawFlags.None, thickness);

        ImGui.Dummy(size);
    }

    private void DrawCompactTextureIndicator()
    {
        var (color, _, icon) = GetBc7ConversionIndicator();
        var textureSizeBytes = GetCurrentTextureSize();
        var textureSizeMB = textureSizeBytes / (1024.0 * 1024.0);

        // If automatic mode is enabled and conversion is running, show spinner instead of the icon
        if (_shrinkuConfigService.Current.TextureProcessingMode == TextureProcessingMode.Automatic && _shrinkuConversionService.IsConverting)
        {
            DrawRotatingSpinnerIcon();
            SpheneUI.AttachTooltip("Automatic conversion is running");

            ImGui.SameLine(0, 2);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.Text($"{textureSizeMB:F0}MB");
            ImGui.PopStyleColor();

            // Keep size tooltip on the MB text
            if (ImGui.IsItemHovered())
            {
                using (SpheneUI.Theme.TooltipScope())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, "Used VRAM Size");
                    ImGui.Separator();
                    ImGui.Text($"Current: {textureSizeMB:F0} MB");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneTheme.Current.CompactServerStatusConnected, "• 0-300 MB: Optimal");
                    ImGui.TextColored(SpheneTheme.Current.CompactServerStatusWarning, "• 300-600 MB: Large");
                    ImGui.TextColored(SpheneTheme.Current.CompactServerStatusError, "• 600+ MB: Very Large");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, "Tip: Click the button for quick conversion!");
                    ImGui.EndTooltip();
                }
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            SpheneUI.Icons.Icon(icon);
            ImGui.SameLine(0, 2);
            ImGui.Text($"{textureSizeMB:F0}MB");
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                using (SpheneUI.Theme.TooltipScope())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, "Texture Size");
                    ImGui.Separator();
                    ImGui.Text($"Current: {textureSizeMB:F0} MB");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneTheme.Current.CompactServerStatusConnected, "• 0-300 MB: Optimal");
                    ImGui.TextColored(SpheneTheme.Current.CompactServerStatusWarning, "• 300-600 MB: Large");
                    ImGui.TextColored(SpheneTheme.Current.CompactServerStatusError, "• 600+ MB: Very Large");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, "Tip: Click the button for quick conversion!");
                    ImGui.EndTooltip();
                }
            }
        }
    }

    /// <summary>April Fools sparkle overlay over the window surface.</summary>
    private void DrawAprilFoolsOverlay(ImDrawListPtr drawList)
    {
        var nowLocal = DateTime.Now;
        var isAprilFoolsSeason = (nowLocal.Month == 4 && nowLocal.Day == 1) || (nowLocal.Month == 3 && nowLocal.Day == 31);
        if (_aprilFoolsHyperSyncEnabled && isAprilFoolsSeason)
        {
            var elapsed = (float)(nowLocal - _aprilFoolsHyperSyncActivatedAt).TotalSeconds;
            var t = (float)ImGui.GetTime();
            var winPos = ImGui.GetWindowPos();
            var winSize = ImGui.GetWindowSize();
            var alpha = Math.Clamp(1.0f - (elapsed / 20.0f), 0.10f, 0.25f);
            var rounding = ImGui.GetStyle().WindowRounding;
            var overlayColor = new Vector4(
                0.55f + 0.35f * MathF.Sin(t * 1.9f),
                0.55f + 0.35f * MathF.Sin(t * 2.3f + 1.3f),
                0.55f + 0.35f * MathF.Sin(t * 2.7f + 2.1f),
                alpha);
            drawList.AddRectFilled(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y),
                ImGui.ColorConvertFloat4ToU32(overlayColor), rounding);

            bool IsInsideRoundedRect(float x, float y)
            {
                var min = winPos;
                var max = new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y);
                if (x < min.X || x > max.X || y < min.Y || y > max.Y) return false;
                if (rounding <= 0.0f) return true;

                var left = min.X + rounding;
                var right = max.X - rounding;
                var top = min.Y + rounding;
                var bottom = max.Y - rounding;
                if (x >= left && x <= right) return true;
                if (y >= top && y <= bottom) return true;

                float cx;
                float cy;
                if (x < left)
                    cx = left;
                else
                    cx = right;
                if (y < top)
                    cy = top;
                else
                    cy = bottom;

                var dx = x - cx;
                var dy = y - cy;
                return (dx * dx + dy * dy) <= (rounding * rounding);
            }

            var sparkleCount = 60;
            for (int i = 0; i < sparkleCount; i++)
            {
                var fx = 0.5f + 0.5f * MathF.Sin(t * 1.1f + i * 2.17f);
                var fy = 0.5f + 0.5f * MathF.Sin(t * 1.7f + i * 1.31f);
                var x = winPos.X + fx * winSize.X;
                var y = winPos.Y + fy * winSize.Y;
                if (!IsInsideRoundedRect(x, y)) continue;
                var r = 1.5f + 2.5f * (0.5f + 0.5f * MathF.Sin(t * 3.4f + i));
                var color = new Vector4(
                    0.5f + 0.5f * MathF.Sin(t * 2.3f + i * 0.7f),
                    0.5f + 0.5f * MathF.Sin(t * 2.9f + i * 1.1f),
                    0.5f + 0.5f * MathF.Sin(t * 3.1f + i * 1.4f),
                    0.55f);
                drawList.AddCircleFilled(new Vector2(x, y), r, ImGui.ColorConvertFloat4ToU32(color));
            }
        }
    }

    private static void DrawHyperSyncPopups()
    {
        if (ImGui.BeginPopupModal("HyperSync", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("HyperSync™ activated.");
            ImGui.Spacing();
            ImGui.TextWrapped("Sphene is now synchronizing packets, pixels, and vibes across all shards. This is completely normal.");
            ImGui.Spacing();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Understood"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal("HyperSyncDisabled", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("HyperSync™ disabled.");
            ImGui.Spacing();
            ImGui.TextWrapped("April Fools. There was never a HyperSync. It was just Sphene doing Sphene things.");
            ImGui.Spacing();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Okay"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

}
