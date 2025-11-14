using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Sphene.API.Data;
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
        Mediator.Subscribe<CompactUiChange>(this, (msg) => _currentCompactUiSize = msg.Size);
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
        _speedTestCts = null;

        // Automatically close progress bar preview when settings are closed
        if (SpheneCustomTheme.CurrentTheme.ShowProgressBarPreview)
        {
            SpheneCustomTheme.CurrentTheme.ShowProgressBarPreview = false;
            _configService.Save();
        }

        base.OnClose();
    }

    // Mark unsaved changes whenever the theme notifies a change
    private void OnThemeChanged()
    {
        _hasUnsavedThemeChanges = true;
    }

    protected override void DrawInternal()
    {
        _ = _uiShared.DrawOtherPluginState();

        // Right-aligned prominent Discord button near plugin status
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var rightButtonWidth = _uiShared.GetIconTextButtonSize(FontAwesomeIcon.Users, "Join Discord Community");
        ImGui.SameLine(availableWidth - rightButtonWidth);
        if (_uiShared.IconTextActionButton(FontAwesomeIcon.Users, "Join Discord Community"))
        {
            Util.OpenLink("https://discord.gg/GbnwsP2XsF");
        }
        UiSharedService.AttachToolTip("Get support, updates, and connect with other users");

        DrawSettingsContent();
        
        // Draw theme save prompt if needed
        DrawThemeSavePrompt();
    }
    private static bool InputDtrColors(string label, ref DtrEntry.Colors colors)
    {
        using var id = ImRaii.PushId(label);
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var foregroundColor = ConvertColor(colors.Foreground);
        var glowColor = ConvertColor(colors.Glow);

        var ret = ImGui.ColorEdit3("###foreground", ref foregroundColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Foreground Color - Set to pure black (#000000) to use the default color");

        ImGui.SameLine(0.0f, innerSpacing);
        ret |= ImGui.ColorEdit3("###glow", ref glowColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Glow Color - Set to pure black (#000000) to use the default color");

        ImGui.SameLine(0.0f, innerSpacing);
        ImGui.TextUnformatted(label);

        if (ret)
            colors = new(ConvertBackColor(foregroundColor), ConvertBackColor(glowColor));

        return ret;

        static Vector3 ConvertColor(uint color)
            => unchecked(new((byte)color / 255.0f, (byte)(color >> 8) / 255.0f, (byte)(color >> 16) / 255.0f));

        static uint ConvertBackColor(Vector3 color)
            => byte.CreateSaturating(color.X * 255.0f) | ((uint)byte.CreateSaturating(color.Y * 255.0f) << 8) | ((uint)byte.CreateSaturating(color.Z * 255.0f) << 16);
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
                if (_downloadServersTask == null || ((_downloadServersTask?.IsCompleted ?? false) && (!_downloadServersTask?.IsCompletedSuccessfully ?? false)))
                {
                    if (_uiShared.IconTextButton(FontAwesomeIcon.GroupArrowsRotate, "Update Download Server List"))
                    {
                        _downloadServersTask = GetDownloadServerList();
                    }
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
            throw;
        }
    }

    private void DrawDebug()
    {
        _lastTab = "Debug";

        _uiShared.BigText("Debug & Diagnostics");
#if DEBUG
        if (LastCreatedCharacterData != null && ImGui.TreeNode("Last created character data"))
        {
            foreach (var l in JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }).Split('\n'))
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
        
        _uiShared.BigText("Debug Windows");
        UiSharedService.TextWrapped("Open debug windows for monitoring and troubleshooting.");
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

        _uiShared.BigText("Export MCDF");

        ImGuiHelpers.ScaledDummy(10);

        UiSharedService.ColorTextWrapped("Exporting MCDF has moved.", ImGuiColors.DalamudYellow);
        ImGuiHelpers.ScaledDummy(5);
        UiSharedService.TextWrapped("It is now found in the Main UI under \"Your User Menu\" (");
        ImGui.SameLine();
        _uiShared.IconText(FontAwesomeIcon.UserCog);
        ImGui.SameLine();
        UiSharedService.TextWrapped(") -> \"Character Data Hub\".");
        if (_uiShared.IconTextButton(FontAwesomeIcon.Running, "Open Sphene Character Data Hub"))
        {
            Mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
        }
        UiSharedService.TextWrapped("Note: this entry will be removed in the near future. Please use the Main UI to open the Character Data Hub.");
        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();

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
                    if (ImGui.Button("Disconnect from Service"))
                    {
                        if (_serverConfigurationManager.CurrentServer != null)
                        {
                            _serverConfigurationManager.CurrentServer.FullPause = true;
                            _serverConfigurationManager.Save();
                            _ = _uiShared.ApiController.CreateConnectionsAsync();
                        }
                    }
                    _uiShared.DrawHelpText("Disconnect the current session from the selected service.");
                }
                else
                {
                    if (ImGui.Button("Connect to Service"))
                    {
                        if (_serverConfigurationManager.CurrentServer != null)
                        {
                            _serverConfigurationManager.CurrentServer.FullPause = false;
                            _serverConfigurationManager.Save();
                            _ = _uiShared.ApiController.CreateConnectionsAsync();
                        }
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
                                    _secretKeysConversionTask = ConvertSecretKeysToUIDs(selectedServer, _secretKeysConversionCts.Token);
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
    private CancellationTokenSource _secretKeysConversionCts = new CancellationTokenSource();

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
        
        // Calculate optimal sidebar width based on longest button text + reduced padding
        var buttonLabels = new[] { "Home", "Connectivity", "People & Notes", "Appearance", "Theme", "Notifications", "Performance", "Transfers", "Storage", "Acknowledgment", "Debug" };
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
        SidebarButton("Debug", SettingsPage.Debug);

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

    private void DrawOverview()
    {
        _lastTab = "Overview";
        
        // Plugin Information Section (moved to top)
        _uiShared.BigText("Sphene Plugin");
        
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        var versionString = version != null
            ? (version.Revision > 0
                ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
                : $"{version.Major}.{version.Minor}.{version.Build}")
            : "Unknown";
        
        ImGui.TextUnformatted("Version:");
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.ParsedGreen, versionString);
        
#if DEBUG
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "(Debug Build)");
#endif

        ImGui.TextUnformatted("Author:");
        ImGui.SameLine();
        ImGui.TextUnformatted("Sphene Development Team");

        ImGui.TextUnformatted("Description:");
        ImGui.SameLine();
        UiSharedService.TextWrapped("Advanced character synchronization and networking plugin for Final Fantasy XIV");

        ImGui.Spacing();
        if (_uiShared.IconTextButton(FontAwesomeIcon.InfoCircle, "Open Release Notes"))
        {
            Task.Run(async () =>
            {
                string? text = null;
                try
                {
                    var baseVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : versionString;
                    text = await _changelogService.GetChangelogTextForVersionAsync(baseVersion).ConfigureAwait(false);
                }
                catch { }
                Mediator.Publish(new ShowReleaseChangelogMessage(versionString, text));
            });
        }
        UiSharedService.AttachToolTip("Open recent changes and highlights for this Sphene version.");

        // Custom Changelog Source
        ImGui.Spacing();
        var url = _configService.Current.ReleaseChangelogUrl ?? string.Empty;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configService.Current.ReleaseChangelogUrl = url?.Trim() ?? string.Empty;
            _configService.Save();
        }

        ImGui.Separator();
        _uiShared.BigText("ShrinkU");
        ImGui.TextUnformatted("ShrinkU Version:");
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.ParsedGreen, _shrinkUVersion);
        UiSharedService.AttachToolTip("Version of bundled ShrinkU assembly.");

        ImGui.TextUnformatted("Description:");
        ImGui.SameLine();
        UiSharedService.TextWrapped("Convert Penumbra textures to BC7 with backups. Standalone texture conversion plugin integrated with Sphene.");

        var enableShrinkUOverview = _configService.Current.EnableShrinkUIntegration;
        if (ImGui.Checkbox("Enable ShrinkU UI", ref enableShrinkUOverview))
        {
            _configService.Current.EnableShrinkUIntegration = enableShrinkUOverview;
            _configService.Save();
            try { _shrinkUHostService.ApplyIntegrationEnabled(enableShrinkUOverview); } catch { }
        }
        UiSharedService.AttachToolTip("Toggle ShrinkU UI integration inside Sphene.");

        // Open ShrinkU Release Notes button (with info icon, matching Sphene style)
        if (_configService.Current.EnableShrinkUIntegration)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.InfoCircle, "Open ShrinkU Release Notes"))
            {
                try { _shrinkUHostService.OpenReleaseNotes(); } catch { }
            }
            UiSharedService.AttachToolTip("Open recent changes and highlights for ShrinkU.");
        }
        else
        {
            ImGui.BeginDisabled();
            _uiShared.IconTextButton(FontAwesomeIcon.InfoCircle, "Open ShrinkU Release Notes");
            UiSharedService.AttachToolTip("Enable ShrinkU UI to open release notes.");
            ImGui.EndDisabled();
        }

        // Discord button moved to Settings header for better visibility

        // Server Connection Section
        ImGui.Separator();
        _uiShared.BigText("Server Connection");
        
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
            ImGui.TextColored(ImGuiColors.ParsedGreen, currentServer.ServerName);

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

            var isTestBuild = (Assembly.GetExecutingAssembly().GetName().Version?.Revision ?? 0) != 0;
            if (isTestBuild)
            {
                var useOverride = _configService.Current.UseTestServerOverride;
                if (ImGui.Checkbox("Use test server override", ref useOverride))
                {
                    _configService.Current.UseTestServerOverride = useOverride;
                    _configService.Save();
                    _uiShared.ApiController.CreateConnectionsAsync();
                }
                if (string.IsNullOrWhiteSpace(_configService.Current.TestServerApiUrl))
                {
                    _configService.Current.TestServerApiUrl = "ws://test.sphene.online:6000";
                    _configService.Save();
                }
                var overrideUrl = _configService.Current.TestServerApiUrl ?? string.Empty;
                if (ImGui.InputText("Test server API URL", ref overrideUrl, 512))
                {
                    _configService.Current.TestServerApiUrl = overrideUrl;
                    _configService.Save();
                }
                UiSharedService.AttachToolTip("Example: ws://1.1.1.2:6000 or wss://test.example.com:6000");
            }
            else if (_configService.Current.UseTestServerOverride)
            {
                _configService.Current.UseTestServerOverride = false;
                _configService.Save();
            }
        }

        // Statistics Section
        ImGui.Separator();
        _uiShared.BigText("Statistics");
        
        var directPairs = _pairManager.DirectPairs.Count();
        var groupPairs = _pairManager.GroupPairs.SelectMany(g => g.Value).Count();
        var totalPairs = directPairs + groupPairs;
        
        ImGui.TextUnformatted($"Direct Pairs: {directPairs}");
        ImGui.TextUnformatted($"Group Pairs: {groupPairs}");
        ImGui.TextUnformatted($"Total Connections: {totalPairs}");
        
        var onlinePairs = _pairManager.DirectPairs.Count(p => p.IsOnline) + 
                         _pairManager.GroupPairs.SelectMany(g => g.Value).Count(p => p.IsOnline);
        ImGui.TextUnformatted($"Currently Online: {onlinePairs}");

        // Cache Information
        var cacheSize = _cacheMonitor.FileCacheSize;
        var cacheSizeFormatted = cacheSize > 0 ? UiSharedService.ByteToString(cacheSize) : "Unknown";
        ImGui.TextUnformatted($"Cache Size: {cacheSizeFormatted}");

        if (currentServer != null)
        {
            ImGui.Spacing();
            _uiShared.BigText("Quick Actions");
            var isPaused = currentServer.FullPause;
            if (!isPaused)
            {
                if (ImGui.Button("Disconnect from Service"))
                {
                    currentServer.FullPause = true;
                    _serverConfigurationManager.Save();
                    _uiShared.ApiController.CreateConnectionsAsync();
                }
                ImGui.TextUnformatted("Temporarily disconnects from the service. Toggle back to reconnect.");
            }
            else
            {
                if (ImGui.Button("Connect to Service"))
                {
                    currentServer.FullPause = false;
                    _serverConfigurationManager.Save();
                    _uiShared.ApiController.CreateConnectionsAsync();
                }
                ImGui.TextUnformatted("Reconnects to the configured service.");
            }
        }
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

    private string GetShrinkUAssemblyVersion()
    {
        try
        {
            var asm = typeof(ShrinkU.Plugin).Assembly;
            var v = asm?.GetName()?.Version;
            return v?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
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
        UiSharedService.ColorTextWrapped("Customize the appearance of the Sphene UI with real-time preview. Changes are applied immediately.", ImGuiColors.DalamudGrey);
        
        ImGui.Separator();
        
        // Theme Selector
        DrawThemeSelector();
        
        ImGui.Separator();
        
        if (ImGui.BeginTabBar("ThemeTabBar"))
        {
            // Calculate available height for the tab content to fill remaining space
            var availableRegion = ImGui.GetContentRegionAvail();
            var tabContentHeight = availableRegion.Y;
            
            if (ImGui.BeginTabItem("General Theme"))
            {
                if (ImGui.BeginChild("GeneralThemeChild", new Vector2(0, tabContentHeight), true))
                {
                    DrawGeneralThemeSettings();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Panel Theme"))
            {
                if (ImGui.BeginChild("PanelThemeChild", new Vector2(0, tabContentHeight), true))
                {
                    DrawCompactUIThemeSettings();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }
            
            ImGui.EndTabBar();
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
    
    private void DrawThemeColorSettings()
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        bool themeChanged = false;
        
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
            
            ImGui.EndTabBar();
        }
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
            _hasUnsavedThemeChanges = true;
        }
    }
    
    private void DrawScrollbarSettings()
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        bool themeChanged = false;
        
        ImGui.Text("Scrollbar Settings");
        ImGui.Separator();
        
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
        
        var scrollbarSize = theme.ScrollbarSize;
        if (ImGui.SliderFloat("Scrollbar Size", ref scrollbarSize, 5.0f, 30.0f, "%.1f"))
        {
            theme.ScrollbarSize = scrollbarSize;
            themeChanged = true;
        }
        
        ImGui.Separator();
        ImGui.Text("Scrollbar Colors");
        ImGui.Separator();
        
        DrawScrollbarSliderColors(theme, ref themeChanged);
        
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
        
        ImGui.Text("Panel Specific Settings");
        ImGui.Separator();
        
        UiSharedService.ColorTextWrapped("These settings apply only to the Panel window to maintain its distinct appearance.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        // Panel Header Settings
        ImGui.Text("Panel Header Options");
        ImGui.Separator();
        
        var compactShowImGuiHeader = theme.CompactShowImGuiHeader;
        if (ImGui.Checkbox("Show ImGui Header", ref compactShowImGuiHeader))
        {
            theme.CompactShowImGuiHeader = compactShowImGuiHeader;
            themeChanged = true;
        }
        UiSharedService.ColorTextWrapped("Enable to show the standard ImGui window header with collapse functionality. Disable to use the custom Sphene header.", ImGuiColors.DalamudGrey);
        
        ImGui.Spacing();
        ImGui.Text("Panel Rounding Settings");
        ImGui.Separator();
        var compactWindowRounding = theme.CompactWindowRounding;
        if (ImGui.SliderFloat("Panel Window Rounding", ref compactWindowRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactWindowRounding = compactWindowRounding;
            themeChanged = true;
        }
        
        var compactChildRounding = theme.CompactChildRounding;
        if (ImGui.SliderFloat("Panel Child Rounding", ref compactChildRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactChildRounding = compactChildRounding;
            themeChanged = true;
        }
        
        var compactPopupRounding = theme.CompactPopupRounding;
        if (ImGui.SliderFloat("Panel Popup Rounding", ref compactPopupRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactPopupRounding = compactPopupRounding;
            themeChanged = true;
        }
        
        var compactFrameRounding = theme.CompactFrameRounding;
        if (ImGui.SliderFloat("Panel Frame Rounding", ref compactFrameRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactFrameRounding = compactFrameRounding;
            themeChanged = true;
        }
        
        var compactScrollbarRounding = theme.CompactScrollbarRounding;
        if (ImGui.SliderFloat("Panel Scrollbar Rounding", ref compactScrollbarRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactScrollbarRounding = compactScrollbarRounding;
            themeChanged = true;
        }
        
        var compactGrabRounding = theme.CompactGrabRounding;
        if (ImGui.SliderFloat("Panel Grab Rounding", ref compactGrabRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactGrabRounding = compactGrabRounding;
            themeChanged = true;
        }
        
        var compactTabRounding = theme.CompactTabRounding;
        if (ImGui.SliderFloat("Panel Tab Rounding", ref compactTabRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactTabRounding = compactTabRounding;
            themeChanged = true;
        }
        
        var compactHeaderRounding = theme.CompactHeaderRounding;
        if (ImGui.SliderFloat("Panel Header Rounding", ref compactHeaderRounding, 0.0f, 30.0f, "%.1f"))
        {
            theme.CompactHeaderRounding = compactHeaderRounding;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        ImGui.Text("Panel Spacing & Sizing");
        ImGui.Separator();
        
        // Panel Spacing Settings
        var compactWindowPadding = theme.CompactWindowPadding;
        if (ImGui.SliderFloat2("Panel Window Padding", ref compactWindowPadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactWindowPadding = compactWindowPadding;
            themeChanged = true;
        }
        
        var compactFramePadding = theme.CompactFramePadding;
        if (ImGui.SliderFloat2("Panel Frame Padding", ref compactFramePadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactFramePadding = compactFramePadding;
            themeChanged = true;
        }
        
        var compactItemSpacing = theme.CompactItemSpacing;
        if (ImGui.SliderFloat2("Panel Item Spacing", ref compactItemSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactItemSpacing = compactItemSpacing;
            themeChanged = true;
        }
        
        var compactItemInnerSpacing = theme.CompactItemInnerSpacing;
        if (ImGui.SliderFloat2("Panel Item Inner Spacing", ref compactItemInnerSpacing, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactItemInnerSpacing = compactItemInnerSpacing;
            themeChanged = true;
        }
        
        var compactCellPadding = theme.CompactCellPadding;
        if (ImGui.SliderFloat2("Panel Cell Padding", ref compactCellPadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactCellPadding = compactCellPadding;
            themeChanged = true;
        }
        
        var compactChildPadding = theme.CompactChildPadding;
        if (ImGui.SliderFloat2("Panel Child Padding", ref compactChildPadding, 0.0f, 20.0f, "%.1f"))
        {
            theme.CompactChildPadding = compactChildPadding;
            themeChanged = true;
        }
        UiSharedService.ColorTextWrapped("Controls padding inside child windows to prevent content squashing when borders are enabled.", ImGuiColors.DalamudGrey);
        
        var compactIndentSpacing = theme.CompactIndentSpacing;
        if (ImGui.SliderFloat("Panel Indent Spacing", ref compactIndentSpacing, 0.0f, 50.0f, "%.1f"))
        {
            theme.CompactIndentSpacing = compactIndentSpacing;
            themeChanged = true;
        }
        
        var compactScrollbarSize = theme.CompactScrollbarSize;
        if (ImGui.SliderFloat("Panel Scrollbar Size", ref compactScrollbarSize, 1.0f, 30.0f, "%.1f"))
        {
            theme.CompactScrollbarSize = compactScrollbarSize;
            themeChanged = true;
        }
        
        var compactGrabMinSize = theme.CompactGrabMinSize;
        if (ImGui.SliderFloat("Panel Grab Min Size", ref compactGrabMinSize, 1.0f, 30.0f, "%.1f"))
        {
            theme.CompactGrabMinSize = compactGrabMinSize;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        ImGui.Text("Panel Text Alignment");
        ImGui.Separator();
        
        var compactButtonTextAlign = theme.CompactButtonTextAlign;
        if (ImGui.SliderFloat2("Panel Button Text Align", ref compactButtonTextAlign, 0.0f, 1.0f, "%.2f"))
        {
            theme.CompactButtonTextAlign = compactButtonTextAlign;
            themeChanged = true;
        }
        
        var compactSelectableTextAlign = theme.CompactSelectableTextAlign;
        if (ImGui.SliderFloat2("Panel Selectable Text Align", ref compactSelectableTextAlign, 0.0f, 1.0f, "%.2f"))
        {
            theme.CompactSelectableTextAlign = compactSelectableTextAlign;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        ImGui.Text("Panel Border Thickness");
        ImGui.Separator();
        
        var compactWindowBorderSize = theme.CompactWindowBorderSize;
        if (ImGui.SliderFloat("Panel Window Border Size", ref compactWindowBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.CompactWindowBorderSize = compactWindowBorderSize;
            themeChanged = true;
        }
        
        var compactChildBorderSize = theme.CompactChildBorderSize;
        if (ImGui.SliderFloat("Panel Child Border Size", ref compactChildBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.CompactChildBorderSize = compactChildBorderSize;
            themeChanged = true;
        }
        
        var compactPopupBorderSize = theme.CompactPopupBorderSize;
        if (ImGui.SliderFloat("Panel Popup Border Size", ref compactPopupBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.CompactPopupBorderSize = compactPopupBorderSize;
            themeChanged = true;
        }
        
        var compactFrameBorderSize = theme.CompactFrameBorderSize;
        if (ImGui.SliderFloat("Panel Frame Border Size", ref compactFrameBorderSize, 0.0f, 5.0f, "%.1f"))
        {
            theme.CompactFrameBorderSize = compactFrameBorderSize;
            themeChanged = true;
        }
        
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
        ImGui.Text("Panel Progress Bar Settings");
        ImGui.Separator();
        
        // CompactUI Progress Bar Settings
        var progressBarRounding = theme.ProgressBarRounding;
        if (ImGui.SliderFloat("Progress Bar Rounding", ref progressBarRounding, 0.0f, 20.0f, "%.1f"))
        {
            theme.ProgressBarRounding = progressBarRounding;
            themeChanged = true;
        }
        
        var compactProgressBarHeight = theme.CompactProgressBarHeight;
        if (ImGui.SliderFloat("Progress Bar Height", ref compactProgressBarHeight, 5.0f, 50.0f, "%.1f"))
        {
            theme.CompactProgressBarHeight = compactProgressBarHeight;
            themeChanged = true;
        }
        
        var compactProgressBarWidth = theme.CompactProgressBarWidth;
        if (ImGui.SliderFloat("Progress Bar Width", ref compactProgressBarWidth, 50.0f, 500.0f, "%.1f"))
        {
            theme.CompactProgressBarWidth = compactProgressBarWidth;
            themeChanged = true;
        }
        
        var compactProgressBarBackground = theme.CompactProgressBarBackground;
        if (ImGui.ColorEdit4("Progress Bar Background", ref compactProgressBarBackground))
        {
            theme.CompactProgressBarBackground = compactProgressBarBackground;
            themeChanged = true;
        }
        
        var compactProgressBarForeground = theme.CompactProgressBarForeground;
        if (ImGui.ColorEdit4("Progress Bar Foreground", ref compactProgressBarForeground))
        {
            theme.CompactProgressBarForeground = compactProgressBarForeground;
            themeChanged = true;
        }
        
        var compactProgressBarBorder = theme.CompactProgressBarBorder;
        if (ImGui.ColorEdit4("Progress Bar Border", ref compactProgressBarBorder))
        {
            theme.CompactProgressBarBorder = compactProgressBarBorder;
            themeChanged = true;
        }
        
        // Progress Bar Preview Toggle
        ImGui.Spacing();
        var showPreview = theme.ShowProgressBarPreview;
        if (ImGui.Checkbox("Show Progress Bar Preview in UI", ref showPreview))
        {
            theme.ShowProgressBarPreview = showPreview;
            themeChanged = true;
        }
        _uiShared.DrawHelpText("When enabled, shows preview progress bars in the CompactUI and under your character");
        
        // Progress Bar Preview Fill Slider
        if (theme.ShowProgressBarPreview)
        {
            var previewFill = theme.ProgressBarPreviewFill;
            if (ImGui.SliderFloat("Preview Fill Percentage", ref previewFill, 0.0f, 100.0f, "%.1f%%"))
            {
                theme.ProgressBarPreviewFill = previewFill;
                themeChanged = true;
            }
            _uiShared.DrawHelpText("Adjusts how full the preview progress bars appear");
        }
        
        // Progress Bar Preview
        ImGui.Spacing();
        ImGui.Text("Progress Bar Preview");
        ImGui.Separator();
        
        // Sample progress bars using the preview fill value
        var previewProgress = theme.ProgressBarPreviewFill / 100.0f;
        _uiShared.DrawThemedProgressBar("Preview Progress Bar", previewProgress, $"{theme.ProgressBarPreviewFill:F1}%");
        
        ImGui.Spacing();
        ImGui.Text("Panel Transmission Bar Settings (Under Players)");
        ImGui.Separator();
        
        // CompactUI Transmission Progress Bar Settings
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
        
        var compactTransmissionBarWidth = theme.CompactTransmissionBarWidth;
        if (ImGui.SliderFloat("Transmission Bar Width", ref compactTransmissionBarWidth, 30.0f, 300.0f, "%.1f"))
        {
            theme.CompactTransmissionBarWidth = compactTransmissionBarWidth;
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
        
        ImGui.Spacing();
        ImGui.Text("Panel Status Colors");
        ImGui.Separator();
        
        var compactUidColor = theme.CompactUidColor;
        if (ImGui.ColorEdit4("Panel UID Color", ref compactUidColor))
        {
            theme.CompactUidColor = compactUidColor;
            themeChanged = true;
        }
        
        var compactServerStatusConnected = theme.CompactServerStatusConnected;
        if (ImGui.ColorEdit4("Panel Server Status Connected", ref compactServerStatusConnected))
        {
            theme.CompactServerStatusConnected = compactServerStatusConnected;
            themeChanged = true;
        }
        
        var compactServerStatusWarning = theme.CompactServerStatusWarning;
        if (ImGui.ColorEdit4("Panel Server Status Warning", ref compactServerStatusWarning))
        {
            theme.CompactServerStatusWarning = compactServerStatusWarning;
            themeChanged = true;
        }
        
        var compactServerStatusError = theme.CompactServerStatusError;
        if (ImGui.ColorEdit4("Panel Server Status Error", ref compactServerStatusError))
        {
            theme.CompactServerStatusError = compactServerStatusError;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
        }
        
        ImGui.Separator();
        ImGui.Text("Panel Theme Colors");
        ImGui.Separator();
        
        if (ImGui.BeginTabBar("PanelColorTabBar"))
        {
            if (ImGui.BeginTabItem("Panel Colors"))
            {
                DrawCompactUIColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            if (ImGui.BeginTabItem("Button Styles"))
            {
                ButtonStyleManagerUI.Draw();
                ImGui.EndTabItem();
            }
            
            ImGui.EndTabBar();
        }
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
        }
    }

    private void DrawColorSettings()
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        bool themeChanged = false;
        
        ImGui.Text("Color Customization");
        ImGui.Separator();
        
        if (ImGui.BeginTabBar("ColorTabBar"))
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
            
            if (ImGui.BeginTabItem("Panel Colors"))
            {
                DrawCompactUIColors(theme, ref themeChanged);
                ImGui.EndTabItem();
            }
            
            ImGui.EndTabBar();
        }
        
        // Apply changes in real-time
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
        }
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
    
    private void DrawCompactUIColors(ThemeConfiguration theme, ref bool themeChanged)
    {
        ImGui.Text("Panel Specific Colors");
        UiSharedService.ColorTextWrapped("These colors are used specifically for the Panel window when different from normal windows.", ImGuiColors.DalamudGrey);
        ImGui.Spacing();
        
        // Background Colors Section
        ImGui.Text("Background Colors");
        ImGui.Separator();
        
        var compactWindowBg = theme.CompactWindowBg;
        if (ImGui.ColorEdit4("Panel Window Background", ref compactWindowBg))
        {
            theme.CompactWindowBg = compactWindowBg;
            themeChanged = true;
        }
        
        var compactTitleBg = theme.CompactTitleBg;
        if (ImGui.ColorEdit4("Panel Title Background", ref compactTitleBg))
        {
            theme.CompactTitleBg = compactTitleBg;
            themeChanged = true;
        }
        
        var compactTitleBgActive = theme.CompactTitleBgActive;
        if (ImGui.ColorEdit4("Panel Title Background Active", ref compactTitleBgActive))
        {
            theme.CompactTitleBgActive = compactTitleBgActive;
            themeChanged = true;
        }
        
        var compactFrameBg = theme.CompactFrameBg;
        if (ImGui.ColorEdit4("Panel Frame Background", ref compactFrameBg))
        {
            theme.CompactFrameBg = compactFrameBg;
            themeChanged = true;
        }
        
        var compactHeaderBg = theme.CompactHeaderBg;
        if (ImGui.ColorEdit4("Panel Header Background", ref compactHeaderBg))
        {
            theme.CompactHeaderBg = compactHeaderBg;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        
        // Button Colors Section
        ImGui.Text("Button Colors");
        ImGui.Separator();
        
        // Panel Buttons
        ImGui.Text("Panel Buttons");
        var compactButton = theme.CompactButton;
        if (ImGui.ColorEdit4("Panel Button", ref compactButton))
        {
            theme.CompactButton = compactButton;
            themeChanged = true;
        }
        
        var compactButtonHovered = theme.CompactButtonHovered;
        if (ImGui.ColorEdit4("Panel Button Hovered", ref compactButtonHovered))
        {
            theme.CompactButtonHovered = compactButtonHovered;
            themeChanged = true;
        }
        
        var compactButtonActive = theme.CompactButtonActive;
        if (ImGui.ColorEdit4("Panel Button Active", ref compactButtonActive))
        {
            theme.CompactButtonActive = compactButtonActive;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        
        // Action Buttons
        ImGui.Text("Action Buttons");
        var compactActionButton = theme.CompactActionButton;
        if (ImGui.ColorEdit4("Action Button", ref compactActionButton))
        {
            theme.CompactActionButton = compactActionButton;
            themeChanged = true;
        }
        
        var compactActionButtonHovered = theme.CompactActionButtonHovered;
        if (ImGui.ColorEdit4("Action Button Hovered", ref compactActionButtonHovered))
        {
            theme.CompactActionButtonHovered = compactActionButtonHovered;
            themeChanged = true;
        }
        
        var compactActionButtonActive = theme.CompactActionButtonActive;
        if (ImGui.ColorEdit4("Action Button Active", ref compactActionButtonActive))
        {
            theme.CompactActionButtonActive = compactActionButtonActive;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        
        // Syncshell Buttons
        ImGui.Text("Syncshell Buttons");
        var compactSyncshellButton = theme.CompactSyncshellButton;
        if (ImGui.ColorEdit4("Syncshell Button", ref compactSyncshellButton))
        {
            theme.CompactSyncshellButton = compactSyncshellButton;
            themeChanged = true;
        }
        
        var compactSyncshellButtonHovered = theme.CompactSyncshellButtonHovered;
        if (ImGui.ColorEdit4("Syncshell Button Hovered", ref compactSyncshellButtonHovered))
        {
            theme.CompactSyncshellButtonHovered = compactSyncshellButtonHovered;
            themeChanged = true;
        }
        
        var compactSyncshellButtonActive = theme.CompactSyncshellButtonActive;
        if (ImGui.ColorEdit4("Syncshell Button Active", ref compactSyncshellButtonActive))
        {
            theme.CompactSyncshellButtonActive = compactSyncshellButtonActive;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        
        // Text Colors Section
        ImGui.Text("Text Colors");
        ImGui.Separator();
        
        // Basic Text Colors
        ImGui.Text("Basic Text");
        var compactText = theme.CompactText;
        if (ImGui.ColorEdit4("Panel Text", ref compactText))
        {
            theme.CompactText = compactText;
            themeChanged = true;
        }
        
        var compactTextSecondary = theme.CompactTextSecondary;
        if (ImGui.ColorEdit4("Panel Secondary Text", ref compactTextSecondary))
        {
            theme.CompactTextSecondary = compactTextSecondary;
            themeChanged = true;
        }
        
        var compactHeaderText = theme.CompactHeaderText;
        if (ImGui.ColorEdit4("Panel Header Text", ref compactHeaderText))
        {
            theme.CompactHeaderText = compactHeaderText;
            themeChanged = true;
        }
        
        var compactPanelTitleText = theme.CompactPanelTitleText;
        if (ImGui.ColorEdit4("Panel Title Text", ref compactPanelTitleText))
        {
            theme.CompactPanelTitleText = compactPanelTitleText;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        
        // Status Text Colors
        ImGui.Text("Status Text");
        var compactConnectedText = theme.CompactConnectedText;
        if (ImGui.ColorEdit4("Connected Pairs Count Text", ref compactConnectedText))
        {
            theme.CompactConnectedText = compactConnectedText;
            themeChanged = true;
        }
        
        var compactAllSyncshellsText = theme.CompactAllSyncshellsText;
        if (ImGui.ColorEdit4("All Syncshells Text", ref compactAllSyncshellsText))
        {
            theme.CompactAllSyncshellsText = compactAllSyncshellsText;
            themeChanged = true;
        }
        
        var compactOfflinePausedText = theme.CompactOfflinePausedText;
        if (ImGui.ColorEdit4("Offline / Paused by other Text", ref compactOfflinePausedText))
        {
            theme.CompactOfflinePausedText = compactOfflinePausedText;
            themeChanged = true;
        }
        
        var compactOfflineSyncshellText = theme.CompactOfflineSyncshellText;
        if (ImGui.ColorEdit4("Offline Syncshell Users Text", ref compactOfflineSyncshellText))
        {
            theme.CompactOfflineSyncshellText = compactOfflineSyncshellText;
            themeChanged = true;
        }
        
        var compactVisibleText = theme.CompactVisibleText;
        if (ImGui.ColorEdit4("Visible Text", ref compactVisibleText))
        {
            theme.CompactVisibleText = compactVisibleText;
            themeChanged = true;
        }
        
        var compactPairsText = theme.CompactPairsText;
        if (ImGui.ColorEdit4("Pairs Text", ref compactPairsText))
        {
            theme.CompactPairsText = compactPairsText;
            themeChanged = true;
        }
        
        ImGui.Spacing();
        
        // Other Colors Section
        ImGui.Text("Other Colors");
        ImGui.Separator();
        
        var compactBorder = theme.CompactBorder;
        if (ImGui.ColorEdit4("Panel Border", ref compactBorder))
        {
            theme.CompactBorder = compactBorder;
            themeChanged = true;
        }
        
        var compactAccent = theme.CompactAccent;
        if (ImGui.ColorEdit4("Panel Accent", ref compactAccent))
        {
            theme.CompactAccent = compactAccent;
            themeChanged = true;
        }
        
        var compactHover = theme.CompactHover;
        if (ImGui.ColorEdit4("Panel Hover", ref compactHover))
        {
            theme.CompactHover = compactHover;
            themeChanged = true;
        }
        
        var compactActive = theme.CompactActive;
        if (ImGui.ColorEdit4("Panel Active", ref compactActive))
        {
            theme.CompactActive = compactActive;
            themeChanged = true;
        }
        
        // Apply changes in real-time for CompactUI
        if (themeChanged)
        {
            theme.NotifyThemeChanged();
            _hasUnsavedThemeChanges = true;
        }
    }

    private string _saveThemeName = "";
    private string _selectedThemeToLoad = "";
    private string[] _availableThemes = Array.Empty<string>();
    private bool _themesLoaded = false;
    private string _selectedPresetTheme = "";
    
    // Theme change tracking
    private bool _hasUnsavedThemeChanges = false;
    private ThemeConfiguration? _originalThemeState = null;
    private string _currentThemeName = "";
    private bool _showThemeSavePrompt = false;

    private void DrawSaveThemePopup()
    {
        ImGui.Text("Save Current Theme");
        ImGui.Separator();
        
        ImGui.Text("Theme Name:");
        ImGui.InputText("##SaveThemeName", ref _saveThemeName, 100);
        
        ImGui.Spacing();
        
        if (ImGui.Button("Save"))
        {
            if (!string.IsNullOrWhiteSpace(_saveThemeName))
            {
                // Note: We can't await in this UI context, so we'll fire and forget
                // The SaveTheme method will handle logging any errors
                _ = Task.Run(async () =>
                {
                    var success = await ThemeManager.SaveTheme(SpheneCustomTheme.CurrentTheme, _saveThemeName);
                    if (success)
                    {
                        // Reset UI state on main thread
                        _saveThemeName = "";
                        _themesLoaded = false; // Force refresh of available themes
                    }
                });
                ImGui.CloseCurrentPopup();
            }
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Cancel"))
        {
            _saveThemeName = "";
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawLoadThemePopup()
    {
        ImGui.Text("Load Theme");
        ImGui.Separator();
        
        // Load available themes if not already loaded
        if (!_themesLoaded)
        {
            _availableThemes = ThemeManager.GetAvailableThemes();
            _themesLoaded = true;
        }
        
        if (_availableThemes.Length == 0)
        {
            ImGui.Text("No saved themes found.");
        }
        else
        {
            ImGui.Text("Available Themes:");
            
            for (int i = 0; i < _availableThemes.Length; i++)
            {
                var themeName = _availableThemes[i];
                
                if (ImGui.Selectable(themeName, _selectedThemeToLoad == themeName))
                {
                    _selectedThemeToLoad = themeName;
                }
                
                // Double-click to load
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(0))
                {
                    LoadSelectedTheme();
                    return;
                }
                
                // Right-click context menu for delete
                if (ImGui.BeginPopupContextItem($"ThemeContext_{i}"))
                {
                    using (SpheneCustomTheme.ApplyContextMenuTheme())
                    {
                        if (ImGui.MenuItem("Delete"))
                        {
                            if (ThemeManager.DeleteTheme(themeName))
                            {
                                _themesLoaded = false; // Force refresh
                                _selectedThemeToLoad = "";
                            }
                        }
                    }
                    ImGui.EndPopup();
                }
            }
        }
        
        ImGui.Spacing();
        
        if (ImGui.Button("Load") && !string.IsNullOrEmpty(_selectedThemeToLoad))
        {
            LoadSelectedTheme();
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Refresh"))
        {
            _themesLoaded = false;
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Cancel"))
        {
            _selectedThemeToLoad = "";
            ImGui.CloseCurrentPopup();
        }
    }

    private void LoadSelectedTheme()
    {
        var loadedTheme = ThemeManager.LoadTheme(_selectedThemeToLoad);
        if (loadedTheme != null)
        {
            // Use the ThemePropertyCopier.Copy method to copy all properties
            ThemePropertyCopier.Copy(loadedTheme, SpheneCustomTheme.CurrentTheme);
            
            // Notify theme changed to apply immediately
            SpheneCustomTheme.CurrentTheme.NotifyThemeChanged();
            
            // Reset change tracking when loading a theme
            _hasUnsavedThemeChanges = false;
            _currentThemeName = _selectedThemeToLoad;
            
            _selectedThemeToLoad = "";
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawThemeSelector()
    {
        ImGui.Text("Theme Presets");
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
            if (selectedTheme.StartsWith("---")) return;
            
            _selectedPresetTheme = selectedTheme;
            ApplySelectedPresetTheme(selectedTheme, builtInThemes);
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Reset to Default Sphene Theme"))
        {
            _selectedPresetTheme = "Default Sphene";
            ApplySelectedPresetTheme("Default Sphene", builtInThemes);
        }
        
        // Theme Management Actions
        ImGui.Spacing();
        ImGui.Text("Theme Management");
        
        if (ImGui.Button("Reset to Defaults"))
        {
            if (_originalThemeState != null)
            {
                // Restore to the original theme state when the settings were opened
                ThemePropertyCopier.Copy(_originalThemeState, SpheneCustomTheme.CurrentTheme);
            }
            else
            {
                // Fallback to system defaults if no original state is available
                SpheneCustomTheme.CurrentTheme.ResetToDefaults();
            }
            SpheneCustomTheme.CurrentTheme.NotifyThemeChanged();
            
            // Reset change tracking
            _hasUnsavedThemeChanges = false;
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Save Theme"))
        {
            ImGui.OpenPopup("SaveThemePopup");
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Load Theme"))
        {
            ImGui.OpenPopup("LoadThemePopup");
        }
        
        // Save Theme Popup
        if (ImGui.BeginPopup("SaveThemePopup"))
        {
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                DrawSaveThemePopup();
            }
            ImGui.EndPopup();
        }
        
        // Load Theme Popup
        if (ImGui.BeginPopup("LoadThemePopup"))
        {
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                DrawLoadThemePopup();
            }
            ImGui.EndPopup();
        }
    }

    private void ApplySelectedPresetTheme(string themeName, List<string> builtInThemes)
    {
        var currentTheme = SpheneCustomTheme.CurrentTheme;
        
        if (builtInThemes.Contains(themeName))
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
        
        currentTheme.NotifyThemeChanged();
    }

    
    private bool CheckForUnsavedThemeChanges()
    {
        if (_originalThemeState == null || !_hasUnsavedThemeChanges)
            return false;
            
        // Check if current theme is a custom theme (not built-in)
        var builtInThemes = ThemePresets.BuiltInThemes.Keys.ToList();
        var isCustomTheme = !builtInThemes.Contains(_currentThemeName);
        
        if (isCustomTheme)
        {
            // Auto-save changes to existing custom theme
            _ = Task.Run(async () =>
            {
                var success = await ThemeManager.SaveTheme(SpheneCustomTheme.CurrentTheme, _currentThemeName);
                if (success)
                {
                    _logger.LogDebug($"Auto-saved changes to custom theme: {_currentThemeName}");
                }
            });
            return false; // Don't show prompt, just auto-save
        }
        else
        {
            // Show prompt to create new theme for built-in themes
            _showThemeSavePrompt = true;
            return true; // Show prompt, keep window open
        }
    }
    
    private void DrawThemeSavePrompt()
    {
        if (!_showThemeSavePrompt)
            return;
            
        ImGui.OpenPopup("Unsaved Theme Changes");
        
        if (ImGui.BeginPopupModal("Unsaved Theme Changes", ref _showThemeSavePrompt, ImGuiWindowFlags.AlwaysAutoResize))
        {
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                ImGui.Text("You have made changes to the theme settings.");
                ImGui.Text("Would you like to save these changes as a new custom theme?");
                ImGui.Spacing();
                
                ImGui.Text("Theme Name:");
                ImGui.InputText("##NewThemeName", ref _saveThemeName, 100);
                
                ImGui.Spacing();
                
                if (ImGui.Button("Save as New Theme"))
                {
                    if (!string.IsNullOrWhiteSpace(_saveThemeName))
                    {
                        // Capture the theme name before clearing it
                        var themeNameToSave = _saveThemeName;
                        _ = Task.Run(async () =>
                        {
                            var success = await ThemeManager.SaveTheme(SpheneCustomTheme.CurrentTheme, themeNameToSave);
                            if (success)
                            {
                                _logger.LogDebug($"Created new custom theme: {themeNameToSave}");
                                // Update current theme name for future auto-saves
                                _currentThemeName = themeNameToSave;
                                ThemeManager.SetSelectedTheme(themeNameToSave);
                                
                                // Reset change tracking
                                _hasUnsavedThemeChanges = false;
                            }
                        });
                        _saveThemeName = "";
                        _showThemeSavePrompt = false;
                        ImGui.CloseCurrentPopup();
                    }
                }
                
                ImGui.SameLine();
                
                if (ImGui.Button("Discard Changes"))
                {
                    // Restore original theme state
                    if (_originalThemeState != null)
                    {
                        ThemePropertyCopier.Copy(_originalThemeState, SpheneCustomTheme.CurrentTheme);
                        SpheneCustomTheme.CurrentTheme.NotifyThemeChanged();
                    }
                    _hasUnsavedThemeChanges = false;
                    _showThemeSavePrompt = false;
                    ImGui.CloseCurrentPopup();
                }
                
                ImGui.SameLine();
                
                if (ImGui.Button("Keep Changes"))
                {
                    _hasUnsavedThemeChanges = false;
                    _showThemeSavePrompt = false;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
    }
}
