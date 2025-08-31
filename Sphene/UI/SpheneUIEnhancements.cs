using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Sphene.UI.Styling;

/// <summary>
/// Enhanced UI components with Sphene theming and modern design elements
/// </summary>
public static class SpheneUIEnhancements
{
    private static readonly float BorderRadius = 4.0f;
    private static readonly float ShadowOffset = 2.0f;
    
    // Static dictionary to store resize state per window
    private static readonly Dictionary<string, Vector2> _pendingWindowSizes = new();
    
    /// <summary>
    /// Draws a modern card-style container with Sphene theming
    /// </summary>
    public static void DrawSpheneCard(string id, Vector2 size, Action content, bool withGlow = false)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var endPos = new Vector2(pos.X + size.X, pos.Y + size.Y);
        
        // Background with gradient
        var bgColorTop = SpheneColors.BackgroundMid;
        var bgColorBottom = SpheneColors.BackgroundDark;
        drawList.AddRectFilledMultiColor(pos, endPos, 
            SpheneColors.ToImGuiColor(bgColorTop),
            SpheneColors.ToImGuiColor(bgColorTop),
            SpheneColors.ToImGuiColor(bgColorBottom),
            SpheneColors.ToImGuiColor(bgColorBottom));
        
        // Border with crystal blue
        drawList.AddRect(pos, endPos, SpheneColors.ToImGuiColor(SpheneColors.BorderColor), BorderRadius, ImDrawFlags.RoundCornersAll, 1.5f);
        
