using System;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;
using Sphene.UI.Styling;

namespace Sphene.UI.Theme;

public static class SpheneCustomTheme
{
    // Original Sphene color palette - matching the Sphene card design
    public static class Colors
    {
        // Use original Sphene colors from SpheneColors.cs with proper alpha values for translucent blue-purple appearance
        public static readonly Vector4 PrimaryDark = SpheneColors.BackgroundMid;        // BackgroundMid with alpha 0.8f for translucent effect
        public static readonly Vector4 SecondaryDark = SpheneColors.BackgroundMid;      // BackgroundMid with alpha 0.8f for consistency
        public static readonly Vector4 AccentBlue = SpheneColors.DeepCrystal;               // Deep crystal blue
        public static readonly Vector4 AccentCyan = SpheneColors.CrystalBlue;               // Crystal blue highlight
        public static readonly Vector4 TextPrimary = SpheneColors.CrystalBlue;              // Primary text
        public static readonly Vector4 TextSecondary = SpheneColors.TextSecondary;          // Secondary text
        public static readonly Vector4 Border = SpheneColors.BorderColor;                   // Border color
        public static readonly Vector4 Hover = SpheneColors.HoverBlue;                      // Hover effect
        public static readonly Vector4 Active = SpheneColors.SelectionBlue;                 // Active state
        public static readonly Vector4 Success = SpheneColors.NetworkActive;                // Success green
        public static readonly Vector4 Warning = SpheneColors.NetworkWarning;               // Warning yellow
        public static readonly Vector4 Error = SpheneColors.NetworkError;                   // Error red
        public static readonly Vector4 HeaderBg = SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.3f); // Header background
        public static readonly Vector4 EtherealGlow = SpheneColors.EtherGlow;               // Ethereal glow
        public static readonly Vector4 SpheneGold = SpheneColors.SpheneGold;                // Golden accent
    }
    
    // Global theme configuration instance
    public static ThemeConfiguration CurrentTheme { get; set; } = new ThemeConfiguration();
    
    // Utility method to apply theme only for a specific window using Push/Pop approach
    public static IDisposable ApplyThemeForWindow()
    {
        return new ThemeScope();
    }
    
    // Utility method to apply theme with reduced window radius for all windows except CompactUI
    public static IDisposable ApplyThemeWithReducedRadius()
    {
        return new ReducedRadiusThemeScope();
    }
    
    // Utility method to apply theme with original window radius for CompactUI
    public static IDisposable ApplyThemeWithOriginalRadius()
    {
        return new OriginalRadiusThemeScope();
    }
    
    // Utility method to apply tooltip-specific styling for CompactUI
    public static IDisposable ApplyTooltipTheme()
    {
        return new TooltipThemeScope();
    }
    
    // Utility method to apply context menu-specific styling for CompactUI
    public static IDisposable ApplyContextMenuTheme()
    {
        return new ContextMenuThemeScope();
    }
    
    // Apply theme colors globally to ImGui style
    public static void ApplyThemeGlobally()
    {
        var style = ImGui.GetStyle();
        var colors = style.Colors;
        
        // Apply all theme colors to the global ImGui style
        colors[(int)ImGuiCol.WindowBg] = CurrentTheme.WindowBg;
        colors[(int)ImGuiCol.ChildBg] = CurrentTheme.ChildBg;
        colors[(int)ImGuiCol.PopupBg] = CurrentTheme.PopupBg;
        colors[(int)ImGuiCol.Border] = CurrentTheme.Border;
        colors[(int)ImGuiCol.BorderShadow] = CurrentTheme.BorderShadow;
        
        // Title bar colors
        colors[(int)ImGuiCol.TitleBg] = CurrentTheme.TitleBg;
        colors[(int)ImGuiCol.TitleBgActive] = CurrentTheme.TitleBgActive;
        colors[(int)ImGuiCol.TitleBgCollapsed] = CurrentTheme.TitleBgCollapsed;
        
        // Menu bar
        colors[(int)ImGuiCol.MenuBarBg] = CurrentTheme.MenuBarBg;
        
        // Text colors
        colors[(int)ImGuiCol.Text] = CurrentTheme.TextPrimary;
        colors[(int)ImGuiCol.TextDisabled] = CurrentTheme.TextSecondary;
        colors[(int)ImGuiCol.TextSelectedBg] = CurrentTheme.TextSelectedBg;
        
        // Button colors
        colors[(int)ImGuiCol.Button] = CurrentTheme.Button;
        colors[(int)ImGuiCol.ButtonHovered] = CurrentTheme.ButtonHovered;
        colors[(int)ImGuiCol.ButtonActive] = CurrentTheme.ButtonActive;
        
        // Header colors
        colors[(int)ImGuiCol.Header] = CurrentTheme.Header;
        colors[(int)ImGuiCol.HeaderHovered] = CurrentTheme.HeaderHovered;
        colors[(int)ImGuiCol.HeaderActive] = CurrentTheme.HeaderActive;
        
        // Frame colors
        colors[(int)ImGuiCol.FrameBg] = CurrentTheme.FrameBg;
        colors[(int)ImGuiCol.FrameBgHovered] = CurrentTheme.FrameBgHovered;
        colors[(int)ImGuiCol.FrameBgActive] = CurrentTheme.FrameBgActive;
        
        // Scrollbar colors
        colors[(int)ImGuiCol.ScrollbarBg] = CurrentTheme.ScrollbarBg;
        colors[(int)ImGuiCol.ScrollbarGrab] = CurrentTheme.ScrollbarGrab;
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = CurrentTheme.ScrollbarGrabHovered;
        colors[(int)ImGuiCol.ScrollbarGrabActive] = CurrentTheme.ScrollbarGrabActive;
        
        // Check mark
        colors[(int)ImGuiCol.CheckMark] = CurrentTheme.CheckMark;
        
        // Slider colors
        colors[(int)ImGuiCol.SliderGrab] = CurrentTheme.SliderGrab;
        colors[(int)ImGuiCol.SliderGrabActive] = CurrentTheme.SliderGrabActive;
        
        // Separator colors
        colors[(int)ImGuiCol.Separator] = CurrentTheme.Separator;
        colors[(int)ImGuiCol.SeparatorHovered] = CurrentTheme.SeparatorHovered;
        colors[(int)ImGuiCol.SeparatorActive] = CurrentTheme.SeparatorActive;
        
        // Resize grip colors
        colors[(int)ImGuiCol.ResizeGrip] = CurrentTheme.ResizeGrip;
        colors[(int)ImGuiCol.ResizeGripHovered] = CurrentTheme.ResizeGripHovered;
        colors[(int)ImGuiCol.ResizeGripActive] = CurrentTheme.ResizeGripActive;
        
        // Tab colors
        colors[(int)ImGuiCol.Tab] = CurrentTheme.Tab;
        colors[(int)ImGuiCol.TabHovered] = CurrentTheme.TabHovered;
        colors[(int)ImGuiCol.TabActive] = CurrentTheme.TabActive;
        colors[(int)ImGuiCol.TabUnfocused] = CurrentTheme.TabUnfocused;
        colors[(int)ImGuiCol.TabUnfocusedActive] = CurrentTheme.TabUnfocusedActive;
        
        // Table colors
        colors[(int)ImGuiCol.TableHeaderBg] = CurrentTheme.TableHeaderBg;
        colors[(int)ImGuiCol.TableBorderStrong] = CurrentTheme.TableBorderStrong;
        colors[(int)ImGuiCol.TableBorderLight] = CurrentTheme.TableBorderLight;
        colors[(int)ImGuiCol.TableRowBg] = CurrentTheme.TableRowBg;
        colors[(int)ImGuiCol.TableRowBgAlt] = CurrentTheme.TableRowBgAlt;
        
        // Drag drop
        colors[(int)ImGuiCol.DragDropTarget] = CurrentTheme.DragDropTarget;
        
        // Navigation colors
        colors[(int)ImGuiCol.NavHighlight] = CurrentTheme.NavHighlight;
        colors[(int)ImGuiCol.NavWindowingHighlight] = CurrentTheme.NavWindowingHighlight;
        colors[(int)ImGuiCol.NavWindowingDimBg] = CurrentTheme.NavWindowingDimBg;
        
        // Modal
        colors[(int)ImGuiCol.ModalWindowDimBg] = CurrentTheme.ModalWindowDimBg;
        
        // Apply style variables as well
        style.WindowRounding = CurrentTheme.WindowRounding;
        style.ChildRounding = CurrentTheme.ChildRounding;
        style.FrameRounding = CurrentTheme.FrameRounding;
        style.PopupRounding = CurrentTheme.PopupRounding;
        style.ScrollbarRounding = CurrentTheme.ScrollbarRounding;
        style.GrabRounding = CurrentTheme.GrabRounding;
        style.TabRounding = CurrentTheme.TabRounding;
        
        style.WindowBorderSize = CurrentTheme.WindowBorderSize;
        style.ChildBorderSize = CurrentTheme.ChildBorderSize;
        style.PopupBorderSize = CurrentTheme.PopupBorderSize;
        style.FrameBorderSize = CurrentTheme.FrameBorderSize;
        
        style.WindowPadding = CurrentTheme.WindowPadding;
        style.FramePadding = CurrentTheme.FramePadding;
        style.ItemSpacing = CurrentTheme.ItemSpacing;
        style.ItemInnerSpacing = CurrentTheme.ItemInnerSpacing;
        style.IndentSpacing = CurrentTheme.IndentSpacing;
        style.ScrollbarSize = CurrentTheme.ScrollbarSize;
        style.GrabMinSize = CurrentTheme.GrabMinSize;
    }
    
    private sealed class ThemeScope : IDisposable
    {
        private int _colorsPushed = 0;
        private int _stylesPushed = 0;
        private bool _disposed;
        
        public ThemeScope()
        {
            ApplyTheme();
        }
        
        private void ApplyTheme()
        {
            // Push all color overrides - using theme configuration colors
            PushColor(ImGuiCol.WindowBg, SpheneCustomTheme.CurrentTheme.WindowBg);
            PushColor(ImGuiCol.ChildBg, SpheneCustomTheme.CurrentTheme.ChildBg);
            PushColor(ImGuiCol.PopupBg, SpheneCustomTheme.CurrentTheme.PopupBg);
            PushColor(ImGuiCol.Border, SpheneCustomTheme.CurrentTheme.Border);
            PushColor(ImGuiCol.BorderShadow, SpheneCustomTheme.CurrentTheme.BorderShadow);
            
            // Title bar - Enhanced Sphene styling matching the card headers
            PushColor(ImGuiCol.TitleBg, SpheneCustomTheme.CurrentTheme.TitleBg);
            PushColor(ImGuiCol.TitleBgActive, SpheneCustomTheme.CurrentTheme.TitleBgActive);
            PushColor(ImGuiCol.TitleBgCollapsed, SpheneCustomTheme.CurrentTheme.TitleBgCollapsed);
            
            // Menu Bar
            PushColor(ImGuiCol.MenuBarBg, SpheneCustomTheme.CurrentTheme.MenuBarBg);
            
            // Text - using theme configuration text colors
            PushColor(ImGuiCol.Text, SpheneCustomTheme.CurrentTheme.TextPrimary);
            PushColor(ImGuiCol.TextDisabled, SpheneCustomTheme.CurrentTheme.TextSecondary);
            PushColor(ImGuiCol.TextSelectedBg, SpheneCustomTheme.CurrentTheme.TextSelectedBg);
            
            // Buttons - subtle styling with theme colors
            PushColor(ImGuiCol.Button, SpheneCustomTheme.CurrentTheme.Button);
            PushColor(ImGuiCol.ButtonHovered, SpheneCustomTheme.CurrentTheme.ButtonHovered);
            PushColor(ImGuiCol.ButtonActive, SpheneCustomTheme.CurrentTheme.ButtonActive);
            
            // Headers (collapsing headers, selectables, etc.) - matching card styling
            PushColor(ImGuiCol.Header, SpheneCustomTheme.CurrentTheme.Header);
            PushColor(ImGuiCol.HeaderHovered, SpheneCustomTheme.CurrentTheme.HeaderHovered);
            PushColor(ImGuiCol.HeaderActive, SpheneCustomTheme.CurrentTheme.HeaderActive);
            
            // Frames (input fields, etc.)
            PushColor(ImGuiCol.FrameBg, SpheneCustomTheme.CurrentTheme.FrameBg);
            PushColor(ImGuiCol.FrameBgHovered, SpheneCustomTheme.CurrentTheme.FrameBgHovered);
            PushColor(ImGuiCol.FrameBgActive, SpheneCustomTheme.CurrentTheme.FrameBgActive);
            
            // Scrollbars
            PushColor(ImGuiCol.ScrollbarBg, SpheneCustomTheme.CurrentTheme.ScrollbarBg);
            PushColor(ImGuiCol.ScrollbarGrab, SpheneCustomTheme.CurrentTheme.ScrollbarGrab);
            PushColor(ImGuiCol.ScrollbarGrabHovered, SpheneCustomTheme.CurrentTheme.ScrollbarGrabHovered);
            PushColor(ImGuiCol.ScrollbarGrabActive, SpheneCustomTheme.CurrentTheme.ScrollbarGrabActive);
            
            // Check marks - using crystal blue
            PushColor(ImGuiCol.CheckMark, SpheneCustomTheme.CurrentTheme.CheckMark);
            
            // Sliders
            PushColor(ImGuiCol.SliderGrab, SpheneCustomTheme.CurrentTheme.SliderGrab);
            PushColor(ImGuiCol.SliderGrabActive, SpheneCustomTheme.CurrentTheme.SliderGrabActive);
            
            // Separators
            PushColor(ImGuiCol.Separator, SpheneCustomTheme.CurrentTheme.Separator);
            PushColor(ImGuiCol.SeparatorHovered, SpheneCustomTheme.CurrentTheme.SeparatorHovered);
            PushColor(ImGuiCol.SeparatorActive, SpheneCustomTheme.CurrentTheme.SeparatorActive);
            
            // Resize grip
            PushColor(ImGuiCol.ResizeGrip, SpheneCustomTheme.CurrentTheme.ResizeGrip);
            PushColor(ImGuiCol.ResizeGripHovered, SpheneCustomTheme.CurrentTheme.ResizeGripHovered);
            PushColor(ImGuiCol.ResizeGripActive, SpheneCustomTheme.CurrentTheme.ResizeGripActive);
            
            // Tabs
            PushColor(ImGuiCol.Tab, SpheneCustomTheme.CurrentTheme.Tab);
            PushColor(ImGuiCol.TabHovered, SpheneCustomTheme.CurrentTheme.TabHovered);
            PushColor(ImGuiCol.TabActive, SpheneCustomTheme.CurrentTheme.TabActive);
            PushColor(ImGuiCol.TabUnfocused, SpheneCustomTheme.CurrentTheme.TabUnfocused);
            PushColor(ImGuiCol.TabUnfocusedActive, SpheneCustomTheme.CurrentTheme.TabUnfocusedActive);
            
            // Table
            PushColor(ImGuiCol.TableHeaderBg, SpheneCustomTheme.CurrentTheme.TableHeaderBg);
            PushColor(ImGuiCol.TableBorderStrong, SpheneCustomTheme.CurrentTheme.TableBorderStrong);
            PushColor(ImGuiCol.TableBorderLight, SpheneCustomTheme.CurrentTheme.TableBorderLight);
            PushColor(ImGuiCol.TableRowBg, SpheneCustomTheme.CurrentTheme.TableRowBg);
            PushColor(ImGuiCol.TableRowBgAlt, SpheneCustomTheme.CurrentTheme.TableRowBgAlt);
            
            // Drag Drop
            PushColor(ImGuiCol.DragDropTarget, SpheneCustomTheme.CurrentTheme.DragDropTarget);
            
            // Navigation
            PushColor(ImGuiCol.NavHighlight, SpheneCustomTheme.CurrentTheme.NavHighlight);
            PushColor(ImGuiCol.NavWindowingHighlight, SpheneCustomTheme.CurrentTheme.NavWindowingHighlight);
            PushColor(ImGuiCol.NavWindowingDimBg, SpheneCustomTheme.CurrentTheme.NavWindowingDimBg);
            
            // Modal
            PushColor(ImGuiCol.ModalWindowDimBg, SpheneCustomTheme.CurrentTheme.ModalWindowDimBg);
            
            // Push style variables
            PushStyleVar(ImGuiStyleVar.WindowRounding, SpheneCustomTheme.CurrentTheme.WindowRounding);
            PushStyleVar(ImGuiStyleVar.ChildRounding, SpheneCustomTheme.CurrentTheme.ChildRounding);
            PushStyleVar(ImGuiStyleVar.FrameRounding, SpheneCustomTheme.CurrentTheme.FrameRounding);
            PushStyleVar(ImGuiStyleVar.PopupRounding, SpheneCustomTheme.CurrentTheme.PopupRounding);
            PushStyleVar(ImGuiStyleVar.ScrollbarRounding, SpheneCustomTheme.CurrentTheme.ScrollbarRounding);
            PushStyleVar(ImGuiStyleVar.GrabRounding, SpheneCustomTheme.CurrentTheme.GrabRounding);
            PushStyleVar(ImGuiStyleVar.TabRounding, SpheneCustomTheme.CurrentTheme.TabRounding);
            
            PushStyleVar(ImGuiStyleVar.WindowBorderSize, SpheneCustomTheme.CurrentTheme.WindowBorderSize);
            PushStyleVar(ImGuiStyleVar.ChildBorderSize, SpheneCustomTheme.CurrentTheme.ChildBorderSize);
            PushStyleVar(ImGuiStyleVar.PopupBorderSize, SpheneCustomTheme.CurrentTheme.PopupBorderSize);
            PushStyleVar(ImGuiStyleVar.FrameBorderSize, SpheneCustomTheme.CurrentTheme.FrameBorderSize);
            
            PushStyleVar(ImGuiStyleVar.WindowPadding, SpheneCustomTheme.CurrentTheme.WindowPadding);
            PushStyleVar(ImGuiStyleVar.FramePadding, SpheneCustomTheme.CurrentTheme.FramePadding);
            PushStyleVar(ImGuiStyleVar.ItemSpacing, SpheneCustomTheme.CurrentTheme.ItemSpacing);
            PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, SpheneCustomTheme.CurrentTheme.ItemInnerSpacing);
            PushStyleVar(ImGuiStyleVar.IndentSpacing, SpheneCustomTheme.CurrentTheme.IndentSpacing);
            PushStyleVar(ImGuiStyleVar.ScrollbarSize, SpheneCustomTheme.CurrentTheme.ScrollbarSize);
            PushStyleVar(ImGuiStyleVar.GrabMinSize, SpheneCustomTheme.CurrentTheme.GrabMinSize);
        }
        
        private void PushColor(ImGuiCol colorId, Vector4 color)
        {
            ImGui.PushStyleColor(colorId, color);
            _colorsPushed++;
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, float value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, Vector2 value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            if (_stylesPushed > 0) ImGui.PopStyleVar(_stylesPushed);
            if (_colorsPushed > 0) ImGui.PopStyleColor(_colorsPushed);
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
    
    private sealed class ReducedRadiusThemeScope : IDisposable
    {
        private int _colorsPushed = 0;
        private int _stylesPushed = 0;
        private bool _disposed;
        
        public ReducedRadiusThemeScope()
        {
            ApplyTheme();
        }
        
        private void ApplyTheme()
        {
            // Push all color overrides - using theme configuration colors
            PushColor(ImGuiCol.WindowBg, SpheneCustomTheme.CurrentTheme.PrimaryDark);
            PushColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0)); // Transparent child containers
            PushColor(ImGuiCol.PopupBg, SpheneCustomTheme.CurrentTheme.PrimaryDark);
            PushColor(ImGuiCol.Border, SpheneCustomTheme.CurrentTheme.Border);
            PushColor(ImGuiCol.BorderShadow, Vector4.Zero);
            
            // Title bar - Enhanced Sphene styling matching the card headers with darker active header for better button visibility
            PushColor(ImGuiCol.TitleBg, SpheneCustomTheme.CurrentTheme.HeaderBg);
            PushColor(ImGuiCol.TitleBgActive, SpheneColors.WithAlpha(SpheneColors.BackgroundDark, 0.9f));
            PushColor(ImGuiCol.TitleBgCollapsed, SpheneCustomTheme.CurrentTheme.SecondaryDark);
            
            // Text - using theme configuration text colors
            PushColor(ImGuiCol.Text, SpheneCustomTheme.CurrentTheme.TextPrimary);
            PushColor(ImGuiCol.TextDisabled, SpheneCustomTheme.CurrentTheme.TextSecondary);
            
            // Buttons - subtle styling with theme colors
            PushColor(ImGuiCol.Button, SpheneCustomTheme.CurrentTheme.SecondaryDark);
            PushColor(ImGuiCol.ButtonHovered, SpheneCustomTheme.CurrentTheme.Hover);
            PushColor(ImGuiCol.ButtonActive, SpheneCustomTheme.CurrentTheme.Active);
            
            // Headers (collapsing headers, selectables, etc.) - matching card styling
            PushColor(ImGuiCol.Header, SpheneCustomTheme.CurrentTheme.HeaderBg);
            PushColor(ImGuiCol.HeaderHovered, SpheneCustomTheme.CurrentTheme.Hover);
            PushColor(ImGuiCol.HeaderActive, SpheneCustomTheme.CurrentTheme.Active);
            
            // Frames (input fields, etc.)
            PushColor(ImGuiCol.FrameBg, SpheneCustomTheme.CurrentTheme.SecondaryDark);
            PushColor(ImGuiCol.FrameBgHovered, SpheneCustomTheme.CurrentTheme.Hover);
            PushColor(ImGuiCol.FrameBgActive, SpheneCustomTheme.CurrentTheme.Active);
            
            // Scrollbars
            PushColor(ImGuiCol.ScrollbarBg, SpheneCustomTheme.CurrentTheme.PrimaryDark);
            PushColor(ImGuiCol.ScrollbarGrab, SpheneCustomTheme.CurrentTheme.AccentBlue);
            PushColor(ImGuiCol.ScrollbarGrabHovered, SpheneCustomTheme.CurrentTheme.Hover);
            PushColor(ImGuiCol.ScrollbarGrabActive, SpheneCustomTheme.CurrentTheme.Active);
            
            // Check marks - using crystal blue
            PushColor(ImGuiCol.CheckMark, SpheneCustomTheme.CurrentTheme.AccentCyan);
            
            // Sliders
            PushColor(ImGuiCol.SliderGrab, SpheneCustomTheme.CurrentTheme.AccentBlue);
            PushColor(ImGuiCol.SliderGrabActive, SpheneCustomTheme.CurrentTheme.Active);
            
            // Separators
            PushColor(ImGuiCol.Separator, SpheneCustomTheme.CurrentTheme.Border);
            PushColor(ImGuiCol.SeparatorHovered, SpheneCustomTheme.CurrentTheme.Hover);
            PushColor(ImGuiCol.SeparatorActive, SpheneCustomTheme.CurrentTheme.Active);
            
            // Resize grip
            PushColor(ImGuiCol.ResizeGrip, SpheneCustomTheme.CurrentTheme.AccentBlue);
            PushColor(ImGuiCol.ResizeGripHovered, SpheneCustomTheme.CurrentTheme.Hover);
            PushColor(ImGuiCol.ResizeGripActive, SpheneCustomTheme.CurrentTheme.Active);
            
            // Tabs
            PushColor(ImGuiCol.Tab, SpheneCustomTheme.CurrentTheme.SecondaryDark);
            PushColor(ImGuiCol.TabHovered, SpheneCustomTheme.CurrentTheme.Hover);
            PushColor(ImGuiCol.TabActive, SpheneCustomTheme.CurrentTheme.AccentBlue);
            PushColor(ImGuiCol.TabUnfocused, SpheneCustomTheme.CurrentTheme.PrimaryDark);
            PushColor(ImGuiCol.TabUnfocusedActive, SpheneCustomTheme.CurrentTheme.SecondaryDark);
            
            // Table
            PushColor(ImGuiCol.TableHeaderBg, SpheneCustomTheme.CurrentTheme.HeaderBg);
            PushColor(ImGuiCol.TableBorderStrong, SpheneCustomTheme.CurrentTheme.Border);
            PushColor(ImGuiCol.TableBorderLight, SpheneCustomTheme.CurrentTheme.Border);
            PushColor(ImGuiCol.TableRowBg, Vector4.Zero);
            PushColor(ImGuiCol.TableRowBgAlt, new Vector4(SpheneCustomTheme.CurrentTheme.SecondaryDark.X, SpheneCustomTheme.CurrentTheme.SecondaryDark.Y, SpheneCustomTheme.CurrentTheme.SecondaryDark.Z, 0.3f));
            
            // Push style variables - using theme configuration values (reduced radius for non-CompactUI)
            PushStyleVar(ImGuiStyleVar.WindowRounding, SpheneCustomTheme.CurrentTheme.WindowRounding);
            PushStyleVar(ImGuiStyleVar.ChildRounding, SpheneCustomTheme.CurrentTheme.ChildRounding);
            PushStyleVar(ImGuiStyleVar.FrameRounding, SpheneCustomTheme.CurrentTheme.FrameRounding);
            PushStyleVar(ImGuiStyleVar.PopupRounding, SpheneCustomTheme.CurrentTheme.PopupRounding);
            PushStyleVar(ImGuiStyleVar.ScrollbarRounding, SpheneCustomTheme.CurrentTheme.ScrollbarRounding);
            PushStyleVar(ImGuiStyleVar.GrabRounding, SpheneCustomTheme.CurrentTheme.GrabRounding);
            PushStyleVar(ImGuiStyleVar.TabRounding, SpheneCustomTheme.CurrentTheme.TabRounding);
            
            PushStyleVar(ImGuiStyleVar.WindowBorderSize, SpheneCustomTheme.CurrentTheme.WindowBorderSize);
            PushStyleVar(ImGuiStyleVar.ChildBorderSize, SpheneCustomTheme.CurrentTheme.ChildBorderSize);
            PushStyleVar(ImGuiStyleVar.PopupBorderSize, SpheneCustomTheme.CurrentTheme.PopupBorderSize);
            PushStyleVar(ImGuiStyleVar.FrameBorderSize, SpheneCustomTheme.CurrentTheme.FrameBorderSize);
            
            PushStyleVar(ImGuiStyleVar.WindowPadding, SpheneCustomTheme.CurrentTheme.WindowPadding);
            PushStyleVar(ImGuiStyleVar.FramePadding, SpheneCustomTheme.CurrentTheme.FramePadding);
            PushStyleVar(ImGuiStyleVar.ItemSpacing, SpheneCustomTheme.CurrentTheme.ItemSpacing);
            PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, SpheneCustomTheme.CurrentTheme.ItemInnerSpacing);
            PushStyleVar(ImGuiStyleVar.IndentSpacing, SpheneCustomTheme.CurrentTheme.IndentSpacing);
            PushStyleVar(ImGuiStyleVar.ScrollbarSize, SpheneCustomTheme.CurrentTheme.ScrollbarSize);
            PushStyleVar(ImGuiStyleVar.GrabMinSize, SpheneCustomTheme.CurrentTheme.GrabMinSize);
        }
        
        private void PushColor(ImGuiCol colorId, Vector4 color)
        {
            ImGui.PushStyleColor(colorId, color);
            _colorsPushed++;
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, float value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, Vector2 value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            if (_stylesPushed > 0) ImGui.PopStyleVar(_stylesPushed);
            if (_colorsPushed > 0) ImGui.PopStyleColor(_colorsPushed);
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
    
    private sealed class OriginalRadiusThemeScope : IDisposable
    {
        private int _colorsPushed = 0;
        private int _stylesPushed = 0;
        private bool _disposed;
        
        public OriginalRadiusThemeScope()
        {
            ApplyTheme();
        }
        
        private void ApplyTheme()
        {
            // Push all color overrides - using CompactUI specific colors
            PushColor(ImGuiCol.WindowBg, SpheneCustomTheme.CurrentTheme.CompactWindowBg);
            PushColor(ImGuiCol.ChildBg, SpheneCustomTheme.CurrentTheme.CompactChildBg);
            PushColor(ImGuiCol.PopupBg, SpheneCustomTheme.CurrentTheme.CompactPopupBg);
            PushColor(ImGuiCol.Border, SpheneCustomTheme.CurrentTheme.CompactBorder);
            PushColor(ImGuiCol.BorderShadow, SpheneCustomTheme.CurrentTheme.BorderShadow);
            
            // Title bar - using CompactUI specific title colors
            PushColor(ImGuiCol.TitleBg, SpheneCustomTheme.CurrentTheme.CompactTitleBg);
            PushColor(ImGuiCol.TitleBgActive, SpheneCustomTheme.CurrentTheme.CompactTitleBgActive);
            PushColor(ImGuiCol.TitleBgCollapsed, SpheneCustomTheme.CurrentTheme.TitleBgCollapsed);
            
            // Menu Bar
            PushColor(ImGuiCol.MenuBarBg, SpheneCustomTheme.CurrentTheme.MenuBarBg);
            
            // Text - using CompactUI specific text colors
            PushColor(ImGuiCol.Text, SpheneCustomTheme.CurrentTheme.CompactText);
            PushColor(ImGuiCol.TextDisabled, SpheneCustomTheme.CurrentTheme.CompactTextSecondary);
            PushColor(ImGuiCol.TextSelectedBg, SpheneCustomTheme.CurrentTheme.TextSelectedBg);
            
            // Buttons - using CompactUI specific button colors
            PushColor(ImGuiCol.Button, SpheneCustomTheme.CurrentTheme.CompactButton);
            PushColor(ImGuiCol.ButtonHovered, SpheneCustomTheme.CurrentTheme.CompactButtonHovered);
            PushColor(ImGuiCol.ButtonActive, SpheneCustomTheme.CurrentTheme.CompactButtonActive);
            
            // Headers - using CompactUI specific header colors
            PushColor(ImGuiCol.Header, SpheneCustomTheme.CurrentTheme.CompactHeaderBg);
            PushColor(ImGuiCol.HeaderHovered, SpheneCustomTheme.CurrentTheme.CompactHover);
            PushColor(ImGuiCol.HeaderActive, SpheneCustomTheme.CurrentTheme.CompactActive);
            
            // Frames - using CompactUI specific frame colors
            PushColor(ImGuiCol.FrameBg, SpheneCustomTheme.CurrentTheme.CompactFrameBg);
            PushColor(ImGuiCol.FrameBgHovered, SpheneCustomTheme.CurrentTheme.CompactHover);
            PushColor(ImGuiCol.FrameBgActive, SpheneCustomTheme.CurrentTheme.CompactActive);
            
            // Scrollbars
            PushColor(ImGuiCol.ScrollbarBg, SpheneCustomTheme.CurrentTheme.ScrollbarBg);
            PushColor(ImGuiCol.ScrollbarGrab, SpheneCustomTheme.CurrentTheme.CompactAccent);
            PushColor(ImGuiCol.ScrollbarGrabHovered, SpheneCustomTheme.CurrentTheme.CompactHover);
            PushColor(ImGuiCol.ScrollbarGrabActive, SpheneCustomTheme.CurrentTheme.CompactActive);
            
            // Check marks - using CompactUI accent
            PushColor(ImGuiCol.CheckMark, SpheneCustomTheme.CurrentTheme.CompactAccent);
            
            // Sliders
            PushColor(ImGuiCol.SliderGrab, SpheneCustomTheme.CurrentTheme.CompactAccent);
            PushColor(ImGuiCol.SliderGrabActive, SpheneCustomTheme.CurrentTheme.CompactActive);
            
            // Separators
            PushColor(ImGuiCol.Separator, SpheneCustomTheme.CurrentTheme.Separator);
            PushColor(ImGuiCol.SeparatorHovered, SpheneCustomTheme.CurrentTheme.SeparatorHovered);
            PushColor(ImGuiCol.SeparatorActive, SpheneCustomTheme.CurrentTheme.SeparatorActive);
            
            // Resize grip
            PushColor(ImGuiCol.ResizeGrip, SpheneCustomTheme.CurrentTheme.CompactAccent);
            PushColor(ImGuiCol.ResizeGripHovered, SpheneCustomTheme.CurrentTheme.CompactHover);
            PushColor(ImGuiCol.ResizeGripActive, SpheneCustomTheme.CurrentTheme.CompactActive);
            
            // Tabs
            PushColor(ImGuiCol.Tab, SpheneCustomTheme.CurrentTheme.Tab);
            PushColor(ImGuiCol.TabHovered, SpheneCustomTheme.CurrentTheme.CompactHover);
            PushColor(ImGuiCol.TabActive, SpheneCustomTheme.CurrentTheme.CompactAccent);
            PushColor(ImGuiCol.TabUnfocused, SpheneCustomTheme.CurrentTheme.TabUnfocused);
            PushColor(ImGuiCol.TabUnfocusedActive, SpheneCustomTheme.CurrentTheme.TabUnfocusedActive);
            
            // Table
            PushColor(ImGuiCol.TableHeaderBg, SpheneCustomTheme.CurrentTheme.TableHeaderBg);
            PushColor(ImGuiCol.TableBorderStrong, SpheneCustomTheme.CurrentTheme.CompactBorder);
            PushColor(ImGuiCol.TableBorderLight, SpheneCustomTheme.CurrentTheme.CompactBorder);
            PushColor(ImGuiCol.TableRowBg, SpheneCustomTheme.CurrentTheme.TableRowBg);
            PushColor(ImGuiCol.TableRowBgAlt, SpheneCustomTheme.CurrentTheme.TableRowBgAlt);
            
            // Drag Drop
            PushColor(ImGuiCol.DragDropTarget, SpheneCustomTheme.CurrentTheme.DragDropTarget);
            
            // Navigation
            PushColor(ImGuiCol.NavHighlight, SpheneCustomTheme.CurrentTheme.CompactAccent);
            PushColor(ImGuiCol.NavWindowingHighlight, SpheneCustomTheme.CurrentTheme.NavWindowingHighlight);
            PushColor(ImGuiCol.NavWindowingDimBg, SpheneCustomTheme.CurrentTheme.NavWindowingDimBg);
            
            // Modal
            PushColor(ImGuiCol.ModalWindowDimBg, SpheneCustomTheme.CurrentTheme.ModalWindowDimBg);
            
            // Note: Tooltips use WindowRounding, so we apply CompactTooltipRounding to WindowRounding
            // We no longer use Math.Max here - let window rounding be independent from tooltip rounding
            PushStyleVar(ImGuiStyleVar.WindowRounding, SpheneCustomTheme.CurrentTheme.CompactWindowRounding);
            PushStyleVar(ImGuiStyleVar.ChildRounding, SpheneCustomTheme.CurrentTheme.CompactChildRounding);
            PushStyleVar(ImGuiStyleVar.FrameRounding, SpheneCustomTheme.CurrentTheme.CompactFrameRounding);
            // PopupRounding is used for other popup elements (not tooltips)
            PushStyleVar(ImGuiStyleVar.PopupRounding, SpheneCustomTheme.CurrentTheme.CompactWindowRounding);
            PushStyleVar(ImGuiStyleVar.ScrollbarRounding, SpheneCustomTheme.CurrentTheme.CompactScrollbarRounding);
            PushStyleVar(ImGuiStyleVar.GrabRounding, SpheneCustomTheme.CurrentTheme.CompactGrabRounding);
            PushStyleVar(ImGuiStyleVar.TabRounding, SpheneCustomTheme.CurrentTheme.CompactTabRounding);
            
            PushStyleVar(ImGuiStyleVar.WindowBorderSize, SpheneCustomTheme.CurrentTheme.CompactWindowBorderSize);
            PushStyleVar(ImGuiStyleVar.ChildBorderSize, SpheneCustomTheme.CurrentTheme.CompactChildBorderSize);
            // Use CompactUI-specific tooltip border size for popups (tooltips use popup styling)
            PushStyleVar(ImGuiStyleVar.PopupBorderSize, SpheneCustomTheme.CurrentTheme.CompactTooltipBorderSize);
            PushStyleVar(ImGuiStyleVar.FrameBorderSize, SpheneCustomTheme.CurrentTheme.CompactFrameBorderSize);
            
            PushStyleVar(ImGuiStyleVar.WindowPadding, SpheneCustomTheme.CurrentTheme.CompactChildPadding);
            PushStyleVar(ImGuiStyleVar.FramePadding, SpheneCustomTheme.CurrentTheme.CompactFramePadding);
            PushStyleVar(ImGuiStyleVar.ItemSpacing, SpheneCustomTheme.CurrentTheme.CompactItemSpacing);
            PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, SpheneCustomTheme.CurrentTheme.CompactItemInnerSpacing);
            PushStyleVar(ImGuiStyleVar.CellPadding, SpheneCustomTheme.CurrentTheme.CompactCellPadding);
            PushStyleVar(ImGuiStyleVar.IndentSpacing, SpheneCustomTheme.CurrentTheme.CompactIndentSpacing);
            PushStyleVar(ImGuiStyleVar.ScrollbarSize, SpheneCustomTheme.CurrentTheme.CompactScrollbarSize);
            PushStyleVar(ImGuiStyleVar.GrabMinSize, SpheneCustomTheme.CurrentTheme.CompactGrabMinSize);
            PushStyleVar(ImGuiStyleVar.ButtonTextAlign, SpheneCustomTheme.CurrentTheme.CompactButtonTextAlign);
            PushStyleVar(ImGuiStyleVar.SelectableTextAlign, SpheneCustomTheme.CurrentTheme.CompactSelectableTextAlign);
        }
        
        private void PushColor(ImGuiCol colorId, Vector4 color)
        {
            ImGui.PushStyleColor(colorId, color);
            _colorsPushed++;
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, float value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, Vector2 value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            if (_stylesPushed > 0) ImGui.PopStyleVar(_stylesPushed);
            if (_colorsPushed > 0) ImGui.PopStyleColor(_colorsPushed);
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
    
    private sealed class ContextMenuThemeScope : IDisposable
    {
        private int _stylesPushed = 0;
        private bool _disposed;
        
        public ContextMenuThemeScope()
        {
            Apply();
        }
        
        private void Apply()
        {
            // Apply CompactUI-specific context menu styling
            // Context menus are popups, so we need to override the global PopupRounding and PopupBorderSize
            // with the specific CompactContextMenu values. Since ImGui uses a stack system,
            // we push our values on top of any existing values to ensure they take precedence.
            PushStyleVar(ImGuiStyleVar.PopupRounding, SpheneCustomTheme.CurrentTheme.CompactContextMenuRounding);
            PushStyleVar(ImGuiStyleVar.PopupBorderSize, SpheneCustomTheme.CurrentTheme.CompactContextMenuBorderSize);
            
            // Also apply frame rounding for menu items within the context menu
            PushStyleVar(ImGuiStyleVar.FrameRounding, SpheneCustomTheme.CurrentTheme.CompactFrameRounding);
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, float value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            if (_stylesPushed > 0) ImGui.PopStyleVar(_stylesPushed);
            _stylesPushed = 0;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
    
    private sealed class TooltipThemeScope : IDisposable
    {
        private int _stylesPushed = 0;
        private bool _disposed;
        
        public TooltipThemeScope()
        {
            Apply();
        }
        
        private void Apply()
        {
            // Apply CompactUI-specific tooltip styling independently
            // This will override any previously set WindowRounding for tooltips
            // Force the tooltip to use the specific tooltip rounding value
            PushStyleVar(ImGuiStyleVar.WindowRounding, SpheneCustomTheme.CurrentTheme.CompactTooltipRounding);
            PushStyleVar(ImGuiStyleVar.PopupBorderSize, SpheneCustomTheme.CurrentTheme.CompactTooltipBorderSize);
            
            // Also push PopupRounding to ensure consistency (though tooltips primarily use WindowRounding)
            PushStyleVar(ImGuiStyleVar.PopupRounding, SpheneCustomTheme.CurrentTheme.CompactTooltipRounding);
        }
        
        private void PushStyleVar(ImGuiStyleVar styleVar, float value)
        {
            ImGui.PushStyleVar(styleVar, value);
            _stylesPushed++;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            if (_stylesPushed > 0) ImGui.PopStyleVar(_stylesPushed);
            _stylesPushed = 0;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
    
    // Helper methods for status colors
    public static Vector4 GetStatusColor(string status)
    {
        return status.ToLower() switch
        {
            "connected" or "online" or "success" => Colors.Success,
            "connecting" or "warning" => Colors.Warning,
            "disconnected" or "offline" or "error" => Colors.Error,
            _ => Colors.TextSecondary
        };
    }
    
    // Helper method for drawing styled text with icons
    public static void DrawStyledText(string text, Vector4? color = null)
    {
        if (color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
            ImGui.Text(text);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.Text(text);
        }
    }
    
    // Helper method for drawing styled buttons
    public static bool DrawStyledButton(string label, Vector2? size = null, Vector4? color = null)
    {
        if (color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, color.Value);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.Value.X + 0.1f, color.Value.Y + 0.1f, color.Value.Z + 0.1f, color.Value.W));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(color.Value.X + 0.2f, color.Value.Y + 0.2f, color.Value.Z + 0.2f, color.Value.W));
            
            var result = ImGui.Button(label, size ?? Vector2.Zero);
            
            ImGui.PopStyleColor(3);
            return result;
        }
        
        return ImGui.Button(label, size ?? Vector2.Zero);
    }
}
