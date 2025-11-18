using Dalamud.Bindings.ImGui;
using Sphene.UI.Styling;
using Microsoft.Extensions.Logging;
using System;

namespace Sphene.Services;

public class SpheneThemeService
{
    private readonly ILogger<SpheneThemeService> _logger;
    
    public SpheneThemeService(ILogger<SpheneThemeService> logger)
    {
        _logger = logger;
    }

    // Apply Sphene theme to a window if it's not the CompactUI
    public IDisposable? ApplyThemeToWindow(Type windowType)
    {
        // Skip theme for CompactUI and external plugins like ShrinkU
        var asmName = windowType.Assembly.GetName().Name ?? string.Empty;
        var ns = windowType.Namespace ?? string.Empty;
        if (windowType.Name == "CompactUi" || asmName.Equals("ShrinkU", StringComparison.OrdinalIgnoreCase) || ns.StartsWith("ShrinkU", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping Sphene theme for window: {WindowType} (assembly={Assembly}, ns={Namespace})", windowType.Name, asmName, ns);
            return null;
        }

        _logger.LogDebug("Applying Sphene theme to window: {WindowType}", windowType.Name);
        return SpheneUIEnhancements.ApplySpheneWindowTheme();
    }

    // Check if a window type should have theme applied
    public bool ShouldApplyTheme(Type windowType)
    {
        var asmName = windowType.Assembly.GetName().Name ?? string.Empty;
        var ns = windowType.Namespace ?? string.Empty;
        return windowType.Name != "CompactUi" && !asmName.Equals("ShrinkU", StringComparison.OrdinalIgnoreCase) && !ns.StartsWith("ShrinkU", StringComparison.OrdinalIgnoreCase);
    }
}