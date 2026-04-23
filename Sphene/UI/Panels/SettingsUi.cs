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
        SyncBehavior,
        Acknowledgment,
        Debug
    }
    private SettingsPage _activeSettingsPage = SettingsPage.Home;
    private bool _preferPanelThemeTab = false;
    private bool _preferIconThemeTab = false;
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
        DrawSettingsPageHeader("Transfers", "Control transfer limits, monitor behavior, and transmission visuals.");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Global Receive Limit");
        ImGui.SameLine();
        TransfersOptionBlock.DrawGlobalReceiveLimitValueOption(_configService, Mediator, ClampSettingsItemWidth(100f), "TransfersGlobalReceiveLimitValue");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ClampSettingsItemWidth(100f));
        TransfersOptionBlock.DrawGlobalReceiveLimitSpeedUnitOption(_configService, _uiShared, Mediator, "TransfersGlobalReceiveLimitSpeedUnit");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("0 = No limit/infinite");

        TransfersOptionBlock.DrawMaximumParallelDataStreamsOption(_configService, "TransfersMaximumParallelDataStreams");
        TransfersOptionBlock.DrawUseSpheneCdnDirectDownloadsOption(_configService, _uiShared, "TransfersUseSpheneCdnDirectDownloads");
        TransfersOptionBlock.DrawAllowReceivingPenumbraModPackagesOption(_configService, _uiShared, ApiController, "TransfersAllowReceivingPenumbraModPackages");
        TransfersOptionBlock.DrawUseAlternativeTransmissionMethodOption(_configService, _uiShared, "TransfersUseAlternativeTransmissionMethod");

        DrawSettingsSectionHeader("Transfer Monitor");

        var showTransferWindow = TransfersOptionBlock.DrawShowSeparateTransmissionMonitorOption(_configService, _uiShared, "TransfersShowSeparateTransmissionMonitor");
        if (!showTransferWindow) ImGui.BeginDisabled();
        ImGui.Indent();
        TransfersOptionBlock.DrawEditTransmissionMonitorPositionOption(_uiShared, "TransfersEditTransmissionMonitorPosition");
        ImGui.Unindent();
        if (!showTransferWindow) ImGui.EndDisabled();

        var showTransferBars = TransfersOptionBlock.DrawShowTransmissionIndicatorsBelowPlayersOption(_configService, _uiShared, "TransfersShowTransmissionIndicatorsBelowPlayers");

        if (!showTransferBars) ImGui.BeginDisabled();
        ImGui.Indent();
        TransfersOptionBlock.DrawShowTransmissionTextOption(_configService, _uiShared, "TransfersShowTransmissionText");
        TransfersOptionBlock.DrawTransmissionIndicatorWidthOption(_configService, _uiShared, "TransfersTransmissionIndicatorWidth");
        TransfersOptionBlock.DrawTransmissionIndicatorHeightOption(_configService, _uiShared, "TransfersTransmissionIndicatorHeight");
        var showUploading = TransfersOptionBlock.DrawShowTransmittingTextBelowPlayersOption(_configService, _uiShared, "TransfersShowTransmittingTextBelowPlayers");

        ImGui.Unindent();
        if (!showUploading) ImGui.BeginDisabled();
        ImGui.Indent();
        TransfersOptionBlock.DrawLargeFontForTransmittingTextOption(_configService, _uiShared, "TransfersLargeFontForTransmittingText");

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
#if IS_TEST_BUILD
        pageLabel = "Debug Diagnostics";
#else
        pageLabel = "Diagnostics";
