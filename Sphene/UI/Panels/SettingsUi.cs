using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using Sphene.API.Data;
using Sphene.API.Dto.Files;
using Sphene.API.Data.Comparer;
using Sphene.API.Routes;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Handlers;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Design;
using Sphene.Configuration;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using Sphene.WebAPI.Files.Models;
using Sphene.WebAPI.SignalR.Utils;
using Sphene.UI.Components;
using ShrinkU.Configuration;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Sphene.UI.CharaDataHub;
using System.Reflection;
using System.Threading;

namespace Sphene.UI.Panels;

public class SettingsUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly CacheMonitor _cacheMonitor;
    private readonly SpheneConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, Dictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly HttpClient _httpClient;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileCompactor _fileCompactor;
    private readonly FileUploadManager _fileTransferManager;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly IpcManager _ipcManager;
    private readonly PairManager _pairManager;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiShared;
    private readonly ShrinkUHostService _shrinkUHostService;
    private readonly ReleaseChangelogUi _releaseChangelogUi;
    private readonly SupporterIconService _supporterIconService;
    private readonly SupporterNameplateService _supporterNameplateService;
    private readonly GameSoundManager _gameSoundManager;
    private readonly FileDialogManager _fileDialogManager;
    private readonly string _shrinkUVersion;
    private readonly IProgress<(int, int, FileCacheEntity)> _validationProgress;
    private (int, int, FileCacheEntity) _currentProgress;
    private const string SeaOfStarsRepoUrl = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/refs/heads/main/repo.json";
    private const string SeaOfStarsRepoName = "SeaOfStars Repo";
    private bool _deleteAccountPopupModalShown = false;
    private bool _deleteFilesPopupModalShown = false;
    private string _lastTab = string.Empty;

    private bool? _notesSuccessfullyApplied = null;
    private bool _overwriteExistingLabels = false;
    private bool _readClearCache = false;
    private bool _showAdvancedCharacterMode = false;
    private bool _styleguideToggle = true;
    private float _styleguideSlider = 42f;
    private string _styleguideInput = string.Empty;
    private int _styleguideComboIndex = 0;
    private static readonly string[] StyleguideComboItems = ["First option", "Second option", "Third option"];
    private int _selectedSecretKeyForNewCharacter = -1;
    private string _customSecretKeyInput = string.Empty;
    private int _selectedEntry = -1;
    private string _uidToAddForIgnore = string.Empty;
    private CancellationTokenSource? _validationCts;
    private Task<List<FileCacheEntity>>? _validationTask;
    private bool _wasOpen = false;
    private Vector2 _currentCompactUiSize = new Vector2(370, 400); // Default CompactUI size
    private bool _compactUiHasLayoutInfo = false;
    private bool? _compactUiWasOpen = null;
    private int _pluginInstallInProgress = 0;
    private string? _installingPluginName;
    // New navigation state for redesigned Settings layout
    internal enum SettingsPage
    {
        Home,
        Connectivity,
        PeopleNotes,
        Display,
        Theme,
        Alerts,
        Performance,
        Transfers,
        Storage,
        SyncBehavior,
        Acknowledgment,
        Supporter,
        ReleaseNotes,
        Debug
    }
    private SettingsPage _standaloneSettingsPage = SettingsPage.Home;
    private SettingsPage _embeddedSettingsPage = SettingsPage.Home;
    private readonly List<SettingsPage> _standaloneNavBack = [];
    private readonly List<SettingsPage> _standaloneNavForward = [];
    private readonly List<SettingsPage> _embeddedNavBack = [];
    private readonly List<SettingsPage> _embeddedNavForward = [];
    private bool _embeddedScope;

    /// <summary>
    /// The active page of the surface currently drawing or navigating — the
    /// standalone window and the Hub-embedded settings each keep their own
    /// page and back/forward history so neither hijacks the other.
    /// </summary>
    private SettingsPage _activeSettingsPage
    {
        get => _embeddedScope ? _embeddedSettingsPage : _standaloneSettingsPage;
        set
        {
            if (_embeddedScope)
            {
                _embeddedSettingsPage = value;
            }
            else
            {
                _standaloneSettingsPage = value;
            }
        }
    }

    private List<SettingsPage> _settingsNavBack => _embeddedScope ? _embeddedNavBack : _standaloneNavBack;
    private List<SettingsPage> _settingsNavForward => _embeddedScope ? _embeddedNavForward : _standaloneNavForward;
    private int _settingsCardIndex;
    private string? _preferThemeTab = null;
    private bool _preferButtonStylesTab = false;

    private static readonly string[] GeneralThemeNavLabels =
    [
        "Rounding", "Borders", "Effects", "Spacing",
        "Basic", "Window", "Frame & Input", "Button & Header",
        "Menu & Navigation", "Scrollbar & Slider", "Table & Tab"
    ];
    private static readonly string[] GeneralThemeSectionTitles =
    [
        "Window Rounding", "Border Sizes", "Window Effects", "Spacing & Padding",
        "Basic Colors", "Window Colors", "Frame & Input Colors",
        "Button & Header Colors", "Menu & Navigation Colors",
        "Scrollbar & Slider Colors", "Table & Tab Colors"
    ];
    private int _generalSectionActive;
    private int _generalScrollRequest = -1;
    private float? _generalScrollTarget;
    private readonly float[] _generalSectionTops = new float[GeneralThemeNavLabels.Length];
    private readonly ConfigBackupService _configBackupService;
    private readonly ShrinkUConfigService _shrinkUConfigService;
    private bool _showExportModal = false;
    private bool _showImportModal = false;
    private string _exportPassword = string.Empty;
    private string _importPassword = string.Empty;
    private string _importFilePath = string.Empty;
    private bool _importRestoreCachePath = true;
    private bool _importRestoreShrinkUPath = true;
    private string? _lastImportError = null;
    private string? _lastImportSuccess = null;
    private string _backupCachePath = string.Empty;
    private string _backupShrinkUPath = string.Empty;

    public SettingsUi(ILogger<SettingsUi> logger,
        UiSharedService uiShared, SpheneConfigService configService,
        PairManager pairManager,
        ServerConfigurationManager serverConfigurationManager,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        SpheneMediator mediator, PerformanceCollectorService performanceCollector,
        FileUploadManager fileTransferManager,
        FileTransferOrchestrator fileTransferOrchestrator,
        FileCacheManager fileCacheManager,
        FileCompactor fileCompactor, ApiController apiController,
        IpcManager ipcManager, CacheMonitor cacheMonitor,
        ShrinkUHostService shrinkUHostService,
        DalamudUtilService dalamudUtilService, HttpClient httpClient,
        ReleaseChangelogUi releaseChangelogUi,
        SupporterIconService supporterIconService,
        SupporterNameplateService supporterNameplateService,
        GameSoundManager gameSoundManager,
        FileDialogManager fileDialogManager,
        ConfigBackupService configBackupService,
        ShrinkUConfigService shrinkUConfigService) : base(logger, mediator, "Network Configuration", performanceCollector)
    {
        _configService = configService;
        _pairManager = pairManager;
        _serverConfigurationManager = serverConfigurationManager;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _performanceCollector = performanceCollector;
        _fileTransferManager = fileTransferManager;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileCacheManager = fileCacheManager;
        _apiController = apiController;
        _ipcManager = ipcManager;
        _cacheMonitor = cacheMonitor;
        _dalamudUtilService = dalamudUtilService;
        _httpClient = httpClient;
        _fileCompactor = fileCompactor;
        _uiShared = uiShared;
        _shrinkUHostService = shrinkUHostService;
        _releaseChangelogUi = releaseChangelogUi;
        _supporterIconService = supporterIconService;
        _supporterNameplateService = supporterNameplateService;
        _gameSoundManager = gameSoundManager;
        _fileDialogManager = fileDialogManager;
        _configBackupService = configBackupService;
        _shrinkUConfigService = shrinkUConfigService;
        _shrinkUVersion = GetShrinkUAssemblyVersion();
        AllowClickthrough = false;
        AllowPinning = false;
        Flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground;
        _validationProgress = new Progress<(int, int, FileCacheEntity)>(v => _currentProgress = v);

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(800, 400),
            MaximumSize = new Vector2(800, 2000),
        };

        Mediator.Subscribe<OpenSettingsUiMessage>(this, (_) => Toggle());
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) => LastCreatedCharacterData = msg.CharacterData);
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        Mediator.Subscribe<CompactUiChange>(this, (msg) => { _currentCompactUiSize = msg.Size; _compactUiHasLayoutInfo = true; });
        Mediator.Subscribe<ThemeNavigateToButtonSettingsMessage>(this, OnNavigateToButtonSettings);
        Mediator.Subscribe<QueryWindowOpenStateMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                msg.Respond(IsOpen);
            }
        });
    }

    public CharacterData? LastCreatedCharacterData { private get; set; }
    private ApiController ApiController => _uiShared.ApiController;

    public override void OnOpen()
    {
        _uiShared.ResetOAuthTasksState();
        _speedTestCts = new();
        
        // Store original theme state when opening settings
        _originalThemeState = SpheneTheme.Current.Clone();
        _currentThemeName = ThemeManager.GetSelectedTheme();
        _hasUnsavedThemeChanges = false;
        
        // Subscribe to theme changes to mark unsaved changes across all tabs
        SpheneTheme.Current.ThemeChanged += OnThemeChanged;
    }

    public override void OnClose()
    {
        // Unsubscribe theme change tracking
        SpheneTheme.Current.ThemeChanged -= OnThemeChanged;
        // Check for unsaved theme changes before closing
        if (CheckForUnsavedThemeChanges())
        {
            // If we need to show a prompt, keep the window open
            IsOpen = true;
            return;
        }

        _uiShared.EditTrackerPosition = false;
        _uidToAddForIgnore = string.Empty;
        _secretKeysConversionCts = _secretKeysConversionCts.CancelRecreate();
        _downloadServersTask = null;
        _speedTestTask = null;
        _speedTestCts?.Cancel();
        _speedTestCts?.Dispose();
        _secretKeysConversionCts?.CancelDispose();
        _secretKeysConversionCts = null;
        _speedTestCts = null;

        // Automatically close progress bar preview when settings are closed
        if (SpheneTheme.Current.ShowProgressBarPreview)
        {
            SpheneTheme.Current.ShowProgressBarPreview = false;
            _configService.Save();
        }
        if (SpheneTheme.Current.ShowTransmissionPreview)
        {
            SpheneTheme.Current.ShowTransmissionPreview = false;
        }
        if (SpheneTheme.Current.ForceShowUpdateHint)
        {
            SpheneTheme.Current.ForceShowUpdateHint = false;
        }
        Sphene.UI.Design.ButtonStyleManagerUI.DisablePicker();

        if (_compactUiWasOpen.HasValue)
        {
            var currentOpen = false;
            Mediator.Publish(new QueryWindowOpenStateMessage(typeof(CompactUi), state => currentOpen = state));
            if (currentOpen != _compactUiWasOpen.Value)
            {
                Mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
            }
            _compactUiWasOpen = null;
        }

        _logger.LogTrace("SettingsUi closed, lastTab={lastTab}, compactUiSize={size}", _lastTab, _currentCompactUiSize);
        base.OnClose();
    }

    private bool _suppressUnsavedForPreview = false;
    private void OnThemeChanged()
    {
        if (_suppressUnsavedForPreview)
            return;
        _hasUnsavedThemeChanges = HasSerializableThemeChanges();
    }

    private bool HasSerializableThemeChanges()
    {
        if (_originalThemeState == null)
            return false;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new Sphene.UI.Design.Vector4JsonConverter(), new Sphene.UI.Design.Vector2JsonConverter() }
        };
        var currentClone = SpheneTheme.Current.Clone();
        currentClone.ForceShowUpdateHint = _originalThemeState.ForceShowUpdateHint;
        var currentJson = JsonSerializer.Serialize(currentClone, options);
        var originalJson = JsonSerializer.Serialize(_originalThemeState, options);
        return !string.Equals(currentJson, originalJson, StringComparison.Ordinal);
    }

    protected override string ChromeTitle => "Settings";
    protected override FontAwesomeIcon? ChromeIcon => FontAwesomeIcon.Cog;
    protected override IDalamudTextureWrap? ChromeIconTexture => SpheneBrandIcons.SpheneLogo();
    protected override Action<Vector2, Vector2>? ChromeMiddleContent => DrawSettingsTitleCluster;
    protected override string? ChromeTrailingText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? null : $"v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }

    protected override IReadOnlyList<SpheneUI.Chrome.TitleBarButton>? ChromeRightButtons =>
    [
        new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.Times, "Close", () => IsOpen = false),
    ];

    protected override bool ChromeWindowPattern => true;

    protected override void DrawInternal()
    {
        SpheneUI.Lint.BeginFrame();

        UpdateCompactUiThemeStick();

        DrawSettingsContent();

        // Draw theme save prompt if needed
        DrawThemeSavePrompt();
        DrawUnsavedCustomThemePrompt();

        // Draw backup/restore modals
        DrawExportBackupModal();
        DrawImportBackupModal();
        SpheneUI.Lint.DrawOverlay();
    }
    

    private void DrawBlockedTransfers()
    {
        _lastTab = "BlockedTransfers";
        SpheneUI.Text.ColoredWrapped("Files that you attempted to upload or download that were forbidden to be transferred by their creators will appear here. " +
                             "If you see file paths from your drive here, then those files were not allowed to be uploaded. If you see hashes, those files were not allowed to be downloaded. " +
                             "Ask your paired friend to send you the mod in question through other means, acquire the mod yourself or pester the mod creator to allow it to be sent over Sphene.",
            ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Text.SectionHeader("Blocked Transfers");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        if (ImGui.BeginTable("TransfersTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Hash/Filename", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Forbidden by", ImGuiTableColumnFlags.WidthFixed, 120);

            ImGui.TableHeadersRow();

            foreach (var item in _fileTransferOrchestrator.ForbiddenTransfers)
            {
                ImGui.TableNextColumn();
                if (item is UploadFileTransfer transfer)
                {
                    SpheneUI.Text.Plain(transfer.LocalFile);
                }
                else
                {
                    SpheneUI.Text.Plain(item.Hash);
                }
                ImGui.TableNextColumn();
                SpheneUI.Text.Plain(item.ForbiddenBy);
            }
            ImGui.EndTable();
        }
    }

    private void DrawCurrentTransfers()
    {
        DrawSettingsPageHeader("Transfers", "Control transfer limits, monitor behavior, and transmission visuals.");

        DrawSettingsCard("Transfer Limits", () =>
        {
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain("Global Receive Limit");
        ImGui.SameLine();
        TransfersOptionBlock.DrawGlobalReceiveLimitValueOption(_configService, Mediator, ClampSettingsItemWidth(100f), "TransfersGlobalReceiveLimitValue");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Colored("0 = No limit/infinite", ImGuiColors.DalamudGrey3);
        ImGui.SameLine();
        TransfersOptionBlock.DrawGlobalReceiveLimitSpeedUnitOption(_configService, _uiShared, Mediator, "TransfersGlobalReceiveLimitSpeedUnit");

        TransfersOptionBlock.DrawMaximumParallelDataStreamsOption(_configService, "TransfersMaximumParallelDataStreams");
        TransfersOptionBlock.DrawUseSpheneCdnDirectDownloadsOption(_configService, _uiShared, "TransfersUseSpheneCdnDirectDownloads");
        TransfersOptionBlock.DrawAllowReceivingPenumbraModPackagesOption(_configService, _uiShared, ApiController, "TransfersAllowReceivingPenumbraModPackages");
        TransfersOptionBlock.DrawUseAlternativeTransmissionMethodOption(_configService, _uiShared, "TransfersUseAlternativeTransmissionMethod");
        });

        DrawSettingsCard("Transfer Monitor", () =>
        {
        var showTransferWindow = TransfersOptionBlock.DrawShowSeparateTransmissionMonitorOption(_configService, _uiShared, "TransfersShowSeparateTransmissionMonitor");
        using (ImRaii.Disabled(!showTransferWindow))
        {
            ImGui.Indent();
            TransfersOptionBlock.DrawEditTransmissionMonitorPositionOption(_uiShared, "TransfersEditTransmissionMonitorPosition");
            ImGui.Unindent();
        }

        var showTransferBars = TransfersOptionBlock.DrawShowTransmissionIndicatorsBelowPlayersOption(_configService, _uiShared, "TransfersShowTransmissionIndicatorsBelowPlayers");

        using (ImRaii.Disabled(!showTransferBars))
        {
            ImGui.Indent();
            TransfersOptionBlock.DrawShowTransmissionTextOption(_configService, _uiShared, "TransfersShowTransmissionText");
            TransfersOptionBlock.DrawTransmissionIndicatorWidthOption(_configService, _uiShared, "TransfersTransmissionIndicatorWidth");
            TransfersOptionBlock.DrawTransmissionIndicatorHeightOption(_configService, _uiShared, "TransfersTransmissionIndicatorHeight");
            var showUploading = TransfersOptionBlock.DrawShowTransmittingTextBelowPlayersOption(_configService, _uiShared, "TransfersShowTransmittingTextBelowPlayers");
            ImGui.Unindent();

            using (ImRaii.Disabled(!showUploading))
            {
                ImGui.Indent();
                TransfersOptionBlock.DrawLargeFontForTransmittingTextOption(_configService, _uiShared, "TransfersLargeFontForTransmittingText");
                ImGui.Unindent();
            }
        }
        });

        if (_apiController.IsConnected)
        {
            DrawSettingsCard("Speed Test to Servers", () =>
            {
            using var tree = ImRaii.TreeNode("Speed Test to Servers");
            if (tree)
            {
                if ((_downloadServersTask == null || ((_downloadServersTask?.IsCompleted ?? false) && (!_downloadServersTask?.IsCompletedSuccessfully ?? false)))
                    && SpheneUI.Icons.TextButton(FontAwesomeIcon.GroupArrowsRotate, "Update Download Server List"))
                {
                    _downloadServersTask = GetDownloadServerList();
                }
                if (_downloadServersTask != null && _downloadServersTask.IsCompleted && !_downloadServersTask.IsCompletedSuccessfully)
                {
                    SpheneUI.Text.ColoredWrapped("Failed to get download servers from service, see /xllog for more information", ImGuiColors.DalamudRed);
                }
                if (_downloadServersTask != null && _downloadServersTask.IsCompleted && _downloadServersTask.IsCompletedSuccessfully)
                {
                    if (_speedTestTask == null || _speedTestTask.IsCompleted)
                    {
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Start Speedtest"))
                        {
                            _speedTestTask = RunSpeedTest(_downloadServersTask.Result!, _speedTestCts?.Token ?? CancellationToken.None);
                        }
                    }
                    else if (!_speedTestTask.IsCompleted)
                    {
                        SpheneUI.Text.ColoredWrapped("Running Speedtest to File Servers...", ImGuiColors.DalamudYellow);
                        SpheneUI.Text.ColoredWrapped("Please be patient, depending on usage and load this can take a while.", ImGuiColors.DalamudYellow);
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Cancel speedtest"))
                        {
                            _speedTestCts?.Cancel();
                            _speedTestCts?.Dispose();
                            _speedTestCts = new();
                        }
                    }
                    if (_speedTestTask != null && _speedTestTask.IsCompleted)
                    {
                        if (_speedTestTask.Result != null && _speedTestTask.Result.Count != 0)
                        {
                            foreach (var result in _speedTestTask.Result)
                            {
                                SpheneUI.Text.Wrapped(result);
                            }
                        }
                        else
                        {
                            SpheneUI.Text.ColoredWrapped("Speedtest completed with no results", ImGuiColors.DalamudYellow);
                        }
                    }
                }
            }
            SpheneUI.Layout.Space(SpheneUI.Spacing.X10);
            });
        }

        DrawSettingsCard("Current Transfers", () =>
        {
        var transferTabs = new List<string>(2);
        if (ApiController.ServerState is ServerState.Connected)
        {
            transferTabs.Add("Transfers");
        }
        transferTabs.Add("Blocked Transfers");
        var transferTab = SpheneUI.Layout.TabStrip("TransfersTabs", transferTabs,
            scrollIntoViewOnChange: true);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        switch (transferTabs[transferTab])
        {
            case "Transfers":
            {
                SpheneUI.Text.Plain("Uploads");
                if (ImGui.BeginTable("UploadsTable", 3, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Uploaded", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableHeadersRow();
                    foreach (var transfer in _fileTransferManager.CurrentUploads.ToArray())
                    {
                        var color = SpheneUI.Colors.UploadProgress((transfer.Transferred, transfer.Total));
                        var col = ImRaii.PushColor(ImGuiCol.Text, color);
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Plain(transfer.Hash);
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Plain(UiSharedService.ByteToString(transfer.Transferred));
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Plain(UiSharedService.ByteToString(transfer.Total));
                        col.Dispose();
                        ImGui.TableNextRow();
                    }

                    ImGui.EndTable();
                }
                SpheneUI.Layout.Separator();
                SpheneUI.Text.Plain("Downloads");
                if (ImGui.BeginTable("DownloadsTable", 4, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Server", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Files", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableSetupColumn("Download", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableHeadersRow();

                    foreach (var transfer in _currentDownloads.ToArray())
                    {
                        var userName = transfer.Key.Name;
                        foreach (var entry in transfer.Value)
                        {
                            var color = SpheneUI.Colors.UploadProgress((entry.Value.TransferredBytes, entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            SpheneUI.Text.Plain(userName);
                            ImGui.TableNextColumn();
                            SpheneUI.Text.Plain(entry.Key);
                            var col = ImRaii.PushColor(ImGuiCol.Text, color);
                            ImGui.TableNextColumn();
                            SpheneUI.Text.Plain(entry.Value.TransferredFiles + "/" + entry.Value.TotalFiles);
                            ImGui.TableNextColumn();
                            SpheneUI.Text.Plain(UiSharedService.ByteToString(entry.Value.TransferredBytes) + "/" + UiSharedService.ByteToString(entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            col.Dispose();
                            ImGui.TableNextRow();
                        }
                    }

                    ImGui.EndTable();
                }
            }
            break;

            case "Blocked Transfers":
                DrawBlockedTransfers();
                break;
        }
        });
    }

    private Task<List<string>?>? _downloadServersTask = null;
    private Task<List<string>?>? _speedTestTask = null;
    private CancellationTokenSource? _speedTestCts;

    private async Task<List<string>?> RunSpeedTest(List<string> servers, CancellationToken token)
    {
        List<string> speedTestResults = new();
        foreach (var server in servers)
        {
            HttpResponseMessage? result = null;
            Stopwatch? st = null;
            try
            {
                result = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, new Uri(new Uri(server), "speedtest/run"), token, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                result.EnsureSuccessStatusCode();
                using CancellationTokenSource speedtestTimeCts = new();
                speedtestTimeCts.CancelAfter(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(speedtestTimeCts.Token, token);
                long readBytes = 0;
                st = Stopwatch.StartNew();
                try
                {
                    var stream = await result.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                    byte[] buffer = new byte[8192];
                    while (!speedtestTimeCts.Token.IsCancellationRequested)
                    {
                        var currentBytes = await stream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                        if (currentBytes == 0)
                            break;
                        readBytes += currentBytes;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Speedtest to {server} cancelled", server);
                }
                st.Stop();
                _logger.LogInformation("Downloaded {bytes} from {server} in {time}", UiSharedService.ByteToString(readBytes), server, st.Elapsed);
                var bps = (long)((readBytes) / st.Elapsed.TotalSeconds);
                speedTestResults.Add($"{server}: ~{UiSharedService.ByteToString(bps)}/s");
            }
            catch (HttpRequestException ex)
            {
                if (result != null)
                {
                    var res = await result!.Content.ReadAsStringAsync().ConfigureAwait(false);
                    speedTestResults.Add($"{server}: {ex.Message} - {res}");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Speedtest on {server} cancelled", server);
                speedTestResults.Add($"{server}: Cancelled by user");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Some exception");
            }
            finally
            {
                st?.Stop();
            }
        }
        return speedTestResults;
    }

    // History drawing moved to ModPackageHistoryUi

    private async Task<List<string>?> GetDownloadServerList()
    {
        try
        {
            var result = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, new Uri(_fileTransferOrchestrator.FilesCdnUri!, "files/downloadServers"), CancellationToken.None).ConfigureAwait(false);
            result.EnsureSuccessStatusCode();
            return await JsonSerializer.DeserializeAsync<List<string>>(await result.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get download server list");
            throw new InvalidOperationException("Failed to get download server list", ex);
        }
    }

    private void DrawDebug()
    {
        string pageLabel;
#if IS_TEST_BUILD
        pageLabel = "Debug Diagnostics";
#else
        pageLabel = "Diagnostics";
#endif

        DrawSettingsPageHeader(pageLabel, "Diagnostics and troubleshooting tools for sync, logs, and runtime status.");
        DrawSettingsCard("Diagnostic Data", () =>
        {
#if IS_TEST_BUILD
        if (LastCreatedCharacterData != null && SpheneUI.Controls.CollapsibleSection("Last created character data"))
        {
            var lastCreatedJson = JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true });
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Copy, "Copy to clipboard##lastCreatedCharacterData"))
            {
                ImGui.SetClipboardText(lastCreatedJson);
            }
            foreach (var l in lastCreatedJson.Split('\n'))
            {
                SpheneUI.Text.Plain($"{l}");
            }
        }
#endif
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Copy, "[DIAGNOSTIC] Copy Last created Character Data to clipboard"))
        {
            if (LastCreatedCharacterData != null)
            {
                ImGui.SetClipboardText(JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }));
            }
            else
            {
                ImGui.SetClipboardText("ERROR: No created character data, cannot copy.");
            }
        }
        SpheneUI.AttachTooltip("Use this when reporting modifications being rejected from the Network.");

        DebugOptionBlock.DrawLogLevelOption(_configService, _uiShared, "DebugLogLevel");
        });

        DrawSettingsCard("Performance Metrics", () =>
        {
        bool logPerformance = DebugOptionBlock.DrawLogNetworkPerformanceMetricsOption(_configService, _uiShared, "DebugLogNetworkPerformanceMetrics");
        DebugOptionBlock.DrawPrintNetworkMetricsActions(_uiShared, logPerformance, () => _performanceCollector.PrintPerformanceStats(), () => _performanceCollector.PrintPerformanceStats(60), "DebugPrintNetworkMetricsActions");
        DebugOptionBlock.DrawDoNotNotifyForModifiedGameFilesOrEnabledLodOption(_configService, _uiShared, "DebugDoNotNotifyForModifiedGameFilesOrEnabledLod");
        });

        DrawSettingsCard("Diagnostic Windows", () =>
        {
        DebugOptionBlock.DrawOpenAcknowledgmentMonitorAction(_uiShared, Mediator, "DebugOpenAcknowledgmentMonitor");
        DebugOptionBlock.DrawOpenStatusDebugAction(_uiShared, Mediator, "DebugOpenStatusDebug");
        }, "Open dedicated windows for acknowledgment and status monitoring.");

        DrawSettingsCard("Active Mismatch Tracker", () =>
        {
        DebugOptionBlock.DrawActiveMismatchTrackerFilterOptions(_configService, _uiShared, "DebugMismatchTrackerFilters");
        }, "Control which paths and file types are recorded and shown.");

        DrawSettingsCard("Design Lint", () =>
        {
        var lintEnabled = SpheneUI.Lint.Enabled;
        if (SpheneUI.Controls.Toggle("Highlight layout violations##designLintEnabled", ref lintEnabled))
        {
            SpheneUI.Lint.Enabled = lintEnabled;
        }
        SpheneUI.AttachTooltip("When enabled, shared UI controls are checked against the card bounds every " +
            "frame. Controls that overshoot the card edge get a red outline and are listed below.");

        if (SpheneUI.Lint.Enabled)
        {
            var violations = SpheneUI.Lint.Violations;
            if (violations.Count == 0)
            {
                SpheneUI.Text.Colored("No layout violations detected this frame.", ImGuiColors.DalamudGrey3);
            }
            else
            {
                SpheneUI.Text.Colored($"{violations.Count} violation(s) this frame:", ImGuiColors.DalamudYellow);
                for (var i = 0; i < violations.Count; i++)
                {
                    var violation = violations[i];
                    SpheneUI.Text.ColoredWrapped(
                        $"• {violation.Context} — {violation.Rule}: {violation.Detail}", ImGuiColors.DalamudGrey3);
                }
            }
        }
        }, "Runtime checks that flag controls poking past the shared card edge.");

        DrawSettingsCard("Design System", () =>
        {
        SpheneUI.Text.SectionHeader("Text & Icons");
        SpheneUI.Text.Plain("Plain body text");
        SpheneUI.Text.Muted("Muted secondary text");
        SpheneUI.Text.Colored("Accent text", SpheneUI.Colors.Accent);
        SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle, SpheneUI.Colors.Accent);
        ImGui.SameLine();
        SpheneUI.Text.Plain("Icon with inline label");
        SpheneUI.Controls.HelpText("Help icons dock to the shared trailing column.");

        SpheneUI.Layout.SectionSeparator("Controls");
        SpheneUI.Controls.Toggle("Toggle control##styleguideToggle", ref _styleguideToggle);
        SpheneUI.Controls.SliderFloat("Slider##styleguideSlider", ref _styleguideSlider, 0f, 100f, "%.0f");
        SpheneUI.Controls.InputText("Input##styleguideInput", ref _styleguideInput, 64);
        var comboIndex = _styleguideComboIndex;
        if (SpheneUI.Controls.LabeledCombo("Combo##styleguideCombo", ref comboIndex, StyleguideComboItems,
            StyleguideComboItems.Length))
        {
            _styleguideComboIndex = comboIndex;
        }
        SpheneUI.Controls.Button("Standard button");
        ImGui.SameLine();
        SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Icon button");

        SpheneUI.Layout.SectionSeparator("Feedback");
        SpheneUI.Controls.StatusDot("Online service", true);
        SpheneUI.Controls.ProgressBar("Transfer", 0.65f, "65%");

        SpheneUI.Layout.SectionSeparator("Media");
        SpheneUI.Media.Placeholder(new Vector2(96f * SpheneUI.Scale));
        SpheneUI.AttachTooltip("Media.Image / Avatar / ImageBackground render here once a texture is bound.");
        }, "Live preview of the shared design-system controls (SpheneUI facade).");
    }

    private void DrawFileStorageSettings()
    {
        DrawSettingsPageHeader("Storage",
            "Sphene stores downloaded files from paired users to improve loading speed and reduce repeated downloads. Storage is self-managed by size limit.");

        _uiShared.DrawFileScanState();
        DrawSettingsCard("Monitoring", () =>
        {
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain("Monitoring Penumbra Folder: " + (_cacheMonitor.PenumbraWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.PenumbraWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("penumbraMonitor");
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            }
        }

        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain("Monitoring Sphene Storage Folder: " + (_cacheMonitor.SpheneWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.SpheneWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("spheneMonitor");
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartSpheneWatcher(_configService.Current.CacheFolder);
            }
        }
        if (_cacheMonitor.SpheneWatcher == null || _cacheMonitor.PenumbraWatcher == null)
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Play, "Resume Monitoring"))
            {
                _cacheMonitor.StartSpheneWatcher(_configService.Current.CacheFolder);
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
                _cacheMonitor.InvokeScan();
            }
            SpheneUI.AttachTooltip("Resumes monitoring for both Penumbra and Sphene Storage. Also triggers a full rescan and index rebuild." + Environment.NewLine
                + "If the button remains present after clicking, consult /xllog for errors.");
        }
        else
        {
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Stop, "Stop Monitoring"))
                {
                    _cacheMonitor.StopMonitoring();
                }
            }
            SpheneUI.AttachTooltip("Stops monitoring for both Penumbra and Sphene Storage. "
                + "Only stop monitoring when moving the Penumbra or Sphene Storage folders to maintain correct functionality." + Environment.NewLine
                + "Resume monitoring once you finish moving files." + SpheneUI.TooltipSeparator + "Hold CTRL to enable this button.");
        }

        });

        DrawSettingsCard("Storage & Compactor", () =>
        {
        _uiShared.DrawCacheDirectorySetting();

        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
        _uiShared.DrawPenumbraModDownloadFolderSetting();

        StorageOptionBlock.DrawDeleteDownloadedModsAfterSuccessfulInstallOption(_configService, _uiShared, "StorageDeleteDownloadedModsAfterSuccessfulInstall");

        ImGui.AlignTextToFramePadding();
        if (_cacheMonitor.FileCacheSize >= 0)
            SpheneUI.Text.Plain($"Currently utilized local storage: {UiSharedService.ByteToString(_cacheMonitor.FileCacheSize)}");
        else
            SpheneUI.Text.Plain($"Currently utilized local storage: Calculating...");
        SpheneUI.Text.Plain($"Remaining space free on drive: {UiSharedService.ByteToString(_cacheMonitor.FileCacheDriveFree)}");
        bool useFileCompactor = _configService.Current.UseCompactor;
        bool isLinux = _dalamudUtilService.IsWine;
        if (!useFileCompactor && !isLinux)
        {
            SpheneUI.Text.ColoredWrapped("Hint: Consider enabling the File Compactor to reduce disk usage.", ImGuiColors.DalamudYellow);
        }
        using (ImRaii.Disabled(isLinux || !_cacheMonitor.StorageisNTFS))
        {
        StorageOptionBlock.DrawUseFileCompactorOption(_configService, _uiShared, "StorageUseFileCompactor", "StorageCompactorPopup");
        if (SpheneUI.Popups.BeginStyled("StorageCompactorPopup"))
        {
            if (!_fileCompactor.MassCompactRunning)
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileArchive, "Compact all files in storage"))
                {
                    _ = Task.Run(() =>
                    {
                        _fileCompactor.CompactStorage(compress: true);
                        _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                    });
                }
                SpheneUI.AttachTooltip("This will run compression on all files in your current Sphene Storage." + Environment.NewLine
                    + "You do not need to run this manually if you keep the file compactor enabled.");
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.File, "Decompact all files in storage"))
                {
                    _ = Task.Run(() =>
                    {
                        _fileCompactor.CompactStorage(compress: false);
                        _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                    });
                }
                SpheneUI.AttachTooltip("This will run decompression on all files in your current Sphene Storage.");
            }
            else
            {
                SpheneUI.Text.Colored($"File compactor currently running ({_fileCompactor.Progress})", ImGuiColors.DalamudYellow);
            }
            SpheneUI.Popups.EndStyled();
        }
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS)
        {
            SpheneUI.Text.Plain("The file compactor is only available on Windows and NTFS drives.");
        }
        SpheneUI.Layout.Space(SpheneUI.Spacing.X10);
        });

        DrawSettingsCard("Storage Validation", () =>
        {
        using (ImRaii.Disabled(_validationTask != null && !_validationTask.IsCompleted))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Start File Storage Validation"))
            {
                _validationCts?.Cancel();
                _validationCts?.Dispose();
                _validationCts = new();
                var token = _validationCts.Token;
                _validationTask = Task.Run(() => _fileCacheManager.ValidateLocalIntegrity(_validationProgress, token));
            }
        }
        if (_validationTask != null && !_validationTask.IsCompleted)
        {
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
            {
                _validationCts?.Cancel();
            }
        }

        if (_validationTask != null)
        {
            using (ImRaii.PushIndent(20f))
            {
                if (_validationTask.IsCompleted)
                {
                    SpheneUI.Text.Wrapped($"The storage validation has completed and removed {_validationTask.Result.Count} invalid files from storage.");
                }
                else
                {

                    SpheneUI.Text.Wrapped($"Storage validation is running: {_currentProgress.Item1}/{_currentProgress.Item2}");
                    SpheneUI.Text.Wrapped($"Current item: {_currentProgress.Item3.ResolvedFilepath}");
                }
            }
        }
        }, "Validation checks local storage integrity and removes invalid files. This can take time and may be CPU and disk intensive.");

        DrawSettingsCard("Clear Local Storage", () =>
        {
        ImGui.Indent();
        SpheneUI.Text.ColoredWrapped("Clearing the local storage forces a re-download of all synced data:" + Environment.NewLine
            + "- It puts the file servers of your connected services under extra strain." + Environment.NewLine
            + "- It is not a step to try to fix sync issues." + Environment.NewLine
            + "- It can make the situation of not getting other players data worse in situations of heavy file server load.",
            ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        StorageOptionBlock.DrawReadClearLocalStorageDisclaimerOption(ref _readClearCache, "StorageReadClearLocalStorageDisclaimer");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        bool clearPressed;
        using (ImRaii.Disabled(!_readClearCache))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
            {
                clearPressed = SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear local storage");
            }
            SpheneUI.AttachTooltip("You normally do not need to do this. THIS IS NOT SOMETHING YOU SHOULD BE DOING TO TRY TO FIX SYNC ISSUES." + Environment.NewLine
                + "This will solely remove all downloaded data from all players and will require you to re-download everything again." + Environment.NewLine
                + "Sphenes storage is self-clearing and will not surpass the limit you have set it to." + Environment.NewLine
                + "If you still think you need to do this hold CTRL while pressing the button.");
        }
        if (SpheneUI.Controls.SameLineIfFits(ImGui.CalcTextSize("Hold CTRL while clicking to confirm.").X))
        {
            SpheneUI.Text.Colored("Hold CTRL while clicking to confirm.", ImGuiColors.DalamudGrey3);
        }
        if (clearPressed && UiSharedService.CtrlPressed() && _readClearCache)
        {
            _ = Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(_configService.Current.CacheFolder))
                {
                    File.Delete(file);
                }
            });
        }
        ImGui.Unindent();
        }, "Read and accept the disclaimer before running this action.", ImGuiColors.DalamudRed);
    }



    private void DrawPerformance()
    {
        DrawSettingsPageHeader("Performance",
            "Configure warning indicators and automatic actions for performance-heavy synced players.");
        DrawSettingsCard("Warnings & Indicators", () =>
        {
            bool showPerformanceIndicator = PerformanceOptionBlock.DrawShowPerformanceIndicatorOption(_playerPerformanceConfigService, _uiShared, "PerformanceShowPerformanceIndicator");
            bool warnOnExceedingThresholds = PerformanceOptionBlock.DrawWarnOnLoadingInPlayersExceedingThresholdsOption(_playerPerformanceConfigService, _uiShared, "PerformanceWarnOnLoadingInPlayersExceedingThresholds");
            using (ImRaii.Disabled(!warnOnExceedingThresholds && !showPerformanceIndicator))
            {
                using var indent = ImRaii.PushIndent();
                PerformanceOptionBlock.DrawWarnIndicateAlsoOnPreferredPermissionsOption(_playerPerformanceConfigService, _uiShared, "PerformanceWarnIndicateAlsoOnPreferredPermissions");
            }
            using (ImRaii.Disabled(!showPerformanceIndicator && !warnOnExceedingThresholds))
            {
                PerformanceOptionBlock.DrawWarningVramThresholdOption(_playerPerformanceConfigService, _uiShared, "PerformanceWarningVramThreshold");
                PerformanceOptionBlock.DrawWarningTriangleThresholdOption(_playerPerformanceConfigService, _uiShared, "PerformanceWarningTriangleThreshold");
            }
        });
        DrawSettingsCard("Auto Pause", () =>
        {
            bool autoPause = PerformanceOptionBlock.DrawAutomaticallyPausePlayersExceedingThresholdsOption(_playerPerformanceConfigService, _uiShared, "PerformanceAutomaticallyPausePlayersExceedingThresholds");
            using (ImRaii.Disabled(!autoPause))
            {
                using var indent = ImRaii.PushIndent();
                PerformanceOptionBlock.DrawAutomaticallyPauseAlsoPreferredPermissionsOption(_playerPerformanceConfigService, _uiShared, "PerformanceAutomaticallyPauseAlsoPreferredPermissions");
                PerformanceOptionBlock.DrawAutoPauseVramThresholdOption(_playerPerformanceConfigService, _uiShared, "PerformanceAutoPauseVramThreshold");
                PerformanceOptionBlock.DrawAutoPauseTriangleThresholdOption(_playerPerformanceConfigService, _uiShared, "PerformanceAutoPauseTriangleThreshold");
            }
        });
        DrawSettingsCard("Whitelisted Users", () =>
        {
            var addUidTrailing = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Plus, "Add handle to whitelist")
                + ImGui.GetStyle().ItemSpacing.X + SpheneUI.Icons.Size(FontAwesomeIcon.QuestionCircle).X
                + ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX() - addUidTrailing);
        SpheneUI.Controls.InputText("##ignoreuid", ref _uidToAddForIgnore, 20);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnore)))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add handle to whitelist"))
            {
                if (!_playerPerformanceConfigService.Current.UIDsToIgnore.Contains(_uidToAddForIgnore, StringComparer.Ordinal))
                {
                    _playerPerformanceConfigService.Current.UIDsToIgnore.Add(_uidToAddForIgnore);
                    _playerPerformanceConfigService.Save();
                }
                _uidToAddForIgnore = string.Empty;
            }
        }
        SpheneUI.Controls.HelpText("Hint: handles are case sensitive.");
        var playerList = _playerPerformanceConfigService.Current.UIDsToIgnore;
        ImGui.SetNextItemWidth(ClampSettingsItemWidth(420f, 200f));
        using (var lb = ImRaii.ListBox("Handle whitelist"))
        {
            if (lb)
            {
                for (int i = 0; i < playerList.Count; i++)
                {
                    bool shouldBeSelected = _selectedEntry == i;
                    var identifier = playerList[i];
                    
                    // Try to get user note by finding the pair
                    var pair = _pairManager.GetPairByUID(identifier);
                    if (pair == null)
                    {
                        // If not found by UID, try to find by alias
                        pair = _pairManager.DirectPairs.FirstOrDefault(p => 
                            string.Equals(p.UserData.Alias, identifier, StringComparison.Ordinal));
                    }
                    
                    var displayText = pair?.UserData.PreferredName ?? identifier;
                    var note = pair?.GetNote();
                    if (!string.IsNullOrEmpty(note))
                    {
                        displayText = $"{displayText} ({note})";
                    }
                    
                    if (SpheneUI.Controls.Selectable(displayText + "##" + i, shouldBeSelected))
                    {
                        _selectedEntry = i;
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntry == -1))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Delete selected entry"))
            {
                _playerPerformanceConfigService.Current.UIDsToIgnore.RemoveAt(_selectedEntry);
                _selectedEntry = -1;
                _playerPerformanceConfigService.Save();
            }
        }
        }, "Entries below are ignored for warnings and auto-pause operations.");
    }

    private void DrawServerConfiguration()
    {
        DrawSettingsPageHeader("Connectivity", "Manage server connection, account actions, and service-related settings.");
        if (ApiController.ServerAlive)
        {
            DrawSettingsCard("Service Actions", () =>
            {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            if (SpheneUI.Controls.Button("Delete all my files"))
            {
                _deleteFilesPopupModalShown = true;
                ImGui.OpenPopup("Delete all your files?");
            }

            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Delete account"))
            {
                _deleteAccountPopupModalShown = true;
                ImGui.OpenPopup("Delete your account?");
            }
            SpheneUI.Controls.HelpText("Deletes all your uploaded files on the service, or your account and all uploaded data.");

            if (SpheneUI.Popups.BeginModalStyled("Delete all your files?", ref _deleteFilesPopupModalShown))
            {
                using var cardBound = SpheneUI.Layout.SuppressBound();
                SpheneUI.Text.Wrapped(
                    "All your own uploaded files on the service will be deleted.\nThis operation cannot be undone.");
                SpheneUI.Text.Plain("Are you sure you want to continue?");
                SpheneUI.Layout.Separator();
                SpheneUI.Layout.VerticalSpace();

                switch (SpheneUI.Controls.DialogButtons("deleteFiles", "Delete everything",
                    "Cancel##cancelDelete", confirmDanger: true))
                {
                    case 1:
                        _ = Task.Run(_fileTransferManager.DeleteAllFiles);
                        _deleteFilesPopupModalShown = false;
                        break;
                    case 2:
                        _deleteFilesPopupModalShown = false;
                        break;
                }

                SpheneUI.Layout.SetScaledWindowSize(325);
                SpheneUI.Popups.EndStyled();
            }
            if (SpheneUI.Popups.BeginModalStyled("Delete your account?", ref _deleteAccountPopupModalShown))
            {
                using var cardBound = SpheneUI.Layout.SuppressBound();
                SpheneUI.Text.Wrapped(
                    "Your account and all associated files and data on the service will be deleted.");
                SpheneUI.Text.Wrapped("Your entry will be removed from all pairing lists.");
                SpheneUI.Text.Plain("Are you sure you want to continue?");
                SpheneUI.Layout.Separator();
                SpheneUI.Layout.VerticalSpace();

                switch (SpheneUI.Controls.DialogButtons("deleteAccount", "Delete account",
                    "Cancel##cancelDelete", confirmDanger: true))
                {
                    case 1:
                        _ = Task.Run(ApiController.UserDelete);
                        _deleteAccountPopupModalShown = false;
                        Mediator.Publish(new SwitchToIntroUiMessage());
                        break;
                    case 2:
                        _deleteAccountPopupModalShown = false;
                        break;
                }

                SpheneUI.Layout.SetScaledWindowSize(325);
                SpheneUI.Popups.EndStyled();
            }
            }, accent: ImGuiColors.DalamudRed);
        }

        DrawSettingsCard("Service & Character Settings", () =>
        {
        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
        ConnectivityOptionBlock.DrawSendStatisticalCensusDataOption(_serverConfigurationManager, _uiShared, "ConnectivitySendStatisticalCensusData");
        SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

        int idx = _uiShared.DrawServiceSelection();
        if (_lastSelectedServerIndex != idx)
        {
            _uiShared.ResetOAuthTasksState();
            _secretKeysConversionCts = _secretKeysConversionCts.CancelRecreate();
            _secretKeysConversionTask = null;
            _lastSelectedServerIndex = idx;
        }

        SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

        var selectedServer = _serverConfigurationManager.GetServerByIndex(idx);
        if (selectedServer == _serverConfigurationManager.CurrentServer)
        {
            SpheneUI.Text.ColoredWrapped("For any changes to be applied to the current service you need to reconnect to the service.", ImGuiColors.DalamudYellow);
        }

        bool useOauth = selectedServer.UseOAuth2;

        var serverTabs = new List<string>(5) { "Overview", "Character Management" };
        if (!useOauth)
        {
            serverTabs.Add("Secret Keys");
        }
        serverTabs.Add("Service Configuration");
        serverTabs.Add("Permission Settings");
        var serverTab = SpheneUI.Layout.TabStrip("serverTabs", serverTabs,
            scrollIntoViewOnChange: true);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        switch (serverTabs[serverTab])
        {
            // Overview tab: quick status and actions
            case "Overview":
            {
                SpheneUI.Text.Big("Network Overview");
                SpheneUI.Text.Wrapped($"Service: {selectedServer.ServerName}");
                SpheneUI.Text.Wrapped($"URI: {selectedServer.ServerUri}");
                SpheneUI.Text.Wrapped($"Status: {_apiController.ServerState}");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                if (_apiController.IsConnected)
                {
                    if (SpheneUI.Controls.Button("Disconnect from Service") && _serverConfigurationManager.CurrentServer != null)
                    {
                        _serverConfigurationManager.CurrentServer.FullPause = true;
                        _serverConfigurationManager.Save();
                        _ = _uiShared.ApiController.CreateConnectionsAsync();
                    }
                    SpheneUI.Controls.HelpText("Disconnect the current session from the selected service.");
                }
                else
                {
                    if (SpheneUI.Controls.Button("Connect to Service") && _serverConfigurationManager.CurrentServer != null)
                    {
                        _serverConfigurationManager.CurrentServer.FullPause = false;
                        _serverConfigurationManager.Save();
                        _ = _uiShared.ApiController.CreateConnectionsAsync();
                    }
                    SpheneUI.Controls.HelpText("Establish a connection to the selected service.");
                }

            }
            break;

            case "Character Management":
            {
                // Primary authentication & character linking
                if (selectedServer.SecretKeys.Any() || useOauth)
                {
                    SpheneUI.Text.ColoredWrapped("Characters listed here will automatically connect to the selected Sphene service with the settings as provided below." +
                        " Make sure to enter the character names correctly or use the 'Add current character' button at the bottom.", ImGuiColors.DalamudYellow);
                    int i = 0;
                    _uiShared.DrawUpdateOAuthUIDsButton(selectedServer);

                    if (selectedServer.UseOAuth2 && !string.IsNullOrEmpty(selectedServer.OAuthToken))
                    {
                        bool hasSetSecretKeysButNoUid = selectedServer.Authentications.Exists(u => u.SecretKeyIdx != -1 && string.IsNullOrEmpty(u.UID));
                        if (hasSetSecretKeysButNoUid)
                        {
                            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                            SpheneUI.Text.Wrapped("Some entries have been detected that have previously been assigned secret keys but no account. " +
                                "Press this button below to attempt to convert those entries.");
                            using (ImRaii.Disabled(_secretKeysConversionTask != null && !_secretKeysConversionTask.IsCompleted))
                            {
                                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowsLeftRight, "Try to Convert Secret Keys to Accounts"))
                                {
                                    var token = _secretKeysConversionCts?.Token ?? CancellationToken.None;
                                    _secretKeysConversionTask = ConvertSecretKeysToUIDs(selectedServer, token);
                                }
                            }
                            if (_secretKeysConversionTask != null && !_secretKeysConversionTask.IsCompleted)
                            {
                                SpheneUI.Text.ColoredWrapped("Converting secret keys to accounts", ImGuiColors.DalamudYellow);
                            }
                            if (_secretKeysConversionTask != null && _secretKeysConversionTask.IsCompletedSuccessfully)
                            {
                                Vector4? textColor = null;
                                if (_secretKeysConversionTask.Result.PartialSuccess)
                                {
                                    textColor = ImGuiColors.DalamudYellow;
                                }
                                if (!_secretKeysConversionTask.Result.Success)
                                {
                                    textColor = ImGuiColors.DalamudRed;
                                }
                                string text = $"Conversion has completed: {_secretKeysConversionTask.Result.Result}";
                                if (textColor == null)
                                {
                                    SpheneUI.Text.Wrapped(text);
                                }
                                else
                                {
                                    SpheneUI.Text.ColoredWrapped(text, textColor!.Value);
                                }
                                if (!_secretKeysConversionTask.Result.Success || _secretKeysConversionTask.Result.PartialSuccess)
                                {
                                    SpheneUI.Text.Wrapped("In case of conversion failures, please set the accounts for the failed conversions manually.");
                                }
                            }
                        }
                    }
                    SpheneUI.Layout.Separator();
                    string youName = _dalamudUtilService.GetPlayerName();
                    uint youWorld = _dalamudUtilService.GetHomeWorldId();
                    ulong youCid = _dalamudUtilService.GetCID();
                    if (!selectedServer.Authentications.Exists(a => string.Equals(a.CharacterName, youName, StringComparison.Ordinal) && a.WorldId == youWorld))
                    {
                        SpheneUI.Text.Big("Your Character is not Configured", ImGuiColors.DalamudRed);
                        SpheneUI.Text.ColoredWrapped("You have currently no character configured that corresponds to your current name and world.", ImGuiColors.DalamudRed);
                        var authWithCid = selectedServer.Authentications.Find(f => f.LastSeenCID == youCid);
                        if (authWithCid != null)
                        {
                            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                            SpheneUI.Text.Colored("A potential rename/world change from this character was detected:", ImGuiColors.DalamudYellow);
                            using (ImRaii.PushIndent(10f))
                                SpheneUI.Text.Colored("Entry: " + authWithCid.CharacterName + " - " + _dalamudUtilService.WorldData.Value[(ushort)authWithCid.WorldId], ImGuiColors.ParsedGreen);
                            SpheneUI.Text.Colored("Press the button below to adjust that entry to your current character:", ImGuiColors.DalamudYellow);
                            using (ImRaii.PushIndent(10f))
                                SpheneUI.Text.Colored("Current: " + youName + " - " + _dalamudUtilService.WorldData.Value[(ushort)youWorld], ImGuiColors.ParsedGreen);
                            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Update Entry to Current Character"))
                            {
                                authWithCid.CharacterName = youName;
                                authWithCid.WorldId = youWorld;
                                _serverConfigurationManager.Save();
                            }
                        }
                        else
                        {
                            var worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)youWorld, out var wn) ? wn : youWorld.ToString();
                            SpheneUI.Text.Big("Your Character", ImGuiColors.DalamudRed);
                            SpheneUI.Text.Colored($"{youName} @ {worldName}", ImGuiColors.DalamudWhite);
                            SpheneUI.Text.Colored("is not yet synced to Sphene.", ImGuiColors.DalamudGrey);
                            SpheneUI.Text.Colored("Add it below to start syncing.", ImGuiColors.DalamudGrey);
                            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                            if (!useOauth && selectedServer.SecretKeys.Any())
                            {
                                var keys = selectedServer.SecretKeys.OrderBy(u => u.Key).ToDictionary(k => k.Key, k => k.Value);
                                var initialKey = keys.ContainsKey(_selectedSecretKeyForNewCharacter)
                                    ? keys.First(f => f.Key == _selectedSecretKeyForNewCharacter)
                                    : keys.First();
                                if (_selectedSecretKeyForNewCharacter == -1 || !keys.ContainsKey(_selectedSecretKeyForNewCharacter))
                                {
                                    _selectedSecretKeyForNewCharacter = initialKey.Key;
                                }
                                SpheneUI.Text.Colored("Secret Key:", ImGuiColors.DalamudWhite);
                                ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
                                SpheneUI.Controls.Combo("##newCharKey", keys,
                                    (w) => w.Value.FriendlyName,
                                    (w) => { _selectedSecretKeyForNewCharacter = w.Key; },
                                    initialKey);
                                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                                SpheneUI.Text.Colored("or enter a new key:", ImGuiColors.DalamudGrey);
                                ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
                                SpheneUI.Controls.InputTextWithHint("##customSecretKeySettings", "enter secret key here", ref _customSecretKeyInput, 255);
                                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                            }
                            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.User, "Add current character"))
                            {
                                int keyToUse = -1;
                                if (!useOauth)
                                {
                                    if (!string.IsNullOrWhiteSpace(_customSecretKeyInput))
                                    {
                                        var friendlyName = _customSecretKeyInput.Length > 10
                                            ? _customSecretKeyInput[..10] + "..."
                                            : _customSecretKeyInput;
                                        keyToUse = _serverConfigurationManager.AddSecretKey(idx, _customSecretKeyInput.Trim(), friendlyName);
                                        _customSecretKeyInput = string.Empty;
                                    }
                                    else
                                    {
                                        keyToUse = _selectedSecretKeyForNewCharacter;
                                    }
                                }
                                _serverConfigurationManager.AddCurrentCharacterToServer(idx, keyToUse);
                                _ = _apiController.CreateConnectionsAsync();
                            }
                        }
                        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                        SpheneUI.Layout.Separator();
                        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                    }
                    if (selectedServer.Authentications.Any())
                    {
                        SpheneUI.Controls.Toggle("Edit Mode", ref _showAdvancedCharacterMode);
                        SpheneUI.AttachTooltip("When enabled, character names and homeworlds become editable and the stored CID is visible in the tooltip.");
                        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                        SpheneUI.Layout.Space(SpheneUI.Spacing.Hairline);
                    }

                    var sortedAuths = selectedServer.Authentications
                        .OrderByDescending(a => string.Equals(a.CharacterName, youName, StringComparison.Ordinal) && a.WorldId == youWorld)
                        .ToList();
                    foreach (var item in sortedAuths)
                    {
                        using var charaId = ImRaii.PushId("selectedChara" + i);

                        var drawList = ImGui.GetWindowDrawList();
                        float rowStartY = ImGui.GetCursorScreenPos().Y;

                        var worldIdx = (ushort)item.WorldId;
                        var data = _uiShared.WorldData.OrderBy(u => u.Value, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value);
                        if (!data.TryGetValue(worldIdx, out string? worldPreview))
                        {
                            worldPreview = data.First().Value;
                        }

                        Dictionary<int, SecretKey> keys = [];

                        if (!useOauth)
                        {
                            var secretKeyIdx = item.SecretKeyIdx;
                            keys = selectedServer.SecretKeys;
                            if (!keys.TryGetValue(secretKeyIdx, out var secretKey))
                            {
                                secretKey = new();
                            }
                        }

                        bool thisIsYou = false;
                        if (string.Equals(youName, item.CharacterName, StringComparison.OrdinalIgnoreCase)
                            && youWorld == worldIdx)
                        {
                            thisIsYou = true;
                        }
                        bool misManaged = false;
                        if (selectedServer.UseOAuth2 && !string.IsNullOrEmpty(selectedServer.OAuthToken) && string.IsNullOrEmpty(item.UID))
                        {
                            misManaged = true;
                        }
                        if (!selectedServer.UseOAuth2 && item.SecretKeyIdx == -1)
                        {
                            misManaged = true;
                        }
                        Vector4 warningColor = ImGuiColors.ParsedGreen;
                        string warningText = string.Empty;
                        if (misManaged)
                        {
                            warningText = "[MISMANAGED (" + (selectedServer.UseOAuth2 ? "No Account Set" : "No Secret Key Set") + ")]";
                            warningColor = ImGuiColors.DalamudRed;
                        }
                        if (selectedServer.Authentications.Where(e => e != item).Any(e => string.Equals(e.CharacterName, item.CharacterName, StringComparison.Ordinal)
                            && e.WorldId == item.WorldId))
                        {
                            warningText = "[DUPLICATE]";
                            warningColor = ImGuiColors.DalamudRed;
                        }

                        if (!string.IsNullOrEmpty(warningText))
                        {
                            SpheneUI.Text.Big(warningText, warningColor);
                        }
                        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                        if (item.LastSeenCID != null && item.LastSeenCID != 0)
                        {
                            SpheneUI.Icons.Icon(FontAwesomeIcon.Fingerprint, ImGuiColors.ParsedGreen);
                            var tooltipText = _showAdvancedCharacterMode
                                ? $"This character's unique ID (CID) is stored locally." +
                                  $" This allows Sphene to automatically detect and correct name or world changes for this character.\nStored CID: {item.LastSeenCID}"
                                : "This character's unique ID (CID) is stored locally. This allows Sphene to automatically detect and correct name or world changes for this character.";
                            SpheneUI.AttachTooltip(tooltipText);
                        }
                        else
                        {
                            SpheneUI.Icons.Icon(FontAwesomeIcon.Fingerprint, ImGuiColors.DalamudGrey);
                            SpheneUI.AttachTooltip("No CID stored for this character. Sphene will not be able to automatically detect name or world changes. Log in with this character to populate the CID.");
                        }
                        if (!_showAdvancedCharacterMode)
                        {
                        ImGui.SameLine();
                        SpheneUI.Layout.HorizontalSpace(SpheneUI.Spacing.Hairline);
                        }
                        ImGui.SameLine();

                        if (_showAdvancedCharacterMode)
                        {
                            var charaName = item.CharacterName;
                            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX() * 0.45f);
                            if (SpheneUI.Controls.InputText("##charaName" + i, ref charaName, 64))
                            {
                                item.CharacterName = charaName;
                                _serverConfigurationManager.Save();
                            }

                            ImGui.SameLine();

                            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
                            SpheneUI.Controls.Combo("##world" + item.CharacterName + i, data, (w) => w.Value,
                                (w) =>
                                {
                                    if (item.WorldId != w.Key)
                                    {
                                        item.WorldId = w.Key;
                                        _serverConfigurationManager.Save();
                                    }
                                }, EqualityComparer<KeyValuePair<ushort, string>>.Default.Equals(data.FirstOrDefault(f => f.Key == worldIdx), default) ? data.First() : data.First(f => f.Key == worldIdx));
                        }
                        else
                        {
                            ImGui.AlignTextToFramePadding();
                            var nameColor = thisIsYou ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudWhite;
                            SpheneUI.Text.Colored(item.CharacterName + " @ " + worldPreview, nameColor);
                        }

                        using (ImRaii.PushIndent())
                        {
                            if (!useOauth)
                            {
                                SpheneUI.Controls.Combo("Secret Key###" + item.CharacterName + i, keys, (w) => w.Value.FriendlyName,
                                    (w) =>
                                    {
                                        if (w.Key != item.SecretKeyIdx)
                                        {
                                            item.SecretKeyIdx = w.Key;
                                            _serverConfigurationManager.Save();
                                        }
                                    }, EqualityComparer<KeyValuePair<int, SecretKey>>.Default.Equals(keys.FirstOrDefault(f => f.Key == item.SecretKeyIdx), default) ? keys.First() : keys.First(f => f.Key == item.SecretKeyIdx));
                            }
                            else
                            {
                                _uiShared.DrawUIDComboForAuthentication(i, item, selectedServer.ServerUri, _logger);
                            }
                            ConnectivityOptionBlock.DrawCharacterAutoLoginOption(item, _serverConfigurationManager, _uiShared, "ConnectivityCharacterAutoLogin");
                            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Delete Character") && UiSharedService.CtrlPressed())
                                _serverConfigurationManager.RemoveCharacterFromServer(idx, item);
                            SpheneUI.AttachTooltip("Hold CTRL to delete this entry.");
                            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                        }

                        if (thisIsYou)
                        {
                            uint bgColor = ImGui.GetColorU32(new Vector4(0.0f, 1.0f, 0.0f, 0.06f));
                            var winPos = ImGui.GetWindowPos();
                            var contentMin = ImGui.GetWindowContentRegionMin();
                            var contentMax = ImGui.GetWindowContentRegionMax();
                            drawList.AddRectFilled(
                                new Vector2(winPos.X + contentMin.X, rowStartY),
                                new Vector2(winPos.X + contentMax.X, ImGui.GetCursorScreenPos().Y),
                                bgColor, 3.0f);
                        }

                        i++;
                        if (item != sortedAuths[^1])
                        {
                            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                            SpheneUI.Layout.Separator();
                            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                        }
                    }

                    if (selectedServer.Authentications.Any())
                        SpheneUI.Layout.Separator();

                    bool currentCidKnown = selectedServer.Authentications.Any(c => c.LastSeenCID != null && c.LastSeenCID != 0 && c.LastSeenCID == youCid);

                    if (!currentCidKnown && SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add new character"))
                    {
                        _serverConfigurationManager.AddEmptyCharacterToServer(idx);
                    }
                }
                else
                {
                    SpheneUI.Text.ColoredWrapped("You need to add a Secret Key first before adding Characters.", ImGuiColors.DalamudYellow);
                    SpheneUI.Text.ColoredWrapped("Please go to the 'Secret Keys' tab to add authentication keys.", ImGuiColors.DalamudYellow);
                }

            }
            break;

            // Secret Key management in separate tab (non-OAuth only)
            case "Secret Keys":
            {
                SpheneUI.Text.Big("Secret Key Management");
                SpheneUI.Text.ColoredWrapped("Secret keys are used to authenticate your characters with the Sphene service. Each character must be assigned to a secret key.", ImGuiColors.DalamudYellow);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                foreach (var item in selectedServer.SecretKeys.ToList())
                {
                    using var id = ImRaii.PushId("key" + item.Key);
                    var friendlyName = item.Value.FriendlyName;
                    if (SpheneUI.Controls.InputText("Secret Key Display Name", ref friendlyName, 255))
                    {
                        item.Value.FriendlyName = friendlyName;
                        _serverConfigurationManager.Save();
                    }
                    var key = item.Value.Key;
                    if (SpheneUI.Controls.InputText("Secret Key", ref key, 64))
                    {
                        item.Value.Key = key;
                        _serverConfigurationManager.Save();
                    }
                    
                    // Show which characters are using this key
                    var charactersUsingKey = selectedServer.Authentications.Where(a => a.SecretKeyIdx == item.Key).ToList();
                    if (charactersUsingKey.Any())
                    {
                        SpheneUI.Text.Plain("Used by characters:");
                        ImGui.Indent();
                        foreach (var auth in charactersUsingKey)
                        {
                            SpheneUI.Text.Plain($"• {auth.CharacterName}@{_dalamudUtilService.WorldData.Value[(ushort)auth.WorldId]}");
                        }
                        ImGui.Unindent();
                    }
                    
                    if (!selectedServer.Authentications.Exists(p => p.SecretKeyIdx == item.Key))
                    {
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Delete Secret Key") && UiSharedService.CtrlPressed())
                        {
                            selectedServer.SecretKeys.Remove(item.Key);
                            _serverConfigurationManager.Save();
                        }
                        SpheneUI.AttachTooltip("Hold CTRL to delete this secret key entry");
                    }
                    else
                    {
                        SpheneUI.Text.ColoredWrapped("This key is in use and cannot be deleted", ImGuiColors.DalamudYellow);
                    }

                    if (item.Key != selectedServer.SecretKeys.Keys.LastOrDefault())
                        SpheneUI.Layout.Separator();
                }

                SpheneUI.Layout.Separator();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add new Secret Key"))
                {
                    selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                    {
                        FriendlyName = "New Secret Key",
                    });
                    _serverConfigurationManager.Save();
                }

            }
            break;

            case "Service Configuration":
            {
                var serverName = selectedServer.ServerName;
                var serverUri = selectedServer.ServerUri;
                var isMain = string.Equals(serverName, ApiController.MainServer, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serverName, "Sphene Test Server", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serverName, "Sphene Debug Server", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serverName, "Sphene Server", StringComparison.OrdinalIgnoreCase);
                var flags = isMain ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;

                if (SpheneUI.Controls.InputText("Service URI", ref serverUri, 255, flags))
                {
                    selectedServer.ServerUri = serverUri;
                }
                if (isMain)
                {
                    SpheneUI.Controls.HelpText("You cannot edit the URI of the main service.");
                }

                if (SpheneUI.Controls.InputText("Service Name", ref serverName, 255, flags))
                {
                    selectedServer.ServerName = serverName;
                    _serverConfigurationManager.Save();
                }
                if (isMain)
                {
                    SpheneUI.Controls.HelpText("You cannot edit the name of the main service.");
                }

                ConnectivityOptionBlock.DrawServerTransportTypeOption(_serverConfigurationManager, _uiShared, "ConnectivityServerTransportType");

                if (_dalamudUtilService.IsWine)
                {
                    if (!selectedServer.ForceWebSockets)
                    {
                        SpheneUI.Text.ColoredWrapped("Wine/Proton detected. WebSockets are disabled by default which may cause connection issues. Enable Force WebSockets below if you cannot connect.", ImGuiColors.DalamudOrange);
                        SpheneUI.Layout.Space(SpheneUI.Spacing.X2);
                    }
                    ConnectivityOptionBlock.DrawWineForceWebSocketsOption(selectedServer, _serverConfigurationManager, _uiShared, "ConnectivityWineForceWebSockets");
                }

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                ConnectivityOptionBlock.DrawUseDiscordOAuthOption(selectedServer, useOauth, _serverConfigurationManager, _uiShared, "ConnectivityUseDiscordOAuth");

            }
            break;

            case "Permission Settings":
            {
                SpheneUI.Text.Big("Default Permission Settings");
                if (selectedServer == _serverConfigurationManager.CurrentServer && _apiController.IsConnected)
                {
                    SpheneUI.Text.Wrapped("Note: The default permissions settings here are not applied retroactively to existing pairs or joined Syncshells.");
                    SpheneUI.Text.Wrapped("Note: The default permissions settings here are sent and stored on the connected service.");
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                    var perms = _apiController.DefaultPermissions!;
                    ConnectivityOptionBlock.DrawPreferredPermissionsOption(perms, _apiController, _uiShared, "ConnectivityPreferredPermissions");
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                    ConnectivityOptionBlock.DrawDisableIndividualPairSoundsOption(perms, _apiController, _uiShared, "ConnectivityDisableIndividualPairSounds");
                    ConnectivityOptionBlock.DrawDisableIndividualPairAnimationsOption(perms, _apiController, _uiShared, "ConnectivityDisableIndividualPairAnimations");
                    ConnectivityOptionBlock.DrawDisableIndividualPairVfxOption(perms, _apiController, _uiShared, "ConnectivityDisableIndividualPairVfx");
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                    ConnectivityOptionBlock.DrawDisableSyncshellPairSoundsOption(perms, _apiController, _uiShared, "ConnectivityDisableSyncshellPairSounds");
                    ConnectivityOptionBlock.DrawDisableSyncshellPairAnimationsOption(perms, _apiController, _uiShared, "ConnectivityDisableSyncshellPairAnimations");
                    ConnectivityOptionBlock.DrawDisableSyncshellPairVfxOption(perms, _apiController, _uiShared, "ConnectivityDisableSyncshellPairVfx");
                }
                else
                {
                    SpheneUI.Text.ColoredWrapped("Default Permission Settings unavailable for this service. " +
                        "You need to connect to this service to change the default permissions since they are stored on the service.", ImGuiColors.DalamudYellow);
                }

            }
            break;
        }
        });
    }

    private int _lastSelectedServerIndex = -1;
    private Task<(bool Success, bool PartialSuccess, string Result)>? _secretKeysConversionTask = null;
    private CancellationTokenSource? _secretKeysConversionCts = new CancellationTokenSource();

    private async Task<(bool Success, bool partialSuccess, string Result)> ConvertSecretKeysToUIDs(ServerStorage serverStorage, CancellationToken token)
    {
        List<Authentication> failedConversions = serverStorage.Authentications.Where(u => u.SecretKeyIdx == -1 && string.IsNullOrEmpty(u.UID)).ToList();
        List<Authentication> conversionsToAttempt = serverStorage.Authentications.Where(u => u.SecretKeyIdx != -1 && string.IsNullOrEmpty(u.UID)).ToList();
        List<Authentication> successfulConversions = [];
        Dictionary<string, List<Authentication>> secretKeyMapping = new(StringComparer.Ordinal);
        foreach (var authEntry in conversionsToAttempt)
        {
            if (!serverStorage.SecretKeys.TryGetValue(authEntry.SecretKeyIdx, out var secretKey))
            {
                failedConversions.Add(authEntry);
                continue;
            }

            if (!secretKeyMapping.TryGetValue(secretKey.Key, out List<Authentication>? authList))
            {
                secretKeyMapping[secretKey.Key] = authList = [];
            }

            authList.Add(authEntry);
        }

        if (secretKeyMapping.Count == 0)
        {
            return (false, false, $"Failed to convert {failedConversions.Count} entries: " + string.Join(", ", failedConversions.Select(k => k.CharacterName)));
        }

        var baseUri = serverStorage.ServerUri.Replace("wss://", "https://").Replace("ws://", "http://");
        var oauthCheckUri = SpheneAuth.GetUIDsBasedOnSecretKeyFullPath(new Uri(baseUri));
        var requestContent = JsonContent.Create(secretKeyMapping.Select(k => k.Key).ToList());
        HttpRequestMessage requestMessage = new(HttpMethod.Post, oauthCheckUri);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverStorage.OAuthToken);
        requestMessage.Content = requestContent;

        using var response = await _httpClient.SendAsync(requestMessage, token).ConfigureAwait(false);
        Dictionary<string, string>? secretKeyUidMapping = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>
            (await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
        if (secretKeyUidMapping == null)
        {
            return (false, false, $"Failed to parse the server response. Failed to convert all entries.");
        }

        foreach (var entry in secretKeyMapping)
        {
            if (!secretKeyUidMapping.TryGetValue(entry.Key, out var assignedUid) || string.IsNullOrEmpty(assignedUid))
            {
                failedConversions.AddRange(entry.Value);
                continue;
            }

            foreach (var auth in entry.Value)
            {
                auth.UID = assignedUid;
                successfulConversions.Add(auth);
            }
        }

        if (successfulConversions.Count > 0)
            _serverConfigurationManager.Save();

        StringBuilder sb = new();
        sb.Append("Conversion complete." + Environment.NewLine);
        sb.Append($"Successfully converted {successfulConversions.Count} entries." + Environment.NewLine);
        if (failedConversions.Count > 0)
        {
            sb.Append($"Failed to convert {failedConversions.Count} entries, assign those manually: ");
            sb.Append(string.Join(", ", failedConversions.Select(k => k.CharacterName)));
        }

        return (true, failedConversions.Count != 0, sb.ToString());
    }

    internal static string DiagnosticsPageLabel =>
