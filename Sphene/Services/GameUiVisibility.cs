using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Microsoft.Extensions.Logging;

namespace Sphene.Services;

/// <summary>
/// Hides the game's own HUD (hotbars, chat, nameplates) so a screen capture
/// comes out clean. SetUiVisibility is game state and must run on the
/// framework thread - callers on any other thread are marshalled over. While
/// hidden the user's own UI-hide keybind is suppressed via
/// <see cref="IUiBuilder.DisableUserUiHide"/> so it cannot fight the
/// capture overlay. Always restored through <see cref="Restore"/>; Dispose is
/// the unload-time safety net.
/// </summary>
public sealed class GameUiVisibility : IDisposable
{
    private readonly ILogger<GameUiVisibility> _logger;
    private readonly IFramework _framework;
    private readonly IDalamudPluginInterface _pluginInterface;
    private bool _hidden;
    private bool _previousDisableUserUiHide;
    private bool _disposed;

    public GameUiVisibility(ILogger<GameUiVisibility> logger, IFramework framework,
        IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _framework = framework;
        _pluginInterface = pluginInterface;
    }

    /// <summary>Whether the game HUD is currently hidden by this service.</summary>
    public bool Hidden => _hidden;

    public void Hide()
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            HideNow();
            return;
        }
        _ = _framework.RunOnFrameworkThread(HideNow);
    }

    public void Restore()
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            RestoreNow();
            return;
        }
        _ = _framework.RunOnFrameworkThread(RestoreNow);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            Restore();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to restore game UI on dispose");
        }
    }

    private unsafe void HideNow()
    {
        if (_disposed || _hidden)
        {
            return;
        }
        try
        {
            var atkModule = RaptureAtkModule.Instance();
            if (atkModule == null || !atkModule->IsUiVisible)
            {
                return;
            }
            _previousDisableUserUiHide = _pluginInterface.UiBuilder.DisableUserUiHide;
            _pluginInterface.UiBuilder.DisableUserUiHide = true;
            _hidden = true;
            atkModule->SetUiVisibility(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to hide game UI");
        }
    }

    private unsafe void RestoreNow()
    {
        if (!_hidden)
        {
            return;
        }
        _hidden = false;
        try
        {
            var atkModule = RaptureAtkModule.Instance();
            if (atkModule != null)
            {
                atkModule->SetUiVisibility(true);
            }
            _pluginInterface.UiBuilder.DisableUserUiHide = _previousDisableUserUiHide;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to restore game UI");
        }
    }
}