#endif

        DrawSettingsPageHeader(pageLabel, "Diagnostics and troubleshooting tools for sync, logs, and runtime status.");
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

        DebugOptionBlock.DrawLogLevelOption(_configService, _uiShared, "DebugLogLevel");

        ImGuiHelpers.ScaledDummy(5);
        _uiShared.BigText("Performance Metrics");

        bool logPerformance = DebugOptionBlock.DrawLogNetworkPerformanceMetricsOption(_configService, _uiShared, "DebugLogNetworkPerformanceMetrics");
        DebugOptionBlock.DrawPrintNetworkMetricsActions(_uiShared, logPerformance, () => _performanceCollector.PrintPerformanceStats(), () => _performanceCollector.PrintPerformanceStats(60), "DebugPrintNetworkMetricsActions");
        DebugOptionBlock.DrawDoNotNotifyForModifiedGameFilesOrEnabledLodOption(_configService, _uiShared, "DebugDoNotNotifyForModifiedGameFilesOrEnabledLod");

        DrawSettingsSectionHeader("Diagnostic Windows", "Open dedicated windows for acknowledgment and status monitoring.");
        
        DebugOptionBlock.DrawOpenAcknowledgmentMonitorAction(_uiShared, Mediator, "DebugOpenAcknowledgmentMonitor");
        DebugOptionBlock.DrawOpenStatusDebugAction(_uiShared, Mediator, "DebugOpenStatusDebug");

        DrawSettingsSectionHeader("Active Mismatch Tracker", "Control which paths and file types are recorded and shown.");
        DebugOptionBlock.DrawActiveMismatchTrackerFilterOptions(_configService, _uiShared, "DebugMismatchTrackerFilters");
        

    }

    private void DrawFileStorageSettings()
    {
        DrawSettingsPageHeader("Storage",
            "Sphene stores downloaded files from paired users to improve loading speed and reduce repeated downloads. Storage is self-managed by size limit.");

        _uiShared.DrawFileScanState();
        DrawSettingsSectionHeader("Monitoring");
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

        StorageOptionBlock.DrawDeleteDownloadedModsAfterSuccessfulInstallOption(_configService, _uiShared, "StorageDeleteDownloadedModsAfterSuccessfulInstall");

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
        StorageOptionBlock.DrawUseFileCompactorOption(_configService, _uiShared, "StorageUseFileCompactor");
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

        DrawSettingsSectionHeader("Storage Validation",
            "Validation checks local storage integrity and removes invalid files. This can take time and may be CPU and disk intensive.");
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
        DrawSettingsSectionHeader("Clear Local Storage", "Read and accept the disclaimer before running this action.");
        ImGui.Indent();
        StorageOptionBlock.DrawReadClearLocalStorageDisclaimerOption(ref _readClearCache, "StorageReadClearLocalStorageDisclaimer");
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
        DrawSettingsPageHeader("Performance",
            "Configure warning indicators and automatic actions for performance-heavy synced players.");
        DrawSettingsSectionHeader("Warnings & Indicators");
        bool showPerformanceIndicator = PerformanceOptionBlock.DrawShowPerformanceIndicatorOption(_playerPerformanceConfigService, _uiShared, "PerformanceShowPerformanceIndicator");
        bool warnOnExceedingThresholds = PerformanceOptionBlock.DrawWarnOnLoadingInPlayersExceedingThresholdsOption(_playerPerformanceConfigService, _uiShared, "PerformanceWarnOnLoadingInPlayersExceedingThresholds");
        using (ImRaii.Disabled(!warnOnExceedingThresholds && !showPerformanceIndicator))
        {
            using var indent = ImRaii.PushIndent();
            PerformanceOptionBlock.DrawWarnIndicateAlsoOnPreferredPermissionsOption(_playerPerformanceConfigService, _uiShared, "PerformanceWarnIndicateAlsoOnPreferredPermissions");
        }
        using (ImRaii.Disabled(!showPerformanceIndicator && !warnOnExceedingThresholds))
        {
            PerformanceOptionBlock.DrawWarningVramThresholdOption(_playerPerformanceConfigService, _uiShared, ClampSettingsItemWidth(100f), "PerformanceWarningVramThreshold");
            PerformanceOptionBlock.DrawWarningTriangleThresholdOption(_playerPerformanceConfigService, _uiShared, ClampSettingsItemWidth(100f), "PerformanceWarningTriangleThreshold");
        }
        DrawSettingsSectionHeader("Auto Pause");
        bool autoPause = PerformanceOptionBlock.DrawAutomaticallyPausePlayersExceedingThresholdsOption(_playerPerformanceConfigService, _uiShared, "PerformanceAutomaticallyPausePlayersExceedingThresholds");
        using (ImRaii.Disabled(!autoPause))
        {
            using var indent = ImRaii.PushIndent();
            PerformanceOptionBlock.DrawAutomaticallyPauseAlsoPreferredPermissionsOption(_playerPerformanceConfigService, _uiShared, "PerformanceAutomaticallyPauseAlsoPreferredPermissions");
            PerformanceOptionBlock.DrawAutoPauseVramThresholdOption(_playerPerformanceConfigService, _uiShared, ClampSettingsItemWidth(100f), "PerformanceAutoPauseVramThreshold");
            PerformanceOptionBlock.DrawAutoPauseTriangleThresholdOption(_playerPerformanceConfigService, _uiShared, ClampSettingsItemWidth(100f), "PerformanceAutoPauseTriangleThreshold");
        }
        DrawSettingsSectionHeader("Whitelisted UIDs", "Entries below are ignored for warnings and auto-pause operations.");
        ImGui.SetNextItemWidth(ClampSettingsItemWidth(220f, 140f));
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
        ImGui.SetNextItemWidth(ClampSettingsItemWidth(420f, 200f));
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
        DrawSettingsPageHeader("Connectivity", "Manage server connection, account actions, and service-related settings.");
        if (ApiController.ServerAlive)
        {
            DrawSettingsSectionHeader("Service Actions");
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
        ConnectivityOptionBlock.DrawSendStatisticalCensusDataOption(_serverConfigurationManager, _uiShared, "ConnectivitySendStatisticalCensusData");
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
                        ConnectivityOptionBlock.DrawCharacterAutoLoginOption(item, _serverConfigurationManager, _uiShared, "ConnectivityCharacterAutoLogin");
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

                ConnectivityOptionBlock.DrawServerTransportTypeOption(_serverConfigurationManager, _uiShared, ClampSettingsItemWidth(220f, 140f), "ConnectivityServerTransportType");

                if (_dalamudUtilService.IsWine)
                {
                    ConnectivityOptionBlock.DrawWineForceWebSocketsOption(selectedServer, _serverConfigurationManager, _uiShared, "ConnectivityWineForceWebSockets");
                }

                ImGuiHelpers.ScaledDummy(5);

                ConnectivityOptionBlock.DrawUseDiscordOAuthOption(selectedServer, useOauth, _serverConfigurationManager, _uiShared, "ConnectivityUseDiscordOAuth");

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
                    ConnectivityOptionBlock.DrawPreferredPermissionsOption(perms, _apiController, _uiShared, "ConnectivityPreferredPermissions");
                    ImGuiHelpers.ScaledDummy(3f);
                    ConnectivityOptionBlock.DrawDisableIndividualPairSoundsOption(perms, _apiController, _uiShared, "ConnectivityDisableIndividualPairSounds");
                    ConnectivityOptionBlock.DrawDisableIndividualPairAnimationsOption(perms, _apiController, _uiShared, "ConnectivityDisableIndividualPairAnimations");
                    ConnectivityOptionBlock.DrawDisableIndividualPairVfxOption(perms, _apiController, _uiShared, "ConnectivityDisableIndividualPairVfx");
                    ImGuiHelpers.ScaledDummy(5f);
                    ConnectivityOptionBlock.DrawDisableSyncshellPairSoundsOption(perms, _apiController, _uiShared, "ConnectivityDisableSyncshellPairSounds");
                    ConnectivityOptionBlock.DrawDisableSyncshellPairAnimationsOption(perms, _apiController, _uiShared, "ConnectivityDisableSyncshellPairAnimations");
                    ConnectivityOptionBlock.DrawDisableSyncshellPairVfxOption(perms, _apiController, _uiShared, "ConnectivityDisableSyncshellPairVfx");
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
        var available = ImGui.GetContentRegionAvail();

        string diagnosticsPageLabel;
#if IS_TEST_BUILD
        diagnosticsPageLabel = "Debug Build";
#else
        diagnosticsPageLabel = "Diagnostics";
#endif

        var buttonLabels = new[] { "Home", "Connectivity", "People & Notes", "Appearance", "Theme", "Notifications", "Performance", "Transfers", "Storage", "Sync", "Acknowledgment", diagnosticsPageLabel };
        var maxTextWidth = 0f;
        foreach (var label in buttonLabels)
        {
            var textSize = ImGui.CalcTextSize(label);
            if (textSize.X > maxTextWidth)
                maxTextWidth = textSize.X;
        }
        var style = ImGui.GetStyle();
        var sidebarButtonPaddingX = MathF.Max(4f * ImGuiHelpers.GlobalScale, style.FramePadding.X * 0.65f);
        var sidebarButtonInnerWidth = maxTextWidth + (sidebarButtonPaddingX * 2f);
        var sidebarWidth = sidebarButtonInnerWidth + (style.WindowPadding.X * 2f) + (2f * ImGuiHelpers.GlobalScale);

        ImGui.BeginChild("settings-sidebar", new Vector2(sidebarWidth, available.Y), true);

        void SidebarCategory(string label)
        {
            ImGuiHelpers.ScaledDummy(0, 5);
            UiSharedService.ColorText(label.ToUpperInvariant(), ImGuiColors.ParsedBlue);
            ImGuiHelpers.ScaledDummy(0, 3);
        }

        void SidebarButton(string label, SettingsPage page)
        {
            var buttonSize = new Vector2(-1, 24f * ImGuiHelpers.GlobalScale);
            var isActive = _activeSettingsPage == page;
            using var buttonPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(sidebarButtonPaddingX, style.FramePadding.Y));
            using var buttonRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f * ImGuiHelpers.GlobalScale);
            if (isActive)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.ParsedBlue);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGuiColors.ParsedBlue);
            }
            if (ImGui.Button(label, buttonSize))
            {
                _activeSettingsPage = page;
            }
            if (isActive)
            {
                ImGui.PopStyleColor(2);
            }
        }

        SidebarCategory("General");
        SidebarButton("Home", SettingsPage.Home);
        SidebarButton("Connectivity", SettingsPage.Connectivity);
        SidebarButton("People & Notes", SettingsPage.PeopleNotes);
        SidebarButton("Notifications", SettingsPage.Alerts);
        ImGui.Separator();

        SidebarCategory("Appearance");
        SidebarButton("Appearance", SettingsPage.Display);
        SidebarButton("Theme", SettingsPage.Theme);
        ImGui.Separator();

        SidebarCategory("Sync & Data");
        SidebarButton("Sync", SettingsPage.SyncBehavior);
        SidebarButton("Acknowledgment", SettingsPage.Acknowledgment);
        SidebarButton("Transfers", SettingsPage.Transfers);
        SidebarButton("Storage", SettingsPage.Storage);
        ImGui.Separator();

        SidebarCategory("Advanced");
        SidebarButton("Performance", SettingsPage.Performance);
        SidebarButton(diagnosticsPageLabel, SettingsPage.Debug);

        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("settings-content", new Vector2(available.X - sidebarWidth - ImGui.GetStyle().ItemSpacing.X, available.Y), false);
        var contentAvail = ImGui.GetContentRegionAvail().X;
        var scrollbarReserve = ImGui.GetStyle().ScrollbarSize + 10f * ImGuiHelpers.GlobalScale;
        var safeContentWidth = MathF.Max(220f * ImGuiHelpers.GlobalScale, contentAvail - scrollbarReserve);
        var maxPageWidth = MathF.Min(safeContentWidth, 1040f * ImGuiHelpers.GlobalScale);
        var pageStartX = ImGui.GetCursorPosX();
        if (contentAvail > maxPageWidth)
        {
            pageStartX = ImGui.GetCursorPosX() + (contentAvail - maxPageWidth) * 0.5f;
            ImGui.SetCursorPosX(pageStartX);
        }

        var pageInfo = _activeSettingsPage switch
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
            SettingsPage.Debug => ("Advanced", diagnosticsPageLabel),
            _ => ("General", "Home")
        };

        using var pageGroup = ImRaii.Group();
        ImGui.PushTextWrapPos(pageStartX + maxPageWidth - 6f * ImGuiHelpers.GlobalScale);
        ImGui.PushItemWidth(MathF.Min(360f * ImGuiHelpers.GlobalScale, maxPageWidth * 0.6f));
        UiSharedService.ColorText(pageInfo.Item1, ImGuiColors.DalamudGrey);
        ImGui.SameLine();
        UiSharedService.ColorText("•", ImGuiColors.DalamudGrey3);
        ImGui.SameLine();
        ImGui.TextUnformatted(pageInfo.Item2);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0, 4);

        var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
        IDisposable? contentIndent = null;
        if (_activeSettingsPage != SettingsPage.Home)
        {
            contentIndent = ImRaii.PushIndent(settingsOptionsIndentX);
        }

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
            case SettingsPage.Debug:
                DrawDebug();
                break;
        }
        contentIndent?.Dispose();
        ImGui.PopItemWidth();
        ImGui.PopTextWrapPos();

        ImGui.EndChild();
    }

    private void DrawSettingsPageHeader(string tabKey, string? description = null)
    {
        var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Unindent(settingsOptionsIndentX);
        _lastTab = tabKey;
        if (!string.IsNullOrWhiteSpace(description))
        {
            UiSharedService.ColorTextWrapped(description, ImGuiColors.DalamudGrey);
            ImGuiHelpers.ScaledDummy(0, 6);
        }
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Indent(settingsOptionsIndentX);
    }

    private void DrawSettingsSectionHeader(string title, string? description = null)
    {
        var settingsOptionsIndentX = GetSettingsOptionsIndentX(_activeSettingsPage);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Unindent(settingsOptionsIndentX);
        ImGuiHelpers.ScaledDummy(0, 4);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0, 5);
        UiSharedService.ColorText(title, ImGuiColors.ParsedBlue);
        if (!string.IsNullOrWhiteSpace(description))
        {
            UiSharedService.ColorTextWrapped(description, ImGuiColors.DalamudGrey);
        }
        ImGuiHelpers.ScaledDummy(0, 5);
        if (_activeSettingsPage != SettingsPage.Home)
            ImGui.Indent(settingsOptionsIndentX);
    }

    private static float GetSettingsOptionsIndentX(SettingsPage page)
        => page switch
        {
            SettingsPage.Theme => 8f * ImGuiHelpers.GlobalScale,
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

        var max = ImGui.GetItemRectMax();
        // Add padding
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
        DrawSettingsPageHeader("People & Notes", "Manage note import/export and default note popup behavior.");

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
        PeopleNotesOptionBlock.DrawOverwriteExistingLabelsOption(ref overwriteExistingLabels, _uiShared, "PeopleNotesOverwriteExistingLabels");
        _overwriteExistingLabels = overwriteExistingLabels;

        if (_notesSuccessfullyApplied is not null)
        {
            ImGui.Spacing();
            if (_notesSuccessfullyApplied!.Value)
                ImGui.TextColored(ImGuiColors.ParsedGreen, "Notes successfully applied.");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, "Failed to apply notes.");
        }

        DrawSettingsSectionHeader("Labels & Popups");
        PeopleNotesOptionBlock.DrawOpenNotesPopupOnUserAdditionOption(_configService, _uiShared, "PeopleNotesOpenNotesPopupOnUserAddition");
        PeopleNotesOptionBlock.DrawAutoPopulateNotesUsingPlayerNamesOption(_configService, _uiShared, "PeopleNotesAutoPopulateNotesUsingPlayerNames");
    }

    private void DrawGeneralUiDisplaySettings()
    {
        DrawSettingsPageHeader("Appearance", "Configure interface behavior, user list presentation, and profile display options.");

        DrawSettingsSectionHeader("Basic Interface");
        AppearanceOptionBlock.DrawShowSpheneIconOption(_configService, "AppearanceShowSpheneIcon");
        AppearanceOptionBlock.DrawLockSpheneIconPositionOption(_configService, "AppearanceLockSpheneIconPosition");
        AppearanceOptionBlock.DrawEnableGameRightClickMenusOption(_configService, "AppearanceEnableGameRightClickMenus");

        // ShrinkU integration settings moved to Overview page alongside version info

        DrawSettingsSectionHeader("Server Info Bar");
        var enableDtrEntry = AppearanceOptionBlock.DrawShowStatusInServerInfoBarOption(_configService, "AppearanceShowStatusInServerInfoBar");
        using (ImRaii.Disabled(!enableDtrEntry))
        {
            using var indent = ImRaii.PushIndent();
            AppearanceOptionBlock.DrawShowUidInTooltipOption(_configService, "AppearanceShowUidInTooltip");
            AppearanceOptionBlock.DrawPreferNotesInTooltipOption(_configService, "AppearancePreferNotesInTooltip");
            AppearanceOptionBlock.DrawUseStatusColorsOption(_configService, "AppearanceUseStatusColors");
        }

        DrawSettingsSectionHeader("User List Options");
        AppearanceOptionBlock.DrawSetVisiblePairsAsFocusTargetsOption(_configService, "AppearanceSetVisiblePairsAsFocusTargets");
        
        ImGuiHelpers.ScaledDummy(10);

        AppearanceOptionBlock.DrawShowCharacterNameInsteadOfNotesOption(_configService, _uiShared, Mediator, "AppearanceShowCharacterNameInsteadOfNotes");
        AppearanceOptionBlock.DrawShowVisibleUsersSeparatelyOption(_configService, _uiShared, Mediator, "AppearanceShowVisibleUsersSeparately");
        AppearanceOptionBlock.DrawShowVisibleSyncshellUsersOnlyInSyncshellsOption(_configService, _uiShared, Mediator, "AppearanceShowVisibleSyncshellUsersOnlyInSyncshells");
        AppearanceOptionBlock.DrawShowOfflineUsersSeparatelyOption(_configService, _uiShared, Mediator, "AppearanceShowOfflineUsersSeparately");
        AppearanceOptionBlock.DrawAlsoShowOfflineSyncshellUsersSeparatelyOption(_configService, _uiShared, Mediator, "AppearanceAlsoShowOfflineSyncshellUsersSeparately");
        ImGuiHelpers.ScaledDummy(10);
        AppearanceOptionBlock.DrawGroupUpAllSyncshellsInOneFolderOption(_configService, _uiShared, Mediator, "AppearanceGroupUpAllSyncshellsInOneFolder");
        

        DrawSettingsSectionHeader("Profile Settings");
        var showProfiles = AppearanceOptionBlock.DrawShowSpheneProfilesOnHoverOption(_configService, _uiShared, Mediator, "AppearanceShowSpheneProfilesOnHover");
        ImGui.Indent();
        if (!showProfiles) ImGui.BeginDisabled();
        AppearanceOptionBlock.DrawPopoutProfilesOnTheRightOption(_configService, _uiShared, Mediator, "AppearancePopoutProfilesOnTheRight");
        AppearanceOptionBlock.DrawHoverDelayOption(_configService, _uiShared, "AppearanceHoverDelay");
        if (!showProfiles) ImGui.EndDisabled();
        ImGui.Unindent();
        AppearanceOptionBlock.DrawShowProfilesMarkedAsNsfwOption(_configService, _uiShared, Mediator, "AppearanceShowProfilesMarkedAsNsfw");
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

        DrawSettingsSectionHeader("Notification Channels");
        NotificationsOptionBlock.DrawInfoNotificationDisplayOption(_configService, _uiShared, "NotificationsInfoNotificationDisplay");
        NotificationsOptionBlock.DrawWarningNotificationDisplayOption(_configService, _uiShared, "NotificationsWarningNotificationDisplay");
        NotificationsOptionBlock.DrawErrorNotificationDisplayOption(_configService, _uiShared, "NotificationsErrorNotificationDisplay");
        NotificationsOptionBlock.DrawDisableOptionalPluginWarningsOption(_configService, _uiShared, "NotificationsDisableOptionalPluginWarnings");
        NotificationsOptionBlock.DrawEnableOnlineNotificationsOption(_configService, "NotificationsEnableOnlineNotifications");
        NotificationsOptionBlock.DrawOnlyForIndividualPairsOption(_configService, "NotificationsOnlyForIndividualPairs");
        NotificationsOptionBlock.DrawOnlyForNamedPairsOption(_configService, "NotificationsOnlyForNamedPairs");
        NotificationsOptionBlock.DrawShowTestBuildUpdatesOption(_configService, _uiShared, "NotificationsShowTestBuildUpdates");

        DrawSettingsSectionHeader("Area-bound Syncshell Notifications");
        if (NotificationsOptionBlock.DrawEnableAreaBoundSyncshellNotificationsOption(_configService, _uiShared, "NotificationsEnableAreaBoundSyncshellNotifications"))
        {
            ImGui.Indent();
            NotificationsOptionBlock.DrawAreaBoundNotificationDisplayOption(_configService, _uiShared, "NotificationsAreaBoundNotificationDisplay");
            ImGui.Unindent();
        }
        NotificationsOptionBlock.DrawShowAreaBoundSyncshellWelcomeMessagesOption(_configService, _uiShared, "NotificationsShowAreaBoundSyncshellWelcomeMessages");
        NotificationsOptionBlock.DrawAutomaticallyShowAreaBoundSyncshellConsentOption(_configService, _uiShared, "NotificationsAutomaticallyShowAreaBoundSyncshellConsent");
    }

    private void DrawAcknowledgmentSettings()
    {
        DrawSettingsPageHeader("Acknowledgment", "Tune acknowledgment notifications, reliability, and batching behavior.");
        DrawSettingsSectionHeader("Popup Settings");

        var showPopups = AcknowledgmentOptionBlock.DrawShowAcknowledgmentNotificationsOption(_configService, _uiShared, "AcknowledgmentShowAcknowledgmentNotifications");
        AcknowledgmentOptionBlock.DrawShowWaitingForAcknowledgmentPopupsOption(_configService, _uiShared, "AcknowledgmentShowWaitingForAcknowledgmentPopups");

        if (showPopups)
        {
            ImGui.Indent();
            AcknowledgmentOptionBlock.DrawAcknowledgmentNotificationLocationOption(_configService, _uiShared, "AcknowledgmentNotificationLocation");
            ImGui.Unindent();
        }

        DrawSettingsSectionHeader("Performance Settings");
        AcknowledgmentOptionBlock.DrawEnableBatchingOption(_configService, _uiShared, "AcknowledgmentEnableBatching");
        AcknowledgmentOptionBlock.DrawEnableAutoRetryOption(_configService, _uiShared, "AcknowledgmentEnableAutoRetry");

        DrawSettingsSectionHeader("Timeout & Reliability");
        AcknowledgmentOptionBlock.DrawAcknowledgmentTimeoutOption(_configService, _uiShared, "AcknowledgmentTimeout");

        DrawSettingsSectionHeader("Information");
        UiSharedService.TextWrapped("The acknowledgment system helps maintain synchronization between connected users. " +
                          "When disabled, popup notifications will not appear, but the system will continue to function in the background. " +
                          "This is useful when you have many active connections to prevent notification spam.");

        ImGui.Spacing();
        AcknowledgmentOptionBlock.DrawResetAcknowledgmentSettingsToDefaultsOption(_configService, _uiShared, "AcknowledgmentResetAcknowledgmentSettingsToDefaults");
    }

    private void DrawSyncBehaviorSettings()
    {
        DrawSettingsPageHeader("Sync Behavior", "Control how incoming and outgoing synchronization data is handled.");
        DrawSettingsSectionHeader("Incoming Sync");
        SyncBehaviorOptionBlock.DrawIncomingSyncWithoutRedraw(_configService, _uiShared, "SettingsIncomingSyncWithoutRedraw");

        DrawSettingsSectionHeader("Outgoing Sync Batching");
        SyncBehaviorOptionBlock.DrawOutgoingSyncBatching(_configService, _uiShared, ClampSettingsItemWidth(240f, 160f), "SettingsOutgoingSyncBatching");

        DrawSettingsSectionHeader("Filter");
        SyncBehaviorOptionBlock.DrawFilterCharacterLegacyShpkInOutgoingCharacterData(_configService, _uiShared, Mediator, "SettingsFilterCharacterLegacyShpk");
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
        
        // Theme Selector
        DrawThemeSelector();
        
        DrawSettingsSectionHeader("Theme Editor");
        
        using (var themeTabBar = ImRaii.TabBar("ThemeTabBar"))
        {
            if (themeTabBar)
            {
                var availableRegion = ImGui.GetContentRegionAvail();
                var tabContentHeight = availableRegion.Y;

                if (ApiController.IsAdmin)
                {
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
                }

                #pragma warning disable S1066
                var iconTab = ImRaii.TabItem("Icon Theme", _preferIconThemeTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
                if (iconTab)
                {
                    if (ImGui.BeginChild("IconThemeChild", new Vector2(0, tabContentHeight), true))
                    {
                        DrawIconThemeSettings();
                        ImGui.EndChild();
                    }
                }
                #pragma warning restore S1066
                iconTab.Dispose();
                _preferIconThemeTab = false;

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
    
    

    private void DrawIconThemeSettings()
    {
        var cfg = _configService.Current;
        bool configChanged = false;

        UiSharedService.ColorTextWrapped("Customize the Sphene floating icon pulse colors, ring sizes, and which notifications appear as badges.", ImGuiColors.DalamudGrey);
        UiSharedService.ColorTextWrapped("Changes apply immediately to the icon. Use the test buttons below to preview each effect.", ImGuiColors.DalamudYellow);
        ImGui.Spacing();

        // Helper to edit packed uint color as Vector4
        (bool Changed, uint NewValue) EditPackedColor(string label, uint packedColor)
        {
            var vec = ImGui.ColorConvertU32ToFloat4(packedColor);
            if (ImGui.ColorEdit4(label, ref vec))
            {
                return (true, ImGui.ColorConvertFloat4ToU32(vec));
            }
            return (false, packedColor);
        }

        ImGui.Text("Global Visual Settings");
        ImGui.Separator();

        var iconGlobalAlpha = cfg.IconGlobalAlpha;
        if (ImGui.SliderFloat("Icon Global Opacity", ref iconGlobalAlpha, 0.1f, 1.0f, "%.2f"))
        {
            cfg.IconGlobalAlpha = iconGlobalAlpha;
            configChanged = true;
        }

        var rainbowSpeed = cfg.IconRainbowSpeed;
        if (ImGui.SliderFloat("Rainbow Cycle Speed", ref rainbowSpeed, 0.1f, 5.0f, "%.2f"))
        {
            cfg.IconRainbowSpeed = rainbowSpeed;
            configChanged = true;
        }

        ImGui.Spacing();
        ImGui.Text("Badge Visibility");
        ImGui.Separator();

        var showMod = cfg.IconShowModTransferBadge;
        if (ImGui.Checkbox("Show Mod Transfer Badges", ref showMod))
        {
            cfg.IconShowModTransferBadge = showMod;
            configChanged = true;
        }

        var showPair = cfg.IconShowPairRequestBadge;
        if (ImGui.Checkbox("Show Pair Request Badges", ref showPair))
        {
            cfg.IconShowPairRequestBadge = showPair;
            configChanged = true;
        }

        var showNotif = cfg.IconShowNotificationBadge;
        if (ImGui.Checkbox("Show Notification Badges", ref showNotif))
        {
            cfg.IconShowNotificationBadge = showNotif;
            configChanged = true;
        }

        ImGui.Spacing();
        ImGui.Text("Per-Event Configuration");
        ImGui.Separator();
        ImGui.TextWrapped("Each event type can have its own color, alpha, and effects. Multiple effects can be combined.");

        // Permanent (Background)
        if (ImGui.CollapsingHeader("Permanent (Background)"))
        {
            var (permColorChanged, permColorNew) = EditPackedColor("Color##perm", cfg.IconPermColor);
            if (permColorChanged) { cfg.IconPermColor = permColorNew; configChanged = true; }
            var permAlpha = cfg.IconPermAlpha;
            if (ImGui.SliderFloat("Alpha##perm", ref permAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconPermAlpha = permAlpha; configChanged = true; }
            var permPulse = cfg.IconPermEffectPulse;
            if (ImGui.Checkbox("Pulse##perm", ref permPulse)) { cfg.IconPermEffectPulse = permPulse; configChanged = true; }
            ImGui.SameLine();
            var permGlow = cfg.IconPermEffectGlow;
            if (ImGui.Checkbox("Glow##perm", ref permGlow)) { cfg.IconPermEffectGlow = permGlow; configChanged = true; }
            ImGui.SameLine();
            var permBounce = cfg.IconPermEffectBounce;
            if (ImGui.Checkbox("Bounce##perm", ref permBounce)) { cfg.IconPermEffectBounce = permBounce; configChanged = true; }
            ImGui.SameLine();
            var permRainbow = cfg.IconPermEffectRainbow;
            if (ImGui.Checkbox("Rainbow##perm", ref permRainbow)) { cfg.IconPermEffectRainbow = permRainbow; configChanged = true; }
            var permPulseMin = cfg.IconPermPulseMinRadius;
            if (ImGui.SliderFloat("Pulse Min Radius##perm", ref permPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconPermPulseMinRadius = permPulseMin; configChanged = true; }
            var permPulseMax = cfg.IconPermPulseMaxRadius;
            if (ImGui.SliderFloat("Pulse Max Radius##perm", ref permPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconPermPulseMaxRadius = permPulseMax; configChanged = true; }
            var permGlowInt = cfg.IconPermGlowIntensity;
            if (ImGui.SliderFloat("Glow Intensity##perm", ref permGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconPermGlowIntensity = permGlowInt; configChanged = true; }
            var permGlowRad = cfg.IconPermGlowRadius;
            if (ImGui.SliderFloat("Glow Radius##perm", ref permGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconPermGlowRadius = permGlowRad; configChanged = true; }
            var permBounceInt = cfg.IconPermBounceIntensity;
            if (ImGui.SliderFloat("Bounce Scale##perm", ref permBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconPermBounceIntensity = permBounceInt; configChanged = true; }
            var permBounceSpd = cfg.IconPermBounceSpeed;
            if (ImGui.SliderFloat("Bounce Speed##perm", ref permBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconPermBounceSpeed = permBounceSpd; configChanged = true; }
        }

        // Mod Transfer
        if (ImGui.CollapsingHeader("Mod Transfer"))
        {
            var (modColorChanged, modColorNew) = EditPackedColor("Color##mod", cfg.IconModTransferColor);
            if (modColorChanged) { cfg.IconModTransferColor = modColorNew; configChanged = true; }
            var modAlpha = cfg.IconModTransferAlpha;
            if (ImGui.SliderFloat("Alpha##mod", ref modAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconModTransferAlpha = modAlpha; configChanged = true; }
            var modPulse = cfg.IconModTransferEffectPulse;
            if (ImGui.Checkbox("Pulse##mod", ref modPulse)) { cfg.IconModTransferEffectPulse = modPulse; configChanged = true; }
            ImGui.SameLine();
            var modGlow = cfg.IconModTransferEffectGlow;
            if (ImGui.Checkbox("Glow##mod", ref modGlow)) { cfg.IconModTransferEffectGlow = modGlow; configChanged = true; }
            ImGui.SameLine();
            var modBounce = cfg.IconModTransferEffectBounce;
            if (ImGui.Checkbox("Bounce##mod", ref modBounce)) { cfg.IconModTransferEffectBounce = modBounce; configChanged = true; }
            ImGui.SameLine();
            var modRainbow = cfg.IconModTransferEffectRainbow;
            if (ImGui.Checkbox("Rainbow##mod", ref modRainbow)) { cfg.IconModTransferEffectRainbow = modRainbow; configChanged = true; }
            var modPulseMin = cfg.IconModTransferPulseMinRadius;
            if (ImGui.SliderFloat("Pulse Min Radius##mod", ref modPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconModTransferPulseMinRadius = modPulseMin; configChanged = true; }
            var modPulseMax = cfg.IconModTransferPulseMaxRadius;
            if (ImGui.SliderFloat("Pulse Max Radius##mod", ref modPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconModTransferPulseMaxRadius = modPulseMax; configChanged = true; }
            var modGlowInt = cfg.IconModTransferGlowIntensity;
            if (ImGui.SliderFloat("Glow Intensity##mod", ref modGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconModTransferGlowIntensity = modGlowInt; configChanged = true; }
            var modGlowRad = cfg.IconModTransferGlowRadius;
            if (ImGui.SliderFloat("Glow Radius##mod", ref modGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconModTransferGlowRadius = modGlowRad; configChanged = true; }
            var modBounceInt = cfg.IconModTransferBounceIntensity;
            if (ImGui.SliderFloat("Bounce Scale##mod", ref modBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconModTransferBounceIntensity = modBounceInt; configChanged = true; }
            var modBounceSpd = cfg.IconModTransferBounceSpeed;
            if (ImGui.SliderFloat("Bounce Speed##mod", ref modBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconModTransferBounceSpeed = modBounceSpd; configChanged = true; }
            ImGui.Spacing();
            var modEffectDuration = cfg.IconModTransferEffectDurationSeconds;
            if (ImGui.SliderInt("Effect Duration##mod", ref modEffectDuration, 0, 300, modEffectDuration == 0 ? "Never" : "%ds")) { cfg.IconModTransferEffectDurationSeconds = modEffectDuration; configChanged = true; }
            _uiShared.DrawHelpText("How long the pulse/glow/bounce effects last. 0 = never expires until icon is clicked.");
            var modBadgeDuration = cfg.IconModTransferBadgeDurationSeconds;
            if (ImGui.SliderInt("Badge Duration##mod", ref modBadgeDuration, 0, 300, modBadgeDuration == 0 ? "Never" : "%ds")) { cfg.IconModTransferBadgeDurationSeconds = modBadgeDuration; configChanged = true; }
            _uiShared.DrawHelpText("How long the badge dot remains visible. 0 = never expires until icon is clicked. Separate from effect duration.");
        }

        // Pair Request
        if (ImGui.CollapsingHeader("Pair Request"))
        {
            var (pairColorChanged, pairColorNew) = EditPackedColor("Color##pair", cfg.IconPairRequestColor);
            if (pairColorChanged) { cfg.IconPairRequestColor = pairColorNew; configChanged = true; }
            var pairAlpha = cfg.IconPairRequestAlpha;
            if (ImGui.SliderFloat("Alpha##pair", ref pairAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconPairRequestAlpha = pairAlpha; configChanged = true; }
            var pairPulse = cfg.IconPairRequestEffectPulse;
            if (ImGui.Checkbox("Pulse##pair", ref pairPulse)) { cfg.IconPairRequestEffectPulse = pairPulse; configChanged = true; }
            ImGui.SameLine();
            var pairGlow = cfg.IconPairRequestEffectGlow;
            if (ImGui.Checkbox("Glow##pair", ref pairGlow)) { cfg.IconPairRequestEffectGlow = pairGlow; configChanged = true; }
            ImGui.SameLine();
            var pairBounce = cfg.IconPairRequestEffectBounce;
            if (ImGui.Checkbox("Bounce##pair", ref pairBounce)) { cfg.IconPairRequestEffectBounce = pairBounce; configChanged = true; }
            ImGui.SameLine();
            var pairRainbow = cfg.IconPairRequestEffectRainbow;
            if (ImGui.Checkbox("Rainbow##pair", ref pairRainbow)) { cfg.IconPairRequestEffectRainbow = pairRainbow; configChanged = true; }
            var pairPulseMin = cfg.IconPairRequestPulseMinRadius;
            if (ImGui.SliderFloat("Pulse Min Radius##pair", ref pairPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconPairRequestPulseMinRadius = pairPulseMin; configChanged = true; }
            var pairPulseMax = cfg.IconPairRequestPulseMaxRadius;
            if (ImGui.SliderFloat("Pulse Max Radius##pair", ref pairPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconPairRequestPulseMaxRadius = pairPulseMax; configChanged = true; }
            var pairGlowInt = cfg.IconPairRequestGlowIntensity;
            if (ImGui.SliderFloat("Glow Intensity##pair", ref pairGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconPairRequestGlowIntensity = pairGlowInt; configChanged = true; }
            var pairGlowRad = cfg.IconPairRequestGlowRadius;
            if (ImGui.SliderFloat("Glow Radius##pair", ref pairGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconPairRequestGlowRadius = pairGlowRad; configChanged = true; }
            var pairBounceInt = cfg.IconPairRequestBounceIntensity;
            if (ImGui.SliderFloat("Bounce Scale##pair", ref pairBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconPairRequestBounceIntensity = pairBounceInt; configChanged = true; }
            var pairBounceSpd = cfg.IconPairRequestBounceSpeed;
            if (ImGui.SliderFloat("Bounce Speed##pair", ref pairBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconPairRequestBounceSpeed = pairBounceSpd; configChanged = true; }
            ImGui.Spacing();
            var pairEffectDuration = cfg.IconPairRequestEffectDurationSeconds;
            if (ImGui.SliderInt("Effect Duration##pair", ref pairEffectDuration, 0, 300, pairEffectDuration == 0 ? "Never" : "%ds")) { cfg.IconPairRequestEffectDurationSeconds = pairEffectDuration; configChanged = true; }
            _uiShared.DrawHelpText("How long the pulse/glow/bounce effects last. 0 = never expires until icon is clicked.");
            var pairBadgeDuration = cfg.IconPairRequestBadgeDurationSeconds;
            if (ImGui.SliderInt("Badge Duration##pair", ref pairBadgeDuration, 0, 300, pairBadgeDuration == 0 ? "Never" : "%ds")) { cfg.IconPairRequestBadgeDurationSeconds = pairBadgeDuration; configChanged = true; }
            _uiShared.DrawHelpText("How long the badge dot remains visible. 0 = never expires until icon is clicked. Separate from effect duration.");
        }

        // Notification
        if (ImGui.CollapsingHeader("Notification"))
        {
            var (notifColorChanged, notifColorNew) = EditPackedColor("Color##notif", cfg.IconNotificationColor);
            if (notifColorChanged) { cfg.IconNotificationColor = notifColorNew; configChanged = true; }
            var notifAlpha = cfg.IconNotificationAlpha;
            if (ImGui.SliderFloat("Alpha##notif", ref notifAlpha, 0.01f, 1.0f, "%.2f")) { cfg.IconNotificationAlpha = notifAlpha; configChanged = true; }
            var notifPulse = cfg.IconNotificationEffectPulse;
            if (ImGui.Checkbox("Pulse##notif", ref notifPulse)) { cfg.IconNotificationEffectPulse = notifPulse; configChanged = true; }
            ImGui.SameLine();
            var notifGlow = cfg.IconNotificationEffectGlow;
            if (ImGui.Checkbox("Glow##notif", ref notifGlow)) { cfg.IconNotificationEffectGlow = notifGlow; configChanged = true; }
            ImGui.SameLine();
            var notifBounce = cfg.IconNotificationEffectBounce;
            if (ImGui.Checkbox("Bounce##notif", ref notifBounce)) { cfg.IconNotificationEffectBounce = notifBounce; configChanged = true; }
            ImGui.SameLine();
            var notifRainbow = cfg.IconNotificationEffectRainbow;
            if (ImGui.Checkbox("Rainbow##notif", ref notifRainbow)) { cfg.IconNotificationEffectRainbow = notifRainbow; configChanged = true; }
            var notifPulseMin = cfg.IconNotificationPulseMinRadius;
            if (ImGui.SliderFloat("Pulse Min Radius##notif", ref notifPulseMin, 0.0f, 1.5f, "%.2f")) { cfg.IconNotificationPulseMinRadius = notifPulseMin; configChanged = true; }
            var notifPulseMax = cfg.IconNotificationPulseMaxRadius;
            if (ImGui.SliderFloat("Pulse Max Radius##notif", ref notifPulseMax, 0.0f, 1.5f, "%.2f")) { cfg.IconNotificationPulseMaxRadius = notifPulseMax; configChanged = true; }
            var notifGlowInt = cfg.IconNotificationGlowIntensity;
            if (ImGui.SliderFloat("Glow Intensity##notif", ref notifGlowInt, 0.1f, 1.0f, "%.2f")) { cfg.IconNotificationGlowIntensity = notifGlowInt; configChanged = true; }
            var notifGlowRad = cfg.IconNotificationGlowRadius;
            if (ImGui.SliderFloat("Glow Radius##notif", ref notifGlowRad, 0.5f, 2.0f, "%.2f")) { cfg.IconNotificationGlowRadius = notifGlowRad; configChanged = true; }
            var notifBounceInt = cfg.IconNotificationBounceIntensity;
            if (ImGui.SliderFloat("Bounce Scale##notif", ref notifBounceInt, 0.0f, 0.3f, "%.2f")) { cfg.IconNotificationBounceIntensity = notifBounceInt; configChanged = true; }
            var notifBounceSpd = cfg.IconNotificationBounceSpeed;
            if (ImGui.SliderFloat("Bounce Speed##notif", ref notifBounceSpd, 0.1f, 3.0f, "%.2f")) { cfg.IconNotificationBounceSpeed = notifBounceSpd; configChanged = true; }
            ImGui.Spacing();
            var notifEffectDuration = cfg.IconNotificationEffectDurationSeconds;
            if (ImGui.SliderInt("Effect Duration##notif", ref notifEffectDuration, 0, 300, notifEffectDuration == 0 ? "Never" : "%ds")) { cfg.IconNotificationEffectDurationSeconds = notifEffectDuration; configChanged = true; }
            _uiShared.DrawHelpText("How long the pulse/glow/bounce effects last. 0 = never expires until icon is clicked.");
            var notifBadgeDuration = cfg.IconNotificationBadgeDurationSeconds;
            if (ImGui.SliderInt("Badge Duration##notif", ref notifBadgeDuration, 0, 300, notifBadgeDuration == 0 ? "Never" : "%ds")) { cfg.IconNotificationBadgeDurationSeconds = notifBadgeDuration; configChanged = true; }
            _uiShared.DrawHelpText("How long the badge dot remains visible. 0 = never expires until icon is clicked. Separate from effect duration.");
        }

        ImGui.Spacing();
        ImGui.Text("Live Preview (Test Buttons)");
        ImGui.Separator();

        if (ImGui.Button("Test Mod Transfer"))
        {
            Mediator.Publish(new TestIconEventMessage(TestIconEventType.ModTransferAvailable, "Test Mod Transfer"));
        }
        ImGui.SameLine();
        if (ImGui.Button("Test Pair Request"))
        {
            Mediator.Publish(new TestIconEventMessage(TestIconEventType.PairRequest, "Test Pair Request"));
        }
        ImGui.SameLine();
        if (ImGui.Button("Test Notification"))
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
        var compactControlPanelBg = theme.CompactControlPanelBg;
        if (ImGui.ColorEdit4("Control Panel UID Section", ref compactControlPanelBg))
        {
            theme.CompactControlPanelBg = compactControlPanelBg;
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
        
        ImGui.SetNextItemWidth(ClampSettingsItemWidth(320f, 180f));
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
