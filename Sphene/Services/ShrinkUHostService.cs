using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private bool _registered;

    public ShrinkUHostService(
        ILogger<ShrinkUHostService> logger,
        WindowSystem windowSystem,
        ConversionUI conversionUi,
        SettingsUI settingsUi,
        FirstRunSetupUI firstRunUi,
        SpheneConfigService configService)
    {
        _logger = logger;
        _windowSystem = windowSystem;
        _conversionUi = conversionUi;
        _settingsUi = settingsUi;
        _firstRunUi = firstRunUi;
        _configService = configService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
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
        if (_registered)
            return;
        _logger.LogDebug("Registering ShrinkU windows in Sphene WindowSystem");
        _windowSystem.AddWindow(_conversionUi);
        _windowSystem.AddWindow(_settingsUi);
        _windowSystem.AddWindow(_firstRunUi);
        _registered = true;
    }

    private void UnregisterWindows()
    {
        if (!_registered)
            return;
        _logger.LogDebug("Removing ShrinkU windows from Sphene WindowSystem");
        _windowSystem.RemoveWindow(_conversionUi);
        _windowSystem.RemoveWindow(_settingsUi);
        _windowSystem.RemoveWindow(_firstRunUi);
        _registered = false;
    }
}