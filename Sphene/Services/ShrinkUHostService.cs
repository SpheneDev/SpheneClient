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
    private readonly FirstRunSetupUI _firstRunUi;
    private readonly SpheneConfigService _configService;
    private readonly ShrinkUConfigService _shrinkuConfigService;
    private bool _registered;

    public ShrinkUHostService(
        ILogger<ShrinkUHostService> logger,
        WindowSystem windowSystem,
        ConversionUI conversionUi,
        SettingsUI settingsUi,
        FirstRunSetupUI firstRunUi,
        SpheneConfigService configService,
        ShrinkUConfigService shrinkuConfigService)
    {
        _logger = logger;
        _windowSystem = windowSystem;
        _conversionUi = conversionUi;
        _settingsUi = settingsUi;
        _firstRunUi = firstRunUi;
        _configService = configService;
        _shrinkuConfigService = shrinkuConfigService;

        try { _configService.ConfigSave += OnConfigSaved; } catch { }
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
            UnregisterWindows();
        }
        catch
        {
            // ignore
        }
        try { _configService.ConfigSave -= OnConfigSaved; } catch { }
        return Task.CompletedTask;
    }

    // Apply a runtime toggle to add or remove ShrinkU windows
    public void ApplyIntegrationEnabled(bool enabled)
    {
        try
        {
            _logger.LogDebug("Applying ShrinkU integration toggle: enabled={enabled}", enabled);
            if (enabled)
                RegisterWindows();
            else
                UnregisterWindows();
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
        _registered = true;
    }

    private void UnregisterWindows()
    {
        _logger.LogDebug("Removing ShrinkU windows from Sphene WindowSystem");
        try { _windowSystem.RemoveWindow(_conversionUi); } catch (Exception ex) { _logger.LogDebug(ex, "ConversionUI not registered or failed to remove"); }
        try { _windowSystem.RemoveWindow(_settingsUi); } catch (Exception ex) { _logger.LogDebug(ex, "SettingsUI not registered or failed to remove"); }
        try { _windowSystem.RemoveWindow(_firstRunUi); } catch (Exception ex) { _logger.LogDebug(ex, "FirstRunSetupUI not registered or failed to remove"); }
        _registered = false;
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
                RegisterWindows();
            else
                UnregisterWindows();
        }
        catch { }
    }
}