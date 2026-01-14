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
using Sphene.UI.Theme;
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
    private readonly UiSharedService _uiSharedService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly TextureBackupService _textureBackupService;
    private readonly AreaBoundSyncshellService _areaBoundSyncshellService;
    private readonly ShrinkUHostService _shrinkuHostService;
    private List<IDrawFolder> _drawFolders = new();
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private Vector2 _lastPosition = Vector2.One;
    private bool _isIncognitoModeActive = false;
    private DateTime _lastIncognitoButtonClick = DateTime.MinValue;
    private readonly HashSet<string> _prePausedPairs;
    private readonly HashSet<string> _prePausedSyncshells;
    private readonly System.Threading.Lock _pendingModSharingLock = new();
    private readonly HashSet<string> _pendingModSharingHashes = new(StringComparer.Ordinal);
    private Sphene.Services.UpdateInfo? _updateBannerInfo;
    private DateTime _lastReconnectButtonClick = DateTime.MinValue;
 private Vector2 _lastSize = Vector2.One;
 // One-time check to correct persisted width below minimum
 private bool _widthCorrectionChecked = false;
    private const string TestServerDisclaimerPopupName = "Test Server Disclaimer";
    private bool _showTestServerDisclaimerPopup;
    private DateTime _testServerDisclaimerOpenedAt = DateTime.MinValue;
    
    private bool _showModalForUserAddition;
    private float _transferPartHeight;
    private bool _wasOpen;
    
    
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

    public CompactUi(ILogger<CompactUi> logger, UiSharedService uiShared, SpheneConfigService configService, ApiController apiController, PairManager pairManager,
        ServerConfigurationManager serverManager, SpheneMediator mediator, FileUploadManager fileTransferManager,
        TagHandler tagHandler, DrawEntityFactory drawEntityFactory, SelectTagForPairUi selectTagForPairUi, SelectPairForTagUi selectPairForTagUi,
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, DalamudUtilService dalamudUtilService, CharacterAnalyzer characterAnalyzer,
        TextureBackupService textureBackupService, AreaBoundSyncshellService areaBoundSyncshellService,
        ShrinkU.Services.TextureBackupService shrinkuBackupService, ShrinkU.Services.TextureConversionService shrinkuConversionService,
        ShrinkUConfigService shrinkuConfigService,
        ShrinkUHostService shrinkuHostService)
        : base(logger, mediator, "###SpheneMainUI", performanceCollectorService)
    {
        _prePausedPairs = new(StringComparer.Ordinal);
        _prePausedSyncshells = new(StringComparer.Ordinal);
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
        _shrinkuBackupService = shrinkuBackupService;
        _shrinkuConversionService = shrinkuConversionService;
        _shrinkuConfigService = shrinkuConfigService;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        _shrinkuHostService = shrinkuHostService;
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
        _tabMenu = new TopTabMenu(Mediator, _apiController, _pairManager, _uiSharedService);

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
                    using (SpheneCustomTheme.ApplyTooltipTheme())
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
                    using (SpheneCustomTheme.ApplyTooltipTheme())
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
        Mediator.Subscribe<AreaBoundSyncshellLeftMessage>(this, (msg) => { 
            // Force UI refresh when syncshell is left so button visibility updates
            _logger.LogDebug("Area syncshell left: {SyncshellId}, checking if area syncshells are available: {HasAvailable}", 
                msg.SyncshellId, _areaBoundSyncshellService.HasAvailableAreaSyncshells());
        });

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
        _logger.LogDebug("Triggering immediate backup scan: {reason}", reason);
        _backupScanTriggeredAt = DateTime.UtcNow;
        // Invalidate caches so UI shows scanning state immediately
        _lastBackupAnalysisUpdate = DateTime.MinValue;
        _cachedBackupsForAnalysis = null;
        _cachedTextureBackupsFiltered = null;
        _textureDetectionTask = null;
        _lastTextureDetectionUpdate = DateTime.MinValue;
        _cachedShrinkUModBackups = null;
        _shrinkUDetectionTask = null;
        _lastShrinkUDetectionUpdate = DateTime.MinValue;

        // Start texture backup detection
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

        // Start ShrinkU mod backup detection
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
        }
        base.Dispose(disposing);
    }

    private IDisposable? _themeScope;
    private bool _stickEnabled;
    private Vector2 _settingsPos;
    private Vector2 _settingsSize;
    private Vector2 _preStickPos;
    private bool _restoreAfterUnstick;

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
        _themeScope = SpheneCustomTheme.ApplyThemeWithOriginalRadius();
        
        // Update window flags based on current theme settings
        if (!SpheneCustomTheme.CurrentTheme.CompactShowImGuiHeader)
        {
            Flags |= ImGuiWindowFlags.NoTitleBar;
        }
        else
        {
            Flags &= ~ImGuiWindowFlags.NoTitleBar;
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
        UiSharedService.ApplyPendingWindowResize(GetControlPanelTitle());
        base.PreDraw();
    }

    public override void PostDraw()
    {
        // Dispose theme scope after drawing
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
        if (_uiSharedService.IconButton(FontAwesomeIcon.Bug, null, null, null, null, ButtonStyleKeys.Compact_TestServer))
        {
            ToggleTestServerConnection();
        }

        var isEnabled = _configService.Current.UseTestServerOverride;
        var tooltip = isEnabled
            ? "Test Server: Enabled\nClick to switch back to the main server."
            : "Test Server: Disabled\nClick to switch to the test server.\nA disclaimer will be shown once.";
        UiSharedService.AttachToolTip(tooltip);
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

        using (SpheneCustomTheme.ApplyContextMenuTheme())
        {
            var isOpen = _showTestServerDisclaimerPopup;
            if (ImGui.BeginPopupModal(TestServerDisclaimerPopupName, ref isOpen, UiSharedService.PopupWindowFlags))
            {
                var elapsedSeconds = (DateTime.UtcNow - _testServerDisclaimerOpenedAt).TotalSeconds;
                var remainingSeconds = Math.Max(0.0, 20.0 - elapsedSeconds);
                var canConfirm = remainingSeconds <= 0.0;

                SpheneCustomTheme.DrawStyledText("Test Server Disclaimer", SpheneCustomTheme.CurrentTheme.CompactHeaderText);
                ImGui.Separator();
                UiSharedService.TextWrapped("You are about to connect to the Test Server.");
                UiSharedService.TextWrapped("On the Test Server, a new UID is created automatically for you.");
                UiSharedService.TextWrapped("Anything you do there has no effect on your main server account.");
                UiSharedService.TextWrapped("If you find bugs, please report them on Discord.");

                ImGui.Spacing();
                if (_uiSharedService.IconTextActionButton(FontAwesomeIcon.Users, "Open Discord"))
                {
                    Util.OpenLink("https://discord.gg/GbnwsP2XsF");
                }

                ImGui.Spacing();
                if (!canConfirm)
                {
                    UiSharedService.ColorTextWrapped($"Please wait {Math.Ceiling(remainingSeconds)} seconds before confirming.", ImGuiColors.DalamudYellow);
                }

                using (ImRaii.Disabled(!canConfirm))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, "I Understand, Connect"))
                    {
                        _configService.Current.HasAcceptedTestServerDisclaimer = true;
                        _configService.Save();
                        EnableTestServerOverrideAndReconnect();
                        isOpen = false;
                    }
                }

                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, "Cancel"))
                {
                    isOpen = false;
                }

                UiSharedService.SetScaledWindowSize(420);
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
        // Theme is already applied in PreDraw, no need to apply it again here
        
        // Draw Halloween background first (behind all content)
        DrawHalloweenBackground();
        


        if (!_apiController.IsCurrentVersion)
        {
            var ver = _apiController.CurrentClientVersion;
            var unsupported = "UNSUPPORTED VERSION";
            using (_uiSharedService.UidFont.Push())
            {
                var uidTextSize = ImGui.CalcTextSize(unsupported);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(SpheneCustomTheme.Colors.Error, unsupported);
            }
            UiSharedService.ColorTextWrapped($"Your Network client is outdated, current version is {ver.Major}.{ver.Minor}.{ver.Build}. " +
            $"Please update your client to maintain Network compatibility. Open /xlplugins and update the plugin.", SpheneCustomTheme.Colors.Error);
        }

        if (!_ipcManager.Initialized)
        {
            var unsupported = "MISSING CORE COMPONENTS";

            using (_uiSharedService.UidFont.Push())
            {
                var uidTextSize = ImGui.CalcTextSize(unsupported);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(SpheneCustomTheme.Colors.Error, unsupported);
            }
            var penumAvailable = _ipcManager.Penumbra.APIAvailable;
            var glamAvailable = _ipcManager.Glamourer.APIAvailable;

            UiSharedService.ColorTextWrapped($"One or more components essential for Network operation are unavailable. Enable or update the following:", SpheneCustomTheme.Colors.Error);
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

        // Draw window header with title and buttons only if ImGui header is disabled
        if (!SpheneCustomTheme.CurrentTheme.CompactShowImGuiHeader)
        {
            DrawWindowHeader();
        }
        
        // Main content area
        // Network Identity Section
        ImGui.Spacing();
        SpheneCustomTheme.DrawStyledText("Regulator ID", SpheneCustomTheme.CurrentTheme.CompactHeaderText);
        ImGui.Separator();
        DrawUIDContent();
        
        ImGui.Spacing();
        
        
        // Calculate connection status text and button positions at the end of the line
        var connectionStatus = _apiController.ServerState == ServerState.Connected ? "Connected" : "Disconnected";
        var statusTextSize = ImGui.CalcTextSize(connectionStatus);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var disconnectSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Unlink);
        var connectSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Link);
        var reconnectSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Redo);