#if IS_TEST_BUILD
        "Debug Build";
#else
        "Diagnostics";
#endif

    /// <summary>
    /// The settings nav items — shared between the standalone sidebar and
    /// the Sphene Hub rail so both compute the same rail width.
    /// </summary>
    internal static IReadOnlyList<(FontAwesomeIcon Icon, string Label)> EmbeddedNavItems =>
    [
        (FontAwesomeIcon.Home, "Home"),
        (FontAwesomeIcon.Wifi, "Connectivity"),
        (FontAwesomeIcon.AddressBook, "People & Notes"),
        (FontAwesomeIcon.Palette, "Appearance"),
        (FontAwesomeIcon.Brush, "Theme"),
        (FontAwesomeIcon.Bell, "Notifications"),
        (FontAwesomeIcon.History, "Update"),
        (FontAwesomeIcon.TachometerAlt, "Performance"),
        (FontAwesomeIcon.ExchangeAlt, "Transfers"),
        (FontAwesomeIcon.Hdd, "Storage"),
        (FontAwesomeIcon.SyncAlt, "Sync"),
        (FontAwesomeIcon.Handshake, "Acknowledgment"),
        (FontAwesomeIcon.Crown, "Supporter"),
        (FontAwesomeIcon.Bug, DiagnosticsPageLabel),
    ];

    private void DrawSettingsContent()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var contentOrigin = ImGui.GetCursorScreenPos();
        var available = new Vector2(
            windowPos.X + windowSize.X - contentOrigin.X,
            windowPos.Y + windowSize.Y - contentOrigin.Y);

        var navItems = EmbeddedNavItems;

        if (_activeSettingsPage == SettingsPage.Supporter && !_apiController.SupporterFeaturesEnabled)
        {
            _activeSettingsPage = SettingsPage.Home;
        }
        var style = ImGui.GetStyle();
        // The band extends WindowPadding.X past the content edge; the shared
        // rail margin of NavRailSegmentedWidth replaces it here.
        var sidebarWidth = SpheneUI.Layout.NavRailSegmentedWidth(navItems) - style.WindowPadding.X;

        // The sidebar is a rail: slightly lighter than the window surface,
        // spanning from below the title bar to the bottom window edge.
        var contentTopPos = contentOrigin;
        var railMin = new Vector2(windowPos.X, windowPos.Y + SpheneUI.Chrome.TitleBarHeight);
        var railMax = new Vector2(contentTopPos.X + sidebarWidth, windowPos.Y + windowSize.Y);
        // Inset the sidebar child by the shared rail margin on both sides so
        // pills keep a visible gap to the band edges.
        var railPad = SpheneUI.Layout.NavRailPadX * ImGuiHelpers.GlobalScale;
        var sidebarMin = new Vector2(railMin.X + railPad, contentTopPos.Y);
        var sidebarSize = new Vector2(
            MathF.Max(0f, railMax.X - railMin.X - railPad * 2f), MathF.Max(0f, available.Y));
        var theme = SpheneTheme.Current;
        var sidebarBg = SpheneUI.ColorUtils.LerpColor(theme.WindowBg, Vector4.One, 0.07f);
        sidebarBg = SpheneUI.ColorUtils.WithAlpha(sidebarBg, MathF.Min(1f, theme.WindowBg.W + 0.08f));
        var outerBorder = MathF.Max(style.WindowBorderSize, 0f);
        var railRadius = MathF.Max(0f, style.WindowRounding - outerBorder);
        var windowDrawList = ImGui.GetWindowDrawList();
        // Escape the inner content clip so the rail reaches the window edges
        // like the title bar does; the bottom-left corner hugs the window arc.
        ImGui.PushClipRect(windowPos, windowPos + windowSize, false);
        windowDrawList.AddRectFilled(railMin, railMax, SpheneUI.ColorUtils.ToU32(sidebarBg),
            railRadius, ImDrawFlags.RoundCornersBottomLeft);
        windowDrawList.AddLine(new Vector2(railMax.X - 0.5f, railMin.Y),
            new Vector2(railMax.X - 0.5f, railMax.Y),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.06f)));
        SpheneUI.Fx.RailMotes(windowDrawList, railMin, railMax);
        ImGui.PopClipRect();
        ImGui.SetCursorScreenPos(sidebarMin);

        // The pinned band docks directly under the accent line.
        var pinnedBandHeight = ImGui.GetTextLineHeight() + 8f * ImGuiHelpers.GlobalScale;
        var pinnedCenterY = railMin.Y + pinnedBandHeight * 0.5f;
        var pinnedBandBottom = pinnedCenterY + pinnedBandHeight * 0.5f;
        SpheneUI.Layout.BeginScrollArea("settings-sidebar", sidebarSize,
            topInset: pinnedBandBottom - sidebarMin.Y,
            contentPadding: new Vector2(0f, 8f));

        var sidebarCategories = new List<(string Label, float CenterY, Vector4 Accent)>();
        DrawSettingsNavButtons(sidebarCategories, pinnedCenterY);

        SpheneUI.Layout.EndScrollArea(fadeBaseColor: sidebarBg,
            fadeSpanX: new Vector2(railMin.X, railMax.X), fadeMinY: pinnedBandBottom);

        SpheneUI.Layout.PinnedSectionLabel(
            new Vector2(sidebarMin.X, railMin.Y), sidebarMin + sidebarSize,
            sidebarCategories, windowPos, windowPos + windowSize,
            baseColor: sidebarBg, pinCenterY: pinnedCenterY);

        ImGui.SetCursorScreenPos(contentTopPos + new Vector2(sidebarWidth, 0f));
        SpheneUI.Layout.BeginScrollArea("settings-content",
            new Vector2(available.X - sidebarWidth, available.Y),
            windowFlags: ImGuiWindowFlags.NoBackground, noPadding: true);
        // The wrapper owns the top inset (mirroring the Hub's NavContent
        // PagePadding) so embedded pages can start flush in both hosts.
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        DrawSettingsPageContent();

        SpheneUI.Layout.EndScrollArea(
            fadeSpanX: new Vector2(railMax.X, windowPos.X + windowSize.X),
            fadeMinY: railMin.Y);
    }

    /// <summary>
    /// The settings nav buttons — standalone draws them inside its own
    /// sidebar scroll area (with pinned categories collected for the pin
    /// band); embedded inside the Sphene Hub rail they render in-flow and
    /// categories simply scroll with the items.
    /// </summary>
    private void DrawSettingsNavButtons(
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories, float pinnedCenterY)
    {
        var theme = SpheneTheme.Current;
        // Per-category accents; dividers blend one category color into the next
        // like the brand strip seam gradient.
        var accentGeneral = theme.AccentBlue;
        var accentAppearance = SpheneUI.Palette.EtherealPurple;
        var accentSync = SpheneUI.Palette.NetworkActive;
        var accentAdvanced = new Vector4(0.93f, 0.68f, 0.38f, 1f);

        void SidebarCategory(string label, Vector4 accent)
            => SpheneUI.Layout.NavRailCategoryLabel(label, accent, pinnedCategories, pinnedCenterY);

        void SidebarDivider(Vector4 from, Vector4 to) => SpheneUI.Layout.NavRailDivider(from, to);

        void SidebarButton(string label, FontAwesomeIcon icon, SettingsPage page, Vector4 accent, bool special = false)
        {
            if (SpheneUI.Layout.NavRailSegmentItem(label, icon, _activeSettingsPage == page, accent, special))
            {
                SetSettingsPage(page);
            }
        }

        SidebarCategory("General", accentGeneral);
        SidebarButton("Home", FontAwesomeIcon.Home, SettingsPage.Home, accentGeneral);
        SidebarButton("Connectivity", FontAwesomeIcon.Wifi, SettingsPage.Connectivity, accentGeneral);
        SidebarButton("People & Notes", FontAwesomeIcon.AddressBook, SettingsPage.PeopleNotes, accentGeneral);
        SidebarButton("Notifications", FontAwesomeIcon.Bell, SettingsPage.Alerts, accentGeneral);
        SidebarButton("Update", FontAwesomeIcon.History, SettingsPage.ReleaseNotes, accentGeneral);
        if (_apiController.SupporterFeaturesEnabled)
        {
            SidebarButton("Supporter", FontAwesomeIcon.Crown, SettingsPage.Supporter, SpheneUI.Palette.SpheneGold, special: true);
        }
        SidebarDivider(accentGeneral, accentAppearance);

        SidebarCategory("Appearance", accentAppearance);
        SidebarButton("Appearance", FontAwesomeIcon.Palette, SettingsPage.Display, accentAppearance);
        SidebarButton("Theme", FontAwesomeIcon.Brush, SettingsPage.Theme, accentAppearance);
        SidebarDivider(accentAppearance, accentSync);

        SidebarCategory("Sync & Data", accentSync);
        SidebarButton("Sync", FontAwesomeIcon.SyncAlt, SettingsPage.SyncBehavior, accentSync);
        SidebarButton("Acknowledgment", FontAwesomeIcon.Handshake, SettingsPage.Acknowledgment, accentSync);
        SidebarButton("Transfers", FontAwesomeIcon.ExchangeAlt, SettingsPage.Transfers, accentSync);
        SidebarButton("Storage", FontAwesomeIcon.Hdd, SettingsPage.Storage, accentSync);
        SidebarDivider(accentSync, accentAdvanced);

        SidebarCategory("Advanced", accentAdvanced);
        SidebarButton("Performance", FontAwesomeIcon.TachometerAlt, SettingsPage.Performance, accentAdvanced);
        SidebarButton(DiagnosticsPageLabel, FontAwesomeIcon.Bug, SettingsPage.Debug, accentAdvanced);
    }

    /// <summary>
    /// The settings nav rendered inside the Sphene Hub rail — same items as
    /// the standalone sidebar plus a pop-out shortcut so the windowed
    /// settings stay one click away. When <paramref name="pinnedCategories"/>
    /// is supplied, category labels are recorded so the hub can pin the
    /// current one below its own fixed header — same behavior as the
    /// standalone sidebar band.
    /// </summary>
    internal void DrawEmbeddedRail(
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories = null,
        float pinnedCenterY = 0f)
    {
        var previousScope = _embeddedScope;
        _embeddedScope = true;
        try
        {
            DrawSettingsNavButtons(pinnedCategories, pinnedCenterY);
            var theme = SpheneTheme.Current;
            SpheneUI.Layout.NavRailDivider(theme.AccentBlue, theme.AccentBlue);
            if (SpheneUI.Layout.NavRailSegmentItem("Pop Out", FontAwesomeIcon.ExternalLinkAlt,
                    false, theme.AccentBlue))
            {
                Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
            }
            SpheneUI.AttachTooltip("Open Settings in a separate window");
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    /// <summary>
    /// The active settings page rendered inside an already-scrollable
    /// container (the Sphene Hub nav content child) — no own scroll area.
    /// </summary>
    internal void DrawEmbeddedContent()
    {
        var previousScope = _embeddedScope;
        _embeddedScope = true;
        try
        {
            UpdateCompactUiThemeStick();
            DrawSettingsPageContent();
            DrawThemeSavePrompt();
            DrawUnsavedCustomThemePrompt();
            DrawExportBackupModal();
            DrawImportBackupModal();
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    private void UpdateCompactUiThemeStick()
    {
        var settingsPos = ImGui.GetWindowPos();
        var settingsSize = ImGui.GetWindowSize();
        var themePageActive = _activeSettingsPage == SettingsPage.Theme;
        Mediator.Publish(new CompactUiStickToSettingsMessage(themePageActive && _compactUiHasLayoutInfo, settingsPos, settingsSize));

        if (_activeSettingsPage == SettingsPage.Theme && _compactUiWasOpen == null)
        {
            var wasOpen = false;
            Mediator.Publish(new QueryWindowOpenStateMessage(typeof(CompactUi), state => wasOpen = state));
            _compactUiWasOpen = wasOpen;
            if (!wasOpen)
            {
                Mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
            }
        }
        else if (_activeSettingsPage != SettingsPage.Theme && _compactUiWasOpen.HasValue)
        {
            var currentOpen = false;
            Mediator.Publish(new QueryWindowOpenStateMessage(typeof(CompactUi), state => currentOpen = state));
            if (currentOpen != _compactUiWasOpen.Value)
            {
                Mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
            }
            _compactUiWasOpen = null;
        }
    }

    private static (string Category, string Label) SettingsCrumbInfo(string diagnosticsPageLabel, SettingsPage page)
        => page switch
        {
            SettingsPage.Home => ("General", "Home"),
            SettingsPage.Connectivity => ("General", "Connectivity"),
            SettingsPage.PeopleNotes => ("General", "People & Notes"),
            SettingsPage.Display => ("Appearance", "Appearance"),
            SettingsPage.Theme => ("Appearance", "Theme"),
            SettingsPage.Alerts => ("General", "Notifications"),
            SettingsPage.Performance => ("Advanced", "Performance"),
            SettingsPage.Transfers => ("Sync & Data", "Transfers"),
            SettingsPage.Storage => ("Sync & Data", "Storage"),
            SettingsPage.SyncBehavior => ("Sync & Data", "Sync"),
            SettingsPage.Acknowledgment => ("Sync & Data", "Acknowledgment"),
            SettingsPage.Supporter => ("General", "Supporter"),
            SettingsPage.ReleaseNotes => ("General", "Update"),
            SettingsPage.Debug => ("Advanced", diagnosticsPageLabel),
            _ => ("General", "Home")
        };

    private void DrawSettingsPageContent()
    {
        // Pages start flush — the host provides the top inset (the Hub's
        // NavContent applies PagePadding; the standalone window adds the
        // equivalent space in its scroll area before this runs).
        var contentAvail = ImGui.GetContentRegionAvail().X;
        var safeContentWidth = MathF.Max(220f * ImGuiHelpers.GlobalScale, contentAvail);
        var maxPageWidth = MathF.Min(safeContentWidth, 1040f * ImGuiHelpers.GlobalScale);
        var pageStartX = ImGui.GetCursorPosX();
        if (contentAvail > maxPageWidth)
        {
            pageStartX = ImGui.GetCursorPosX() + (contentAvail - maxPageWidth) * 0.5f;
            ImGui.SetCursorPosX(pageStartX);
        }

        using (var pageGroup = ImRaii.Group())
        {
            ImGui.PushTextWrapPos(pageStartX + maxPageWidth
                - SpheneUI.Layout.ScrollAreaContentInset * ImGuiHelpers.GlobalScale);
            ImGui.PushItemWidth(MathF.Min(360f * ImGuiHelpers.GlobalScale, maxPageWidth * 0.6f));

            var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
            IDisposable? contentIndent = null;
            if (_activeSettingsPage != SettingsPage.Home)
            {
                contentIndent = ImRaii.PushIndent(settingsOptionsIndentX);
            }

            _settingsCardIndex = 0;
            switch (_activeSettingsPage)
            {
                case SettingsPage.Home:
                    DrawOverview();
                    break;
                case SettingsPage.Connectivity:
                    DrawServerConfiguration();
                    break;
                case SettingsPage.PeopleNotes:
                    DrawGeneralUserManagement();
                    break;
                case SettingsPage.Display:
                    DrawGeneralUiDisplaySettings();
                    break;
                case SettingsPage.Theme:
                    DrawThemeSettings();
                    break;
                case SettingsPage.Alerts:
                    DrawGeneralNotifications();
                    break;
                case SettingsPage.Performance:
                    DrawPerformance();
                    break;
                case SettingsPage.Transfers:
                    DrawCurrentTransfers();
                    break;
                case SettingsPage.Storage:
                    DrawFileStorageSettings();
                    break;
                case SettingsPage.SyncBehavior:
                    DrawSyncBehaviorSettings();
                    break;
                case SettingsPage.Acknowledgment:
                    DrawAcknowledgmentSettings();
                    break;
                case SettingsPage.Supporter:
                    DrawSupporterPage();
                    break;
                case SettingsPage.ReleaseNotes:
                    _releaseChangelogUi.DrawEmbedded();
                    break;
                case SettingsPage.Debug:
                    DrawDebug();
                    break;
            }
            contentIndent?.Dispose();
            ImGui.PopItemWidth();
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawSettingsPageHeader(string tabKey, string? description = null)
    {
        var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Unindent(settingsOptionsIndentX);
        _lastTab = tabKey;
        if (!string.IsNullOrWhiteSpace(description))
        {
            SpheneUI.Text.PageIntro(description);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Indent(settingsOptionsIndentX);
    }

    private void DrawSettingsSectionHeader(string title, string? description = null)
    {
        var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Unindent(settingsOptionsIndentX);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.SectionHeader(title, description);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Indent(settingsOptionsIndentX);
    }

    /// <summary>
    /// Wraps a settings block in a section card (see
    /// <see cref="SpheneUI.Layout.SectionCard"/>). Spans the same
    /// unindented width as section headers and adds trailing spacing.
    /// </summary>
    private void DrawSettingsCard(string title, Action content, string? description = null,
        Vector4? accent = null)
    {
        // Inter-card spacing leads each card so the last card of a page ends
        // flush at the shared bottom rule instead of leaving a stray dummy.
        if (_settingsCardIndex++ > 0)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }
        var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Unindent(settingsOptionsIndentX);
        SpheneUI.Layout.SectionCard(content, title, accent, description,
            animatedBackdrop: true);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Indent(settingsOptionsIndentX);
    }

    private static float GetSettingsOptionsIndentX(SettingsPage page)
        => page switch
        {
            SettingsPage.Theme => 8f * ImGuiHelpers.GlobalScale,
            SettingsPage.ReleaseNotes => 0f,
            SettingsPage.Connectivity => 14f * ImGuiHelpers.GlobalScale,
            SettingsPage.Storage => 14f * ImGuiHelpers.GlobalScale,
            SettingsPage.Transfers => 13f * ImGuiHelpers.GlobalScale,
            SettingsPage.Performance => 13f * ImGuiHelpers.GlobalScale,
            SettingsPage.Acknowledgment => 13f * ImGuiHelpers.GlobalScale,
            SettingsPage.SyncBehavior => 13f * ImGuiHelpers.GlobalScale,
            SettingsPage.Debug => 12f * ImGuiHelpers.GlobalScale,
            SettingsPage.PeopleNotes => 12f * ImGuiHelpers.GlobalScale,
            SettingsPage.Display => 12f * ImGuiHelpers.GlobalScale,
            SettingsPage.Alerts => 12f * ImGuiHelpers.GlobalScale,
            _ => 12f * ImGuiHelpers.GlobalScale,
        };

    private static float GetSettingsSafeContentWidth()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var reserve = ImGui.GetStyle().ScrollbarSize + 10f * scale;
        return MathF.Max(220f * scale, ImGui.GetContentRegionAvail().X - reserve);
    }

    private static float ClampSettingsItemWidth(float desiredWidth, float minWidth = 80f)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var desired = desiredWidth * scale;
        var minimum = minWidth * scale;
        return MathF.Min(desired, MathF.Max(minimum, GetSettingsSafeContentWidth()));
    }

    private bool IsPluginInstallInProgress => Volatile.Read(ref _pluginInstallInProgress) == 1;
    private string? InstallingPluginName => Volatile.Read(ref _installingPluginName);

    private void StartPluginInstall(string pluginName, Func<Task> installAction)
    {
        if (Interlocked.CompareExchange(ref _pluginInstallInProgress, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _installingPluginName, pluginName);

        try
        {
            var task = installAction();

            if (task.IsCompleted)
            {
                if (task.IsFaulted)
                {
                    _logger.LogError(task.Exception, "Plugin installation failed for {plugin}", pluginName);
                }
                Volatile.Write(ref _installingPluginName, null);
                Interlocked.Exchange(ref _pluginInstallInProgress, 0);
                return;
            }

            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogError(t.Exception, "Plugin installation failed for {plugin}", pluginName);
                }
                Volatile.Write(ref _installingPluginName, null);
                Interlocked.Exchange(ref _pluginInstallInProgress, 0);
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin installation failed for {plugin}", pluginName);
            Volatile.Write(ref _installingPluginName, null);
            Interlocked.Exchange(ref _pluginInstallInProgress, 0);
        }
    }

    private static void DrawRotatingSpinner(Vector2 center, float radius, float thickness)
    {
        var drawList = ImGui.GetWindowDrawList();
        var theme = SpheneTheme.Current;
        var color = theme.TextPrimary;
        var u32 = ImGui.ColorConvertFloat4ToU32(color);
        var bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.25f));
        drawList.AddCircle(center, radius, bgColor, 48, thickness);

        var t = (float)ImGui.GetTime();
        var speed = 3.0f;
        var baseAngle = t * speed;
        var arcLen = 1.35f;
        drawList.PathClear();
        drawList.PathArcTo(center, radius, baseAngle, baseAngle + arcLen, 32);
        drawList.PathStroke(u32, ImDrawFlags.None, thickness);
    }

    private static bool DrawToggleSwitch(string id, bool value)
    {
        var height = ImGui.GetFrameHeight();
        var width = height * 1.8f;
        var position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(width, height));

        if (ImGui.IsItemClicked())
        {
            value = !value;
        }

        // Same proportions as SpheneUI.Controls.Toggle: the track is drawn at
        // 70% of the frame height, centered inside the full-height hit box.
        var trackHeight = MathF.Round(height * 0.7f);
        var trackMin = new Vector2(position.X + (width - trackHeight * 1.8f) * 0.5f,
            position.Y + (height - trackHeight) * 0.5f);
        var animT = SpheneUI.Controls.ToggleAnimValue(ImGui.GetID(id), value);
        SpheneUI.Controls.SwitchVisual(ImGui.GetWindowDrawList(), trackMin,
            trackMin + new Vector2(trackHeight * 1.8f, trackHeight), animT, ImGui.GetStyle().Alpha);

        return value;
    }

    private void DrawPluginListRow(string name, string description, string statusOrVersion, bool isInstalled, bool isEnabled, Func<Task>? onInstallAction = null, Func<Task>? onEnableAction = null, Func<Task>? onDisableAction = null, Action? onOpenSettings = null, string? settingsLabel = null)
    {
        using var id = ImRaii.PushId("PluginRow_" + name);
        var startPos = ImGui.GetCursorScreenPos();
        var rowHeight = 28f * ImGuiHelpers.GlobalScale;
        var rowMarginX = SpheneUI.Layout.ScrollAreaContentInset * ImGuiHelpers.GlobalScale;
        
        // Use Avail width for alignment, safer than absolute WindowContentRegionMax in groups/children
        var availWidth = SpheneUI.Controls.BoundedAvailX();
        
        var nameColor = !isInstalled
            ? ImGuiColors.DalamudRed
            : isEnabled
                ? ImGuiColors.ParsedGreen
                : ImGuiColors.DalamudYellow;
        
        var textHeight = ImGui.GetTextLineHeight();
        var textY = startPos.Y + (rowHeight - textHeight) / 2;

        bool showSettings = isInstalled && isEnabled && onOpenSettings != null;
        bool showInstall = !isInstalled && onInstallAction != null;
        bool showToggle = isInstalled && onEnableAction != null && onDisableAction != null;
        bool isInstalling = showInstall && IsPluginInstallInProgress && string.Equals(InstallingPluginName, name, StringComparison.Ordinal);
        bool disableInstall = showInstall && IsPluginInstallInProgress && !isInstalling;

        // Resolve the right-edge control position up front so the row hit zone
        // can cover everything left of it.
        var minBtnX = startPos.X + 200;
        var controlX = startPos.X + availWidth - rowMarginX;
        var btnText = showSettings ? (settingsLabel ?? "Settings") : (isInstalling ? "Installing..." : "Install");
        var displayText = btnText;
        var spinnerSize = 0f;
        var spinnerAreaWidth = 0f;
        if (isInstalling)
        {
            spinnerSize = ImGui.GetTextLineHeight();
            var spaceWidth = Math.Max(1e-3f, ImGui.CalcTextSize(" ").X);
            spinnerAreaWidth = spinnerSize + ImGui.GetStyle().ItemInnerSpacing.X;
            var spaces = Math.Max(2, (int)MathF.Ceiling(spinnerAreaWidth / spaceWidth));
            displayText = new string(' ', spaces) + btnText;
        }
        if (showSettings || showInstall)
        {
            var btnH = 22f * ImGuiHelpers.GlobalScale;
            var btnSize = new Vector2(SpheneUI.Controls.SegAutoWidth(displayText, null, btnH), btnH);
            controlX = MathF.Max(minBtnX, startPos.X + availWidth - btnSize.X - rowMarginX);
        }
        else if (showToggle)
        {
            var toggleWidth = ImGui.GetFrameHeight() * 1.8f;
            controlX = MathF.Max(minBtnX, startPos.X + availWidth - toggleWidth - rowMarginX);
        }

        // Subtle hover tint across the whole row.
        var rowMin = startPos;
        var rowMax = new Vector2(startPos.X + availWidth, startPos.Y + rowHeight);
        var rowHovered = ImGui.IsMouseHoveringRect(rowMin, rowMax) && ImGui.IsWindowHovered();
        if (rowHovered)
        {
            var tint = SpheneUI.Palette.AccentBlue with { W = 0.06f * ImGui.GetStyle().Alpha };
            ImGui.GetWindowDrawList().AddRectFilled(rowMin, rowMax,
                ImGui.ColorConvertFloat4ToU32(tint), 4f * ImGuiHelpers.GlobalScale, ImDrawFlags.RoundCornersAll);
        }

        ImGui.SetCursorScreenPos(new Vector2(startPos.X + rowMarginX, textY));
        SpheneUI.Text.Colored(name, nameColor);

        ImGui.SameLine();
        SpheneUI.Text.Colored($"({statusOrVersion})", ImGuiColors.DalamudGrey);

        // Row hit zone: clicking anywhere on the label area triggers the row's
        // primary action; hovering shows the plugin description.
        ImGui.SetCursorScreenPos(startPos);
        var rowHitWidth = MathF.Max(0f, controlX - startPos.X - ImGui.GetStyle().ItemSpacing.X);
        var rowClicked = ImGui.InvisibleButton("##rowhit", new Vector2(rowHitWidth, rowHeight));
        SpheneUI.AttachTooltip(description);
        if (rowClicked)
        {
            if (showToggle)
            {
                _ = isEnabled ? onDisableAction!.Invoke() : onEnableAction!.Invoke();
            }
            else if (showInstall && !isInstalling && !disableInstall)
            {
                StartPluginInstall(name, onInstallAction!);
            }
            else if (showSettings)
            {
                onOpenSettings!.Invoke();
            }
        }

        if (showSettings || showInstall || showToggle)
        {
            var btnHeight = 22f * ImGuiHelpers.GlobalScale;
            var btnSize = new Vector2(SpheneUI.Controls.SegAutoWidth(displayText, null, btnHeight), btnHeight);
            var btnY = startPos.Y + (rowHeight - btnHeight) / 2;
            ImGui.SetCursorScreenPos(new Vector2(controlX, btnY));

            if (showSettings)
            {
                if (SpheneUI.Controls.Button(displayText, new Vector2(btnSize.X, btnHeight)))
                {
                    onOpenSettings!.Invoke();
                }
            }
            else if (showInstall)
            {
                using (ImRaii.Disabled(disableInstall || isInstalling))
                {
                    if (SpheneUI.Controls.Button(displayText, new Vector2(btnSize.X, btnHeight)))
                    {
                        StartPluginInstall(name, onInstallAction!);
                    }
                }

                if (isInstalling)
                {
                    var rectMin = ImGui.GetItemRectMin();
                    var rectMax = ImGui.GetItemRectMax();
                    var radius = spinnerSize * 0.45f;
                    var thickness = Math.Max(2.0f * ImGuiHelpers.GlobalScale, spinnerSize * 0.08f);
                    var center = new Vector2(
                        rectMin.X + ImGui.GetStyle().FramePadding.X + spinnerAreaWidth * 0.5f,
                        rectMin.Y + (rectMax.Y - rectMin.Y) * 0.5f);
                    DrawRotatingSpinner(center, radius, thickness);
                }
                else if (!disableInstall)
                {
                    SpheneUI.AttachTooltip("Adds repository. Install via Plugin Installer.");
                }
            }
            else if (showToggle)
            {
                var toggleHeight = ImGui.GetFrameHeight();
                var toggleY = startPos.Y + (rowHeight - toggleHeight) / 2;
                ImGui.SetCursorScreenPos(new Vector2(controlX, toggleY));
                var toggled = DrawToggleSwitch("EnabledToggle", isEnabled);
                if (toggled != isEnabled)
                {
                    if (toggled)
                    {
                        _ = onEnableAction!.Invoke();
                    }
                    else
                    {
                        _ = onDisableAction!.Invoke();
                    }
                }
                SpheneUI.AttachTooltip("Enable or disable plugin.");
            }
        }
        
        // Move cursor past row
        ImGui.SetCursorScreenPos(new Vector2(startPos.X, startPos.Y + rowHeight));
    }


    private void DrawOverview()
    {
        _lastTab = "Overview";

        DrawSettingsCard("Service & Connection", () =>
        {
        var currentServer = _serverConfigurationManager.CurrentServer;
        if (currentServer == null)
        {
            SpheneUI.Text.Colored("No server configured", ImGuiColors.DalamudRed);
            SpheneUI.Text.Plain("Configure a service under Connectivity to get started.");
        }
        else
        {
            SpheneUI.Text.Plain("Current Service:");
            ImGui.SameLine();
            var displayName = currentServer.ServerName;
#if IS_TEST_BUILD
            if (_configService.Current.UseTestServerOverride)
            {
                displayName = "Test Server";
            }
#endif
            SpheneUI.Text.Colored(displayName, ImGuiColors.ParsedGreen);

            SpheneUI.Text.Plain("Connection State:");
            ImGui.SameLine();
            var connectionState = _apiController.ServerState.ToString();
            var stateColor = _apiController.ServerState switch
            {
                WebAPI.SignalR.Utils.ServerState.Connected => ImGuiColors.ParsedGreen,
                WebAPI.SignalR.Utils.ServerState.Connecting or 
                WebAPI.SignalR.Utils.ServerState.Reconnecting => ImGuiColors.DalamudYellow,
                _ => ImGuiColors.DalamudRed
            };
            SpheneUI.Text.Colored(connectionState, stateColor);

#if IS_TEST_BUILD
            var useOverride = _configService.Current.UseTestServerOverride;
            if (SpheneUI.Controls.Toggle("Connect to Test Server", ref useOverride, menuPopupId: "TestServerUrlPopup"))
            {
                _configService.Current.UseTestServerOverride = useOverride;
                _configService.Save();
                _ = _uiShared.ApiController.CreateConnectionsAsync();
            }
            SpheneUI.AttachTooltip("When enabled, the client ignores the configured service and connects to the test server address. Toggling this setting switches between the main and test server and triggers an immediate reconnect. The address can be changed via the menu button left of the switch. Only available in Dev/Test builds (disabled on release builds).");
            if (string.IsNullOrWhiteSpace(_configService.Current.TestServerApiUrl))
            {
                _configService.Current.TestServerApiUrl = "ws://test.sphene.online:6000";
                _configService.Save();
            }

            if (SpheneUI.Popups.BeginStyled("TestServerUrlPopup"))
            {
                SpheneUI.Text.Colored("Test Server Address", ImGuiColors.DalamudGrey);
                var overrideUrl = _configService.Current.TestServerApiUrl ?? string.Empty;
                ImGui.SetNextItemWidth(300f * ImGuiHelpers.GlobalScale);
                if (SpheneUI.Controls.InputText("##testServerUrl", ref overrideUrl, 200))
                {
                    _configService.Current.TestServerApiUrl = overrideUrl;
                    _configService.Save();
                }
                SpheneUI.AttachTooltip("Websocket URL, e.g. ws://host:port. Applies while typing.");
                SpheneUI.Popups.EndStyled();
            }
#else
            if (_configService.Current.UseTestServerOverride)
            {
                _configService.Current.UseTestServerOverride = false;
                _configService.Save();
            }
#endif
        }
        
        SpheneUI.Layout.VerticalSpace();
        });

        // --- Plugins Section ---

        string GetPluginStatusLabel(UiSharedService.PluginInstallState state)
        {
            return !state.IsInstalled ? "Missing" : state.IsEnabled ? "Detected" : "Deactivated";
        }

        DrawSettingsCard("Sphene Plugins", () =>
        {
            // 1. Sphene (Self)
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var versionString = version != null
                ? (version.Revision > 0
                    ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
                    : $"{version.Major}.{version.Minor}.{version.Build}")
                : "Unknown";
                
            DrawPluginListRow("Sphene", "Advanced character synchronization and networking.", versionString, true, true,
                onOpenSettings: () => SetSettingsPage(SettingsPage.ReleaseNotes),
                settingsLabel: "Update");

            // 2. ShrinkU (Built-in)
            bool shrinkUEnabled = _configService.Current.EnableShrinkUIntegration;
            DrawPluginListRow("ShrinkU", "Texture compression and optimization tool.", _shrinkUVersion, shrinkUEnabled, shrinkUEnabled,
                onInstallAction: async () =>
                {
                    _configService.Current.EnableShrinkUIntegration = true;
                    _configService.Save();
                    try { _shrinkUHostService.ApplyIntegrationEnabled(true); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to apply ShrinkU integration setting"); }
                    await Task.CompletedTask.ConfigureAwait(false);
                },
                onOpenSettings: () =>
                {
                    try { _shrinkUHostService.OpenReleaseNotes(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to open ShrinkU release notes"); }
                },
                settingsLabel: "Release Notes");

            SpheneUI.Layout.SectionSeparator("Required Plugins");

            // 3. Penumbra (External)
            var penumbraState = _uiShared.GetPluginInstallState("Penumbra");
            DrawPluginListRow("Penumbra", "Mod management framework. Required for Sphene.", GetPluginStatusLabel(penumbraState), penumbraState.IsInstalled, penumbraState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.AddRepoViaReflectionAsync(SeaOfStarsRepoUrl, SeaOfStarsRepoName).ConfigureAwait(false);
                    Mediator.Publish(new NotificationMessage("Repository Added", $"{SeaOfStarsRepoName} added. Installing...", NotificationType.Info));
                    await _uiShared.InstallPluginViaReflectionAsync("Penumbra").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("Penumbra").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("Penumbra").ConfigureAwait(false));

            // 4. Glamourer (External)
            var glamourerState = _uiShared.GetPluginInstallState("Glamourer");
            DrawPluginListRow("Glamourer", "Appearance customization tool. Required for full functionality.", GetPluginStatusLabel(glamourerState), glamourerState.IsInstalled, glamourerState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.AddRepoViaReflectionAsync(SeaOfStarsRepoUrl, SeaOfStarsRepoName).ConfigureAwait(false);
                    Mediator.Publish(new NotificationMessage("Repository Added", $"{SeaOfStarsRepoName} added. Installing...", NotificationType.Info));
                    await _uiShared.InstallPluginViaReflectionAsync("Glamourer").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("Glamourer").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("Glamourer").ConfigureAwait(false));

            SpheneUI.Layout.SectionSeparator("Optional Plugins");

            // 5. Customize+
            var customizePlusState = _uiShared.GetPluginInstallState("CustomizePlus");
            DrawPluginListRow("Customize+", "Body scale customization.", GetPluginStatusLabel(customizePlusState), customizePlusState.IsInstalled, customizePlusState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.AddRepoViaReflectionAsync(SeaOfStarsRepoUrl, SeaOfStarsRepoName).ConfigureAwait(false);
                    Mediator.Publish(new NotificationMessage("Repository Added", $"{SeaOfStarsRepoName} added. Installing...", NotificationType.Info));
                    await _uiShared.InstallPluginViaReflectionAsync("CustomizePlus").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("CustomizePlus").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("CustomizePlus").ConfigureAwait(false));

            // 6. Heels
            var heelsState = _uiShared.GetPluginInstallState("SimpleHeels");
            DrawPluginListRow("Heels", "Adjusts character height based on footwear.", GetPluginStatusLabel(heelsState), heelsState.IsInstalled, heelsState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.AddRepoViaReflectionAsync(SeaOfStarsRepoUrl, SeaOfStarsRepoName).ConfigureAwait(false);
                    Mediator.Publish(new NotificationMessage("Repository Added", $"{SeaOfStarsRepoName} added. Installing...", NotificationType.Info));
                    await _uiShared.InstallPluginViaReflectionAsync("SimpleHeels").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("SimpleHeels").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("SimpleHeels").ConfigureAwait(false));

            // 7. Honorific
            var honorificState = _uiShared.GetPluginInstallState("Honorific");
            DrawPluginListRow("Honorific", "Adds titles and plates to characters.", GetPluginStatusLabel(honorificState), honorificState.IsInstalled, honorificState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.InstallPluginViaReflectionAsync("Honorific").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("Honorific").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("Honorific").ConfigureAwait(false));

            // 8. Moodles
            var moodlesState = _uiShared.GetPluginInstallState("Moodles");
            DrawPluginListRow("Moodles", "Status icon customization.", GetPluginStatusLabel(moodlesState), moodlesState.IsInstalled, moodlesState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.AddRepoViaReflectionAsync(SeaOfStarsRepoUrl, SeaOfStarsRepoName).ConfigureAwait(false);
                    Mediator.Publish(new NotificationMessage("Repository Added", $"{SeaOfStarsRepoName} added. Installing...", NotificationType.Info));
                    await _uiShared.InstallPluginViaReflectionAsync("Moodles").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("Moodles").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("Moodles").ConfigureAwait(false));
            
            // 9. PetNames
            var petNamesState = _uiShared.GetPluginInstallState("PetRenamer");
            DrawPluginListRow("PetNames", "Custom names for minions and pets.", GetPluginStatusLabel(petNamesState), petNamesState.IsInstalled, petNamesState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.InstallPluginViaReflectionAsync("PetRenamer").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("PetRenamer").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("PetRenamer").ConfigureAwait(false));

            // 10. Brio
            var brioState = _uiShared.GetPluginInstallState("Brio");
            DrawPluginListRow("Brio", "Animation and posing tool.", GetPluginStatusLabel(brioState), brioState.IsInstalled, brioState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.AddRepoViaReflectionAsync(SeaOfStarsRepoUrl, SeaOfStarsRepoName).ConfigureAwait(false);
                    Mediator.Publish(new NotificationMessage("Repository Added", $"{SeaOfStarsRepoName} added. Installing...", NotificationType.Info));
                    await _uiShared.InstallPluginViaReflectionAsync("Brio").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("Brio").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("Brio").ConfigureAwait(false));
            
            // 11. BypassEmote
            var bypassEmoteState = _uiShared.GetPluginInstallState("BypassEmote");
            DrawPluginListRow("BypassEmote", "Lets you plya any emote, without restriction.", GetPluginStatusLabel(bypassEmoteState), bypassEmoteState.IsInstalled, bypassEmoteState.IsEnabled,
                onInstallAction: async () =>
                {
                    await _uiShared.AddRepoViaReflectionAsync("https://raw.githubusercontent.com/Aspher0/BypassEmote/refs/heads/main/repo.json", "BypassEmote Repo").ConfigureAwait(false);
                    Mediator.Publish(new NotificationMessage("Repository Added", "BypassEmote Repo added. Installing...", NotificationType.Info));
                    await _uiShared.InstallPluginViaReflectionAsync("BypassEmote").ConfigureAwait(false);
                },
                onEnableAction: async () => await _uiShared.EnablePluginViaReflectionAsync("BypassEmote").ConfigureAwait(false),
                onDisableAction: async () => await _uiShared.DisablePluginViaReflectionAsync("BypassEmote").ConfigureAwait(false));
        });

        DrawSettingsCard("Backup & Restore", () =>
        {
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileExport, "Export Config Backup"))
        {
            _showExportModal = true;
            _exportPassword = string.Empty;
            _lastImportError = null;
            _lastImportSuccess = null;
        }
        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileImport, "Import Config Backup"))
        {
            _showImportModal = true;
            _importPassword = string.Empty;
            _importFilePath = string.Empty;
            _lastImportError = null;
            _lastImportSuccess = null;
            _importRestoreCachePath = true;
            _importRestoreShrinkUPath = true;
            _backupCachePath = string.Empty;
            _backupShrinkUPath = string.Empty;
        }

        if (!string.IsNullOrEmpty(_lastImportSuccess))
        {
            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Text.Colored(_lastImportSuccess, ImGuiColors.ParsedGreen);
        }
        if (!string.IsNullOrEmpty(_lastImportError))
        {
            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Text.Colored(_lastImportError, ImGuiColors.DalamudRed);
        }
        }, "Export or import all Sphene settings in a single file. Secret keys can be protected with a password.");
    }

    private void DrawSupporterPage()
    {
        DrawSettingsPageHeader("Supporter");

        DrawSupporterHeroCard();

        if (_apiController.IsSupporter)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawSupporterNameplateSettings();

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawSupporterSection(() =>
            {
                if (SpheneUI.Controls.CollapsibleSection("What you unlock"))
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    SpheneUI.Layout.SectionCard(
                        () => SupporterPerks.DrawList(),
                        accentColor: SpheneUI.Palette.SpheneGold,
                        description: "Supporter status is synced from the server automatically once your Ko-fi donation is linked to your Discord account.");
                }
            });
        }
        else
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawSupporterSection(() =>
                SpheneUI.Layout.SectionCard(
                    () => SupporterPerks.DrawList(),
                    title: "What you unlock",
                    accentColor: SpheneUI.Palette.SpheneGold,
                    description: "Supporter status is synced from the server automatically once your Ko-fi donation is linked to your Discord account."));
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawSupporterSection(() =>
                SpheneUI.Layout.Callout(
                    "Include your Discord username or ID in the donation message so your supporter status can be linked to your account. Linking is checked periodically — re-login refreshes it immediately.",
                    FontAwesomeIcon.InfoCircle, SpheneUI.Palette.CrystalBlue,
                    title: "How linking works"));
        }
    }

    private void DrawSupporterHeroCard()
    {
        var supporter = _apiController.IsSupporter;
        DrawSupporterCard(() =>
        {
            var gold = SpheneUI.Palette.SpheneGold;
            var titlePos = ImGui.GetCursorScreenPos();
            var titleY = titlePos.Y;
            var crownSize = SpheneUI.Icons.Size(FontAwesomeIcon.Crown);
            ImGui.SetCursorScreenPos(titlePos + new Vector2(0, 5f * ImGuiHelpers.GlobalScale));
            SpheneUI.Icons.Icon(FontAwesomeIcon.Crown, gold);
            ImGui.SetCursorScreenPos(new Vector2(
                titlePos.X + crownSize.X + 8f * ImGuiHelpers.GlobalScale, titleY));
            SpheneUI.Text.Display("Sphene Supporter", gold);
            var titleMaxY = MathF.Max(ImGui.GetItemRectMax().Y,
                titleY + ImGui.GetFrameHeight());
            ImGui.SameLine(0, 10f * ImGuiHelpers.GlobalScale);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X,
                titleY + (titleMaxY - titleY - SpheneUI.Layout.BadgeHeight) * 0.5f));
            SpheneUI.Layout.Badge(supporter ? "ACTIVE" : "LOCKED",
                supporter ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted);

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Text.MutedWrapped(supporter
                ? "Thank you for supporting Sphene — your supporter perks are active and visible to other users."
                : "Every donation keeps Sphene's development and servers running — and unlocks the personal perks listed below for your account.");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            if (SpheneBrandIcons.IconTextButton(SpheneBrandIcons.Brand.Kofi,
                    supporter ? "Support again on Ko-fi" : "Support on Ko-fi",
                    "supporter_kofi",
                    tooltip: "Open Sphene's Ko-fi page in your browser."))
            {
                Util.OpenLink("https://ko-fi.com/sphenedev");
            }
        });
    }

    /// <summary>Unindents to the card edge, runs the draw, re-indents — the shared
    /// wrapper for every block on the supporter page.</summary>
    private void DrawSupporterSection(Action content)
    {
        var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Unindent(settingsOptionsIndentX);
        content();
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Indent(settingsOptionsIndentX);
    }

    private void DrawSupporterCard(Action content)
        => DrawSupporterSection(() => SpheneUI.Layout.AccentCard(content));

    private void DrawSupporterNameplateSettings()
    {
        DrawSettingsSectionHeader("Supporter Nameplate", "Display a symbol or text next to the nameplates of Sphene supporters. Supporter status is automatically synced from the server via Ko-fi donations.");

        var showNameplate = _configService.Current.ShowSupporterNameplate;
        if (SpheneUI.Controls.Toggle("Show Supporter Nameplate Indicators", ref showNameplate))
        {
            _configService.Current.ShowSupporterNameplate = showNameplate;
            _configService.Save();
        }

        if (!showNameplate)
        {
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        DrawSupporterSection(() =>
            SpheneUI.Layout.SectionCard(() =>
            {
            DrawSupporterNameplatePreview();
            SpheneUI.Layout.DistanceSeparator();

            BeginSupporterRow("Symbol");
            BeginSupporterSwatchCluster(3);
            DrawSupporterSymbolPicker("supporterSymbol", dockRight: false);
            ImGui.SameLine(0, SpheneUI.Metrics.Gap);
            DrawSupporterColorKeyPicker("supporterSymbolColor", _configService.Current.SupporterColorKey,
                key => _configService.Current.SupporterColorKey = key, dockRight: false);
            ImGui.SameLine(0, SpheneUI.Metrics.Gap);
            DrawSupporterGlowKeyPicker("supporterSymbolGlow",
                _configService.Current.SupporterSymbolGlowEnabled, _configService.Current.SupporterSymbolGlowKey,
                (enabled, key) =>
                {
                    _configService.Current.SupporterSymbolGlowEnabled = enabled;
                    _configService.Current.SupporterSymbolGlowKey = key;
                }, dockRight: false);
            SpheneUI.AttachTooltip("Glyph, text color and optional edge glow for the mark next to your name.");

            BeginSupporterRow("Position");
            var position = (int)_configService.Current.SupporterSymbolPosition;
            if (SpheneUI.Controls.LabeledCombo("##supporterPosition", ref position, "Left\0Right\0Both\0"))
            {
                _configService.Current.SupporterSymbolPosition = (SupporterSymbolPosition)position;
                _configService.Save();
            }
            SpheneUI.AttachTooltip("Where the mark sits relative to your name. Both repeats the symbol on the right.");

            BeginSupporterRow("Title");
            var titleMode = (int)_configService.Current.SupporterTitlePlacement;
            if (SpheneUI.Controls.LabeledCombo("##supporterTitlePlacement", ref titleMode,
                    "Game default\0Above name\0Below name\0Hidden\0"))
            {
                _configService.Current.SupporterTitlePlacement = (SupporterTitlePlacement)titleMode;
                _configService.Save();
            }
            SpheneUI.AttachTooltip("Where your game title appears on your nameplate. Game default keeps your in-game nameplate setting.");

            SpheneUI.Layout.DistanceSeparator();

            var textEnabled = _configService.Current.SupporterTextEnabled;
            if (SpheneUI.Controls.Toggle("Text label##supporterTextEnabled", ref textEnabled))
            {
                _configService.Current.SupporterTextEnabled = textEnabled;
                _configService.Save();
            }
            SpheneUI.AttachTooltip("Show a custom text together with the symbol on your nameplate.");

            if (textEnabled)
            {
                BeginSupporterRow("Text", true);
                var labelText = _configService.Current.SupporterLabelText ?? string.Empty;
                if (SpheneUI.Controls.InputText("##supporterLabelText", ref labelText, 30,
                        dockRight: true))
                {
                    _configService.Current.SupporterLabelText = labelText;
                    _configService.Save();
                }

                BeginSupporterRow("Order", true);
                var labelOrder = (int)_configService.Current.SupporterLabelOrder;
                if (SpheneUI.Controls.LabeledCombo("##supporterLabelOrder", ref labelOrder, "Symbol then Text\0Text then Symbol\0",
                        trailingWidth: SpheneUI.Metrics.TrailingIconReserve
                            + 2f * SpheneUI.Metrics.SwatchSize + 2f * SpheneUI.Metrics.Gap))
                {
                    _configService.Current.SupporterLabelOrder = (SupporterLabelOrder)labelOrder;
                    _configService.Save();
                }
                SpheneUI.AttachTooltip("Whether the symbol or the label text comes first.");

                ImGui.SameLine();
                BeginSupporterSwatchCluster(2);
                DrawSupporterColorKeyPicker("supporterLabelColor", _configService.Current.SupporterLabelColorKey,
                    key => _configService.Current.SupporterLabelColorKey = key, dockRight: false);
                ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                DrawSupporterGlowKeyPicker("supporterLabelGlow",
                    _configService.Current.SupporterLabelGlowEnabled, _configService.Current.SupporterLabelGlowKey,
                    (enabled, key) =>
                    {
                        _configService.Current.SupporterLabelGlowEnabled = enabled;
                        _configService.Current.SupporterLabelGlowKey = key;
                    }, dockRight: false);
            }

            SpheneUI.Layout.DistanceSeparator();

            var iconEnabled = _configService.Current.SupporterIconEnabled;
            if (SpheneUI.Controls.Toggle("Sphene icon##supporterIconEnabled", ref iconEnabled))
            {
                _configService.Current.SupporterIconEnabled = iconEnabled;
                _configService.Save();
            }
            SpheneUI.AttachTooltip("Draws the Sphene icon at the end of the nameplate of supporters.");

            if (iconEnabled)
            {
                DrawSupporterIconPicker();

                var iconLabelEnabled = _configService.Current.SupporterIconLabelEnabled;
                if (SpheneUI.Controls.Toggle("Icon label##supporterIconLabelEnabled", ref iconLabelEnabled,
                    indented: true))
                {
                    _configService.Current.SupporterIconLabelEnabled = iconLabelEnabled;
                    _configService.Save();
                }
                SpheneUI.AttachTooltip("Draws the configured label text next to the Sphene icon (overlay, not part of the nameplate).");

                if (iconLabelEnabled)
                {
                    BeginSupporterRow("Text", true);
                    var iconLabelText = _configService.Current.SupporterIconLabelText ?? string.Empty;
                    if (SpheneUI.Controls.InputText("##supporterIconLabelText", ref iconLabelText, 30,
                            dockRight: true))
                    {
                        _configService.Current.SupporterIconLabelText = iconLabelText;
                        _configService.Save();
                    }
                    SpheneUI.AttachTooltip("Text drawn next to the Sphene icon. Set independently from the nameplate label.");

                    BeginSupporterRow("Colors", true);
                    BeginSupporterSwatchCluster(2);
                    DrawIconLabelColorPicker("supporterIconLabelFg",
                        _configService.Current.SupporterIconLabelColor,
                        color => _configService.Current.SupporterIconLabelColor = color);
                    ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                    DrawIconLabelEdgePicker("supporterIconLabelEdge",
                        _configService.Current.SupporterIconLabelEdgeEnabled,
                        _configService.Current.SupporterIconLabelEdgeColor,
                        (enabled, color) =>
                        {
                            _configService.Current.SupporterIconLabelEdgeEnabled = enabled;
                            _configService.Current.SupporterIconLabelEdgeColor = color;
                        });
                }
            }
            }, title: "Nameplate", accentColor: SpheneUI.Palette.LuminousGold,
            description: "How your name appears to other Sphene users in game."));
    }

    /// <summary>
    /// Mock nameplate strip inside the settings card: renders the configured
    /// mark, label, icon and colors around the player's name so every control
    /// below shows its effect immediately.
    /// </summary>
    private void DrawSupporterNameplatePreview()
    {
        var cfg = _configService.Current;
        var scale = SpheneUI.Scale;
        var symbol = cfg.SupporterSymbolChar ?? string.Empty;
        var label = cfg.SupporterTextEnabled ? cfg.SupporterLabelText ?? string.Empty : string.Empty;

        // Real nameplate data captured by the service — formatted display name,
        // quoted title + FC tag and the actual nameplate text/edge colors.
        var plate = _supporterNameplateService.LocalPlate;
        var name = plate?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = _uiShared.PlayerName;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Player Name";
        }
        var fcTag = plate?.QuotedFreeCompanyTag ?? string.Empty;
        var title = plate?.QuotedTitle ?? string.Empty;
        var titleShown = title.Length > 0 && cfg.SupporterTitlePlacement switch
        {
            SupporterTitlePlacement.AboveName or SupporterTitlePlacement.BelowName => true,
            SupporterTitlePlacement.Hidden => false,
            _ => plate?.DisplayTitle ?? false,
        };
        var titleAbove = cfg.SupporterTitlePlacement switch
        {
            SupporterTitlePlacement.AboveName => true,
            SupporterTitlePlacement.BelowName => false,
            _ => plate?.IsPrefixTitle ?? true,
        };
        var textColor = plate.HasValue ? PlateColor(plate.Value.TextColor) : SpheneUI.Colors.TextPrimary;
        var edgeColor = plate.HasValue ? (Vector4?)PlateColor(plate.Value.EdgeColor) : null;
        var nameFg = plate?.NameColors.Foreground is { } nf ? PlateColor(nf) : textColor;
        var nameEdge = plate?.NameColors.Edge is { } ne ? (Vector4?)PlateColor(ne) : edgeColor;
        var titleFg = plate?.TitleColors.Foreground is { } tf ? PlateColor(tf) : textColor;
        var titleEdge = plate?.TitleColors.Edge is { } te ? (Vector4?)PlateColor(te) : edgeColor;
        var fcFg = plate?.FreeCompanyColors.Foreground is { } ff ? PlateColor(ff) : textColor;
        var fcEdge = plate?.FreeCompanyColors.Edge is { } fe ? (Vector4?)PlateColor(fe) : edgeColor;

        // Key 0 disables the color payload — the part renders in the
        // nameplate's own text color.
        var symbolFg = cfg.SupporterColorKey == 0 ? textColor : ResolveUiForegroundColor(cfg.SupporterColorKey);
        var labelFg = cfg.SupporterLabelColorKey == 0 ? textColor : ResolveUiForegroundColor(cfg.SupporterLabelColorKey);
        // Without an explicit glow the game renders supporter parts with the
        // regular nameplate text edge.
        var symbolGlow = cfg.SupporterSymbolGlowEnabled ? ResolveUiGlowColor(cfg.SupporterSymbolGlowKey) : edgeColor;
        var labelGlow = cfg.SupporterLabelGlowEnabled ? ResolveUiGlowColor(cfg.SupporterLabelGlowKey) : edgeColor;
        var symbolFirst = cfg.SupporterLabelOrder == SupporterLabelOrder.SymbolThenText;
        var position = cfg.SupporterSymbolPosition;
        var showLeft = position != SupporterSymbolPosition.Right;
        var showRight = position != SupporterSymbolPosition.Left;

        var icon = cfg.SupporterIconEnabled
            ? _supporterIconService.GetIcon(cfg.SupporterIconId ?? string.Empty) : null;
        // The default Sphene icon is not a catalog entry — it lives in the
        // nameplate service's embedded texture.
        IDalamudTextureWrap? defaultIconWrap = null;
        if (cfg.SupporterIconEnabled && icon == null)
        {
            _ = _supporterNameplateService.TryGetDefaultIconWrap(out defaultIconWrap);
        }
        var iconLabel = cfg.SupporterIconLabelEnabled
            ? cfg.SupporterIconLabelText ?? string.Empty : string.Empty;
        var iconLabelFg = ImGui.ColorConvertU32ToFloat4(cfg.SupporterIconLabelColor);
        var iconLabelEdge = cfg.SupporterIconLabelEdgeEnabled
            ? (Vector4?)ImGui.ColorConvertU32ToFloat4(cfg.SupporterIconLabelEdgeColor)
            : null;

        var textH = ImGui.GetTextLineHeight();
        var spaceW = ImGui.CalcTextSize(" ").X;
        var markGap = 8f * scale;
        var iconHeight = textH * 1.15f;
        var iconSize = icon != null ? icon.AspectSize(iconHeight)
            : defaultIconWrap != null ? new Vector2(iconHeight) : Vector2.Zero;

        float PartW(string s) => s.Length == 0 ? 0f : ImGui.CalcTextSize(s).X;
        float MarkW(bool withLabel)
        {
            var w = (symbol.Length > 0 ? PartW(symbol) : 0f)
                + (withLabel && label.Length > 0 ? PartW(label) : 0f);
            if (w > 0f && symbol.Length > 0 && withLabel && label.Length > 0)
            {
                w += spaceW;
            }
            return w;
        }

        var leftW = showLeft ? MarkW(withLabel: true) : 0f;
        // "Both" repeats only the symbol on the right, mirroring the service.
        var rightW = showRight ? MarkW(withLabel: position == SupporterSymbolPosition.Right) : 0f;
        var fcW = PartW(fcTag);
        var titleW = PartW(title);
        var iconLabelW = PartW(iconLabel);
        var totalW = leftW + PartW(name) + rightW + fcW + iconSize.X + iconLabelW
            + (leftW > 0f ? markGap : 0f)
            + (rightW > 0f ? markGap : 0f)
            + (fcW > 0f ? markGap : 0f)
            + (iconSize.X > 0f ? markGap : 0f)
            + (iconLabelW > 0f && iconSize.X > 0f ? spaceW : 0f);

        var drawList = ImGui.GetWindowDrawList();
        var stripMin = ImGui.GetCursorScreenPos();
        var stripW = MathF.Max(0f, SpheneUI.Controls.BoundedAvailX());
        var padY = 7f * scale;
        var lineGap = 3f * scale;
        var stripH = textH + padY * 2f + (titleShown ? textH + lineGap : 0f);
        var radius = 6f * scale;
        drawList.AddRectFilled(stripMin, stripMin + new Vector2(stripW, stripH),
            ImGui.ColorConvertFloat4ToU32(SpheneUI.Colors.FrameBg with { W = 0.55f }), radius);
        drawList.AddRect(stripMin, stripMin + new Vector2(stripW, stripH),
            ImGui.ColorConvertFloat4ToU32(SpheneUI.Colors.Border with { W = 0.6f }), radius);
        ImGui.Dummy(new Vector2(stripW, stripH));

        var nameLineY = stripMin.Y + padY + (titleShown && titleAbove ? textH + lineGap : 0f);
        var x = stripMin.X + MathF.Max(8f * scale, (stripW - totalW) * 0.5f);
        var y = nameLineY;
        var glowOff = MathF.Max(1f, scale);

        void DrawPart(string text, Vector4 fg, Vector4? glow)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (glow.HasValue)
            {
                var glowU32 = ImGui.ColorConvertFloat4ToU32(glow.Value);
                drawList.AddText(new Vector2(x + glowOff, y), glowU32, text);
                drawList.AddText(new Vector2(x - glowOff, y), glowU32, text);
                drawList.AddText(new Vector2(x, y + glowOff), glowU32, text);
                drawList.AddText(new Vector2(x, y - glowOff), glowU32, text);
            }
            drawList.AddText(new Vector2(x, y), ImGui.ColorConvertFloat4ToU32(fg), text);
            x += PartW(text);
        }

        void DrawMark(bool withLabel)
        {
            var drew = false;
            void Part(string text, Vector4 fg, Vector4? glow)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                if (drew)
                {
                    x += spaceW;
                }
                DrawPart(text, fg, glow);
                drew = true;
            }
            if (symbolFirst)
            {
                Part(symbol, symbolFg, symbolGlow);
                if (withLabel)
                {
                    Part(label, labelFg, labelGlow);
                }
            }
            else
            {
                if (withLabel)
                {
                    Part(label, labelFg, labelGlow);
                }
                Part(symbol, symbolFg, symbolGlow);
            }
        }

        if (titleShown)
        {
            var titleY = titleAbove
                ? stripMin.Y + padY
                : stripMin.Y + padY + textH + lineGap;
            var savedPos = new Vector2(x, y);
            x = stripMin.X + MathF.Max(8f * scale, (stripW - titleW) * 0.5f);
            y = titleY;
            DrawPart(title, titleFg, titleEdge);
            x = savedPos.X;
            y = savedPos.Y;
        }

        if (leftW > 0f)
        {
            DrawMark(withLabel: true);
            x += markGap;
        }
        DrawPart(name, nameFg, nameEdge);
        if (rightW > 0f)
        {
            x += markGap;
            DrawMark(withLabel: position == SupporterSymbolPosition.Right);
        }
        if (fcW > 0f)
        {
            x += markGap;
            DrawPart(fcTag, fcFg, fcEdge);
        }
        // Small down-bias like GetNameplateIconPosition's 0.55 line factor —
        // glyphs sit baseline-low, so the icon centers a hair below the
        // geometric line center.
        var iconAnchorY = nameLineY + (textH - iconSize.Y) * 0.5f + textH * 0.05f;
        if (icon != null)
        {
            x += markGap;
            var (handle, uv0, uv1) = icon.GetFrame(ImGui.GetTime());
            SpheneUI.Media.SpriteInRect(drawList, handle,
                new Vector2(x, iconAnchorY), new Vector2(x + iconSize.X, iconAnchorY + iconSize.Y), uv0, uv1);
            x += iconSize.X;
        }
        else if (defaultIconWrap != null)
        {
            x += markGap;
            SpheneUI.Media.ImageInRect(drawList, defaultIconWrap,
                new Vector2(x, iconAnchorY), new Vector2(x + iconSize.X, iconAnchorY + iconSize.Y));
            x += iconSize.X;
        }
        if (iconLabelW > 0f && (icon != null || defaultIconWrap != null))
        {
            x += spaceW;
            DrawPart(iconLabel, iconLabelFg, iconLabelEdge);
        }
    }

    /// <summary>
    /// Positions the cursor so <paramref name="count"/> swatches drawn via
    /// SameLine end flush at the shared control edge, vertically centered on
    /// the current frame row.
    /// </summary>
    private static void BeginSupporterSwatchCluster(int count)
    {
        var swatch = SpheneUI.Metrics.SwatchSize;
        var gap = SpheneUI.Metrics.Gap;
        var width = count * swatch + (count - 1) * gap;
        var cursor = ImGui.GetCursorScreenPos();
        var right = cursor.X + SpheneUI.Controls.BoundedAvailX()
            - SpheneUI.Metrics.TrailingIconReserve;
        ImGui.SetCursorScreenPos(new Vector2(right - width,
            cursor.Y + (ImGui.GetFrameHeight() - swatch) * 0.5f));
    }

    private static void BeginSupporterRow(string label, bool indented = false)
    {
        ImGui.AlignTextToFramePadding();
        if (indented)
        {
            SpheneUI.Text.Colored("  " + label, ImGuiColors.DalamudGrey3);
        }
        else
        {
            SpheneUI.Text.Colored(label, ImGuiColors.DalamudGrey);
        }
        ImGui.SameLine(SupporterRowLabelColumn * ImGuiHelpers.GlobalScale);
    }

    private void DrawSupporterIconPicker()
    {
        var catalog = _supporterIconService.GetCatalog();
        var currentId = _configService.Current.SupporterIconId ?? string.Empty;

        BeginSupporterRow("Icon Image", true);
        var previewLabel = currentId.Length == 0 ? "Default (Sphene Icon)" : currentId;
        var previewIcon = _supporterIconService.GetIcon(currentId);
        var previewHeight = ImGui.GetTextLineHeightWithSpacing() * 1.6f;
        var trailingWidth = previewIcon != null
            ? previewIcon.AspectSize(previewHeight).X + ImGui.GetStyle().ItemSpacing.X
            : currentId.Length > 0
                ? ImGui.CalcTextSize("(not on server)").X + ImGui.GetStyle().ItemSpacing.X
                : 0f;
        ImGui.SetNextItemWidth(MathF.Max(60f, SpheneUI.Controls.FillRowWidth(trailingWidth)));
        using (var controlStyle = SpheneUI.Controls.PushValueControlStyle())
        {
            if (ImGui.BeginCombo("##supporterIconPicker", previewLabel))
            {
                if (SpheneUI.Controls.Selectable("Default (Sphene Icon)", currentId.Length == 0))
                {
                    _configService.Current.SupporterIconId = string.Empty;
                    _configService.Save();
                }
                for (var i = 0; i < catalog.Count; i++)
                {
                    var iconInfo = catalog[i];
                    var label = iconInfo.Animated ? iconInfo.Name + " (animated)" : iconInfo.Name;
                    if (SpheneUI.Controls.Selectable(label + "##" + iconInfo.Id, string.Equals(currentId, iconInfo.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        _configService.Current.SupporterIconId = iconInfo.Id;
                        _configService.Save();
                    }
                }
                ImGui.EndCombo();
            }
        }
        SpheneUI.AttachTooltip(catalog.Count == 0
            ? "No custom icons available on this server. The server admin can add PNG, GIF or WebP files to the supporter icon folder."
            : "Pick a server-provided icon. Animated icons play on the nameplate.");

        if (previewIcon != null)
        {
            ImGui.SameLine();
            var (handle, uv0, uv1) = previewIcon.GetFrame(ImGui.GetTime());
            SpheneUI.Media.Sprite(handle, previewIcon.AspectSize(previewHeight), uv0, uv1);
        }
        else if (currentId.Length > 0 && !_supporterIconService.IsIconKnown(currentId))
        {
            ImGui.SameLine();
            SpheneUI.Text.Colored("(not on server)", ImGuiColors.DalamudOrange);
        }
    }

    private const float SupporterRowLabelColumn = 110f;
    private static readonly string[] SupporterSymbolPresets = ["None", "✦", "★", "☆", "♥", "♡", "◆", "◇"];
    private const int UiForegroundKeyInlineMax = 77;
    private const int UiForegroundKeyExtendedStart = 500;
    private const int UiForegroundKeyExtendedMax = 511;
    private const int UiForegroundPaletteMaxIndex = UiForegroundKeyInlineMax + (UiForegroundKeyExtendedMax - UiForegroundKeyExtendedStart + 1);

    private void DrawSupporterColorKeyPicker(string id, ushort colorKey, Action<ushort> apply,
        bool dockRight = true)
    {
        var preview = ResolveUiForegroundColor(colorKey);
        var swatchSize = SpheneUI.Metrics.SwatchSize;
        var popupId = "##" + id + "_palette";

        // Dock the swatch flush with the shared control edge (bound minus the
        // help column so it aligns with every other value control on the page).
        if (dockRight)
        {
            SpheneUI.Controls.DockToRightEdge(swatchSize);
        }

        var clicked = colorKey == 0
            ? SpheneUI.Controls.GlyphSwatch(popupId + "_btn", "–", swatchSize)
            : SpheneUI.Controls.ColorSwatch(popupId + "_btn", preview);
        if (clicked)
        {
            ImGui.OpenPopup(popupId);
        }
        SpheneUI.AttachTooltip(colorKey == 0
            ? "No text color — the nameplate default is used. Click to pick a color."
            : $"Game UI foreground color (key {colorKey}). Click to choose a color.");

        if (SpheneUI.Popups.BeginStyled(popupId))
        {
            // Key 0 is the payload's disabled state — the transparent cell
            // offers it as "no color, keep the default nameplate color".
            DrawSupporterPaletteCells(SortedUiForegroundKeys(), ResolveUiForegroundColor,
                colorKey, colorKey != 0, apply, applyNone: () => apply(0));
            SpheneUI.Popups.EndStyled();
        }
    }

    private void DrawSupporterGlowKeyPicker(string id, bool enabled, ushort colorKey,
        Action<bool, ushort> apply, bool dockRight = true)
    {
        var swatchSize = SpheneUI.Metrics.SwatchSize;
        var popupId = "##" + id + "_palette";

        if (dockRight)
        {
            SpheneUI.Controls.DockToRightEdge(swatchSize);
        }

        var clicked = enabled
            ? SpheneUI.Controls.FrameSwatch(popupId + "_btn", ResolveUiGlowColor(colorKey))
            : SpheneUI.Controls.GlyphSwatch(popupId + "_btn", "–", swatchSize);
        if (clicked)
        {
            ImGui.OpenPopup(popupId);
        }
        SpheneUI.AttachTooltip(enabled
            ? $"Game UI glow color (key {colorKey}). Click to change or disable."
            : "Edge glow color disabled. Click to pick a color.");

        if (SpheneUI.Popups.BeginStyled(popupId))
        {
            DrawSupporterPaletteCells(SortedUiGlowKeys(), ResolveUiGlowColor,
                colorKey, enabled, key => apply(true, key), frame: true,
                applyNone: () => apply(false, colorKey));
            SpheneUI.Popups.EndStyled();
        }
    }

    /// <summary>
    /// Free RGBA picker for the icon label fill — a regular filled swatch,
    /// since the color is what fills the letters.
    /// </summary>
    private void DrawIconLabelColorPicker(string id, uint color, Action<uint> apply)
    {
        var preview = ImGui.ColorConvertU32ToFloat4(color);
        var popupId = "##" + id + "_picker";
        if (SpheneUI.Controls.ColorSwatch(popupId + "_btn", preview))
        {
            ImGui.OpenPopup(popupId);
        }
        SpheneUI.AttachTooltip("Fill color of the icon label text. Click to pick a color.");
        if (SpheneUI.Popups.BeginStyled(popupId))
        {
            if (ImGui.ColorPicker4(popupId, ref preview,
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoLabel))
            {
                apply(ImGui.ColorConvertFloat4ToU32(preview));
                _configService.Save();
            }
            SpheneUI.Popups.EndStyled();
        }
    }

    /// <summary>
    /// Free RGBA picker for the icon label outline — rendered as a frame ring
    /// so the swatch reads as "edge" rather than fill. Disabled state shows a
    /// dash like the glow pickers; the popup offers None + the color picker.
    /// </summary>
    private void DrawIconLabelEdgePicker(string id, bool enabled, uint color, Action<bool, uint> apply)
    {
        var popupId = "##" + id + "_picker";
        var clicked = enabled
            ? SpheneUI.Controls.FrameSwatch(popupId + "_btn", ImGui.ColorConvertU32ToFloat4(color))
            : SpheneUI.Controls.GlyphSwatch(popupId + "_btn", "–");
        if (clicked)
        {
            ImGui.OpenPopup(popupId);
        }
        SpheneUI.AttachTooltip(enabled
            ? "Outline color drawn as a frame around the icon label text. Click to change or disable."
            : "Label outline disabled. Click to pick a color.");
        if (SpheneUI.Popups.BeginStyled(popupId))
        {
            if (SpheneUI.Controls.Selectable("None", !enabled))
            {
                apply(false, color);
                _configService.Save();
                ImGui.CloseCurrentPopup();
            }
            var preview = ImGui.ColorConvertU32ToFloat4(color);
            if (ImGui.ColorPicker4(popupId, ref preview,
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoLabel))
            {
                apply(true, ImGui.ColorConvertFloat4ToU32(preview));
                _configService.Save();
            }
            SpheneUI.Popups.EndStyled();
        }
    }

    private void DrawSupporterPaletteCells(List<ushort> palette, Func<ushort, Vector4> resolveColor,
        ushort selectedKey, bool hasSelection, Action<ushort> apply,
        bool frame = false, Action? applyNone = null)
    {
        const int columns = 8;
        var cell = ImGui.GetFrameHeight() + 2f * ImGuiHelpers.GlobalScale;
        var leadingNone = applyNone != null ? 1 : 0;
        if (applyNone != null)
        {
            if (SpheneUI.Controls.TransparentSwatch("swNone", cell, !hasSelection))
            {
                applyNone();
                _configService.Save();
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("None — no color.");
        }
        for (var i = 0; i < palette.Count; i++)
        {
            var key = palette[i];
            if ((i + leadingNone) % columns != 0)
            {
                ImGui.SameLine(0, SpheneUI.Metrics.Gap);
            }
            using var swatchId = ImRaii.PushId("sw" + i);
            var clicked = frame
                ? SpheneUI.Controls.FrameSwatch("swatch",
                    resolveColor(key), cell, hasSelection && key == selectedKey)
                : SpheneUI.Controls.ColorSwatch("swatch",
                    resolveColor(key), cell, hasSelection && key == selectedKey);
            if (clicked)
            {
                apply(key);
                _configService.Save();
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip($"Key {key}");
        }
    }

    private static List<ushort>? _sortedUiForegroundKeys;

    /// <summary>
    /// The UI-foreground color keys in palette order: greys first (light to
    /// dark), then hues red to magenta, saturated to muted inside one hue.
    /// Keys resolving to an identical color value appear only once.
    /// </summary>
    private static List<ushort> SortedUiForegroundKeys()
        => _sortedUiForegroundKeys ??= SortedUiColorKeys(ResolveUiForegroundColor);

    /// <summary>
    /// Glow pickers show the same key set in the same order as the foreground
    /// palette — only the resolved cell colors differ. Sorting the glow
    /// palette by its own colors produced a diverging order.
    /// </summary>
    private static List<ushort> SortedUiGlowKeys()
        => SortedUiForegroundKeys();

    private static List<ushort> SortedUiColorKeys(Func<ushort, Vector4> resolveColor)
    {
        var seen = new HashSet<uint>();
        var entries = new List<(ushort Key, int HueBucket, float L, float S, float H)>();
        for (var i = 0; i <= UiForegroundPaletteMaxIndex; i++)
        {
            var key = UiForegroundPaletteIndexToColorKey(i);
            // Key 0 has no UIColor row — it is the "color off" state, not a
            // color. Rendered as a black cell it picks a disabled payload and
            // the nameplate falls back to the default text color.
            if (key == 0)
            {
                continue;
            }
            var color = resolveColor(key);
            if (!seen.Add(ImGui.ColorConvertFloat4ToU32(color)))
            {
                continue;
            }
            RgbaToHsl(color, out var h, out var s, out var l);
            entries.Add((key, (int)(h / HueBucketDegrees), l, s, h));
        }
        // Greys first (light to dark), then one hue family after another; each
        // family runs as a light-to-dark gradient so neighbours flow visually.
        entries.Sort(static (a, b) =>
        {
            var greyA = a.S < 0.08f;
            var greyB = b.S < 0.08f;
            if (greyA != greyB)
            {
                return greyA ? -1 : 1;
            }
            if (greyA)
            {
                return b.L.CompareTo(a.L);
            }
            var bucket = a.HueBucket.CompareTo(b.HueBucket);
            if (bucket != 0)
            {
                return bucket;
            }
            var light = b.L.CompareTo(a.L);
            if (light != 0)
            {
                return light;
            }
            var sat = b.S.CompareTo(a.S);
            return sat != 0 ? sat : a.H.CompareTo(b.H);
        });
        return entries.ConvertAll(static e => e.Key);
    }

    private const float HueBucketDegrees = 24f;

    private static void RgbaToHsl(Vector4 c, out float h, out float s, out float l)
    {
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        var d = max - min;
        l = (max + min) * 0.5f;
        if (d <= 0f)
        {
            h = 0f;
            s = 0f;
            return;
        }
        s = d / (1f - MathF.Abs(2f * l - 1f));
        if (c.X >= c.Y && c.X >= c.Z)
        {
            h = (c.Y - c.Z) / d % 6f;
        }
        else if (c.Y >= c.Z)
        {
            h = (c.Z - c.X) / d + 2f;
        }
        else
        {
            h = (c.X - c.Y) / d + 4f;
        }
        h = (h * 60f + 360f) % 360f;
    }

    private void DrawSupporterSymbolPicker(string id, bool dockRight = true)
    {
        var symbolChar = _configService.Current.SupporterSymbolChar ?? string.Empty;
        var popupId = "##" + id + "_palette";
        var scale = ImGuiHelpers.GlobalScale;
        var cell = ImGui.GetFrameHeight();
        var buttonSize = SpheneUI.Metrics.SwatchSize;

        // Reserve the color swatch slot that follows on the same row so both
        // controls end flush at the shared right edge together.
        if (dockRight)
        {
            SpheneUI.Controls.DockToRightEdge(buttonSize,
                SpheneUI.Metrics.Gap + SpheneUI.Metrics.SwatchSize);
        }
        if (SpheneUI.Controls.GlyphSwatch(popupId + "_btn",
                symbolChar.Length > 0 ? symbolChar : "–", buttonSize))
        {
            ImGui.OpenPopup(popupId);
        }
        SpheneUI.AttachTooltip(
            "Symbol next to supporter names. Click to pick a preset or type a custom one.");

        if (SpheneUI.Popups.BeginStyled(popupId))
        {
            const int columns = 4;
            var cellSize = cell + 6f * scale;
            for (var i = 0; i < SupporterSymbolPresets.Length; i++)
            {
                if (i % columns != 0)
                {
                    ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                }
                var preset = SupporterSymbolPresets[i];
                var isCurrent = i == 0
                    ? symbolChar.Length == 0
                    : string.Equals(symbolChar, preset, StringComparison.Ordinal);
                using var swatchId = ImRaii.PushId("sym" + i);
                if (SpheneUI.Controls.GlyphSwatch("symSwatch",
                        i == 0 ? "–" : preset, cellSize, isCurrent))
                {
                    _configService.Current.SupporterSymbolChar = i == 0 ? string.Empty : preset;
                    _configService.Save();
                    ImGui.CloseCurrentPopup();
                }
                SpheneUI.AttachTooltip(i == 0 ? "None" : preset);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Colored("Custom", ImGuiColors.DalamudGrey);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * scale);
            var custom = symbolChar;
            if (SpheneUI.Controls.InputText("##supporterCustomSymbol", ref custom, 4,
                    ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _configService.Current.SupporterSymbolChar = custom;
                _configService.Save();
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("Type any short symbol; Enter applies it.");
            SpheneUI.Popups.EndStyled();
        }
    }

    /// <summary>
    /// Converts a packed nameplate color (<c>0xRRGGBBAA</c>, as stored in the
    /// nameplate info object) to a <see cref="Vector4"/>.
    /// </summary>
    private static Vector4 PlateColor(uint c)
        => new(((c >> 24) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f,
            ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f);

    private static Vector4 ResolveUiForegroundColor(ushort colorKey)
    {
        try
        {
            var rgba = new UIForegroundPayload(colorKey).RGBA;
            return new Vector4(((rgba >> 24) & 0xFF) / 255f,
                ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, 1f);
        }
        catch
        {
            return SpheneUI.Palette.SpheneGold;
        }
    }

    private static Vector4 ResolveUiGlowColor(ushort colorKey)
    {
        try
        {
            var rgba = new UIGlowPayload(colorKey).RGBA;
            return new Vector4(((rgba >> 24) & 0xFF) / 255f,
                ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, 1f);
        }
        catch
        {
            return SpheneUI.Palette.SpheneGold;
        }
    }

    private static ushort UiForegroundPaletteIndexToColorKey(int index)
    {
        return index <= UiForegroundKeyInlineMax
            ? (ushort)index
            : (ushort)(UiForegroundKeyExtendedStart + index - UiForegroundKeyInlineMax - 1);
    }

    private void DrawExportBackupModal()
    {
        if (!_showExportModal) return;

        var center = (ImGui.GetMainViewport().Size / 2) + ImGui.GetMainViewport().Pos;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(400, 180), new Vector2(600, 300));

        if (ImGui.Begin("Export Config Backup", ref _showExportModal, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            using (SpheneUI.Theme.ContextMenuScope())
            {
                using var cardBound = SpheneUI.Layout.SuppressBound();
                SpheneUI.Text.ColoredWrapped("Enter an optional password to protect secret keys. Leave blank for unencrypted export.", ImGuiColors.DalamudGrey);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Text.Plain("Password (optional):");
                SpheneUI.Controls.InputText("##exportPassword", ref _exportPassword, 64, ImGuiInputTextFlags.Password);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

                var exportClicked = SpheneUI.Controls.DialogButtons("exportBackup", "Export", "Cancel");
                if (exportClicked == 1)
                {
                    var defaultFileName = $"sphene-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json";
                    _fileDialogManager.SaveFileDialog("Export Config Backup", ".json", defaultFileName, "json", (success, path) =>
                    {
                        if (success && !string.IsNullOrEmpty(path))
                        {
                            try
                            {
                                _configBackupService.ExportToFile(path, string.IsNullOrWhiteSpace(_exportPassword) ? null : _exportPassword);
                                _lastImportSuccess = $"Backup exported to {Path.GetFileName(path)}";
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to export config backup");
                                _lastImportError = "Failed to export backup: " + ex.Message;
                            }
                        }
                        _showExportModal = false;
                    });
                }

                if (exportClicked == 2)
                {
                    _showExportModal = false;
                }
            }
            ImGui.End();
        }
    }

    private void DrawImportBackupModal()
    {
        if (!_showImportModal) return;

        var center = (ImGui.GetMainViewport().Size / 2) + ImGui.GetMainViewport().Pos;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(450, 250), new Vector2(700, 500));

        if (ImGui.Begin("Import Config Backup", ref _showImportModal, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            using (SpheneUI.Theme.ContextMenuScope())
            {
                using var cardBound = SpheneUI.Layout.SuppressBound();
                if (string.IsNullOrEmpty(_importFilePath))
                {
                    SpheneUI.Text.ColoredWrapped("Select a backup file to restore all settings.", ImGuiColors.DalamudGrey);
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                    if (SpheneUI.Controls.Button("Select Backup File", new Vector2(0, 0)))
                    {
                        _fileDialogManager.OpenFileDialog("Import Config Backup", ".json", (success, path) =>
                        {
                            if (success && !string.IsNullOrEmpty(path) && File.Exists(path))
                            {
                                _importFilePath = path;
                                try
                                {
                                    var json = File.ReadAllText(path);
                                    var dto = JsonSerializer.Deserialize<SpheneBackupDto>(json);
                                    if (dto != null)
                                    {
                                        _backupCachePath = ConfigBackupService.GetBackupCacheFolderPath(dto);
                                        _backupShrinkUPath = ConfigBackupService.GetBackupShrinkUPath(dto);
                                    }
                                }
                                catch { /* ignore */ }
                            }
                        });
                    }
                }
                else
                {
                    SpheneUI.Text.ColoredWrapped($"Selected: {Path.GetFileName(_importFilePath)}", ImGuiColors.ParsedGreen);
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                    if (string.IsNullOrEmpty(_importPassword))
                    {
                        SpheneUI.Text.ColoredWrapped("If the backup is password-protected, enter the password below. Otherwise leave blank.", ImGuiColors.DalamudGrey);
                    }
                    SpheneUI.Text.Plain("Password (if encrypted):");
                    SpheneUI.Controls.InputText("##importPassword", ref _importPassword, 64, ImGuiInputTextFlags.Password);
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

                    DrawSettingsSectionHeader("Path Options", "Choose whether to restore folder paths from the backup or keep current paths.");

                    var currentCache = _configService.Current.CacheFolder;
                    if (!string.IsNullOrEmpty(_backupCachePath))
                    {
                        SpheneUI.Controls.ToggleChip($"Restore Cache Folder from backup: {_backupCachePath}", ref _importRestoreCachePath);
                        if (!_importRestoreCachePath)
                        {
                            ImGui.Indent();
                            SpheneUI.Text.ColoredWrapped($"Current cache folder will be kept: {currentCache}", ImGuiColors.DalamudGrey);
                            ImGui.Unindent();
                        }
                    }
                    else
                    {
                        _importRestoreCachePath = false;
                        SpheneUI.Text.ColoredWrapped("No cache folder path in backup.", ImGuiColors.DalamudGrey);
                    }

                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                    var currentShrinkU = _shrinkUConfigService?.Current?.BackupFolderPath ?? "Not configured";
                    if (!string.IsNullOrEmpty(_backupShrinkUPath))
                    {
                        SpheneUI.Controls.ToggleChip($"Restore ShrinkU Backup Folder from backup: {_backupShrinkUPath}", ref _importRestoreShrinkUPath);
                        if (!_importRestoreShrinkUPath)
                        {
                            ImGui.Indent();
                            SpheneUI.Text.ColoredWrapped($"Current ShrinkU folder will be kept: {currentShrinkU}", ImGuiColors.DalamudGrey);
                            ImGui.Unindent();
                        }
                    }
                    else
                    {
                        _importRestoreShrinkUPath = false;
                        SpheneUI.Text.ColoredWrapped("No ShrinkU backup folder path in backup.", ImGuiColors.DalamudGrey);
                    }

                    SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

                    var importClicked = SpheneUI.Controls.DialogButtons("importBackup", "Import", "Cancel");
                    if (importClicked == 1)
                    {
                        try
                        {
                            var password = string.IsNullOrWhiteSpace(_importPassword) ? null : _importPassword;
                            var result = _configBackupService.ImportFromFile(_importFilePath, password, _importRestoreCachePath, _importRestoreShrinkUPath, _shrinkUConfigService);
                            if (result.Success)
                            {
                                _lastImportSuccess = $"Import completed: {result.RestoredCount} config(s) restored, {result.SkippedCount} skipped, {result.FailedCount} failed.";
                                _lastImportError = null;
                            }
                            else
                            {
                                _lastImportError = result.ErrorMessage ?? "Import failed.";
                                _lastImportSuccess = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to import config backup");
                            _lastImportError = "Failed to import backup: " + ex.Message;
                            _lastImportSuccess = null;
                        }
                        _showImportModal = false;
                    }
                    else if (importClicked == 2)
                    {
                        _showImportModal = false;
                    }
                }
            }
            ImGui.End();
        }
    }

    private void DrawGeneralUserManagement()
    {
        DrawSettingsPageHeader("People & Notes", "Manage note import/export and default note popup behavior.");

        DrawSettingsCard("Note Transfer", () =>
        {
            var overwriteExistingLabels = _overwriteExistingLabels;
            if (SpheneUI.Controls.Button("Export Notes"))
            {
                ImGui.SetClipboardText(UiSharedService.GetNotes(
                    _pairManager.DirectPairs.UnionBy(
                        _pairManager.GroupPairs.SelectMany(p => p.Value),
                        p => p.UserData,
                        UserDataComparer.Instance).ToList()));
            }
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Import Notes"))
            {
                _notesSuccessfullyApplied = null;
                var notes = ImGui.GetClipboardText();
                _notesSuccessfullyApplied = _uiShared.ApplyNotesFromClipboard(notes, overwriteExistingLabels);
            }
            ImGui.SameLine();
            PeopleNotesOptionBlock.DrawOverwriteExistingLabelsOption(ref overwriteExistingLabels, _uiShared, "PeopleNotesOverwriteExistingLabels");
            _overwriteExistingLabels = overwriteExistingLabels;

            if (_notesSuccessfullyApplied is not null)
            {
                SpheneUI.Layout.VerticalSpace();
                if (_notesSuccessfullyApplied!.Value)
                    SpheneUI.Text.Colored("Notes successfully applied.", ImGuiColors.ParsedGreen);
                else
                    SpheneUI.Text.Colored("Failed to apply notes.", ImGuiColors.DalamudRed);
            }
        });

        DrawSettingsCard("Labels & Popups", () =>
        {
            PeopleNotesOptionBlock.DrawOpenNotesPopupOnUserAdditionOption(_configService, _uiShared, "PeopleNotesOpenNotesPopupOnUserAddition");
            PeopleNotesOptionBlock.DrawAutoPopulateNotesUsingPlayerNamesOption(_configService, _uiShared, "PeopleNotesAutoPopulateNotesUsingPlayerNames");
        });
    }

    private void DrawGeneralUiDisplaySettings()
    {
        DrawSettingsPageHeader("Appearance", "Configure interface behavior, user list presentation, and profile display options.");

        DrawSettingsCard("Basic Interface", () =>
        {
            AppearanceOptionBlock.DrawShowSpheneIconOption(_configService, "AppearanceShowSpheneIcon");
            AppearanceOptionBlock.DrawHideSpheneIconInGposeOption(_configService, "AppearanceHideSpheneIconInGpose");
            AppearanceOptionBlock.DrawLockSpheneIconPositionOption(_configService, "AppearanceLockSpheneIconPosition");
            AppearanceOptionBlock.DrawEnableGameRightClickMenusOption(_configService, "AppearanceEnableGameRightClickMenus");
            AppearanceOptionBlock.DrawReopenSpheneHubOnStartupOption(_configService, "AppearanceReopenSpheneHubOnStartup");
        });

        DrawSettingsCard("Window Effects", () =>
        {
            var theme = SpheneTheme.Current;
            var themeChanged = false;

            var enableWindowBlur = theme.EnableWindowBlur;
            if (SpheneUI.Controls.Toggle("Background Blur", ref enableWindowBlur))
            {
                theme.EnableWindowBlur = enableWindowBlur;
                themeChanged = true;
            }
            using (ImRaii.Disabled(!theme.EnableWindowBlur))
            {
                var windowBlurStrength = theme.WindowBlurStrength * 100f;
                if (SpheneUI.Controls.SliderFloat("Blur Strength##AppearanceWindowBlurStrength", ref windowBlurStrength, 0.0f, 100.0f, "%.0f%%"))
                {
                    theme.WindowBlurStrength = windowBlurStrength / 100f;
                    themeChanged = true;
                }
            }

            SpheneUI.Layout.Space(SpheneUI.Spacing.X8);

            var enablePanelBlur = theme.CompactEnableWindowBlur;
            if (SpheneUI.Controls.Toggle("Panel Background Blur", ref enablePanelBlur))
            {
                theme.CompactEnableWindowBlur = enablePanelBlur;
                themeChanged = true;
            }
            using (ImRaii.Disabled(!theme.CompactEnableWindowBlur))
            {
                var panelBlurStrength = theme.CompactWindowBlurStrength * 100f;
                if (SpheneUI.Controls.SliderFloat("Panel Blur Strength##AppearancePanelBlurStrength", ref panelBlurStrength, 0.0f, 100.0f, "%.0f%%"))
                {
                    theme.CompactWindowBlurStrength = panelBlurStrength / 100f;
                    themeChanged = true;
                }
            }

            SpheneUI.Text.ColoredWrapped("Blurs the game scene behind Sphene windows. Changes apply to your active theme and are saved with it.", ImGuiColors.DalamudGrey);

            if (themeChanged)
            {
                theme.NotifyThemeChanged();
                _hasUnsavedThemeChanges = true;
            }
        }, "Softly blurs the game behind windows and the main panel.");

        // ShrinkU integration settings moved to Overview page alongside version info

        DrawSettingsCard("Server Info Bar", () =>
        {
            var enableDtrEntry = AppearanceOptionBlock.DrawShowStatusInServerInfoBarOption(_configService, "AppearanceShowStatusInServerInfoBar");
            using (ImRaii.Disabled(!enableDtrEntry))
            {
                using var indent = ImRaii.PushIndent();
                AppearanceOptionBlock.DrawShowUidInTooltipOption(_configService, "AppearanceShowUidInTooltip");
                AppearanceOptionBlock.DrawPreferNotesInTooltipOption(_configService, "AppearancePreferNotesInTooltip");
                AppearanceOptionBlock.DrawUseStatusColorsOption(_configService, "AppearanceUseStatusColors");
            }
        });

        DrawSettingsCard("User List Options", () =>
        {
            AppearanceOptionBlock.DrawSetVisiblePairsAsFocusTargetsOption(_configService, "AppearanceSetVisiblePairsAsFocusTargets");

            SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

            AppearanceOptionBlock.DrawShowVisibleUsersSeparatelyOption(_configService, _uiShared, Mediator, "AppearanceShowVisibleUsersSeparately");
            AppearanceOptionBlock.DrawShowVisibleSyncshellUsersOnlyInSyncshellsOption(_configService, _uiShared, Mediator, "AppearanceShowVisibleSyncshellUsersOnlyInSyncshells");
            AppearanceOptionBlock.DrawShowOfflineUsersSeparatelyOption(_configService, _uiShared, Mediator, "AppearanceShowOfflineUsersSeparately");
            AppearanceOptionBlock.DrawAlsoShowOfflineSyncshellUsersSeparatelyOption(_configService, _uiShared, Mediator, "AppearanceAlsoShowOfflineSyncshellUsersSeparately");
            SpheneUI.Layout.Space(SpheneUI.Spacing.X10);
            AppearanceOptionBlock.DrawGroupUpAllSyncshellsInOneFolderOption(_configService, _uiShared, Mediator, "AppearanceGroupUpAllSyncshellsInOneFolder");
        });

        DrawSettingsCard("Profile Settings", () =>
        {
            var showProfiles = AppearanceOptionBlock.DrawShowSpheneProfilesOption(_configService, _uiShared, Mediator, "AppearanceShowSpheneProfiles");
            ImGui.Indent();
            using (ImRaii.Disabled(!showProfiles))
            {
                AppearanceOptionBlock.DrawPopoutProfilesOnTheRightOption(_configService, _uiShared, Mediator, "AppearancePopoutProfilesOnTheRight");
            }
            ImGui.Unindent();
            AppearanceOptionBlock.DrawShowProfilesMarkedAsNsfwOption(_configService, _uiShared, Mediator, "AppearanceShowProfilesMarkedAsNsfw");
        });
    }

    private static string GetShrinkUAssemblyVersion()
    {
        try
        {
            var asm = typeof(ShrinkU.Plugin).Assembly;
            var v = asm?.GetName()?.Version;
            return v?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private void DrawGeneralNotifications()
    {
        DrawSettingsPageHeader("Notifications", "Configure where notifications appear and which sync events should trigger them.");

        DrawSettingsCard("Notification Channels", () =>
        {
            NotificationsOptionBlock.DrawInfoNotificationDisplayOption(_configService, _uiShared, "NotificationsInfoNotificationDisplay");
            NotificationsOptionBlock.DrawWarningNotificationDisplayOption(_configService, _uiShared, "NotificationsWarningNotificationDisplay");
            NotificationsOptionBlock.DrawErrorNotificationDisplayOption(_configService, _uiShared, "NotificationsErrorNotificationDisplay");
            NotificationsOptionBlock.DrawDisableOptionalPluginWarningsOption(_configService, _uiShared, "NotificationsDisableOptionalPluginWarnings");
            NotificationsOptionBlock.DrawEnableOnlineNotificationsOption(_configService, "NotificationsEnableOnlineNotifications");
            NotificationsOptionBlock.DrawOnlyForIndividualPairsOption(_configService, "NotificationsOnlyForIndividualPairs");
            NotificationsOptionBlock.DrawOnlyForNamedPairsOption(_configService, "NotificationsOnlyForNamedPairs");
            NotificationsOptionBlock.DrawShowTestBuildUpdatesOption(_configService, _uiShared, "NotificationsShowTestBuildUpdates");
        });

        DrawSettingsCard("Area-bound Syncshell Notifications", () =>
        {
            if (NotificationsOptionBlock.DrawEnableAreaBoundSyncshellNotificationsOption(_configService, _uiShared, "NotificationsEnableAreaBoundSyncshellNotifications"))
            {
                ImGui.Indent();
                NotificationsOptionBlock.DrawAreaBoundNotificationDisplayOption(_configService, _uiShared, "NotificationsAreaBoundNotificationDisplay");
                ImGui.Unindent();
            }
            NotificationsOptionBlock.DrawShowAreaBoundSyncshellWelcomeMessagesOption(_configService, _uiShared, "NotificationsShowAreaBoundSyncshellWelcomeMessages");
            NotificationsOptionBlock.DrawAutomaticallyShowAreaBoundSyncshellConsentOption(_configService, _uiShared, "NotificationsAutomaticallyShowAreaBoundSyncshellConsent");
        });
    }

    private void DrawAcknowledgmentSettings()
    {
        DrawSettingsPageHeader("Acknowledgment", "Tune acknowledgment notifications, reliability, and batching behavior.");

        DrawSettingsCard("Popup Settings", () =>
        {
            var showPopups = AcknowledgmentOptionBlock.DrawShowAcknowledgmentNotificationsOption(_configService, _uiShared, "AcknowledgmentShowAcknowledgmentNotifications");
            AcknowledgmentOptionBlock.DrawShowWaitingForAcknowledgmentPopupsOption(_configService, _uiShared, "AcknowledgmentShowWaitingForAcknowledgmentPopups");

            if (showPopups)
            {
                ImGui.Indent();
                AcknowledgmentOptionBlock.DrawAcknowledgmentNotificationLocationOption(_configService, _uiShared, "AcknowledgmentNotificationLocation");
                ImGui.Unindent();
            }
        });

        DrawSettingsCard("Performance Settings", () =>
        {
            AcknowledgmentOptionBlock.DrawEnableBatchingOption(_configService, _uiShared, "AcknowledgmentEnableBatching");
            AcknowledgmentOptionBlock.DrawEnableAutoRetryOption(_configService, _uiShared, "AcknowledgmentEnableAutoRetry");
        });

        DrawSettingsCard("Timeout & Reliability", () =>
        {
            AcknowledgmentOptionBlock.DrawAcknowledgmentTimeoutOption(_configService, _uiShared, "AcknowledgmentTimeout");
        });

        DrawSettingsCard("Information", () =>
        {
            SpheneUI.Text.Wrapped("The acknowledgment system helps maintain synchronization between connected users. " +
                              "When disabled, popup notifications will not appear, but the system will continue to function in the background. " +
                              "This is useful when you have many active connections to prevent notification spam.");

            SpheneUI.Layout.VerticalSpace();
            AcknowledgmentOptionBlock.DrawResetAcknowledgmentSettingsToDefaultsOption(_configService, _uiShared, "AcknowledgmentResetAcknowledgmentSettingsToDefaults");
        });
    }

    private void DrawSyncBehaviorSettings()
    {
        DrawSettingsPageHeader("Sync Behavior", "Control how incoming and outgoing synchronization data is handled.");

        DrawSettingsCard("Incoming Sync", () =>
        {
            SyncBehaviorOptionBlock.DrawIncomingSyncWithoutRedraw(_configService, _uiShared, "SettingsIncomingSyncWithoutRedraw");
        });

        DrawSettingsCard("Outgoing Sync Batching", () =>
        {
            SyncBehaviorOptionBlock.DrawOutgoingSyncBatching(_configService, _uiShared, "SettingsOutgoingSyncBatching");
        });

        DrawSettingsCard("Filter", () =>
        {
            SyncBehaviorOptionBlock.DrawFilterCharacterLegacyShpkInOutgoingCharacterData(_configService, _uiShared, Mediator, "SettingsFilterCharacterLegacyShpk");
        });
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

    private void DrawThemeSettings()
    {
        DrawSettingsPageHeader("Theme", "Customize colors and styling for Sphene UI elements.");

        DrawSettingsCard("Themes", DrawThemeSelector,
            "Select from built-in themes or your custom saved themes. Changes apply immediately.");

        DrawSettingsCard("Theme Editor", () =>
        {
            var tabLabels = new List<string>(4);
            if (ApiController.IsAdmin)
            {
                tabLabels.Add("General Theme");
            }
            tabLabels.Add("Icon Theme");
            tabLabels.Add("Panel Theme");
            tabLabels.Add("Sounds");

            var preferredIndex = _preferThemeTab != null
                ? tabLabels.IndexOf(_preferThemeTab)
                : -1;
            _preferThemeTab = null;
            var selectedTab = SpheneUI.Layout.TabStrip("ThemeEditorTabs", tabLabels, preferredIndex);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

            var tabPadding = new Vector2(12f, 10f);
            if (SpheneUI.Layout.BeginFillToBottomScrollArea($"ThemeTabContent##{selectedTab}", contentPadding: tabPadding))
            {
                using var controlStyle = SpheneUI.Controls.PushValueControlStyle();
                switch (tabLabels[selectedTab])
                {
                    case "General Theme": DrawGeneralThemeSettings(); break;
                    case "Icon Theme": DrawIconThemeSettings(); break;
                    case "Panel Theme": DrawCompactUIThemeSettings(); break;
                    case "Sounds": DrawSoundThemeSettings(); break;
                }
            }
            SpheneUI.Layout.EndScrollArea();
        });
    }

    private void DrawGeneralThemeSettings()
    {
        var theme = SpheneTheme.Current;
        var themeChanged = false;

        int navClicked;
        using (ImRaii.Group())
        {
            navClicked = SpheneUI.Layout.VerticalTabNav("GeneralThemeNav",
                GeneralThemeNavLabels, _generalSectionActive);
        }
        if (navClicked >= 0)
        {
            _generalScrollRequest = navClicked;
            _generalSectionActive = navClicked;
        }
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X + 6f * ImGuiHelpers.GlobalScale);
        // The enclosing ThemeTabContent scroll area appends its own
        // ScrollAreaContentInset spacer after this child — without reserving
        // for it the parent gains a sliver of scroll range and the wheel
        // chaining pushes the whole nav column up when scrolling to bottom.
        var sectionsBottomReserve = ImGui.GetStyle().ItemSpacing.Y
            + SpheneUI.Layout.ScrollAreaContentInset * ImGuiHelpers.GlobalScale;
        if (SpheneUI.Layout.BeginFillToBottomScrollArea("GeneralThemeSections",
            sectionsBottomReserve, border: false, contentPadding: Vector2.Zero))
        {
            DrawGeneralThemeSections(theme, ref themeChanged);
        }
        SpheneUI.Layout.EndScrollArea();

        if (themeChanged)
        {
            theme.NotifyThemeChanged();
            _hasUnsavedThemeChanges = true;
        }
    }

    private static void DrawRoundingSection(ThemeConfiguration theme, ref bool themeChanged)
    {
        var windowRounding = theme.WindowRounding;
        if (SpheneUI.Controls.SliderFloat("Window Rounding", ref windowRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.WindowRounding = windowRounding;
            themeChanged = true;
        }
        
        var childRounding = theme.ChildRounding;
        if (SpheneUI.Controls.SliderFloat("Child Rounding", ref childRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.ChildRounding = childRounding;
            themeChanged = true;
        }
        
        var popupRounding = theme.PopupRounding;
        if (SpheneUI.Controls.SliderFloat("Popup Rounding", ref popupRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.PopupRounding = popupRounding;
            themeChanged = true;
        }
        
        var frameRounding = theme.FrameRounding;
        if (SpheneUI.Controls.SliderFloat("Frame Rounding", ref frameRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.FrameRounding = frameRounding;
            themeChanged = true;
        }
        
        var scrollbarRounding = theme.ScrollbarRounding;
        if (SpheneUI.Controls.SliderFloat("Scrollbar Rounding", ref scrollbarRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.ScrollbarRounding = scrollbarRounding;
            themeChanged = true;
        }
        
        var grabRounding = theme.GrabRounding;
        if (SpheneUI.Controls.SliderFloat("Grab Rounding", ref grabRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.GrabRounding = grabRounding;
            themeChanged = true;
        }
        
        var tabRounding = theme.TabRounding;
        if (SpheneUI.Controls.SliderFloat("Tab Rounding", ref tabRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.TabRounding = tabRounding;
            themeChanged = true;
        }
    }

    private static void DrawBorderSection(ThemeConfiguration theme, ref bool themeChanged)
    {
        var windowBorderSize = theme.WindowBorderSize;
        if (SpheneUI.Controls.SliderFloat("Window Border", ref windowBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.WindowBorderSize = windowBorderSize;
            themeChanged = true;
        }
        
        var childBorderSize = theme.ChildBorderSize;
        if (SpheneUI.Controls.SliderFloat("Child Border", ref childBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.ChildBorderSize = childBorderSize;
            themeChanged = true;
        }
        
        var popupBorderSize = theme.PopupBorderSize;
        if (SpheneUI.Controls.SliderFloat("Popup Border", ref popupBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.PopupBorderSize = popupBorderSize;
            themeChanged = true;
        }
        
        var frameBorderSize = theme.FrameBorderSize;
        if (SpheneUI.Controls.SliderFloat("Frame Border", ref frameBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.FrameBorderSize = frameBorderSize;
            themeChanged = true;
        }
    }

    private static void DrawWindowEffectsSection(ThemeConfiguration theme, ref bool themeChanged)
    {
        var enableWindowBlur = theme.EnableWindowBlur;
        if (SpheneUI.Controls.Toggle("Background Blur", ref enableWindowBlur))
        {
            theme.EnableWindowBlur = enableWindowBlur;
            themeChanged = true;
        }

        var windowBlurStrength = theme.WindowBlurStrength * 100f;
        using (ImRaii.Disabled(!theme.EnableWindowBlur))
        {
            if (SpheneUI.Controls.SliderFloat("Blur Strength", ref windowBlurStrength, 0.0f, 100.0f, "%.0f%%"))
            {
                theme.WindowBlurStrength = windowBlurStrength / 100f;
                themeChanged = true;
            }
        }
        SpheneUI.Text.ColoredWrapped("Blurs the game and windows behind Sphene windows (main game window only). Lower the Window Background alpha for a stronger glass effect.", ImGuiColors.DalamudGrey);
    }

    private static void DrawSpacingSection(ThemeConfiguration theme, ref bool themeChanged)
    {
        var windowPadding = theme.WindowPadding;
        if (SpheneUI.Controls.SliderFloat2("Window Padding", ref windowPadding, 0.0f, 50.0f, "%.1f"))
        {
            theme.WindowPadding = windowPadding;
            themeChanged = true;
        }
        
        var framePadding = theme.FramePadding;
        if (SpheneUI.Controls.SliderFloat2("Frame Padding", ref framePadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.FramePadding = framePadding;
            themeChanged = true;
        }
        
        var itemSpacing = theme.ItemSpacing;
        if (SpheneUI.Controls.SliderFloat2("Item Spacing", ref itemSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.ItemSpacing = itemSpacing;
            themeChanged = true;
        }
        
        var itemInnerSpacing = theme.ItemInnerSpacing;
        if (SpheneUI.Controls.SliderFloat2("Item Inner Spacing", ref itemInnerSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.ItemInnerSpacing = itemInnerSpacing;
            themeChanged = true;
        }
        
        var indentSpacing = theme.IndentSpacing;
        if (SpheneUI.Controls.SliderFloat("Indent Spacing", ref indentSpacing, 0.0f, 50.0f, "%.1f"))
        {
            theme.IndentSpacing = indentSpacing;
            themeChanged = true;
        }
        
        var scrollbarSize = theme.ScrollbarSize;
        if (SpheneUI.Controls.SliderFloat("Scrollbar Size", ref scrollbarSize, 5.0f, 30.0f, "%.1f"))
        {
            theme.ScrollbarSize = scrollbarSize;
            themeChanged = true;
        }
        
        var grabMinSize = theme.GrabMinSize;
        if (SpheneUI.Controls.SliderFloat("Grab Min Size", ref grabMinSize, 5.0f, 30.0f, "%.1f"))
        {
            theme.GrabMinSize = grabMinSize;
            themeChanged = true;
        }
    }

    /// <summary>
    /// Draws all general theme sections stacked inside the sections scroll
    /// child and keeps the left nav in sync: a queued nav click eases the
    /// scroll toward that section (wheel input cancels), otherwise the scroll
    /// position picks the highlighted nav entry. Section tops are recorded
    /// relative to the first section so any padding cancels out.
    /// </summary>
    private void DrawGeneralThemeSections(ThemeConfiguration theme, ref bool themeChanged)
    {
        if (_generalScrollRequest >= 0 && _generalScrollRequest < _generalSectionTops.Length)
        {
            _generalScrollTarget = MathF.Max(0f,
                _generalSectionTops[_generalScrollRequest] - _generalSectionTops[0]);
        }
        _generalScrollRequest = -1;

        if (_generalScrollTarget.HasValue)
        {
            var target = _generalScrollTarget.Value;
            var io = ImGui.GetIO();
            if (ImGui.IsWindowHovered() && MathF.Abs(io.MouseWheel) > 0f)
            {
                _generalScrollTarget = null;
            }
            else
            {
                var next = ImGui.GetScrollY()
                    + (target - ImGui.GetScrollY()) * MathF.Min(1f, io.DeltaTime * 12f);
                if (MathF.Abs(next - target) < 1f)
                {
                    ImGui.SetScrollY(target);
                    _generalScrollTarget = null;
                }
                else
                {
                    ImGui.SetScrollY(next);
                }
            }
        }

        for (var i = 0; i < GeneralThemeSectionTitles.Length; i++)
        {
            if (i > 0)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            }
            _generalSectionTops[i] = ImGui.GetCursorPos().Y;
            SpheneUI.Text.Plain(GeneralThemeSectionTitles[i]);
            SpheneUI.Layout.Separator();
            switch (i)
            {
                case 0: DrawRoundingSection(theme, ref themeChanged); break;
                case 1: DrawBorderSection(theme, ref themeChanged); break;
                case 2: DrawWindowEffectsSection(theme, ref themeChanged); break;
                case 3: DrawSpacingSection(theme, ref themeChanged); break;
                case 4: DrawBasicColors(theme, ref themeChanged); break;
                case 5: DrawWindowColors(theme, ref themeChanged); break;
                case 6: DrawFrameInputColors(theme, ref themeChanged); break;
                case 7: DrawButtonHeaderColors(theme, ref themeChanged); break;
                case 8: DrawMenuNavigationColors(theme, ref themeChanged); break;
                case 9: DrawScrollbarSliderColors(theme, ref themeChanged); break;
                case 10: DrawTableTabColors(theme, ref themeChanged); break;
            }
        }

        if (!_generalScrollTarget.HasValue)
        {
            var scrollY = ImGui.GetScrollY();
            var threshold = ImGui.GetTextLineHeightWithSpacing();
            var active = 0;
            for (var i = 1; i < _generalSectionTops.Length; i++)
            {
                if (_generalSectionTops[i] - _generalSectionTops[0] - threshold <= scrollY)
                {
                    active = i;
                }
            }
            _generalSectionActive = active;
        }
    }


    private void DrawIconThemeSettings()
    {
        var cfg = _configService.Current;
        bool configChanged = false;

        SpheneUI.Text.ColoredWrapped("Customize the Sphene floating icon pulse colors, ring sizes, and which notifications appear as badges.", ImGuiColors.DalamudGrey);
        SpheneUI.Text.ColoredWrapped("Changes apply immediately to the icon. Use the test buttons below to preview each effect.", ImGuiColors.DalamudYellow);
        SpheneUI.Layout.VerticalSpace();

        // Helper to edit packed uint color as Vector4
        (bool Changed, uint NewValue) EditPackedColor(string label, uint packedColor)
        {
            var vec = ImGui.ColorConvertU32ToFloat4(packedColor);
            if (SpheneUI.Controls.ColorEdit(label, ref vec))
            {
                return (true, ImGui.ColorConvertFloat4ToU32(vec));
            }
            return (false, packedColor);
        }

        SpheneUI.Text.Plain("Global Visual Settings");
        SpheneUI.Layout.Separator();

        var iconGlobalAlpha = cfg.IconGlobalAlpha;
        if (SpheneUI.Controls.SliderFloat("Icon Global Opacity", ref iconGlobalAlpha, 0.1f, 1.0f, "%.2f"))
        {
            cfg.IconGlobalAlpha = iconGlobalAlpha;
            configChanged = true;
        }

        var rainbowSpeed = cfg.IconRainbowSpeed;
        if (SpheneUI.Controls.SliderFloat("Rainbow Cycle Speed", ref rainbowSpeed, 0.1f, 5.0f, "%.2f"))
        {
            cfg.IconRainbowSpeed = rainbowSpeed;
            configChanged = true;
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Badge Visibility");
        SpheneUI.Layout.Separator();

        var showMod = cfg.IconShowModTransferBadge;
        if (SpheneUI.Controls.Toggle("Show Mod Transfer Badges", ref showMod))
        {
            cfg.IconShowModTransferBadge = showMod;
            configChanged = true;
        }

        var showPair = cfg.IconShowPairRequestBadge;
        if (SpheneUI.Controls.Toggle("Show Pair Request Badges", ref showPair))
        {
            cfg.IconShowPairRequestBadge = showPair;
            configChanged = true;
        }

        var showNotif = cfg.IconShowNotificationBadge;
        if (SpheneUI.Controls.Toggle("Show Notification Badges", ref showNotif))
        {
            cfg.IconShowNotificationBadge = showNotif;
            configChanged = true;
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Per-Event Configuration");
        SpheneUI.Layout.Separator();
        SpheneUI.Text.Wrapped("Each event type can have its own color, alpha, and effects. Multiple effects can be combined.");

        // Permanent (Background)
        if (SpheneUI.Controls.CollapsibleSection("Permanent (Background)"))
        {
            var (permColorChanged, permColorNew) = EditPackedColor("Color##perm", cfg.IconPermColor);
            if (permColorChanged) { cfg.IconPermColor = permColorNew; configChanged = true; }
            var permAlpha = cfg.IconPermAlpha;
            if (SpheneUI.Controls.SliderFloat("Alpha##perm", ref permAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconPermAlpha = permAlpha; configChanged = true; }
            var permPulse = cfg.IconPermEffectPulse;
            if (SpheneUI.Controls.ToggleChip("Pulse##perm", ref permPulse)) { cfg.IconPermEffectPulse = permPulse; configChanged = true; }
            ImGui.SameLine();
            var permGlow = cfg.IconPermEffectGlow;
            if (SpheneUI.Controls.ToggleChip("Glow##perm", ref permGlow)) { cfg.IconPermEffectGlow = permGlow; configChanged = true; }
            ImGui.SameLine();
            var permBounce = cfg.IconPermEffectBounce;
            if (SpheneUI.Controls.ToggleChip("Bounce##perm", ref permBounce)) { cfg.IconPermEffectBounce = permBounce; configChanged = true; }
            ImGui.SameLine();
            var permRainbow = cfg.IconPermEffectRainbow;
            if (SpheneUI.Controls.ToggleChip("Rainbow##perm", ref permRainbow)) { cfg.IconPermEffectRainbow = permRainbow; configChanged = true; }
            var permPulseMin = cfg.IconPermPulseMinRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Min Radius##perm", ref permPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconPermPulseMinRadius = permPulseMin; configChanged = true; }
            var permPulseMax = cfg.IconPermPulseMaxRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Max Radius##perm", ref permPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconPermPulseMaxRadius = permPulseMax; configChanged = true; }
            var permGlowInt = cfg.IconPermGlowIntensity;
            if (SpheneUI.Controls.SliderFloat("Glow Intensity##perm", ref permGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconPermGlowIntensity = permGlowInt; configChanged = true; }
            var permGlowRad = cfg.IconPermGlowRadius;
            if (SpheneUI.Controls.SliderFloat("Glow Radius##perm", ref permGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconPermGlowRadius = permGlowRad; configChanged = true; }
            var permBounceInt = cfg.IconPermBounceIntensity;
            if (SpheneUI.Controls.SliderFloat("Bounce Scale##perm", ref permBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconPermBounceIntensity = permBounceInt; configChanged = true; }
            var permBounceSpd = cfg.IconPermBounceSpeed;
            if (SpheneUI.Controls.SliderFloat("Bounce Speed##perm", ref permBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconPermBounceSpeed = permBounceSpd; configChanged = true; }
        }

        // Mod Transfer
        if (SpheneUI.Controls.CollapsibleSection("Mod Transfer"))
        {
            var (modColorChanged, modColorNew) = EditPackedColor("Color##mod", cfg.IconModTransferColor);
            if (modColorChanged) { cfg.IconModTransferColor = modColorNew; configChanged = true; }
            var modAlpha = cfg.IconModTransferAlpha;
            if (SpheneUI.Controls.SliderFloat("Alpha##mod", ref modAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconModTransferAlpha = modAlpha; configChanged = true; }
            var modPulse = cfg.IconModTransferEffectPulse;
            if (SpheneUI.Controls.ToggleChip("Pulse##mod", ref modPulse)) { cfg.IconModTransferEffectPulse = modPulse; configChanged = true; }
            ImGui.SameLine();
            var modGlow = cfg.IconModTransferEffectGlow;
            if (SpheneUI.Controls.ToggleChip("Glow##mod", ref modGlow)) { cfg.IconModTransferEffectGlow = modGlow; configChanged = true; }
            ImGui.SameLine();
            var modBounce = cfg.IconModTransferEffectBounce;
            if (SpheneUI.Controls.ToggleChip("Bounce##mod", ref modBounce)) { cfg.IconModTransferEffectBounce = modBounce; configChanged = true; }
            ImGui.SameLine();
            var modRainbow = cfg.IconModTransferEffectRainbow;
            if (SpheneUI.Controls.ToggleChip("Rainbow##mod", ref modRainbow)) { cfg.IconModTransferEffectRainbow = modRainbow; configChanged = true; }
            var modPulseMin = cfg.IconModTransferPulseMinRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Min Radius##mod", ref modPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconModTransferPulseMinRadius = modPulseMin; configChanged = true; }
            var modPulseMax = cfg.IconModTransferPulseMaxRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Max Radius##mod", ref modPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconModTransferPulseMaxRadius = modPulseMax; configChanged = true; }
            var modGlowInt = cfg.IconModTransferGlowIntensity;
            if (SpheneUI.Controls.SliderFloat("Glow Intensity##mod", ref modGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconModTransferGlowIntensity = modGlowInt; configChanged = true; }
            var modGlowRad = cfg.IconModTransferGlowRadius;
            if (SpheneUI.Controls.SliderFloat("Glow Radius##mod", ref modGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconModTransferGlowRadius = modGlowRad; configChanged = true; }
            var modBounceInt = cfg.IconModTransferBounceIntensity;
            if (SpheneUI.Controls.SliderFloat("Bounce Scale##mod", ref modBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconModTransferBounceIntensity = modBounceInt; configChanged = true; }
            var modBounceSpd = cfg.IconModTransferBounceSpeed;
            if (SpheneUI.Controls.SliderFloat("Bounce Speed##mod", ref modBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconModTransferBounceSpeed = modBounceSpd; configChanged = true; }
            SpheneUI.Layout.VerticalSpace();
            var modEffectDuration = cfg.IconModTransferEffectDurationSeconds;
            if (SpheneUI.Controls.SliderInt("Effect Duration##mod", ref modEffectDuration, 0, 300, modEffectDuration == 0 ? "Never" : "%ds")) { cfg.IconModTransferEffectDurationSeconds = modEffectDuration; configChanged = true; }
            SpheneUI.Controls.HelpText("How long the pulse/glow/bounce effects last. 0 = never expires until icon is clicked.");
            var modBadgeDuration = cfg.IconModTransferBadgeDurationSeconds;
            if (SpheneUI.Controls.SliderInt("Badge Duration##mod", ref modBadgeDuration, 0, 300, modBadgeDuration == 0 ? "Never" : "%ds")) { cfg.IconModTransferBadgeDurationSeconds = modBadgeDuration; configChanged = true; }
            SpheneUI.Controls.HelpText("How long the badge dot remains visible. 0 = never expires until icon is clicked. Separate from effect duration.");
        }

        // Pair Request
        if (SpheneUI.Controls.CollapsibleSection("Pair Request"))
        {
            var (pairColorChanged, pairColorNew) = EditPackedColor("Color##pair", cfg.IconPairRequestColor);
            if (pairColorChanged) { cfg.IconPairRequestColor = pairColorNew; configChanged = true; }
            var pairAlpha = cfg.IconPairRequestAlpha;
            if (SpheneUI.Controls.SliderFloat("Alpha##pair", ref pairAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconPairRequestAlpha = pairAlpha; configChanged = true; }
            var pairPulse = cfg.IconPairRequestEffectPulse;
            if (SpheneUI.Controls.ToggleChip("Pulse##pair", ref pairPulse)) { cfg.IconPairRequestEffectPulse = pairPulse; configChanged = true; }
            ImGui.SameLine();
            var pairGlow = cfg.IconPairRequestEffectGlow;
            if (SpheneUI.Controls.ToggleChip("Glow##pair", ref pairGlow)) { cfg.IconPairRequestEffectGlow = pairGlow; configChanged = true; }
            ImGui.SameLine();
            var pairBounce = cfg.IconPairRequestEffectBounce;
            if (SpheneUI.Controls.ToggleChip("Bounce##pair", ref pairBounce)) { cfg.IconPairRequestEffectBounce = pairBounce; configChanged = true; }
            ImGui.SameLine();
            var pairRainbow = cfg.IconPairRequestEffectRainbow;
            if (SpheneUI.Controls.ToggleChip("Rainbow##pair", ref pairRainbow)) { cfg.IconPairRequestEffectRainbow = pairRainbow; configChanged = true; }
            var pairPulseMin = cfg.IconPairRequestPulseMinRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Min Radius##pair", ref pairPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconPairRequestPulseMinRadius = pairPulseMin; configChanged = true; }
            var pairPulseMax = cfg.IconPairRequestPulseMaxRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Max Radius##pair", ref pairPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconPairRequestPulseMaxRadius = pairPulseMax; configChanged = true; }
            var pairGlowInt = cfg.IconPairRequestGlowIntensity;
            if (SpheneUI.Controls.SliderFloat("Glow Intensity##pair", ref pairGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconPairRequestGlowIntensity = pairGlowInt; configChanged = true; }
            var pairGlowRad = cfg.IconPairRequestGlowRadius;
            if (SpheneUI.Controls.SliderFloat("Glow Radius##pair", ref pairGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconPairRequestGlowRadius = pairGlowRad; configChanged = true; }
            var pairBounceInt = cfg.IconPairRequestBounceIntensity;
            if (SpheneUI.Controls.SliderFloat("Bounce Scale##pair", ref pairBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconPairRequestBounceIntensity = pairBounceInt; configChanged = true; }
            var pairBounceSpd = cfg.IconPairRequestBounceSpeed;
            if (SpheneUI.Controls.SliderFloat("Bounce Speed##pair", ref pairBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconPairRequestBounceSpeed = pairBounceSpd; configChanged = true; }
            SpheneUI.Layout.VerticalSpace();
            var pairEffectDuration = cfg.IconPairRequestEffectDurationSeconds;
            if (SpheneUI.Controls.SliderInt("Effect Duration##pair", ref pairEffectDuration, 0, 300, pairEffectDuration == 0 ? "Never" : "%ds")) { cfg.IconPairRequestEffectDurationSeconds = pairEffectDuration; configChanged = true; }
            SpheneUI.Controls.HelpText("How long the pulse/glow/bounce effects last. 0 = never expires until icon is clicked.");
            var pairBadgeDuration = cfg.IconPairRequestBadgeDurationSeconds;
            if (SpheneUI.Controls.SliderInt("Badge Duration##pair", ref pairBadgeDuration, 0, 300, pairBadgeDuration == 0 ? "Never" : "%ds")) { cfg.IconPairRequestBadgeDurationSeconds = pairBadgeDuration; configChanged = true; }
            SpheneUI.Controls.HelpText("How long the badge dot remains visible. 0 = never expires until icon is clicked. Separate from effect duration.");
        }

        // Notification
        if (SpheneUI.Controls.CollapsibleSection("Notification"))
        {
            var (notifColorChanged, notifColorNew) = EditPackedColor("Color##notif", cfg.IconNotificationColor);
            if (notifColorChanged) { cfg.IconNotificationColor = notifColorNew; configChanged = true; }
            var notifAlpha = cfg.IconNotificationAlpha;
            if (SpheneUI.Controls.SliderFloat("Alpha##notif", ref notifAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconNotificationAlpha = notifAlpha; configChanged = true; }
            var notifPulse = cfg.IconNotificationEffectPulse;
            if (SpheneUI.Controls.ToggleChip("Pulse##notif", ref notifPulse)) { cfg.IconNotificationEffectPulse = notifPulse; configChanged = true; }
            ImGui.SameLine();
            var notifGlow = cfg.IconNotificationEffectGlow;
            if (SpheneUI.Controls.ToggleChip("Glow##notif", ref notifGlow)) { cfg.IconNotificationEffectGlow = notifGlow; configChanged = true; }
            ImGui.SameLine();
            var notifBounce = cfg.IconNotificationEffectBounce;
            if (SpheneUI.Controls.ToggleChip("Bounce##notif", ref notifBounce)) { cfg.IconNotificationEffectBounce = notifBounce; configChanged = true; }
            ImGui.SameLine();
            var notifRainbow = cfg.IconNotificationEffectRainbow;
            if (SpheneUI.Controls.ToggleChip("Rainbow##notif", ref notifRainbow)) { cfg.IconNotificationEffectRainbow = notifRainbow; configChanged = true; }
            var notifPulseMin = cfg.IconNotificationPulseMinRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Min Radius##notif", ref notifPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconNotificationPulseMinRadius = notifPulseMin; configChanged = true; }
            var notifPulseMax = cfg.IconNotificationPulseMaxRadius;
            if (SpheneUI.Controls.SliderFloat("Pulse Max Radius##notif", ref notifPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconNotificationPulseMaxRadius = notifPulseMax; configChanged = true; }
            var notifGlowInt = cfg.IconNotificationGlowIntensity;
            if (SpheneUI.Controls.SliderFloat("Glow Intensity##notif", ref notifGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconNotificationGlowIntensity = notifGlowInt; configChanged = true; }
            var notifGlowRad = cfg.IconNotificationGlowRadius;
            if (SpheneUI.Controls.SliderFloat("Glow Radius##notif", ref notifGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconNotificationGlowRadius = notifGlowRad; configChanged = true; }
            var notifBounceInt = cfg.IconNotificationBounceIntensity;
            if (SpheneUI.Controls.SliderFloat("Bounce Scale##notif", ref notifBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconNotificationBounceIntensity = notifBounceInt; configChanged = true; }
            var notifBounceSpd = cfg.IconNotificationBounceSpeed;
            if (SpheneUI.Controls.SliderFloat("Bounce Speed##notif", ref notifBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconNotificationBounceSpeed = notifBounceSpd; configChanged = true; }
            SpheneUI.Layout.VerticalSpace();
            var notifEffectDuration = cfg.IconNotificationEffectDurationSeconds;
            if (SpheneUI.Controls.SliderInt("Effect Duration##notif", ref notifEffectDuration, 0, 300, notifEffectDuration == 0 ? "Never" : "%ds")) { cfg.IconNotificationEffectDurationSeconds = notifEffectDuration; configChanged = true; }
            SpheneUI.Controls.HelpText("How long the pulse/glow/bounce effects last. 0 = never expires until icon is clicked.");
            var notifBadgeDuration = cfg.IconNotificationBadgeDurationSeconds;
            if (SpheneUI.Controls.SliderInt("Badge Duration##notif", ref notifBadgeDuration, 0, 300, notifBadgeDuration == 0 ? "Never" : "%ds")) { cfg.IconNotificationBadgeDurationSeconds = notifBadgeDuration; configChanged = true; }
            SpheneUI.Controls.HelpText("How long the badge dot remains visible. 0 = never expires until icon is clicked. Separate from effect duration.");
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Live Preview (Test Buttons)");
        SpheneUI.Layout.Separator();

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        if (SpheneUI.Controls.Button("Test Mod Transfer"))
        {
            Mediator.Publish(new TestIconEventMessage(TestIconEventType.ModTransferAvailable, "Test Mod Transfer"));
        }
        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Test Pair Request"))
        {
            Mediator.Publish(new TestIconEventMessage(TestIconEventType.PairRequest, "Test Pair Request"));
        }
        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Test Notification"))
        {
            Mediator.Publish(new TestIconEventMessage(TestIconEventType.Notification, "Test Notification"));
        }

        if (configChanged)
        {
            _configService.Save();
        }
    }

    private void DrawCompactUIThemeSettings()
    {
        var theme = SpheneTheme.Current;
        bool themeChanged = false;


        SpheneUI.Text.ColoredWrapped("Customize how the Panel looks and feels.", ImGuiColors.DalamudGrey);
        SpheneUI.Text.ColoredWrapped("Panel will be opened automaticly and you can see changes immediately.", ImGuiColors.DalamudYellow);
        SpheneUI.Layout.VerticalSpace();
        
        
        SpheneUI.Layout.VerticalSpace();
                
        if (SpheneUI.Controls.CollapsibleSection("Control Panel Layout"))
        {
                
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Panel Spacing & Sizing");
        SpheneUI.Layout.Separator();
        
        // Panel Spacing Settings
        
        var compactItemSpacing = theme.CompactItemSpacing;
        if (SpheneUI.Controls.SliderFloat2("Panel Item Spacing", ref compactItemSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactItemSpacing = compactItemSpacing;
            themeChanged = true;
        }
        
        
        var compactWindowPadding = theme.CompactWindowPadding;
        if (SpheneUI.Controls.SliderFloat2("Panel Padding", ref compactWindowPadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactWindowPadding = compactWindowPadding;
            themeChanged = true;
        }

        var compactPairListSeparatorPadding = theme.CompactPairListSeparatorPadding;
        if (SpheneUI.Controls.SliderFloat("Pair List Separator Padding", ref compactPairListSeparatorPadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactPairListSeparatorPadding = compactPairListSeparatorPadding;
            themeChanged = true;
        }

        var compactPairListContentPadding = theme.CompactPairListContentPadding;
        if (SpheneUI.Controls.SliderFloat("Pair List Content Padding", ref compactPairListContentPadding, 0.0f, 24.0f, "%.1f"))
        {
            theme.CompactPairListContentPadding = compactPairListContentPadding;
            themeChanged = true;
        }

        var compactPairRowContentPadding = theme.CompactPairRowContentPadding;
        if (SpheneUI.Controls.SliderFloat("Pair Row Content Padding", ref compactPairRowContentPadding, 0.0f, 16.0f, "%.1f"))
        {
            theme.CompactPairRowContentPadding = compactPairRowContentPadding;
            themeChanged = true;
        }

        var compactFramePadding = theme.CompactFramePadding;
        if (SpheneUI.Controls.SliderFloat2("Control Inner Padding", ref compactFramePadding, 0.0f, 16.0f, "%.1f"))
        {
            theme.CompactFramePadding = compactFramePadding;
            themeChanged = true;
        }

        var compactItemInnerSpacing = theme.CompactItemInnerSpacing;
        if (SpheneUI.Controls.SliderFloat2("Inner Item Spacing", ref compactItemInnerSpacing, 0.0f, 16.0f, "%.1f"))
        {
            theme.CompactItemInnerSpacing = compactItemInnerSpacing;
            themeChanged = true;
        }

        var compactScrollbarSize = theme.CompactScrollbarSize;
        if (SpheneUI.Controls.SliderFloat("Scrollbar Width", ref compactScrollbarSize, 6.0f, 24.0f, "%.1f"))
        {
            theme.CompactScrollbarSize = compactScrollbarSize;
            themeChanged = true;
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Panel Border and Rounding");
        SpheneUI.Layout.Separator();

        var compactWindowBorderSize = theme.CompactWindowBorderSize;
        if (SpheneUI.Controls.SliderFloat("Panel Window Border Size", ref compactWindowBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.CompactWindowBorderSize = compactWindowBorderSize;
            themeChanged = true;
        }

        var compactWindowRounding = theme.CompactWindowRounding;
        if (SpheneUI.Controls.SliderFloat("Corner Rounding", ref compactWindowRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactWindowRounding = compactWindowRounding;
            themeChanged = true;
        }

        var compactChildRounding = theme.CompactChildRounding;
        if (SpheneUI.Controls.SliderFloat("Child Surface Rounding", ref compactChildRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactChildRounding = compactChildRounding;
            themeChanged = true;
        }

        var compactPairRowRounding = theme.CompactPairRowRounding;
        if (SpheneUI.Controls.SliderFloat("Pair Row Rounding", ref compactPairRowRounding, -1.0f, 30.0f, "%.1f"))
        {
            theme.CompactPairRowRounding = compactPairRowRounding;
            themeChanged = true;
        }
        SpheneUI.AttachTooltip("Corner radius of pair list rows. -1 follows Child Surface Rounding.");

        var compactChildBorderSize = theme.CompactChildBorderSize;
        if (SpheneUI.Controls.SliderFloat("Child Surface Border Size", ref compactChildBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.CompactChildBorderSize = compactChildBorderSize;
            themeChanged = true;
        }

        var compactScrollbarRounding = theme.CompactScrollbarRounding;
        if (SpheneUI.Controls.SliderFloat("Scrollbar Rounding", ref compactScrollbarRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactScrollbarRounding = compactScrollbarRounding;
            themeChanged = true;
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Panel Window Effects");
        SpheneUI.Layout.Separator();

        var compactEnableWindowBlur = theme.CompactEnableWindowBlur;
        if (SpheneUI.Controls.Toggle("Panel Background Blur", ref compactEnableWindowBlur))
        {
            theme.CompactEnableWindowBlur = compactEnableWindowBlur;
            themeChanged = true;
        }

        var compactWindowBlurStrength = theme.CompactWindowBlurStrength * 100f;
        using (ImRaii.Disabled(!theme.CompactEnableWindowBlur))
        {
            if (SpheneUI.Controls.SliderFloat("Panel Blur Strength", ref compactWindowBlurStrength, 0.0f, 100.0f, "%.0f%%"))
            {
                theme.CompactWindowBlurStrength = compactWindowBlurStrength / 100f;
                themeChanged = true;
            }
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Panel Backgrounds");
        SpheneUI.Layout.Separator();
        var compactWindowBg = theme.CompactWindowBg;
        if (SpheneUI.Controls.ColorEdit("Panel Background", ref compactWindowBg))
        {
            theme.CompactWindowBg = compactWindowBg;
            themeChanged = true;
        }
        var compactFrameBg = theme.CompactFrameBg;
        if (SpheneUI.Controls.ColorEdit("Control Background", ref compactFrameBg))
        {
            theme.CompactFrameBg = compactFrameBg;
            themeChanged = true;
        }
        var compactControlPanelBg = theme.CompactControlPanelBg;
        if (SpheneUI.Controls.ColorEdit("Control Panel Account Section", ref compactControlPanelBg))
        {
            theme.CompactControlPanelBg = compactControlPanelBg;
            themeChanged = true;
        }
        var compactChildBg = theme.CompactChildBg;
        if (SpheneUI.Controls.ColorEdit("Child Surface Background", ref compactChildBg))
        {
            theme.CompactChildBg = compactChildBg;
            themeChanged = true;
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("General Text");
        SpheneUI.Layout.Separator();
        var compactText = theme.CompactText;
        if (SpheneUI.Controls.ColorEdit("Text", ref compactText))
        {
            theme.CompactText = compactText;
            themeChanged = true;
        }
        var compactTextSecondary = theme.CompactTextSecondary;
        if (SpheneUI.Controls.ColorEdit("Text (Secondary)", ref compactTextSecondary))
        {
            theme.CompactTextSecondary = compactTextSecondary;
            themeChanged = true;
        }

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Accents & Borders");
        SpheneUI.Layout.Separator();
        var compactBorder = theme.CompactBorder;
        if (SpheneUI.Controls.ColorEdit("Border", ref compactBorder))
        {
            theme.CompactBorder = compactBorder;
            themeChanged = true;
        }
        var separator = theme.CompactSeparator;
        if (SpheneUI.Controls.ColorEdit("Separator", ref separator))
        {
            theme.CompactSeparator = separator;
            themeChanged = true;
        }
        var compactAccent = theme.CompactAccent;
        if (SpheneUI.Controls.ColorEdit("Accent", ref compactAccent))
        {
            theme.CompactAccent = compactAccent;
            themeChanged = true;
        }
        var compactHover = theme.CompactHover;
        if (SpheneUI.Controls.ColorEdit("Hover Highlight", ref compactHover))
        {
            theme.CompactHover = compactHover;
            themeChanged = true;
        }
        var compactPairRowHover = theme.CompactPairRowHover;
        if (SpheneUI.Controls.ColorEdit("Pair Row Hover", ref compactPairRowHover))
        {
            theme.CompactPairRowHover = compactPairRowHover;
            themeChanged = true;
        }
        var compactActive = theme.CompactActive;
        if (SpheneUI.Controls.ColorEdit("Active Highlight", ref compactActive))
        {
            theme.CompactActive = compactActive;
            themeChanged = true;
        }

        }

        SpheneUI.Layout.VerticalSpace();
        if (SpheneUI.Controls.CollapsibleSection("Context & Tooltip"))
        {
            SpheneUI.Layout.Separator();
            var compactTooltipRounding = theme.CompactTooltipRounding;
            if (SpheneUI.Controls.SliderFloat("Panel Tooltip Rounding", ref compactTooltipRounding, 0.0f, 30.0f, "%.1f"))
            {
                theme.CompactTooltipRounding = compactTooltipRounding;
                themeChanged = true;
            }
            var compactTooltipBorderSize = theme.CompactTooltipBorderSize;
            if (SpheneUI.Controls.SliderFloat("Panel Tooltip Border Size", ref compactTooltipBorderSize, 0.0f, 5.0f, "%.1f"))
            {
                theme.CompactTooltipBorderSize = compactTooltipBorderSize;
                themeChanged = true;
            }
            var compactTooltipPadding = theme.CompactTooltipPadding;
            if (SpheneUI.Controls.SliderFloat2("Panel Tooltip Padding", ref compactTooltipPadding, 0.0f, 24.0f, "%.1f"))
            {
                theme.CompactTooltipPadding = compactTooltipPadding;
                themeChanged = true;
            }
            var compactPopupBg = theme.CompactPopupBg;
            if (SpheneUI.Controls.ColorEdit("Popup Background", ref compactPopupBg))
            {
                theme.CompactPopupBg = compactPopupBg;
                themeChanged = true;
            }
            var compactPopupRounding = theme.CompactPopupRounding;
            if (SpheneUI.Controls.SliderFloat("Popup Rounding", ref compactPopupRounding, 0.0f, 30.0f, "%.1f"))
            {
                theme.CompactPopupRounding = compactPopupRounding;
                themeChanged = true;
            }
            var compactPopupBorderSize = theme.CompactPopupBorderSize;
            if (SpheneUI.Controls.SliderFloat("Popup Border Size", ref compactPopupBorderSize, 0.0f, 5.0f, "%.1f"))
            {
                theme.CompactPopupBorderSize = compactPopupBorderSize;
                themeChanged = true;
            }
            var compactContextMenuRounding = theme.CompactContextMenuRounding;
            if (SpheneUI.Controls.SliderFloat("Panel Context Menu Rounding", ref compactContextMenuRounding, 0.0f, 30.0f, "%.1f"))
            {
                theme.CompactContextMenuRounding = compactContextMenuRounding;
                themeChanged = true;
            }
            var compactContextMenuBorderSize = theme.CompactContextMenuBorderSize;
            if (SpheneUI.Controls.SliderFloat("Panel Context Menu Border Size", ref compactContextMenuBorderSize, 0.0f, 5.0f, "%.1f"))
            {
                theme.CompactContextMenuBorderSize = compactContextMenuBorderSize;
                themeChanged = true;
            }
            var compactChildPadding = theme.CompactChildPadding;
            if (SpheneUI.Controls.SliderFloat2("Context Menu Padding", ref compactChildPadding, 0.0f, 20.0f, "%.1f"))
            {
                theme.CompactChildPadding = compactChildPadding;
                themeChanged = true;
            }
            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Text.Plain("Context Menu Button Style");
            SpheneUI.Layout.Separator();
            var key = Sphene.UI.Design.ButtonStyleKeys.ContextMenu_Item;
            if (!theme.ButtonStyles.TryGetValue(key, out var ov))
            {
                ov = new Sphene.UI.Design.ButtonStyleOverride();
                theme.ButtonStyles[key] = ov;
            }
            var widthDelta = ov.WidthDelta;
            if (SpheneUI.Controls.DragFloat("Width", ref widthDelta, 0.1f, -100f, 200f))
            {
                ov.WidthDelta = widthDelta;
                theme.NotifyThemeChanged();
            }
            var heightDelta = ov.HeightDelta;
            if (SpheneUI.Controls.DragFloat("Height", ref heightDelta, 0.1f, -50f, 100f))
            {
                ov.HeightDelta = heightDelta;
                theme.NotifyThemeChanged();
            }
            var iconOffset = ov.IconOffset;
            if (SpheneUI.Controls.DragFloat2("Icon Offset", ref iconOffset, 0.1f, -50f, 50f))
            {
                ov.IconOffset = iconOffset;
                theme.NotifyThemeChanged();
            }
            SpheneUI.Layout.Separator();
            var effBtn = ov.Button ?? theme.CompactButton;
            if (SpheneUI.Controls.ColorEdit("Button", ref effBtn))
            {
                ov.Button = effBtn;
                theme.NotifyThemeChanged();
            }
            ImGui.SameLine();
            var genHoverActive = SpheneUI.Icons.Button(FontAwesomeIcon.Magic);
            if (genHoverActive)
            {
                var tup = ButtonStyleManagerUI.DeriveHoverActive(ov.Button ?? theme.CompactButton);
                ov.ButtonHovered = tup.Hover;
                ov.ButtonActive = tup.Active;
                theme.NotifyThemeChanged();
            }
            if (ImGui.IsItemHovered())
                SpheneUI.AttachTooltip("Generate Hover/Active colors from current Button color.");
            var effBtnH = ov.ButtonHovered ?? theme.CompactButtonHovered;
            if (SpheneUI.Controls.ColorEdit("Button Hovered", ref effBtnH))
            {
                ov.ButtonHovered = effBtnH;
                theme.NotifyThemeChanged();
            }
            var effBtnA = ov.ButtonActive ?? theme.CompactButtonActive;
            if (SpheneUI.Controls.ColorEdit("Button Active", ref effBtnA))
            {
                ov.ButtonActive = effBtnA;
                theme.NotifyThemeChanged();
            }
            var effIcon = ov.Icon ?? theme.CompactText;
            if (SpheneUI.Controls.ColorEdit("Icon", ref effIcon))
            {
                ov.Icon = effIcon;
                theme.NotifyThemeChanged();
            }
            var effTxt = ov.Text ?? theme.CompactText;
            if (SpheneUI.Controls.ColorEdit("Text", ref effTxt))
            {
                ov.Text = effTxt;
                theme.NotifyThemeChanged();
            }
            var effBorder = ov.Border ?? theme.CompactBorder;
            if (SpheneUI.Controls.ColorEdit("Border", ref effBorder))
            {
                ov.Border = effBorder;
                theme.NotifyThemeChanged();
            }
            var effBorderSize = ov.BorderSize ?? theme.CompactFrameBorderSize;
            if (SpheneUI.Controls.SliderFloat("Border Width", ref effBorderSize, 0f, 5f, "%.1f"))
            {
                ov.BorderSize = effBorderSize;
                theme.NotifyThemeChanged();
            }
            var desiredMenuWidth = 200.0f * ImGuiHelpers.GlobalScale;
            var tooltipPadding = theme.CompactTooltipPadding;
            var contextPadding = theme.CompactChildPadding;
            var spacingX = ImGui.GetStyle().ItemSpacing.X;
            var spacingY = ImGui.GetStyle().ItemSpacing.Y;
            var contextBorder = theme.CompactContextMenuBorderSize;
            var tooltipBorder = theme.CompactTooltipBorderSize;
            var contextPreviewWidth = desiredMenuWidth + (contextPadding.X * 2.0f) + (contextBorder * 2.0f);
            var tooltipPreviewWidth = desiredMenuWidth + (tooltipPadding.X * 2.0f) + (tooltipBorder * 2.0f);
            var previewHeight = 96.0f;
            var start = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            var tooltipHeadingPos = start;
            var contextHeadingPos = new Vector2(start.X + tooltipPreviewWidth + spacingX, start.Y);
            ImGui.SetCursorScreenPos(tooltipHeadingPos);
            SpheneUI.Text.Plain("Tooltip Preview");
            var headingH = ImGui.GetTextLineHeight();
            ImGui.SetCursorScreenPos(contextHeadingPos);
            SpheneUI.Text.Plain("Context Menu Preview");
            var tooltipStart = new Vector2(tooltipHeadingPos.X, tooltipHeadingPos.Y + headingH + spacingY);
            var contextStart = new Vector2(contextHeadingPos.X, contextHeadingPos.Y + headingH + spacingY);
            var tooltipEnd = new Vector2(tooltipStart.X + tooltipPreviewWidth, tooltipStart.Y + previewHeight);
            var contextEnd = new Vector2(contextStart.X + contextPreviewWidth, contextStart.Y + previewHeight);
            dl.AddRectFilled(tooltipStart, tooltipEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactPopupBg), theme.CompactTooltipRounding);
            dl.AddRect(tooltipStart, tooltipEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactBorder), theme.CompactTooltipRounding, ImDrawFlags.None, tooltipBorder);
            dl.AddRectFilled(contextStart, contextEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactPopupBg), theme.CompactContextMenuRounding);
            dl.AddRect(contextStart, contextEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactBorder), theme.CompactContextMenuRounding, ImDrawFlags.None, contextBorder);
            var menuWidth = desiredMenuWidth;
            ImGui.SetCursorScreenPos(new Vector2(tooltipStart.X + tooltipPadding.X + tooltipBorder, tooltipStart.Y + tooltipPadding.Y + tooltipBorder));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, tooltipPadding);
            if (ImGui.BeginChild("##tooltip-preview", new Vector2(desiredMenuWidth + tooltipPadding.X * 2.0f, previewHeight - tooltipBorder * 2.0f), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground))
            {
                SpheneUI.Text.Plain("Tooltip text");
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.SetCursorScreenPos(new Vector2(contextStart.X + contextPadding.X + contextBorder, contextStart.Y + contextPadding.Y + contextBorder));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, contextPadding);
            if (ImGui.BeginChild("##context-preview", new Vector2(desiredMenuWidth + contextPadding.X * 2.0f, previewHeight - contextBorder * 2.0f), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground))
            {
                SpheneUI.Text.Plain("Common Pair Functions");
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                SpheneUI.Icons.ActionButton(FontAwesomeIcon.User, "Open Profile", menuWidth, ButtonStyleKeys.ContextMenu_Item);
                SpheneUI.Icons.ActionButton(FontAwesomeIcon.Sync, "Reload last data", menuWidth, ButtonStyleKeys.ContextMenu_Item);
                SpheneUI.Icons.ActionButton(FontAwesomeIcon.PlayCircle, "Cycle pause state", menuWidth, ButtonStyleKeys.ContextMenu_Item);
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.SetCursorScreenPos(new Vector2(start.X, MathF.Max(contextEnd.Y, tooltipEnd.Y)));
        }
        SpheneUI.Layout.VerticalSpace();

        if (_preferButtonStylesTab) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        if (SpheneUI.Controls.CollapsibleSection("Button Styles"))
        {
            SpheneUI.Layout.Separator();
            var compactFrameRounding = theme.CompactFrameRounding;
            if (SpheneUI.Controls.SliderFloat("Rounding for all buttons", ref compactFrameRounding, 0.0f, 30.0f, "%.1f"))
            {
                theme.CompactFrameRounding = compactFrameRounding;
                themeChanged = true;
            }
            ButtonStyleManagerUI.Draw();
            _preferButtonStylesTab = false;
        }
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace();
        
        if (SpheneUI.Controls.CollapsibleSection("Header"))
        {
            SpheneUI.Layout.Separator();

            var compactShowImGuiHeader = theme.CompactShowImGuiHeader;
            if (SpheneUI.Controls.Toggle("Show Window Header", ref compactShowImGuiHeader))
            {
                theme.CompactShowImGuiHeader = compactShowImGuiHeader;
                themeChanged = true;
            }
            SpheneUI.Text.ColoredWrapped("Show the standard window header. Disable for a cleaner panel.", ImGuiColors.DalamudGrey);

            if (!theme.CompactShowImGuiHeader)
            {
                var headerBg = theme.CompactHeaderBg;
                if (SpheneUI.Controls.ColorEdit("Header Background", ref headerBg))
                {
                    theme.CompactHeaderBg = headerBg;
                    themeChanged = true;
                }

                var titleText = theme.CompactPanelTitleText;
                if (SpheneUI.Controls.ColorEdit("Title Text", ref titleText))
                {
                    theme.CompactPanelTitleText = titleText;
                    themeChanged = true;
                }
                
                var compactHeaderRounding = theme.CompactHeaderRounding;
                if (SpheneUI.Controls.SliderFloat("Header Rounding", ref compactHeaderRounding, 0.0f, 30.0f, "%.1f"))
                {
                    theme.CompactHeaderRounding = compactHeaderRounding;
                    themeChanged = true;
                }
            }
            if (theme.CompactShowImGuiHeader)
            {
                var compactTitleBg = theme.CompactTitleBg;
                if (SpheneUI.Controls.ColorEdit("Window Header Background", ref compactTitleBg))
                {
                    theme.CompactTitleBg = compactTitleBg;
                    themeChanged = true;
                }
                var compactTitleBgActive = theme.CompactTitleBgActive;
                if (SpheneUI.Controls.ColorEdit("Window Header Background (Active)", ref compactTitleBgActive))
                {
                    theme.CompactTitleBgActive = compactTitleBgActive;
                    themeChanged = true;
                }
            }
            var sectionHeaderText = theme.CompactHeaderText;
            if (SpheneUI.Controls.ColorEdit("Section Header Text", ref sectionHeaderText))
            {
                theme.CompactHeaderText = sectionHeaderText;
                themeChanged = true;
            }




            SpheneUI.Layout.VerticalSpace();
        }

        SpheneUI.Layout.VerticalSpace();

        

        SpheneUI.Layout.VerticalSpace();
        if (SpheneUI.Controls.CollapsibleSection("Connected Status"))
        {
            SpheneUI.Layout.Separator();

            var statusConnected = theme.CompactServerStatusConnected;
            if (SpheneUI.Controls.ColorEdit("Connected", ref statusConnected))
            {
                theme.CompactServerStatusConnected = statusConnected;
                themeChanged = true;
            }

            var statusWarning = theme.CompactServerStatusWarning;
            if (SpheneUI.Controls.ColorEdit("Warning", ref statusWarning))
            {
                theme.CompactServerStatusWarning = statusWarning;
                themeChanged = true;
            }

            var statusError = theme.CompactServerStatusError;
            if (SpheneUI.Controls.ColorEdit("Error", ref statusError))
            {
                theme.CompactServerStatusError = statusError;
                themeChanged = true;
            }

            SpheneUI.Layout.VerticalSpace();
        }

        

        
        SpheneUI.Layout.VerticalSpace();

        if (SpheneUI.Controls.CollapsibleSection("Connected Pairs"))
        {
            SpheneUI.Layout.Separator();

            var connectedText = theme.CompactConnectedText;
            if (SpheneUI.Controls.ColorEdit("Online Count Text", ref connectedText))
            {
                theme.CompactConnectedText = connectedText;
                themeChanged = true;
            }

            var allSyncshellsText = theme.CompactAllSyncshellsText;
            if (SpheneUI.Controls.ColorEdit("All Syncshells Text", ref allSyncshellsText))
            {
                theme.CompactAllSyncshellsText = allSyncshellsText;
                themeChanged = true;
            }

            var offlinePausedText = theme.CompactOfflinePausedText;
            if (SpheneUI.Controls.ColorEdit("Offline / Paused Text", ref offlinePausedText))
            {
                theme.CompactOfflinePausedText = offlinePausedText;
                themeChanged = true;
            }

            var offlineSyncshellText = theme.CompactOfflineSyncshellText;
            if (SpheneUI.Controls.ColorEdit("Offline Syncshell Users Text", ref offlineSyncshellText))
            {
                theme.CompactOfflineSyncshellText = offlineSyncshellText;
                themeChanged = true;
            }

            var visibleText = theme.CompactVisibleText;
            if (SpheneUI.Controls.ColorEdit("Visible Users Text", ref visibleText))
            {
                theme.CompactVisibleText = visibleText;
                themeChanged = true;
            }

            var pairsText = theme.CompactPairsText;
            if (SpheneUI.Controls.ColorEdit("Pairs Label Text", ref pairsText))
            {
                theme.CompactPairsText = pairsText;
                themeChanged = true;
            }

            SpheneUI.Layout.VerticalSpace();
        }
        
        SpheneUI.Layout.VerticalSpace();
        var ptbOpen = SpheneUI.Controls.CollapsibleSection("Player Transmission Bars");
        if (ptbOpen != theme.ShowTransmissionPreview)
        {
            theme.ShowTransmissionPreview = ptbOpen;
            _suppressUnsavedForPreview = true;
            theme.NotifyThemeChanged();
            _suppressUnsavedForPreview = false;
        }
        if (ptbOpen)
        {
            SpheneUI.Layout.Separator();
        
            var prevSeparate = theme.SeparateTransmissionBarStyles;
            var separateBars = theme.SeparateTransmissionBarStyles;
            if (SpheneUI.Controls.Toggle("Separate Upload/Download Settings", ref separateBars))
            {
                theme.SeparateTransmissionBarStyles = separateBars;
                themeChanged = true;
                if (!prevSeparate && theme.SeparateTransmissionBarStyles)
                {
                    theme.UploadTransmissionBarRounding = theme.TransmissionBarRounding;
                    theme.UploadTransmissionBarHeight = theme.CompactTransmissionBarHeight;
                    theme.UploadTransmissionBarBackground = theme.CompactTransmissionBarBackground;
                    theme.UploadTransmissionBarForeground = theme.CompactTransmissionBarForeground;
                    theme.UploadTransmissionBarBorder = theme.CompactTransmissionBarBorder;
                    theme.DownloadTransmissionBarRounding = theme.TransmissionBarRounding;
                    theme.DownloadTransmissionBarHeight = theme.CompactTransmissionBarHeight;
                    theme.DownloadTransmissionBarBackground = theme.CompactTransmissionBarBackground;
                    theme.DownloadTransmissionBarForeground = theme.CompactTransmissionBarForeground;
                    theme.DownloadTransmissionBarBorder = theme.CompactTransmissionBarBorder;
                    theme.UploadTransmissionGradientStart = theme.TransmissionGradientStart;
                    theme.UploadTransmissionGradientEnd = theme.TransmissionGradientEnd;
                    theme.DownloadTransmissionGradientStart = theme.TransmissionGradientStart;
                    theme.DownloadTransmissionGradientEnd = theme.TransmissionGradientEnd;
                    themeChanged = true;
                }
            }
        
            var autoTransmissionWidth = theme.AutoTransmissionBarWidth;
            if (SpheneUI.Controls.Toggle("Auto Transmission Width (fit content)", ref autoTransmissionWidth))
            {
                theme.AutoTransmissionBarWidth = autoTransmissionWidth;
                themeChanged = true;
            }
        
            if (!theme.SeparateTransmissionBarStyles)
            {
                var transmissionBarRounding = theme.TransmissionBarRounding;
                if (SpheneUI.Controls.SliderFloat("Transmission Bar Rounding", ref transmissionBarRounding, 0.0f, 15.0f, "%.1f"))
                {
                    theme.TransmissionBarRounding = transmissionBarRounding;
                    themeChanged = true;
                }
                
                var compactTransmissionBarHeight = theme.CompactTransmissionBarHeight;
                if (SpheneUI.Controls.SliderFloat("Transmission Bar Height", ref compactTransmissionBarHeight, 2.0f, 30.0f, "%.1f"))
                {
                    theme.CompactTransmissionBarHeight = compactTransmissionBarHeight;
                    themeChanged = true;
                }
                
                var compactTransmissionBarBackground = theme.CompactTransmissionBarBackground;
                if (SpheneUI.Controls.ColorEdit("Transmission Bar Background", ref compactTransmissionBarBackground))
                {
                    theme.CompactTransmissionBarBackground = compactTransmissionBarBackground;
                    themeChanged = true;
                }
                
                var compactTransmissionBarForeground = theme.CompactTransmissionBarForeground;
                if (SpheneUI.Controls.ColorEdit("Transmission Bar Foreground", ref compactTransmissionBarForeground))
                {
                    theme.CompactTransmissionBarForeground = compactTransmissionBarForeground;
                    themeChanged = true;
                }
                
                var compactTransmissionBarBorder = theme.CompactTransmissionBarBorder;
                if (SpheneUI.Controls.ColorEdit("Transmission Bar Border", ref compactTransmissionBarBorder))
                {
                    theme.CompactTransmissionBarBorder = compactTransmissionBarBorder;
                    themeChanged = true;
                }
                var useTransGrad = theme.TransmissionUseGradient;
                if (SpheneUI.Controls.Toggle("Use Gradient Fill", ref useTransGrad))
                {
                    theme.TransmissionUseGradient = useTransGrad;
                    themeChanged = true;
                }
                if (theme.TransmissionUseGradient)
                {
                    var tGradStart = theme.TransmissionGradientStart;
                    if (SpheneUI.Controls.ColorEdit("Gradient Start", ref tGradStart))
                    {
                        theme.TransmissionGradientStart = tGradStart;
                        themeChanged = true;
                    }
                    var tGradEnd = theme.TransmissionGradientEnd;
                    if (SpheneUI.Controls.ColorEdit("Gradient End", ref tGradEnd))
                    {
                        theme.TransmissionGradientEnd = tGradEnd;
                        themeChanged = true;
                    }
                }
            }
            else
            {
                SpheneUI.Text.Plain("Upload Bar");
                SpheneUI.Layout.Separator();
                var uploadRounding = theme.UploadTransmissionBarRounding;
                if (SpheneUI.Controls.SliderFloat("Upload Bar Rounding", ref uploadRounding, 0.0f, 15.0f, "%.1f"))
                {
                    theme.UploadTransmissionBarRounding = uploadRounding;
                    themeChanged = true;
                }
                var uploadHeight = theme.UploadTransmissionBarHeight;
                if (SpheneUI.Controls.SliderFloat("Upload Bar Height", ref uploadHeight, 2.0f, 30.0f, "%.1f"))
                {
                    theme.UploadTransmissionBarHeight = uploadHeight;
                    themeChanged = true;
                }
                var uploadBg = theme.UploadTransmissionBarBackground;
                if (SpheneUI.Controls.ColorEdit("Upload Bar Background", ref uploadBg))
                {
                    theme.UploadTransmissionBarBackground = uploadBg;
                    themeChanged = true;
                }
                var uploadFg = theme.UploadTransmissionBarForeground;
                if (SpheneUI.Controls.ColorEdit("Upload Bar Foreground", ref uploadFg))
                {
                    theme.UploadTransmissionBarForeground = uploadFg;
                    themeChanged = true;
                }
                var uploadBorder = theme.UploadTransmissionBarBorder;
                if (SpheneUI.Controls.ColorEdit("Upload Bar Border", ref uploadBorder))
                {
                    theme.UploadTransmissionBarBorder = uploadBorder;
                    themeChanged = true;
                }
                var useTransGrad = theme.TransmissionUseGradient;
                if (SpheneUI.Controls.Toggle("Use Gradient Fill (Upload/Download)", ref useTransGrad))
                {
                    theme.TransmissionUseGradient = useTransGrad;
                    themeChanged = true;
                }
                if (theme.TransmissionUseGradient)
                {
                    var upGradStart = theme.UploadTransmissionGradientStart;
                    if (SpheneUI.Controls.ColorEdit("Upload Gradient Start", ref upGradStart))
                    {
                        theme.UploadTransmissionGradientStart = upGradStart;
                        themeChanged = true;
                    }
                    var upGradEnd = theme.UploadTransmissionGradientEnd;
                    if (SpheneUI.Controls.ColorEdit("Upload Gradient End", ref upGradEnd))
                    {
                        theme.UploadTransmissionGradientEnd = upGradEnd;
                        themeChanged = true;
                    }
                }
                
                SpheneUI.Layout.VerticalSpace();
                SpheneUI.Text.Plain("Download Bar");
                SpheneUI.Layout.Separator();
                var downloadRounding = theme.DownloadTransmissionBarRounding;
                if (SpheneUI.Controls.SliderFloat("Download Bar Rounding", ref downloadRounding, 0.0f, 15.0f, "%.1f"))
                {
                    theme.DownloadTransmissionBarRounding = downloadRounding;
                    themeChanged = true;
                }
                var downloadHeight = theme.DownloadTransmissionBarHeight;
                if (SpheneUI.Controls.SliderFloat("Download Bar Height", ref downloadHeight, 2.0f, 30.0f, "%.1f"))
                {
                    theme.DownloadTransmissionBarHeight = downloadHeight;
                    themeChanged = true;
                }
                var downloadBg = theme.DownloadTransmissionBarBackground;
                if (SpheneUI.Controls.ColorEdit("Download Bar Background", ref downloadBg))
                {
                    theme.DownloadTransmissionBarBackground = downloadBg;
                    themeChanged = true;
                }
                var downloadFg = theme.DownloadTransmissionBarForeground;
                if (SpheneUI.Controls.ColorEdit("Download Bar Foreground", ref downloadFg))
                {
                    theme.DownloadTransmissionBarForeground = downloadFg;
                    themeChanged = true;
                }
                var downloadBorder = theme.DownloadTransmissionBarBorder;
                if (SpheneUI.Controls.ColorEdit("Download Bar Border", ref downloadBorder))
                {
                    theme.DownloadTransmissionBarBorder = downloadBorder;
                    themeChanged = true;
                }
                if (theme.TransmissionUseGradient)
                {
                    var downGradStart = theme.DownloadTransmissionGradientStart;
                    if (SpheneUI.Controls.ColorEdit("Download Gradient Start", ref downGradStart))
                    {
                        theme.DownloadTransmissionGradientStart = downGradStart;
                        themeChanged = true;
                    }
                    var downGradEnd = theme.DownloadTransmissionGradientEnd;
                    if (SpheneUI.Controls.ColorEdit("Download Gradient End", ref downGradEnd))
                    {
                        theme.DownloadTransmissionGradientEnd = downGradEnd;
                        themeChanged = true;
                    }
                }
            }
        
            SpheneUI.Layout.VerticalSpace();
            bool previewChanged = false;
            if (theme.ShowTransmissionPreview)
            {
                var uploadFill = theme.TransmissionPreviewUploadFill;
                if (SpheneUI.Controls.SliderFloat("Preview Upload Fill", ref uploadFill, 0.0f, 100.0f, "%.1f%"))
                {
                    theme.TransmissionPreviewUploadFill = uploadFill;
                    previewChanged = true;
                }
                
                var downloadFill = theme.TransmissionPreviewDownloadFill;
                if (SpheneUI.Controls.SliderFloat("Preview Download Fill", ref downloadFill, 0.0f, 100.0f, "%.1f%"))
                {
                    theme.TransmissionPreviewDownloadFill = downloadFill;
                    previewChanged = true;
                }
            }
            if (previewChanged)
            {
                _suppressUnsavedForPreview = true;
                theme.NotifyThemeChanged();
                _suppressUnsavedForPreview = false;
            }
        }
        SpheneUI.Layout.VerticalSpace();

        var uhOpen = SpheneUI.Controls.CollapsibleSection("Update Hint");
        if (uhOpen != theme.ForceShowUpdateHint)
        {
            theme.ForceShowUpdateHint = uhOpen;
            _suppressUnsavedForPreview = true;
            theme.NotifyThemeChanged();
            _suppressUnsavedForPreview = false;
        }
        if (uhOpen)
        {
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace();
            var updateHintHeight = theme.CompactUpdateHintHeight;
            if (SpheneUI.Controls.SliderFloat("Update Hint Height", ref updateHintHeight, 0.0f, 64.0f, "%.1f"))
            {
                theme.CompactUpdateHintHeight = updateHintHeight;
                themeChanged = true;
            }
            var updateHintPadding = theme.CompactUpdateHintPaddingY;
            if (SpheneUI.Controls.SliderFloat("Update Hint Padding", ref updateHintPadding, 0.0f, 24.0f, "%.1f"))
            {
                theme.CompactUpdateHintPaddingY = updateHintPadding;
                themeChanged = true;
            }
            var updateHintColor = theme.CompactUpdateHintColor;
            if (SpheneUI.Controls.ColorEdit("Update Hint Color", ref updateHintColor))
            {
                theme.CompactUpdateHintColor = updateHintColor;
                themeChanged = true;
            }
            SpheneUI.Layout.VerticalSpace();
        }
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
        }
    }

    /// <summary>
    /// Back/forward arrows plus the crumb pill inside the chrome title bar —
    /// the same cluster the hub draws in its header, minus the profile card.
    /// </summary>
    private void DrawSettingsTitleCluster(Vector2 zoneMin, Vector2 zoneMax)
    {
        var scale = SpheneUI.Scale;
        var btnSize = ImGui.GetFrameHeight();
        var zoneHeight = zoneMax.Y - zoneMin.Y;
        var btnY = zoneMin.Y + (zoneHeight - btnSize) * 0.5f;

        ImGui.SetCursorScreenPos(new Vector2(zoneMin.X, btnY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowLeft, btnSize, null, null, btnSize,
                enabled: _settingsNavBack.Count > 0))
        {
            SettingsNavigateBack();
        }
        SpheneUI.AttachTooltip("Back");

        ImGui.SetCursorScreenPos(new Vector2(zoneMin.X + btnSize + 4f * scale, btnY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowRight, btnSize, null, null, btnSize,
                enabled: _settingsNavForward.Count > 0))
        {
            SettingsNavigateForward();
        }
        SpheneUI.AttachTooltip("Forward");

        var pillH = 26f * scale;
        var crumbX = zoneMin.X + btnSize * 2f + 4f * scale + 10f * scale;
        if (crumbX >= zoneMax.X)
        {
            return;
        }
        var (category, label) = SettingsCrumbInfo(DiagnosticsPageLabel, _activeSettingsPage);
        SpheneUI.Chrome.CrumbPill(
            new Vector2(crumbX, zoneMin.Y + (zoneHeight - pillH) * 0.5f),
            zoneMax.X - crumbX, category, label, null);
    }

    /// <summary>Raised when the active settings page changes through user navigation — args are (previous, next, embedded). The hub folds embedded-surface changes into its own breadcrumb/history.</summary>
    internal event Action<SettingsPage, SettingsPage, bool>? SettingsPageChanged;

    /// <summary>The page the Hub-embedded settings shows — read by the hub for its crumb. The standalone window has its own page state.</summary>
    internal SettingsPage ActiveSettingsPageValue => _embeddedSettingsPage;

    /// <summary>Label of the embedded settings page for the hub breadcrumb.</summary>
    internal string ActiveCrumbLabel => SettingsCrumbInfo(DiagnosticsPageLabel, _embeddedSettingsPage).Label;

    /// <summary>Applies a page to the Hub-embedded settings without recording it in history — used for hub-driven restores.</summary>
    internal void ApplySettingsPageQuiet(SettingsPage page)
    {
        _embeddedSettingsPage = page;
    }

    /// <summary>Switches the active settings page and records it in the title bar back/forward history.</summary>
    private void SetSettingsPage(SettingsPage page)
    {
        if (page == _activeSettingsPage)
        {
            return;
        }
        _settingsNavBack.Add(_activeSettingsPage);
        _settingsNavForward.Clear();
        var previous = _activeSettingsPage;
        _activeSettingsPage = page;
        SettingsPageChanged?.Invoke(previous, page, _embeddedScope);
    }

    private void SettingsNavigateBack()
    {
        if (_settingsNavBack.Count == 0)
        {
            return;
        }
        var target = _settingsNavBack[^1];
        _settingsNavBack.RemoveAt(_settingsNavBack.Count - 1);
        _settingsNavForward.Add(_activeSettingsPage);
        _activeSettingsPage = target;
    }

    private void SettingsNavigateForward()
    {
        if (_settingsNavForward.Count == 0)
        {
            return;
        }
        var target = _settingsNavForward[^1];
        _settingsNavForward.RemoveAt(_settingsNavForward.Count - 1);
        _settingsNavBack.Add(_activeSettingsPage);
        _activeSettingsPage = target;
    }

    private void OnNavigateToButtonSettings(ThemeNavigateToButtonSettingsMessage msg)
    {
        SetSettingsPage(SettingsPage.Theme);
        IsOpen = true;
        _preferThemeTab = "Panel Theme";
        _preferButtonStylesTab = true;
        ButtonStyleManagerUI.SelectButtonKey(msg.ButtonStyleKey);
    }

    
    
    private static void DrawBasicColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        SpheneUI.Text.ColoredWrapped("Basic colors form the foundation of your theme. These colors are used throughout the interface for primary elements.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace();
        
        // Primary Colors
        SpheneUI.Text.Plain("Primary Colors");
        SpheneUI.Layout.Separator();
        
        var primaryDark = theme.PrimaryDark;
        if (SpheneUI.Controls.ColorEdit("Primary Dark", ref primaryDark))
        {
            theme.PrimaryDark = primaryDark;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Main dark color used for backgrounds and primary interface elements.");
        
        var secondaryDark = theme.SecondaryDark;
        if (SpheneUI.Controls.ColorEdit("Secondary Dark", ref secondaryDark))
        {
            theme.SecondaryDark = secondaryDark;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Secondary dark color used for contrast and supporting interface elements.");
        
        SpheneUI.Layout.VerticalSpace();
        
        // Accent Colors
        SpheneUI.Text.Plain("Accent Colors");
        SpheneUI.Layout.Separator();
        
        var accentBlue = theme.AccentBlue;
        if (SpheneUI.Controls.ColorEdit("Accent Blue", ref accentBlue))
        {
            theme.AccentBlue = accentBlue;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Blue accent color used for highlights, links, and important interactive elements.");
        
        var accentCyan = theme.AccentCyan;
        if (SpheneUI.Controls.ColorEdit("Accent Cyan", ref accentCyan))
        {
            theme.AccentCyan = accentCyan;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Cyan accent color used for secondary highlights and status indicators.");
        
        SpheneUI.Layout.VerticalSpace();
        
        // Text Colors
        SpheneUI.Text.Plain("Text Colors");
        SpheneUI.Layout.Separator();
        
        var textPrimary = theme.TextPrimary;
        if (SpheneUI.Controls.ColorEdit("Text Primary", ref textPrimary))
        {
            theme.TextPrimary = textPrimary;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Primary text color used for most readable text throughout the interface.");
        
        var textSecondary = theme.TextSecondary;
        if (SpheneUI.Controls.ColorEdit("Text Secondary", ref textSecondary))
        {
            theme.TextSecondary = textSecondary;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Secondary text color used for less important text and descriptions.");
        
        var textDisabled = theme.TextDisabled;
        if (SpheneUI.Controls.ColorEdit("Text Disabled", ref textDisabled))
        {
            theme.TextDisabled = textDisabled;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Text color for disabled or inactive elements that cannot be interacted with.");
        
        var textSelectedBg = theme.TextSelectedBg;
        if (SpheneUI.Controls.ColorEdit("Text Selected Background", ref textSelectedBg))
        {
            theme.TextSelectedBg = textSelectedBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for selected text and highlighted text areas.");
        
        SpheneUI.Layout.VerticalSpace();
        
        // Interactive Colors
        SpheneUI.Text.Plain("Interactive Colors");
        SpheneUI.Layout.Separator();
        
        var border = theme.Border;
        if (SpheneUI.Controls.ColorEdit("Border", ref border))
        {
            theme.Border = border;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color used for borders around windows, frames, and input fields.");
        
        var hover = theme.Hover;
        if (SpheneUI.Controls.ColorEdit("Hover", ref hover))
        {
            theme.Hover = hover;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color used when hovering over interactive elements like buttons and menu items.");
        
        var active = theme.Active;
        if (SpheneUI.Controls.ColorEdit("Active", ref active))
        {
            theme.Active = active;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color used when actively clicking or pressing interactive elements.");
    }
    
    private static void DrawWindowColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        SpheneUI.Text.ColoredWrapped("Window colors control the appearance of different window types and their backgrounds.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace();
        
        SpheneUI.Text.Plain("Window Colors");
        SpheneUI.Layout.Separator();
        
        var windowBg = theme.WindowBg;
        if (SpheneUI.Controls.ColorEdit("Window Background", ref windowBg))
        {
            theme.WindowBg = windowBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Main background color for all windows and dialogs.");
        
        var childBg = theme.ChildBg;
        if (SpheneUI.Controls.ColorEdit("Child Background", ref childBg))
        {
            theme.ChildBg = childBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for child windows and nested content areas.");
        
        var popupBg = theme.PopupBg;
        if (SpheneUI.Controls.ColorEdit("Popup Background", ref popupBg))
        {
            theme.PopupBg = popupBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for popup windows, tooltips, and context menus.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Title Bar Colors");
        SpheneUI.Layout.Separator();
        
        var titleBg = theme.TitleBg;
        if (SpheneUI.Controls.ColorEdit("Title Background", ref titleBg))
        {
            theme.TitleBg = titleBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for inactive window title bars.");
        
        var titleBgActive = theme.TitleBgActive;
        if (SpheneUI.Controls.ColorEdit("Title Background Active", ref titleBgActive))
        {
            theme.TitleBgActive = titleBgActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for active (focused) window title bars.");
        
        var titleBgCollapsed = theme.TitleBgCollapsed;
        if (SpheneUI.Controls.ColorEdit("Title Background Collapsed", ref titleBgCollapsed))
        {
            theme.TitleBgCollapsed = titleBgCollapsed;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for collapsed window title bars.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Modal & Overlay Colors");
        SpheneUI.Layout.Separator();
        
        var modalWindowDimBg = theme.ModalWindowDimBg;
        if (SpheneUI.Controls.ColorEdit("Modal Window Dim Background", ref modalWindowDimBg))
        {
            theme.ModalWindowDimBg = modalWindowDimBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Dimming overlay color behind modal dialogs and popups.");
    }
    
    private static void DrawFrameInputColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        SpheneUI.Text.ColoredWrapped("Frame and input colors control the appearance of input fields, checkboxes, and interactive elements.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace();
        
        SpheneUI.Text.Plain("Frame & Input Colors");
        SpheneUI.Layout.Separator();
        
        var frameBg = theme.FrameBg;
        if (SpheneUI.Controls.ColorEdit("Frame Background", ref frameBg))
        {
            theme.FrameBg = frameBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for input fields, sliders, and other frame elements.");
        
        var frameBgHovered = theme.FrameBgHovered;
        if (SpheneUI.Controls.ColorEdit("Frame Background Hovered", ref frameBgHovered))
        {
            theme.FrameBgHovered = frameBgHovered;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for frame elements when hovered over.");
        
        var frameBgActive = theme.FrameBgActive;
        if (SpheneUI.Controls.ColorEdit("Frame Background Active", ref frameBgActive))
        {
            theme.FrameBgActive = frameBgActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for frame elements when actively being used.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Interactive Elements");
        SpheneUI.Layout.Separator();
        
        var checkMark = theme.CheckMark;
        if (SpheneUI.Controls.ColorEdit("Check Mark", ref checkMark))
        {
            theme.CheckMark = checkMark;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for checkmarks in checkboxes and radio buttons.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Window Controls");
        SpheneUI.Layout.Separator();
        
        var resizeGrip = theme.ResizeGrip;
        if (SpheneUI.Controls.ColorEdit("Resize Grip", ref resizeGrip))
        {
            theme.ResizeGrip = resizeGrip;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for the resize grip in the bottom-right corner of windows.");
        
        var resizeGripHovered = theme.ResizeGripHovered;
        if (SpheneUI.Controls.ColorEdit("Resize Grip Hovered", ref resizeGripHovered))
        {
            theme.ResizeGripHovered = resizeGripHovered;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for the resize grip when hovered over.");
        
        var resizeGripActive = theme.ResizeGripActive;
        if (SpheneUI.Controls.ColorEdit("Resize Grip Active", ref resizeGripActive))
        {
            theme.ResizeGripActive = resizeGripActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for the resize grip when actively being dragged.");
    }
    
    private static void DrawButtonHeaderColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        SpheneUI.Text.ColoredWrapped("Button and header colors control the appearance of clickable elements and section headers.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace();
        
        SpheneUI.Text.Plain("Button Colors");
        SpheneUI.Layout.Separator();
        
        var button = theme.Button;
        if (SpheneUI.Controls.ColorEdit("Button", ref button))
        {
            theme.Button = button;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Default background color for buttons.");
        
        var buttonHovered = theme.ButtonHovered;
        if (SpheneUI.Controls.ColorEdit("Button Hovered", ref buttonHovered))
        {
            theme.ButtonHovered = buttonHovered;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for buttons when hovered over.");
        
        var buttonActive = theme.ButtonActive;
        if (SpheneUI.Controls.ColorEdit("Button Active", ref buttonActive))
        {
            theme.ButtonActive = buttonActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for buttons when clicked or pressed.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Header Colors");
        SpheneUI.Layout.Separator();
        
        var headerBg = theme.HeaderBg;
        if (SpheneUI.Controls.ColorEdit("Header Background", ref headerBg))
        {
            theme.HeaderBg = headerBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for collapsible headers and section titles.");
        
        var headerHovered = theme.HeaderHovered;
        if (SpheneUI.Controls.ColorEdit("Header Hovered", ref headerHovered))
        {
            theme.HeaderHovered = headerHovered;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for headers when hovered over.");
        
        var headerActive = theme.HeaderActive;
        if (SpheneUI.Controls.ColorEdit("Header Active", ref headerActive))
        {
            theme.HeaderActive = headerActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for headers when clicked or active.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Separators");
        SpheneUI.Layout.Separator();
        
        var separator = theme.Separator;
        if (SpheneUI.Controls.ColorEdit("Separator", ref separator))
        {
            theme.Separator = separator;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for separator lines that divide sections.");
        
        var separatorHovered = theme.SeparatorHovered;
        if (SpheneUI.Controls.ColorEdit("Separator Hovered", ref separatorHovered))
        {
            theme.SeparatorHovered = separatorHovered;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for separator lines when hovered over.");
        
        var separatorActive = theme.SeparatorActive;
        if (SpheneUI.Controls.ColorEdit("Separator Active", ref separatorActive))
        {
            theme.SeparatorActive = separatorActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for separator lines when being dragged or active.");
    }
    
    private static void DrawMenuNavigationColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        SpheneUI.Text.ColoredWrapped("Menu and navigation colors control the appearance of menu bars, navigation highlights, and drag-drop interactions.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace();
        
        SpheneUI.Text.Plain("Menu Colors");
        SpheneUI.Layout.Separator();
        
        var menuBarBg = theme.MenuBarBg;
        if (SpheneUI.Controls.ColorEdit("Menu Bar Background", ref menuBarBg))
        {
            theme.MenuBarBg = menuBarBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for menu bars at the top of windows.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Navigation Colors");
        SpheneUI.Layout.Separator();
        
        var navHighlight = theme.NavHighlight;
        if (SpheneUI.Controls.ColorEdit("Navigation Highlight", ref navHighlight))
        {
            theme.NavHighlight = navHighlight;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for highlighting the currently focused navigation element.");
        
        var navWindowingHighlight = theme.NavWindowingHighlight;
        if (SpheneUI.Controls.ColorEdit("Navigation Windowing Highlight", ref navWindowingHighlight))
        {
            theme.NavWindowingHighlight = navWindowingHighlight;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for highlighting windows during navigation mode.");
        
        var navWindowingDimBg = theme.NavWindowingDimBg;
        if (SpheneUI.Controls.ColorEdit("Navigation Windowing Dim Background", ref navWindowingDimBg))
        {
            theme.NavWindowingDimBg = navWindowingDimBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background dimming color during window navigation mode.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Drag & Drop Colors");
        SpheneUI.Layout.Separator();
        
        var dragDropTarget = theme.DragDropTarget;
        if (SpheneUI.Controls.ColorEdit("Drag Drop Target", ref dragDropTarget))
        {
            theme.DragDropTarget = dragDropTarget;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for highlighting valid drop targets during drag operations.");
    }
    
    private static void DrawScrollbarSliderColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        SpheneUI.Text.ColoredWrapped("Scrollbar and slider colors control the appearance of scrollbars and slider controls throughout the interface.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace();
        
        SpheneUI.Text.Plain("Scrollbar Colors");
        SpheneUI.Layout.Separator();
        
        var scrollbarBg = theme.ScrollbarBg;
        if (SpheneUI.Controls.ColorEdit("Scrollbar Background", ref scrollbarBg))
        {
            theme.ScrollbarBg = scrollbarBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for the scrollbar track.");
        
        var scrollbarGrab = theme.ScrollbarGrab;
        if (SpheneUI.Controls.ColorEdit("Scrollbar Grab", ref scrollbarGrab))
        {
            theme.ScrollbarGrab = scrollbarGrab;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for the scrollbar handle that you can drag.");
        
        var scrollbarGrabHovered = theme.ScrollbarGrabHovered;
        if (SpheneUI.Controls.ColorEdit("Scrollbar Grab Hovered", ref scrollbarGrabHovered))
        {
            theme.ScrollbarGrabHovered = scrollbarGrabHovered;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for the scrollbar handle when hovered over.");
        
        var scrollbarGrabActive = theme.ScrollbarGrabActive;
        if (SpheneUI.Controls.ColorEdit("Scrollbar Grab Active", ref scrollbarGrabActive))
        {
            theme.ScrollbarGrabActive = scrollbarGrabActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for the scrollbar handle when being dragged.");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Slider Colors");
        SpheneUI.Layout.Separator();
        
        var sliderGrab = theme.SliderGrab;
        if (SpheneUI.Controls.ColorEdit("Slider Grab", ref sliderGrab))
        {
            theme.SliderGrab = sliderGrab;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for slider handles and knobs.");
        
        var sliderGrabActive = theme.SliderGrabActive;
        if (SpheneUI.Controls.ColorEdit("Slider Grab Active", ref sliderGrabActive))
        {
            theme.SliderGrabActive = sliderGrabActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for slider handles when being dragged.");
    }
    
    private static void DrawTableTabColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        SpheneUI.Text.ColoredWrapped("Table and tab colors control the appearance of data tables and tab navigation elements.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace();
        
        SpheneUI.Text.Plain("Table Colors");
        SpheneUI.Layout.Separator();
        
        var tableHeaderBg = theme.TableHeaderBg;
        if (SpheneUI.Controls.ColorEdit("Table Header Background", ref tableHeaderBg))
        {
            theme.TableHeaderBg = tableHeaderBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for table headers and column titles.");
        
        var tableBorderStrong = theme.TableBorderStrong;
        if (SpheneUI.Controls.ColorEdit("Table Border Strong", ref tableBorderStrong))
        {
            theme.TableBorderStrong = tableBorderStrong;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for strong table borders (outer borders and major divisions).");
        
        var tableBorderLight = theme.TableBorderLight;
        if (SpheneUI.Controls.ColorEdit("Table Border Light", ref tableBorderLight))
        {
            theme.TableBorderLight = tableBorderLight;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Color for light table borders (inner cell borders).");
        
        var tableRowBg = theme.TableRowBg;
        if (SpheneUI.Controls.ColorEdit("Table Row Background", ref tableRowBg))
        {
            theme.TableRowBg = tableRowBg;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for table rows.");
        
        var tableRowBgAlt = theme.TableRowBgAlt;
        if (SpheneUI.Controls.ColorEdit("Table Row Background Alt", ref tableRowBgAlt))
        {
            theme.TableRowBgAlt = tableRowBgAlt;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Alternating background color for table rows (zebra striping).");
        
        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Text.Plain("Tab Colors");
        SpheneUI.Layout.Separator();
        
        var tab = theme.Tab;
        if (SpheneUI.Controls.ColorEdit("Tab", ref tab))
        {
            theme.Tab = tab;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Default background color for inactive tabs.");
        
        var tabHovered = theme.TabHovered;
        if (SpheneUI.Controls.ColorEdit("Tab Hovered", ref tabHovered))
        {
            theme.TabHovered = tabHovered;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for tabs when hovered over.");
        
        var tabActive = theme.TabActive;
        if (SpheneUI.Controls.ColorEdit("Tab Active", ref tabActive))
        {
            theme.TabActive = tabActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for the currently active tab.");
        
        var tabUnfocused = theme.TabUnfocused;
        if (SpheneUI.Controls.ColorEdit("Tab Unfocused", ref tabUnfocused))
        {
            theme.TabUnfocused = tabUnfocused;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for tabs when the window is not focused.");
        
        var tabUnfocusedActive = theme.TabUnfocusedActive;
        if (SpheneUI.Controls.ColorEdit("Tab Unfocused Active", ref tabUnfocusedActive))
        {
            theme.TabUnfocusedActive = tabUnfocusedActive;
            themeChanged = true;
        }
        SpheneUI.Controls.HelpText("Background color for the active tab when the window is not focused.");
    }
    
    

    private string _saveThemeName = "";
    private string _selectedPresetTheme = "";
    
    // Theme change tracking
    private bool _hasUnsavedThemeChanges = false;
    private ThemeConfiguration? _originalThemeState = null;
    private string _currentThemeName = "";
    private bool _showThemeSavePrompt = false;
    private bool _showUnsavedCustomThemePrompt = false;

    private void DrawSaveThemePopup()
    {
        SpheneUI.Text.Plain("Save Current Theme");
        SpheneUI.Layout.Separator();
        
        SpheneUI.Text.Plain("Theme Name:");
        SpheneUI.Controls.InputText("##SaveThemeName", ref _saveThemeName, 100);
        
        SpheneUI.Layout.VerticalSpace();
        
        if (SpheneUI.Controls.Button("Save") && !string.IsNullOrWhiteSpace(_saveThemeName))
        {
            _ = Task.Run(async () =>
            {
                var success = await ThemeManager.SaveTheme(SpheneTheme.Current, _saveThemeName).ConfigureAwait(false);
                if (success)
                {
                    _saveThemeName = "";
                }
            });
            ImGui.CloseCurrentPopup();
        }
        
        ImGui.SameLine();
        
        if (SpheneUI.Controls.Button("Cancel"))
        {
            _saveThemeName = "";
            ImGui.CloseCurrentPopup();
        }
    }

    

    

    private void DrawThemeSelector()
    {
        // Get all available themes (built-in + custom)
        var builtInThemes = ThemePresets.BuiltInThemes.Keys.ToList();
        var customThemes = ThemeManager.GetAvailableThemes();
        var allThemes = new List<string>();
        
        // Add built-in themes first
        allThemes.AddRange(builtInThemes);
        
        // Add separator if we have custom themes
        if (customThemes.Any())
        {
            allThemes.Add("--- Custom Themes ---");
            allThemes.AddRange(customThemes);
        }
        
        // Current selection - load from configuration
        var savedTheme = ThemeManager.GetSelectedTheme();
        if (!string.IsNullOrEmpty(savedTheme))
            _selectedPresetTheme = savedTheme;
        else if (string.IsNullOrEmpty(_selectedPresetTheme))
            _selectedPresetTheme = "Default Sphene";
        
        var currentIndex = allThemes.IndexOf(_selectedPresetTheme);
        if (currentIndex == -1) currentIndex = 0;
        
        var selectorButtonSpacing = ImGui.GetStyle().ItemSpacing.X;
        var themeSelectorTrailing = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X + selectorButtonSpacing
            + ImGui.CalcTextSize("Reset to Default Sphene Theme").X + ImGui.GetStyle().FramePadding.X * 2f + selectorButtonSpacing;
        if (SpheneUI.Controls.LabeledCombo("##ThemeSelector", ref currentIndex, allThemes.ToArray(), allThemes.Count,
                trailingWidth: themeSelectorTrailing, preferredWidth: ClampSettingsItemWidth(320f, 180f)))
        {
            var selectedTheme = allThemes[currentIndex];
            
            // Skip separator entries
            if (selectedTheme.StartsWith("---", StringComparison.Ordinal)) return;
            
            _selectedPresetTheme = selectedTheme;
            ApplySelectedPresetTheme(selectedTheme, builtInThemes);
        }
        
        SpheneUI.Controls.SameLineIfFits(SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X);
        var canDelete = !string.IsNullOrEmpty(_selectedPresetTheme) && !builtInThemes.Contains(_selectedPresetTheme, StringComparer.Ordinal) && !_selectedPresetTheme.StartsWith("---", StringComparison.Ordinal);
        using (ImRaii.Disabled(!canDelete || !UiSharedService.CtrlPressed()))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash) && ThemeManager.DeleteTheme(_selectedPresetTheme))
            {
                _selectedPresetTheme = "Default Sphene";
                ApplySelectedPresetTheme("Default Sphene", builtInThemes);
            }
            SpheneUI.AttachTooltip(canDelete
                ? ($"Hold CTRL to delete selected custom theme: '{_selectedPresetTheme}'")
                : "Only custom themes can be deleted");
        }
        
        SpheneUI.Controls.SameLineIfFits(ImGui.CalcTextSize("Reset to Default Sphene Theme").X + ImGui.GetStyle().FramePadding.X * 2f);
        if (SpheneUI.Controls.Button("Reset to Default Sphene Theme"))
        {
            _selectedPresetTheme = "Default Sphene";
            ApplySelectedPresetTheme("Default Sphene", builtInThemes);
        }
        
        // Theme Management Actions
        SpheneUI.Layout.VerticalSpace();

        if (SpheneUI.Controls.Button("New Theme"))
        {
            _saveThemeName = "";
            ImGui.OpenPopup("SaveThemePopup");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!_hasUnsavedThemeChanges))
        {
            if (SpheneUI.Controls.Button("Save Theme Changes"))
            {
                if (!string.IsNullOrEmpty(_currentThemeName) && !builtInThemes.Contains(_currentThemeName, StringComparer.Ordinal))
                {
                    _ = Task.Run(async () =>
                    {
                        var success = await ThemeManager.SaveTheme(SpheneTheme.Current, _currentThemeName).ConfigureAwait(false);
                        if (success)
                        {
                            _hasUnsavedThemeChanges = false;
                        }
                    });
                }
                else
                {
                    ImGui.OpenPopup("SaveThemePopup");
                }
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!_hasUnsavedThemeChanges))
        {
            if (SpheneUI.Controls.Button("Reset Theme Changes"))
            {
                if (_originalThemeState != null)
                {
                    ThemePropertyCopier.Copy(_originalThemeState, SpheneTheme.Current);
                }
                else
                {
                    SpheneTheme.Current.ResetToDefaults();
                }
                SpheneTheme.Current.NotifyThemeChanged();
                _hasUnsavedThemeChanges = false;
            }
        }
        
        // Save Theme Popup
        if (SpheneUI.Popups.BeginStyled("SaveThemePopup"))
        {
            DrawSaveThemePopup();
            SpheneUI.Popups.EndStyled();
        }
    }

    private void ApplySelectedPresetTheme(string themeName, List<string> builtInThemes)
    {
        var currentTheme = SpheneTheme.Current;
        ThemeConfiguration? sourceTheme = null;

        if (builtInThemes.Contains(themeName, StringComparer.Ordinal))
        {
            // Apply built-in theme
            sourceTheme = ThemePresets.BuiltInThemes[themeName];
            ThemePropertyCopier.Copy(sourceTheme, currentTheme);
        }
        else
        {
            // Apply custom theme
            sourceTheme = ThemeManager.LoadTheme(themeName);
            if (sourceTheme != null)
            {
                ThemePropertyCopier.Copy(sourceTheme, currentTheme);
            }
        }

        // Apply icon theme settings from the loaded/preset theme to config
        if (sourceTheme != null)
        {
            var cfg = _configService.Current;
            cfg.IconGlobalAlpha = sourceTheme.IconGlobalAlpha;
            cfg.IconRainbowSpeed = sourceTheme.IconRainbowSpeed;
            cfg.IconShowModTransferBadge = sourceTheme.IconShowModTransferBadge;
            cfg.IconShowPairRequestBadge = sourceTheme.IconShowPairRequestBadge;
            cfg.IconShowNotificationBadge = sourceTheme.IconShowNotificationBadge;
            cfg.IconPermColor = sourceTheme.IconPermColor;
            cfg.IconPermAlpha = sourceTheme.IconPermAlpha;
            cfg.IconPermEffectPulse = sourceTheme.IconPermEffectPulse;
            cfg.IconPermEffectGlow = sourceTheme.IconPermEffectGlow;
            cfg.IconPermEffectBounce = sourceTheme.IconPermEffectBounce;
            cfg.IconPermEffectRainbow = sourceTheme.IconPermEffectRainbow;
            cfg.IconPermPulseMinRadius = sourceTheme.IconPermPulseMinRadius;
            cfg.IconPermPulseMaxRadius = sourceTheme.IconPermPulseMaxRadius;
            cfg.IconPermGlowIntensity = sourceTheme.IconPermGlowIntensity;
            cfg.IconPermGlowRadius = sourceTheme.IconPermGlowRadius;
            cfg.IconPermBounceIntensity = sourceTheme.IconPermBounceIntensity;
            cfg.IconPermBounceSpeed = sourceTheme.IconPermBounceSpeed;
            cfg.IconModTransferColor = sourceTheme.IconModTransferColor;
            cfg.IconModTransferAlpha = sourceTheme.IconModTransferAlpha;
            cfg.IconModTransferEffectPulse = sourceTheme.IconModTransferEffectPulse;
            cfg.IconModTransferEffectGlow = sourceTheme.IconModTransferEffectGlow;
            cfg.IconModTransferEffectBounce = sourceTheme.IconModTransferEffectBounce;
            cfg.IconModTransferEffectRainbow = sourceTheme.IconModTransferEffectRainbow;
            cfg.IconModTransferPulseMinRadius = sourceTheme.IconModTransferPulseMinRadius;
            cfg.IconModTransferPulseMaxRadius = sourceTheme.IconModTransferPulseMaxRadius;
            cfg.IconModTransferGlowIntensity = sourceTheme.IconModTransferGlowIntensity;
            cfg.IconModTransferGlowRadius = sourceTheme.IconModTransferGlowRadius;
            cfg.IconModTransferBounceIntensity = sourceTheme.IconModTransferBounceIntensity;
            cfg.IconModTransferBounceSpeed = sourceTheme.IconModTransferBounceSpeed;
            cfg.IconPairRequestColor = sourceTheme.IconPairRequestColor;
            cfg.IconPairRequestAlpha = sourceTheme.IconPairRequestAlpha;
            cfg.IconPairRequestEffectPulse = sourceTheme.IconPairRequestEffectPulse;
            cfg.IconPairRequestEffectGlow = sourceTheme.IconPairRequestEffectGlow;
            cfg.IconPairRequestEffectBounce = sourceTheme.IconPairRequestEffectBounce;
            cfg.IconPairRequestEffectRainbow = sourceTheme.IconPairRequestEffectRainbow;
            cfg.IconPairRequestPulseMinRadius = sourceTheme.IconPairRequestPulseMinRadius;
            cfg.IconPairRequestPulseMaxRadius = sourceTheme.IconPairRequestPulseMaxRadius;
            cfg.IconPairRequestGlowIntensity = sourceTheme.IconPairRequestGlowIntensity;
            cfg.IconPairRequestGlowRadius = sourceTheme.IconPairRequestGlowRadius;
            cfg.IconPairRequestBounceIntensity = sourceTheme.IconPairRequestBounceIntensity;
            cfg.IconPairRequestBounceSpeed = sourceTheme.IconPairRequestBounceSpeed;
            cfg.IconNotificationColor = sourceTheme.IconNotificationColor;
            cfg.IconNotificationAlpha = sourceTheme.IconNotificationAlpha;
            cfg.IconNotificationEffectPulse = sourceTheme.IconNotificationEffectPulse;
            cfg.IconNotificationEffectGlow = sourceTheme.IconNotificationEffectGlow;
            cfg.IconNotificationEffectBounce = sourceTheme.IconNotificationEffectBounce;
            cfg.IconNotificationEffectRainbow = sourceTheme.IconNotificationEffectRainbow;
            cfg.IconNotificationPulseMinRadius = sourceTheme.IconNotificationPulseMinRadius;
            cfg.IconNotificationPulseMaxRadius = sourceTheme.IconNotificationPulseMaxRadius;
            cfg.IconNotificationGlowIntensity = sourceTheme.IconNotificationGlowIntensity;
            cfg.IconNotificationGlowRadius = sourceTheme.IconNotificationGlowRadius;
            cfg.IconNotificationBounceIntensity = sourceTheme.IconNotificationBounceIntensity;
            cfg.IconNotificationBounceSpeed = sourceTheme.IconNotificationBounceSpeed;
            cfg.IconModTransferEffectDurationSeconds = sourceTheme.IconModTransferEffectDurationSeconds;
            cfg.IconPairRequestEffectDurationSeconds = sourceTheme.IconPairRequestEffectDurationSeconds;
            cfg.IconNotificationEffectDurationSeconds = sourceTheme.IconNotificationEffectDurationSeconds;
            cfg.IconModTransferBadgeDurationSeconds = sourceTheme.IconModTransferBadgeDurationSeconds;
            cfg.IconPairRequestBadgeDurationSeconds = sourceTheme.IconPairRequestBadgeDurationSeconds;
            cfg.IconNotificationBadgeDurationSeconds = sourceTheme.IconNotificationBadgeDurationSeconds;
            _configService.Save();
        }

        // Save selected theme to configuration for persistence
        ThemeManager.SetSelectedTheme(themeName);

        // Reset change tracking when applying a preset theme
        _hasUnsavedThemeChanges = false;
        _currentThemeName = themeName;
        _originalThemeState = currentTheme.Clone();
        var builtIn = builtInThemes.Contains(themeName, StringComparer.Ordinal);
        if (!builtIn)
        {
            _ = Task.Run(async () =>
            {
                var success = await ThemeManager.SaveTheme(currentTheme, themeName).ConfigureAwait(false);
                if (success)
                {
                    _logger.LogDebug("Auto-saved theme: {themeName}", themeName);
                }
            });
        }
        
        currentTheme.NotifyThemeChanged();
    }

    
    private bool CheckForUnsavedThemeChanges()
    {
        if (_originalThemeState == null || !_hasUnsavedThemeChanges)
            return false;
            
        // Check if current theme is a custom theme (not built-in)
        var builtInThemes = ThemePresets.BuiltInThemes.Keys.ToList();
        var isCustomTheme = !builtInThemes.Contains(_currentThemeName, StringComparer.Ordinal);
        
        if (isCustomTheme)
        {
            _showUnsavedCustomThemePrompt = true;
            return true;
        }
        else
        {
            // Show prompt to create new theme for built-in themes
            _showThemeSavePrompt = true;
            return true; // Show prompt, keep window open
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _secretKeysConversionCts?.CancelDispose();
            _secretKeysConversionCts?.Dispose();
            _secretKeysConversionCts = null;
        }
        base.Dispose(disposing);
    }
    
    private void DrawThemeSavePrompt()
    {
        if (!_showThemeSavePrompt)
            return;
        var center = (ImGui.GetMainViewport().Size / 2) + ImGui.GetMainViewport().Pos;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(480, 240), new System.Numerics.Vector2(900, float.MaxValue));
        if (ImGui.Begin("Unsaved Theme Changes", ref _showThemeSavePrompt, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            using (SpheneUI.Theme.ContextMenuScope())
            {
                using var cardBound = SpheneUI.Layout.SuppressBound();
                SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
                ImGui.SameLine();
                SpheneUI.Text.Big("Unsaved Theme Changes");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X8);
                SpheneUI.Text.ColoredWrapped("You have modified theme settings that are not yet saved.", ImGuiColors.DalamudYellow);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Text.ColoredWrapped("Save them as a new theme, keep them temporarily, or discard.", ImGuiColors.DalamudGrey);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);
                SpheneUI.Text.Plain("Theme Name:");
                SpheneUI.Controls.InputText("##NewThemeName", ref _saveThemeName, 100);
                var bottomPadding = 16f * ImGuiHelpers.GlobalScale;
                var spacingX = ImGui.GetStyle().ItemSpacing.X;
                var btn1 = "Save as New Theme";
                var btn2 = "Discard Changes";
                var btn3 = "Keep Changes";
                var w1 = ImGui.CalcTextSize(btn1).X + ImGui.GetStyle().FramePadding.X * 2f;
                var w2 = ImGui.CalcTextSize(btn2).X + ImGui.GetStyle().FramePadding.X * 2f;
                var w3 = ImGui.CalcTextSize(btn3).X + ImGui.GetStyle().FramePadding.X * 2f;
                var totalButtonsWidth = w1 + w2 + w3 + spacingX * 2f;
                var contentWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                var startX = System.Math.Max(0f, (contentWidth - totalButtonsWidth) / 2f);
                var availY = ImGui.GetContentRegionAvail().Y;
                var buttonHeight = ImGui.GetFrameHeight();
                var offsetY = System.Math.Max(0f, availY - buttonHeight - bottomPadding);
                ImGui.Dummy(new System.Numerics.Vector2(1, offsetY));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + startX);
                if (SpheneUI.Controls.Button(btn1) && !string.IsNullOrWhiteSpace(_saveThemeName))
                {
                    var themeNameToSave = _saveThemeName;
                    _ = Task.Run(async () =>
                    {
                        var success = await ThemeManager.SaveTheme(SpheneTheme.Current, themeNameToSave).ConfigureAwait(false);
                        if (success)
                        {
                            _logger.LogDebug("Created new custom theme: {ThemeName}", themeNameToSave);
                            _currentThemeName = themeNameToSave;
                            ThemeManager.SetSelectedTheme(themeNameToSave);
                            _hasUnsavedThemeChanges = false;
                        }
                    });
                    _saveThemeName = "";
                    _showThemeSavePrompt = false;
                    IsOpen = false;
                }
                ImGui.SameLine(0, spacingX);
                if (SpheneUI.Controls.Button(btn2))
                {
                    var builtInThemes = ThemePresets.BuiltInThemes.Keys.ToList();
                    var themeToReload = string.IsNullOrEmpty(_currentThemeName) ? ThemeManager.GetSelectedTheme() : _currentThemeName;
                    if (!string.IsNullOrEmpty(themeToReload))
                    {
                        ApplySelectedPresetTheme(themeToReload, builtInThemes);
                    }
                    _hasUnsavedThemeChanges = false;
                    _showThemeSavePrompt = false;
                    IsOpen = false;
                }
                ImGui.SameLine(0, spacingX);
                if (SpheneUI.Controls.Button(btn3))
                {
                    _hasUnsavedThemeChanges = false;
                    _showThemeSavePrompt = false;
                    IsOpen = false;
                }
            }
            ImGui.End();
        }
    }

    private void DrawUnsavedCustomThemePrompt()
    {
        if (!_showUnsavedCustomThemePrompt)
            return;
        var center2 = (ImGui.GetMainViewport().Size / 2) + ImGui.GetMainViewport().Pos;
        ImGui.SetNextWindowPos(center2, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(480, 240), new System.Numerics.Vector2(900, float.MaxValue));
        if (ImGui.Begin("Unsaved Changes on Custom Theme", ref _showUnsavedCustomThemePrompt, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            using (SpheneUI.Theme.ContextMenuScope())
            {
                using var cardBound = SpheneUI.Layout.SuppressBound();
                SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
                ImGui.SameLine();
                SpheneUI.Text.Big("Unsaved Custom Theme Changes");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X8);
                SpheneUI.Text.ColoredWrapped("These changes are not saved to your current custom theme.", ImGuiColors.DalamudYellow);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);
                var bottomPadding2 = 16f * ImGuiHelpers.GlobalScale;
                var spacingX2 = ImGui.GetStyle().ItemSpacing.X;
                var lbl1 = "Save";
                var lbl2 = "Discard Changes";
                var lbl3 = "Keep Changes";
                var sw1 = ImGui.CalcTextSize(lbl1).X + ImGui.GetStyle().FramePadding.X * 2f;
                var sw2 = ImGui.CalcTextSize(lbl2).X + ImGui.GetStyle().FramePadding.X * 2f;
                var sw3 = ImGui.CalcTextSize(lbl3).X + ImGui.GetStyle().FramePadding.X * 2f;
                var tWidth = sw1 + sw2 + sw3 + spacingX2 * 2f;
                var cWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                var sX = System.Math.Max(0f, (cWidth - tWidth) / 2f);
                var aY = ImGui.GetContentRegionAvail().Y;
                var bH = ImGui.GetFrameHeight();
                var offY = System.Math.Max(0f, aY - bH - bottomPadding2);
                ImGui.Dummy(new System.Numerics.Vector2(1, offY));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + sX);
                if (SpheneUI.Controls.Button(lbl1))
                {
                    if (!string.IsNullOrEmpty(_currentThemeName))
                    {
                        var themeNameToSave = _currentThemeName;
                        _ = Task.Run(async () =>
                        {
                            var success = await ThemeManager.SaveTheme(SpheneTheme.Current, themeNameToSave).ConfigureAwait(false);
                            if (success)
                            {
                                _hasUnsavedThemeChanges = false;
                            }
                        });
                    }
                    _showUnsavedCustomThemePrompt = false;
                    IsOpen = false;
                }
                ImGui.SameLine(0, spacingX2);
                if (SpheneUI.Controls.Button(lbl2))
                {
                    var builtInThemes = ThemePresets.BuiltInThemes.Keys.ToList();
                    var themeToReload = string.IsNullOrEmpty(_currentThemeName) ? ThemeManager.GetSelectedTheme() : _currentThemeName;
                    if (!string.IsNullOrEmpty(themeToReload))
                    {
                        ApplySelectedPresetTheme(themeToReload, builtInThemes);
                    }
                    _hasUnsavedThemeChanges = false;
                    _showUnsavedCustomThemePrompt = false;
                    IsOpen = false;
                }
                ImGui.SameLine(0, spacingX2);
                if (SpheneUI.Controls.Button(lbl3))
                {
                    _hasUnsavedThemeChanges = false;
                    _showUnsavedCustomThemePrompt = false;
                    IsOpen = false;
                }
            }
            ImGui.End();
        }
    }

    private void DrawSoundThemeSettings()
    {
        var cfg = _configService.Current;
        bool configChanged = false;

        SpheneUI.Text.ColoredWrapped("Use Game System for in-game SCD sounds, Sphene Default for built-in sounds, or Custom Sound for external WAV/MP3 files.", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        void DrawSoundConfig(string label, NotificationSoundConfig soundConfig, string blockId)
        {
            if (SpheneUI.Controls.CollapsibleSection(label)
                && NotificationSoundOptionBlock.DrawNotificationSoundConfig(
                    _configService, _uiShared, soundConfig, _fileDialogManager, config => _gameSoundManager.PlayNotificationSoundTest(config), blockId))
            {
                configChanged = true;
            }
        }

        DrawSoundConfig("Info Notifications", cfg.InfoNotificationSound, "SettingsInfoSound");
        DrawSoundConfig("Warning Notifications", cfg.WarningNotificationSound, "SettingsWarningSound");
        DrawSoundConfig("Error Notifications", cfg.ErrorNotificationSound, "SettingsErrorSound");
        DrawSoundConfig("Success Notifications", cfg.SuccessNotificationSound, "SettingsSuccessSound");
        DrawSoundConfig("Area-bound Syncshell Notifications", cfg.AreaBoundNotificationSound, "SettingsAreaBoundSound");
        DrawSoundConfig("File Transfer Notifications", cfg.FileTransferNotificationSound, "SettingsFileTransferSound");

        if (configChanged)
        {
            _configService.Save();
        }
    }
}
