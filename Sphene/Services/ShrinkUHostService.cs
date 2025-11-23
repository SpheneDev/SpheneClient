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

namespace Sphene.Services;

public sealed class ShrinkUHostService : IHostedService
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
    private readonly Sphene.Interop.Ipc.IpcManager _ipcManager;
    private CancellationTokenSource? _refreshCts;

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
        Sphene.Interop.Ipc.IpcManager ipcManager)
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
        _ipcManager = ipcManager;

        try { _configService.ConfigSave += OnConfigSaved; } catch { }
    }

    public bool IsConversionUiOpen
    {
        get
        {
            try { return _conversionUi != null && _conversionUi.IsOpen; } catch { return false; }
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
                // Mark ShrinkU as integrated with Sphene for UI gating
                try
                {
                    _shrinkuConfigService.Current.AutomaticControllerName = "Sphene";
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = true;
                    _shrinkuConfigService.Save();
                    _logger.LogDebug("Set ShrinkU AutomaticControllerName to Sphene on integration start");
                }
                catch { }
                CleanupDuplicateShrinkUConfig();
                try { _conversionUi.GetType().GetMethod("SetStartupRefreshInProgress", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.Invoke(_conversionUi, new object[] { true }); } catch { }
                try { _shrinkuConversionService.SetEnabled(true); } catch { }
            }
            if (enable)
            {
                try
                {
                    _refreshCts?.Cancel();
                    _refreshCts = new CancellationTokenSource();
                    var token = _refreshCts.Token;
                    try { _startupProgressUi.ResetAll(); _startupProgressUi.IsOpen = true; } catch { }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            try { _logger.LogDebug("ShrinkU startup via Sphene: skipping Penumbra wait"); } catch { }
                            try { _backupService.SetSavingEnabled(false); } catch { }
                            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                            if (token.IsCancellationRequested) return;
                            try { _startupProgressUi.SetStep(1); } catch { }
                            var maxBackupSeconds = 5;
                            var refreshTask = _backupService.RefreshAllBackupStateAsync();
                            var completed = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(maxBackupSeconds), token)).ConfigureAwait(false);
                            if (completed != refreshTask)
                            {
                                _logger.LogWarning("Backup refresh timed out after {sec}s; continuing", maxBackupSeconds);
                            }
                            else
                            {
                                try { await refreshTask.ConfigureAwait(false); } catch { }
                            }
                            try { _startupProgressUi.MarkBackupDone(); _startupProgressUi.SetStep(3); } catch { }
                            await _backupService.PopulateMissingOriginalBytesAsync(token).ConfigureAwait(false);
                            try { _startupProgressUi.SetStep(4); } catch { }
                            try { await _shrinkuConversionService.UpdateAllModUsedTextureFilesAsync().ConfigureAwait(false); } catch { }
                            try { _startupProgressUi.MarkUsedDone(); _startupProgressUi.SetStep(5); } catch { }
                            try { _backupService.SetSavingEnabled(true); } catch { }
                            try { _backupService.SaveModState(); } catch { }
                            try { _startupProgressUi.MarkSaveDone(); } catch { }
                            try { _conversionUi.TriggerStartupRescan(); } catch { }
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
                            try { _conversionUi.SetStartupRefreshInProgress(false); } catch { }
                            try { _startupProgressUi.IsOpen = false; } catch { }
                        }
                    }, token);
                }
                catch { }
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
            try { _conversionUi.DisableModStateSaving(); } catch { }
            try { _conversionUi.ShutdownBackgroundWork(); } catch { }
            UnregisterWindows();
        }
        catch
        {
            // ignore
        }
        try { _refreshCts?.Cancel(); } catch { }
        try { _refreshCts?.Dispose(); } catch { }
        try { _configService.ConfigSave -= OnConfigSaved; } catch { }
        try
        {
            _shrinkuConfigService.Current.AutomaticHandledBySphene = false;
            _shrinkuConfigService.Current.AutomaticControllerName = string.Empty;
            _shrinkuConfigService.Save();
        }
        catch { }
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
                try
                {
                    if (_shrinkuConfigService.Current.FirstRunCompleted)
                        _conversionUi.IsOpen = true;
                    else
                        _firstRunUi.IsOpen = true;
                }
                catch { }
            }
            else
            {
                try { _refreshCts?.Cancel(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                _refreshCts = null;
                UnregisterWindows();
                try { _shrinkuConversionService.SetEnabled(false); } catch { }
                try
                {
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = false;
                    _shrinkuConfigService.Current.AutomaticControllerName = string.Empty;
                    _shrinkuConfigService.Save();
                }
                catch { }
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
            catch { }
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

            if (_shrinkuConfigService.Current.FirstRunCompleted == false || isDefault || string.IsNullOrWhiteSpace(current))
            {
                try { Directory.CreateDirectory(target); } catch { }
                _shrinkuConfigService.Current.BackupFolderPath = target;
                _shrinkuConfigService.Current.FirstRunCompleted = true;
                try { _shrinkuConfigService.Save(); } catch { }
                _logger.LogDebug("Configured ShrinkU backup path to Sphene texture_backups on setup completion: {path}", target);
            }
            else
            {
                try
                {
                    try { Directory.CreateDirectory(target); } catch { }
                    var test = Path.Combine(target, ".shrinku_write_test.tmp");
                    try { File.WriteAllText(test, "ok"); } catch { }
                    try { if (File.Exists(test)) File.Delete(test); } catch { }
                }
                catch
                {
                    try { Directory.CreateDirectory(target); } catch { }
                    _shrinkuConfigService.Current.BackupFolderPath = target;
                    try { _shrinkuConfigService.Save(); } catch { }
                    _logger.LogDebug("Backup path not writable; switched to Sphene texture_backups: {path}", target);
                }
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
                try
                {
                    _shrinkuConfigService.Current.AutomaticControllerName = "Sphene";
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = true;
                    _shrinkuConfigService.Save();
                    _logger.LogDebug("Set ShrinkU AutomaticControllerName to Sphene on config save");
                }
                catch { }
                CleanupDuplicateShrinkUConfig();
            }
            else
            {
                UnregisterWindows();
                try
                {
                    _shrinkuConfigService.Current.AutomaticHandledBySphene = false;
                    _shrinkuConfigService.Current.AutomaticControllerName = string.Empty;
                    _shrinkuConfigService.Save();
                }
                catch { }
            }
        }
        catch { }
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
            try { if (File.Exists(spheneRootPath)) File.Delete(spheneRootPath); } catch { }
            try { if (File.Exists(spheneSubPath)) File.Delete(spheneSubPath); } catch { }
            _logger.LogDebug("CleanupDuplicateShrinkUConfig executed");
        }
        catch { }
    }
}