#if IS_TEST_BUILD
        var testServerSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bug);
#endif
        
        // Calculate positions from right to left: status indicator + text, disconnect button, reconnect button
        var statusIndicatorWidth = 25.0f; // Approximate width for status indicator
        var connectOrDisconnectSize = _apiController.IsConnected ? disconnectSize : connectSize;
#if IS_TEST_BUILD
        var rightButtonsTotalWidth = connectOrDisconnectSize.X + testServerSize.X + reconnectSize.X + (ImGui.GetStyle().ItemSpacing.X * 2);
        var totalRightContentWidth = statusTextSize.X + statusIndicatorWidth + connectOrDisconnectSize.X + testServerSize.X + reconnectSize.X + (ImGui.GetStyle().ItemSpacing.X * 3);
#else
        var rightButtonsTotalWidth = connectOrDisconnectSize.X + reconnectSize.X + (ImGui.GetStyle().ItemSpacing.X * 0.5f);
        var totalRightContentWidth = statusTextSize.X + statusIndicatorWidth + connectOrDisconnectSize.X + reconnectSize.X + (ImGui.GetStyle().ItemSpacing.X * 2);
#endif
        
        ImGui.AlignTextToFramePadding();
        SpheneCustomTheme.DrawStyledText("Character Status", SpheneCustomTheme.CurrentTheme.CompactHeaderText);
        
        // Position disconnect button first (second from right) - move closer to right edge
        ImGui.SameLine(availableWidth - rightButtonsTotalWidth);
        
        if (_apiController.IsConnected)
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Unlink, null, null, null, null, ButtonStyleKeys.Compact_Disconnect) && _serverManager.CurrentServer != null)
            {
                _serverManager.CurrentServer.FullPause = true;
                _serverManager.Save();
                _ = _apiController.CreateConnectionsAsync();
            }
            UiSharedService.AttachToolTip("Disconnect from Server");
        }
        else
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Link, null, null, null, null, ButtonStyleKeys.Compact_Connect) && _serverManager.CurrentServer != null)
            {
                _serverManager.CurrentServer.FullPause = false;
                _serverManager.Save();
                _ = _apiController.CreateConnectionsAsync();
            }
            UiSharedService.AttachToolTip("Connect to Server");
        }
        
        // Position reconnect button (rightmost) - use SameLine() for natural spacing
