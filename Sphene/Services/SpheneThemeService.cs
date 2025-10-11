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
        // Skip theme application for CompactUI to maintain its original styling
        if (windowType.Name == "CompactUi")
        {
            _logger.LogDebug("Skipping theme application for CompactUI window");
            return null;
        }

        _logger.LogDebug("Applying Sphene theme to window: {WindowType}", windowType.Name);
        return SpheneUIEnhancements.ApplySpheneWindowTheme();
    }

    // Check if a window type should have theme applied
    public bool ShouldApplyTheme(Type windowType)
    {
        return windowType.Name != "CompactUi";
    }
}