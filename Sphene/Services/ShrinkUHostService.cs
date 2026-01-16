using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using ShrinkU.Configuration;
using ShrinkU.UI;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.SpheneConfiguration;
using ShrinkU.Services;

namespace Sphene.Services;

public sealed class ShrinkUHostService : IHostedService, IDisposable
{
    private readonly ILogger<ShrinkUHostService> _logger;
    private readonly WindowSystem _windowSystem;
    private readonly ConversionUI _conversionUi;
    private readonly SettingsUI _settingsUi;
    private readonly ReleaseChangelogUI _releaseChangelogUi;
    private readonly DebugUI _debugUi;
    private readonly ShrinkU.UI.StartupProgressUI _startupProgressUi;
    private readonly FirstRunSetupUI _firstRunUi;
    private readonly SpheneConfigService _configService;
    private readonly ShrinkUConfigService _shrinkuConfigService;
    private bool _registered;
    private readonly ShrinkU.Services.TextureBackupService _backupService;
    private readonly ShrinkU.Services.TextureConversionService _shrinkuConversionService;
    private CancellationTokenSource? _refreshCts;
    private readonly PenumbraIpc _penumbraIpc;
    private PenumbraExtensionService? _penumbraExtension;

    public ShrinkUHostService(
        ILogger<ShrinkUHostService> logger,
        WindowSystem windowSystem,
        ConversionUI conversionUi,
        SettingsUI settingsUi,
        ReleaseChangelogUI releaseChangelogUi,
        DebugUI debugUi,
        FirstRunSetupUI firstRunUi,
        SpheneConfigService configService,
        ShrinkUConfigService shrinkuConfigService,
        ShrinkU.Services.TextureBackupService backupService,
        ShrinkU.Services.TextureConversionService shrinkuConversionService,
        ShrinkU.UI.StartupProgressUI startupProgressUi,
        Sphene.Interop.Ipc.IpcManager ipcManager,
        PenumbraIpc penumbraIpc)
    {
        _logger = logger;
        _windowSystem = windowSystem;
        _conversionUi = conversionUi;
        _settingsUi = settingsUi;
        _releaseChangelogUi = releaseChangelogUi;
        _debugUi = debugUi;
        _firstRunUi = firstRunUi;
        _startupProgressUi = startupProgressUi;
        _configService = configService;
        _shrinkuConfigService = shrinkuConfigService;
        _backupService = backupService;
        _shrinkuConversionService = shrinkuConversionService;
        _ = ipcManager;
        _penumbraIpc = penumbraIpc;

        try { _configService.ConfigSave += OnConfigSaved; }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to subscribe to ConfigSave event"); }
    }

    public bool IsConversionUiOpen
    {
        get
        {
            try { return _conversionUi != null && _conversionUi.IsOpen; }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to read ConversionUI IsOpen"); return false; }
        }
    }

