using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Sphene.API.Data;
using Sphene.API.Dto.Files;
using Sphene.API.Data.Comparer;
using Sphene.API.Routes;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Handlers;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Styling;
using Sphene.Configuration;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Files;
using Sphene.WebAPI.Files.Models;
using Sphene.WebAPI.SignalR.Utils;
using Sphene.UI.Components;
using Sphene.UI.Theme;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly ChangelogService _changelogService;
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
    private enum SettingsPage
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
        Acknowledgment,
        Debug
    }
    private SettingsPage _activeSettingsPage = SettingsPage.Home;
    private bool _preferPanelThemeTab = false;
    private bool _preferButtonStylesTab = false;


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
        ChangelogService changelogService) : base(logger, mediator, "Network Configuration", performanceCollector)
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
        _changelogService = changelogService;
        _shrinkUVersion = GetShrinkUAssemblyVersion();
        AllowClickthrough = false;
        AllowPinning = false;
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
        _originalThemeState = SpheneCustomTheme.CurrentTheme.Clone();
        _currentThemeName = ThemeManager.GetSelectedTheme();
        _hasUnsavedThemeChanges = false;
        
        // Subscribe to theme changes to mark unsaved changes across all tabs
        SpheneCustomTheme.CurrentTheme.ThemeChanged += OnThemeChanged;
    }

    public override void OnClose()
    {
        // Unsubscribe theme change tracking
        SpheneCustomTheme.CurrentTheme.ThemeChanged -= OnThemeChanged;
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
        if (SpheneCustomTheme.CurrentTheme.ShowProgressBarPreview)
        {
            SpheneCustomTheme.CurrentTheme.ShowProgressBarPreview = false;
            _configService.Save();
        }
        if (SpheneCustomTheme.CurrentTheme.ShowTransmissionPreview)
        {
            SpheneCustomTheme.CurrentTheme.ShowTransmissionPreview = false;
        }
        if (SpheneCustomTheme.CurrentTheme.ForceShowUpdateHint)
        {
            SpheneCustomTheme.CurrentTheme.ForceShowUpdateHint = false;
        }
        Sphene.UI.Theme.ButtonStyleManagerUI.DisablePicker();

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
            Converters = { new Sphene.UI.Theme.Vector4JsonConverter(), new Sphene.UI.Theme.Vector2JsonConverter() }
        };
        var currentClone = SpheneCustomTheme.CurrentTheme.Clone();
        currentClone.ForceShowUpdateHint = _originalThemeState.ForceShowUpdateHint;
        var currentJson = JsonSerializer.Serialize(currentClone, options);
        var originalJson = JsonSerializer.Serialize(_originalThemeState, options);
        return !string.Equals(currentJson, originalJson, StringComparison.Ordinal);
    }

    protected override void DrawInternal()
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

        DrawSettingsContent();
        
        // Draw theme save prompt if needed
        DrawThemeSavePrompt();
        DrawUnsavedCustomThemePrompt();
    }
    

    private void DrawBlockedTransfers()
    {
        _lastTab = "BlockedTransfers";
        UiSharedService.ColorTextWrapped("Files that you attempted to upload or download that were forbidden to be transferred by their creators will appear here. " +
                             "If you see file paths from your drive here, then those files were not allowed to be uploaded. If you see hashes, those files were not allowed to be downloaded. " +
                             "Ask your paired friend to send you the mod in question through other means, acquire the mod yourself or pester the mod creator to allow it to be sent over Sphene.",
            ImGuiColors.DalamudGrey);

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
                    ImGui.TextUnformatted(transfer.LocalFile);
                }
                else
                {
                    ImGui.TextUnformatted(item.Hash);
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ForbiddenBy);
            }
            ImGui.EndTable();
        }
    }

    private void DrawCurrentTransfers()
    {
        _lastTab = "Transfers";
        _uiShared.BigText("Transfers & Limits");

        int maxParallelDownloads = _configService.Current.ParallelDownloads;
        bool useAlternativeUpload = _configService.Current.UseAlternativeFileUpload;
        int downloadSpeedLimit = _configService.Current.DownloadSpeedLimitInBytes;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Global Receive Limit");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("###speedlimit", ref downloadSpeedLimit))
        {
            _configService.Current.DownloadSpeedLimitInBytes = downloadSpeedLimit;
            _configService.Save();
            Mediator.Publish(new DownloadLimitChangedMessage());
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        _uiShared.DrawCombo("###speed", [DownloadSpeeds.Bps, DownloadSpeeds.KBps, DownloadSpeeds.MBps],
            (s) => s switch
            {
                DownloadSpeeds.Bps => "Byte/s",
                DownloadSpeeds.KBps => "KB/s",
                DownloadSpeeds.MBps => "MB/s",
                _ => throw new NotSupportedException()
            }, (s) =>
            {
                _configService.Current.DownloadSpeedType = s;
                _configService.Save();
                Mediator.Publish(new DownloadLimitChangedMessage());
            }, _configService.Current.DownloadSpeedType);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("0 = No limit/infinite");

        if (ImGui.SliderInt("Maximum Parallel Data Streams", ref maxParallelDownloads, 1, 10))
        {
            _configService.Current.ParallelDownloads = maxParallelDownloads;
            _configService.Save();
        }

        bool allowPenumbraMods = _configService.Current.AllowReceivingPenumbraMods;
        if (ImGui.Checkbox("Allow receiving Penumbra mod packages", ref allowPenumbraMods))
        {
            _configService.Current.AllowReceivingPenumbraMods = allowPenumbraMods;
            _configService.Save();
            _ = ApiController.UserUpdatePenumbraReceivePreference(allowPenumbraMods);
        }
        _uiShared.DrawHelpText("When disabled, incoming Penumbra mod packages are ignored and no install popups are shown.");

        if (ImGui.Checkbox("Use Alternative Transmission Method", ref useAlternativeUpload))
        {
            _configService.Current.UseAlternativeFileUpload = useAlternativeUpload;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Attempts a single-shot transmission instead of streaming. Not usually required; enable only if you encounter transfer issues.");

        ImGui.Separator();
        _uiShared.BigText("Transfer Monitor");

        bool showTransferWindow = _configService.Current.ShowTransferWindow;
        if (ImGui.Checkbox("Show separate transmission monitor", ref showTransferWindow))
        {
            _configService.Current.ShowTransferWindow = showTransferWindow;
            _configService.Save();
        }
        _uiShared.DrawHelpText($"The transmission monitor displays current progress of active data streams.{Environment.NewLine}{Environment.NewLine}" +
            $"Status indicators:{Environment.NewLine}W = Waiting for Slot (see Maximum Parallel Data Streams){Environment.NewLine}" +
            $"Q = Queued on Network Node, awaiting signal{Environment.NewLine}" +
            $"P = Processing transmission (receiving data){Environment.NewLine}" +
            $"D = Decompressing received data");
        if (!_configService.Current.ShowTransferWindow) ImGui.BeginDisabled();
        ImGui.Indent();
        bool editTransferWindowPosition = _uiShared.EditTrackerPosition;
        if (ImGui.Checkbox("Edit Transmission Monitor position", ref editTransferWindowPosition))
        {
            _uiShared.EditTrackerPosition = editTransferWindowPosition;
        }
        ImGui.Unindent();
        if (!_configService.Current.ShowTransferWindow) ImGui.EndDisabled();

        bool showTransferBars = _configService.Current.ShowTransferBars;
        if (ImGui.Checkbox("Show transmission indicators below players", ref showTransferBars))
        {
            _configService.Current.ShowTransferBars = showTransferBars;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render a progress indicator during data reception at the feet of the connected player.");

        if (!showTransferBars) ImGui.BeginDisabled();
        ImGui.Indent();
        bool transferBarShowText = _configService.Current.TransferBarsShowText;
        if (ImGui.Checkbox("Show Transmission Text", ref transferBarShowText))
        {
            _configService.Current.TransferBarsShowText = transferBarShowText;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Shows transmission text (amount of MiB received) in the progress indicators");
        int transferBarWidth = _configService.Current.TransferBarsWidth;
        if (ImGui.SliderInt("Transmission Indicator Width", ref transferBarWidth, 10, 500))
        {
            _configService.Current.TransferBarsWidth = transferBarWidth;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Width of the displayed transmission indicators (will never be less wide than the displayed text)");
        int transferBarHeight = _configService.Current.TransferBarsHeight;
        if (ImGui.SliderInt("Transmission Indicator Height", ref transferBarHeight, 2, 50))
        {
            _configService.Current.TransferBarsHeight = transferBarHeight;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Height of the displayed transmission indicators (will never be less tall than the displayed text)");
        bool showUploading = _configService.Current.ShowUploading;
        if (ImGui.Checkbox("Show 'Transmitting' text below players that are currently transmitting", ref showUploading))
        {
            _configService.Current.ShowUploading = showUploading;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render a 'Transmitting' text at the feet of the player that is in progress of transmitting data.");

        ImGui.Unindent();
        if (!showUploading) ImGui.BeginDisabled();
        ImGui.Indent();
        bool showUploadingBigText = _configService.Current.ShowUploadingBigText;
        if (ImGui.Checkbox("Large font for 'Transmitting' text", ref showUploadingBigText))
        {
            _configService.Current.ShowUploadingBigText = showUploadingBigText;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will render an 'Transferring' text in a larger font.");

        ImGui.Unindent();

        if (!showUploading) ImGui.EndDisabled();
        if (!showTransferBars) ImGui.EndDisabled();

        if (_apiController.IsConnected)
        {
            ImGuiHelpers.ScaledDummy(5);
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(10);
            using var tree = ImRaii.TreeNode("Speed Test to Servers");
            if (tree)
            {
                if ((_downloadServersTask == null || ((_downloadServersTask?.IsCompleted ?? false) && (!_downloadServersTask?.IsCompletedSuccessfully ?? false)))
                    && _uiShared.IconTextButton(FontAwesomeIcon.GroupArrowsRotate, "Update Download Server List"))
                {
                    _downloadServersTask = GetDownloadServerList();
                }
                if (_downloadServersTask != null && _downloadServersTask.IsCompleted && !_downloadServersTask.IsCompletedSuccessfully)
                {
                    UiSharedService.ColorTextWrapped("Failed to get download servers from service, see /xllog for more information", ImGuiColors.DalamudRed);
                }
                if (_downloadServersTask != null && _downloadServersTask.IsCompleted && _downloadServersTask.IsCompletedSuccessfully)
                {
                    if (_speedTestTask == null || _speedTestTask.IsCompleted)
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowRight, "Start Speedtest"))
                        {
                            _speedTestTask = RunSpeedTest(_downloadServersTask.Result!, _speedTestCts?.Token ?? CancellationToken.None);
                        }
                    }
                    else if (!_speedTestTask.IsCompleted)
                    {
                        UiSharedService.ColorTextWrapped("Running Speedtest to File Servers...", ImGuiColors.DalamudYellow);
                        UiSharedService.ColorTextWrapped("Please be patient, depending on usage and load this can take a while.", ImGuiColors.DalamudYellow);
                        if (_uiShared.IconTextButton(FontAwesomeIcon.Ban, "Cancel speedtest"))
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
                                UiSharedService.TextWrapped(result);
                            }
                        }
                        else
                        {
                            UiSharedService.ColorTextWrapped("Speedtest completed with no results", ImGuiColors.DalamudYellow);
                        }
                    }
                }
            }
            ImGuiHelpers.ScaledDummy(10);
        }

        ImGui.Separator();
        _uiShared.BigText("Current Transfers");

        if (ImGui.BeginTabBar("TransfersTabBar"))
        {
            if (ApiController.ServerState is ServerState.Connected && ImGui.BeginTabItem("Transfers"))
            {
                ImGui.TextUnformatted("Uploads");
                if (ImGui.BeginTable("UploadsTable", 3, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Uploaded", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableHeadersRow();
                    foreach (var transfer in _fileTransferManager.CurrentUploads.ToArray())
                    {
                        var color = UiSharedService.UploadColor((transfer.Transferred, transfer.Total));
                        var col = ImRaii.PushColor(ImGuiCol.Text, color);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(transfer.Hash);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(UiSharedService.ByteToString(transfer.Transferred));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(UiSharedService.ByteToString(transfer.Total));
                        col.Dispose();
                        ImGui.TableNextRow();
                    }

                    ImGui.EndTable();
                }
                ImGui.Separator();
                ImGui.TextUnformatted("Downloads");
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
                            var color = UiSharedService.UploadColor((entry.Value.TransferredBytes, entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(userName);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry.Key);
                            var col = ImRaii.PushColor(ImGuiCol.Text, color);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry.Value.TransferredFiles + "/" + entry.Value.TotalFiles);
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(UiSharedService.ByteToString(entry.Value.TransferredBytes) + "/" + UiSharedService.ByteToString(entry.Value.TotalBytes));
                            ImGui.TableNextColumn();
                            col.Dispose();
                            ImGui.TableNextRow();
                        }
                    }

                    ImGui.EndTable();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Blocked Transfers"))
            {
                DrawBlockedTransfers();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
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
        string title;
#if IS_TEST_BUILD
        pageLabel = "Debug Diagnostics";
        title = "Debug Build & Diagnostics";
#else
        pageLabel = "Diagnostics";
        title = "Diagnostics";
#endif

        _lastTab = pageLabel;
        _uiShared.BigText(title);
#if IS_TEST_BUILD
        if (LastCreatedCharacterData != null && ImGui.TreeNode("Last created character data"))
        {
            var lastCreatedJson = JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true });
            if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "Copy to clipboard##lastCreatedCharacterData"))
            {
                ImGui.SetClipboardText(lastCreatedJson);
            }
            foreach (var l in lastCreatedJson.Split('\n'))
            {
                ImGui.TextUnformatted($"{l}");
            }

            ImGui.TreePop();
        }
#endif
        if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "[DIAGNOSTIC] Copy Last created Character Data to clipboard"))
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
        UiSharedService.AttachToolTip("Use this when reporting modifications being rejected from the Network.");

        _uiShared.DrawCombo("Log Level", Enum.GetValues<LogLevel>(), (l) => l.ToString(), (l) =>
        {
            _configService.Current.LogLevel = l;
            _configService.Save();
        }, _configService.Current.LogLevel);
        _uiShared.DrawHelpText("Controls verbosity of logs written to /xllog and plugin console.");

        ImGuiHelpers.ScaledDummy(5);
        _uiShared.BigText("Performance Metrics");

        bool logPerformance = _configService.Current.LogPerformance;
        if (ImGui.Checkbox("Log Network Performance Metrics", ref logPerformance))
        {
            _configService.Current.LogPerformance = logPerformance;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enabling this can incur a slight performance impact. Extended monitoring is not recommended.");

        using (ImRaii.Disabled(!logPerformance))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Network Metrics to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats();
            }
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Network Metrics (last 60s) to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats(60);
            }
        }

        bool stopWhining = _configService.Current.DebugStopWhining;
        if (ImGui.Checkbox("Do not notify for modified game files or enabled LOD", ref stopWhining))
        {
            _configService.Current.DebugStopWhining = stopWhining;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Having modified game files will still mark your logs with UNSUPPORTED and you will not receive Network support, message shown or not." + UiSharedService.TooltipSeparator
            + "Keeping LOD enabled can lead to more crashes. Use at your own risk.");

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(10);
        
        _uiShared.BigText("Diagnostic Windows");
        UiSharedService.TextWrapped("Open diagnostic windows for monitoring and troubleshooting.");
        ImGuiHelpers.ScaledDummy(5);
        
        if (_uiShared.IconTextButton(FontAwesomeIcon.Desktop, "Open Acknowledgment Monitor"))
        {
            Mediator.Publish(new UiToggleMessage(typeof(AcknowledgmentMonitorUI)));
        }
        UiSharedService.AttachToolTip("Opens the Acknowledgment Monitor window for monitoring acknowledgment system status and metrics.");
        
        ImGui.SameLine();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Bug, "Open Status Debug"))
        {
            Mediator.Publish(new UiToggleMessage(typeof(StatusDebugUi)));
        }
        UiSharedService.AttachToolTip("Opens the Status Debug window for connection status monitoring and debugging.");
        

    }

    private void DrawFileStorageSettings()
    {
        _lastTab = "FileCache";

        _uiShared.BigText("Storage & Cache");

        UiSharedService.TextWrapped("Sphene stores downloaded files from paired people permanently. This is to improve loading performance and requiring less downloads. " +
            "The storage governs itself by clearing data beyond the set storage size. Please set the storage size accordingly. It is not necessary to manually clear the storage.");

        _uiShared.DrawFileScanState();
        ImGuiHelpers.ScaledDummy(5);
        _uiShared.BigText("Monitoring");
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Penumbra Folder: " + (_cacheMonitor.PenumbraWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.PenumbraWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("penumbraMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Sphene Storage Folder: " + (_cacheMonitor.SpheneWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.SpheneWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("spheneMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartSpheneWatcher(_configService.Current.CacheFolder);
            }
        }
        if (_cacheMonitor.SpheneWatcher == null || _cacheMonitor.PenumbraWatcher == null)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Play, "Resume Monitoring"))
            {
                _cacheMonitor.StartSpheneWatcher(_configService.Current.CacheFolder);
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
                _cacheMonitor.InvokeScan();
            }
            UiSharedService.AttachToolTip("Resumes monitoring for both Penumbra and Sphene Storage. Also triggers a full rescan and index rebuild." + Environment.NewLine
                + "If the button remains present after clicking, consult /xllog for errors.");
        }
        else
        {
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.Stop, "Stop Monitoring"))
                {
                    _cacheMonitor.StopMonitoring();
                }
            }
            UiSharedService.AttachToolTip("Stops monitoring for both Penumbra and Sphene Storage. "
                + "Only stop monitoring when moving the Penumbra or Sphene Storage folders to maintain correct functionality." + Environment.NewLine
                + "Resume monitoring once you finish moving files." + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button.");
        }

        _uiShared.DrawCacheDirectorySetting();
        _uiShared.DrawHelpText("Configure cache location and size. The storage manages itself by clearing old data beyond the set size.");
        
        ImGuiHelpers.ScaledDummy(5);
        _uiShared.DrawPenumbraModDownloadFolderSetting();

        bool deleteAfterInstall = _configService.Current.DeletePenumbraModAfterInstall;
        if (ImGui.Checkbox("Delete downloaded mods after successful install", ref deleteAfterInstall))
        {
            _configService.Current.DeletePenumbraModAfterInstall = deleteAfterInstall;
            _configService.Save();
        }
        _uiShared.DrawHelpText("If enabled, the downloaded .pmp file will be deleted automatically after it has been successfully imported into Penumbra.");

        ImGui.AlignTextToFramePadding();
        if (_cacheMonitor.FileCacheSize >= 0)
            ImGui.TextUnformatted($"Currently utilized local storage: {UiSharedService.ByteToString(_cacheMonitor.FileCacheSize)}");
        else
            ImGui.TextUnformatted($"Currently utilized local storage: Calculating...");
        ImGui.TextUnformatted($"Remaining space free on drive: {UiSharedService.ByteToString(_cacheMonitor.FileCacheDriveFree)}");
        bool useFileCompactor = _configService.Current.UseCompactor;
        bool isLinux = _dalamudUtilService.IsWine;
        if (!useFileCompactor && !isLinux)
        {
            UiSharedService.ColorTextWrapped("Hint: Consider enabling the File Compactor to reduce disk usage.", ImGuiColors.DalamudYellow);
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Use file compactor", ref useFileCompactor))
        {
            _configService.Current.UseCompactor = useFileCompactor;
            _configService.Save();
        }
        _uiShared.DrawHelpText("The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU." + Environment.NewLine
            + "It is recommended to leave it enabled to save on space.");
        ImGui.SameLine();
        if (!_fileCompactor.MassCompactRunning)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.FileArchive, "Compact all files in storage"))
            {
                _ = Task.Run(() =>
                {
                    _fileCompactor.CompactStorage(compress: true);
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            UiSharedService.AttachToolTip("This will run compression on all files in your current Sphene Storage." + Environment.NewLine
                + "You do not need to run this manually if you keep the file compactor enabled.");
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.File, "Decompact all files in storage"))
            {
                _ = Task.Run(() =>
                {
                    _fileCompactor.CompactStorage(compress: false);
                    _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                });
            }
            UiSharedService.AttachToolTip("This will run decompression on all files in your current Sphene Storage.");
        }
        else
        {
            UiSharedService.ColorText($"File compactor currently running ({_fileCompactor.Progress})", ImGuiColors.DalamudYellow);
        }
        if (isLinux || !_cacheMonitor.StorageisNTFS)
        {
            ImGui.EndDisabled();
            ImGui.TextUnformatted("The file compactor is only available on Windows and NTFS drives.");
        }
        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        ImGui.Separator();
        UiSharedService.TextWrapped("File Storage validation can make sure that all files in your local Sphene Storage are valid. " +
            "Run the validation before you clear the Storage for no reason. " + Environment.NewLine +
            "This operation, depending on how many files you have in your storage, can take a while and will be CPU and drive intensive.");
        using (ImRaii.Disabled(_validationTask != null && !_validationTask.IsCompleted))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Check, "Start File Storage Validation"))
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
            if (_uiShared.IconTextButton(FontAwesomeIcon.Times, "Cancel"))
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
                    UiSharedService.TextWrapped($"The storage validation has completed and removed {_validationTask.Result.Count} invalid files from storage.");
                }
                else
                {

                    UiSharedService.TextWrapped($"Storage validation is running: {_currentProgress.Item1}/{_currentProgress.Item2}");
                    UiSharedService.TextWrapped($"Current item: {_currentProgress.Item3.ResolvedFilepath}");
                }
            }
        }
        ImGui.Separator();

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.TextUnformatted("To clear the local storage accept the following disclaimer");
        ImGui.Indent();
        ImGui.Checkbox("##readClearCache", ref _readClearCache);
        ImGui.SameLine();
        UiSharedService.TextWrapped("I understand that: " + Environment.NewLine + "- By clearing the local storage I put the file servers of my connected service under extra strain by having to redownload all data."
            + Environment.NewLine + "- This is not a step to try to fix sync issues."
            + Environment.NewLine + "- This can make the situation of not getting other players data worse in situations of heavy file server load.");
        if (!_readClearCache)
            ImGui.BeginDisabled();
        if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Clear local storage") && UiSharedService.CtrlPressed() && _readClearCache)
        {
            _ = Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(_configService.Current.CacheFolder))
                {
                    File.Delete(file);
                }
            });
        }
        UiSharedService.AttachToolTip("You normally do not need to do this. THIS IS NOT SOMETHING YOU SHOULD BE DOING TO TRY TO FIX SYNC ISSUES." + Environment.NewLine
            + "This will solely remove all downloaded data from all players and will require you to re-download everything again." + Environment.NewLine
            + "Sphenes storage is self-clearing and will not surpass the limit you have set it to." + Environment.NewLine
            + "If you still think you need to do this hold CTRL while pressing the button.");
        if (!_readClearCache)
            ImGui.EndDisabled();
        ImGui.Unindent();
    }



    private void DrawPerformance()
    {
        _uiShared.BigText("Performance Settings");
        UiSharedService.TextWrapped("The configuration options here are to give you more informed warnings and automation when it comes to other performance-intensive synced players.");
        ImGui.Dummy(new Vector2(10));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(10));
        bool showPerformanceIndicator = _playerPerformanceConfigService.Current.ShowPerformanceIndicator;
        if (ImGui.Checkbox("Show performance indicator", ref showPerformanceIndicator))
        {
            _playerPerformanceConfigService.Current.ShowPerformanceIndicator = showPerformanceIndicator;
            _playerPerformanceConfigService.Save();
        }
        _uiShared.DrawHelpText("Will show a performance indicator when players exceed defined thresholds in Sphenes UI." + Environment.NewLine + "Will use warning thresholds.");
        bool warnOnExceedingThresholds = _playerPerformanceConfigService.Current.WarnOnExceedingThresholds;
        if (ImGui.Checkbox("Warn on loading in players exceeding performance thresholds", ref warnOnExceedingThresholds))
        {
            _playerPerformanceConfigService.Current.WarnOnExceedingThresholds = warnOnExceedingThresholds;
            _playerPerformanceConfigService.Save();
        }
        _uiShared.DrawHelpText("Sphene will print a warning in chat once per session of meeting those people. Will not warn on players with preferred permissions.");
        using (ImRaii.Disabled(!warnOnExceedingThresholds && !showPerformanceIndicator))
        {
            using var indent = ImRaii.PushIndent();
            var warnOnPref = _playerPerformanceConfigService.Current.WarnOnPreferredPermissionsExceedingThresholds;
            if (ImGui.Checkbox("Warn/Indicate also on players with preferred permissions", ref warnOnPref))
            {
                _playerPerformanceConfigService.Current.WarnOnPreferredPermissionsExceedingThresholds = warnOnPref;
                _playerPerformanceConfigService.Save();
            }
            _uiShared.DrawHelpText("Sphene will also print warnings and show performance indicator for players where you enabled preferred permissions. If warning in general is disabled, this will not produce any warnings.");
        }
        using (ImRaii.Disabled(!showPerformanceIndicator && !warnOnExceedingThresholds))
        {
            var vram = _playerPerformanceConfigService.Current.VRAMSizeWarningThresholdMiB;
            var tris = _playerPerformanceConfigService.Current.TrisWarningThresholdThousands;
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Warning VRAM threshold", ref vram))
            {
                _playerPerformanceConfigService.Current.VRAMSizeWarningThresholdMiB = vram;
                _playerPerformanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(MiB)");
            _uiShared.DrawHelpText("Limit in MiB of approximate VRAM usage to trigger warning or performance indicator on UI." + UiSharedService.TooltipSeparator
                + "Default: 375 MiB");
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Warning Triangle threshold", ref tris))
            {
                _playerPerformanceConfigService.Current.TrisWarningThresholdThousands = tris;
                _playerPerformanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            _uiShared.DrawHelpText("Limit in approximate used triangles from mods to trigger warning or performance indicator on UI." + UiSharedService.TooltipSeparator
                + "Default: 165 thousand");
        }
        ImGui.Dummy(new Vector2(10));
        bool autoPause = _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds;
        bool autoPauseEveryone = _playerPerformanceConfigService.Current.AutoPausePlayersWithPreferredPermissionsExceedingThresholds;
        if (ImGui.Checkbox("Automatically pause players exceeding thresholds", ref autoPause))
        {
            _playerPerformanceConfigService.Current.AutoPausePlayersExceedingThresholds = autoPause;
            _playerPerformanceConfigService.Save();
        }
        _uiShared.DrawHelpText("When enabled, it will automatically pause all players without preferred permissions that exceed the thresholds defined below." + Environment.NewLine
            + "Will print a warning in chat when a player got paused automatically."
            + UiSharedService.TooltipSeparator + "Warning: this will not automatically unpause those people again, you will have to do this manually.");
        using (ImRaii.Disabled(!autoPause))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Automatically pause also players with preferred permissions", ref autoPauseEveryone))
            {
                _playerPerformanceConfigService.Current.AutoPausePlayersWithPreferredPermissionsExceedingThresholds = autoPauseEveryone;
                _playerPerformanceConfigService.Save();
            }
            _uiShared.DrawHelpText("When enabled, will automatically pause all players regardless of preferred permissions that exceed thresholds defined below." + UiSharedService.TooltipSeparator +
                "Warning: this will not automatically unpause those people again, you will have to do this manually.");
            var vramAuto = _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB;
            var trisAuto = _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands;
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Auto Pause VRAM threshold", ref vramAuto))
            {
                _playerPerformanceConfigService.Current.VRAMSizeAutoPauseThresholdMiB = vramAuto;
                _playerPerformanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(MiB)");
            _uiShared.DrawHelpText("When a loading in player and their VRAM usage exceeds this amount, automatically pauses the synced player." + UiSharedService.TooltipSeparator
                + "Default: 550 MiB");
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Auto Pause Triangle threshold", ref trisAuto))
            {
                _playerPerformanceConfigService.Current.TrisAutoPauseThresholdThousands = trisAuto;
                _playerPerformanceConfigService.Save();
            }
            ImGui.SameLine();
            ImGui.Text("(thousand triangles)");
            _uiShared.DrawHelpText("When a loading in player and their triangle count exceeds this amount, automatically pauses the synced player." + UiSharedService.TooltipSeparator
                + "Default: 250 thousand");
        }
        ImGui.Dummy(new Vector2(10));
        _uiShared.BigText("Whitelisted UIDs");
        UiSharedService.TextWrapped("The entries in the list below will be ignored for all warnings and auto pause operations.");
        ImGui.Dummy(new Vector2(10));
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("##ignoreuid", ref _uidToAddForIgnore, 20);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrEmpty(_uidToAddForIgnore)))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add UID/Vanity ID to whitelist"))
            {
                if (!_playerPerformanceConfigService.Current.UIDsToIgnore.Contains(_uidToAddForIgnore, StringComparer.Ordinal))
                {
                    _playerPerformanceConfigService.Current.UIDsToIgnore.Add(_uidToAddForIgnore);
                    _playerPerformanceConfigService.Save();
                }
                _uidToAddForIgnore = string.Empty;
            }
        }
        _uiShared.DrawHelpText("Hint: UIDs are case sensitive.");
        var playerList = _playerPerformanceConfigService.Current.UIDsToIgnore;
        ImGui.SetNextItemWidth(400 * ImGuiHelpers.GlobalScale);
        using (var lb = ImRaii.ListBox("UID whitelist"))
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
                    
                    var displayText = identifier;
                    if (pair != null)
                    {
                        var note = pair.GetNote();
                        if (!string.IsNullOrEmpty(note))
                        {
                            displayText = $"{identifier} ({note})";
                        }
                        else if (!string.IsNullOrEmpty(pair.UserData.Alias))
                        {
                            displayText = $"{identifier} ({pair.UserData.Alias})";
                        }
                    }
                    
                    if (ImGui.Selectable(displayText + "##" + i, shouldBeSelected))
                    {
                        _selectedEntry = i;
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedEntry == -1))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete selected UID"))
            {
                _playerPerformanceConfigService.Current.UIDsToIgnore.RemoveAt(_selectedEntry);
                _selectedEntry = -1;
                _playerPerformanceConfigService.Save();
            }
        }
    }

    private void DrawServerConfiguration()
    {
        _lastTab = "Network Configuration";
        if (ApiController.ServerAlive)
        {
            _uiShared.BigText("Service Actions");
            ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
            if (ImGui.Button("Delete all my files"))
            {
                _deleteFilesPopupModalShown = true;
                ImGui.OpenPopup("Delete all your files?");
            }

            _uiShared.DrawHelpText("Completely deletes all your uploaded files on the service.");

            if (ImGui.BeginPopupModal("Delete all your files?", ref _deleteFilesPopupModalShown, UiSharedService.PopupWindowFlags))
            {
                using (SpheneCustomTheme.ApplyContextMenuTheme())
                {
                UiSharedService.TextWrapped(
                    "All your own uploaded files on the service will be deleted.\nThis operation cannot be undone.");
                ImGui.TextUnformatted("Are you sure you want to continue?");
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                 ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button("Delete everything", new Vector2(buttonSize, 0)))
                {
                    _ = Task.Run(_fileTransferManager.DeleteAllFiles);
                    _deleteFilesPopupModalShown = false;
                }

                ImGui.SameLine();

                if (ImGui.Button("Cancel##cancelDelete", new Vector2(buttonSize, 0)))
                {
                    _deleteFilesPopupModalShown = false;
                }

                UiSharedService.SetScaledWindowSize(325);
                }
                ImGui.EndPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete account"))
            {
                _deleteAccountPopupModalShown = true;
                ImGui.OpenPopup("Delete your account?");
            }

            _uiShared.DrawHelpText("Completely deletes your account and all uploaded files to the service.");

            if (ImGui.BeginPopupModal("Delete your account?", ref _deleteAccountPopupModalShown, UiSharedService.PopupWindowFlags))
            {
                using (SpheneCustomTheme.ApplyContextMenuTheme())
                {
                UiSharedService.TextWrapped(
                    "Your account and all associated files and data on the service will be deleted.");
                UiSharedService.TextWrapped("Your UID will be removed from all pairing lists.");
                ImGui.TextUnformatted("Are you sure you want to continue?");
                ImGui.Separator();
                ImGui.Spacing();

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (ImGui.Button("Delete account", new Vector2(buttonSize, 0)))
                {
                    _ = Task.Run(ApiController.UserDelete);
                    _deleteAccountPopupModalShown = false;
                    Mediator.Publish(new SwitchToIntroUiMessage());
                }

                ImGui.SameLine();

                if (ImGui.Button("Cancel##cancelDelete", new Vector2(buttonSize, 0)))
                {
                    _deleteAccountPopupModalShown = false;
                }

                UiSharedService.SetScaledWindowSize(325);
                }
                ImGui.EndPopup();
            }
            ImGui.Separator();
        }

        _uiShared.BigText("Service & Character Settings");
        ImGuiHelpers.ScaledDummy(new Vector2(5, 5));
        var sendCensus = _serverConfigurationManager.SendCensusData;
        if (ImGui.Checkbox("Send Statistical Census Data", ref sendCensus))
        {
            _serverConfigurationManager.SendCensusData = sendCensus;
        }
        _uiShared.DrawHelpText("This will allow sending census data to the currently connected service." + UiSharedService.TooltipSeparator
            + "Census data contains:" + Environment.NewLine
            + "- Current World" + Environment.NewLine
            + "- Current Gender" + Environment.NewLine
            + "- Current Race" + Environment.NewLine
            + "- Current Clan (this is not your Free Company, this is e.g. Keeper or Seeker for Miqo'te)" + UiSharedService.TooltipSeparator
            + "The census data is only saved temporarily and will be removed from the server on disconnect. It is stored temporarily associated with your UID while you are connected." + UiSharedService.TooltipSeparator
            + "If you do not wish to participate in the statistical census, untick this box and reconnect to the server.");
        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        int idx = _uiShared.DrawServiceSelection();
        if (_lastSelectedServerIndex != idx)
        {
            _uiShared.ResetOAuthTasksState();
            _secretKeysConversionCts = _secretKeysConversionCts.CancelRecreate();
            _secretKeysConversionTask = null;
            _lastSelectedServerIndex = idx;
        }

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        var selectedServer = _serverConfigurationManager.GetServerByIndex(idx);
        if (selectedServer == _serverConfigurationManager.CurrentServer)
        {
            UiSharedService.ColorTextWrapped("For any changes to be applied to the current service you need to reconnect to the service.", ImGuiColors.DalamudYellow);
        }

        bool useOauth = selectedServer.UseOAuth2;

        if (ImGui.BeginTabBar("serverTabBar"))
        {
            // Overview tab: quick status and actions
            if (ImGui.BeginTabItem("Overview"))
            {
                _uiShared.BigText("Network Overview");
                UiSharedService.TextWrapped($"Service: {selectedServer.ServerName}");
                UiSharedService.TextWrapped($"URI: {selectedServer.ServerUri}");
                UiSharedService.TextWrapped($"Status: {_apiController.ServerState}");
                ImGuiHelpers.ScaledDummy(5f);
                if (_apiController.IsConnected)
                {
                    if (ImGui.Button("Disconnect from Service") && _serverConfigurationManager.CurrentServer != null)
                    {
                        _serverConfigurationManager.CurrentServer.FullPause = true;
                        _serverConfigurationManager.Save();
                        _ = _uiShared.ApiController.CreateConnectionsAsync();
                    }
                    _uiShared.DrawHelpText("Disconnect the current session from the selected service.");
                }
                else
                {
                    if (ImGui.Button("Connect to Service") && _serverConfigurationManager.CurrentServer != null)
                    {
                        _serverConfigurationManager.CurrentServer.FullPause = false;
                        _serverConfigurationManager.Save();
                        _ = _uiShared.ApiController.CreateConnectionsAsync();
                    }
                    _uiShared.DrawHelpText("Establish a connection to the selected service.");
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Character Management"))
            {
                // Primary authentication & character linking
                if (selectedServer.SecretKeys.Any() || useOauth)
                {
                    UiSharedService.ColorTextWrapped("Characters listed here will automatically connect to the selected Sphene service with the settings as provided below." +
                        " Make sure to enter the character names correctly or use the 'Add current character' button at the bottom.", ImGuiColors.DalamudYellow);
                    int i = 0;
                    _uiShared.DrawUpdateOAuthUIDsButton(selectedServer);

                    if (selectedServer.UseOAuth2 && !string.IsNullOrEmpty(selectedServer.OAuthToken))
                    {
                        bool hasSetSecretKeysButNoUid = selectedServer.Authentications.Exists(u => u.SecretKeyIdx != -1 && string.IsNullOrEmpty(u.UID));
                        if (hasSetSecretKeysButNoUid)
                        {
                            ImGui.Dummy(new(5f, 5f));
                            UiSharedService.TextWrapped("Some entries have been detected that have previously been assigned secret keys but not UIDs. " +
                                "Press this button below to attempt to convert those entries.");
                            using (ImRaii.Disabled(_secretKeysConversionTask != null && !_secretKeysConversionTask.IsCompleted))
                            {
                                if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsLeftRight, "Try to Convert Secret Keys to UIDs"))
                                {
                                    var token = _secretKeysConversionCts?.Token ?? CancellationToken.None;
                                    _secretKeysConversionTask = ConvertSecretKeysToUIDs(selectedServer, token);
                                }
                            }
                            if (_secretKeysConversionTask != null && !_secretKeysConversionTask.IsCompleted)
                            {
                                UiSharedService.ColorTextWrapped("Converting Secret Keys to UIDs", ImGuiColors.DalamudYellow);
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
                                    UiSharedService.TextWrapped(text);
                                }
                                else
                                {
                                    UiSharedService.ColorTextWrapped(text, textColor!.Value);
                                }
                                if (!_secretKeysConversionTask.Result.Success || _secretKeysConversionTask.Result.PartialSuccess)
                                {
                                    UiSharedService.TextWrapped("In case of conversion failures, please set the UIDs for the failed conversions manually.");
                                }
                            }
                        }
                    }
                    ImGui.Separator();
                    string youName = _dalamudUtilService.GetPlayerName();
                    uint youWorld = _dalamudUtilService.GetHomeWorldId();
                    ulong youCid = _dalamudUtilService.GetCID();
                    if (!selectedServer.Authentications.Exists(a => string.Equals(a.CharacterName, youName, StringComparison.Ordinal) && a.WorldId == youWorld))
                    {
                        _uiShared.BigText("Your Character is not Configured", ImGuiColors.DalamudRed);
                        UiSharedService.ColorTextWrapped("You have currently no character configured that corresponds to your current name and world.", ImGuiColors.DalamudRed);
                        var authWithCid = selectedServer.Authentications.Find(f => f.LastSeenCID == youCid);
                        if (authWithCid != null)
                        {
                            ImGuiHelpers.ScaledDummy(5);
                            UiSharedService.ColorText("A potential rename/world change from this character was detected:", ImGuiColors.DalamudYellow);
                            using (ImRaii.PushIndent(10f))
                                UiSharedService.ColorText("Entry: " + authWithCid.CharacterName + " - " + _dalamudUtilService.WorldData.Value[(ushort)authWithCid.WorldId], ImGuiColors.ParsedGreen);
                            UiSharedService.ColorText("Press the button below to adjust that entry to your current character:", ImGuiColors.DalamudYellow);
                            using (ImRaii.PushIndent(10f))
                                UiSharedService.ColorText("Current: " + youName + " - " + _dalamudUtilService.WorldData.Value[(ushort)youWorld], ImGuiColors.ParsedGreen);
                            ImGuiHelpers.ScaledDummy(5);
                            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowRight, "Update Entry to Current Character"))
                            {
                                authWithCid.CharacterName = youName;
                                authWithCid.WorldId = youWorld;
                                _serverConfigurationManager.Save();
                            }
                        }
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.Separator();
                        ImGuiHelpers.ScaledDummy(5);
                    }
                    foreach (var item in selectedServer.Authentications.ToList())
                    {
                        using var charaId = ImRaii.PushId("selectedChara" + i);

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
                        Vector4 color = ImGuiColors.ParsedGreen;
                        string text = thisIsYou ? "Your Current Character" : string.Empty;
                        if (misManaged)
                        {
                            text += " [MISMANAGED (" + (selectedServer.UseOAuth2 ? "No UID Set" : "No Secret Key Set") + ")]";
                            color = ImGuiColors.DalamudRed;
                        }
                        if (selectedServer.Authentications.Where(e => e != item).Any(e => string.Equals(e.CharacterName, item.CharacterName, StringComparison.Ordinal)
                            && e.WorldId == item.WorldId))
                        {
                            text += " [DUPLICATE]";
                            color = ImGuiColors.DalamudRed;
                        }

                        if (!string.IsNullOrEmpty(text))
                        {
                            text = text.Trim();
                            _uiShared.BigText(text, color);
                        }

                        var charaName = item.CharacterName;
                        if (ImGui.InputText("Character Name", ref charaName, 64))
                        {
                            item.CharacterName = charaName;
                            _serverConfigurationManager.Save();
                        }

                        _uiShared.DrawCombo("World##" + item.CharacterName + i, data, (w) => w.Value,
                            (w) =>
                            {
                                if (item.WorldId != w.Key)
                                {
                                    item.WorldId = w.Key;
                                    _serverConfigurationManager.Save();
                                }
                            }, EqualityComparer<KeyValuePair<ushort, string>>.Default.Equals(data.FirstOrDefault(f => f.Key == worldIdx), default) ? data.First() : data.First(f => f.Key == worldIdx));

                        if (!useOauth)
                        {
                            _uiShared.DrawCombo("Secret Key###" + item.CharacterName + i, keys, (w) => w.Value.FriendlyName,
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
                        bool isAutoLogin = item.AutoLogin;
                        if (ImGui.Checkbox("Automatically login to Sphene", ref isAutoLogin))
                        {
                            item.AutoLogin = isAutoLogin;
                            _serverConfigurationManager.Save();
                        }
                        _uiShared.DrawHelpText("When enabled and logging into this character in XIV, Sphene will automatically connect to the current service.");
                        if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete Character") && UiSharedService.CtrlPressed())
                            _serverConfigurationManager.RemoveCharacterFromServer(idx, item);
                        UiSharedService.AttachToolTip("Hold CTRL to delete this entry.");

                        i++;
                        if (item != selectedServer.Authentications.ToList()[^1])
                        {
                            ImGuiHelpers.ScaledDummy(5);
                            ImGui.Separator();
                            ImGuiHelpers.ScaledDummy(5);
                        }
                    }

                    if (selectedServer.Authentications.Any())
                        ImGui.Separator();

                    if (!selectedServer.Authentications.Exists(c => string.Equals(c.CharacterName, youName, StringComparison.Ordinal)
                        && c.WorldId == youWorld))
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.User, "Add current character"))
                        {
                            _serverConfigurationManager.AddCurrentCharacterToServer(idx);
                        }
                        ImGui.SameLine();
                    }

                    if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add new character"))
                    {
                        _serverConfigurationManager.AddEmptyCharacterToServer(idx);
                    }
                }
                else
                {
                    UiSharedService.ColorTextWrapped("You need to add a Secret Key first before adding Characters.", ImGuiColors.DalamudYellow);
                    UiSharedService.ColorTextWrapped("Please go to the 'Secret Keys' tab to add authentication keys.", ImGuiColors.DalamudYellow);
                }

                ImGui.EndTabItem();
            }

            // Secret Key management in separate tab (non-OAuth only)
            if (!useOauth && ImGui.BeginTabItem("Secret Keys"))
            {
                _uiShared.BigText("Secret Key Management");
                UiSharedService.ColorTextWrapped("Secret keys are used to authenticate your characters with the Sphene service. Each character must be assigned to a secret key.", ImGuiColors.DalamudYellow);
                ImGuiHelpers.ScaledDummy(5);

                foreach (var item in selectedServer.SecretKeys.ToList())
                {
                    using var id = ImRaii.PushId("key" + item.Key);
                    var friendlyName = item.Value.FriendlyName;
                    if (ImGui.InputText("Secret Key Display Name", ref friendlyName, 255))
                    {
                        item.Value.FriendlyName = friendlyName;
                        _serverConfigurationManager.Save();
                    }
                    var key = item.Value.Key;
                    if (ImGui.InputText("Secret Key", ref key, 64))
                    {
                        item.Value.Key = key;
                        _serverConfigurationManager.Save();
                    }
                    
                    // Show which characters are using this key
                    var charactersUsingKey = selectedServer.Authentications.Where(a => a.SecretKeyIdx == item.Key).ToList();
                    if (charactersUsingKey.Any())
                    {
                        ImGui.TextUnformatted("Used by characters:");
                        ImGui.Indent();
                        foreach (var auth in charactersUsingKey)
                        {
                            ImGui.TextUnformatted($"• {auth.CharacterName}@{_dalamudUtilService.WorldData.Value[(ushort)auth.WorldId]}");
                        }
                        ImGui.Unindent();
                    }
                    
                    if (!selectedServer.Authentications.Exists(p => p.SecretKeyIdx == item.Key))
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Delete Secret Key") && UiSharedService.CtrlPressed())
                        {
                            selectedServer.SecretKeys.Remove(item.Key);
                            _serverConfigurationManager.Save();
                        }
                        UiSharedService.AttachToolTip("Hold CTRL to delete this secret key entry");
                    }
                    else
                    {
                        UiSharedService.ColorTextWrapped("This key is in use and cannot be deleted", ImGuiColors.DalamudYellow);
                    }

                    if (item.Key != selectedServer.SecretKeys.Keys.LastOrDefault())
                        ImGui.Separator();
                }

                ImGui.Separator();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add new Secret Key"))
                {
                    selectedServer.SecretKeys.Add(selectedServer.SecretKeys.Any() ? selectedServer.SecretKeys.Max(p => p.Key) + 1 : 0, new SecretKey()
                    {
                        FriendlyName = "New Secret Key",
                    });
                    _serverConfigurationManager.Save();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Service Configuration"))
            {
                var serverName = selectedServer.ServerName;
                var serverUri = selectedServer.ServerUri;
                var isMain = string.Equals(serverName, ApiController.MainServer, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serverName, "Sphene Test Server", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serverName, "Sphene Debug Server", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(serverName, "Sphene Server", StringComparison.OrdinalIgnoreCase);
                var flags = isMain ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;

                if (ImGui.InputText("Service URI", ref serverUri, 255, flags))
                {
                    selectedServer.ServerUri = serverUri;
                }
                if (isMain)
                {
                    _uiShared.DrawHelpText("You cannot edit the URI of the main service.");
                }

                if (ImGui.InputText("Service Name", ref serverName, 255, flags))
                {
                    selectedServer.ServerName = serverName;
                    _serverConfigurationManager.Save();
                }
                if (isMain)
                {
                    _uiShared.DrawHelpText("You cannot edit the name of the main service.");
                }

                ImGui.SetNextItemWidth(200);
                var serverTransport = _serverConfigurationManager.GetTransport();
                _uiShared.DrawCombo("Server Transport Type", Enum.GetValues<HttpTransportType>().Where(t => t != HttpTransportType.None),
                    (v) => v.ToString(),
                    onSelected: (t) => _serverConfigurationManager.SetTransportType(t),
                    serverTransport);
                _uiShared.DrawHelpText("You normally do not need to change this, if you don't know what this is or what it's for, keep it to WebSockets." + Environment.NewLine
                    + "If you run into connection issues with e.g. VPNs, try ServerSentEvents first before trying out LongPolling." + UiSharedService.TooltipSeparator
                    + "Note: if the server does not support a specific Transport Type it will fall through to the next automatically: WebSockets > ServerSentEvents > LongPolling");

                if (_dalamudUtilService.IsWine)
                {
                    bool forceWebSockets = selectedServer.ForceWebSockets;
                    if (ImGui.Checkbox("[wine only] Force WebSockets", ref forceWebSockets))
                    {
                        selectedServer.ForceWebSockets = forceWebSockets;
                        _serverConfigurationManager.Save();
                    }
                    _uiShared.DrawHelpText("On wine, Sphene will automatically fall back to ServerSentEvents/LongPolling, even if WebSockets is selected. "
                        + "WebSockets are known to crash XIV entirely on wine 8.5 shipped with Dalamud. "
                        + "Only enable this if you are not running wine 8.5." + Environment.NewLine
                        + "Note: If the issue gets resolved at some point this option will be removed.");
                }

                ImGuiHelpers.ScaledDummy(5);

                if (ImGui.Checkbox("Use Discord OAuth2 Authentication", ref useOauth))
                {
                    selectedServer.UseOAuth2 = useOauth;
                    _serverConfigurationManager.Save();
                }
                _uiShared.DrawHelpText("Use Discord OAuth2 Authentication to identify with this server instead of secret keys");
                if (useOauth)
                {
                    _uiShared.DrawOAuth(selectedServer);
                    if (string.IsNullOrEmpty(_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)))
                    {
                        ImGuiHelpers.ScaledDummy(10f);
                        UiSharedService.ColorTextWrapped("You have enabled OAuth2 but it is not linked. Press the buttons Check, then Authenticate to link properly.", ImGuiColors.DalamudRed);
                    }
                    if (!string.IsNullOrEmpty(_serverConfigurationManager.GetDiscordUserFromToken(selectedServer))
                        && selectedServer.Authentications.TrueForAll(u => string.IsNullOrEmpty(u.UID)))
                    {
                        ImGuiHelpers.ScaledDummy(10f);
                        UiSharedService.ColorTextWrapped("You have enabled OAuth2 but no characters configured. Set the correct UIDs for your characters in \"Character Management\".",
                            ImGuiColors.DalamudRed);
                    }
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Permission Settings"))
            {
                _uiShared.BigText("Default Permission Settings");
                if (selectedServer == _serverConfigurationManager.CurrentServer && _apiController.IsConnected)
                {
                    UiSharedService.TextWrapped("Note: The default permissions settings here are not applied retroactively to existing pairs or joined Syncshells.");
                    UiSharedService.TextWrapped("Note: The default permissions settings here are sent and stored on the connected service.");
                    ImGuiHelpers.ScaledDummy(5f);
                    var perms = _apiController.DefaultPermissions!;
                    bool individualIsSticky = perms.IndividualIsSticky;
                    bool disableIndividualSounds = perms.DisableIndividualSounds;
                    bool disableIndividualAnimations = perms.DisableIndividualAnimations;
                    bool disableIndividualVFX = perms.DisableIndividualVFX;
                    if (ImGui.Checkbox("Individually set permissions become preferred permissions", ref individualIsSticky))
                    {
                        perms.IndividualIsSticky = individualIsSticky;
                        _ = _apiController.UserUpdateDefaultPermissions(perms);
                    }
                    _uiShared.DrawHelpText("The preferred attribute means that the permissions to that user will never change through any of your permission changes to Syncshells " +
                        "(i.e. if you have paused one specific user in a Syncshell and they become preferred permissions, then pause and unpause the same Syncshell, the user will remain paused - " +
                        "if a user does not have preferred permissions, it will follow the permissions of the Syncshell and be unpaused)." + Environment.NewLine + Environment.NewLine +
                        "This setting means:" + Environment.NewLine +
                        "  - All new individual pairs get their permissions defaulted to preferred permissions." + Environment.NewLine +
                        "  - All individually set permissions for any pair will also automatically become preferred permissions. This includes pairs in Syncshells." + Environment.NewLine + Environment.NewLine +
                        "It is possible to remove or set the preferred permission state for any pair at any time." + Environment.NewLine + Environment.NewLine +
                        "If unsure, leave this setting off.");
                    ImGuiHelpers.ScaledDummy(3f);

                    if (ImGui.Checkbox("Disable individual pair sounds", ref disableIndividualSounds))
                    {
                        perms.DisableIndividualSounds = disableIndividualSounds;
                        _ = _apiController.UserUpdateDefaultPermissions(perms);
                    }
                    _uiShared.DrawHelpText("This setting will disable sound sync for all new individual pairs.");
                    if (ImGui.Checkbox("Disable individual pair animations", ref disableIndividualAnimations))
                    {
                        perms.DisableIndividualAnimations = disableIndividualAnimations;
                        _ = _apiController.UserUpdateDefaultPermissions(perms);
                    }
                    _uiShared.DrawHelpText("This setting will disable animation sync for all new individual pairs.");
                    if (ImGui.Checkbox("Disable individual pair VFX", ref disableIndividualVFX))
                    {
                        perms.DisableIndividualVFX = disableIndividualVFX;
                        _ = _apiController.UserUpdateDefaultPermissions(perms);
                    }
                    _uiShared.DrawHelpText("This setting will disable VFX sync for all new individual pairs.");
                    ImGuiHelpers.ScaledDummy(5f);
                    bool disableGroundSounds = perms.DisableGroupSounds;
                    bool disableGroupAnimations = perms.DisableGroupAnimations;
                    bool disableGroupVFX = perms.DisableGroupVFX;
                    if (ImGui.Checkbox("Disable Syncshell pair sounds", ref disableGroundSounds))
                    {
                        perms.DisableGroupSounds = disableGroundSounds;
                        _ = _apiController.UserUpdateDefaultPermissions(perms);
                    }
                    _uiShared.DrawHelpText("This setting will disable sound sync for all non-sticky pairs in newly joined syncshells.");
                    if (ImGui.Checkbox("Disable Syncshell pair animations", ref disableGroupAnimations))
                    {
                        perms.DisableGroupAnimations = disableGroupAnimations;
                        _ = _apiController.UserUpdateDefaultPermissions(perms);
                    }
                    _uiShared.DrawHelpText("This setting will disable animation sync for all non-sticky pairs in newly joined syncshells.");
                    if (ImGui.Checkbox("Disable Syncshell pair VFX", ref disableGroupVFX))
                    {
                        perms.DisableGroupVFX = disableGroupVFX;
                        _ = _apiController.UserUpdateDefaultPermissions(perms);
                    }
                    _uiShared.DrawHelpText("This setting will disable VFX sync for all non-sticky pairs in newly joined syncshells.");
                }
                else
                {
                    UiSharedService.ColorTextWrapped("Default Permission Settings unavailable for this service. " +
                        "You need to connect to this service to change the default permissions since they are stored on the service.", ImGuiColors.DalamudYellow);
                }

                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
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

    private void DrawSettingsContent()
    {
        // Split layout: Sidebar navigation + Content pane
        var available = ImGui.GetContentRegionAvail();

        string diagnosticsPageLabel;
#if IS_TEST_BUILD
        diagnosticsPageLabel = "Debug Build";
#else
        diagnosticsPageLabel = "Diagnostics";
#endif
        
        // Calculate optimal sidebar width based on longest button text + reduced padding
        var buttonLabels = new[] { "Home", "Connectivity", "People & Notes", "Appearance", "Theme", "Notifications", "Performance", "Transfers", "Storage", "Acknowledgment", diagnosticsPageLabel };
        var maxTextWidth = 0f;
        foreach (var label in buttonLabels)
        {
            var textSize = ImGui.CalcTextSize(label);
            if (textSize.X > maxTextWidth)
                maxTextWidth = textSize.X;
        }
        var sidebarWidth = (maxTextWidth + 16f) * ImGuiHelpers.GlobalScale; // Reduced padding from 32f to 16f

        // Sidebar
        ImGui.BeginChild("settings-sidebar", new Vector2(sidebarWidth, available.Y), true);

        void SidebarButton(string label, SettingsPage page)
        {
            var buttonSize = new Vector2(-1, 0); // Use full available width
            var isActive = _activeSettingsPage == page;
            if (isActive) ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.ParsedBlue);
            if (ImGui.Button(label, buttonSize))
            {
                _activeSettingsPage = page;
            }
            if (isActive) ImGui.PopStyleColor();
        }

        SidebarButton("Home", SettingsPage.Home);
        SidebarButton("Connectivity", SettingsPage.Connectivity);
        SidebarButton("People & Notes", SettingsPage.PeopleNotes);
        SidebarButton("Appearance", SettingsPage.Display);
        SidebarButton("Theme", SettingsPage.Theme);
        SidebarButton("Notifications", SettingsPage.Alerts);
        SidebarButton("Performance", SettingsPage.Performance);
        SidebarButton("Transfers", SettingsPage.Transfers);
        SidebarButton("Storage", SettingsPage.Storage);
        SidebarButton("Acknowledgment", SettingsPage.Acknowledgment);
        SidebarButton(diagnosticsPageLabel, SettingsPage.Debug);

        ImGui.EndChild();

        ImGui.SameLine();

        // Content pane without horizontal scrolling - content should fit within available width
        ImGui.BeginChild("settings-content", new Vector2(available.X - sidebarWidth - ImGui.GetStyle().ItemSpacing.X, available.Y), false);

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
            case SettingsPage.Acknowledgment:
                DrawAcknowledgmentSettings();
                break;
            case SettingsPage.Debug:
                DrawDebug();
                break;
        }

        ImGui.EndChild();
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
        var theme = SpheneCustomTheme.CurrentTheme;
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

        var drawList = ImGui.GetWindowDrawList();
        var radius = height * 0.5f;
        var bgColor = ImGui.GetColorU32(ImGuiCol.FrameBg);
        drawList.AddRectFilled(position, new Vector2(position.X + width, position.Y + height), bgColor, height * 0.5f);

        var circleX = value ? position.X + width - radius : position.X + radius;
        var circleColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
        drawList.AddCircleFilled(new Vector2(circleX, position.Y + radius), radius - 1.0f, circleColor);

        return value;
    }

    private void DrawPluginListRow(string name, string description, string statusOrVersion, bool isInstalled, bool isEnabled, Func<Task>? onInstallAction = null, Func<Task>? onEnableAction = null, Func<Task>? onDisableAction = null, Action? onOpenSettings = null, string? settingsLabel = null)
    {
        using var id = ImRaii.PushId("PluginRow_" + name);
        var startPos = ImGui.GetCursorScreenPos();
        var rowHeight = 28f * ImGuiHelpers.GlobalScale;
        
        // Use Avail width for alignment, safer than absolute WindowContentRegionMax in groups/children
        var availWidth = ImGui.GetContentRegionAvail().X;
        
        var nameColor = !isInstalled
            ? ImGuiColors.DalamudRed
            : isEnabled
                ? ImGuiColors.ParsedGreen
                : ImGuiColors.DalamudYellow;
        
        var textHeight = ImGui.GetTextLineHeight();
        var textY = startPos.Y + (rowHeight - textHeight) / 2;
        
        ImGui.SetCursorScreenPos(new Vector2(startPos.X + 10, textY));
        ImGui.TextColored(nameColor, name);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(description);
        
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"({statusOrVersion})");
        
        bool showSettings = isInstalled && isEnabled && onOpenSettings != null;
        bool showInstall = !isInstalled && onInstallAction != null;
        bool showToggle = isInstalled && onEnableAction != null && onDisableAction != null;
        bool isInstalling = showInstall && IsPluginInstallInProgress && string.Equals(InstallingPluginName, name, StringComparison.Ordinal);
        bool disableInstall = showInstall && IsPluginInstallInProgress && !isInstalling;

        if (showSettings || showInstall || showToggle)
        {
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

            var btnSize = ImGui.CalcTextSize(displayText) + new Vector2(20, 0);
            var btnHeight = 22f * ImGuiHelpers.GlobalScale;
            var btnY = startPos.Y + (rowHeight - btnHeight) / 2;
            
            var minBtnX = startPos.X + 200;
            var targetBtnX = startPos.X + availWidth - btnSize.X - 10;
            
            if (targetBtnX < minBtnX) targetBtnX = minBtnX;
            
            ImGui.SetCursorScreenPos(new Vector2(targetBtnX, btnY));

            if (showSettings)
            {
                if (ImGui.Button(displayText, new Vector2(btnSize.X, btnHeight)))
                {
                    onOpenSettings!.Invoke();
                }
            }
            else if (showInstall)
            {
                using (ImRaii.Disabled(disableInstall || isInstalling))
                {
                    if (ImGui.Button(displayText, new Vector2(btnSize.X, btnHeight)))
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
                else if (!disableInstall && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Adds repository. Install via Plugin Installer.");
                }
            }
            else if (showToggle)
            {
                var toggleHeight = ImGui.GetFrameHeight();
                var toggleWidth = toggleHeight * 1.8f;
                var toggleY = startPos.Y + (rowHeight - toggleHeight) / 2;
                var toggleTargetX = startPos.X + availWidth - toggleWidth - 10;
                if (toggleTargetX < minBtnX) toggleTargetX = minBtnX;

                ImGui.SetCursorScreenPos(new Vector2(toggleTargetX, toggleY));
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
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Enable or disable plugin.");
                }
            }
        }
        
        // Move cursor past row
        ImGui.SetCursorScreenPos(new Vector2(startPos.X, startPos.Y + rowHeight));
    }


    private static void DrawPluginGroupContainer(Action drawContents)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1); // Foreground

        ImGui.BeginGroup();
        drawContents();
        ImGui.EndGroup();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        // Add padding
        min -= new Vector2(5, 5);
        max += new Vector2(5, 5);
        
        // Ensure max width respects available content region to fix separator alignment
        var availMaxX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X + 5; // +5 to account for padding
        if (max.X < availMaxX) max.X = availMaxX;

        drawList.ChannelsSetCurrent(0); // Background

        drawList.ChannelsMerge();

        // Advance cursor to account for padding
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, max.Y + 10));
    }

    private void DrawOverview()
    {
        _lastTab = "Overview";
        
        // --- Server Connection Section ---        
        // Discord Button (Right Aligned)
        var rightButtonWidth = _uiShared.GetIconTextButtonSize(FontAwesomeIcon.Users, "Join Discord Community");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - rightButtonWidth);
        if (_uiShared.IconTextActionButton(FontAwesomeIcon.Users, "Join Discord Community"))
        {
            Util.OpenLink("https://discord.gg/GbnwsP2XsF");
        }
        UiSharedService.AttachToolTip("Get support, updates, and connect with other users");
        
        var currentServer = _serverConfigurationManager.CurrentServer;
        if (currentServer == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "No server configured");
            ImGui.TextUnformatted("Configure a service under Connectivity to get started.");
        }
        else
        {
            ImGui.TextUnformatted("Current Service:");
            ImGui.SameLine();
            var displayName = currentServer.ServerName;
#if IS_TEST_BUILD
            if (_configService.Current.UseTestServerOverride)
            {
                displayName = "Test Server";
            }
#endif
            ImGui.TextColored(ImGuiColors.ParsedGreen, displayName);

            ImGui.TextUnformatted("Connection State:");
            ImGui.SameLine();
            var connectionState = _apiController.ServerState.ToString();
            var stateColor = _apiController.ServerState switch
            {
                WebAPI.SignalR.Utils.ServerState.Connected => ImGuiColors.ParsedGreen,
                WebAPI.SignalR.Utils.ServerState.Connecting or 
                WebAPI.SignalR.Utils.ServerState.Reconnecting => ImGuiColors.DalamudYellow,
                _ => ImGuiColors.DalamudRed
            };
            ImGui.TextColored(stateColor, connectionState);

#if IS_TEST_BUILD
            var useOverride = _configService.Current.UseTestServerOverride;
            if (ImGui.Checkbox("Use test server override", ref useOverride))
            {
                _configService.Current.UseTestServerOverride = useOverride;
                _configService.Save();
                _ = _uiShared.ApiController.CreateConnectionsAsync();
            }
            UiSharedService.AttachToolTip("When enabled, the client ignores the configured service and connects to the test server URL below. Toggling this setting switches between the main and test server and triggers an immediate reconnect. Only available in Dev/Test builds (disabled on release builds).");
            if (string.IsNullOrWhiteSpace(_configService.Current.TestServerApiUrl))
            {
                _configService.Current.TestServerApiUrl = "ws://test.sphene.online:6000";
                _configService.Save();
            }
            ImGui.SameLine();
            var overrideUrl = _configService.Current.TestServerApiUrl ?? string.Empty;
            if (ImGui.InputText("", ref overrideUrl, 50))
            {
                _configService.Current.TestServerApiUrl = overrideUrl;
                _configService.Save();
            }
#else
            if (_configService.Current.UseTestServerOverride)
            {
                _configService.Current.UseTestServerOverride = false;
                _configService.Save();
            }
#endif
        }
        
        ImGui.Spacing();

        // --- Plugins Section ---
        ImGui.Spacing();

        string GetPluginStatusLabel(UiSharedService.PluginInstallState state)
        {
            return !state.IsInstalled ? "Missing" : state.IsEnabled ? "Detected" : "Deactivated";
        }

        DrawPluginGroupContainer(() => {
            UiSharedService.DrawSectionSeparator("Sphene Plugins");

            // 1. Sphene (Self)
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var versionString = version != null
                ? (version.Revision > 0
                    ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
                    : $"{version.Major}.{version.Minor}.{version.Build}")
                : "Unknown";
                
            DrawPluginListRow("Sphene", "Advanced character synchronization and networking.", versionString, true, true,
                onOpenSettings: () =>
                {
                    _ = Task.Run(async () =>
                    {
                        string? text = null;
                        try
                        {
                            text = await _changelogService.GetChangelogTextForVersionAsync(versionString).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to load changelog text for version {version}", versionString);
                        }
                        Mediator.Publish(new ShowReleaseChangelogMessage(versionString, text, _configService.Current.LastSeenVersionChangelog));
                    });
                },
                settingsLabel: "Release Notes");

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

            UiSharedService.DrawSectionSeparator("Required Plugins");

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

            UiSharedService.DrawSectionSeparator("Optional Plugins");

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


    }


    private void DrawGeneralUserManagement()
    {
        _lastTab = "People & Notes";
        _uiShared.BigText("People & Notes");
        ImGui.Spacing();

        var currentProfile = _configService.Current;
        var overwriteExistingLabels = _overwriteExistingLabels;
        if (ImGui.Button("Export Notes"))
        {
            ImGui.SetClipboardText(UiSharedService.GetNotes(
                _pairManager.DirectPairs.UnionBy(
                    _pairManager.GroupPairs.SelectMany(p => p.Value),
                    p => p.UserData,
                    UserDataComparer.Instance).ToList()));
        }
        ImGui.SameLine();
        if (ImGui.Button("Import Notes"))
        {
            _notesSuccessfullyApplied = null;
            var notes = ImGui.GetClipboardText();
            _notesSuccessfullyApplied = _uiShared.ApplyNotesFromClipboard(notes, overwriteExistingLabels);
        }
        ImGui.SameLine();
        ImGui.Checkbox("Overwrite existing labels", ref overwriteExistingLabels);
        _overwriteExistingLabels = overwriteExistingLabels;

        if (_notesSuccessfullyApplied is not null)
        {
            ImGui.Spacing();
            if (_notesSuccessfullyApplied!.Value)
                ImGui.TextColored(ImGuiColors.ParsedGreen, "Notes successfully applied.");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, "Failed to apply notes.");
        }

        ImGui.Separator();
        _uiShared.BigText("Labels & Popups");
        var openPopupOnAddition = currentProfile.OpenPopupOnAdd;
        if (ImGui.Checkbox("Open Notes Popup on user addition", ref openPopupOnAddition))
        {
            currentProfile.OpenPopupOnAdd = openPopupOnAddition;
            _configService.Save();
        }

        var autoPopulateNotes = currentProfile.AutoPopulateEmptyNotesFromCharaName;
        if (ImGui.Checkbox("Automatically populate notes using player names", ref autoPopulateNotes))
        {
            currentProfile.AutoPopulateEmptyNotesFromCharaName = autoPopulateNotes;
            _configService.Save();
        }
    }

    private void DrawGeneralUiDisplaySettings()
    {
        _lastTab = "Appearance";
        _uiShared.BigText("Appearance");

        var currentProfile = _configService.Current;

        ImGui.Separator();
        _uiShared.BigText("Basic Interface");
        var showSpheneIcon = currentProfile.ShowSpheneIcon;
        if (ImGui.Checkbox("Show Sphene Icon", ref showSpheneIcon))
        {
            currentProfile.ShowSpheneIcon = showSpheneIcon;
            _configService.Save();
        }
        var lockSpheneIcon = currentProfile.LockSpheneIcon;
        if (ImGui.Checkbox("Lock Sphene Icon position", ref lockSpheneIcon))
        {
            currentProfile.LockSpheneIcon = lockSpheneIcon;
            _configService.Save();
        }
        var enableRightClickMenu = currentProfile.EnableRightClickMenus;
        if (ImGui.Checkbox("Enable game right-click menus", ref enableRightClickMenu))
        {
            currentProfile.EnableRightClickMenus = enableRightClickMenu;
            _configService.Save();
        }

        // ShrinkU integration settings moved to Overview page alongside version info

        ImGui.Separator();
        _uiShared.BigText("Server Info Bar");
        var enableDtrEntry = currentProfile.EnableDtrEntry;
        if (ImGui.Checkbox("Show status in Server Info Bar", ref enableDtrEntry))
        {
            currentProfile.EnableDtrEntry = enableDtrEntry;
            _configService.Save();
        }
        using (ImRaii.Disabled(!enableDtrEntry))
        {
            using var indent = ImRaii.PushIndent();
            var showUidInDtrTooltip = currentProfile.ShowUidInDtrTooltip;
            if (ImGui.Checkbox("Show UID in tooltip", ref showUidInDtrTooltip))
            {
                currentProfile.ShowUidInDtrTooltip = showUidInDtrTooltip;
                _configService.Save();
            }
            var preferNoteInDtrTooltip = currentProfile.PreferNoteInDtrTooltip;
            if (ImGui.Checkbox("Prefer notes in tooltip", ref preferNoteInDtrTooltip))
            {
                currentProfile.PreferNoteInDtrTooltip = preferNoteInDtrTooltip;
                _configService.Save();
            }
            var useColorsInDtr = currentProfile.UseColorsInDtr;
            if (ImGui.Checkbox("Use status colors", ref useColorsInDtr))
            {
                currentProfile.UseColorsInDtr = useColorsInDtr;
                _configService.Save();
            }
        }

        ImGui.Separator();
        _uiShared.BigText("User List Options");
       
        var useFocusTarget = _configService.Current.UseFocusTarget;
        if (ImGui.Checkbox("Set visible pairs as focus targets when clicking the eye", ref useFocusTarget))
        {
            _configService.Current.UseFocusTarget = useFocusTarget;
            _configService.Save();
        }
        
        ImGuiHelpers.ScaledDummy(10);

        var showNameInsteadOfNotes = currentProfile.ShowCharacterNameInsteadOfNotesForVisible;
        if (ImGui.Checkbox("Show character name instead of notes", ref showNameInsteadOfNotes))
        {
            currentProfile.ShowCharacterNameInsteadOfNotesForVisible = showNameInsteadOfNotes;
            _configService.Save();
            Mediator.Publish(new RefreshUiMessage());
        }
        _uiShared.DrawHelpText("When enabled, visible users will display their character name instead of your custom notes for them.");
        
        var showVisibleSeparate = currentProfile.ShowVisibleUsersSeparately;
        if (ImGui.Checkbox("Show visible users separately", ref showVisibleSeparate))
        {
            currentProfile.ShowVisibleUsersSeparately = showVisibleSeparate;
            _configService.Save();
            Mediator.Publish(new StructuralRefreshUiMessage());
        }
        _uiShared.DrawHelpText("Visible users will appear in a separate 'Visible' group instead of being mixed with other users.");
        
        var showVisibleSyncshellUsersOnlyInSyncshells = _configService.Current.ShowVisibleSyncshellUsersOnlyInSyncshells;
        if (ImGui.Checkbox("Show visible Syncshell users only in Syncshells", ref showVisibleSyncshellUsersOnlyInSyncshells))
        {
            _configService.Current.ShowVisibleSyncshellUsersOnlyInSyncshells = showVisibleSyncshellUsersOnlyInSyncshells;
            _configService.Save();
            Mediator.Publish(new StructuralRefreshUiMessage());
        }
        _uiShared.DrawHelpText("When enabled, visible users who are only connected through Syncshells will only appear in their respective Syncshells and not in the separate 'Visible' group.");
        
        var showOfflineSeparate = currentProfile.ShowOfflineUsersSeparately;
        if (ImGui.Checkbox("Show offline users separately", ref showOfflineSeparate))
        {
            currentProfile.ShowOfflineUsersSeparately = showOfflineSeparate;
            _configService.Save();
            Mediator.Publish(new StructuralRefreshUiMessage());
        }
        _uiShared.DrawHelpText("Directly paired offline users will appear in a separate 'Offline' group. Offline syncshell members remain in their syncshells.");
        
        var showSyncshellOfflineSeparate = currentProfile.ShowSyncshellOfflineUsersSeparately;
        if (ImGui.Checkbox("Also show offline Syncshell users separately", ref showSyncshellOfflineSeparate))
        {
            currentProfile.ShowSyncshellOfflineUsersSeparately = showSyncshellOfflineSeparate;
            _configService.Save();
            Mediator.Publish(new StructuralRefreshUiMessage());
        }
        _uiShared.DrawHelpText("When enabled, offline syncshell members will also appear in a separate 'Offline Syncshell' group instead of remaining in their syncshells.");
        ImGuiHelpers.ScaledDummy(10);
       
        var groupUpSyncshells = _configService.Current.GroupUpSyncshells;
        if (ImGui.Checkbox("Group up all syncshells in one folder", ref groupUpSyncshells))
        {
            _configService.Current.GroupUpSyncshells = groupUpSyncshells;
            _configService.Save();
            Mediator.Publish(new StructuralRefreshUiMessage());
        }
        _uiShared.DrawHelpText("This will group up all Syncshells in a special 'All Syncshells' folder in the main UI.");
        

        ImGui.Separator();
        // Profile Settings
        _uiShared.BigText("Profile Settings");
        var showProfiles = _configService.Current.ProfilesShow;
        if (ImGui.Checkbox("Show Sphene Profiles on Hover", ref showProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesShow = showProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText("This will show the configured user profile after a set delay");
        ImGui.Indent();
        if (!showProfiles) ImGui.BeginDisabled();
        var profileOnRight = _configService.Current.ProfilePopoutRight;
        if (ImGui.Checkbox("Popout profiles on the right", ref profileOnRight))
        {
            _configService.Current.ProfilePopoutRight = profileOnRight;
            _configService.Save();
            Mediator.Publish(new CompactUiChange(Vector2.Zero, Vector2.Zero));
        }
        _uiShared.DrawHelpText("Will show profiles on the right side of the main UI");
        var profileDelay = _configService.Current.ProfileDelay;
        if (ImGui.SliderFloat("Hover Delay", ref profileDelay, 1, 10))
        {
            _configService.Current.ProfileDelay = profileDelay;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Delay until the profile should be displayed");
        if (!showProfiles) ImGui.EndDisabled();
        ImGui.Unindent();
        var showNsfwProfiles = _configService.Current.ProfilesAllowNsfw;
        if (ImGui.Checkbox("Show profiles marked as NSFW", ref showNsfwProfiles))
        {
            Mediator.Publish(new ClearProfileDataMessage());
            _configService.Current.ProfilesAllowNsfw = showNsfwProfiles;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Will show profiles that have the NSFW tag enabled");
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
        _lastTab = "Notifications";
        _uiShared.BigText("Notifications");

        var currentProfile = _configService.Current;

        _uiShared.DrawCombo("Info Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
        (i) =>
        {
            currentProfile.InfoNotification = i;
            _configService.Save();
        }, currentProfile.InfoNotification);
        _uiShared.DrawHelpText("The location where 'Info' notifications will display. Nowhere will not show any Info notifications; Chat prints in chat; Toast shows a toast; Both shows chat and toast.");

        _uiShared.DrawCombo("Warning Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
        (i) =>
        {
            currentProfile.WarningNotification = i;
            _configService.Save();
        }, currentProfile.WarningNotification);
        _uiShared.DrawHelpText("The location where 'Warning' notifications will display. Nowhere, Chat, Toast, or Both.");

        _uiShared.DrawCombo("Error Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
        (i) =>
        {
            currentProfile.ErrorNotification = i;
            _configService.Save();
        }, currentProfile.ErrorNotification);
        _uiShared.DrawHelpText("The location where 'Error' notifications will display. Nowhere, Chat, Toast, or Both.");

        var disableOptionalPluginWarnings = currentProfile.DisableOptionalPluginWarnings;
        if (ImGui.Checkbox("Disable optional plugin warnings", ref disableOptionalPluginWarnings))
        {
            currentProfile.DisableOptionalPluginWarnings = disableOptionalPluginWarnings;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Suppress 'Warning' messages for missing optional plugins.");

        var onlineNotifs = currentProfile.ShowOnlineNotifications;
        if (ImGui.Checkbox("Enable online notifications", ref onlineNotifs))
        {
            currentProfile.ShowOnlineNotifications = onlineNotifs;
            _configService.Save();
        }
        var onlineNotifsPairsOnly = currentProfile.ShowOnlineNotificationsOnlyForIndividualPairs;
        if (ImGui.Checkbox("Only for individual pairs", ref onlineNotifsPairsOnly))
        {
            currentProfile.ShowOnlineNotificationsOnlyForIndividualPairs = onlineNotifsPairsOnly;
            _configService.Save();
        }
        var onlineNotifsNamedOnly = currentProfile.ShowOnlineNotificationsOnlyForNamedPairs;
        if (ImGui.Checkbox("Only for named pairs", ref onlineNotifsNamedOnly))
        {
            currentProfile.ShowOnlineNotificationsOnlyForNamedPairs = onlineNotifsNamedOnly;
            _configService.Save();
        }

        ImGui.Separator();
        _uiShared.BigText("Area-bound Syncshell Notifications");
        
        var areaBoundNotifs = currentProfile.ShowAreaBoundSyncshellNotifications;
        if (ImGui.Checkbox("Enable area-bound syncshell notifications", ref areaBoundNotifs))
        {
            currentProfile.ShowAreaBoundSyncshellNotifications = areaBoundNotifs;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Show notifications when area-bound syncshells become available to join in your current location.");

        if (currentProfile.ShowAreaBoundSyncshellNotifications)
        {
            ImGui.Indent();
            _uiShared.DrawCombo("Area-bound Notification Display##settingsUi", (NotificationLocation[])Enum.GetValues(typeof(NotificationLocation)), (i) => i.ToString(),
            (i) =>
            {
                currentProfile.AreaBoundSyncshellNotification = i;
                _configService.Save();
            }, currentProfile.AreaBoundSyncshellNotification);
            _uiShared.DrawHelpText("Choose where area-bound syncshell notifications should appear. Nowhere, Chat, Toast, or Both.");
            ImGui.Unindent();
        }
        
        var showWelcomeMessages = currentProfile.ShowAreaBoundSyncshellWelcomeMessages;
        if (ImGui.Checkbox("Show area-bound syncshell welcome messages", ref showWelcomeMessages))
        {
            currentProfile.ShowAreaBoundSyncshellWelcomeMessages = showWelcomeMessages;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Automatically show welcome messages when joining area-bound syncshells. When disabled, you can still view welcome messages by clicking the area-bound indicator next to the syncshell name.");
        
        var autoShowConsent = currentProfile.AutoShowAreaBoundSyncshellConsent;
        if (ImGui.Checkbox("Automatically show area-bound syncshell consent", ref autoShowConsent))
        {
            currentProfile.AutoShowAreaBoundSyncshellConsent = autoShowConsent;
            _configService.Save();
        }
        _uiShared.DrawHelpText("When enabled, consent dialogs for area-bound syncshells will appear automatically when entering areas. When disabled, you can manually trigger consent using the button in the Compact UI. This setting also controls city syncshell join requests.");
    }

    private void DrawAcknowledgmentSettings()
    {
        _uiShared.BigText("Acknowledgment System");
        ImGui.Separator();
        
        // Popup Settings Section
        _uiShared.BigText("Popup Settings");
        
        var showPopups = _configService.Current.ShowAcknowledgmentPopups;
        if (ImGui.Checkbox("Show Acknowledgment Notifications", ref showPopups))
        {
            _configService.Current.ShowAcknowledgmentPopups = showPopups;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enable or disable notifications for acknowledgment requests. Disable to prevent spam when receiving many requests.");
        
        var showWaitingPopups = _configService.Current.ShowWaitingForAcknowledgmentPopups;
        if (ImGui.Checkbox("Show 'Waiting for Acknowledgment' Popups", ref showWaitingPopups))
        {
            _configService.Current.ShowWaitingForAcknowledgmentPopups = showWaitingPopups;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enable or disable 'waiting for acknowledgment' popups. Success notifications show regardless of this setting.");
        
        if (_configService.Current.ShowAcknowledgmentPopups)
        {
            ImGui.Indent();
            
            var notificationLocation = (int)_configService.Current.AcknowledgmentNotification;
            var notificationOptions = new[] { "None", "Chat", "Toast", "Both" };
            if (ImGui.Combo("Notification Location", ref notificationLocation, notificationOptions, notificationOptions.Length))
            {
                _configService.Current.AcknowledgmentNotification = (NotificationLocation)notificationLocation;
                _configService.Save();
            }
            _uiShared.DrawHelpText("Choose where acknowledgment notifications should appear.");
            
            ImGui.Unindent();
        }
        
        ImGui.Spacing();
        ImGui.Separator();
        
        // Performance Settings Section
        _uiShared.BigText("Performance Settings");
        
        var enableBatching = _configService.Current.EnableAcknowledgmentBatching;
        if (ImGui.Checkbox("Enable Batching", ref enableBatching))
        {
            _configService.Current.EnableAcknowledgmentBatching = enableBatching;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Group multiple acknowledgments for better performance. Recommended with many active connections.");
        
        var enableAutoRetry = _configService.Current.EnableAcknowledgmentAutoRetry;
        if (ImGui.Checkbox("Enable Auto Retry", ref enableAutoRetry))
        {
            _configService.Current.EnableAcknowledgmentAutoRetry = enableAutoRetry;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Automatically retry failed acknowledgments to improve reliability.");
        

        
        ImGui.Spacing();
        ImGui.Separator();
        
        // Timeout Settings Section
        _uiShared.BigText("Timeout & Reliability");
        
        var timeoutSeconds = _configService.Current.AcknowledgmentTimeoutSeconds;
        if (ImGui.SliderInt("Acknowledgment Timeout (seconds)", ref timeoutSeconds, 5, 120))
        {
            _configService.Current.AcknowledgmentTimeoutSeconds = timeoutSeconds;
            _configService.Save();
        }
        _uiShared.DrawHelpText("How long to wait for acknowledgment responses before timing out.");
        
        ImGui.Spacing();
        ImGui.Separator();
        
        // Information Section
        _uiShared.BigText("Information");
        UiSharedService.TextWrapped("The acknowledgment system helps maintain synchronization between connected users. " +
                          "When disabled, popup notifications will not appear, but the system will continue to function in the background. " +
                          "This is useful when you have many active connections to prevent notification spam.");
        
        ImGui.Spacing();
        
        // Reset to defaults button
        if (ImGui.Button("Reset Acknowledgment Settings to Defaults"))
        {
            _configService.Current.ShowAcknowledgmentPopups = false;
            _configService.Current.ShowWaitingForAcknowledgmentPopups = false;
            _configService.Current.EnableAcknowledgmentBatching = true;
            _configService.Current.EnableAcknowledgmentAutoRetry = true;
            _configService.Current.AcknowledgmentTimeoutSeconds = 30;
            _configService.Current.AcknowledgmentNotification = NotificationLocation.Chat;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Reset all acknowledgment settings to their default values.");
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
        _uiShared.BigText("Theme Customization");
        
        ImGui.Separator();
        
        // Theme Selector
        DrawThemeSelector();
        
        ImGui.Separator();
        
        if (ApiController.IsAdmin)
        {
            using (var themeTabBar = ImRaii.TabBar("ThemeTabBar"))
            {
                if (themeTabBar)
                {
                    var availableRegion = ImGui.GetContentRegionAvail();
                    var tabContentHeight = availableRegion.Y;

                    #pragma warning disable S1066
                    var generalTab = ImRaii.TabItem("General Theme", ImGuiTabItemFlags.None);
                    if (generalTab)
                    {
                        if (ImGui.BeginChild("GeneralThemeChild", new Vector2(0, tabContentHeight), true))
                        {
                            DrawGeneralThemeSettings();
                            ImGui.EndChild();
                        }
                    }
                    #pragma warning restore S1066
                    generalTab.Dispose();

                    #pragma warning disable S1066
                    var panelTab = ImRaii.TabItem("Panel Theme", _preferPanelThemeTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
                    if (panelTab)
                    {
                        if (ImGui.BeginChild("PanelThemeChild", new Vector2(0, tabContentHeight), true))
                        {
                            DrawCompactUIThemeSettings();
                            ImGui.EndChild();
                        }
                    }
                    #pragma warning restore S1066
                    panelTab.Dispose();
                    _preferPanelThemeTab = false;
                }
            }
        }
        else
        {
            var availableRegion = ImGui.GetContentRegionAvail();
            var tabContentHeight = availableRegion.Y;
            if (ImGui.BeginChild("PanelThemeChild", new Vector2(0, tabContentHeight), true))
            {
                DrawCompactUIThemeSettings();
                ImGui.EndChild();
            }
            _preferPanelThemeTab = false;
        }
    }

    private void DrawGeneralThemeSettings()
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        bool themeChanged = false;
        
        ImGui.Text("General Window Radius Settings");
        ImGui.Separator();
        
        // Rounding Settings
        var windowRounding = theme.WindowRounding;
        if (ImGui.SliderFloat("Window Rounding", ref windowRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.WindowRounding = windowRounding;
            themeChanged = true;
        }
        
        var childRounding = theme.ChildRounding;
        if (ImGui.SliderFloat("Child Rounding", ref childRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.ChildRounding = childRounding;
            themeChanged = true;
        }
        
        var popupRounding = theme.PopupRounding;
        if (ImGui.SliderFloat("Popup Rounding", ref popupRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.PopupRounding = popupRounding;
            themeChanged = true;
        }
        
        var frameRounding = theme.FrameRounding;
        if (ImGui.SliderFloat("Frame Rounding", ref frameRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.FrameRounding = frameRounding;
            themeChanged = true;
        }
        
        var scrollbarRounding = theme.ScrollbarRounding;
        if (ImGui.SliderFloat("Scrollbar Rounding", ref scrollbarRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.ScrollbarRounding = scrollbarRounding;
            themeChanged = true;
        }
        
        var grabRounding = theme.GrabRounding;
        if (ImGui.SliderFloat("Grab Rounding", ref grabRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.GrabRounding = grabRounding;
            themeChanged = true;
        }
        
        var tabRounding = theme.TabRounding;
        if (ImGui.SliderFloat("Tab Rounding", ref tabRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.TabRounding = tabRounding;
            themeChanged = true;
        }
        
        ImGui.Separator();
        ImGui.Text("Border Settings");
        
        var windowBorderSize = theme.WindowBorderSize;
        if (ImGui.SliderFloat("Window Border", ref windowBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.WindowBorderSize = windowBorderSize;
            themeChanged = true;
        }
        
        var childBorderSize = theme.ChildBorderSize;
        if (ImGui.SliderFloat("Child Border", ref childBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.ChildBorderSize = childBorderSize;
            themeChanged = true;
        }
        
        var popupBorderSize = theme.PopupBorderSize;
        if (ImGui.SliderFloat("Popup Border", ref popupBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.PopupBorderSize = popupBorderSize;
            themeChanged = true;
        }
        
        var frameBorderSize = theme.FrameBorderSize;
        if (ImGui.SliderFloat("Frame Border", ref frameBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.FrameBorderSize = frameBorderSize;
            themeChanged = true;
        }
        
        ImGui.Separator();
        ImGui.Text("Spacing & Padding Settings");
        
        var windowPadding = theme.WindowPadding;
        if (ImGui.SliderFloat2("Window Padding", ref windowPadding, 0.0f, 50.0f, "%.1f"))
        {
            theme.WindowPadding = windowPadding;
            themeChanged = true;
        }
        
        var framePadding = theme.FramePadding;
        if (ImGui.SliderFloat2("Frame Padding", ref framePadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.FramePadding = framePadding;
            themeChanged = true;
        }
        
        var itemSpacing = theme.ItemSpacing;
        if (ImGui.SliderFloat2("Item Spacing", ref itemSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.ItemSpacing = itemSpacing;
            themeChanged = true;
        }
        
        var itemInnerSpacing = theme.ItemInnerSpacing;
        if (ImGui.SliderFloat2("Item Inner Spacing", ref itemInnerSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.ItemInnerSpacing = itemInnerSpacing;
            themeChanged = true;
        }
        
        var indentSpacing = theme.IndentSpacing;
        if (ImGui.SliderFloat("Indent Spacing", ref indentSpacing, 0.0f, 50.0f, "%.1f"))
        {
            theme.IndentSpacing = indentSpacing;
            themeChanged = true;
        }
        
        var scrollbarSize = theme.ScrollbarSize;
        if (ImGui.SliderFloat("Scrollbar Size", ref scrollbarSize, 5.0f, 30.0f, "%.1f"))
        {
            theme.ScrollbarSize = scrollbarSize;
            themeChanged = true;
        }
        
        var grabMinSize = theme.GrabMinSize;
        if (ImGui.SliderFloat("Grab Min Size", ref grabMinSize, 5.0f, 30.0f, "%.1f"))
        {
            theme.GrabMinSize = grabMinSize;
            themeChanged = true;
        }
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
            _hasUnsavedThemeChanges = true;
        }
        
        ImGui.Separator();
        ImGui.Text("General Theme Colors");
        ImGui.Separator();
        
        if (ImGui.BeginTabBar("GeneralColorTabBar"))
        {
            if (ImGui.BeginTabItem("Basic Colors"))
            {
                DrawBasicColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Window Colors"))
            {
                DrawWindowColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Frame & Input Colors"))
            {
                DrawFrameInputColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Button & Header Colors"))
            {
                DrawButtonHeaderColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Menu & Navigation Colors"))
            {
                DrawMenuNavigationColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Scrollbar & Slider Colors"))
            {
                DrawScrollbarSliderColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Table & Tab Colors"))
            {
                DrawTableTabColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            // Moved Button Styles to Panel Theme tab
            
            ImGui.EndTabBar();
        }
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
            _hasUnsavedThemeChanges = true;
        }
    }
    
    

    private void DrawCompactUIThemeSettings()
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        bool themeChanged = false;
        
        
        UiSharedService.ColorTextWrapped("Customize how the Panel looks and feels.", ImGuiColors.DalamudGrey);
        UiSharedService.ColorTextWrapped("Panel will be opened automaticly and you can see changes immediately.", ImGuiColors.DalamudYellow);
        ImGui.Spacing();
        
        
        ImGui.Spacing();
                
        if (ImGui.CollapsingHeader("Control Panel Layout", ImGuiTreeNodeFlags.None))
        {
                
        ImGui.Spacing();
        ImGui.Text("Panel Spacing & Sizing");
        ImGui.Separator();
        
        // Panel Spacing Settings
        
        var compactItemSpacing = theme.CompactItemSpacing;
        if (ImGui.SliderFloat2("Panel Item Spacing", ref compactItemSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactItemSpacing = compactItemSpacing;
            themeChanged = true;
        }
        
        
        var compactChildPadding = theme.CompactChildPadding;
        if (ImGui.SliderFloat2("Panel Padding", ref compactChildPadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactChildPadding = compactChildPadding;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        ImGui.Text("Panel Border and Rounding");
        ImGui.Separator();
        
        var compactWindowBorderSize = theme.CompactWindowBorderSize;
        if (ImGui.SliderFloat("Panel Window Border Size", ref compactWindowBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.CompactWindowBorderSize = compactWindowBorderSize;
            themeChanged = true;
        }

        var compactWindowRounding = theme.CompactWindowRounding;
        if (ImGui.SliderFloat("Corner Rounding", ref compactWindowRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactWindowRounding = compactWindowRounding;
            themeChanged = true;
        }

        ImGui.Spacing();
        ImGui.Text("Panel Backgrounds");
        ImGui.Separator();
        var compactWindowBg = theme.CompactWindowBg;
        if (ImGui.ColorEdit4("Panel Background", ref compactWindowBg))
        {
            theme.CompactWindowBg = compactWindowBg;
            themeChanged = true;
        }
        var compactFrameBg = theme.CompactFrameBg;
        if (ImGui.ColorEdit4("Control Background", ref compactFrameBg))
        {
            theme.CompactFrameBg = compactFrameBg;
            themeChanged = true;
        }

        ImGui.Spacing();
        ImGui.Text("General Text");
        ImGui.Separator();
        var compactText = theme.CompactText;
        if (ImGui.ColorEdit4("Text", ref compactText))
        {
            theme.CompactText = compactText;
            themeChanged = true;
        }
        var compactTextSecondary = theme.CompactTextSecondary;
        if (ImGui.ColorEdit4("Text (Secondary)", ref compactTextSecondary))
        {
            theme.CompactTextSecondary = compactTextSecondary;
            themeChanged = true;
        }

        ImGui.Spacing();
        ImGui.Text("Accents & Borders");
        ImGui.Separator();
        var compactBorder = theme.CompactBorder;
        if (ImGui.ColorEdit4("Border", ref compactBorder))
        {
            theme.CompactBorder = compactBorder;
            themeChanged = true;
        }
        var separator = theme.Separator;
        if (ImGui.ColorEdit4("Separator", ref separator))
        {
            theme.Separator = separator;
            themeChanged = true;
        }

        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Context & Tooltip", ImGuiTreeNodeFlags.None))
        {
            ImGui.Separator();
            var compactTooltipRounding = theme.CompactTooltipRounding;
            if (ImGui.SliderFloat("Panel Tooltip Rounding", ref compactTooltipRounding, 0.0f, 30.0f, "%.1f"))
            {
                theme.CompactTooltipRounding = compactTooltipRounding;
                themeChanged = true;
            }
            var compactTooltipBorderSize = theme.CompactTooltipBorderSize;
            if (ImGui.SliderFloat("Panel Tooltip Border Size", ref compactTooltipBorderSize, 0.0f, 5.0f, "%.1f"))
            {
                theme.CompactTooltipBorderSize = compactTooltipBorderSize;
                themeChanged = true;
            }
            var compactContextMenuRounding = theme.CompactContextMenuRounding;
            if (ImGui.SliderFloat("Panel Context Menu Rounding", ref compactContextMenuRounding, 0.0f, 30.0f, "%.1f"))
            {
                theme.CompactContextMenuRounding = compactContextMenuRounding;
                themeChanged = true;
            }
            var compactContextMenuBorderSize = theme.CompactContextMenuBorderSize;
            if (ImGui.SliderFloat("Panel Context Menu Border Size", ref compactContextMenuBorderSize, 0.0f, 5.0f, "%.1f"))
            {
                theme.CompactContextMenuBorderSize = compactContextMenuBorderSize;
                themeChanged = true;
            }
            ImGui.Spacing();
            ImGui.Text("Context Menu Button Style");
            ImGui.Separator();
            var key = Sphene.UI.Theme.ButtonStyleKeys.ContextMenu_Item;
            if (!theme.ButtonStyles.TryGetValue(key, out var ov))
            {
                ov = new Sphene.UI.Theme.ButtonStyleOverride();
                theme.ButtonStyles[key] = ov;
            }
            var widthDelta = ov.WidthDelta;
            if (ImGui.DragFloat("Width", ref widthDelta, 0.1f, -100f, 200f))
            {
                ov.WidthDelta = widthDelta;
                theme.NotifyThemeChanged();
            }
            var heightDelta = ov.HeightDelta;
            if (ImGui.DragFloat("Height", ref heightDelta, 0.1f, -50f, 100f))
            {
                ov.HeightDelta = heightDelta;
                theme.NotifyThemeChanged();
            }
            var iconOffset = ov.IconOffset;
            if (ImGui.DragFloat2("Icon Offset", ref iconOffset, 0.1f, -50f, 50f))
            {
                ov.IconOffset = iconOffset;
                theme.NotifyThemeChanged();
            }
            ImGui.Separator();
            var effBtn = ov.Button ?? theme.Button;
            if (ImGui.ColorEdit4("Button", ref effBtn))
            {
                ov.Button = effBtn;
                theme.NotifyThemeChanged();
            }
            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            var genHoverActive = ImGui.SmallButton(FontAwesomeIcon.Magic.ToIconString());
            ImGui.PopFont();
            if (genHoverActive)
            {
                var tup = ButtonStyleManagerUI.DeriveHoverActive(ov.Button ?? theme.Button);
                ov.ButtonHovered = tup.Hover;
                ov.ButtonActive = tup.Active;
                theme.NotifyThemeChanged();
            }
            if (ImGui.IsItemHovered())
                UiSharedService.AttachToolTip("Generate Hover/Active colors from current Button color.");
            var effBtnH = ov.ButtonHovered ?? theme.ButtonHovered;
            if (ImGui.ColorEdit4("Button Hovered", ref effBtnH))
            {
                ov.ButtonHovered = effBtnH;
                theme.NotifyThemeChanged();
            }
            var effBtnA = ov.ButtonActive ?? theme.ButtonActive;
            if (ImGui.ColorEdit4("Button Active", ref effBtnA))
            {
                ov.ButtonActive = effBtnA;
                theme.NotifyThemeChanged();
            }
            var effIcon = ov.Icon ?? theme.TextPrimary;
            if (ImGui.ColorEdit4("Icon", ref effIcon))
            {
                ov.Icon = effIcon;
                theme.NotifyThemeChanged();
            }
            var effTxt = ov.Text ?? theme.TextPrimary;
            if (ImGui.ColorEdit4("Text", ref effTxt))
            {
                ov.Text = effTxt;
                theme.NotifyThemeChanged();
            }
            var effBorder = ov.Border ?? theme.Border;
            if (ImGui.ColorEdit4("Border", ref effBorder))
            {
                ov.Border = effBorder;
                theme.NotifyThemeChanged();
            }
            var effBorderSize = ov.BorderSize ?? theme.FrameBorderSize;
            if (ImGui.SliderFloat("Border Width", ref effBorderSize, 0f, 5f, "%.1f"))
            {
                ov.BorderSize = effBorderSize;
                theme.NotifyThemeChanged();
            }
            var desiredMenuWidth = 200.0f * ImGuiHelpers.GlobalScale;
            var padding = theme.CompactChildPadding;
            var spacingX = ImGui.GetStyle().ItemSpacing.X;
            var spacingY = ImGui.GetStyle().ItemSpacing.Y;
            var contextBorder = theme.CompactContextMenuBorderSize;
            var tooltipBorder = theme.CompactTooltipBorderSize;
            var contextPreviewWidth = desiredMenuWidth + (padding.X * 2.0f) + (contextBorder * 2.0f);
            var tooltipPreviewWidth = desiredMenuWidth + (padding.X * 2.0f) + (tooltipBorder * 2.0f);
            var previewHeight = 96.0f;
            var start = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            var tooltipHeadingPos = start;
            var contextHeadingPos = new Vector2(start.X + tooltipPreviewWidth + spacingX, start.Y);
            ImGui.SetCursorScreenPos(tooltipHeadingPos);
            ImGui.TextUnformatted("Tooltip Preview");
            var headingH = ImGui.GetTextLineHeight();
            ImGui.SetCursorScreenPos(contextHeadingPos);
            ImGui.TextUnformatted("Context Menu Preview");
            var tooltipStart = new Vector2(tooltipHeadingPos.X, tooltipHeadingPos.Y + headingH + spacingY);
            var contextStart = new Vector2(contextHeadingPos.X, contextHeadingPos.Y + headingH + spacingY);
            var tooltipEnd = new Vector2(tooltipStart.X + tooltipPreviewWidth, tooltipStart.Y + previewHeight);
            var contextEnd = new Vector2(contextStart.X + contextPreviewWidth, contextStart.Y + previewHeight);
            dl.AddRectFilled(tooltipStart, tooltipEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactPopupBg), theme.CompactTooltipRounding);
            dl.AddRect(tooltipStart, tooltipEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactBorder), theme.CompactTooltipRounding, ImDrawFlags.None, tooltipBorder);
            dl.AddRectFilled(contextStart, contextEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactPopupBg), theme.CompactContextMenuRounding);
            dl.AddRect(contextStart, contextEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactBorder), theme.CompactContextMenuRounding, ImDrawFlags.None, contextBorder);
            var menuWidth = desiredMenuWidth;
            ImGui.SetCursorScreenPos(new Vector2(tooltipStart.X + padding.X + tooltipBorder, tooltipStart.Y + padding.Y + tooltipBorder));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
            if (ImGui.BeginChild("##tooltip-preview", new Vector2(desiredMenuWidth + padding.X * 2.0f, previewHeight - tooltipBorder * 2.0f), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground))
            {
                ImGui.TextUnformatted("Tooltip text");
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.SetCursorScreenPos(new Vector2(contextStart.X + padding.X + contextBorder, contextStart.Y + padding.Y + contextBorder));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
            if (ImGui.BeginChild("##context-preview", new Vector2(desiredMenuWidth + padding.X * 2.0f, previewHeight - contextBorder * 2.0f), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground))
            {
                ImGui.TextUnformatted("Common Pair Functions");
                _uiShared.IconTextActionButton(FontAwesomeIcon.User, "Open Profile", menuWidth, ButtonStyleKeys.ContextMenu_Item);
                _uiShared.IconTextActionButton(FontAwesomeIcon.Sync, "Reload last data", menuWidth, ButtonStyleKeys.ContextMenu_Item);
                _uiShared.IconTextActionButton(FontAwesomeIcon.PlayCircle, "Cycle pause state", menuWidth, ButtonStyleKeys.ContextMenu_Item);
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.SetCursorScreenPos(new Vector2(start.X, MathF.Max(contextEnd.Y, tooltipEnd.Y)));
        }
        ImGui.Spacing();

        if (_preferButtonStylesTab) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        if (ImGui.CollapsingHeader("Button Styles", ImGuiTreeNodeFlags.None))
        {
            ImGui.Separator();
            var compactFrameRounding = theme.CompactFrameRounding;
            if (ImGui.SliderFloat("Rounding for all buttons", ref compactFrameRounding, 0.0f, 30.0f, "%.1f"))
            {
                theme.CompactFrameRounding = compactFrameRounding;
                themeChanged = true;
            }
            ButtonStyleManagerUI.Draw();
            _preferButtonStylesTab = false;
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        
        if (ImGui.CollapsingHeader("Header", ImGuiTreeNodeFlags.None))
        {
            ImGui.Separator();

            var compactShowImGuiHeader = theme.CompactShowImGuiHeader;
            if (ImGui.Checkbox("Show Window Header", ref compactShowImGuiHeader))
            {
                theme.CompactShowImGuiHeader = compactShowImGuiHeader;
                themeChanged = true;
            }
            UiSharedService.ColorTextWrapped("Show the standard window header. Disable for a cleaner panel.", ImGuiColors.DalamudGrey);

            if (!theme.CompactShowImGuiHeader)
            {
                var headerBg = theme.CompactHeaderBg;
                if (ImGui.ColorEdit4("Header Background", ref headerBg))
                {
                    theme.CompactHeaderBg = headerBg;
                    themeChanged = true;
                }

                var titleText = theme.CompactPanelTitleText;
                if (ImGui.ColorEdit4("Title Text", ref titleText))
                {
                    theme.CompactPanelTitleText = titleText;
                    themeChanged = true;
                }
                
                var compactHeaderRounding = theme.CompactHeaderRounding;
                if (ImGui.SliderFloat("Header Rounding", ref compactHeaderRounding, 0.0f, 30.0f, "%.1f"))
                {
                    theme.CompactHeaderRounding = compactHeaderRounding;
                    themeChanged = true;
                }
            }
            if (theme.CompactShowImGuiHeader)
            {
                var compactTitleBg = theme.CompactTitleBg;
                if (ImGui.ColorEdit4("Window Header Background", ref compactTitleBg))
                {
                    theme.CompactTitleBg = compactTitleBg;
                    themeChanged = true;
                }
                var compactTitleBgActive = theme.CompactTitleBgActive;
                if (ImGui.ColorEdit4("Window Header Background (Active)", ref compactTitleBgActive))
                {
                    theme.CompactTitleBgActive = compactTitleBgActive;
                    themeChanged = true;
                }
            }
            var sectionHeaderText = theme.CompactHeaderText;
            if (ImGui.ColorEdit4("Section Header Text", ref sectionHeaderText))
            {
                theme.CompactHeaderText = sectionHeaderText;
                themeChanged = true;
            }




            ImGui.Spacing();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Regulator ID", ImGuiTreeNodeFlags.None))
        {
            ImGui.Separator();

            var uidColor = theme.CompactUidColor;
            if (ImGui.ColorEdit4("UID Text", ref uidColor))
            {
                theme.CompactUidColor = uidColor;
                themeChanged = true;
            }

            ImGui.Spacing();
            var uidScale = theme.CompactUidFontScale;
            if (ImGui.SliderFloat("UID Font Scale", ref uidScale, 0.5f, 2.0f, "%.2f"))
            {
                theme.CompactUidFontScale = uidScale;
                themeChanged = true;
            }
            ImGui.Spacing();
        }

        

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Connected Status", ImGuiTreeNodeFlags.None))
        {
            ImGui.Separator();

            var statusConnected = theme.CompactServerStatusConnected;
            if (ImGui.ColorEdit4("Connected", ref statusConnected))
            {
                theme.CompactServerStatusConnected = statusConnected;
                themeChanged = true;
            }

            var statusWarning = theme.CompactServerStatusWarning;
            if (ImGui.ColorEdit4("Warning", ref statusWarning))
            {
                theme.CompactServerStatusWarning = statusWarning;
                themeChanged = true;
            }

            var statusError = theme.CompactServerStatusError;
            if (ImGui.ColorEdit4("Error", ref statusError))
            {
                theme.CompactServerStatusError = statusError;
                themeChanged = true;
            }

            ImGui.Spacing();
        }

        

        
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Connected Pairs", ImGuiTreeNodeFlags.None))
        {
            ImGui.Separator();

            var connectedText = theme.CompactConnectedText;
            if (ImGui.ColorEdit4("Online Count Text", ref connectedText))
            {
                theme.CompactConnectedText = connectedText;
                themeChanged = true;
            }

            var allSyncshellsText = theme.CompactAllSyncshellsText;
            if (ImGui.ColorEdit4("All Syncshells Text", ref allSyncshellsText))
            {
                theme.CompactAllSyncshellsText = allSyncshellsText;
                themeChanged = true;
            }

            var offlinePausedText = theme.CompactOfflinePausedText;
            if (ImGui.ColorEdit4("Offline / Paused Text", ref offlinePausedText))
            {
                theme.CompactOfflinePausedText = offlinePausedText;
                themeChanged = true;
            }

            var offlineSyncshellText = theme.CompactOfflineSyncshellText;
            if (ImGui.ColorEdit4("Offline Syncshell Users Text", ref offlineSyncshellText))
            {
                theme.CompactOfflineSyncshellText = offlineSyncshellText;
                themeChanged = true;
            }

            var visibleText = theme.CompactVisibleText;
            if (ImGui.ColorEdit4("Visible Users Text", ref visibleText))
            {
                theme.CompactVisibleText = visibleText;
                themeChanged = true;
            }

            var pairsText = theme.CompactPairsText;
            if (ImGui.ColorEdit4("Pairs Label Text", ref pairsText))
            {
                theme.CompactPairsText = pairsText;
                themeChanged = true;
            }

            ImGui.Spacing();
        }
        
        ImGui.Spacing();
        var ptbOpen = ImGui.CollapsingHeader("Player Transmission Bars", ImGuiTreeNodeFlags.None);
        if (ptbOpen != theme.ShowTransmissionPreview)
        {
            theme.ShowTransmissionPreview = ptbOpen;
            _suppressUnsavedForPreview = true;
            theme.NotifyThemeChanged();
            _suppressUnsavedForPreview = false;
        }
        if (ptbOpen)
        {
            ImGui.Separator();
        
            var prevSeparate = theme.SeparateTransmissionBarStyles;
            var separateBars = theme.SeparateTransmissionBarStyles;
            if (ImGui.Checkbox("Separate Upload/Download Settings", ref separateBars))
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
            if (ImGui.Checkbox("Auto Transmission Width (fit content)", ref autoTransmissionWidth))
            {
                theme.AutoTransmissionBarWidth = autoTransmissionWidth;
                themeChanged = true;
            }
        
            if (!theme.SeparateTransmissionBarStyles)
            {
                var transmissionBarRounding = theme.TransmissionBarRounding;
                if (ImGui.SliderFloat("Transmission Bar Rounding", ref transmissionBarRounding, 0.0f, 15.0f, "%.1f"))
                {
                    theme.TransmissionBarRounding = transmissionBarRounding;
                    themeChanged = true;
                }
                
                var compactTransmissionBarHeight = theme.CompactTransmissionBarHeight;
                if (ImGui.SliderFloat("Transmission Bar Height", ref compactTransmissionBarHeight, 2.0f, 30.0f, "%.1f"))
                {
                    theme.CompactTransmissionBarHeight = compactTransmissionBarHeight;
                    themeChanged = true;
                }
                
                var compactTransmissionBarBackground = theme.CompactTransmissionBarBackground;
                if (ImGui.ColorEdit4("Transmission Bar Background", ref compactTransmissionBarBackground))
                {
                    theme.CompactTransmissionBarBackground = compactTransmissionBarBackground;
                    themeChanged = true;
                }
                
                var compactTransmissionBarForeground = theme.CompactTransmissionBarForeground;
                if (ImGui.ColorEdit4("Transmission Bar Foreground", ref compactTransmissionBarForeground))
                {
                    theme.CompactTransmissionBarForeground = compactTransmissionBarForeground;
                    themeChanged = true;
                }
                
                var compactTransmissionBarBorder = theme.CompactTransmissionBarBorder;
                if (ImGui.ColorEdit4("Transmission Bar Border", ref compactTransmissionBarBorder))
                {
                    theme.CompactTransmissionBarBorder = compactTransmissionBarBorder;
                    themeChanged = true;
                }
                var useTransGrad = theme.TransmissionUseGradient;
                if (ImGui.Checkbox("Use Gradient Fill", ref useTransGrad))
                {
                    theme.TransmissionUseGradient = useTransGrad;
                    themeChanged = true;
                }
                if (theme.TransmissionUseGradient)
                {
                    var tGradStart = theme.TransmissionGradientStart;
                    if (ImGui.ColorEdit4("Gradient Start", ref tGradStart))
                    {
                        theme.TransmissionGradientStart = tGradStart;
                        themeChanged = true;
                    }
                    var tGradEnd = theme.TransmissionGradientEnd;
                    if (ImGui.ColorEdit4("Gradient End", ref tGradEnd))
                    {
                        theme.TransmissionGradientEnd = tGradEnd;
                        themeChanged = true;
                    }
                }
            }
            else
            {
                ImGui.Text("Upload Bar");
                ImGui.Separator();
                var uploadRounding = theme.UploadTransmissionBarRounding;
                if (ImGui.SliderFloat("Upload Bar Rounding", ref uploadRounding, 0.0f, 15.0f, "%.1f"))
                {
                    theme.UploadTransmissionBarRounding = uploadRounding;
                    themeChanged = true;
                }
                var uploadHeight = theme.UploadTransmissionBarHeight;
                if (ImGui.SliderFloat("Upload Bar Height", ref uploadHeight, 2.0f, 30.0f, "%.1f"))
                {
                    theme.UploadTransmissionBarHeight = uploadHeight;
                    themeChanged = true;
                }
                var uploadBg = theme.UploadTransmissionBarBackground;
                if (ImGui.ColorEdit4("Upload Bar Background", ref uploadBg))
                {
                    theme.UploadTransmissionBarBackground = uploadBg;
                    themeChanged = true;
                }
                var uploadFg = theme.UploadTransmissionBarForeground;
                if (ImGui.ColorEdit4("Upload Bar Foreground", ref uploadFg))
                {
                    theme.UploadTransmissionBarForeground = uploadFg;
                    themeChanged = true;
                }
                var uploadBorder = theme.UploadTransmissionBarBorder;
                if (ImGui.ColorEdit4("Upload Bar Border", ref uploadBorder))
                {
                    theme.UploadTransmissionBarBorder = uploadBorder;
                    themeChanged = true;
                }
                var useTransGrad = theme.TransmissionUseGradient;
                if (ImGui.Checkbox("Use Gradient Fill (Upload/Download)", ref useTransGrad))
                {
                    theme.TransmissionUseGradient = useTransGrad;
                    themeChanged = true;
                }
                if (theme.TransmissionUseGradient)
                {
                    var upGradStart = theme.UploadTransmissionGradientStart;
                    if (ImGui.ColorEdit4("Upload Gradient Start", ref upGradStart))
                    {
                        theme.UploadTransmissionGradientStart = upGradStart;
                        themeChanged = true;
                    }
                    var upGradEnd = theme.UploadTransmissionGradientEnd;
                    if (ImGui.ColorEdit4("Upload Gradient End", ref upGradEnd))
                    {
                        theme.UploadTransmissionGradientEnd = upGradEnd;
                        themeChanged = true;
                    }
                }
                
                ImGui.Spacing();
                ImGui.Text("Download Bar");
                ImGui.Separator();
                var downloadRounding = theme.DownloadTransmissionBarRounding;
                if (ImGui.SliderFloat("Download Bar Rounding", ref downloadRounding, 0.0f, 15.0f, "%.1f"))
                {
                    theme.DownloadTransmissionBarRounding = downloadRounding;
                    themeChanged = true;
                }
                var downloadHeight = theme.DownloadTransmissionBarHeight;
                if (ImGui.SliderFloat("Download Bar Height", ref downloadHeight, 2.0f, 30.0f, "%.1f"))
                {
                    theme.DownloadTransmissionBarHeight = downloadHeight;
                    themeChanged = true;
                }
                var downloadBg = theme.DownloadTransmissionBarBackground;
                if (ImGui.ColorEdit4("Download Bar Background", ref downloadBg))
                {
                    theme.DownloadTransmissionBarBackground = downloadBg;
                    themeChanged = true;
                }
                var downloadFg = theme.DownloadTransmissionBarForeground;
                if (ImGui.ColorEdit4("Download Bar Foreground", ref downloadFg))
                {
                    theme.DownloadTransmissionBarForeground = downloadFg;
                    themeChanged = true;
                }
                var downloadBorder = theme.DownloadTransmissionBarBorder;
                if (ImGui.ColorEdit4("Download Bar Border", ref downloadBorder))
                {
                    theme.DownloadTransmissionBarBorder = downloadBorder;
                    themeChanged = true;
                }
                if (theme.TransmissionUseGradient)
                {
                    var downGradStart = theme.DownloadTransmissionGradientStart;
                    if (ImGui.ColorEdit4("Download Gradient Start", ref downGradStart))
                    {
                        theme.DownloadTransmissionGradientStart = downGradStart;
                        themeChanged = true;
                    }
                    var downGradEnd = theme.DownloadTransmissionGradientEnd;
                    if (ImGui.ColorEdit4("Download Gradient End", ref downGradEnd))
                    {
                        theme.DownloadTransmissionGradientEnd = downGradEnd;
                        themeChanged = true;
                    }
                }
            }
        
            ImGui.Spacing();
            bool previewChanged = false;
            if (theme.ShowTransmissionPreview)
            {
                var uploadFill = theme.TransmissionPreviewUploadFill;
                if (ImGui.SliderFloat("Preview Upload Fill", ref uploadFill, 0.0f, 100.0f, "%.1f%"))
                {
                    theme.TransmissionPreviewUploadFill = uploadFill;
                    previewChanged = true;
                }
                
                var downloadFill = theme.TransmissionPreviewDownloadFill;
                if (ImGui.SliderFloat("Preview Download Fill", ref downloadFill, 0.0f, 100.0f, "%.1f%"))
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
        ImGui.Spacing();

        var uhOpen = ImGui.CollapsingHeader("Update Hint", ImGuiTreeNodeFlags.None);
        if (uhOpen != theme.ForceShowUpdateHint)
        {
            theme.ForceShowUpdateHint = uhOpen;
            _suppressUnsavedForPreview = true;
            theme.NotifyThemeChanged();
            _suppressUnsavedForPreview = false;
        }
        if (uhOpen)
        {
            ImGui.Separator();
            ImGui.Spacing();
            var updateHintHeight = theme.CompactUpdateHintHeight;
            if (ImGui.SliderFloat("Update Hint Height", ref updateHintHeight, 0.0f, 64.0f, "%.1f"))
            {
                theme.CompactUpdateHintHeight = updateHintHeight;
                themeChanged = true;
            }
            var updateHintPadding = theme.CompactUpdateHintPaddingY;
            if (ImGui.SliderFloat("Update Hint Padding", ref updateHintPadding, 0.0f, 24.0f, "%.1f"))
            {
                theme.CompactUpdateHintPaddingY = updateHintPadding;
                themeChanged = true;
            }
            var updateHintColor = theme.CompactUpdateHintColor;
            if (ImGui.ColorEdit4("Update Hint Color", ref updateHintColor))
            {
                theme.CompactUpdateHintColor = updateHintColor;
                themeChanged = true;
            }
            ImGui.Spacing();
        }
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
        }
        
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
        }
    }

    private void OnNavigateToButtonSettings(ThemeNavigateToButtonSettingsMessage msg)
    {
        _activeSettingsPage = SettingsPage.Theme;
        IsOpen = true;
        _preferPanelThemeTab = true;
        _preferButtonStylesTab = true;
        ButtonStyleManagerUI.SelectButtonKey(msg.ButtonStyleKey);
    }

    
    
    private void DrawBasicColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        UiSharedService.ColorTextWrapped("Basic colors form the foundation of your theme. These colors are used throughout the interface for primary elements.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        // Primary Colors
        ImGui.Text("Primary Colors");
        ImGui.Separator();
        
        var primaryDark = theme.PrimaryDark;
        if (ImGui.ColorEdit4("Primary Dark", ref primaryDark))
        {
            theme.PrimaryDark = primaryDark;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Main dark color used for backgrounds and primary interface elements.");
        
        var secondaryDark = theme.SecondaryDark;
        if (ImGui.ColorEdit4("Secondary Dark", ref secondaryDark))
        {
            theme.SecondaryDark = secondaryDark;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Secondary dark color used for contrast and supporting interface elements.");
        
        ImGui.Spacing();
        
        // Accent Colors
        ImGui.Text("Accent Colors");
        ImGui.Separator();
        
        var accentBlue = theme.AccentBlue;
        if (ImGui.ColorEdit4("Accent Blue", ref accentBlue))
        {
            theme.AccentBlue = accentBlue;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Blue accent color used for highlights, links, and important interactive elements.");
        
        var accentCyan = theme.AccentCyan;
        if (ImGui.ColorEdit4("Accent Cyan", ref accentCyan))
        {
            theme.AccentCyan = accentCyan;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Cyan accent color used for secondary highlights and status indicators.");
        
        ImGui.Spacing();
        
        // Text Colors
        ImGui.Text("Text Colors");
        ImGui.Separator();
        
        var textPrimary = theme.TextPrimary;
        if (ImGui.ColorEdit4("Text Primary", ref textPrimary))
        {
            theme.TextPrimary = textPrimary;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Primary text color used for most readable text throughout the interface.");
        
        var textSecondary = theme.TextSecondary;
        if (ImGui.ColorEdit4("Text Secondary", ref textSecondary))
        {
            theme.TextSecondary = textSecondary;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Secondary text color used for less important text and descriptions.");
        
        var textDisabled = theme.TextDisabled;
        if (ImGui.ColorEdit4("Text Disabled", ref textDisabled))
        {
            theme.TextDisabled = textDisabled;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Text color for disabled or inactive elements that cannot be interacted with.");
        
        var textSelectedBg = theme.TextSelectedBg;
        if (ImGui.ColorEdit4("Text Selected Background", ref textSelectedBg))
        {
            theme.TextSelectedBg = textSelectedBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for selected text and highlighted text areas.");
        
        ImGui.Spacing();
        
        // Interactive Colors
        ImGui.Text("Interactive Colors");
        ImGui.Separator();
        
        var border = theme.Border;
        if (ImGui.ColorEdit4("Border", ref border))
        {
            theme.Border = border;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color used for borders around windows, frames, and input fields.");
        
        var hover = theme.Hover;
        if (ImGui.ColorEdit4("Hover", ref hover))
        {
            theme.Hover = hover;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color used when hovering over interactive elements like buttons and menu items.");
        
        var active = theme.Active;
        if (ImGui.ColorEdit4("Active", ref active))
        {
            theme.Active = active;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color used when actively clicking or pressing interactive elements.");
    }
    
    private void DrawWindowColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        UiSharedService.ColorTextWrapped("Window colors control the appearance of different window types and their backgrounds.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        ImGui.Text("Window Colors");
        ImGui.Separator();
        
        var windowBg = theme.WindowBg;
        if (ImGui.ColorEdit4("Window Background", ref windowBg))
        {
            theme.WindowBg = windowBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Main background color for all windows and dialogs.");
        
        var childBg = theme.ChildBg;
        if (ImGui.ColorEdit4("Child Background", ref childBg))
        {
            theme.ChildBg = childBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for child windows and nested content areas.");
        
        var popupBg = theme.PopupBg;
        if (ImGui.ColorEdit4("Popup Background", ref popupBg))
        {
            theme.PopupBg = popupBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for popup windows, tooltips, and context menus.");
        
        ImGui.Spacing();
        ImGui.Text("Title Bar Colors");
        ImGui.Separator();
        
        var titleBg = theme.TitleBg;
        if (ImGui.ColorEdit4("Title Background", ref titleBg))
        {
            theme.TitleBg = titleBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for inactive window title bars.");
        
        var titleBgActive = theme.TitleBgActive;
        if (ImGui.ColorEdit4("Title Background Active", ref titleBgActive))
        {
            theme.TitleBgActive = titleBgActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for active (focused) window title bars.");
        
        var titleBgCollapsed = theme.TitleBgCollapsed;
        if (ImGui.ColorEdit4("Title Background Collapsed", ref titleBgCollapsed))
        {
            theme.TitleBgCollapsed = titleBgCollapsed;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for collapsed window title bars.");
        
        ImGui.Spacing();
        ImGui.Text("Modal & Overlay Colors");
        ImGui.Separator();
        
        var modalWindowDimBg = theme.ModalWindowDimBg;
        if (ImGui.ColorEdit4("Modal Window Dim Background", ref modalWindowDimBg))
        {
            theme.ModalWindowDimBg = modalWindowDimBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Dimming overlay color behind modal dialogs and popups.");
    }
    
    private void DrawFrameInputColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        UiSharedService.ColorTextWrapped("Frame and input colors control the appearance of input fields, checkboxes, and interactive elements.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        ImGui.Text("Frame & Input Colors");
        ImGui.Separator();
        
        var frameBg = theme.FrameBg;
        if (ImGui.ColorEdit4("Frame Background", ref frameBg))
        {
            theme.FrameBg = frameBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for input fields, sliders, and other frame elements.");
        
        var frameBgHovered = theme.FrameBgHovered;
        if (ImGui.ColorEdit4("Frame Background Hovered", ref frameBgHovered))
        {
            theme.FrameBgHovered = frameBgHovered;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for frame elements when hovered over.");
        
        var frameBgActive = theme.FrameBgActive;
        if (ImGui.ColorEdit4("Frame Background Active", ref frameBgActive))
        {
            theme.FrameBgActive = frameBgActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for frame elements when actively being used.");
        
        ImGui.Spacing();
        ImGui.Text("Interactive Elements");
        ImGui.Separator();
        
        var checkMark = theme.CheckMark;
        if (ImGui.ColorEdit4("Check Mark", ref checkMark))
        {
            theme.CheckMark = checkMark;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for checkmarks in checkboxes and radio buttons.");
        
        ImGui.Spacing();
        ImGui.Text("Window Controls");
        ImGui.Separator();
        
        var resizeGrip = theme.ResizeGrip;
        if (ImGui.ColorEdit4("Resize Grip", ref resizeGrip))
        {
            theme.ResizeGrip = resizeGrip;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for the resize grip in the bottom-right corner of windows.");
        
        var resizeGripHovered = theme.ResizeGripHovered;
        if (ImGui.ColorEdit4("Resize Grip Hovered", ref resizeGripHovered))
        {
            theme.ResizeGripHovered = resizeGripHovered;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for the resize grip when hovered over.");
        
        var resizeGripActive = theme.ResizeGripActive;
        if (ImGui.ColorEdit4("Resize Grip Active", ref resizeGripActive))
        {
            theme.ResizeGripActive = resizeGripActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for the resize grip when actively being dragged.");
    }
    
    private void DrawButtonHeaderColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        UiSharedService.ColorTextWrapped("Button and header colors control the appearance of clickable elements and section headers.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        ImGui.Text("Button Colors");
        ImGui.Separator();
        
        var button = theme.Button;
        if (ImGui.ColorEdit4("Button", ref button))
        {
            theme.Button = button;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Default background color for buttons.");
        
        var buttonHovered = theme.ButtonHovered;
        if (ImGui.ColorEdit4("Button Hovered", ref buttonHovered))
        {
            theme.ButtonHovered = buttonHovered;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for buttons when hovered over.");
        
        var buttonActive = theme.ButtonActive;
        if (ImGui.ColorEdit4("Button Active", ref buttonActive))
        {
            theme.ButtonActive = buttonActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for buttons when clicked or pressed.");
        
        ImGui.Spacing();
        ImGui.Text("Header Colors");
        ImGui.Separator();
        
        var headerBg = theme.HeaderBg;
        if (ImGui.ColorEdit4("Header Background", ref headerBg))
        {
            theme.HeaderBg = headerBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for collapsible headers and section titles.");
        
        var headerHovered = theme.HeaderHovered;
        if (ImGui.ColorEdit4("Header Hovered", ref headerHovered))
        {
            theme.HeaderHovered = headerHovered;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for headers when hovered over.");
        
        var headerActive = theme.HeaderActive;
        if (ImGui.ColorEdit4("Header Active", ref headerActive))
        {
            theme.HeaderActive = headerActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for headers when clicked or active.");
        
        ImGui.Spacing();
        ImGui.Text("Separators");
        ImGui.Separator();
        
        var separator = theme.Separator;
        if (ImGui.ColorEdit4("Separator", ref separator))
        {
            theme.Separator = separator;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for separator lines that divide sections.");
        
        var separatorHovered = theme.SeparatorHovered;
        if (ImGui.ColorEdit4("Separator Hovered", ref separatorHovered))
        {
            theme.SeparatorHovered = separatorHovered;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for separator lines when hovered over.");
        
        var separatorActive = theme.SeparatorActive;
        if (ImGui.ColorEdit4("Separator Active", ref separatorActive))
        {
            theme.SeparatorActive = separatorActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for separator lines when being dragged or active.");
    }
    
    private void DrawMenuNavigationColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        UiSharedService.ColorTextWrapped("Menu and navigation colors control the appearance of menu bars, navigation highlights, and drag-drop interactions.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        ImGui.Text("Menu Colors");
        ImGui.Separator();
        
        var menuBarBg = theme.MenuBarBg;
        if (ImGui.ColorEdit4("Menu Bar Background", ref menuBarBg))
        {
            theme.MenuBarBg = menuBarBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for menu bars at the top of windows.");
        
        ImGui.Spacing();
        ImGui.Text("Navigation Colors");
        ImGui.Separator();
        
        var navHighlight = theme.NavHighlight;
        if (ImGui.ColorEdit4("Navigation Highlight", ref navHighlight))
        {
            theme.NavHighlight = navHighlight;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for highlighting the currently focused navigation element.");
        
        var navWindowingHighlight = theme.NavWindowingHighlight;
        if (ImGui.ColorEdit4("Navigation Windowing Highlight", ref navWindowingHighlight))
        {
            theme.NavWindowingHighlight = navWindowingHighlight;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for highlighting windows during navigation mode.");
        
        var navWindowingDimBg = theme.NavWindowingDimBg;
        if (ImGui.ColorEdit4("Navigation Windowing Dim Background", ref navWindowingDimBg))
        {
            theme.NavWindowingDimBg = navWindowingDimBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background dimming color during window navigation mode.");
        
        ImGui.Spacing();
        ImGui.Text("Drag & Drop Colors");
        ImGui.Separator();
        
        var dragDropTarget = theme.DragDropTarget;
        if (ImGui.ColorEdit4("Drag Drop Target", ref dragDropTarget))
        {
            theme.DragDropTarget = dragDropTarget;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for highlighting valid drop targets during drag operations.");
    }
    
    private void DrawScrollbarSliderColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        UiSharedService.ColorTextWrapped("Scrollbar and slider colors control the appearance of scrollbars and slider controls throughout the interface.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        ImGui.Text("Scrollbar Colors");
        ImGui.Separator();
        
        var scrollbarBg = theme.ScrollbarBg;
        if (ImGui.ColorEdit4("Scrollbar Background", ref scrollbarBg))
        {
            theme.ScrollbarBg = scrollbarBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for the scrollbar track.");
        
        var scrollbarGrab = theme.ScrollbarGrab;
        if (ImGui.ColorEdit4("Scrollbar Grab", ref scrollbarGrab))
        {
            theme.ScrollbarGrab = scrollbarGrab;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for the scrollbar handle that you can drag.");
        
        var scrollbarGrabHovered = theme.ScrollbarGrabHovered;
        if (ImGui.ColorEdit4("Scrollbar Grab Hovered", ref scrollbarGrabHovered))
        {
            theme.ScrollbarGrabHovered = scrollbarGrabHovered;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for the scrollbar handle when hovered over.");
        
        var scrollbarGrabActive = theme.ScrollbarGrabActive;
        if (ImGui.ColorEdit4("Scrollbar Grab Active", ref scrollbarGrabActive))
        {
            theme.ScrollbarGrabActive = scrollbarGrabActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for the scrollbar handle when being dragged.");
        
        ImGui.Spacing();
        ImGui.Text("Slider Colors");
        ImGui.Separator();
        
        var sliderGrab = theme.SliderGrab;
        if (ImGui.ColorEdit4("Slider Grab", ref sliderGrab))
        {
            theme.SliderGrab = sliderGrab;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for slider handles and knobs.");
        
        var sliderGrabActive = theme.SliderGrabActive;
        if (ImGui.ColorEdit4("Slider Grab Active", ref sliderGrabActive))
        {
            theme.SliderGrabActive = sliderGrabActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for slider handles when being dragged.");
    }
    
    private void DrawTableTabColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        UiSharedService.ColorTextWrapped("Table and tab colors control the appearance of data tables and tab navigation elements.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        ImGui.Text("Table Colors");
        ImGui.Separator();
        
        var tableHeaderBg = theme.TableHeaderBg;
        if (ImGui.ColorEdit4("Table Header Background", ref tableHeaderBg))
        {
            theme.TableHeaderBg = tableHeaderBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for table headers and column titles.");
        
        var tableBorderStrong = theme.TableBorderStrong;
        if (ImGui.ColorEdit4("Table Border Strong", ref tableBorderStrong))
        {
            theme.TableBorderStrong = tableBorderStrong;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for strong table borders (outer borders and major divisions).");
        
        var tableBorderLight = theme.TableBorderLight;
        if (ImGui.ColorEdit4("Table Border Light", ref tableBorderLight))
        {
            theme.TableBorderLight = tableBorderLight;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Color for light table borders (inner cell borders).");
        
        var tableRowBg = theme.TableRowBg;
        if (ImGui.ColorEdit4("Table Row Background", ref tableRowBg))
        {
            theme.TableRowBg = tableRowBg;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for table rows.");
        
        var tableRowBgAlt = theme.TableRowBgAlt;
        if (ImGui.ColorEdit4("Table Row Background Alt", ref tableRowBgAlt))
        {
            theme.TableRowBgAlt = tableRowBgAlt;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Alternating background color for table rows (zebra striping).");
        
        ImGui.Spacing();
        ImGui.Text("Tab Colors");
        ImGui.Separator();
        
        var tab = theme.Tab;
        if (ImGui.ColorEdit4("Tab", ref tab))
        {
            theme.Tab = tab;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Default background color for inactive tabs.");
        
        var tabHovered = theme.TabHovered;
        if (ImGui.ColorEdit4("Tab Hovered", ref tabHovered))
        {
            theme.TabHovered = tabHovered;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for tabs when hovered over.");
        
        var tabActive = theme.TabActive;
        if (ImGui.ColorEdit4("Tab Active", ref tabActive))
        {
            theme.TabActive = tabActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for the currently active tab.");
        
        var tabUnfocused = theme.TabUnfocused;
        if (ImGui.ColorEdit4("Tab Unfocused", ref tabUnfocused))
        {
            theme.TabUnfocused = tabUnfocused;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for tabs when the window is not focused.");
        
        var tabUnfocusedActive = theme.TabUnfocusedActive;
        if (ImGui.ColorEdit4("Tab Unfocused Active", ref tabUnfocusedActive))
        {
            theme.TabUnfocusedActive = tabUnfocusedActive;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("Background color for the active tab when the window is not focused.");
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
        ImGui.Text("Save Current Theme");
        ImGui.Separator();
        
        ImGui.Text("Theme Name:");
        ImGui.InputText("##SaveThemeName", ref _saveThemeName, 100);
        
        ImGui.Spacing();
        
        if (ImGui.Button("Save") && !string.IsNullOrWhiteSpace(_saveThemeName))
        {
            _ = Task.Run(async () =>
            {
                var success = await ThemeManager.SaveTheme(SpheneCustomTheme.CurrentTheme, _saveThemeName).ConfigureAwait(false);
                if (success)
                {
                    _saveThemeName = "";
                }
            });
            ImGui.CloseCurrentPopup();
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Cancel"))
        {
            _saveThemeName = "";
            ImGui.CloseCurrentPopup();
        }
    }

    

    

    private void DrawThemeSelector()
    {
        ImGui.Text("Themes");
        ImGui.SameLine();
        _uiShared.DrawHelpText("Select from built-in themes or your custom saved themes. Changes apply immediately.");
        
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
        
        ImGui.SetNextItemWidth(300);
        if (ImGui.Combo("##ThemeSelector", ref currentIndex, allThemes.ToArray(), allThemes.Count))
        {
            var selectedTheme = allThemes[currentIndex];
            
            // Skip separator entries
            if (selectedTheme.StartsWith("---", StringComparison.Ordinal)) return;
            
            _selectedPresetTheme = selectedTheme;
            ApplySelectedPresetTheme(selectedTheme, builtInThemes);
        }
        
        ImGui.SameLine();
        var canDelete = !string.IsNullOrEmpty(_selectedPresetTheme) && !builtInThemes.Contains(_selectedPresetTheme, StringComparer.Ordinal) && !_selectedPresetTheme.StartsWith("---", StringComparison.Ordinal);
        using (ImRaii.Disabled(!canDelete || !UiSharedService.CtrlPressed()))
        {
            if (_uiShared.IconButton(FontAwesomeIcon.Trash) && ThemeManager.DeleteTheme(_selectedPresetTheme))
            {
                _selectedPresetTheme = "Default Sphene";
                ApplySelectedPresetTheme("Default Sphene", builtInThemes);
            }
            UiSharedService.AttachToolTip(canDelete
                ? ($"Hold CTRL to delete selected custom theme: '{_selectedPresetTheme}'")
                : "Only custom themes can be deleted");
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Reset to Default Sphene Theme"))
        {
            _selectedPresetTheme = "Default Sphene";
            ApplySelectedPresetTheme("Default Sphene", builtInThemes);
        }
        
        // Theme Management Actions
        ImGui.Spacing();

        if (ImGui.Button("New Theme"))
        {
            _saveThemeName = "";
            ImGui.OpenPopup("SaveThemePopup");
        }

        ImGui.SameLine();
        if (!_hasUnsavedThemeChanges) ImGui.BeginDisabled();
        if (ImGui.Button("Save Theme Changes"))
        {
            if (!string.IsNullOrEmpty(_currentThemeName) && !builtInThemes.Contains(_currentThemeName, StringComparer.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    var success = await ThemeManager.SaveTheme(SpheneCustomTheme.CurrentTheme, _currentThemeName).ConfigureAwait(false);
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
        if (!_hasUnsavedThemeChanges) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!_hasUnsavedThemeChanges) ImGui.BeginDisabled();
        if (ImGui.Button("Reset Theme Changes"))
        {
            if (_originalThemeState != null)
            {
                ThemePropertyCopier.Copy(_originalThemeState, SpheneCustomTheme.CurrentTheme);
            }
            else
            {
                SpheneCustomTheme.CurrentTheme.ResetToDefaults();
            }
            SpheneCustomTheme.CurrentTheme.NotifyThemeChanged();
            _hasUnsavedThemeChanges = false;
        }
        if (!_hasUnsavedThemeChanges) ImGui.EndDisabled();
        
        // Save Theme Popup
        if (ImGui.BeginPopup("SaveThemePopup"))
        {
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                DrawSaveThemePopup();
            }
            ImGui.EndPopup();
        }
    }

    private void ApplySelectedPresetTheme(string themeName, List<string> builtInThemes)
    {
        var currentTheme = SpheneCustomTheme.CurrentTheme;
        
        if (builtInThemes.Contains(themeName, StringComparer.Ordinal))
        {
            // Apply built-in theme
            var presetTheme = ThemePresets.BuiltInThemes[themeName];
            ThemePropertyCopier.Copy(presetTheme, currentTheme);
        }
        else
        {
            // Apply custom theme
            var loadedTheme = ThemeManager.LoadTheme(themeName);
            if (loadedTheme != null)
            {
                ThemePropertyCopier.Copy(loadedTheme, currentTheme);
            }
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
            _secretKeysConversionCts?.Cancel();
            _secretKeysConversionCts?.Dispose();
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
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                _uiShared.IconText(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
                ImGui.SameLine();
                _uiShared.BigText("Unsaved Theme Changes");
                ImGuiHelpers.ScaledDummy(8f);
                UiSharedService.ColorTextWrapped("You have modified theme settings that are not yet saved.", ImGuiColors.DalamudYellow);
                ImGuiHelpers.ScaledDummy(4f);
                UiSharedService.ColorTextWrapped("Save them as a new theme, keep them temporarily, or discard.", ImGuiColors.DalamudGrey);
                ImGuiHelpers.ScaledDummy(10f);
                ImGui.Text("Theme Name:");
                ImGui.InputText("##NewThemeName", ref _saveThemeName, 100);
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
                if (ImGui.Button(btn1) && !string.IsNullOrWhiteSpace(_saveThemeName))
                {
                    var themeNameToSave = _saveThemeName;
                    _ = Task.Run(async () =>
                    {
                        var success = await ThemeManager.SaveTheme(SpheneCustomTheme.CurrentTheme, themeNameToSave).ConfigureAwait(false);
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
                if (ImGui.Button(btn2))
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
                if (ImGui.Button(btn3))
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
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                _uiShared.IconText(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
                ImGui.SameLine();
                _uiShared.BigText("Unsaved Custom Theme Changes");
                ImGuiHelpers.ScaledDummy(8f);
                UiSharedService.ColorTextWrapped("These changes are not saved to your current custom theme.", ImGuiColors.DalamudYellow);
                ImGuiHelpers.ScaledDummy(10f);
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
                if (ImGui.Button(lbl1))
                {
                    if (!string.IsNullOrEmpty(_currentThemeName))
                    {
                        var themeNameToSave = _currentThemeName;
                        _ = Task.Run(async () =>
                        {
                            var success = await ThemeManager.SaveTheme(SpheneCustomTheme.CurrentTheme, themeNameToSave).ConfigureAwait(false);
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
                if (ImGui.Button(lbl2))
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
                if (ImGui.Button(lbl3))
                {
                    _hasUnsavedThemeChanges = false;
                    _showUnsavedCustomThemePrompt = false;
                    IsOpen = false;
                }
            }
            ImGui.End();
        }
    }
}
