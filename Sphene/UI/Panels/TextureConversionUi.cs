using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using ObjectKind = Sphene.API.Data.Enum.ObjectKind;
using Sphene.Interop.Ipc;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using Sphene.Utils;
using ShrinkU.Configuration;
using ShrinkU.Helpers;
using System.IO;
using System.Linq;

namespace Sphene.UI.Panels;

/// <summary>
/// Standalone texture-conversion window (opened from the Compact UI VRAM
/// button). Three views share one window: the main tool, the conversion
/// progress and the restore progress - conversions started from ShrinkU
/// or automatically pop the window straight into the progress view.
/// </summary>
public sealed class TextureConversionUi : WindowMediatorSubscriberBase
{
    private enum View { Main, Converting, Restoring }

    private View _view = View.Main;
    private bool _openedAutomatically;
    private bool _autoSilentConversion;
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private Task? _conversionTask;
    private CancellationTokenSource? _conversionCancellationTokenSource = new();
    private readonly Progress<(string fileName, int progress)> _conversionProgress = new();
    private int _conversionCurrentFileProgress;
    private DateTime _conversionStartTime = DateTime.MinValue;
    private string _currentModName = string.Empty;
    private int _currentModIndex;
    private int _totalMods;
    private int _currentModTotalFiles;
    private DateTime _currentModStartedAt = DateTime.MinValue;
    private readonly Progress<(string fileName, int current, int total)> _backupProgress = new();
    private string _backupCurrentFileName = string.Empty;
    private int _backupCurrentIndex;
    private int _backupTotalCount;
    private DateTime _backupStartTime = DateTime.MinValue;
    private readonly List<string> _backupStepLog = new();
    private int _backupTextureCount;
    private int _backupZipCount;
    private int _backupPmpCount;
    private bool _automaticModeEnabled;
    private string _autoConvertKey = string.Empty;
    private Task? _popupAnalysisTask;
    private bool _isPopupAnalysisBlocking;
    private Dictionary<string, List<string>>? _cachedBackupsForAnalysis;
    private DateTime _lastBackupAnalysisUpdate = DateTime.MinValue;
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysisForPopup;
    private Task? _restoreTask;
    private CancellationTokenSource _restoreCancellationTokenSource = new();
    private readonly Progress<(string fileName, int current, int total)> _restoreProgress = new();
    private int _restoreCurrentIndex;
    private int _restoreTotalCount;
    private DateTime _restoreStartTime = DateTime.MinValue;
    private string _restoreStepText = string.Empty;
    private volatile bool _isRestoreInProgress;
    private int _restoreModsTotal;
    private int _restoreModsDone;
    private string _currentRestoreMod = string.Empty;
    private Dictionary<string, List<string>>? _cachedShrinkUModBackups;
    private Task? _shrinkUDetectionTask;
    private DateTime _lastShrinkUDetectionUpdate = DateTime.MinValue;
    private volatile bool _isTextureBackupScanInProgress;
    private Dictionary<string, List<string>>? _cachedTextureBackupsFiltered;
    private Task<Dictionary<string, List<string>>>? _textureDetectionTask;
    private DateTime _lastTextureDetectionUpdate = DateTime.MinValue;
    private DateTime _backupScanTriggeredAt = DateTime.MinValue;
    private readonly Lock _backupScanDebounceLock = new();
    private CancellationTokenSource? _backupScanDebounceCts;
    private string _pendingBackupScanReason = string.Empty;
    private readonly SemaphoreSlim _shrinkUDetectionSemaphore = new(1, 1);
    private volatile bool _isDisposed;
    private string _allBackupsKey = string.Empty;
    private string _allBackupsStatusText = string.Empty;
    private string _currentBackupsKey = string.Empty;
    private string _currentBackupsStatusText = string.Empty;
    private Task<(long totalSize, int fileCount)>? _storageInfoTask;
    private DateTime _lastStorageInfoUpdate = DateTime.MinValue;
    private (long totalSize, int fileCount) _cachedStorageInfo;

    private readonly IpcManager _ipcManager;
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly TextureBackupService _textureBackupService;
    private readonly ShrinkU.Services.TextureBackupService _shrinkuBackupService;
    private readonly ShrinkU.Services.TextureConversionService _shrinkuConversionService;
    private readonly ShrinkUConfigService _shrinkuConfigService;
    private readonly ShrinkUHostService _shrinkuHostService;