    private void SetPenumbraIntegration(bool enable)
    {
        try
        {
            if (enable)
            {
                if (_penumbraExtension == null)
                {
                    _penumbraExtension = new PenumbraExtensionService(_penumbraIpc, _conversionUi, _logger);
                    _logger.LogDebug("Initialized ShrinkU Penumbra integration");
                }
            }
            else
            {
                if (_penumbraExtension != null)
                {
                    _penumbraExtension.Dispose();
                    _penumbraExtension = null;
                    _logger.LogDebug("Disposed ShrinkU Penumbra integration");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ShrinkU Penumbra integration state to {Enable}", enable);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Ensure ShrinkU backup path uses Sphene cache once available
            TryConfigureShrinkUBackupPath();

            var enable = _configService.Current.EnableShrinkUIntegration;
            _logger.LogDebug("ShrinkUHostService StartAsync: integration enabled={enabled}", enable);
            if (enable)
            {
                RegisterWindows();
                SetPenumbraIntegration(true);
                // Mark ShrinkU as integrated with Sphene for UI gating
                try
                {
                    _shrinkuConfigService.Current.AutomaticControllerName = "Sphene";
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = true;
                    _shrinkuConfigService.Save();
                    _logger.LogDebug("Set ShrinkU AutomaticControllerName to Sphene on integration start");
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to set ShrinkU automatic controller on start"); }
                try
                {
                    if (!_shrinkuConfigService.Current.EnableFullModBackupBeforeConversion)
                    {
                        _shrinkuConfigService.Current.EnableFullModBackupBeforeConversion = true;
                        _shrinkuConfigService.Current.EnableBackupBeforeConversion = false;
                        _shrinkuConfigService.Save();
                        _logger.LogDebug("Enforced default: EnableFullModBackupBeforeConversion=true (integrated)");
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to enforce PMP backup default in integrated mode"); }
                CleanupDuplicateShrinkUConfig();
                try { _conversionUi.SetStartupRefreshInProgress(true); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to set startup refresh in progress"); }
                try { _shrinkuConversionService.SetEnabled(true); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to enable ShrinkU conversion"); }
            }
            if (enable)
            {
                try
                {
                    _refreshCts?.Cancel();
                    _refreshCts = new CancellationTokenSource();
                    var token = _refreshCts.Token;
                    var showStartupUi = true;
                    try { showStartupUi = !_backupService.IsBackupFolderFingerprintUnchanged(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to determine ShrinkU backup fingerprint state"); }
                    _logger.LogDebug("[ShrinkU][Fingerprint] Startup UI decision: show={show} backupPath={path}", showStartupUi, _shrinkuConfigService.Current.BackupFolderPath ?? string.Empty);
                    if (showStartupUi)
                    {
                        try { _startupProgressUi.ResetAll(); _startupProgressUi.IsOpen = true; }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to open StartupProgressUI"); }
                    }
                    else
                    {
                        try { _startupProgressUi.IsOpen = false; }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to close StartupProgressUI"); }
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogDebug("ShrinkU startup via Sphene: skipping Penumbra wait");
                            try { _backupService.SetSavingEnabled(false); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to disable backup saving at startup"); }
                            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                            if (token.IsCancellationRequested) return;
                            try { _startupProgressUi.SetStep(1); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to set StartupProgressUI step 1"); }
                            var maxBackupSeconds = 5;
                            var refreshTask = _backupService.RefreshAllBackupStateAsync();
                            var completed = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(maxBackupSeconds), token)).ConfigureAwait(false);
                            if (completed != refreshTask)
                            {
                                _logger.LogWarning("Backup refresh timed out after {sec}s; continuing", maxBackupSeconds);
                            }
                            else
                            {
                                try { await refreshTask.ConfigureAwait(false); }
                                catch (Exception ex) { _logger.LogDebug(ex, "RefreshAllBackupStateAsync completed with error"); }
                            }
                            try { _startupProgressUi.MarkBackupDone(); _startupProgressUi.SetStep(3); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to mark backup done / set step 3"); }
                            await _backupService.PopulateMissingOriginalBytesAsync(token).ConfigureAwait(false);
                            try { _startupProgressUi.SetStep(4); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to set StartupProgressUI step 4"); }
                            try { await _shrinkuConversionService.UpdateAllModUsedTextureFilesAsync().ConfigureAwait(false); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to update used texture files"); }
                            try { _startupProgressUi.MarkUsedDone(); _startupProgressUi.SetStep(5); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to mark used done / set step 5"); }
                            try { _backupService.SetSavingEnabled(true); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to re-enable backup saving"); }
                            try { _backupService.SaveModState(); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to save mod state"); }
                            try { _startupProgressUi.MarkSaveDone(); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to mark save done"); }
                            try { _conversionUi.TriggerStartupRescan(); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to trigger startup rescan"); }
                            var threads = Math.Max(1, _shrinkuConfigService.Current.MaxStartupThreads);
                            await _shrinkuConversionService.RunInitialParallelUpdateAsync(threads, token).ConfigureAwait(false);
                            _logger.LogDebug("Initial ShrinkU startup update completed");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Initial ShrinkU backup state refresh failed");
                        }
                        finally
                        {
                            try { _conversionUi.SetStartupRefreshInProgress(false); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clear startup refresh flag"); }
                            try { _startupProgressUi.IsOpen = false; }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to close StartupProgressUI"); }
                        }
                    }, token);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to start ShrinkU startup refresh task"); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register ShrinkU windows");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("ShrinkUHostService StopAsync: removing windows");
            try { _conversionUi.DisableModStateSaving(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to disable mod state saving"); }
            try { _conversionUi.ShutdownBackgroundWork(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to shutdown background work"); }
            UnregisterWindows();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during StopAsync window removal");
        }
        try { _refreshCts?.Cancel(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel refresh CTS in StopAsync"); }
        try { _refreshCts?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose refresh CTS in StopAsync"); }
        try { _configService.ConfigSave -= OnConfigSaved; }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe ConfigSave in StopAsync"); }
        try
        {
            _shrinkuConfigService.Current.AutomaticHandledBySphene = false;
            _shrinkuConfigService.Current.AutomaticControllerName = string.Empty;
            _shrinkuConfigService.Save();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to clear ShrinkU automatic controller settings on StopAsync"); }
        return Task.CompletedTask;
    }

    // Apply a runtime toggle to add or remove ShrinkU windows
    public void ApplyIntegrationEnabled(bool enabled)
    {
        try
        {
            _logger.LogDebug("Applying ShrinkU integration toggle: enabled={enabled}", enabled);
            if (enabled)
            {
                RegisterWindows();
                SetPenumbraIntegration(true);
                try
                {
                    if (_shrinkuConfigService.Current.FirstRunCompleted)
                        _conversionUi.IsOpen = true;
                    else
                        _firstRunUi.IsOpen = true;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to open ShrinkU window on enable"); }
            }
            else
            {
                try { _refreshCts?.Cancel(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel refresh CTS"); }
                try { _refreshCts?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose refresh CTS"); }
                _refreshCts = null;
                UnregisterWindows();
                SetPenumbraIntegration(false);
                try { _shrinkuConversionService.SetEnabled(false); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to disable ShrinkU conversion"); }
                try
                {
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = false;
                    _shrinkuConfigService.Current.AutomaticControllerName = string.Empty;
                    _shrinkuConfigService.Save();
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to clear ShrinkU automatic controller settings on disable"); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply ShrinkU integration toggle");
        }
    }

    private void RegisterWindows()
    {
        _logger.LogDebug("Registering ShrinkU windows in Sphene WindowSystem");
        try { _windowSystem.AddWindow(_conversionUi); } catch (Exception ex) { _logger.LogDebug(ex, "ConversionUI already registered or failed to add"); }
        try { _windowSystem.AddWindow(_settingsUi); } catch (Exception ex) { _logger.LogDebug(ex, "SettingsUI already registered or failed to add"); }
        try { _windowSystem.AddWindow(_firstRunUi); } catch (Exception ex) { _logger.LogDebug(ex, "FirstRunSetupUI already registered or failed to add"); }
        try { _windowSystem.AddWindow(_releaseChangelogUi); } catch (Exception ex) { _logger.LogDebug(ex, "ReleaseChangelogUI already registered or failed to add"); }
        try { _windowSystem.AddWindow(_debugUi); } catch (Exception ex) { _logger.LogDebug(ex, "DebugUI already registered or failed to add"); }
        try { _windowSystem.AddWindow(_startupProgressUi); } catch (Exception ex) { _logger.LogDebug(ex, "StartupProgressUI already registered or failed to add"); }
        _registered = true;
    }

    private void UnregisterWindows()
    {
        _logger.LogDebug("Removing ShrinkU windows from Sphene WindowSystem");
        try { _windowSystem.RemoveWindow(_conversionUi); } catch (Exception ex) { _logger.LogDebug(ex, "ConversionUI not registered or failed to remove"); }
        try { _windowSystem.RemoveWindow(_settingsUi); } catch (Exception ex) { _logger.LogDebug(ex, "SettingsUI not registered or failed to remove"); }
        try { _windowSystem.RemoveWindow(_firstRunUi); } catch (Exception ex) { _logger.LogDebug(ex, "FirstRunSetupUI not registered or failed to remove"); }
        try { _windowSystem.RemoveWindow(_releaseChangelogUi); } catch (Exception ex) { _logger.LogDebug(ex, "ReleaseChangelogUI not registered or failed to remove"); }
            try { _windowSystem.RemoveWindow(_debugUi); } catch (Exception ex) { _logger.LogDebug(ex, "DebugUI not registered or failed to remove"); }
            _registered = false;
            // Clear integration marker so ShrinkU exposes generic Automatic mode when not hosted
            try
            {
                _shrinkuConfigService.Current.AutomaticControllerName = string.Empty;
                _shrinkuConfigService.Current.AutomaticHandledBySphene = false;
                _shrinkuConfigService.Save();
                _logger.LogDebug("Cleared ShrinkU AutomaticControllerName on integration stop");
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed clearing ShrinkU automatic controller settings"); }
    }

    // Open ShrinkU release notes window from Sphene settings
    public void OpenReleaseNotes()
    {
        try
        {
            // Ensure windows are registered when integration is enabled
            if (!_registered && _configService.Current.EnableShrinkUIntegration)
                RegisterWindows();

            _releaseChangelogUi.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to open ShrinkU release notes");
        }
    }

    public void OpenConversion()
    {
        try
        {
            if (!_registered && _configService.Current.EnableShrinkUIntegration)
                RegisterWindows();
            _conversionUi.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to open ShrinkU ConversionUI");
        }
    }

    public void OpenConversionForMods(IEnumerable<string> mods)
    {
        try
        {
            if (!_registered && _configService.Current.EnableShrinkUIntegration)
                RegisterWindows();
            _conversionUi.OpenForMods(mods);
            _conversionUi.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to open ShrinkU ConversionUI for mods");
        }
    }

    // Configure ShrinkU backup folder to Sphene's cache when available
    private void TryConfigureShrinkUBackupPath()
    {
        try
        {
            var cache = _configService.Current.CacheFolder ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cache) || !Directory.Exists(cache))
                return;

            var target = Path.Combine(cache, "texture_backups");
            var current = _shrinkuConfigService.Current.BackupFolderPath ?? string.Empty;
            var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShrinkU", "Backups");
            var isDefault = string.Equals(current, defaultPath, StringComparison.OrdinalIgnoreCase);

            if (!_shrinkuConfigService.Current.FirstRunCompleted || isDefault || string.IsNullOrWhiteSpace(current))
            {
                try { Directory.CreateDirectory(target); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to ensure backup target directory"); }
                _shrinkuConfigService.Current.BackupFolderPath = target;
                _shrinkuConfigService.Current.FirstRunCompleted = true;
                try { _shrinkuConfigService.Save(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to save ShrinkU backup path configuration"); }
                _logger.LogDebug("Configured ShrinkU backup path to Sphene texture_backups on setup completion: {path}", target);
            }
            else
            {
                try
                {
                    try { Directory.CreateDirectory(target); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to ensure backup target directory"); }
                    var test = Path.Combine(target, ".shrinku_write_test.tmp");
                    try { File.WriteAllText(test, "ok"); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to write ShrinkU backup write test file"); }
                    try { if (File.Exists(test)) File.Delete(test); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete ShrinkU backup write test file"); }
                }
                catch
                {
                    try { Directory.CreateDirectory(target); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to ensure backup target directory after write test failure"); }
                    _shrinkuConfigService.Current.BackupFolderPath = target;
                    try { _shrinkuConfigService.Save(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to save ShrinkU backup path configuration after write test failure"); }
                    _logger.LogDebug("Backup path not writable; switched to Sphene texture_backups: {path}", target);
                }
            }

            try
            {
                var finalPath = _shrinkuConfigService.Current.BackupFolderPath ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(finalPath))
                    _backupService.ConfigureBackupFolderPath(finalPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to sync ShrinkU backup folder path to backup service");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to configure ShrinkU backup path on setup completion");
        }
    }

    // React to Sphene configuration saves to finalize ShrinkU setup and ensure windows are registered
    private void OnConfigSaved(object? sender, EventArgs e)
    {
        TryConfigureShrinkUBackupPath();
        try
        {
            var enable = _configService.Current.EnableShrinkUIntegration;
            if (enable)
            {
                RegisterWindows();
                SetPenumbraIntegration(true);
                try
                {
                    _shrinkuConfigService.Current.AutomaticControllerName = "Sphene";
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = true;
                    _shrinkuConfigService.Save();
                    _logger.LogDebug("Set ShrinkU AutomaticControllerName to Sphene on config save");
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed setting ShrinkU automatic controller settings on enable"); }
                CleanupDuplicateShrinkUConfig();
            }
            else
            {
                UnregisterWindows();
                SetPenumbraIntegration(false);
                try
                {
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = false;
                    _shrinkuConfigService.Current.AutomaticControllerName = string.Empty;
                    _shrinkuConfigService.Save();
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed clearing ShrinkU automatic controller settings on disable"); }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed handling ShrinkU OnConfigSaved"); }
    }

    private void CleanupDuplicateShrinkUConfig()
    {
        try
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs");
            var shrinkPath = Path.Combine(baseDir, "ShrinkU.json");
            if (!File.Exists(shrinkPath))
                return;
            var spheneRootPath = Path.Combine(baseDir, "Sphene.json");
            var spheneSubPath = Path.Combine(baseDir, "Sphene", "Sphene.json");
            try { if (File.Exists(spheneRootPath)) File.Delete(spheneRootPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed deleting Sphene root config during ShrinkU cleanup"); }
            try { if (File.Exists(spheneSubPath)) File.Delete(spheneSubPath); } catch (Exception ex) { _logger.LogDebug(ex, "Failed deleting Sphene subpath config during ShrinkU cleanup"); }
            _logger.LogDebug("CleanupDuplicateShrinkUConfig executed");
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed cleaning up duplicate ShrinkU config"); }
    }

    public void Dispose()
    {
        _penumbraExtension?.Dispose();
    }
}