#if IS_TEST_BUILD
        ImGui.SameLine();
        DrawTestServerToggleButton();
#endif

        ImGui.SameLine();
        
        var reconnectCurrentTime = DateTime.UtcNow;
        var reconnectTimeSinceLastClick = reconnectCurrentTime - _lastReconnectButtonClick;
        var isReconnectButtonDisabled = reconnectTimeSinceLastClick.TotalSeconds < 5.0;
        
        using (ImRaii.Disabled(isReconnectButtonDisabled))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Redo, null, null, null, null, ButtonStyleKeys.Compact_Reconnect))
            {
                _lastReconnectButtonClick = reconnectCurrentTime;
                _ = Task.Run(() => _apiController.CreateConnectionsAsync());
            }
        }
        
        var reconnectTooltipText = "Reconnect to the Sphene Network";
        if (isReconnectButtonDisabled)
        {
            var reconnectRemainingSeconds = Math.Ceiling(5.0 - reconnectTimeSinceLastClick.TotalSeconds);
            reconnectTooltipText += $"\nCooldown: {reconnectRemainingSeconds} seconds remaining";
        }
        UiSharedService.AttachToolTip(reconnectTooltipText);
        
        // Position connection status indicator and text (third from right)
        ImGui.SameLine(availableWidth - totalRightContentWidth);
        UiSharedService.DrawThemedStatusIndicator(connectionStatus, _apiController.ServerState == ServerState.Connected);
        
        
        ImGui.Separator();
        DrawServerStatusContent();
        
    
        ImGui.Spacing();
        
        if (_apiController.ServerState is ServerState.Connected)
        {
            ImGui.Spacing();

            
            // Navigation Section
            SpheneCustomTheme.DrawStyledText("Navigation", SpheneCustomTheme.CurrentTheme.CompactHeaderText);
            ImGui.Separator();
            _tabMenu.Draw(GetPendingModSharingCount());

            ImGui.Spacing();

            
            // Connected Pairs Section
            var onlineCount = _pairManager.DirectPairs.Count(p => p.IsOnline);
            var totalCount = _pairManager.DirectPairs.Count;
            var onlineText = $"({onlineCount} / {totalCount}) online";
            var onlineTextSize = ImGui.CalcTextSize(onlineText);
            
            SpheneCustomTheme.DrawStyledText("Connected Pairs", SpheneCustomTheme.CurrentTheme.CompactHeaderText);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - onlineTextSize.X);
            SpheneCustomTheme.DrawStyledText(onlineText, SpheneCustomTheme.CurrentTheme.CompactConnectedText);
            ImGui.Separator();
            
            
            
            using (ImRaii.PushId("pairlist")) DrawPairs();
            
            float pairlistEnd = ImGui.GetCursorPosY();
            using (ImRaii.PushId("transfers")) DrawTransfers();
            _transferPartHeight = ImGui.GetCursorPosY() - pairlistEnd - ImGui.GetTextLineHeight();
            using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
            using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
        }
        
        // Draw update notification at bottom if available or forced by theme
        if (Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.ForceShowUpdateHint || _updateBannerInfo?.IsUpdateAvailable == true)
        {
            ImGui.Separator();
            using (ImRaii.PushId("update-hint-footer"))
            {
                var startY = ImGui.GetCursorPosY();
                var lineH = ImGui.GetTextLineHeight();
                var h = Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.CompactUpdateHintHeight;
                var pad = Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.CompactUpdateHintPaddingY;
                var offset = Math.Max(0.0f, (h - lineH) * 0.5f);
                ImGui.SetCursorPosY(startY + pad + offset);
                using (var font = ImRaii.PushFont(UiBuilder.IconFont))
                {
                    ImGui.TextColored(Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.CompactUpdateHintColor, FontAwesomeIcon.InfoCircle.ToIconString());
                }
                ImGui.SameLine();
                var updateText = _updateBannerInfo?.LatestVersion != null ? $"Update available: {_updateBannerInfo.LatestVersion}" : "Update available";
                UiSharedService.ColorTextWrapped(updateText, Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.CompactUpdateHintColor);
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Download, "Open Details"))
                {
                    _logger.LogDebug("Update details button clicked, toggling UpdateNotificationUi");
                    Mediator.Publish(new UiToggleMessage(typeof(UpdateNotificationUi)));
                }
                ImGui.SetCursorPosY(startY + h + pad * 2);
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

        using (SpheneCustomTheme.ApplyContextMenuTheme())
        {
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
            (Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.ForceShowUpdateHint || _updateBannerInfo?.IsUpdateAvailable == true)
                ? Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.CompactUpdateHintHeight
                    + Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme.CompactUpdateHintPaddingY * 2
                    + ImGui.GetStyle().ItemSpacing.Y
                : 0f;
        
        var ySize = Math.Abs(_transferPartHeight) < 0.0001f
            ? 1
            : (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y
                + ImGui.GetTextLineHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetStyle().WindowBorderSize) - _transferPartHeight - ImGui.GetCursorPosY() - 15 - updateNotificationHeight; // add some Space and reserve space for update notification

        // Use full window content width with equal margins for the drawing area
        var drawAreaWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        ImGui.BeginChild("list", new Vector2(drawAreaWidth, ySize), border: false, ImGuiWindowFlags.NoScrollbar);

        foreach (var item in _drawFolders)
        {
            item.Draw();
        }

        ImGui.EndChild();
    }
    

    private void DrawServerStatusContent()
    {
        // Create a more intuitive layout with grouped functionality
        ImGui.AlignTextToFramePadding();
        
        DrawModeControls();
        
        // Right side: Texture Management
        DrawTextureManagement();
        
        if (_apiController.ServerState == ServerState.Connected)
        {
            // reserved for future server status details
        }
    }

    private void DrawModeControls()
    {
        ImGui.BeginGroup();
        ImGui.Text("Mode:");
        ImGui.SameLine();
        var currentTime = DateTime.UtcNow;
        var timeSinceLastClick = currentTime - _lastIncognitoButtonClick;
        var isButtonDisabled = timeSinceLastClick.TotalSeconds < 5.0;
        if (_isIncognitoModeActive)
        {
            var resumeColor = isButtonDisabled ? SpheneCustomTheme.CurrentTheme.TextSecondary : SpheneCustomTheme.Colors.Success;
            ImGui.PushStyleColor(ImGuiCol.Text, resumeColor);
            using (ImRaii.Disabled(isButtonDisabled))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Play, null, null, null, null, ButtonStyleKeys.Compact_IncognitoOff))
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
                UiSharedService.AttachToolTip(tooltipText);
            }
            ImGui.SameLine();
            ImGui.TextColored(SpheneCustomTheme.Colors.Success, "Incognito");
        }
        else
        {
            var heartColor = isButtonDisabled ? SpheneCustomTheme.CurrentTheme.TextSecondary : SpheneCustomTheme.Colors.Error;
            ImGui.PushStyleColor(ImGuiCol.Text, heartColor);
            using (ImRaii.Disabled(isButtonDisabled))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Heart, null, null, null, null, ButtonStyleKeys.Compact_IncognitoOn))
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
                UiSharedService.AttachToolTip(tooltipText);
            }
            ImGui.SameLine();
            ImGui.TextColored(SpheneCustomTheme.CurrentTheme.TextPrimary, "Normal");
        }
        bool hasAreaSyncshells = _areaBoundSyncshellService.HasAvailableAreaSyncshells();
        if (hasAreaSyncshells)
        {
            ImGui.SameLine();
            ImGui.Dummy(new Vector2(10, 0));
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.MapMarkerAlt, null, null, null, null, ButtonStyleKeys.Compact_AreaSelect))
            {
                _areaBoundSyncshellService.TriggerAreaSyncshellSelection();
            }
            UiSharedService.AttachToolTip("Open Area Syncshell Selection");
        }
        ImGui.EndGroup();
    }

    private void DrawTextureManagement()
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var conversionButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.ArrowsToEye);
        var buttonWidth = conversionButtonSize.X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var textureLabelWidth = ImGui.CalcTextSize("Textures:").X;
        var textureSizeBytes = GetCurrentTextureSize();
        var textureSizeMB = textureSizeBytes / (1024.0 * 1024.0);
        var textureIndicatorText = $"{textureSizeMB:F0}MB";
        var iconWidth = 16f;
        var textWidth = ImGui.CalcTextSize(textureIndicatorText).X;
        var textureIndicatorWidth = iconWidth + textWidth + 2f;
        var totalRightContentWidth = textureLabelWidth + buttonWidth + textureIndicatorWidth + (spacing * 2);
        ImGui.SameLine(availableWidth - totalRightContentWidth);
        ImGui.BeginGroup();
        ImGui.Text("Textures:");
        ImGui.SameLine();
        using (ImRaii.Disabled(_shrinkuConversionService.IsConverting))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.ArrowsToEye, null, null, null, null, ButtonStyleKeys.Compact_Conversion) && !_shrinkuConversionService.IsConverting)
            {
                _conversionWindowOpen = true;
            }
        }
        UiSharedService.AttachToolTip(_shrinkuConversionService.IsConverting
            ? "Texture Conversion\nDisabled while background conversion is running"
            : "Texture Conversion\nOptimize and convert textures to BC7 format");
        ImGui.SameLine();
        DrawCompactTextureIndicator();
        ImGui.EndGroup();
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.CurrentUploads
            .Where(u => !_fileTransferManager.IsPenumbraModUpload(u.Hash))
            .ToList();
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
            
            UiSharedService.DrawTransmissionBar("Transmitting", uploadProgress, uploadText, true);
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
            
            UiSharedService.DrawTransmissionBar("Receiving", downloadProgress, downloadText, false);
        }

        // Show transmission preview in-place when enabled
        var theme = Sphene.UI.Theme.SpheneCustomTheme.CurrentTheme;
        if (theme.ShowTransmissionPreview)
        {
            if (!currentUploads.Any())
            {
                ImGui.AlignTextToFramePadding();
                _uiSharedService.IconText(FontAwesomeIcon.Upload);
                ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);
                var previewUpload = theme.TransmissionPreviewUploadFill / 100.0f;
                UiSharedService.DrawTransmissionBar("Transmitting", previewUpload, $"{theme.TransmissionPreviewUploadFill:F1}%", true);
            }

            if (!currentDownloads.Any())
            {
                ImGui.AlignTextToFramePadding();
                _uiSharedService.IconText(FontAwesomeIcon.Download);
                ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);
                var previewDownload = theme.TransmissionPreviewDownloadFill / 100.0f;
                UiSharedService.DrawTransmissionBar("Receiving", previewDownload, $"{theme.TransmissionPreviewDownloadFill:F1}%", false);
        }
        }
    }

    

    private void DrawUIDContent()
    {
        var uidText = GetUidText();
        
        using (_uiSharedService.UidFont.Push())
        {
            ImGui.SetWindowFontScale(SpheneCustomTheme.CurrentTheme.CompactUidFontScale);
            var uidTextSize = ImGui.CalcTextSize(uidText);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (uidTextSize.X / 2));
            
            // Use CompactUidColor for connected state, server status colors for other states
            var uidColor = _apiController.ServerState == ServerState.Connected 
                ? SpheneCustomTheme.CurrentTheme.CompactUidColor 
                : GetServerStatusColor();
            
            ImGui.TextColored(uidColor, uidText);
            ImGui.SetWindowFontScale(1.0f);
        }

        if (_apiController.ServerState is ServerState.Connected)
        {
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(_apiController.DisplayName);
            }
            UiSharedService.AttachToolTip("Click to copy");
        }
        else
        {
            UiSharedService.ColorTextWrapped(GetServerError(), GetServerStatusColor());
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
                && !u.Key.UserPair.OwnPermissions.IsPaused();
        bool FilterPausedUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.UserPair.OwnPermissions.IsPaused() && (!u.Key.IsMutuallyVisible || !_configService.Current.ShowVisibleUsersSeparately);
        Dictionary<Pair, List<GroupFullInfoDto>> BasicSortedDictionary(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.OrderByDescending(u => u.Key.IsMutuallyVisible)
                .ThenByDescending(u => u.Key.IsOnline)
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(u => u.Key, u => u.Value);
        ImmutableList<Pair> ImmutablePairList(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.Select(k => k.Key).ToImmutableList();
        bool FilterVisibleUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsMutuallyVisible
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
            => u.Key.IsDirectlyPaired && (!u.Key.IsOneSidedPair || u.Value.Any()) && !u.Key.IsOnline && !u.Key.UserPair.OwnPermissions.IsPaused() && (!u.Key.IsMutuallyVisible || !_configService.Current.ShowVisibleUsersSeparately);
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
                    return u.Key.IsMutuallyVisible ? 3 : 4;
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

    private static string GetControlPanelTitle()
    {
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (ver is not null)
        {
            var revision = ver.Revision < 0 ? 0 : ver.Revision;
            return $"Sphene Control Panel {ver.Major}.{ver.Minor}.{ver.Build}.{revision}";
        }
        return "Sphene Control Panel";
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
        return _apiController.ServerState switch
        {
            ServerState.Connecting => SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning,
            ServerState.Reconnecting => SpheneCustomTheme.CurrentTheme.CompactServerStatusError,
            ServerState.Connected => SpheneCustomTheme.CurrentTheme.CompactServerStatusConnected,
            ServerState.Disconnected => SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning,
            ServerState.Disconnecting => SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning,
            ServerState.Unauthorized => SpheneCustomTheme.CurrentTheme.CompactServerStatusError,
            ServerState.VersionMisMatch => SpheneCustomTheme.CurrentTheme.CompactServerStatusError,
            ServerState.Offline => SpheneCustomTheme.CurrentTheme.CompactServerStatusError,
            ServerState.RateLimited => SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning,
            ServerState.NoSecretKey => SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning,
            ServerState.MultiChara => SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning,
            ServerState.OAuthMisconfigured => SpheneCustomTheme.CurrentTheme.CompactServerStatusError,
            ServerState.OAuthLoginTokenStale => SpheneCustomTheme.CurrentTheme.CompactServerStatusError,
            ServerState.NoAutoLogon => SpheneCustomTheme.CurrentTheme.CompactServerStatusWarning,
            _ => SpheneCustomTheme.CurrentTheme.CompactServerStatusError
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
                    await _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), syncshellsToPause)).ConfigureAwait(false);
                    _logger.LogInformation("Paused {count} syncshells for incognito mode", syncshellsToPause.Count);
                }
                
                _isIncognitoModeActive = true;
                _logger.LogInformation("Incognito mode activated");
                
                // Save configuration
                _configService.Current.IsIncognitoModeActive = _isIncognitoModeActive;
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
            return (SpheneCustomTheme.Colors.Success, $"Texture Size: Optimal ({textureSizeMB:F1} MB)", FontAwesomeIcon.CheckCircle);
        }
        else if (textureSizeMB <= 600)
        {
            return (SpheneCustomTheme.Colors.Warning, $"Texture Size: Large ({textureSizeMB:F1} MB)", FontAwesomeIcon.ExclamationTriangle);
        }
        else
        {
            return (SpheneCustomTheme.Colors.Error, $"Texture Size: Very Large ({textureSizeMB:F1} MB)", FontAwesomeIcon.ExclamationCircle);
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
            UiSharedService.AttachToolTip("Automatic conversion is running");

            ImGui.SameLine(0, 2);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.Text($"{textureSizeMB:F0}MB");
            ImGui.PopStyleColor();

            // Keep size tooltip on the MB text
            if (ImGui.IsItemHovered())
            {
                using (SpheneCustomTheme.ApplyTooltipTheme())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Texture Size");
                    ImGui.Separator();
                    ImGui.Text($"Current: {textureSizeMB:F0} MB");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneCustomTheme.Colors.Success, "• 0-300 MB: Optimal");
                    ImGui.TextColored(SpheneCustomTheme.Colors.Warning, "• 300-600 MB: Large");
                    ImGui.TextColored(SpheneCustomTheme.Colors.Error, "• 600+ MB: Very Large");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Tip: Click the button for quick conversion!");
                    ImGui.EndTooltip();
                }
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            _uiSharedService.IconText(icon);
            ImGui.SameLine(0, 2);
            ImGui.Text($"{textureSizeMB:F0}MB");
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                using (SpheneCustomTheme.ApplyTooltipTheme())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Texture Size");
                    ImGui.Separator();
                    ImGui.Text($"Current: {textureSizeMB:F0} MB");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneCustomTheme.Colors.Success, "• 0-300 MB: Optimal");
                    ImGui.TextColored(SpheneCustomTheme.Colors.Warning, "• 300-600 MB: Large");
                    ImGui.TextColored(SpheneCustomTheme.Colors.Error, "• 600+ MB: Very Large");
                    ImGui.Spacing();
                    ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Tip: Click the button for quick conversion!");
                    ImGui.EndTooltip();
                }
            }
        }
    }

    private void DrawConversionWindow()
    {
        if (!_conversionWindowOpen)
            return;

        using (SpheneCustomTheme.ApplyContextMenuTheme())
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
                    UiSharedService.ColorTextWrapped("Analyzing character data…", SpheneCustomTheme.Colors.Warning);
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
                ImGui.TextColored(SpheneCustomTheme.Colors.Error, "No character data available");
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
                            ? "Scanning for backups… none found yet."
                            : "No texture backups found.";
                    }
                }
                // When scanning and no backups yet, show a rotating spinner before the status text
                if (scanning && allCount == 0)
                {
                    DrawRotatingSpinnerIcon(1.0f);
                    ImGui.SameLine();
                    UiSharedService.ColorTextWrapped(_allBackupsStatusText, SpheneCustomTheme.CurrentTheme.TextSecondary);
                }
                else
                {
                    UiSharedService.ColorTextWrapped(_allBackupsStatusText, allCount > 0 ? SpheneCustomTheme.Colors.Warning : SpheneCustomTheme.Colors.Error);
                }
                if (allCount == 0)
                {
                    // Keep tooltip aligned with scanning window
                    detectionInProgress = _isTextureBackupScanInProgress || (_shrinkUDetectionTask != null && !_shrinkUDetectionTask.IsCompleted);
                    withinForcedWindow = _backupScanTriggeredAt != DateTime.MinValue && (DateTime.UtcNow - _backupScanTriggeredAt) < TimeSpan.FromSeconds(10);
                    scanning = (detectionInProgress || withinForcedWindow);
                    UiSharedService.AttachToolTip(scanning
                        ? "Backups are being scanned. It may be that none exist yet or conversion was not required."
                        : "There are currently no backups. A backup may not exist yet or was never required if the mod was already converted.");
                }
                // Reserve extra space to avoid layout shift on periodic redraws
                ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2));

                if (allCount > 0)
                {
                    using (ImRaii.Disabled(_automaticModeEnabled))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, "Restore from ShrinkU backups"))
                        {
                            StartTextureRestore(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
                        }
                    }
                    UiSharedService.AttachToolTip(_automaticModeEnabled
                        ? "Disable Automatic mode to enable restore."
                        : "Restore textures from ShrinkU backups.");
                    if (_automaticModeEnabled)
                    {
                        ImGui.SameLine();
                        _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle, SpheneCustomTheme.Colors.Warning);
                        ImGui.SameLine();
                        UiSharedService.ColorText("Disabled in Automatic mode", SpheneCustomTheme.Colors.Warning);
                    }
                    ImGui.SameLine();
                    
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
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
                    var msg = scanning ? "Scanning for backups… none found yet." : "No texture backups found.";
                    if (scanning)
                    {
                        DrawRotatingSpinnerIcon(1.0f);
                        ImGui.SameLine();
                        UiSharedService.ColorTextWrapped(msg, SpheneCustomTheme.CurrentTheme.TextSecondary);
                    }
                    else
                    {
                        UiSharedService.ColorTextWrapped(msg, SpheneCustomTheme.CurrentTheme.TextSecondary);
                    }
                    // Reserve extra space to avoid layout shift on periodic redraws
                    ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2));
                    
                    using (ImRaii.Disabled(true))
                    {
                        _uiSharedService.IconTextButton(FontAwesomeIcon.Undo, "Restore from ShrinkU backups");
                    }
                    UiSharedService.AttachToolTip(scanning
                        ? "Scanning backups; restore will be enabled automatically when backups are found."
                        : "No texture backups available to restore.");
                    ImGui.SameLine();
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
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
                ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Statistics");
                ImGui.Text($"Total Textures: {totalTextures}");
                ImGui.Text($"BC7 Optimized: {bc7Textures}");
                ImGui.TextColored(SpheneCustomTheme.Colors.Warning, $"To Convert: {nonBc7Textures?.Count ?? 0} ({totalSizeMB:F1} MB)");

                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Storage");
                
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
                    ImGui.TextColored(SpheneCustomTheme.Colors.Warning, $"Storage: {sizeInMB:F2} MB ({fileCount} files)");
                }
                else
                {
                    ImGui.TextColored(SpheneCustomTheme.CurrentTheme.TextSecondary, "Calculating...");
                }
                ImGui.EndTable();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Backups Section (Redesigned)
            ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Backups");
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
                        ? "Scanning for backups… none found yet."
                        : "No backups found for current textures.";
                }
            }
            // When scanning and no backups yet, show a rotating spinner before the status line
            if (scanningCurrent && (textureBackupCount + shrinkuModBackupCount == 0))
            {
                DrawRotatingSpinnerIcon(1.0f);
                ImGui.SameLine();
                UiSharedService.ColorText(_currentBackupsStatusText, ImGuiColors.DalamudGrey);
            }
            else
            {
                UiSharedService.ColorText(_currentBackupsStatusText,
                    (textureBackupCount > 0 || shrinkuModBackupCount > 0)
                        ? SpheneCustomTheme.Colors.Success
                        : (scanningCurrent ? ImGuiColors.DalamudGrey : SpheneCustomTheme.CurrentTheme.TextSecondary));
            }

            if (textureBackupCount > 0 || shrinkuModBackupCount > 0)
            {
                using (ImRaii.Disabled(_automaticModeEnabled))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Undo, "Restore"))
                    {
                        StartTextureRestore(availableBackups);
                    }
                }
                UiSharedService.AttachToolTip(_automaticModeEnabled
                    ? "Disable Automatic mode to enable restore."
                    : "Restore current textures from backups created earlier.");
                if (_automaticModeEnabled)
                {
                    ImGui.SameLine();
                    _uiSharedService.IconText(FontAwesomeIcon.ExclamationTriangle, SpheneCustomTheme.Colors.Warning);
                    ImGui.SameLine();
                    UiSharedService.ColorText("Disabled in Automatic mode", SpheneCustomTheme.Colors.Warning);
                }
                
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
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
                    _uiSharedService.IconTextButton(FontAwesomeIcon.Undo, "Restore");
                }
                UiSharedService.AttachToolTip("No texture backups available to restore.");
                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
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
            ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, "Conversion Options");
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
            UiSharedService.AttachToolTip(_automaticModeEnabled
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
                        ImGui.TextColored(SpheneCustomTheme.Colors.SpheneGold, $"Affected Mods (Excluded: {excludedCount})");
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

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileArchive, $"Convert {(nonBc7Textures?.Count ?? 0)} to BC7"))
                {
                    _ = StartTextureConversion(nonBc7Textures!);
                    _conversionWindowOpen = false;
                    _cachedAnalysisForPopup = null; // Clear cached analysis when window closes
                    _autoSilentConversion = false; // show progress for manual conversion
                    _conversionProgressWindowOpen = true;
                }

                ImGui.SameLine();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, "Open ShrinkU"))
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
                        UiSharedService.AttachToolTip("Automatic conversion is running");
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
                ImGui.TextColored(SpheneCustomTheme.Colors.Success, "All textures are already optimized!");
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

        using (SpheneCustomTheme.ApplyContextMenuTheme())
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
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel Conversion"))
                    {
                        try { _shrinkuConversionService.Cancel(); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel ShrinkU conversion"); }
                    }
                    ImGui.SameLine();
                    UiSharedService.ColorText("Conversion is running…", SpheneCustomTheme.Colors.Warning);
                }
                else
                {
                    UiSharedService.ColorText("Conversion completed.", SpheneCustomTheme.Colors.Success);
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
            using (SpheneCustomTheme.ApplyContextMenuTheme())
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
                        UiSharedService.ColorText(_restoreStepText, SpheneCustomTheme.Colors.TextSecondary);
                    }

                    ImGui.Spacing();
                    if (restoreRunning)
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel Restore"))
                        {
                            try { _restoreCancellationTokenSource.Cancel(); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel restore operation"); }
                        }
                        ImGui.SameLine();
                        UiSharedService.ColorText("Restore is running…", SpheneCustomTheme.Colors.Warning);
                    }
                    else
                    {
                        UiSharedService.ColorText("Restore completed.", SpheneCustomTheme.Colors.Success);
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
        var theme = SpheneCustomTheme.CurrentTheme;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect ShrinkU mod backups asynchronously");
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
    
    private void DrawWindowHeader()
    {
        // Calculate header dimensions with proper padding
        var headerPadding = new Vector2(12.0f, 8.0f);
        var contentStart = ImGui.GetCursorScreenPos();
        var textHeight = ImGui.GetTextLineHeight();
        var headerHeight = textHeight + (headerPadding.Y * 2);
        
        // Calculate background rectangle coordinates
        var headerStart = new Vector2(contentStart.X, contentStart.Y);
        var headerEnd = new Vector2(headerStart.X + ImGui.GetContentRegionAvail().X, headerStart.Y + headerHeight);
        
        var headerBgColor = SpheneCustomTheme.CurrentTheme.CompactHeaderBg;
        
        // Draw the rounded background rectangle
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(headerStart, headerEnd, SpheneColors.ToImGuiColor(headerBgColor), SpheneCustomTheme.CurrentTheme.CompactHeaderRounding);
        
        // Position content vertically centered within the header
        var contentY = contentStart.Y + (headerHeight - textHeight) / 2.0f;
        ImGui.SetCursorScreenPos(new Vector2(contentStart.X + headerPadding.X, contentY));
        
        // Window title - vertically centered
        SpheneCustomTheme.DrawStyledText(GetControlPanelTitle(), SpheneCustomTheme.CurrentTheme.CompactPanelTitleText);
        
        
        
        // Header buttons on the same line, positioned from the right with padding
        var buttonSpacing = 8.0f; // Reduced spacing between buttons (was 16.0f)
        var buttonWidth = 22.0f; // Width of each button
        var buttonHeight = ImGui.GetFrameHeight(); // Get standard button height
        
        // Calculate button Y position to center them vertically in the header
        var buttonY = contentStart.Y + (headerHeight - buttonHeight) / 2.0f;
        
        // Position close button first (rightmost)
        var closeButtonX = ImGui.GetContentRegionAvail().X - buttonWidth - headerPadding.X;
        
        // Position settings button with reduced spacing from close button
        var settingsButtonX = closeButtonX - buttonWidth - buttonSpacing;
        
        // Position settings button centered vertically in header
        ImGui.SetCursorScreenPos(new Vector2(contentStart.X + settingsButtonX, buttonY));
        
        if (_uiSharedService.IconButton(FontAwesomeIcon.Cog, null, null, null, null, ButtonStyleKeys.Compact_Settings))
        {
            Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
        }
        if (ImGui.IsItemHovered())
        {
            using (SpheneCustomTheme.ApplyTooltipTheme())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Open Network Configuration");
                ImGui.EndTooltip();
            }
        }
        
        
        // Position close button centered vertically in header
        ImGui.SetCursorScreenPos(new Vector2(contentStart.X + closeButtonX, buttonY));
        
        if (_uiSharedService.IconButton(FontAwesomeIcon.Times, null, null, null, null, ButtonStyleKeys.Compact_Close))
        {
            _logger.LogDebug("Close button clicked, closing CompactUI");
            IsOpen = false;
        }
        if (ImGui.IsItemHovered())
        {
            using (SpheneCustomTheme.ApplyTooltipTheme())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Close Sphene");
                ImGui.EndTooltip();
            }
        }
        
        
        // Move cursor to end of header area
        ImGui.SetCursorScreenPos(new Vector2(contentStart.X, headerEnd.Y));
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
        
        // Calculate background position and size to cover the entire window
        var backgroundPos = windowPos;
        var backgroundSize = windowSize;
        
        // Draw the Halloween background with transparency (0.15 alpha for subtle effect)
        var tintColor = new Vector4(1.0f, 1.0f, 1.0f, 0.30f);
        drawList.AddImage(_halloweenBackgroundTexture.Handle, backgroundPos, 
            new Vector2(backgroundPos.X + backgroundSize.X, backgroundPos.Y + backgroundSize.Y), 
            Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(tintColor));
    }
    
    
}