    public TextureConversionUi(ILogger<TextureConversionUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager,
        CharacterAnalyzer characterAnalyzer, TextureBackupService textureBackupService,
        ShrinkU.Services.TextureBackupService shrinkuBackupService,
        ShrinkU.Services.TextureConversionService shrinkuConversionService,
        ShrinkUConfigService shrinkuConfigService, ShrinkUHostService shrinkuHostService)
        : base(logger, mediator, "Texture Conversion###SpheneTextureConversion", performanceCollectorService)
    {
        _ipcManager = ipcManager;
        _characterAnalyzer = characterAnalyzer;
        _textureBackupService = textureBackupService;
        _shrinkuBackupService = shrinkuBackupService;
        _shrinkuConversionService = shrinkuConversionService;
        _shrinkuConfigService = shrinkuConfigService;
        _shrinkuHostService = shrinkuHostService;

        Size = new Vector2(560, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 420),
        };

        try
        {
            _automaticModeEnabled = _shrinkuConfigService.Current.TextureProcessingMode == TextureProcessingMode.Automatic;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read ShrinkU configuration for automatic mode");
        }

        _conversionProgress.ProgressChanged += (sender, progress) =>
        {
            _conversionCurrentFileProgress = progress.progress;
        };
        _shrinkuConversionService.OnConversionProgress += HandleConversionProgress;
        _shrinkuConversionService.OnModProgress += HandleModProgress;
        _backupProgress.ProgressChanged += (sender, progress) =>
        {
            try
            {
                _backupCurrentFileName = progress.fileName;
                _backupCurrentIndex = progress.current;
                _backupTotalCount = progress.total;
                if (_backupStartTime == DateTime.MinValue)
                {
                    _backupStartTime = DateTime.UtcNow;
                }
                AppendBackupStep(progress.fileName);
                if (!_autoSilentConversion && !_automaticModeEnabled && !(_shrinkuHostService?.IsConversionUiOpen ?? false))
                {
                    OpenConversionView();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update backup progress");
            }
        };
        _shrinkuConversionService.OnBackupProgress += HandleBackupProgress;
        _restoreProgress.ProgressChanged += (sender, progress) =>
        {
            try
            {
                _restoreCurrentIndex = progress.current;
                _restoreTotalCount = progress.total;
                OpenRestoreView();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update restore progress");
            }
        };
        _shrinkuConversionService.OnPenumbraModsChanged += HandlePenumbraModsChanged;
        _shrinkuConversionService.OnPenumbraModSettingChanged += HandlePenumbraModSettingChanged;
        _shrinkuConversionService.OnExternalTexturesChanged += HandleExternalTexturesChanged;

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
        Mediator.Subscribe<OpenTextureConversionMessage>(this, (_) =>
        {
            _view = View.Main;
            _openedAutomatically = false;
            _cachedAnalysisForPopup = null;
            IsOpen = true;
        });
    }

    protected override FontAwesomeIcon? ChromeIcon => FontAwesomeIcon.ArrowsToEye;

    private void HandleConversionProgress((string, int) e)
    {
        try
        {
            _conversionCurrentFileProgress = e.Item2;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update conversion progress");
        }
    }

