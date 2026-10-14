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
    private readonly TextureBackupService _textureBackupService;
    private readonly AreaBoundSyncshellService _areaBoundSyncshellService;
    private readonly ShrinkUHostService _shrinkuHostService;
    private readonly SpheneProfileManager _spheneProfileManager;
    private byte[] _lastAvatarBytes = [];
    private byte[] _lastRowImageBytes = [];
    private float _profileBarHoverT;
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
    // Unscaled height of the profile title bar (34 avatar + 2*6 padding)
    private const float ProfileBarHeight = 46f;
    /// <summary>Horizontal inset for header content rows — the window runs
    /// edge to edge, so content keeps its own breathing room.</summary>
    private const float HeaderContentPadX = 14f;
#if IS_TEST_BUILD
    private const string TestServerDisclaimerPopupName = "Test Server Disclaimer";
    private bool _showTestServerDisclaimerPopup;
    private DateTime _testServerDisclaimerOpenedAt = DateTime.MinValue;
#endif
    
    private bool _showModalForUserAddition;
    private float _transferPartHeight;
    private bool _wasOpen;
    private bool _aprilFoolsHyperSyncEnabled;
    private DateTime _aprilFoolsHyperSyncActivatedAt = DateTime.MinValue;
    
    
    // Halloween background texture
    private readonly IDalamudTextureWrap? _halloweenBackgroundTexture = null;
    
    
    // Texture conversion fields
    
    private bool _conversionWindowOpen = false;
    private bool _conversionProgressWindowOpen = false;
    private bool _autoSilentConversion = false;
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private Task? _conversionTask;
    private CancellationTokenSource _conversionCancellationTokenSource = new();
    private readonly Progress<(string fileName, int progress)> _conversionProgress = new();
    private int _conversionCurrentFileProgress;
    private DateTime _conversionStartTime = DateTime.MinValue;
    private string _currentModName = string.Empty;
    private int _currentModIndex = 0;
    private int _totalMods = 0;
    private int _currentModTotalFiles = 0;
    private DateTime _currentModStartedAt = DateTime.MinValue;
    // Backup progress fields for progress UI
    private readonly Progress<(string fileName, int current, int total)> _backupProgress = new();
    private string _backupCurrentFileName = string.Empty;
    private int _backupCurrentIndex = 0;
    private int _backupTotalCount = 0;
    private DateTime _backupStartTime = DateTime.MinValue;
    private readonly List<string> _backupStepLog = new();
    private int _backupTextureCount = 0;
    private int _backupZipCount = 0;
    private int _backupPmpCount = 0;
    // Automatic conversion mode and gating to avoid repeated triggers across frames/analyses
    private bool _automaticModeEnabled = false;
    
    private string _autoConvertKey = string.Empty;
    
    // Popup analysis gating
    private Task? _popupAnalysisTask;
    private bool _isPopupAnalysisBlocking;
    
    
    // Backup restore fields
    private Dictionary<string, List<string>>? _cachedBackupsForAnalysis;
    private DateTime _lastBackupAnalysisUpdate = DateTime.MinValue;
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysisForPopup;
    private Task? _restoreTask;
    private CancellationTokenSource _restoreCancellationTokenSource = new();
    private readonly Progress<(string fileName, int current, int total)> _restoreProgress = new();
    
    private int _restoreCurrentIndex = 0;
    private int _restoreTotalCount = 0;
    private DateTime _restoreStartTime = DateTime.MinValue;
    private bool _restoreWindowOpen = false;
    private string _restoreStepText = string.Empty;
    private volatile bool _isRestoreInProgress = false;
    private int _restoreModsTotal = 0;
    private int _restoreModsDone = 0;
    private string _currentRestoreMod = string.Empty;

    // Async ShrinkU mod backup detection cache
    private Dictionary<string, List<string>>? _cachedShrinkUModBackups;
    private Task? _shrinkUDetectionTask;
    private DateTime _lastShrinkUDetectionUpdate = DateTime.MinValue;
    private volatile bool _isTextureBackupScanInProgress;
    
    // Async texture backup detection cache
    private Dictionary<string, List<string>>? _cachedTextureBackupsFiltered;
    private Task<Dictionary<string, List<string>>>? _textureDetectionTask;
    private DateTime _lastTextureDetectionUpdate = DateTime.MinValue;
    private DateTime _backupScanTriggeredAt = DateTime.MinValue;
    private readonly Lock _backupScanDebounceLock = new();
    private CancellationTokenSource? _backupScanDebounceCts;
    private string _pendingBackupScanReason = string.Empty;
    private readonly SemaphoreSlim _shrinkUDetectionSemaphore = new(1, 1);
    private volatile bool _isDisposed;

    // Stable UI text caching to avoid flicker in backup sections
    private string _allBackupsKey = string.Empty;
    private string _allBackupsStatusText = string.Empty;
    private string _currentBackupsKey = string.Empty;
    private string _currentBackupsStatusText = string.Empty;

    // Storage info fields for backup management
    private Task<(long totalSize, int fileCount)>? _storageInfoTask;
    private DateTime _lastStorageInfoUpdate = DateTime.MinValue;
    private (long totalSize, int fileCount) _cachedStorageInfo;
    private readonly ShrinkU.Services.TextureBackupService _shrinkuBackupService;
    private readonly ShrinkU.Services.TextureConversionService _shrinkuConversionService;
    private readonly ShrinkUConfigService _shrinkuConfigService;

    public CompactUi(ILogger<CompactUi> logger, SpheneConfigService configService, ApiController apiController, PairManager pairManager,
        ServerConfigurationManager serverManager, SpheneMediator mediator, FileUploadManager fileTransferManager,
        TagHandler tagHandler, DrawEntityFactory drawEntityFactory, SelectTagForPairUi selectTagForPairUi, SelectPairForTagUi selectPairForTagUi,
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, DalamudUtilService dalamudUtilService, CharacterAnalyzer characterAnalyzer,
        TextureBackupService textureBackupService, AreaBoundSyncshellService areaBoundSyncshellService,
        ShrinkU.Services.TextureBackupService shrinkuBackupService, ShrinkU.Services.TextureConversionService shrinkuConversionService,
        ShrinkUConfigService shrinkuConfigService,
        ShrinkUHostService shrinkuHostService, SpheneProfileManager spheneProfileManager)
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
        _textureBackupService = textureBackupService;
        _shrinkuBackupService = shrinkuBackupService;
        _shrinkuConversionService = shrinkuConversionService;
        _shrinkuConfigService = shrinkuConfigService;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        _shrinkuHostService = shrinkuHostService;
        _spheneProfileManager = spheneProfileManager;
        try
        {
            _automaticModeEnabled = _shrinkuConfigService.Current.TextureProcessingMode == TextureProcessingMode.Automatic;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read ShrinkU configuration for automatic mode");
        }
        
        // Setup conversion progress handler
        _conversionProgress.ProgressChanged += (sender, progress) =>
        {
            _conversionCurrentFileProgress = progress.progress;
        };
        _shrinkuConversionService.OnConversionProgress += e =>
        {
            try
            {
                _conversionCurrentFileProgress = e.Item2;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update conversion progress");
            }
        };
        _shrinkuConversionService.OnModProgress += e =>
        {
            try
            {
                _currentModName = e.modName;
                _currentModIndex = e.current;
                _totalMods = e.total;
                _currentModTotalFiles = e.fileTotal;
                _conversionCurrentFileProgress = 0;
                _currentModStartedAt = DateTime.UtcNow;
                if (!_autoSilentConversion && !_automaticModeEnabled && !(_shrinkuHostService?.IsConversionUiOpen ?? false))
                    _conversionProgressWindowOpen = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update mod-level conversion progress");
            }
        };
        // Setup backup progress handlers (local + ShrinkU service)
        _backupProgress.ProgressChanged += (sender, progress) =>
        {
            try
            {
                _backupCurrentFileName = progress.fileName;
                _backupCurrentIndex = progress.current;
                _backupTotalCount = progress.total;
                if (_backupStartTime == DateTime.MinValue)
                    _backupStartTime = DateTime.UtcNow;
                AppendBackupStep(progress.fileName);
                if (!_autoSilentConversion && !_automaticModeEnabled && !(_shrinkuHostService?.IsConversionUiOpen ?? false))
                    _conversionProgressWindowOpen = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update backup progress");
            }
        };
        _shrinkuConversionService.OnBackupProgress += e =>
        {
            try
            {
                _backupCurrentFileName = e.Item1;
                _backupCurrentIndex = e.Item2;
                _backupTotalCount = e.Item3;
                if (_backupStartTime == DateTime.MinValue)
                    _backupStartTime = DateTime.UtcNow;
                AppendBackupStep(e.Item1);
                if (!_autoSilentConversion && !_automaticModeEnabled && !(_shrinkuHostService?.IsConversionUiOpen ?? false))
                    _conversionProgressWindowOpen = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update backup progress (service)");
            }
        };
        // Setup restore progress handler
        _restoreProgress.ProgressChanged += (sender, progress) =>
        {
            try
            {
                _restoreCurrentIndex = progress.current;
                _restoreTotalCount = progress.total;
                _restoreWindowOpen = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update restore progress");
            }
        };
        _tabMenu = new TopTabMenu(Mediator, _apiController, _pairManager);

        Mediator.Subscribe<PenumbraModTransferAvailableMessage>(this, OnModSharingTransferAvailable);
        Mediator.Subscribe<PenumbraModTransferCompletedMessage>(this, OnModSharingTransferCompleted);
        Mediator.Subscribe<PenumbraModTransferDiscardedMessage>(this, OnModSharingTransferDiscarded);
        Mediator.Subscribe<DisconnectedMessage>(this, _ => ClearPendingModSharing());

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
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (msg) =>
        {
            // Invalidate backup caches and ShrinkU detection when analysis changes
            _lastBackupAnalysisUpdate = DateTime.MinValue;
            _cachedBackupsForAnalysis = null;
            _cachedShrinkUModBackups = null;
            _shrinkUDetectionTask = null;
            _lastShrinkUDetectionUpdate = DateTime.MinValue;
            _cachedTextureBackupsFiltered = null;
            _textureDetectionTask = null;
            _lastTextureDetectionUpdate = DateTime.MinValue;
            // No need to cache analysis for popup; it reads live LastAnalysis now
            _cachedAnalysisForPopup = null;
            // Allow popup to proceed after analysis completes
            _popupAnalysisTask = null;
            _isPopupAnalysisBlocking = false;

            // Prewarm backup detection caches for faster popup responsiveness
            try
            {
                _isTextureBackupScanInProgress = true;
            _textureDetectionTask = Task.Run(() => GetBackupsForCurrentAnalysis());
            _ = _textureDetectionTask.ContinueWith(t =>
            {
                try { _cachedTextureBackupsFiltered = t.Status == TaskStatus.RanToCompletion ? t.Result : new Dictionary<string, List<string>>(StringComparer.Ordinal); }
                catch (Exception ex) { _cachedTextureBackupsFiltered = new Dictionary<string, List<string>>(StringComparer.Ordinal); _logger.LogDebug(ex, "Texture backup detection task failed"); }
                finally { _lastTextureDetectionUpdate = DateTime.UtcNow; _isTextureBackupScanInProgress = false; }
            }, TaskScheduler.Default);
            }
            catch (Exception ex) { _isTextureBackupScanInProgress = false; _logger.LogDebug(ex, "Failed to prewarm backup detection caches"); }
        try { _shrinkUDetectionTask = DetectShrinkUModBackupsAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to start ShrinkU detection task"); }
        });
        // React immediately to Penumbra state changes to refresh backup detection
        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {
            try { TriggerImmediateBackupScan("penumbra-initialized"); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: penumbra-initialized"); }
        });
        Mediator.Subscribe<PenumbraDirectoryChangedMessage>(this, (_) =>
        {
            try { TriggerImmediateBackupScan("penumbra-directory-changed"); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: penumbra-directory-changed"); }
        });
        // Also listen to ShrinkU conversion service broadcasts for mod changes and external texture updates
        try
        {
            _shrinkuConversionService.OnPenumbraModsChanged += () =>
            {
                if (_isRestoreInProgress) return;
                try { TriggerImmediateBackupScan("penumbra-mods-changed"); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: penumbra-mods-changed"); }
            };
            _shrinkuConversionService.OnPenumbraModSettingChanged += (change, collectionId, modDir, inherited) =>
            {
                if (_isRestoreInProgress) return;
                try { TriggerImmediateBackupScan("penumbra-mod-setting-changed"); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: penumbra-mod-setting-changed"); }
            };
            _shrinkuConversionService.OnExternalTexturesChanged += reason =>
            {
                var r = reason ?? string.Empty;
                if (_isRestoreInProgress) return;
                if (r.StartsWith("restore", StringComparison.OrdinalIgnoreCase)) return;
                try { TriggerImmediateBackupScan("external-textures-changed:" + r); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: external-textures-changed"); }
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to subscribe to ShrinkU conversion events");
        }
        Mediator.Subscribe<ShowUpdateNotificationMessage>(this, (msg) => _updateBannerInfo = msg.UpdateInfo);

        // Configure base window flags
        Flags |= ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        // Enforce minimum window size for control panel
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(350, 400)
        };

        // End of constructor
    }

    private void TriggerImmediateBackupScan(string reason)
    {
        var r = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        int delayMs = 350;
        if (r.StartsWith("penumbra-mod-setting-changed", StringComparison.OrdinalIgnoreCase))
            delayMs = 1200;
        else if (r.StartsWith("external-textures-changed", StringComparison.OrdinalIgnoreCase))
            delayMs = 900;

        CancellationTokenSource cts;
        lock (_backupScanDebounceLock)
        {
            _pendingBackupScanReason = r;
            DisposeCancellationTokenSource(ref _backupScanDebounceCts);
            _backupScanDebounceCts = new CancellationTokenSource();
            cts = _backupScanDebounceCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested)
                    return;
                ExecuteBackupScanNow(_pendingBackupScanReason);
            }
            catch (TaskCanceledException ex) when (cts.Token.IsCancellationRequested)
            {
                _logger.LogTrace(ex, "Backup scan debounce task canceled");
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Backup scan debounce task failed"); }
        });
    }

    private void DisposeCancellationTokenSource(ref CancellationTokenSource? source)
    {
        var local = Interlocked.Exchange(ref source, null);
        if (local == null)
            return;
        try
        {
            local.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogTrace(ex, "Cancellation token source already disposed before cancel");
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to cancel cancellation token source");
        }
        try
        {
            local.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to dispose cancellation token source");
        }
    }

    private void ExecuteBackupScanNow(string reason)
    {
        _logger.LogDebug("Triggering backup scan: {reason}", reason);
        _backupScanTriggeredAt = DateTime.UtcNow;
        _lastBackupAnalysisUpdate = DateTime.MinValue;
        _cachedBackupsForAnalysis = null;
        _cachedTextureBackupsFiltered = null;
        _textureDetectionTask = null;
        _lastTextureDetectionUpdate = DateTime.MinValue;
        _cachedShrinkUModBackups = null;
        _shrinkUDetectionTask = null;
        _lastShrinkUDetectionUpdate = DateTime.MinValue;

        try
        {
            _isTextureBackupScanInProgress = true;
            _textureDetectionTask = Task.Run(() => GetBackupsForCurrentAnalysis());
            _ = _textureDetectionTask.ContinueWith(t =>
            {
                try { _cachedTextureBackupsFiltered = t.Status == TaskStatus.RanToCompletion ? t.Result : new Dictionary<string, List<string>>(StringComparer.Ordinal); }
                catch (Exception ex) { _cachedTextureBackupsFiltered = new Dictionary<string, List<string>>(StringComparer.Ordinal); _logger.LogDebug(ex, "Texture backup detection task failed"); }
                finally { _lastTextureDetectionUpdate = DateTime.UtcNow; _isTextureBackupScanInProgress = false; }
            }, TaskScheduler.Default);
        }
        catch (Exception ex) { _isTextureBackupScanInProgress = false; _logger.LogDebug(ex, "Failed to start texture backup detection"); }

        try { _shrinkUDetectionTask = DetectShrinkUModBackupsAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to start ShrinkU mod backup detection"); }
    }

    // ShrinkU helpers for storage info and cleanup
    private async Task<(long totalSize, int fileCount)> GetShrinkUStorageInfoAsync()
    {
        try
        {
            var overview = await _shrinkuBackupService.GetBackupOverviewAsync().ConfigureAwait(false);
            long total = 0;
            int count = 0;
            foreach (var sess in overview)
            {
                if (sess == null) continue;
                if (sess.IsZip)
                {
                    if (File.Exists(sess.SourcePath))
                    {
                        var fi = new FileInfo(sess.SourcePath);
                        total += fi.Length;
                        count += 1;
                    }
                }
                else
                {
                    if (Directory.Exists(sess.SourcePath))
                    {
                        foreach (var f in Directory.EnumerateFiles(sess.SourcePath, "*", SearchOption.AllDirectories))
                        {

                            try { total += new FileInfo(f).Length; count++; }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to read file size during backup storage info"); }
                        }
                    }
                }
            }
            try
            {
                string backupDirectory = string.Empty;
                var firstPath = overview.FirstOrDefault()?.SourcePath;
                if (!string.IsNullOrWhiteSpace(firstPath))
                {
                    if (File.Exists(firstPath))
                    {
                        var modDir = Path.GetDirectoryName(firstPath) ?? string.Empty;
                        backupDirectory = Directory.GetParent(modDir)?.FullName ?? string.Empty;
                    }
                    else if (Directory.Exists(firstPath))
                    {
                        backupDirectory = Path.GetDirectoryName(firstPath) ?? string.Empty;
                    }
                }
                if (string.IsNullOrWhiteSpace(backupDirectory))
                {
                    backupDirectory = _textureBackupService.GetBackupDirectory();
                }
                if (!string.IsNullOrWhiteSpace(backupDirectory) && Directory.Exists(backupDirectory))
                {
                    foreach (var modDir in Directory.EnumerateDirectories(backupDirectory))
                    {
                        foreach (var pmp in Directory.EnumerateFiles(modDir, "mod_backup_*.pmp"))
                        {
                            try { total += new FileInfo(pmp).Length; count++; }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to read pmp file size during backup storage info"); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update PMP restore preference");
            }
            return (total, count);
        }
        catch
        {
            // Fallback to Sphene service
            return await _textureBackupService.GetBackupStorageInfoAsync().ConfigureAwait(false);
        }
    }


    

    // Removed ConvertSvgBase64ToPng method - no longer needed

    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
        if (disposing)
        {
            // Dispose all draw folders to clean up event subscriptions
            if (_drawFolders != null)
            {
                foreach (var folder in _drawFolders)
                {
                    folder.Dispose();
                }
            }
            
            // Dispose Halloween background texture
            _halloweenBackgroundTexture?.Dispose();

            _conversionCancellationTokenSource?.Cancel();
            _conversionCancellationTokenSource?.Dispose();
            _restoreCancellationTokenSource?.Cancel();
            _restoreCancellationTokenSource?.Dispose();
            DisposeCancellationTokenSource(ref _backupScanDebounceCts);
            _shrinkUDetectionSemaphore.Dispose();
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
        _themeScope = SpheneUI.Theme.CompactScope();

        // ImGui forces WindowRounding to 0 once the window owns a secondary
        // viewport (dragged onto another monitor) — its native frame would
        // render square there. The surface and border are drawn manually
        // instead, using the style radius which stays correct.
        Flags |= ImGuiWindowFlags.NoBackground;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        // Content runs edge to edge horizontally — the title bar and the
        // lower area both reach the window borders (content region = window rect)
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(0f, SpheneTheme.Current.CompactWindowPadding.Y));

        // Update window flags based on current theme settings
        if (!SpheneTheme.Current.CompactShowImGuiHeader)
        {
            // The custom profile bar acts as the title bar, including its own
            // fold — collapsed height is exactly the bar so nothing shows below
            Flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
            SpheneUI.Chrome.UpdateCollapse(this, ProfileFoldId, ProfileBarHeight);
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
        base.PreDraw();
    }

    public override void PostDraw()
    {
        // Dispose theme scope after drawing (pops the two vars pushed in
        // PreDraw — WindowPadding and WindowBorderSize — then the scope's own)
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
            if (ImGui.BeginPopupModal(TestServerDisclaimerPopupName, ref isOpen, SpheneUI.Popups.WindowFlags))
            {
                var elapsedSeconds = (DateTime.UtcNow - _testServerDisclaimerOpenedAt).TotalSeconds;
                var remainingSeconds = Math.Max(0.0, 20.0 - elapsedSeconds);
                var canConfirm = remainingSeconds <= 0.0;

                SpheneUI.Text.Colored("Test Server Disclaimer", SpheneTheme.Current.CompactHeaderText);
                ImGui.Separator();
                SpheneUI.Text.Wrapped("You are about to connect to the Test Server.");
                SpheneUI.Text.Wrapped("On the Test Server, a new UID is created automatically for you.");
                SpheneUI.Text.Wrapped("Anything you do there has no effect on your main server account.");
                SpheneUI.Text.Wrapped("If you find bugs, please report them on Discord.");

                ImGui.Spacing();
                if (SpheneUI.Icons.ActionButton(FontAwesomeIcon.Users, "Open Discord"))
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

                SpheneUI.Layout.SetScaledWindowSize(420);
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
        DrawInternalBody();

        // Window border ring above all content — the flush title bar fill
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
        // Theme is already applied in PreDraw, no need to apply it again here

        var theme = SpheneTheme.Current;
        SpheneUI.Chrome.WindowBlur(theme.CompactEnableWindowBlur ? theme.CompactWindowBlurStrength : 0f);

        // Self-drawn surface — the style radius stays correct even when ImGui
        // has zeroed the native window rounding (viewport-owned)
        var surfacePos = ImGui.GetWindowPos();
        var surfaceMax = surfacePos + ImGui.GetWindowSize();
        var surfaceRounding = ImGui.GetStyle().WindowRounding;
        var surfaceDrawList = ImGui.GetWindowDrawList();
        SpheneUI.Chrome.DrawWindowSurface(surfaceDrawList, surfacePos, surfaceMax, surfaceRounding);

        // Draw Halloween background first (behind all content)
        DrawHalloweenBackground();

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
                var onlineText = $"Connected Pairs ({onlineCount} / {totalCount}) online";
                
                // Center the text
                var textSize = ImGui.CalcTextSize(onlineText);
                var availableWidthPairs = ImGui.GetContentRegionAvail().X;
                var startX = (availableWidthPairs - textSize.X) / 2;
                if (startX < 0) startX = 0;
                
                ImGui.SetCursorPosX(startX);
                SpheneUI.Text.Colored(onlineText, SpheneTheme.Current.CompactHeaderText);
                
                using (ImRaii.PushId("pairlist")) DrawPairs();
                
                float pairlistEnd = ImGui.GetCursorPosY();
                using (ImRaii.PushIndent(SpheneTheme.Current.CompactPairListContentPadding))
                    using (ImRaii.PushId("transfers")) DrawTransfers();
                // reserve exactly the consumed height — the trailing item
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
                    SpheneUI.Text.Wrapped($"You have successfully added {_lastAddedUser.UserData.AliasOrUID}. Set a local note for the user in the field below:");
                    ImGui.InputTextWithHint("##noteforuser", $"Note for {_lastAddedUser.UserData.AliasOrUID}", ref _lastAddedUserComment, 100);
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

        // Conversion windows (non-blocking)
        DrawConversionWindow();
        DrawConversionProgressWindow();

        // Persistent Restore Progress Window
        DrawRestoreProgressWindow();

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
            
            Mediator.Publish(new CompactUiChange(_lastSize, _lastPosition));
        }
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
        bool hasAreaSyncshells = _areaBoundSyncshellService.HasAvailableAreaSyncshells();
        if (hasAreaSyncshells && !_hadAvailableAreaSyncshells)
        {
            _areaSyncshellAttentionUntilUtc = DateTime.UtcNow.AddSeconds(20);
        }
        _hadAvailableAreaSyncshells = hasAreaSyncshells;
        ImGui.SameLine();
        ImGui.Dummy(new Vector2(10, 0));
        ImGui.SameLine();
        var now = DateTime.UtcNow;
        var drawAttention = hasAreaSyncshells && now < _areaSyncshellAttentionUntilUtc;
        var iconButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.MapMarkerAlt);
        var wideButtonWidth = iconButtonSize.X + 18f * ImGuiHelpers.GlobalScale;
        if (drawAttention)
        {
            var time = (float)now.TimeOfDay.TotalSeconds;
            var pulse = 0.5f + 0.5f * MathF.Sin(time * 6.0f);
            var highlight = Vector4.Lerp(SpheneTheme.Current.CompactAccent, SpheneTheme.Current.CompactServerStatusWarning, pulse * 0.65f);
            var button = new Vector4(highlight.X, highlight.Y, highlight.Z, 0.55f);
            var hovered = new Vector4(highlight.X, highlight.Y, highlight.Z, 0.75f);
            var active = new Vector4(highlight.X, highlight.Y, highlight.Z, 0.9f);
            ImGui.PushStyleColor(ImGuiCol.Button, button);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
            ImGui.PushStyleColor(ImGuiCol.Text, SpheneTheme.Current.CompactText);
        }
        else if (!hasAreaSyncshells)
        {
            var baseColor = SpheneTheme.Current.CompactActionButton;
            var textColor = SpheneTheme.Current.CompactTextSecondary;
            var disabledButton = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 0.18f);
            var disabledHovered = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 0.18f);
            var disabledActive = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 0.18f);
            var disabledText = new Vector4(textColor.X, textColor.Y, textColor.Z, 0.55f);
            ImGui.PushStyleColor(ImGuiCol.Button, disabledButton);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, disabledHovered);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, disabledActive);
            ImGui.PushStyleColor(ImGuiCol.Text, disabledText);
        }
        bool clicked;
        using (ImRaii.Disabled(!hasAreaSyncshells))
        {
            clicked = SpheneUI.Icons.Button(FontAwesomeIcon.MapMarkerAlt, width: wideButtonWidth, styleKey: ButtonStyleKeys.Compact_AreaSelect);
        }
        if (drawAttention)
        {
            ImGui.PopStyleColor(4);
            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var badgeCenter = new Vector2(max.X - 7f, min.Y + 7f);
            drawList.AddCircleFilled(badgeCenter, 5.5f, ImGui.ColorConvertFloat4ToU32(SpheneTheme.Current.CompactServerStatusWarning));
            drawList.AddCircle(badgeCenter, 5.5f, ImGui.ColorConvertFloat4ToU32(SpheneTheme.Current.CompactBorder), 12, 1.5f);
        }
        else if (!hasAreaSyncshells)
        {
            ImGui.PopStyleColor(4);
        }
        if (clicked)
        {
            _areaBoundSyncshellService.TriggerAreaSyncshellSelection();
        }
        var hasAnyAreaSyncshellsHere = _areaBoundSyncshellService.HasAnyAreaSyncshellsInCurrentLocation();
        var isInAreaSyncshellHere = _areaBoundSyncshellService.IsInAreaSyncshellInCurrentLocation();
        SpheneUI.AttachTooltip(hasAreaSyncshells
            ? "Open Area Syncshell Selection"
            : !hasAnyAreaSyncshellsHere
                ? "No Area Syncshells found"
                : isInAreaSyncshellHere
                    ? "No other Area Syncshells found"
                    : "No Area Syncshells available");
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
        var iconWidth = _automaticModeEnabled && _shrinkuConversionService.IsConverting
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
                _conversionWindowOpen = true;
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
        
        // Only show upload progress if there are active uploads
        if (currentUploads.Any())
        {
            var totalUploads = currentUploads.Count;
            var doneUploads = currentUploads.Count(c => c.IsTransferred);
            var totalUploaded = currentUploads.Sum(c => c.Transferred);
            var totalToUpload = currentUploads.Sum(c => c.Total);

            var uploadProgress = totalToUpload > 0 ? (float)totalUploaded / totalToUpload : 0f;
            var uploadText = $"{doneUploads}/{totalUploads} ({UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)})";

            DrawTransferRow(FontAwesomeIcon.Upload, "Transmitting", uploadProgress, uploadText, true);
        }

        // Only show download progress if there are active downloads
        if (currentDownloads.Any())
        {
            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);

            var downloadProgress = totalToDownload > 0 ? (float)totalDownloaded / totalToDownload : 0f;
            var downloadText = $"{doneDownloads}/{totalDownloads} ({UiSharedService.ByteToString(totalDownloaded)}/{UiSharedService.ByteToString(totalToDownload)})";

            DrawTransferRow(FontAwesomeIcon.Download, "Receiving", downloadProgress, downloadText, false);
        }

        // Show transmission preview in-place when enabled
        var theme = Sphene.UI.Design.SpheneTheme.Current;
        if (theme.ShowTransmissionPreview)
        {
            if (!currentUploads.Any())
            {
                var previewUpload = theme.TransmissionPreviewUploadFill / 100.0f;
                DrawTransferRow(FontAwesomeIcon.Upload, "Transmitting", previewUpload,
                    $"{theme.TransmissionPreviewUploadFill:F1}%", true);
            }

            if (!currentDownloads.Any())
            {
                var previewDownload = theme.TransmissionPreviewDownloadFill / 100.0f;
                DrawTransferRow(FontAwesomeIcon.Download, "Receiving", previewDownload,
                    $"{theme.TransmissionPreviewDownloadFill:F1}%", false);
            }
        }
    }

    private static void DrawTransferRow(FontAwesomeIcon icon, string label,
        float progress, string detail, bool isUpload)
    {
        var theme = Sphene.UI.Design.SpheneTheme.Current;
        var barH = theme.SeparateTransmissionBarStyles
            ? (isUpload ? theme.UploadTransmissionBarHeight : theme.DownloadTransmissionBarHeight)
            : theme.CompactTransmissionBarHeight;
        var lineH = ImGui.GetTextLineHeight();
        progress = Math.Clamp(progress, 0f, 1f);

        // label line: icon + label left, percentage right-aligned
        SpheneUI.Icons.Icon(icon, theme.CompactTextSecondary);
        ImGui.SameLine(0, SpheneUI.Spacing.Inline.Scaled);
        SpheneUI.Text.Colored(label, theme.CompactText);
        var pct = $"{progress * 100f:F0}%";
        var pctW = ImGui.CalcTextSize(pct).X;
        // Indent only pulls the left edge in — subtract the list padding again
        // for the right bound so the bar aligns with pair rows
        var availX = SpheneUI.Controls.BoundedAvailX()
            - theme.CompactPairListContentPadding;
        var lineRight = ImGui.GetCursorPosX() + availX;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX() + SpheneUI.Spacing.X4.Scaled,
            lineRight - pctW));
        SpheneUI.Text.Muted(pct);

        var barW = theme.AutoTransmissionBarWidth
            ? MathF.Max(50f, availX)
            : MathF.Min(theme.CompactTransmissionBarWidth, availX);
        var barSize = new Vector2(barW, MathF.Max(2f, barH));
        var barPos = ImGui.GetCursorScreenPos();
        var overlay = barH >= lineH ? $"{detail} · {pct}" : null;

        ImGui.Dummy(barSize);
        if (overlay == null)
            SpheneUI.AttachTooltip($"{label}: {detail} — {pct}");
        ImGui.SetCursorScreenPos(barPos);
        SpheneUI.Controls.TransmissionBar(progress, barSize, overlay, isUpload);
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

        drawList.AddRectFilled(cardMin, cardMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                new Vector4(0.02f, 0.02f, 0.04f, 1f), 0.5f)), cornerRadius,
            ImDrawFlags.RoundCornersTop);

        ImGui.SetCursorScreenPos(cardMin);
        var clicked = ImGui.InvisibleButton("##compact-profile", new Vector2(width, cardH));
        var hovered = ImGui.IsItemHovered();
        if (clicked)
        {
            Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Profile));
        }

        // same row backdrop viewers see in their friends list, plus the accent
        var profile = CompactProfile();
        var theme = profile?.ProfileTheme.Value ?? SpheneProfileTheme.Default;
        var accent = theme.AccentColor(SpheneUI.Colors.Accent);
        _profileBarHoverT = Math.Clamp(
            _profileBarHoverT + ImGui.GetIO().DeltaTime * 6f * (hovered ? 1f : -1f), 0f, 1f);
        SpheneUI.Profile.RowBackdrop(drawList, cardMin, cardMax, accent, hovered,
            _profileBarHoverT, theme, GetCompactRowImage(profile), cornerRadius,
            ImDrawFlags.RoundCornersTop);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            drawList.AddRectFilled(cardMin, cardMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.06f)),
                cornerRadius, ImDrawFlags.RoundCornersTop);
        }
        drawList.AddRect(cardMin, cardMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.10f)),
            cornerRadius, ImDrawFlags.RoundCornersTop);

        DrawProfileIdentity(drawList, cardMin, cardH, width, HeaderContentPadX, profile, accent, hovered);

        // move the layout cursor below the card so the card body continues underneath
        ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMax.Y));
    }

    /// <summary>
    /// Shared profile block for the compact header card and the title bar:
    /// round avatar + accent halo + presence dot, display name and status line.
    /// The caller owns the clickable/hover area — <paramref name="hovered"/>
    /// decides whether the open-hub tooltip attaches.
    /// </summary>
    private void DrawProfileIdentity(ImDrawListPtr drawList, Vector2 areaMin, float areaHeight,
        float areaWidth, float padX, SpheneProfileData? profile, Vector4 accent, bool hovered)
    {
        var scale = SpheneUI.Scale;
        var avatarSize = MathF.Min(34f * scale, areaHeight - 10f * scale);
        var avatarPos = new Vector2(areaMin.X + padX, areaMin.Y + (areaHeight - avatarSize) * 0.5f);

        drawList.AddCircleFilled(avatarPos + new Vector2(avatarSize * 0.5f),
            avatarSize * 0.5f + 2f * scale,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.30f)), 0);
        ImGui.SetCursorScreenPos(avatarPos);
        SpheneUI.Media.Avatar(GetCompactAvatarTexture(profile), avatarSize);

        var connected = _apiController.IsConnected;
        var paused = _apiController.IsConnectionPaused;
        var connColor = !connected ? SpheneTheme.Current.CompactServerStatusError
            : paused ? SpheneTheme.Current.CompactServerStatusWarning
            : SpheneTheme.Current.CompactServerStatusConnected;
        var connText = !connected ? "Offline" : paused ? "Paused" : "Online";
        SpheneUI.Media.PresenceDot(drawList, avatarPos, avatarSize, connColor);

        var textX = avatarPos.X + avatarSize + 9f * scale;
        var textMaxW = MathF.Max(30f * scale, areaMin.X + areaWidth - textX - 8f * scale);
        var lineH = ImGui.GetTextLineHeight();
        var lineGap = 2f * scale;
        var nameY = areaMin.Y + (areaHeight - lineH * 2f - lineGap) * 0.5f;

        var playerName = _dalamudUtilService.GetPlayerName();
        var displayName = string.IsNullOrEmpty(playerName)
            || string.Equals(playerName, "--", StringComparison.Ordinal)
            ? _apiController.UID : playerName;
        var (statusLine, statusColor) = ProfileStatusLine(profile);
        var statusText = SpheneUI.Text.Ellipsize(statusLine, textMaxW);
        drawList.AddText(new Vector2(textX, nameY),
            SpheneUI.ColorUtils.ToU32(accent),
            SpheneUI.Text.Ellipsize(displayName, textMaxW));
        drawList.AddText(new Vector2(textX, nameY + lineH + lineGap),
            SpheneUI.ColorUtils.ToU32(statusColor), statusText);

        if (hovered)
        {
            var tip = $"{connText} — UID: {_apiController.UID}";
            if (!string.Equals(statusText, statusLine, StringComparison.Ordinal))
            {
                tip = $"{statusLine}\n{tip}";
            }
            SpheneUI.AttachTooltip(tip + " — click to open the Sphene Hub");
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
        // Folded: the bar IS the whole window — its bottom edge is the window's
        // bottom edge, so the corners round all the way around
        var folded = SpheneUI.Chrome.CollapseAmount(ProfileFoldId) >= 1f;
        var barCorners = folded ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersTop;

        // flush header background covering the window's top edge
        drawList.AddRectFilled(barMin, barMax,
            SpheneUI.ColorUtils.ToU32(SpheneTheme.Current.CompactHeaderBg), rounding,
            barCorners);
        DrawAprilFoolsOverlay(drawList);

        // Buttons first — the item-hover check below must already see them.
        var buttonsLeftX = DrawTitleBarButtons(barMin, barMax);

        // same row backdrop viewers see in their friends list: animated
        // row image fading right plus the owner's accent + effect layers
        var profile = CompactProfile();
        var theme = profile?.ProfileTheme.Value ?? SpheneProfileTheme.Default;
        var accent = theme.AccentColor(SpheneUI.Colors.Accent);

        // Nothing claims presses on the empty bar — ImGui's native window move
        // handles dragging at Begin time (lag-free). We only track the press
        // for click = open hub, gated to presses that start on free bar space.
        var mousePos = ImGui.GetMousePos();
        var inBar = mousePos.X >= barMin.X && mousePos.X < barMax.X
            && mousePos.Y >= barMin.Y && mousePos.Y < barMax.Y;
        var hovered = inBar && ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered();
        _profileBarHoverT = Math.Clamp(
            _profileBarHoverT + ImGui.GetIO().DeltaTime * 6f * (hovered ? 1f : -1f), 0f, 1f);
        SpheneUI.Profile.RowBackdrop(drawList, barMin, barMax, accent, hovered,
            _profileBarHoverT, theme, GetCompactRowImage(profile), rounding, barCorners);

        // Accent edge under the bar: a 3px strip can't carry the window's
        // corner radius (ImGui clamps rounding to half the rect height and
        // the corners poke past the folded silhouette), so clip to the bottom
        // band and stamp the whole rounded bar shape — the visible part
        // follows the corner curve exactly. Expanded = flat strip.
        var accentLineH = 3f * scale;
        drawList.PushClipRect(new Vector2(barMin.X, barMax.Y - accentLineH), barMax, true);
        drawList.AddRectFilled(barMin, barMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            rounding, barCorners);
        drawList.PopClipRect();

        var padX = 12f;
        DrawProfileIdentity(drawList, barMin, barH, buttonsLeftX - barMin.X - 6f * scale,
            padX, profile, accent, hovered);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _titleBarPressed = true;
        }
        if (_titleBarPressed && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            _titleBarPressed = false;
            if (inBar && ImGui.GetIO().MouseDragMaxDistanceSqr[(int)ImGuiMouseButton.Left] < 64f)
            {
                Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Profile));
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(
            windowPos.X + style.WindowPadding.X, barMax.Y + 6f * scale));
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

        // Collapse/expand — folds the window down to just this bar
        var folded = SpheneUI.Chrome.FoldCollapsed(ProfileFoldId);
        ImGui.SetCursorScreenPos(new Vector2(currentX, buttonY));
        if (SpheneUI.Icons.Button(folded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp))
        {
            SpheneUI.Chrome.ToggleFold(ProfileFoldId);
        }
        SpheneUI.AttachTooltip(folded ? "Expand" : "Collapse");

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

        return currentX;
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
        if (profile != null && !string.IsNullOrWhiteSpace(profile.StatusMessage))
        {
            return (profile.StatusMessage, SpheneUI.Colors.TextSecondary);
        }
        if (profile != null && !string.IsNullOrWhiteSpace(profile.Description)
            && !string.Equals(profile.Description, SpheneProfileManager.NoDescriptionPlaceholder,
                StringComparison.Ordinal))
        {
            return (profile.Description, SpheneUI.Colors.TextMuted);
        }
        return ("Set a status in your profile", SpheneUI.Colors.TextMuted);
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
        
        drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(SpheneTheme.Current.CompactControlPanelBg), 10.0f);
        
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
                       (p.Key.GetNote()?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (p.Key.PlayerName?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToDictionary(k => k.Key, k => k.Value);

        string? AlphabeticalSort(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (_configService.Current.ShowCharacterNameInsteadOfNotesForVisible && !string.IsNullOrEmpty(u.Key.PlayerName)
                    ? (_configService.Current.PreferNotesOverNamesForVisible ? u.Key.GetNote() : u.Key.PlayerName)
                    : (u.Key.GetNote() ?? u.Key.UserData.AliasOrUID));
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
            ServerState.OAuthMisconfigured => "OAuth2 is enabled but not fully configured, verify in the Settings -> Network Configuration that you have OAuth2 connected and, importantly, a UID assigned to your current character.",
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

    private void DrawCompactTextureIndicator()
    {
        var (color, _, icon) = GetBc7ConversionIndicator();
        var textureSizeBytes = GetCurrentTextureSize();
        var textureSizeMB = textureSizeBytes / (1024.0 * 1024.0);

        // If automatic mode is enabled and conversion is running, show spinner instead of the icon
        if (_automaticModeEnabled && _shrinkuConversionService.IsConverting)
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

    private void DrawConversionWindow()
    {
        if (!_conversionWindowOpen)
            return;

        using (SpheneUI.Theme.ContextMenuScope())
        {
            // Dynamic window size for better flexibility
            if (ImGui.Begin("Texture Conversion", ref _conversionWindowOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                // Ensure character data analysis is triggered and completed before popup is usable
                if ((_popupAnalysisTask == null || _popupAnalysisTask.IsCompleted) && NeedsPopupAnalysis())
                {
                    _popupAnalysisTask = EnsurePopupAnalysisAsync();
                }

                // If analysis is running or blocking, show busy indicator and avoid interactive content
                if ((_popupAnalysisTask != null && !_popupAnalysisTask.IsCompleted) || _isPopupAnalysisBlocking)
                {
                    SpheneUI.Text.ColoredWrapped("Analyzing character data...", SpheneTheme.Current.CompactServerStatusWarning);
                    if (_characterAnalyzer.IsAnalysisRunning)
                    {
                        ImGui.SameLine();
                        ImGui.Text($"{_characterAnalyzer.CurrentFile}/{_characterAnalyzer.TotalFiles}");
                    }
                    ImGui.End();
                    return;
                }

                // Use live analysis to keep popup in sync with Character Data Analysis UI
                var analysis = _characterAnalyzer.LastAnalysis;
            
            if (analysis == null || !analysis.Any())
            {
                ImGui.TextColored(SpheneTheme.Current.CompactServerStatusError, "No character data available");
                ImGui.Text("Please ensure you have a character loaded and analyzed.");
                
                // Still show backup management even without analysis
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextUnformatted("Backup Management:");
        
        // Backup info block: render inline to simplify layout
        var allCount = _textureBackupService.GetBackupsByOriginalFile().Count;
                // Always show scanning message for up to 10 seconds when there are no backups
                var detectionInProgress = _isTextureBackupScanInProgress || (_shrinkUDetectionTask != null && !_shrinkUDetectionTask.IsCompleted);
                if (allCount == 0 && _backupScanTriggeredAt == DateTime.MinValue)
                    _backupScanTriggeredAt = DateTime.UtcNow;
                var withinForcedWindow = _backupScanTriggeredAt != DateTime.MinValue && (DateTime.UtcNow - _backupScanTriggeredAt) < TimeSpan.FromSeconds(10);
                var scanning = (detectionInProgress || withinForcedWindow);
                // Gate text updates also on scanning state so it can flip between scanning/no-backups
                var newAllKey = $"C:{allCount}-S:{(scanning ? 1 : 0)}";
                if (!string.Equals(_allBackupsKey, newAllKey, StringComparison.Ordinal))
                {
                    _allBackupsKey = newAllKey;
                    if (allCount > 0)
                    {
                        _allBackupsStatusText = $"Found {allCount} total backup file(s). Note: Without character analysis, all backups are shown.";
                        // Reset forced scanning window when backups are available
                        _backupScanTriggeredAt = DateTime.MinValue;
                    }
                    else
                    {
                        _allBackupsStatusText = scanning
                            ? "Scanning for backups... none found yet."
                            : "No texture backups found.";
                    }
                }
                // When scanning and no backups yet, show a rotating spinner before the status text
                if (scanning && allCount == 0)
                {
                    DrawRotatingSpinnerIcon(1.0f);
                    ImGui.SameLine();
                    SpheneUI.Text.ColoredWrapped(_allBackupsStatusText, SpheneTheme.Current.CompactTextSecondary);
                }
                else
                {
                    SpheneUI.Text.ColoredWrapped(_allBackupsStatusText, allCount > 0 ? SpheneTheme.Current.CompactServerStatusWarning : SpheneTheme.Current.CompactServerStatusError);
                }
                if (allCount == 0)
                {
                    // Keep tooltip aligned with scanning window
                    detectionInProgress = _isTextureBackupScanInProgress || (_shrinkUDetectionTask != null && !_shrinkUDetectionTask.IsCompleted);
                    withinForcedWindow = _backupScanTriggeredAt != DateTime.MinValue && (DateTime.UtcNow - _backupScanTriggeredAt) < TimeSpan.FromSeconds(10);
                    scanning = (detectionInProgress || withinForcedWindow);
                    SpheneUI.AttachTooltip(scanning
                        ? "Backups are being scanned. It may be that none exist yet or conversion was not required."
                        : "There are currently no backups. A backup may not exist yet or was never required if the mod was already converted.");
                }
                // Reserve extra space to avoid layout shift on periodic redraws
                ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2));

                if (allCount > 0)
                {
                    using (ImRaii.Disabled(_automaticModeEnabled))
                    {
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Restore from ShrinkU backups"))
                        {
                            StartTextureRestore(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
                        }
                    }
                    SpheneUI.AttachTooltip(_automaticModeEnabled
                        ? "Disable Automatic mode to enable restore."
                        : "Restore textures from ShrinkU backups.");
                    if (_automaticModeEnabled)
                    {
                        ImGui.SameLine();
                        SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, SpheneTheme.Current.CompactServerStatusWarning);
                        ImGui.SameLine();
                        SpheneUI.Text.Colored("Disabled in Automatic mode", SpheneTheme.Current.CompactServerStatusWarning);
                    }
                    ImGui.SameLine();
                    
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
                    {
                        try
                        {
                            var overview = _shrinkuBackupService.GetBackupOverviewAsync().GetAwaiter().GetResult();
                            var firstPath = overview.FirstOrDefault()?.SourcePath;
                            var backupDirectory = string.IsNullOrEmpty(firstPath) ? _textureBackupService.GetBackupDirectory() : Path.GetDirectoryName(firstPath)!;
                            if (!string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory))
                            {
                                System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
                            }
                        }
                        catch
                        {
                            var fallback = _textureBackupService.GetBackupDirectory();
                            if (Directory.Exists(fallback))
                                System.Diagnostics.Process.Start("explorer.exe", fallback);
                        }
                    }
                }
                else
                {
                    detectionInProgress = _isTextureBackupScanInProgress || (_shrinkUDetectionTask != null && !_shrinkUDetectionTask.IsCompleted);
                    withinForcedWindow = _backupScanTriggeredAt != DateTime.MinValue && (DateTime.UtcNow - _backupScanTriggeredAt) < TimeSpan.FromSeconds(10);
                    scanning = (detectionInProgress || withinForcedWindow);
                    var msg = scanning ? "Scanning for backups... none found yet." : "No texture backups found.";
                    if (scanning)
                    {
                        DrawRotatingSpinnerIcon(1.0f);
                        ImGui.SameLine();
                        SpheneUI.Text.ColoredWrapped(msg, SpheneTheme.Current.CompactTextSecondary);
                    }
                    else
                    {
                        SpheneUI.Text.ColoredWrapped(msg, SpheneTheme.Current.CompactTextSecondary);
                    }
                    // Reserve extra space to avoid layout shift on periodic redraws
                    ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2));
                    
                    using (ImRaii.Disabled(true))
                    {
                        SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Restore from ShrinkU backups");
                    }
                    SpheneUI.AttachTooltip(scanning
                        ? "Scanning backups; restore will be enabled automatically when backups are found."
                        : "No texture backups available to restore.");
                    ImGui.SameLine();
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
                    {
                        try
                        {
                            var overview = _shrinkuBackupService.GetBackupOverviewAsync().GetAwaiter().GetResult();
                            var firstPath = overview.FirstOrDefault()?.SourcePath;
                            var backupDirectory = string.IsNullOrEmpty(firstPath) ? _textureBackupService.GetBackupDirectory() : Path.GetDirectoryName(firstPath)!;
                            if (!string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory))
                            {
                                System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
                            }
                        }
                        catch
                        {
                            var fallback = _textureBackupService.GetBackupDirectory();
                            if (Directory.Exists(fallback))
                                System.Diagnostics.Process.Start("explorer.exe", fallback);
                        }
                    }
                }
                
                ImGui.End();
                // Ensure cleanup when window was closed via title bar
                if (!_conversionWindowOpen)
                {
                    _cachedAnalysisForPopup = null;
                }
                return;
            }

            // Get texture data from analysis
            var textureData = GetTextureDataFromAnalysis(analysis);
            var nonBc7Textures = textureData.Where(t => !string.Equals(t.Format, "BC7", StringComparison.Ordinal)).ToList();
            var totalTextures = textureData.Count;
            var bc7Textures = totalTextures - nonBc7Textures.Count;

            // Compute a stable key for current candidates to reset trigger when analysis changes
            // Use primary file paths to avoid repeated triggers across identical states
            var candidateKey = string.Join("|", nonBc7Textures.Select(t => t.FilePaths.FirstOrDefault() ?? string.Empty));
            if (!string.Equals(candidateKey, _autoConvertKey, StringComparison.Ordinal))
            {
                _autoConvertKey = candidateKey;
            }

            var totalSizeBytes = (nonBc7Textures?.Sum(t => t.OriginalSize) ?? 0L);
            var totalSizeMB = totalSizeBytes / (1024.0 * 1024.0);

            // Redesigned Layout using Tables
            if (ImGui.BeginTable("ConversionOverview", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Statistics", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Storage", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, "Statistics");
                ImGui.Text($"Total Textures: {totalTextures}");
                ImGui.Text($"BC7 Optimized: {bc7Textures}");
                ImGui.TextColored(SpheneTheme.Current.CompactServerStatusWarning, $"To Convert: {nonBc7Textures?.Count ?? 0} ({totalSizeMB:F1} MB)");

                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(SpheneTheme.Current.CompactHeaderText, "Storage");
                
                // Ensure storage info task is started or refreshed periodically
                try
                {
                    var now = DateTime.UtcNow;
                    if (_storageInfoTask != null && _storageInfoTask.IsCompleted && _storageInfoTask.Status == TaskStatus.RanToCompletion)
                        _cachedStorageInfo = _storageInfoTask.Result;

                    if (_storageInfoTask == null || (((now - _lastStorageInfoUpdate) > TimeSpan.FromSeconds(5)) && _storageInfoTask.IsCompleted))
                    {
                        _storageInfoTask = GetShrinkUStorageInfoAsync();
                        _lastStorageInfoUpdate = now;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to refresh ShrinkU storage info task");
                }
                var (totalSize, fileCount) = _cachedStorageInfo;
                // Compact storage info line
                if (fileCount > 0)
                {
                    var sizeInMB = totalSize / (1024.0 * 1024.0);
                    ImGui.TextColored(SpheneTheme.Current.CompactServerStatusWarning, $"Storage: {sizeInMB:F2} MB ({fileCount} files)");
                }
                else
                {
                    ImGui.TextColored(SpheneTheme.Current.CompactTextSecondary, "Calculating...");
                }
                ImGui.EndTable();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Backups Section (Redesigned)
            ImGui.TextColored(SpheneUI.Palette.SpheneGold, "Backups");
            var availableBackups = GetCachedBackupsForCurrentAnalysis();
            // Compute counts and gate status text updates on changes in number/type
            var textureBackupCount = availableBackups.Count(kvp => !kvp.Key.StartsWith("[ShrinkU Mod:", StringComparison.Ordinal));
            var shrinkuModBackupCount = availableBackups.Count(kvp => kvp.Key.StartsWith("[ShrinkU Mod:", StringComparison.Ordinal));

            // Determine scanning state for current-textures block with 10s forced window
            var detectionInProgressCurrent = _isTextureBackupScanInProgress
                || (_textureDetectionTask?.Status == TaskStatus.Running)
                || (_shrinkUDetectionTask?.Status == TaskStatus.Running);
            if ((textureBackupCount + shrinkuModBackupCount) == 0 && _backupScanTriggeredAt == DateTime.MinValue)
            {
                // No backups yet and no timestamp set: start forced 10s scanning window
                _backupScanTriggeredAt = DateTime.UtcNow;
            }
            var withinForcedWindowCurrent = _backupScanTriggeredAt != DateTime.MinValue
                && (DateTime.UtcNow - _backupScanTriggeredAt).TotalSeconds < 10.0;
            var scanningCurrent = detectionInProgressCurrent || withinForcedWindowCurrent;

            var newCurrentKey = $"T{textureBackupCount}|S{shrinkuModBackupCount}|C{(scanningCurrent ? 1 : 0)}";
            if (!string.Equals(_currentBackupsKey, newCurrentKey, StringComparison.Ordinal))
            {
                _currentBackupsKey = newCurrentKey;
                if (textureBackupCount > 0 && shrinkuModBackupCount > 0)
                {
                    _currentBackupsStatusText = $"Found {textureBackupCount} texture backup(s) and {shrinkuModBackupCount} ShrinkU mod backup(s) for current textures.";
                    // Backups found: clear forced window to avoid stale scanning state
                    _backupScanTriggeredAt = DateTime.MinValue;
                }
                else if (textureBackupCount > 0)
                {
                    _currentBackupsStatusText = $"Found {textureBackupCount} texture backup(s) for current textures.";
                    _backupScanTriggeredAt = DateTime.MinValue;
                }
                else if (shrinkuModBackupCount > 0)
                {
                    _currentBackupsStatusText = $"Found {shrinkuModBackupCount} ShrinkU mod backup(s) for current textures.";
                    _backupScanTriggeredAt = DateTime.MinValue;
                }
                else
                {
                    _currentBackupsStatusText = scanningCurrent
                        ? "Scanning for backups... none found yet."
                        : "No backups found for current textures.";
                }
            }
            // When scanning and no backups yet, show a rotating spinner before the status line
            if (scanningCurrent && (textureBackupCount + shrinkuModBackupCount == 0))
            {
                DrawRotatingSpinnerIcon(1.0f);
                ImGui.SameLine();
                SpheneUI.Text.Colored(_currentBackupsStatusText, SpheneTheme.Current.CompactTextSecondary);
            }
            else
            {
                SpheneUI.Text.Colored(_currentBackupsStatusText,
                    (textureBackupCount > 0 || shrinkuModBackupCount > 0)
                        ? SpheneUI.Palette.Success
                        : SpheneTheme.Current.CompactTextSecondary);
            }

            if (textureBackupCount > 0 || shrinkuModBackupCount > 0)
            {
                using (ImRaii.Disabled(_automaticModeEnabled))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Restore"))
                    {
                        StartTextureRestore(availableBackups);
                    }
                }
                SpheneUI.AttachTooltip(_automaticModeEnabled
                    ? "Disable Automatic mode to enable restore."
                    : "Restore current textures from backups created earlier.");
                if (_automaticModeEnabled)
                {
                    ImGui.SameLine();
                    SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, SpheneUI.Palette.Warning);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Disabled in Automatic mode", SpheneUI.Palette.Warning);
                }
                
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
                {
                    try
                    {
                        var overview = _shrinkuBackupService.GetBackupOverviewAsync().GetAwaiter().GetResult();
                        var firstPath = overview.FirstOrDefault()?.SourcePath;
                        var backupDirectory = string.IsNullOrEmpty(firstPath) ? _textureBackupService.GetBackupDirectory() : Path.GetDirectoryName(firstPath)!;
                        if (!string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
                        }
                    }
                    catch
                    {
                        var fallback = _textureBackupService.GetBackupDirectory();
                        if (Directory.Exists(fallback))
                            System.Diagnostics.Process.Start("explorer.exe", fallback);
                    }
                }
            }
            else
            {
                using (ImRaii.Disabled(true))
                {
                    SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Restore");
                }
                SpheneUI.AttachTooltip("No texture backups available to restore.");
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
                {
                    try
                    {
                        var overview = _shrinkuBackupService.GetBackupOverviewAsync().GetAwaiter().GetResult();
                        var firstPath = overview.FirstOrDefault()?.SourcePath;
                        var backupDirectory = string.IsNullOrEmpty(firstPath) ? _textureBackupService.GetBackupDirectory() : Path.GetDirectoryName(firstPath)!;
                        if (!string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
                        }
                    }
                    catch
                    {
                        var fallback = _textureBackupService.GetBackupDirectory();
                        if (Directory.Exists(fallback))
                            System.Diagnostics.Process.Start("explorer.exe", fallback);
                    }
                }
            }
            
            
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Conversion Section
            ImGui.TextColored(SpheneUI.Palette.SpheneGold, "Conversion Options");
            // Sync checkbox state with ShrinkU config to reflect external changes
            try
            {
                var isAutomaticBySphene = _shrinkuConfigService.Current.TextureProcessingMode == TextureProcessingMode.Automatic
                    && _shrinkuConfigService.Current.AutomaticHandledBySphene;
                if (_automaticModeEnabled != isAutomaticBySphene)
                    _automaticModeEnabled = isAutomaticBySphene;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to sync automatic mode state with ShrinkU config");
            }
            if (ImGui.Checkbox("Automatic mode", ref _automaticModeEnabled))
            {
                _logger.LogDebug("Automatic mode toggled: {Enabled}", _automaticModeEnabled);
                try
                {
                    var newMode = _automaticModeEnabled ? TextureProcessingMode.Automatic : TextureProcessingMode.Manual;
                    if (_shrinkuConfigService.Current.TextureProcessingMode != newMode)
                    {
                        _shrinkuConfigService.Current.TextureProcessingMode = newMode;
                        // Mark that Sphene orchestrates automatic conversion when enabling Automatic mode
                        _shrinkuConfigService.Current.AutomaticHandledBySphene = _automaticModeEnabled;
                        _shrinkuConfigService.Save();
                        _logger.LogDebug("Propagated automatic mode to ShrinkU: {Mode}", newMode);
                        try { _shrinkuConversionService.OnProcessingModeChanged(newMode); } catch (Exception ex) { _logger.LogDebug(ex, "Notify processing mode changed failed"); }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to propagate automatic mode to ShrinkU");
                }

                // Immediately start background conversion when enabling Automatic mode, but only for analyzed textures
                try
                {
                    if (_automaticModeEnabled && !_shrinkuConversionService.IsConverting && nonBc7Textures != null && nonBc7Textures.Count > 0)
                    {
                        _autoSilentConversion = true; // suppress progress window for automatic background conversions
                        _ = StartTextureConversion(nonBc7Textures!);
                        _conversionWindowOpen = false;
                        _cachedAnalysisForPopup = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to start automatic conversion on mode enable");
                }
            }
            SpheneUI.AttachTooltip(_automaticModeEnabled
                ? "Automatic conversion is enabled. Revert actions are disabled."
                : "Enable automatic on-the-fly conversion of non-BC7 textures.");

            try
            {
                var fullModBackup = _shrinkuConfigService.Current.EnableFullModBackupBeforeConversion;
                ImGui.TextUnformatted("Backups:");
                ImGui.SameLine();
                if (ImGui.Checkbox("Create full mod PMP before conversion", ref fullModBackup))
                {
                    _shrinkuConfigService.Current.EnableFullModBackupBeforeConversion = fullModBackup;
                    _shrinkuConfigService.Current.EnableBackupBeforeConversion = false;
                    _shrinkuConfigService.Save();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to render backup UI");
            }
            
            if ((nonBc7Textures?.Count ?? 0) > 0)
            {
                try
                {
                    var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var root = _ipcManager.Penumbra.ModDirectory ?? string.Empty;
                    if (nonBc7Textures != null)
                    {
                        foreach (var texture in nonBc7Textures)
                        {
                            var paths = texture.FilePaths ?? new List<string>();
                            if (paths.Count == 0) continue;
                            var primary = paths[0] ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(primary)) continue;
                            var rel = !string.IsNullOrWhiteSpace(root) ? Path.GetRelativePath(root, primary) : primary;
                            rel = rel.Replace('/', '\\');
                            var idx = rel.IndexOf('\\');
                            var mod = idx >= 0 ? rel.Substring(0, idx) : rel;
                            if (!string.IsNullOrWhiteSpace(mod)) mods.Add(mod);
                        }
                    }
                    var excludedCount = 0;
                    foreach (var m in mods)
                    {
                        try
                        {
                            if (_shrinkuConfigService.Current.ExcludedMods != null && _shrinkuConfigService.Current.ExcludedMods.Contains(m))
                                excludedCount++;
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "Excluded mods count check failed"); }
                    }
                    if (mods.Count > 0)
                    {
                        ImGui.TextColored(SpheneUI.Palette.SpheneGold, $"Affected Mods (Excluded: {excludedCount})");
                        // Use a child window to make the list scrollable
                        var requiredHeight = Math.Max(50, mods.Count * ImGui.GetTextLineHeightWithSpacing() + 10);
                        var childHeight = Math.Min(requiredHeight, 300.0f);
                        
                        // Use fixed width to prevent auto-resize animation issues
                        ImGui.Separator();
                        if (ImGui.BeginChild("AffectedModsList", new Vector2(300, childHeight), false))
                        {
                            foreach (var m in mods)
                            {
                                bool isExcluded = false;
                                try { isExcluded = _shrinkuConfigService.Current.ExcludedMods != null && _shrinkuConfigService.Current.ExcludedMods.Contains(m); } catch (Exception ex) { _logger.LogDebug(ex, "Excluded state eval failed for {mod}", m); }
                                ImGui.Checkbox($"Exclude {m}", ref isExcluded);
                                if (ImGui.IsItemDeactivatedAfterEdit())
                                {
                                    try
                                    {
                                        _shrinkuConfigService.Current.ExcludedMods ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                        if (isExcluded) _shrinkuConfigService.Current.ExcludedMods.Add(m); else _shrinkuConfigService.Current.ExcludedMods.Remove(m);
                                        _shrinkuConfigService.Save();
                                    }
                                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to toggle exclude for {mod}", m); }
                                }
                            }
                            }
                        ImGui.EndChild();
                        ImGui.Separator();
                        ImGui.Spacing();
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed computing affected mods"); }

                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileArchive, $"Convert {(nonBc7Textures?.Count ?? 0)} to BC7"))
                {
                    _ = StartTextureConversion(nonBc7Textures!);
                    _conversionWindowOpen = false;
                    _cachedAnalysisForPopup = null; // Clear cached analysis when window closes
                    _autoSilentConversion = false; // show progress for manual conversion
                    _conversionProgressWindowOpen = true;
                }

                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ExternalLinkAlt, "Open ShrinkU"))
                {
                    try
                    {
                        // Enable Penumbra Used Only filter and open ShrinkU
                        _shrinkuConfigService.Current.FilterPenumbraUsedOnly = true;
                        _shrinkuConfigService.Save();
                        _shrinkuHostService.OpenConversion();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to open ShrinkU");
                    }
                }

                // Visual indicator: show spinning arrows when automatic conversion is active
                try
                {
                    if (_automaticModeEnabled && _shrinkuConversionService.IsConverting)
                    {
                        ImGui.SameLine();
                        DrawRotatingSpinnerIcon();
                        SpheneUI.AttachTooltip("Automatic conversion is running");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to update automatic conversion spinner");
                }


                // No automatic conversion trigger on popup open when Automatic mode is enabled
            }
            else
            {
                ImGui.TextColored(SpheneUI.Palette.Success, "All textures are already optimized!");
            }

            }
            ImGui.End();

            if (!_conversionWindowOpen)
            {
                _cachedAnalysisForPopup = null;
            }
        }
    }

    private void AppendBackupStep(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath)) return;
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
            string msg;
            if (string.Equals(ext, ".zip", StringComparison.Ordinal))
            {
                msg = $"Created mod backup ZIP {filePath}";
                _backupZipCount++;
            }
            else if (string.Equals(ext, ".pmp", StringComparison.Ordinal))
            {
                msg = $"Created full mod backup PMP {filePath}";
                _backupPmpCount++;
            }
            else
            {
                msg = $"Backed up texture {filePath}";
                _backupTextureCount++;
            }

            var stamped = $"{DateTime.UtcNow:HH:mm:ss.fff} | {msg}";

            // De-duplicate consecutive identical messages
            if (_backupStepLog.Count == 0 || !string.Equals(_backupStepLog[^1], stamped, StringComparison.Ordinal))
                _backupStepLog.Add(stamped);
            // Keep log size reasonable
            if (_backupStepLog.Count > 200)
                _backupStepLog.RemoveRange(0, _backupStepLog.Count - 200);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update backup step log");
        }
    }

    private void DrawConversionProgressWindow()
    {
        var converting = _conversionTask != null && !_conversionTask.IsCompleted;
        // Suppress entirely while ShrinkU ConversionUI is open
        if ((_shrinkuHostService?.IsConversionUiOpen ?? false))
            return;
        if (!converting && !_conversionProgressWindowOpen)
            return;

        if (converting && !_autoSilentConversion && !_automaticModeEnabled && !(_shrinkuHostService?.IsConversionUiOpen ?? false))
            _conversionProgressWindowOpen = true;

        using (SpheneUI.Theme.ContextMenuScope())
        {
            ImGui.SetNextWindowSize(new Vector2(350, 0), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(350, 0), new Vector2(350, float.MaxValue));
            if (ImGui.Begin("Conversion Progress", ref _conversionProgressWindowOpen, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
            {
                // Title and batch mod progress
                ImGui.TextColored(new Vector4(0.40f, 0.85f, 0.40f, 1f), "Converting Mods");
                ImGui.Separator();
                
                var doneCount = Math.Max(0, _currentModIndex - 1);
                float currentModFrac = (_currentModTotalFiles > 0) ? Math.Min(_conversionCurrentFileProgress, _currentModTotalFiles) / (float)_currentModTotalFiles : 0f;
                float modsPercent = _totalMods > 0 ? (doneCount + currentModFrac) / _totalMods : 0f;
                var doneDisplay = Math.Max(0, _currentModIndex);
                
                // 1. Batch Progress
                if (_totalMods > 0)
                {
                    ImGui.Text($"Overall Progress: {doneDisplay} of {_totalMods} Mods");
                    ImGui.ProgressBar(modsPercent, new Vector2(-1, 0), $"{modsPercent:P0}");
                    
                    var elapsed = (_conversionStartTime == DateTime.MinValue) ? TimeSpan.Zero : (DateTime.UtcNow - _conversionStartTime);
                    var etaSec = 0.0;
                    if (_currentModIndex > 0)
                    {
                        var rate = _currentModIndex / Math.Max(1.0, elapsed.TotalSeconds);
                         etaSec = (_totalMods - _currentModIndex) / rate;
                    }
                    
                    var m = (int)etaSec / 60;
                    var s = (int)etaSec % 60;
                    
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Backups created: {_backupZipCount + _backupPmpCount + _backupTextureCount} • Elapsed: {elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2} • ETA: {m}:{s:D2}");
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                // 2. Current Mod / File Progress
                if (!string.IsNullOrEmpty(_currentModName))
                    ImGui.Text($"Current Mod: {_currentModName}");

                // Backup progress section (shows while backups run or if completed with steps)
                var backupActive = _backupTotalCount > 0 && _backupCurrentIndex < _backupTotalCount;
                var backupCompleted = _backupTotalCount > 0 && _backupCurrentIndex >= _backupTotalCount;
                if (backupActive || (_backupStepLog.Count > 0 && (converting || backupCompleted)))
                {
                    ImGui.TextColored(new Vector4(0.40f, 0.85f, 0.40f, 1f), "Backing up Mods");
                    ImGui.Separator();

                    bool expectPmp = false;
                    try { expectPmp = _shrinkuConfigService.Current.EnableFullModBackupBeforeConversion; }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to read ShrinkU full mod backup setting"); }
                    int expectedFromConfig = (expectPmp ? 1 : 0);

                    // Prefer service-provided total when available; fallback to config-based estimate
                    int effectiveExpected = _backupTotalCount > 0 ? _backupTotalCount : expectedFromConfig;

                    // Prefer service-provided index when available; otherwise use aggregated counts
                    int aggregatedCompleted = Math.Max(0, _backupTextureCount) + Math.Max(0, _backupZipCount) + Math.Max(0, _backupPmpCount);
                    int effectiveCompleted = _backupTotalCount > 0 ? Math.Min(_backupCurrentIndex, effectiveExpected) : aggregatedCompleted;

                    // If service signals completion, force 100%
                    if (_backupTotalCount > 0 && _backupCurrentIndex >= _backupTotalCount)
                        effectiveCompleted = effectiveExpected;

                    float stepPercent = effectiveExpected > 0 ? Math.Clamp((float)effectiveCompleted / effectiveExpected, 0f, 1f) : 0f;

                    if (!string.IsNullOrEmpty(_backupCurrentFileName))
                         ImGui.Text($"Current Backup: {_backupCurrentFileName}");
                    
                    ImGui.ProgressBar(stepPercent, new Vector2(-1, 0), $"{stepPercent:P0}");
                }











                

                if (_currentModTotalFiles > 0)
                {
                     var current = Math.Min(_conversionCurrentFileProgress, _currentModTotalFiles);
                     float perMod = (float)current / _currentModTotalFiles;
                     ImGui.ProgressBar(perMod, new Vector2(-1, 0), $"{current}/{_currentModTotalFiles}");
                     
                     var modElapsed = (_currentModStartedAt == DateTime.MinValue) ? TimeSpan.Zero : (DateTime.UtcNow - _currentModStartedAt);
                     ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Mod Elapsed: {modElapsed.Hours:D2}:{modElapsed.Minutes:D2}:{modElapsed.Seconds:D2}");
                }
                else 
                {
                     ImGui.ProgressBar(0f, new Vector2(-1, 0), "Preparing...");
                }

                ImGui.Spacing();

                ImGui.Spacing();
                ImGui.Separator();

                if (converting)
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.StopCircle, "Cancel Conversion"))
                    {
                        try { _shrinkuConversionService.Cancel(); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel ShrinkU conversion"); }
                    }
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Conversion is running...", SpheneUI.Palette.Warning);
                }
                else
                {
                    SpheneUI.Text.Colored("Conversion completed.", SpheneUI.Palette.Success);
                }

                ImGui.End();
            }
        }

        // Auto-close and cleanup once done (also handles cases where the task was nulled by lifecycle)
        if (_conversionTask == null || _conversionTask.IsCompleted)
        {
            _conversionProgressWindowOpen = false;
            if (_texturesToConvert.Count > 0)
            {
                _texturesToConvert.Clear();
            }
            _conversionTask = null;
            // Reset backup progress state
            _backupCurrentFileName = string.Empty;
            _backupCurrentIndex = 0;
            _backupTotalCount = 0;
            _backupStartTime = DateTime.MinValue;
            _backupStepLog.Clear();
            _backupTextureCount = 0;
            _backupZipCount = 0;
            _backupPmpCount = 0;
        }
    }

    private void DrawRestoreProgressWindow()
    {
        var restoreRunning = _restoreTask != null && !_restoreTask.IsCompleted;
        if (!restoreRunning && !_restoreWindowOpen)
            return;

        // Auto-open when restore starts
        if (restoreRunning)
            _restoreWindowOpen = true;

        if (_restoreWindowOpen)
        {
            using (SpheneUI.Theme.ContextMenuScope())
            {
                ImGui.SetNextWindowSize(new Vector2(350, 0), ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowSizeConstraints(new Vector2(350, 0), new Vector2(350, float.MaxValue));
                if (ImGui.Begin("Restore Progress", ref _restoreWindowOpen, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextColored(new Vector4(0.40f, 0.85f, 0.40f, 1f), "Restoring Mods");
                    ImGui.Separator();

                    var batchTotal = _restoreModsTotal;
                    var batchDone = _restoreModsDone;
                    var batchPercent = batchTotal > 0 ? (float)batchDone / batchTotal : 0f;
                    ImGui.Text($"Overall Progress: {batchDone} of {batchTotal} Mods");
                    ImGui.ProgressBar(batchPercent, new Vector2(-1, 0), $"{batchPercent:P0}");
                    if (_restoreStartTime != DateTime.MinValue && batchDone >= 0)
                    {
                        var elapsed = DateTime.UtcNow - _restoreStartTime;
                        var rate = batchDone / Math.Max(1.0, elapsed.TotalSeconds);
                        var secsRemaining = rate > 0 ? (batchTotal - batchDone) / rate : 0.0;
                        var eta = TimeSpan.FromSeconds(secsRemaining);
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Elapsed: {elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2} • ETA: {eta.Minutes}:{eta.Seconds:D2}");
                    }

                    ImGui.Spacing();
                    if (!string.IsNullOrEmpty(_currentRestoreMod))
                        ImGui.Text($"Current Mod: {_currentRestoreMod}");

                    var fileTotal = _restoreTotalCount;
                    var fileCurrent = _restoreCurrentIndex;
                    var filePercent = fileTotal > 0 ? (float)fileCurrent / fileTotal : 0f;
                    ImGui.ProgressBar(filePercent, new Vector2(-1, 0), $"{fileCurrent}/{fileTotal}");

                    if (!string.IsNullOrEmpty(_restoreStepText))
                    {
                        ImGui.Spacing();
                        SpheneUI.Text.Colored(_restoreStepText, SpheneUI.Palette.TextSecondary);
                    }

                    ImGui.Spacing();
                    if (restoreRunning)
                    {
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.StopCircle, "Cancel Restore"))
                        {
                            try { _restoreCancellationTokenSource.Cancel(); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel restore operation"); }
                        }
                        ImGui.SameLine();
                        SpheneUI.Text.Colored("Restore is running...", SpheneUI.Palette.Warning);
                    }
                    else
                    {
                        SpheneUI.Text.Colored("Restore completed.", SpheneUI.Palette.Success);
                    }

                    ImGui.End();
                }
            }
        }
    }

    private static List<(string Format, long OriginalSize, List<string> FilePaths)> GetTextureDataFromAnalysis(Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> analysis)
    {
        var textureData = new List<(string Format, long OriginalSize, List<string> FilePaths)>();
        var totalFiles = 0;
        var textureFiles = 0;

        foreach (var objectKindData in analysis.Values)
        {
            foreach (var fileData in objectKindData.Values)
            {
                totalFiles++;
                if (fileData.FilePaths != null && fileData.FilePaths.Count > 0 && fileData.Format != null && !string.IsNullOrEmpty(fileData.Format.Value))
                {
                    textureFiles++;
                    textureData.Add((fileData.Format.Value, fileData.OriginalSize, fileData.FilePaths));
                }
            }
        }
        return textureData;
    }

    // Draw a small spinning arrows indicator to visualize auto conversion
    

    // Draw a rotating spinner that visually replaces the FontAwesome spinner glyph
    // The spinner itself rotates, without any additional overlay around it
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
   private Task StartTextureConversion(List<(string Format, long OriginalSize, List<string> FilePaths)> textureData)
    {
        _texturesToConvert.Clear();

        // Prepare conversion dictionary
        foreach (var texture in textureData)
        {
            if (texture.FilePaths.Count > 0)
            {
                var primaryPath = texture.FilePaths[0];
                var duplicatePaths = texture.FilePaths.Skip(1).ToArray();
                _texturesToConvert[primaryPath] = duplicatePaths;
            }
        }
        _logger.LogDebug("Starting texture conversion for {Count} textures", _texturesToConvert.Count);

        _conversionStartTime = DateTime.UtcNow;
        _conversionCancellationTokenSource = new CancellationTokenSource();

        _conversionTask = Task.Run(async () =>
        {
            var dict = await BuildFullModConversionDictionaryFromSelection().ConfigureAwait(false);
            await _shrinkuConversionService.StartConversionAsync(dict).ConfigureAwait(false);
        });
        _ = _conversionTask.ContinueWith(t => { _autoSilentConversion = false; }, TaskScheduler.Default);
        return Task.CompletedTask;
    }

        
    
    private Dictionary<string, List<string>> GetCachedBackupsForCurrentAnalysis()
    {
        // Cache for 5 seconds to avoid excessive recalculation
        if (_cachedBackupsForAnalysis != null && 
            DateTime.UtcNow - _lastBackupAnalysisUpdate < TimeSpan.FromSeconds(5))
        {
            return _cachedBackupsForAnalysis;
        }
        // Start texture backup detection in background if stale/not started
        var shouldStartTextureDetection = _textureDetectionTask == null
            || (_textureDetectionTask.IsCompleted && DateTime.UtcNow - _lastTextureDetectionUpdate > TimeSpan.FromSeconds(5));
        if (shouldStartTextureDetection)
        {
            try
            {
                _isTextureBackupScanInProgress = true;
                _textureDetectionTask = Task.Run(() => GetBackupsForCurrentAnalysis());
            }
            catch (Exception ex) { _isTextureBackupScanInProgress = false; _logger.LogDebug(ex, "Failed to prewarm backup detection caches (restore)"); }
        }
        // If texture detection finished, update cache
        if (_textureDetectionTask != null && _textureDetectionTask.IsCompleted)
        {
            try
            {
                _cachedTextureBackupsFiltered = _textureDetectionTask.Result;
            }
            catch (Exception ex) { _cachedTextureBackupsFiltered = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); _logger.LogDebug(ex, "Failed to cache texture backups (restore)"); }
            finally
            {
                _lastTextureDetectionUpdate = DateTime.UtcNow;
                _isTextureBackupScanInProgress = false;
            }
        }

        // Kick off ShrinkU mod backup detection asynchronously if stale or not started
        var shouldStartDetection = _shrinkUDetectionTask == null
            || (_shrinkUDetectionTask.IsCompleted && DateTime.UtcNow - _lastShrinkUDetectionUpdate > TimeSpan.FromSeconds(5));
        if (shouldStartDetection)
        {
            try
            {
                _shrinkUDetectionTask = DetectShrinkUModBackupsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to start ShrinkU mod backup detection task");
            }
        }

        // Combine cached texture service backups with any cached ShrinkU mod backups
        var combined = _cachedTextureBackupsFiltered != null 
            ? new Dictionary<string, List<string>>(_cachedTextureBackupsFiltered, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_cachedShrinkUModBackups != null && _cachedShrinkUModBackups.Count > 0)
        {
            foreach (var kvp in _cachedShrinkUModBackups)
            {
                combined[kvp.Key] = kvp.Value;
            }
        }

        _cachedBackupsForAnalysis = combined;
        _lastBackupAnalysisUpdate = DateTime.UtcNow;
        return _cachedBackupsForAnalysis;
    }

    private Dictionary<string, List<string>> GetBackupsForCurrentAnalysis()
    {
        try
        {
            _isTextureBackupScanInProgress = true;
            // Use cached analysis if available (for popup), otherwise use current analysis
            var analysis = _cachedAnalysisForPopup ?? _characterAnalyzer.LastAnalysis;
            if (analysis == null || !analysis.Any()) 
            {
                _logger.LogDebug("No character analysis available for backup filtering (cached: {cached}, current: {current})", 
                    _cachedAnalysisForPopup != null, _characterAnalyzer.LastAnalysis != null);
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            
            // Get all available backups from texture backup service (fast path)
            var allBackups = _textureBackupService.GetBackupsByOriginalFile();
            _logger.LogDebug("Found {count} total texture backup service files", allBackups.Count);
            
            var filteredBackups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            
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
            
            // Filter texture backup service backups to only include those for currently loaded textures
            foreach (var backup in allBackups)
            {
                if (currentTextureNames.Contains(backup.Key))
                {
                    filteredBackups[backup.Key] = backup.Value;
                    _logger.LogDebug("Found matching texture backup service backup for texture: {fileName} with {backupCount} backup files", backup.Key, backup.Value.Count);
                }
            }
            
            _logger.LogDebug("Filtered texture backup detection result: {count} entries have backups", filteredBackups.Count);
            return filteredBackups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get backups for current analysis");
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _isTextureBackupScanInProgress = false;
        }
    }

    private async Task DetectShrinkUModBackupsAsync()
    {
        if (_isDisposed)
            return;
        var lockTaken = false;
        try
        {
            lockTaken = await _shrinkUDetectionSemaphore.WaitAsync(0).ConfigureAwait(false);
            if (!lockTaken || _isDisposed)
                return;
            var usedPaths = await _shrinkuConversionService.GetUsedModTexturePathsAsync().ConfigureAwait(false);
            var penumbraRoot = _ipcManager.Penumbra.ModDirectory ?? string.Empty;
            penumbraRoot = penumbraRoot.Replace('/', '\\');
            if (!string.IsNullOrWhiteSpace(penumbraRoot) && !penumbraRoot.EndsWith('\\'))
                penumbraRoot += "\\";

            var owningMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in usedPaths)
            {
                try
                {
                    var full = p.Replace('/', '\\');
                    if (!string.IsNullOrWhiteSpace(penumbraRoot) && full.StartsWith(penumbraRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var remainder = full.Substring(penumbraRoot.Length);
                        var idx = remainder.IndexOf('\\');
                        var modFolder = idx > 0 ? remainder.Substring(0, idx) : remainder;
                        if (!string.IsNullOrWhiteSpace(modFolder))
                            owningMods.Add(modFolder);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to analyze used texture path for owning mod");
                }
            }

            _logger.LogDebug("Detected {count} owning mods from used texture paths: {mods}", owningMods.Count, string.Join(", ", owningMods));

            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int shrinkuBackupCount = 0;
            foreach (var mod in owningMods)
            {
                try
                {
                    bool hasBackup = await _shrinkuBackupService.HasBackupForModAsync(mod).ConfigureAwait(false);
                    if (hasBackup)
                    {
                        var syntheticKey = $"[ShrinkU Mod: {mod}]";
                        result[syntheticKey] = new List<string> { "ShrinkU mod backup available" };
                        shrinkuBackupCount++;
                        _logger.LogDebug("Found ShrinkU mod backup for: {mod}", mod);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to check ShrinkU backup availability for mod: {mod}", mod);
                }
            }

            _cachedShrinkUModBackups = result;
            _lastShrinkUDetectionUpdate = DateTime.UtcNow;
            _logger.LogDebug("ShrinkU backup detection completed: {count} mods with available backups", shrinkuBackupCount);
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "ShrinkU backup detection skipped because CompactUI was disposed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect ShrinkU mod backups asynchronously");
        }
        finally
        {
            if (lockTaken)
            {
                try { _shrinkUDetectionSemaphore.Release(); }
                catch (ObjectDisposedException ex) { _logger.LogDebug(ex, "ShrinkU backup detection semaphore already disposed on release"); }
            }
        }
    }
    
    private bool NeedsPopupAnalysis()
    {
        try
        {
            var analysis = _characterAnalyzer.LastAnalysis;
            if (analysis == null || analysis.Count == 0)
            {
                _logger.LogDebug("Popup analysis required: no analysis available");
                return true;
            }

            // Require compute if any entries are not computed
            bool needsCompute = analysis.Any(group => group.Value.Any(f => !f.Value.IsComputed));
            if (needsCompute)
            {
                _logger.LogDebug("Popup analysis required: entries not computed");
                return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private async Task EnsurePopupAnalysisAsync()
    {
        _isPopupAnalysisBlocking = true;
        try
        {
            // If analysis is already running, wait for completion
            if (_characterAnalyzer.IsAnalysisRunning)
            {
                _logger.LogDebug("Waiting for running analysis to complete for popup");
                // Poll until analysis completes
                while (_characterAnalyzer.IsAnalysisRunning)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            // If analysis missing or has uncomputed entries, trigger compute and await
            var analysis = _characterAnalyzer.LastAnalysis;
            bool needsCompute = analysis == null || analysis.Count == 0 || analysis.Any(group => group.Value.Any(f => !f.Value.IsComputed));
            if (needsCompute)
            {
                _logger.LogDebug("Triggering character analysis for popup");
                await _characterAnalyzer.ComputeAnalysis(print: false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed during popup analysis gating");
        }
        finally
        {
            _isPopupAnalysisBlocking = false;
        }
    }
    
    private void StartTextureRestore(Dictionary<string, List<string>> availableBackups)
    {
        _restoreCancellationTokenSource = _restoreCancellationTokenSource.CancelRecreate();
        _restoreStartTime = DateTime.UtcNow;
        _restoreCurrentIndex = 0;
        _restoreTotalCount = 0;
        _restoreWindowOpen = true;
        _restoreStepText = "Preparing restore (detecting owning mods and backups)";
        _isRestoreInProgress = true;
        
        _restoreTask = Task.Run(async () =>
        {
            try
            {
                // First try targeted mod restoration using ShrinkU services and Penumbra paths
                try
                {
                    var usedPaths = await _shrinkuConversionService.GetUsedModTexturePathsAsync().ConfigureAwait(false);
                    var penumbraRoot = _ipcManager.Penumbra.ModDirectory ?? string.Empty;
                    penumbraRoot = penumbraRoot.Replace('/', '\\');
                    if (!string.IsNullOrWhiteSpace(penumbraRoot) && !penumbraRoot.EndsWith('\\'))
                        penumbraRoot += "\\";

                    var owningMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in usedPaths)
                    {
                        try
                        {
                            var full = p.Replace('/', '\\');
                            if (!string.IsNullOrWhiteSpace(penumbraRoot) && full.StartsWith(penumbraRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                var remainder = full.Substring(penumbraRoot.Length);
                                var idx = remainder.IndexOf('\\');
                                var modFolder = idx > 0 ? remainder.Substring(0, idx) : remainder;
                                if (!string.IsNullOrWhiteSpace(modFolder))
                                    owningMods.Add(modFolder);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to analyze texture path for session matching");
                        }
                    }

                    _logger.LogInformation("Penumbra mod root: {root}", penumbraRoot);
                    _logger.LogInformation("Detected {count} owning mods for used textures: {mods}", owningMods.Count, string.Join(", ", owningMods));

                    // Try restore latest backup per owning mod, preferring those with available backups
                    bool anyTargetedRestoreSucceeded = false;
                    var modsWithBackup = new List<string>();
                    foreach (var mod in owningMods)
                    {
                        try { if (await _shrinkuBackupService.HasBackupForModAsync(mod).ConfigureAwait(false)) modsWithBackup.Add(mod); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Backup availability check failed for {mod}", mod); }
                    }
                    _restoreModsTotal = modsWithBackup.Count;
                    _restoreModsDone = 0;
                    int restoredModCount = 0;
                    foreach (var mod in modsWithBackup)
                    {
                        _currentRestoreMod = mod;
                        if (_restoreStartTime == DateTime.MinValue)
                            _restoreStartTime = DateTime.UtcNow;

                        _logger.LogInformation("Attempting targeted restore for mod {mod} ({current}/{total})", mod, restoredModCount + 1, owningMods.Count(m => { try { return _shrinkuBackupService.HasBackupForModAsync(m).Result; } catch { return false; } }));

                        bool ok = false;
                        try
                        {
                            var pmpList = await _shrinkuBackupService.GetPmpBackupsForModAsync(mod).ConfigureAwait(false);
                            var latestPmp = pmpList?.FirstOrDefault();
                            if (!string.IsNullOrEmpty(latestPmp))
                            {
                                _restoreStepText = $"Restoring PMP archive for mod '{mod}'";
                                _logger.LogDebug("Restoring PMP for mod {mod}: {pmp}", mod, latestPmp);
                                ok = await _shrinkuBackupService.RestorePmpAsync(mod, latestPmp, _restoreProgress, _restoreCancellationTokenSource.Token).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "PMP restore attempt failed; falling back to latest normal restore for {mod}", mod);
                        }

                        if (!ok)
                        {
                            _restoreStepText = $"Restoring latest backup for mod '{mod}'";
                            ok = await _shrinkuBackupService.RestoreLatestForModAsync(mod, _restoreProgress, _restoreCancellationTokenSource.Token).ConfigureAwait(false);
                        }
                        _logger.LogInformation("Restore result for mod {mod}: {result}", mod, ok);
                        if (ok)
                        {
                            _logger.LogDebug("Targeted restore succeeded for mod {mod}", mod);
                            anyTargetedRestoreSucceeded = true;
                            restoredModCount++;
                            _restoreModsDone = restoredModCount;
                        }
                        else
                        {
                            _logger.LogDebug("Targeted restore failed for mod {mod}", mod);
                        }
                    }

                    // If any targeted restore succeeded, clear cache and redraw via ShrinkU once at the end
                    if (anyTargetedRestoreSucceeded)
                    {
                        _cachedBackupsForAnalysis = null;
                        try { _shrinkuBackupService.RedrawPlayer(); } catch (Exception ex) { _logger.LogDebug(ex, "Redraw after targeted restore failed"); }
                        
                        _restoreStepText = "Restore completed (targeted mods)";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Targeted mod restore via ShrinkU services failed; will try session-based restore.");
                }

                // Prefer restoring via ShrinkU and choose the best-matching session for current textures
                var performedShrinkuRestore = false;
                try
                {
                    _restoreStepText = "Selecting best matching ShrinkU session";
                    var overview = await _shrinkuBackupService.GetBackupOverviewAsync().ConfigureAwait(false);
                    if (overview != null && overview.Count > 0)
                    {
                        // Build matching context from analysis: filenames and directory segments that indicate mod names
                        var analysis = _cachedAnalysisForPopup ?? _characterAnalyzer.LastAnalysis;
                        var texturePaths = analysis != null && analysis.Count > 0
                            ? GetTextureDataFromAnalysis(analysis).SelectMany(t => t.FilePaths).ToList()
                            : new List<string>();

                        var fileNamesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var dirNamesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var owningModDirsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        // Detect Penumbra mod root to extract owning mod folder names
                        var penumbraRoot = _ipcManager.Penumbra.ModDirectory ?? string.Empty;
                        penumbraRoot = penumbraRoot.Replace('/', '\\');
                        if (!string.IsNullOrWhiteSpace(penumbraRoot) && !penumbraRoot.EndsWith('\\'))
                            penumbraRoot += "\\";

                        foreach (var p in texturePaths)
                        {
                            try
                            {
                                var fn = Path.GetFileName(p);
                                if (!string.IsNullOrEmpty(fn))
                                    fileNamesSet.Add(fn);

                                var norm = p.Replace('/', '\\');
                                var parts = norm.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var part in parts)
                                {
                                    if (string.IsNullOrWhiteSpace(part) || (part.Length == 2 && part[1] == ':'))
                                        continue;
                                    dirNamesSet.Add(part);
                                }

                                // If the resolved path is under Penumbra's mod root, extract mod folder name
                                if (!string.IsNullOrWhiteSpace(penumbraRoot))
                                {
                                    var lower = norm;
                                    if (lower.StartsWith(penumbraRoot, StringComparison.OrdinalIgnoreCase))
                                    {
                                        var remainder = lower.Substring(penumbraRoot.Length);
                                        var idx = remainder.IndexOf('\\');
                                        var modFolder = idx > 0 ? remainder.Substring(0, idx) : remainder;
                                        if (!string.IsNullOrWhiteSpace(modFolder))
                                            owningModDirsSet.Add(modFolder);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to parse texture path for session matching");
                            }
                        }

                        // Include any file names from availableBackups mapping as additional match candidates
                        foreach (var k in availableBackups.Keys)
                            fileNamesSet.Add(k);

                        ShrinkU.Services.TextureBackupService.BackupSessionInfo? best = null;
                        int bestMatches = 0;
                        DateTime bestCreated = DateTime.MinValue;
                        foreach (var sess in overview)
                        {
                            if (sess == null || sess.Entries == null || sess.Entries.Count == 0)
                                continue;
                            var matches = 0;
                            foreach (var entry in sess.Entries)
                            {
                                try
                                {
                                    // Match by original filename
                                    if (!string.IsNullOrEmpty(entry.OriginalFileName) && fileNamesSet.Contains(entry.OriginalFileName))
                                        matches++;
                                    
                                    // Match by mod folder name observed in analysis paths
                                    if (!string.IsNullOrEmpty(entry.ModFolderName))
                                    {
                                        if (dirNamesSet.Contains(entry.ModFolderName))
                                            matches++;
                                        // Stronger signal if this mod folder is the owning mod inferred from Penumbra root
                                        if (owningModDirsSet.Contains(entry.ModFolderName))
                                            matches += 2;
                                    }

                                    // Match by relative path tail filename
                                    if (!string.IsNullOrEmpty(entry.ModRelativePath))
                                    {
                                        var relFile = Path.GetFileName(entry.ModRelativePath);
                                        if (!string.IsNullOrEmpty(relFile) && fileNamesSet.Contains(relFile))
                                            matches++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Failed to match backup entry against analysis");
                                }
                            }
                            if (matches > bestMatches || (matches == bestMatches && sess.CreatedUtc > bestCreated))
                            {
                                bestMatches = matches;
                                best = sess;
                                bestCreated = sess.CreatedUtc;
                            }
                        }

                        if (best != null && bestMatches > 0)
                        {
                            _logger.LogDebug("Restoring ShrinkU backup session '{display}' with {matches} matching entries.", best.DisplayName, bestMatches);
                            if (best.IsZip)
                            {
                                _restoreStepText = $"Restoring from ZIP session '{best.DisplayName}'";
                                await _shrinkuBackupService.RestoreFromZipAsync(best.SourcePath, _restoreProgress, _restoreCancellationTokenSource.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                _restoreStepText = $"Restoring session '{best.DisplayName}'";
                                await _shrinkuBackupService.RestoreFromSessionAsync(best.SourcePath, _restoreProgress, _restoreCancellationTokenSource.Token).ConfigureAwait(false);
                            }

                            _cachedBackupsForAnalysis = null;
                            _restoreModsTotal = 1;
                            _restoreModsDone = 1;
                            try { _shrinkuBackupService.RedrawPlayer(); } catch (Exception ex) { _logger.LogDebug(ex, "Redraw after session restore failed"); }
                            performedShrinkuRestore = true;
                            _restoreStepText = "Restore completed (session)";
                        }
                        else
                        {
                            _logger.LogDebug("No ShrinkU session matched current textures; falling back to latest session restore.");
                            _restoreStepText = "Restoring latest session (no match)";
                            await _shrinkuBackupService.RestoreLatestAsync(_restoreProgress, _restoreCancellationTokenSource.Token).ConfigureAwait(false);
                            _cachedBackupsForAnalysis = null;
                            _restoreModsTotal = 1;
                            _restoreModsDone = 1;
                            try { _shrinkuBackupService.RedrawPlayer(); } catch (Exception ex) { _logger.LogDebug(ex, "Redraw after latest restore failed"); }
                            performedShrinkuRestore = true;
                            _restoreStepText = "Restore completed (latest)";
                        }
                    }
                }
                catch (Exception shrEx)
                {
                    _logger.LogWarning(shrEx, "ShrinkU restore failed or unavailable, falling back to Sphene per-file restore.");
                }
                if (!performedShrinkuRestore)
                {
                    // Create mapping of backup files to their target locations
                    _restoreStepText = "Mapping backups to current texture locations";
                    var backupToTargetMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in availableBackups)
                    {
                        var originalFileName = kvp.Key;
                        var backupFiles = kvp.Value;
                        if (backupFiles.Count > 0)
                        {
                            var mostRecentBackup = backupFiles[0];
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
                        _restoreStepText = "Restoring textures per-file from analysis";
                        var results = await _textureBackupService.RestoreTexturesAsync(backupToTargetMap, deleteBackupsAfterRestore: true, _restoreProgress, _restoreCancellationTokenSource.Token).ConfigureAwait(false);
                        var successCount = results.Values.Count(success => success);
                        _logger.LogInformation("Texture restore completed: {successCount}/{totalCount} textures restored successfully. Backup files have been automatically deleted.", successCount, results.Count);
                        if (successCount > 0)
                        {
                            _cachedBackupsForAnalysis = null;
                            _restoreStepText = "Restore completed (per-file)";
                        }
                    }
                    else
                    {
                        _restoreStepText = "No textures could be mapped for restoration";
                        _logger.LogWarning("No textures from current analysis could be mapped for restoration");
                    }
                }
            }
            catch (Exception ex)
            {
                _restoreStepText = "Error during restore";
                _logger.LogError(ex, "Error during texture restore process");
            }
        }, _restoreCancellationTokenSource.Token).ContinueWith(_ =>
        {
            // Auto-close window after completion; user can reopen via next restore
            _restoreWindowOpen = false;
            _isRestoreInProgress = false;
        }, TaskScheduler.Default);
    }
    
    private string FindCurrentTextureLocation(string originalFileName)
    {
        try
        {
            // Use cached analysis if present (popup prepares a snapshot), otherwise use last analysis
            var analysis = _cachedAnalysisForPopup ?? _characterAnalyzer.LastAnalysis;
            if (analysis == null) return string.Empty;
            
            var textureData = GetTextureDataFromAnalysis(analysis);
            var normalizedOriginal = NormalizeDx11Name(originalFileName);
            foreach (var texture in textureData)
            {
                foreach (var filePath in texture.FilePaths)
                {
                    var fileName = Path.GetFileName(filePath);
                    // Exact match on file name
                    if (string.Equals(fileName, originalFileName, StringComparison.OrdinalIgnoreCase))
                        return filePath;

                    // Match ignoring DX11 token variations in names
                    var normalizedCandidate = NormalizeDx11Name(fileName);
                    if (string.Equals(normalizedCandidate, normalizedOriginal, StringComparison.OrdinalIgnoreCase))
                        return filePath;

                    // Fallback: match by path suffix to catch mod path variants
                    if (filePath.EndsWith(originalFileName, StringComparison.OrdinalIgnoreCase))
                        return filePath;
                    if (filePath.EndsWith(normalizedOriginal, StringComparison.OrdinalIgnoreCase))
                        return filePath;
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

    private static string NormalizeDx11Name(string name)
    {
        try
        {
            // Normalize common DX11 tokens in file names (e.g., "_dx11", "-dx11", ".dx11")
            var n = name;
            n = n.Replace("_dx11", string.Empty, StringComparison.OrdinalIgnoreCase);
            n = n.Replace("-dx11", string.Empty, StringComparison.OrdinalIgnoreCase);
            n = n.Replace(".dx11", string.Empty, StringComparison.OrdinalIgnoreCase);
            return n;
        }
        catch
        {
            return name;
        }
    }

    private async Task<Dictionary<string, string[]>> BuildFullModConversionDictionaryFromSelection()
    {
        var dict = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            var root = _ipcManager.Penumbra.ModDirectory ?? string.Empty;
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _texturesToConvert)
            {
                var source = kvp.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(source)) continue;
                var rel = !string.IsNullOrWhiteSpace(root) ? Path.GetRelativePath(root, source) : source;
                rel = rel.Replace('/', '\\');
                var idx = rel.IndexOf('\\');
                var mod = idx >= 0 ? rel.Substring(0, idx) : rel;
                if (!string.IsNullOrWhiteSpace(mod)) mods.Add(mod);
            }
            foreach (var mod in mods)
            {
                var files = await _shrinkuConversionService.GetModTextureFilesAsync(mod).ConfigureAwait(false);
                foreach (var f in files)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    if (!dict.ContainsKey(f)) dict[f] = Array.Empty<string>();
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to build full mod conversion dictionary"); }
        return dict;
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

private void DrawHalloweenBackground()
    {
        if (_halloweenBackgroundTexture == null) return;
        
        // Check if it's Halloween season (October 25-31)
        var now = DateTime.UtcNow;
        bool isHalloweenSeason = now.Month == 10 && now.Day >= 15 && now.Day <= 31;
        
        if (!isHalloweenSeason) return;
        
        var drawList = ImGui.GetWindowDrawList();
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();

        // Stretch over the whole window, clipped to the (style) corner radius
        var tintColor = new Vector4(1.0f, 1.0f, 1.0f, 0.30f);
        SpheneUI.Media.ImageInRect(drawList, _halloweenBackgroundTexture,
            windowPos, windowPos + windowSize, ImageFit.Stretch, tintColor,
            ImGui.GetStyle().WindowRounding);
    }
    
    
}
