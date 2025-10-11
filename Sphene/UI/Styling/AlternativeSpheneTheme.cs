using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Styling;

/// <summary>
/// Alternative Sphene theme application for non-CompactUI windows
/// Provides warmer, more harmonious colors for better user experience
/// </summary>
public static class AlternativeSpheneTheme
{
    /// <summary>
    /// Applies the alternative Sphene theme to the current window
    /// Returns a disposable that restores original colors when disposed
    /// </summary>
    public static IDisposable ApplyAlternativeTheme()
    {
        var style = ImGui.GetStyle();
        var colors = style.Colors;
        
        // Store original colors for restoration
        var originalColors = new Dictionary<ImGuiCol, Vector4>();
        
        // Apply refined Sphene theme colors - similar to original but with transparent child containers
        var themeColors = new Dictionary<ImGuiCol, Vector4>
        {
            // Window backgrounds - keep original Sphene style
            { ImGuiCol.WindowBg, SpheneColors.BackgroundDark },
            { ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0) }, // Completely transparent child containers
            { ImGuiCol.PopupBg, SpheneColors.WithAlpha(SpheneColors.BackgroundDark, 0.95f) }, // Semi-transparent dropdown background
            
            // Borders - original Sphene style
            { ImGuiCol.Border, SpheneColors.BorderColor },
            { ImGuiCol.BorderShadow, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.2f) },
            
            // Frame backgrounds - simplified and harmonized
            { ImGuiCol.FrameBg, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.3f) },
            { ImGuiCol.FrameBgHovered, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.5f) },
            { ImGuiCol.FrameBgActive, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.7f) },
            
            // Title bars - unified blue tones with darker active header for better button visibility
            { ImGuiCol.TitleBg, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.8f) },
            { ImGuiCol.TitleBgActive, SpheneColors.WithAlpha(SpheneColors.BackgroundDark, 0.9f) },
            { ImGuiCol.TitleBgCollapsed, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.6f) },
            
            // Menu and scrollbars - consistent with main theme
            { ImGuiCol.MenuBarBg, SpheneColors.BackgroundMid },
            { ImGuiCol.ScrollbarBg, SpheneColors.BackgroundDark },
            { ImGuiCol.ScrollbarGrab, SpheneColors.DeepCrystal },
            { ImGuiCol.ScrollbarGrabHovered, SpheneColors.CrystalBlue },
            { ImGuiCol.ScrollbarGrabActive, SpheneColors.CrystalBlue },
            
            // Interactive elements - simplified to blue tones only
            { ImGuiCol.CheckMark, SpheneColors.CrystalBlue },
            { ImGuiCol.SliderGrab, SpheneColors.DeepCrystal },
            { ImGuiCol.SliderGrabActive, SpheneColors.CrystalBlue },
            
            // Buttons - unified blue progression
            { ImGuiCol.Button, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.7f) },
            { ImGuiCol.ButtonHovered, SpheneColors.DeepCrystal },
            { ImGuiCol.ButtonActive, SpheneColors.CrystalBlue },
            
            // Headers - simplified blue tones
            { ImGuiCol.Header, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.4f) },
            { ImGuiCol.HeaderHovered, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.6f) },
            { ImGuiCol.HeaderActive, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.8f) },
            
            // Separators - consistent blue theme
            { ImGuiCol.Separator, SpheneColors.BorderColor },
            { ImGuiCol.SeparatorHovered, SpheneColors.DeepCrystal },
            { ImGuiCol.SeparatorActive, SpheneColors.CrystalBlue },
            
            // Resize grips - unified blue progression
            { ImGuiCol.ResizeGrip, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.3f) },
            { ImGuiCol.ResizeGripHovered, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.6f) },
            { ImGuiCol.ResizeGripActive, SpheneColors.DeepCrystal },
            
            // Tabs - simplified blue variations
            { ImGuiCol.Tab, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.5f) },
            { ImGuiCol.TabHovered, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.8f) },
            { ImGuiCol.TabActive, SpheneColors.CrystalBlue },
            { ImGuiCol.TabUnfocused, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.3f) },
            { ImGuiCol.TabUnfocusedActive, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.6f) },
            
            // Plots and graphs - simplified blue theme
            { ImGuiCol.PlotLines, SpheneColors.CrystalBlue },
            { ImGuiCol.PlotLinesHovered, SpheneColors.CrystalBlue },
            { ImGuiCol.PlotHistogram, SpheneColors.DeepCrystal },
            { ImGuiCol.PlotHistogramHovered, SpheneColors.CrystalBlue },
            
            // Tables - original Sphene style
            { ImGuiCol.TableHeaderBg, SpheneColors.BackgroundMid },
            { ImGuiCol.TableBorderStrong, SpheneColors.BorderColor },
            { ImGuiCol.TableBorderLight, SpheneColors.WithAlpha(SpheneColors.BorderColor, 0.5f) },
            { ImGuiCol.TableRowBg, SpheneColors.WithAlpha(SpheneColors.BackgroundMid, 0.0f) },
            { ImGuiCol.TableRowBgAlt, SpheneColors.WithAlpha(SpheneColors.BackgroundMid, 0.3f) },
            
            // Selection and navigation - unified blue theme
            { ImGuiCol.TextSelectedBg, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.4f) },
            { ImGuiCol.DragDropTarget, SpheneColors.CrystalBlue },
            { ImGuiCol.NavHighlight, SpheneColors.CrystalBlue },
            { ImGuiCol.NavWindowingHighlight, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.7f) },
            { ImGuiCol.NavWindowingDimBg, SpheneColors.WithAlpha(SpheneColors.BackgroundDark, 0.2f) },
            { ImGuiCol.ModalWindowDimBg, SpheneColors.WithAlpha(SpheneColors.BackgroundDark, 0.6f) }
        };
        
        // Apply colors and store originals
        foreach (var (colorType, color) in themeColors)
        {
            originalColors[colorType] = colors[(int)colorType];
            colors[(int)colorType] = color;
        }
        
        // Return disposable to restore original colors
        return new AlternativeColorRestorer(originalColors);
    }
    
    /// <summary>
    /// Disposable class that restores original ImGui colors when disposed
    /// </summary>
    private class AlternativeColorRestorer : IDisposable
    {
        private readonly Dictionary<ImGuiCol, Vector4> _originalColors;
        
        public AlternativeColorRestorer(Dictionary<ImGuiCol, Vector4> originalColors)
        {
            _originalColors = originalColors;
        }
        
        public void Dispose()
        {
            var colors = ImGui.GetStyle().Colors;
            foreach (var (colorType, originalColor) in _originalColors)
            {
                colors[(int)colorType] = originalColor;
            }
        }
    }
}