    private void HandleModProgress((string modName, int current, int total, int fileTotal) e)
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
            {
                OpenConversionView();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update mod-level conversion progress");
        }
    }

    private void HandleBackupProgress((string, int, int) e)
    {
        try
        {
            _backupCurrentFileName = e.Item1;
            _backupCurrentIndex = e.Item2;
            _backupTotalCount = e.Item3;
            if (_backupStartTime == DateTime.MinValue)
            {
                _backupStartTime = DateTime.UtcNow;
            }
            AppendBackupStep(e.Item1);
            if (!_autoSilentConversion && !_automaticModeEnabled && !(_shrinkuHostService?.IsConversionUiOpen ?? false))
            {
                OpenConversionView();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update backup progress (service)");
        }
    }

    private void HandlePenumbraModsChanged()
    {
        if (_isRestoreInProgress)
        {
            return;
        }
        try { TriggerImmediateBackupScan("penumbra-mods-changed"); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: penumbra-mods-changed"); }
    }

    private void HandlePenumbraModSettingChanged(Penumbra.Api.Enums.ModSettingChange change,
        Guid collectionId, string modDir, bool inherited)
    {
        if (_isRestoreInProgress)
        {
            return;
        }
        try { TriggerImmediateBackupScan("penumbra-mod-setting-changed"); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: penumbra-mod-setting-changed"); }
    }

    private void HandleExternalTexturesChanged(string reason)
    {
        var r = reason ?? string.Empty;
        if (_isRestoreInProgress || r.StartsWith("restore", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try { TriggerImmediateBackupScan("external-textures-changed:" + r); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger backup scan: external-textures-changed"); }
    }

    private void OpenConversionView()
    {
        _view = View.Converting;
        if (!IsOpen)
        {
            IsOpen = true;
            _openedAutomatically = true;
        }
    }

    private void OpenRestoreView()
    {
        _view = View.Restoring;
        if (!IsOpen)
        {
            IsOpen = true;
            _openedAutomatically = true;
        }
    }

    protected override void DrawInternal()
    {
        var converting = _conversionTask != null && !_conversionTask.IsCompleted;
        if (converting && _view == View.Main && !_autoSilentConversion && !_automaticModeEnabled
            && !(_shrinkuHostService?.IsConversionUiOpen ?? false))
        {
            _view = View.Converting;
        }
        switch (_view)
        {
            case View.Converting:
                DrawConvertingView();
                break;
            case View.Restoring:
                DrawRestoringView();
                break;
            default:
                DrawMainView();
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposed = true;
            DisposeCancellationTokenSource(ref _conversionCancellationTokenSource);
            _restoreCancellationTokenSource.Cancel();
            _restoreCancellationTokenSource.Dispose();
            DisposeCancellationTokenSource(ref _backupScanDebounceCts);
            _shrinkUDetectionSemaphore.Dispose();
            _shrinkuConversionService.OnConversionProgress -= HandleConversionProgress;
            _shrinkuConversionService.OnModProgress -= HandleModProgress;
            _shrinkuConversionService.OnBackupProgress -= HandleBackupProgress;
            _shrinkuConversionService.OnPenumbraModsChanged -= HandlePenumbraModsChanged;
            _shrinkuConversionService.OnPenumbraModSettingChanged -= HandlePenumbraModSettingChanged;
            _shrinkuConversionService.OnExternalTexturesChanged -= HandleExternalTexturesChanged;
        }
        base.Dispose(disposing);
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

    private void DrawMainView()
    {
                SpheneUI.Layout.PageHeader("Texture Conversion", "texconv", () =>
                {
                    SpheneUI.Text.MutedWrapped(
                        "Optimize and convert your character's textures to BC7 for lower VRAM use. " +
                        "Backups let you restore originals; Automatic mode converts on the fly.",
                        ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
                });
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
                    return;
                }

                // Use live analysis to keep popup in sync with Character Data Analysis UI
                var analysis = _characterAnalyzer.LastAnalysis;
            
            if (analysis == null || !analysis.Any())
            {
                ImGui.TextColored(SpheneTheme.Current.CompactServerStatusError, "No character data available");
                ImGui.Text("Please ensure you have a character loaded and analyzed.");
                
                // Still show backup management even without analysis
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
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
                    OpenTextureBackupFolder();
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
                    OpenTextureBackupFolder();
                }
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
            using (var tbl = SpheneUI.Tables.Begin("ConversionOverview", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            {
                if (!tbl.Success)
                {
                    return;
                }
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
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            SpheneUI.Text.SectionHeader("Backups");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
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
                    OpenTextureBackupFolder();
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
                    OpenTextureBackupFolder();
                }
            }
            
            
            
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            SpheneUI.Text.SectionHeader("Conversion Options");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
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
            if (SpheneUI.Controls.Checkbox("Automatic mode", ref _automaticModeEnabled))
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
                        _view = View.Converting;
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
                if (SpheneUI.Controls.Checkbox("Create full mod PMP before conversion", ref fullModBackup))
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
                        SpheneUI.Text.SectionHeader($"Affected Mods · {excludedCount} excluded");
                        // Use a child window to make the list scrollable
                        var requiredHeight = Math.Max(50, mods.Count * ImGui.GetTextLineHeightWithSpacing() + 10);
                        var childHeight = Math.Min(requiredHeight, 300.0f);

                        // Use fixed width to prevent auto-resize animation issues
                        if (ImGui.BeginChild("AffectedModsList", new Vector2(300, childHeight), false))
                        {
                            foreach (var m in mods)
                            {
                                bool isExcluded = false;
                                try { isExcluded = _shrinkuConfigService.Current.ExcludedMods != null && _shrinkuConfigService.Current.ExcludedMods.Contains(m); } catch (Exception ex) { _logger.LogDebug(ex, "Excluded state eval failed for {mod}", m); }
                                SpheneUI.Controls.Checkbox($"Exclude {m}", ref isExcluded);
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
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed computing affected mods"); }

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                var convertLabel = $"Convert {(nonBc7Textures?.Count ?? 0)} to BC7";
                var shrinkuW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.ExternalLinkAlt, "Open ShrinkU");
                var convertW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.FileArchive, convertLabel);
                var barRight = shrinkuW + ImGui.GetStyle().ItemSpacing.X + convertW;
                SpheneUI.Layout.ActionBar(barRight, () =>
                {
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
                    ImGui.SameLine();
                    if (SpheneUI.Controls.PrimaryButton(convertLabel, FontAwesomeIcon.FileArchive))
                    {
                        _ = StartTextureConversion(nonBc7Textures!);
                        _view = View.Converting;
                        _cachedAnalysisForPopup = null; // Clear cached analysis when window closes
                        _autoSilentConversion = false; // show progress for manual conversion
                        OpenConversionView();
                    }
                },
                _ =>
                {
                    // Visual indicator: show spinning arrows when automatic conversion is active
                    try
                    {
                        if (_automaticModeEnabled && _shrinkuConversionService.IsConverting)
                        {
                            DrawRotatingSpinnerIcon();
                            ImGui.SameLine();
                            ImGui.AlignTextToFramePadding();
                            SpheneUI.Text.Muted("Automatic conversion running");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to update automatic conversion spinner");
                    }
                });

                // No automatic conversion trigger on popup open when Automatic mode is enabled
            }
            else
            {
                SpheneUI.Text.Colored("All textures are already optimized!", SpheneUI.Palette.Success);
            }

    }

    private void OpenTextureBackupFolder()
    {
        try
        {
            var overview = _shrinkuBackupService.GetBackupOverviewAsync().GetAwaiter().GetResult();
            var firstPath = overview.FirstOrDefault()?.SourcePath;
            var backupDirectory = string.IsNullOrEmpty(firstPath)
                ? _textureBackupService.GetBackupDirectory()
                : Path.GetDirectoryName(firstPath)!;
            if (!string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory))
            {
                System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
            }
        }
        catch
        {
            var fallback = _textureBackupService.GetBackupDirectory();
            if (Directory.Exists(fallback))
            {
                System.Diagnostics.Process.Start("explorer.exe", fallback);
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

    private void DrawConvertingView()
    {
        var converting = _conversionTask != null && !_conversionTask.IsCompleted;
        // Suppress entirely while ShrinkU ConversionUI is open
        if ((_shrinkuHostService?.IsConversionUiOpen ?? false))
        {
            if (_openedAutomatically)
            {
                IsOpen = false;
            }
            return;
        }
        if (!converting && _view != View.Converting)
        {
            return;
        }
        // Title and batch mod progress
                SpheneUI.Text.SectionHeader("Converting Mods");
                
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

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                ImGui.Separator();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                // 2. Current Mod / File Progress
                if (!string.IsNullOrEmpty(_currentModName))
                    ImGui.Text($"Current Mod: {_currentModName}");

                // Backup progress section (shows while backups run or if completed with steps)
                var backupActive = _backupTotalCount > 0 && _backupCurrentIndex < _backupTotalCount;
                var backupCompleted = _backupTotalCount > 0 && _backupCurrentIndex >= _backupTotalCount;
                if (backupActive || (_backupStepLog.Count > 0 && (converting || backupCompleted)))
                {
                    SpheneUI.Text.SectionHeader("Backing up Mods");

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

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                ImGui.Separator();

                var cancelW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.StopCircle, "Cancel Conversion");
                SpheneUI.Layout.ActionBar(cancelW, () =>
                {
                    if (converting && SpheneUI.Icons.TextButton(FontAwesomeIcon.StopCircle, "Cancel Conversion"))
                    {
                        try { _shrinkuConversionService.Cancel(); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel ShrinkU conversion"); }
                    }
                },
                _ =>
                {
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Text.Colored(converting ? "Conversion is running..." : "Conversion completed.",
                        converting ? SpheneUI.Palette.Warning : SpheneUI.Palette.Success);
                });

        // Auto-close and cleanup once done (also handles cases where the task was nulled by lifecycle)
        if (_conversionTask == null || _conversionTask.IsCompleted)
        {
            if (_view == View.Converting)
            {
                _view = View.Main;
                if (_openedAutomatically)
                {
                    IsOpen = false;
                    _openedAutomatically = false;
                }
            }
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

    private void DrawRestoringView()
    {
        var restoreRunning = _restoreTask != null && !_restoreTask.IsCompleted;
        if (!restoreRunning && _view != View.Restoring)
        {
            return;
        }
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

                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    if (!string.IsNullOrEmpty(_currentRestoreMod))
                        ImGui.Text($"Current Mod: {_currentRestoreMod}");

                    var fileTotal = _restoreTotalCount;
                    var fileCurrent = _restoreCurrentIndex;
                    var filePercent = fileTotal > 0 ? (float)fileCurrent / fileTotal : 0f;
                    ImGui.ProgressBar(filePercent, new Vector2(-1, 0), $"{fileCurrent}/{fileTotal}");

                    if (!string.IsNullOrEmpty(_restoreStepText))
                    {
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                        SpheneUI.Text.Colored(_restoreStepText, SpheneUI.Palette.TextSecondary);
                    }

                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
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
        OpenRestoreView();
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

                        _logger.LogInformation("Attempting targeted restore for mod {mod} ({current}/{total})", mod, restoredModCount + 1, modsWithBackup.Count);

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
            if (_openedAutomatically)
            {
                IsOpen = false;
                _openedAutomatically = false;
            }
            else
            {
                _view = View.Main;
            }
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
    
}