        // Optional glow effect
        if (withGlow)
        {
            var glowColor = SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.3f);
            drawList.AddRect(new Vector2(pos.X - 1, pos.Y - 1), new Vector2(endPos.X + 1, endPos.Y + 1), 
                SpheneColors.ToImGuiColor(glowColor), BorderRadius + 1, ImDrawFlags.RoundCornersAll, 2.0f);
        }
        
        // Content area
        using var child = ImRaii.Child(id, size, false, ImGuiWindowFlags.NoBackground);
        if (child)
        {
            ImGui.SetCursorPos(new Vector2(8, 8)); // Padding
            content();
        }
    }
    
    /// <summary>
    /// Draws a Sphene-themed card container with modern styling
    /// </summary>
    public static void DrawSpheneCard(string title, Action content, bool collapsible = false, float? maxHeight = null)
    {
        DrawSpheneCard(title, content, collapsible, maxHeight, null);
    }
    
    /// <summary>
    /// Draws a Sphene-themed card container with modern styling and optional header buttons
    /// </summary>
    public static void DrawSpheneCard(string title, Action content, bool collapsible = false, float? maxHeight = null, Action headerButtons = null)
    {
        var cardBg = SpheneColors.ToImGuiColor(SpheneColors.BackgroundMid);
        var borderColor = SpheneColors.ToImGuiColor(SpheneColors.BorderColor);
        var headerBg = SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.3f));
        
        using var cardStyle = ImRaii.PushColor(ImGuiCol.ChildBg, cardBg);
        using var borderStyle = ImRaii.PushColor(ImGuiCol.Border, borderColor);
        using var roundingStyle = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 12.0f);
        using var borderSizeStyle = ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 1.5f);
        using var paddingStyle = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16.0f, 12.0f));
        
        // Calculate card size - use maxHeight if specified, otherwise auto-size
        var cardSize = maxHeight.HasValue ? new Vector2(0, maxHeight.Value) : Vector2.Zero;
        
        if (ImGui.BeginChild($"##card_{title}", cardSize, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            // Modern card header with background
            var headerHeight = ImGui.GetTextLineHeight() + 16.0f;
            var drawList = ImGui.GetWindowDrawList();
            var headerMin = ImGui.GetCursorScreenPos();
            var headerMax = new Vector2(headerMin.X + ImGui.GetContentRegionAvail().X, headerMin.Y + headerHeight);
            
            drawList.AddRectFilled(headerMin, headerMax, headerBg, 12.0f);
            
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8.0f);
            using var headerColor = ImRaii.PushColor(ImGuiCol.Text, SpheneColors.ToImGuiColor(SpheneColors.CrystalBlue));
            
            if (collapsible)
            {
                if (ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Spacing();
                    content();
                }
            }
            else
            {
                // Header layout with title and buttons
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
                
                if (headerButtons != null)
                {
                    // Draw title on the left
                    ImGui.TextUnformatted(title);
                    
                    // Draw buttons on the right side of the header
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - 40.0f);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 0.0f);
                    headerButtons();
                }
                else
                {
                    ImGui.TextUnformatted(title);
                }
                
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8.0f);
                
                // Subtle separator line
                var separatorColor = SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.3f));
                var separatorStart = ImGui.GetCursorScreenPos();
                var separatorEnd = new Vector2(separatorStart.X + ImGui.GetContentRegionAvail().X - 16.0f, separatorStart.Y);
                drawList.AddLine(separatorStart, separatorEnd, separatorColor, 1.0f);
                
                ImGui.Spacing();
                ImGui.Spacing();
                content();
            }
        }
        ImGui.EndChild();
        ImGui.Spacing();
    }
    
    /// <summary>
    /// Draws a Sphene-themed card container with modern styling, optional header buttons and resize handle
    /// </summary>
    public static void DrawSpheneCard(string title, Action content, bool collapsible = false, float? maxHeight = null, Action headerButtons = null, bool withResizeHandle = false, Vector2? minSize = null, Vector2? maxSize = null)
    {
        var cardBg = SpheneColors.ToImGuiColor(SpheneColors.BackgroundMid);
        var borderColor = SpheneColors.ToImGuiColor(SpheneColors.BorderColor);
        var headerBg = SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.3f));
        
        using var cardStyle = ImRaii.PushColor(ImGuiCol.ChildBg, cardBg);
        using var borderStyle = ImRaii.PushColor(ImGuiCol.Border, borderColor);
        using var roundingStyle = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 12.0f);
        using var borderSizeStyle = ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 1.5f);
        using var paddingStyle = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16.0f, 12.0f));
        
        // Calculate card size - use maxHeight if specified, otherwise auto-size
        var cardSize = maxHeight.HasValue ? new Vector2(0, maxHeight.Value) : Vector2.Zero;
        
        if (ImGui.BeginChild($"##card_{title}", cardSize, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            // Modern card header with background
            var headerHeight = ImGui.GetTextLineHeight() + 16.0f;
            var drawList = ImGui.GetWindowDrawList();
            var headerMin = ImGui.GetCursorScreenPos();
            var headerMax = new Vector2(headerMin.X + ImGui.GetContentRegionAvail().X, headerMin.Y + headerHeight);
            
            drawList.AddRectFilled(headerMin, headerMax, headerBg, 12.0f);
            
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8.0f);
            using var headerColor = ImRaii.PushColor(ImGuiCol.Text, SpheneColors.ToImGuiColor(SpheneColors.CrystalBlue));
            
            if (collapsible)
            {
                if (ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Spacing();
                    content();
                }
            }
            else
            {
                // Header layout with title and buttons
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8.0f);
                
                if (headerButtons != null)
                {
                    // Draw title on the left
                    ImGui.TextUnformatted(title);
                    
                    // Draw buttons on the right side of the header
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - 40.0f);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 0.0f);
                    headerButtons();
                }
                else
                {
                    ImGui.TextUnformatted(title);
                }
                
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8.0f);
                
                // Subtle separator line
                var separatorColor = SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.3f));
                var separatorStart = ImGui.GetCursorScreenPos();
                var separatorEnd = new Vector2(separatorStart.X + ImGui.GetContentRegionAvail().X - 16.0f, separatorStart.Y);
                drawList.AddLine(separatorStart, separatorEnd, separatorColor, 1.0f);
                
                ImGui.Spacing();
                ImGui.Spacing();
                content();
            }
            
            // Add resize handle if requested
            if (withResizeHandle)
            {
                // Get the main window position and size
                var mainWindowPos = ImGui.GetWindowPos();
                var mainWindowSize = ImGui.GetWindowSize();
                var resizeHandleSize = new Vector2(16, 16);
                var resizeHandlePos = new Vector2(mainWindowPos.X + mainWindowSize.X - resizeHandleSize.X - 4, mainWindowPos.Y + mainWindowSize.Y - resizeHandleSize.Y - 4);
                
                ImGui.SetCursorScreenPos(resizeHandlePos);
                ImGui.InvisibleButton($"##resize_handle_{title}", resizeHandleSize);
                
                var isHovered = ImGui.IsItemHovered();
                var isActive = ImGui.IsItemActive();
                
                if (isActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    var mousePos = ImGui.GetIO().MousePos;
                    
                    // Calculate new size based on mouse position relative to window position
                    var newWidth = mousePos.X - mainWindowPos.X + 8; // Add some padding
                    var newHeight = mousePos.Y - mainWindowPos.Y + 8; // Add some padding
                    
                    // Apply size constraints if provided
                    if (minSize.HasValue)
                    {
                        newWidth = Math.Max(minSize.Value.X, newWidth);
                        newHeight = Math.Max(minSize.Value.Y, newHeight);
                    }
                    if (maxSize.HasValue)
                    {
                        newWidth = Math.Min(maxSize.Value.X, newWidth);
                        newHeight = Math.Min(maxSize.Value.Y, newHeight);
                    }
                    
                    // Store the new size to be applied next frame
                    _pendingWindowSizes[title] = new Vector2(newWidth, newHeight);
                }
                
                // Draw resize handle visual indicator
                // Corner triangle indicator
                var triangleColor = isActive ? SpheneColors.ToImGuiColor(SpheneColors.CrystalBlue) :
                                   isHovered ? SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.8f)) :
                                   SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.BorderColor, 0.6f));
                
                // Draw corner triangle (larger and more visible)
                var p1 = new Vector2(resizeHandlePos.X + resizeHandleSize.X - 1, resizeHandlePos.Y + 1);
                var p2 = new Vector2(resizeHandlePos.X + resizeHandleSize.X - 1, resizeHandlePos.Y + resizeHandleSize.Y - 1);
                var p3 = new Vector2(resizeHandlePos.X + 1, resizeHandlePos.Y + resizeHandleSize.Y - 1);
                
                drawList.AddTriangleFilled(p1, p2, p3, triangleColor);
                
                // Add more prominent grip lines
                var lineColor = SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.CrystalWhite, isHovered ? 0.6f : 0.4f));
                for (int i = 0; i < 4; i++)
                {
                    var offset = i * 3;
                    var lineStart = new Vector2(resizeHandlePos.X + resizeHandleSize.X - 4 - offset, resizeHandlePos.Y + 4 + offset);
                    var lineEnd = new Vector2(resizeHandlePos.X + resizeHandleSize.X - 4 - offset, resizeHandlePos.Y + resizeHandleSize.Y - 4);
                    drawList.AddLine(lineStart, lineEnd, lineColor, 1.5f);
                }
                
                // Add horizontal grip lines for better visibility
                for (int i = 0; i < 4; i++)
                {
                    var offset = i * 3;
                    var lineStart = new Vector2(resizeHandlePos.X + 4 + offset, resizeHandlePos.Y + resizeHandleSize.Y - 4 - offset);
                    var lineEnd = new Vector2(resizeHandlePos.X + resizeHandleSize.X - 4, resizeHandlePos.Y + resizeHandleSize.Y - 4 - offset);
                    drawList.AddLine(lineStart, lineEnd, lineColor, 1.5f);
                }
            }
        }
        ImGui.EndChild();
        ImGui.Spacing();
    }
    
    /// <summary>
    /// Draws a Sphene-themed button with crystal aesthetics
    /// </summary>
    public static bool DrawSpheneButton(string label, Vector2? size = null, bool isPrimary = false)
    {
        var buttonSize = size ?? new Vector2(120, 30);
        var baseColor = isPrimary ? SpheneColors.CrystalBlue : SpheneColors.DeepCrystal;
        var hoverColor = SpheneColors.LerpColor(baseColor, SpheneColors.CrystalWhite, 0.2f);
        var activeColor = SpheneColors.LerpColor(baseColor, SpheneColors.EtherealPurple, 0.3f);
        
        using var colorButton = ImRaii.PushColor(ImGuiCol.Button, SpheneColors.ToImGuiColor(baseColor));
        using var colorHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, SpheneColors.ToImGuiColor(hoverColor));
        using var colorActive = ImRaii.PushColor(ImGuiCol.ButtonActive, SpheneColors.ToImGuiColor(activeColor));
        using var colorText = ImRaii.PushColor(ImGuiCol.Text, SpheneColors.ToImGuiColor(SpheneColors.CrystalWhite));
        
        using var styleRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, BorderRadius);
        
        return ImGui.Button(label, buttonSize);
    }
    
    /// <summary>
    /// Draws a status indicator with Sphene theming
    /// </summary>
    public static void DrawSpheneStatusIndicator(string label, bool isActive, bool hasWarning = false, bool hasError = false)
    {
        var statusColor = SpheneColors.GetConnectionStatusColor(isActive, hasWarning, hasError);
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        
        // Draw status circle
        var circleCenter = new Vector2(pos.X + 8, pos.Y + ImGui.GetTextLineHeight() * 0.5f);
        drawList.AddCircleFilled(circleCenter, 6, SpheneColors.ToImGuiColor(statusColor));
        drawList.AddCircle(circleCenter, 6, SpheneColors.ToImGuiColor(SpheneColors.CrystalWhite), 12, 1.5f);
        
        // Add glow effect for active status
        if (isActive && !hasError)
        {
            var glowColor = SpheneColors.WithAlpha(statusColor, 0.4f);
            drawList.AddCircle(circleCenter, 8, SpheneColors.ToImGuiColor(glowColor), 12, 2.0f);
        }
        
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, SpheneColors.ToImGuiColor(SpheneColors.TextPrimary));
        ImGui.Text(label);
    }
    
    /// <summary>
    /// Draws a Sphene-themed progress bar with crystalline styling
    /// </summary>
    public static void DrawSpheneProgressBar(string label, float progress, string? overlay = null, Vector4? color = null)
    {
        // Draw label first
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        
        // Use default size and call the main implementation
        DrawSpheneProgressBar(progress, null, overlay);
    }
    
    /// <summary>
    /// Draws a Sphene-themed progress bar with crystalline styling
    /// </summary>
    public static void DrawSpheneProgressBar(float progress, Vector2? size = null, string? overlay = null)
    {
        var barSize = size ?? new Vector2(200, 20);
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var endPos = new Vector2(pos.X + barSize.X, pos.Y + barSize.Y);
        
        // Background
        drawList.AddRectFilled(pos, endPos, SpheneColors.ToImGuiColor(SpheneColors.BackgroundDark), BorderRadius);
        
        // Progress fill with gradient
        if (progress > 0)
        {
            var progressEnd = new Vector2(pos.X + (barSize.X * progress), pos.Y + barSize.Y);
            var progressColorStart = SpheneColors.CrystalBlue;
            var progressColorEnd = SpheneColors.EtherealPurple;
            
            drawList.AddRectFilledMultiColor(pos, progressEnd,
                SpheneColors.ToImGuiColor(progressColorStart),
                SpheneColors.ToImGuiColor(progressColorEnd),
                SpheneColors.ToImGuiColor(progressColorEnd),
                SpheneColors.ToImGuiColor(progressColorStart));
        }
        
        // Border
        drawList.AddRect(pos, endPos, SpheneColors.ToImGuiColor(SpheneColors.BorderColor), BorderRadius, ImDrawFlags.RoundCornersAll, 1.0f);
        
        // Overlay text
        if (!string.IsNullOrEmpty(overlay))
        {
            var textSize = ImGui.CalcTextSize(overlay);
            var textPos = new Vector2(
                pos.X + (barSize.X - textSize.X) * 0.5f,
                pos.Y + (barSize.Y - textSize.Y) * 0.5f
            );
            drawList.AddText(textPos, SpheneColors.ToImGuiColor(SpheneColors.CrystalWhite), overlay);
        }
        
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + barSize.Y + ImGui.GetStyle().ItemSpacing.Y);
    }
    
    /// <summary>
    /// Draws a Sphene-themed header with crystal accent
    /// </summary>
    public static void DrawSpheneHeader(string text, bool withUnderline = true)
    {
        using var font = ImRaii.PushFont(UiBuilder.MonoFont);
        using var color = ImRaii.PushColor(ImGuiCol.Text, SpheneColors.ToImGuiColor(SpheneColors.SpheneGold));
        
        ImGui.Text(text);
        
        if (withUnderline)
        {
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var textWidth = ImGui.CalcTextSize(text).X;
            
            // Crystal-style underline with gradient
            var startPos = new Vector2(pos.X, pos.Y - 2);
            var endPos = new Vector2(pos.X + textWidth, pos.Y - 2);
            
            drawList.AddLine(startPos, endPos, SpheneColors.ToImGuiColor(SpheneColors.CrystalBlue), 2.0f);
            drawList.AddLine(new Vector2(startPos.X, startPos.Y + 1), new Vector2(endPos.X, endPos.Y + 1), 
                SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.EtherealPurple, 0.6f)), 1.0f);
        }
    }
    
    /// <summary>
    /// Draws a tooltip with Sphene theming
    /// </summary>
    public static void DrawSpheneTooltip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            using var bgColor = ImRaii.PushColor(ImGuiCol.PopupBg, SpheneColors.ToImGuiColor(SpheneColors.BackgroundDark));
            using var borderColor = ImRaii.PushColor(ImGuiCol.Border, SpheneColors.ToImGuiColor(SpheneColors.CrystalBlue));
            using var textColor = ImRaii.PushColor(ImGuiCol.Text, SpheneColors.ToImGuiColor(SpheneColors.TextPrimary));
            
            ImGui.Text(text);
        }
    }
    
    /// <summary>
    /// Applies Sphene theme colors locally using ImRaii (recommended for plugin-only styling)
    /// </summary>
    /// <param name="useGlobalTheme">If true, applies theme globally (affects all Dalamud UI). If false, applies only locally.</param>
    public static IDisposable ApplySpheneTheme(bool useGlobalTheme = false)
    {
        return useGlobalTheme ? ApplySpheneWindowTheme() : ApplySpheneLocalTheme();
    }
    
    /// <summary>
    /// Returns a no-op disposable - theme should be applied manually using individual Sphene UI components
    /// </summary>
    public static IDisposable ApplySpheneLocalTheme()
    {
        // Return a no-op disposable since we'll use individual themed components instead
        return new NoOpDisposable();
    }
    
    /// <summary>
    /// Applies Sphene theme to the current window
    /// </summary>
    public static IDisposable ApplySpheneWindowTheme()
    {
        var style = ImGui.GetStyle();
        var colors = style.Colors;
        
        // Store original colors for restoration
        var originalColors = new Dictionary<ImGuiCol, Vector4>();
        
        // Apply Sphene theme colors
        var themeColors = new Dictionary<ImGuiCol, Vector4>
        {
            { ImGuiCol.WindowBg, SpheneColors.BackgroundDark },
            { ImGuiCol.ChildBg, SpheneColors.BackgroundMid },
            { ImGuiCol.PopupBg, SpheneColors.BackgroundDark },
            { ImGuiCol.Border, SpheneColors.BorderColor },
            { ImGuiCol.BorderShadow, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.2f) },
            { ImGuiCol.FrameBg, SpheneColors.BackgroundMid },
            { ImGuiCol.FrameBgHovered, SpheneColors.HoverBlue },
            { ImGuiCol.FrameBgActive, SpheneColors.SelectionBlue },
            { ImGuiCol.TitleBg, SpheneColors.DeepCrystal },
            { ImGuiCol.TitleBgActive, SpheneColors.CrystalBlue },
            { ImGuiCol.TitleBgCollapsed, SpheneColors.VoidPurple },
            { ImGuiCol.MenuBarBg, SpheneColors.BackgroundMid },
            { ImGuiCol.ScrollbarBg, SpheneColors.BackgroundDark },
            { ImGuiCol.ScrollbarGrab, SpheneColors.CrystalBlue },
            { ImGuiCol.ScrollbarGrabHovered, SpheneColors.EtherealPurple },
            { ImGuiCol.ScrollbarGrabActive, SpheneColors.SpheneGold },
            { ImGuiCol.CheckMark, SpheneColors.NetworkActive },
            { ImGuiCol.SliderGrab, SpheneColors.CrystalBlue },
            { ImGuiCol.SliderGrabActive, SpheneColors.SpheneGold },
            { ImGuiCol.Button, SpheneColors.DeepCrystal },
            { ImGuiCol.ButtonHovered, SpheneColors.CrystalBlue },
            { ImGuiCol.ButtonActive, SpheneColors.EtherealPurple },
            { ImGuiCol.Header, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.4f) },
            { ImGuiCol.HeaderHovered, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.6f) },
            { ImGuiCol.HeaderActive, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.8f) },
            { ImGuiCol.Separator, SpheneColors.BorderColor },
            { ImGuiCol.SeparatorHovered, SpheneColors.CrystalBlue },
            { ImGuiCol.SeparatorActive, SpheneColors.SpheneGold },
            { ImGuiCol.ResizeGrip, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.3f) },
            { ImGuiCol.ResizeGripHovered, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.6f) },
            { ImGuiCol.ResizeGripActive, SpheneColors.CrystalBlue },
            { ImGuiCol.Tab, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.7f) },
            { ImGuiCol.TabHovered, SpheneColors.CrystalBlue },
            { ImGuiCol.TabActive, SpheneColors.ActiveTab },
            { ImGuiCol.TabUnfocused, SpheneColors.WithAlpha(SpheneColors.VoidPurple, 0.5f) },
            { ImGuiCol.TabUnfocusedActive, SpheneColors.WithAlpha(SpheneColors.DeepCrystal, 0.8f) },
            { ImGuiCol.PlotLines, SpheneColors.CrystalBlue },
            { ImGuiCol.PlotLinesHovered, SpheneColors.SpheneGold },
            { ImGuiCol.PlotHistogram, SpheneColors.EtherealPurple },
            { ImGuiCol.PlotHistogramHovered, SpheneColors.SpheneGold },
            { ImGuiCol.TableHeaderBg, SpheneColors.BackgroundMid },
            { ImGuiCol.TableBorderStrong, SpheneColors.BorderColor },
            { ImGuiCol.TableBorderLight, SpheneColors.WithAlpha(SpheneColors.BorderColor, 0.5f) },
            { ImGuiCol.TableRowBg, SpheneColors.WithAlpha(SpheneColors.BackgroundMid, 0.0f) },
            { ImGuiCol.TableRowBgAlt, SpheneColors.WithAlpha(SpheneColors.BackgroundMid, 0.3f) },
            { ImGuiCol.TextSelectedBg, SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.4f) },
            { ImGuiCol.DragDropTarget, SpheneColors.SpheneGold },
            { ImGuiCol.NavHighlight, SpheneColors.CrystalBlue },
            { ImGuiCol.NavWindowingHighlight, SpheneColors.WithAlpha(SpheneColors.CrystalWhite, 0.7f) },
            { ImGuiCol.NavWindowingDimBg, SpheneColors.WithAlpha(SpheneColors.BackgroundDark, 0.2f) },
            { ImGuiCol.ModalWindowDimBg, SpheneColors.WithAlpha(SpheneColors.BackgroundDark, 0.6f) }
        };
        
        foreach (var (colorType, color) in themeColors)
        {
            originalColors[colorType] = colors[(int)colorType];
            colors[(int)colorType] = color;
        }
        
        // Return disposable to restore original colors
        return new ColorRestorer(originalColors);
    }
    
    private class ColorRestorer : IDisposable
    {
        private readonly Dictionary<ImGuiCol, Vector4> _originalColors;
        
        public ColorRestorer(Dictionary<ImGuiCol, Vector4> originalColors)
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
    
    private class LocalColorRestorer : IDisposable
    {
        private readonly List<IDisposable> _colorStack;
        
        public LocalColorRestorer(List<IDisposable> colorStack)
        {
            _colorStack = colorStack;
        }
        
        public void Dispose()
        {
            // Dispose in reverse order to properly restore the color stack
            for (int i = _colorStack.Count - 1; i >= 0; i--)
            {
                _colorStack[i]?.Dispose();
            }
        }
    }
    
    private class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
            // No operation - this is used when we don't want to apply any theme
        }
    }
    
    /// <summary>
    /// Draws a modern section with title and content
    /// </summary>
    public static void DrawSpheneSection(string title, Action content, bool spacing = true)
    {
        if (spacing) ImGui.Spacing();
        
        // Section title with underline
        using var titleColor = ImRaii.PushColor(ImGuiCol.Text, SpheneColors.ToImGuiColor(SpheneColors.CrystalBlue));
        ImGui.TextUnformatted(title);
        
        // Modern underline
        var drawList = ImGui.GetWindowDrawList();
        var titleSize = ImGui.CalcTextSize(title);
        var lineStart = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2.0f);
        var lineEnd = new Vector2(lineStart.X + titleSize.X, lineStart.Y);
        var lineColor = SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.6f));
        drawList.AddLine(lineStart, lineEnd, lineColor, 2.0f);
        
        ImGui.Spacing();
        
        // Content with slight indentation
        using var indent = ImRaii.PushIndent(12.0f);
        content();
        
        if (spacing) ImGui.Spacing();
    }
    
    /// <summary>
    /// Draws a subtle separator with Sphene styling
    /// </summary>
    public static void DrawSpheneSeparator(float alpha = 0.3f)
    {
        ImGui.Spacing();
        
        var drawList = ImGui.GetWindowDrawList();
        var separatorColor = SpheneColors.ToImGuiColor(SpheneColors.WithAlpha(SpheneColors.CrystalBlue, alpha));
        var separatorStart = ImGui.GetCursorScreenPos();
        var separatorEnd = new Vector2(separatorStart.X + ImGui.GetContentRegionAvail().X, separatorStart.Y);
        
        drawList.AddLine(separatorStart, separatorEnd, separatorColor, 1.0f);
        
        ImGui.Spacing();
    }
    
    /// <summary>
    /// Applies pending window size changes that were requested during resize operations
    /// This should be called before ImGui.Begin() for windows that use resize handles
    /// </summary>
    /// <param name="windowName">The name of the window to check for pending resize</param>
    public static void ApplyPendingWindowResize(string windowName)
    {
        if (_pendingWindowSizes.TryGetValue(windowName, out var newSize))
        {
            ImGui.SetNextWindowSize(newSize);
            _pendingWindowSizes.Remove(windowName);
        }
    }
    
    /// <summary>
    /// Draws a modern info box with icon and text
    /// </summary>
    public static void DrawSpheneInfoBox(string text, FontAwesomeIcon icon = FontAwesomeIcon.InfoCircle, Vector4? color = null)
    {
        var boxColor = ImGui.ColorConvertFloat4ToU32(color ?? SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.1f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(color ?? SpheneColors.WithAlpha(SpheneColors.CrystalBlue, 0.4f));
        var textColor = color ?? SpheneColors.CrystalBlue;
        
        var drawList = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        var iconSize = ImGui.CalcTextSize(icon.ToIconString());
        var boxHeight = Math.Max(textSize.Y, iconSize.Y) + 16.0f;
        var boxWidth = ImGui.GetContentRegionAvail().X;
        
        var boxMin = startPos;
        var boxMax = new Vector2(startPos.X + boxWidth, startPos.Y + boxHeight);
        
        // Draw background and border
        drawList.AddRectFilled(boxMin, boxMax, boxColor, 8.0f);
        drawList.AddRect(boxMin, boxMax, borderColor, 8.0f, ImDrawFlags.RoundCornersAll, 1.0f);
        
        // Position cursor for content
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8.0f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12.0f);
        
        // Draw icon and text
        using var iconColor = ImRaii.PushColor(ImGuiCol.Text, textColor);
        ImGui.Text(icon.ToIconString());
        ImGui.SameLine();
        ImGui.Text(text);
        
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8.0f);
        ImGui.Spacing();
    }
}