using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Design;

/// <summary>
/// Base class for theme scopes: derived scopes push ImGui colors/style vars in
/// their constructor via the <see cref="PushColor"/>/<see cref="PushStyleVar"/>
/// helpers; dispose pops the exact same counts.
/// </summary>
internal abstract class ThemeStyleScope : IDisposable
{
    private int _colorsPushed;
    private int _stylesPushed;
    private bool _disposed;

    protected void PushColor(ImGuiCol colorId, Vector4 color)
    {
        ImGui.PushStyleColor(colorId, color);
        _colorsPushed++;
    }

    protected void PushStyleVar(ImGuiStyleVar styleVar, float value)
    {
        ImGui.PushStyleVar(styleVar, value);
        _stylesPushed++;
    }

    protected void PushStyleVar(ImGuiStyleVar styleVar, Vector2 value)
    {
        ImGui.PushStyleVar(styleVar, value);
        _stylesPushed++;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || _disposed) return;
        if (_stylesPushed > 0) ImGui.PopStyleVar(_stylesPushed);
        if (_colorsPushed > 0) ImGui.PopStyleColor(_colorsPushed);
        _disposed = true;
    }
}

/// <summary>
/// The standard window theme scope: pushes every <see cref="ImGuiCol"/> and
/// style var from <see cref="SpheneTheme.Current"/>. Applied to all design-
/// system windows (everything except CompactUI) by the window base class.
/// </summary>
internal sealed class WindowThemeScope : ThemeStyleScope
{
    /// <summary>Pushes this scope's token set.</summary>
    public WindowThemeScope()
    {
        SpheneUI.ThemeScopeTracker.Push();
        Apply();
    }

    protected override void Dispose(bool disposing)
    {
        SpheneUI.ThemeScopeTracker.Pop();
        base.Dispose(disposing);
    }

    private void Apply()
    {
        var theme = SpheneTheme.Current;
        PushColor(ImGuiCol.WindowBg, theme.WindowBg);
        PushColor(ImGuiCol.ChildBg, theme.ChildBg);
        PushColor(ImGuiCol.PopupBg, theme.PopupBg);
        PushColor(ImGuiCol.Border, theme.Border);
        PushColor(ImGuiCol.BorderShadow, theme.BorderShadow);

        PushColor(ImGuiCol.TitleBg, theme.TitleBg);
        PushColor(ImGuiCol.TitleBgActive, theme.TitleBgActive);
        PushColor(ImGuiCol.TitleBgCollapsed, theme.TitleBgCollapsed);

        PushColor(ImGuiCol.MenuBarBg, theme.MenuBarBg);

        PushColor(ImGuiCol.Text, theme.TextPrimary);
        PushColor(ImGuiCol.TextDisabled, theme.TextSecondary);
        PushColor(ImGuiCol.TextSelectedBg, theme.TextSelectedBg);

        PushColor(ImGuiCol.Button, theme.Button);
        PushColor(ImGuiCol.ButtonHovered, theme.ButtonHovered);
        PushColor(ImGuiCol.ButtonActive, theme.ButtonActive);

        PushColor(ImGuiCol.Header, theme.Header);
        PushColor(ImGuiCol.HeaderHovered, theme.HeaderHovered);
        PushColor(ImGuiCol.HeaderActive, theme.HeaderActive);

        PushColor(ImGuiCol.FrameBg, theme.FrameBg);
        PushColor(ImGuiCol.FrameBgHovered, theme.FrameBgHovered);
        PushColor(ImGuiCol.FrameBgActive, theme.FrameBgActive);

        PushColor(ImGuiCol.ScrollbarBg, theme.ScrollbarBg);
        PushColor(ImGuiCol.ScrollbarGrab, theme.ScrollbarGrab);
        PushColor(ImGuiCol.ScrollbarGrabHovered, theme.ScrollbarGrabHovered);
        PushColor(ImGuiCol.ScrollbarGrabActive, theme.ScrollbarGrabActive);

        PushColor(ImGuiCol.CheckMark, theme.CheckMark);

        PushColor(ImGuiCol.SliderGrab, theme.SliderGrab);
        PushColor(ImGuiCol.SliderGrabActive, theme.SliderGrabActive);

        PushColor(ImGuiCol.Separator, theme.Separator);
        PushColor(ImGuiCol.SeparatorHovered, theme.SeparatorHovered);
        PushColor(ImGuiCol.SeparatorActive, theme.SeparatorActive);

        PushColor(ImGuiCol.ResizeGrip, theme.ResizeGrip);
        PushColor(ImGuiCol.ResizeGripHovered, theme.ResizeGripHovered);
        PushColor(ImGuiCol.ResizeGripActive, theme.ResizeGripActive);

        PushColor(ImGuiCol.Tab, theme.Tab);
        PushColor(ImGuiCol.TabHovered, theme.TabHovered);
        PushColor(ImGuiCol.TabActive, theme.TabActive);
        PushColor(ImGuiCol.TabUnfocused, theme.TabUnfocused);
        PushColor(ImGuiCol.TabUnfocusedActive, theme.TabUnfocusedActive);

        PushColor(ImGuiCol.TableHeaderBg, theme.TableHeaderBg);
        PushColor(ImGuiCol.TableBorderStrong, theme.TableBorderStrong);
        PushColor(ImGuiCol.TableBorderLight, theme.TableBorderLight);
        PushColor(ImGuiCol.TableRowBg, theme.TableRowBg);
        PushColor(ImGuiCol.TableRowBgAlt, theme.TableRowBgAlt);

        PushColor(ImGuiCol.DragDropTarget, theme.DragDropTarget);

        PushColor(ImGuiCol.NavHighlight, theme.NavHighlight);
        PushColor(ImGuiCol.NavWindowingHighlight, theme.NavWindowingHighlight);
        PushColor(ImGuiCol.NavWindowingDimBg, theme.NavWindowingDimBg);

        PushColor(ImGuiCol.ModalWindowDimBg, theme.ModalWindowDimBg);

        PushStyleVar(ImGuiStyleVar.WindowRounding, theme.WindowRounding);
        PushStyleVar(ImGuiStyleVar.ChildRounding, theme.ChildRounding);
        PushStyleVar(ImGuiStyleVar.FrameRounding, theme.FrameRounding);
        PushStyleVar(ImGuiStyleVar.PopupRounding, theme.PopupRounding);
        PushStyleVar(ImGuiStyleVar.ScrollbarRounding, theme.ScrollbarRounding);
        PushStyleVar(ImGuiStyleVar.GrabRounding, theme.GrabRounding);
        PushStyleVar(ImGuiStyleVar.TabRounding, theme.TabRounding);

        PushStyleVar(ImGuiStyleVar.WindowBorderSize, theme.WindowBorderSize);
        PushStyleVar(ImGuiStyleVar.ChildBorderSize, theme.ChildBorderSize);
        PushStyleVar(ImGuiStyleVar.PopupBorderSize, theme.PopupBorderSize);
        PushStyleVar(ImGuiStyleVar.FrameBorderSize, theme.FrameBorderSize);

        PushStyleVar(ImGuiStyleVar.WindowPadding, theme.WindowPadding);
        PushStyleVar(ImGuiStyleVar.FramePadding, theme.FramePadding);
        PushStyleVar(ImGuiStyleVar.ItemSpacing, theme.ItemSpacing);
        PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, theme.ItemInnerSpacing);
        PushStyleVar(ImGuiStyleVar.IndentSpacing, theme.IndentSpacing);
        PushStyleVar(ImGuiStyleVar.ScrollbarSize, theme.ScrollbarSize);
        PushStyleVar(ImGuiStyleVar.GrabMinSize, theme.GrabMinSize);
    }
}

/// <summary>
/// The CompactUI window theme scope: pushes the <c>Compact*</c> color set and
/// style vars. Owned by CompactUI until it receives its own design system.
/// Separator/grip hover-active colors stay transparent so ImGui's native
/// border-resize ticks do not show on the panel.
/// </summary>
internal sealed class CompactThemeScope : ThemeStyleScope
{
    /// <summary>Pushes the CompactUI token set.</summary>
    public CompactThemeScope()
    {
        SpheneUI.ThemeScopeTracker.Push();
        Apply();
    }

    protected override void Dispose(bool disposing)
    {
        SpheneUI.ThemeScopeTracker.Pop();
        base.Dispose(disposing);
    }

    private static Vector4 TransparentCopy(Vector4 color) => new(color.X, color.Y, color.Z, 0f);

    private void Apply()
    {
        var theme = SpheneTheme.Current;
        PushColor(ImGuiCol.WindowBg, theme.CompactWindowBg);
        PushColor(ImGuiCol.ChildBg, theme.CompactChildBg);
        PushColor(ImGuiCol.PopupBg, theme.CompactPopupBg);
        PushColor(ImGuiCol.Border, theme.CompactBorder);
        PushColor(ImGuiCol.BorderShadow, theme.CompactBorderShadow);

        PushColor(ImGuiCol.TitleBg, theme.CompactTitleBg);
        PushColor(ImGuiCol.TitleBgActive, theme.CompactTitleBgActive);
        PushColor(ImGuiCol.TitleBgCollapsed, theme.CompactTitleBgCollapsed);

        PushColor(ImGuiCol.MenuBarBg, theme.CompactMenuBarBg);

        PushColor(ImGuiCol.Text, theme.CompactText);
        PushColor(ImGuiCol.TextDisabled, theme.CompactTextSecondary);
        PushColor(ImGuiCol.TextSelectedBg, theme.CompactTextSelectedBg);

        PushColor(ImGuiCol.Button, theme.CompactButton);
        PushColor(ImGuiCol.ButtonHovered, theme.CompactButtonHovered);
        PushColor(ImGuiCol.ButtonActive, theme.CompactButtonActive);

        PushColor(ImGuiCol.Header, theme.CompactHeaderBg);
        PushColor(ImGuiCol.HeaderHovered, theme.CompactHover);
        PushColor(ImGuiCol.HeaderActive, theme.CompactActive);

        PushColor(ImGuiCol.FrameBg, theme.CompactFrameBg);
        PushColor(ImGuiCol.FrameBgHovered, theme.CompactHover);
        PushColor(ImGuiCol.FrameBgActive, theme.CompactActive);

        PushColor(ImGuiCol.ScrollbarBg, theme.CompactScrollbarBg);
        PushColor(ImGuiCol.ScrollbarGrab, theme.CompactAccent);
        PushColor(ImGuiCol.ScrollbarGrabHovered, theme.CompactHover);
        PushColor(ImGuiCol.ScrollbarGrabActive, theme.CompactActive);

        PushColor(ImGuiCol.CheckMark, theme.CompactAccent);

        PushColor(ImGuiCol.SliderGrab, theme.CompactAccent);
        PushColor(ImGuiCol.SliderGrabActive, theme.CompactActive);

        PushColor(ImGuiCol.Separator, theme.CompactSeparator);
        PushColor(ImGuiCol.SeparatorHovered, TransparentCopy(theme.CompactSeparatorHovered));
        PushColor(ImGuiCol.SeparatorActive, TransparentCopy(theme.CompactSeparatorActive));

        PushColor(ImGuiCol.ResizeGrip, theme.CompactAccent);
        PushColor(ImGuiCol.ResizeGripHovered, TransparentCopy(theme.CompactHover));
        PushColor(ImGuiCol.ResizeGripActive, TransparentCopy(theme.CompactActive));

        PushColor(ImGuiCol.Tab, theme.CompactTab);
        PushColor(ImGuiCol.TabHovered, theme.CompactHover);
        PushColor(ImGuiCol.TabActive, theme.CompactAccent);
        PushColor(ImGuiCol.TabUnfocused, theme.CompactTabUnfocused);
        PushColor(ImGuiCol.TabUnfocusedActive, theme.CompactTabUnfocusedActive);

        PushColor(ImGuiCol.TableHeaderBg, theme.CompactTableHeaderBg);
        PushColor(ImGuiCol.TableBorderStrong, theme.CompactBorder);
        PushColor(ImGuiCol.TableBorderLight, theme.CompactBorder);
        PushColor(ImGuiCol.TableRowBg, theme.CompactTableRowBg);
        PushColor(ImGuiCol.TableRowBgAlt, theme.CompactTableRowBgAlt);

        PushColor(ImGuiCol.DragDropTarget, theme.CompactDragDropTarget);

        PushColor(ImGuiCol.NavHighlight, theme.CompactAccent);
        PushColor(ImGuiCol.NavWindowingHighlight, theme.CompactNavWindowingHighlight);
        PushColor(ImGuiCol.NavWindowingDimBg, theme.CompactNavWindowingDimBg);

        PushColor(ImGuiCol.ModalWindowDimBg, theme.CompactModalWindowDimBg);

        PushStyleVar(ImGuiStyleVar.WindowRounding, theme.CompactWindowRounding);
        PushStyleVar(ImGuiStyleVar.ChildRounding, theme.CompactChildRounding);
        PushStyleVar(ImGuiStyleVar.FrameRounding, theme.CompactFrameRounding);
        PushStyleVar(ImGuiStyleVar.PopupRounding, theme.CompactPopupRounding);
        PushStyleVar(ImGuiStyleVar.ScrollbarRounding, theme.CompactScrollbarRounding);
        PushStyleVar(ImGuiStyleVar.GrabRounding, theme.CompactGrabRounding);
        PushStyleVar(ImGuiStyleVar.TabRounding, theme.CompactTabRounding);

        PushStyleVar(ImGuiStyleVar.WindowBorderSize, theme.CompactWindowBorderSize);
        PushStyleVar(ImGuiStyleVar.ChildBorderSize, theme.CompactChildBorderSize);
        PushStyleVar(ImGuiStyleVar.PopupBorderSize, theme.CompactPopupBorderSize);
        PushStyleVar(ImGuiStyleVar.FrameBorderSize, theme.CompactFrameBorderSize);

        PushStyleVar(ImGuiStyleVar.WindowPadding, theme.CompactWindowPadding);
        PushStyleVar(ImGuiStyleVar.FramePadding, theme.CompactFramePadding);
        PushStyleVar(ImGuiStyleVar.ItemSpacing, theme.CompactItemSpacing);
        PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, theme.CompactItemInnerSpacing);
        PushStyleVar(ImGuiStyleVar.CellPadding, theme.CompactCellPadding);
        PushStyleVar(ImGuiStyleVar.IndentSpacing, theme.CompactIndentSpacing);
        PushStyleVar(ImGuiStyleVar.ScrollbarSize, theme.CompactScrollbarSize);
        PushStyleVar(ImGuiStyleVar.GrabMinSize, theme.CompactGrabMinSize);
        PushStyleVar(ImGuiStyleVar.ButtonTextAlign, theme.CompactButtonTextAlign);
        PushStyleVar(ImGuiStyleVar.SelectableTextAlign, theme.CompactSelectableTextAlign);
    }
}

/// <summary>
/// Tooltip styling scope: applies the theme's tooltip rounding and border so
/// tooltips render consistently regardless of the owning window's rounding.
/// </summary>
internal sealed class TooltipThemeScope : ThemeStyleScope
{
    /// <summary>Pushes this scope's token set.</summary>
    public TooltipThemeScope() => Apply();

    private void Apply()
    {
        var theme = SpheneTheme.Current;
        PushStyleVar(ImGuiStyleVar.WindowRounding, theme.CompactTooltipRounding);
        PushStyleVar(ImGuiStyleVar.PopupBorderSize, theme.CompactTooltipBorderSize);
        PushStyleVar(ImGuiStyleVar.PopupRounding, theme.CompactTooltipRounding);
    }
}

/// <summary>
/// Context menu styling scope: overrides popup rounding/border/padding and
/// frame rounding so context menus share the design-system popup language.
/// </summary>
internal sealed class ContextMenuThemeScope : ThemeStyleScope
{
    /// <summary>Pushes this scope's token set.</summary>
    public ContextMenuThemeScope() => Apply();

    private void Apply()
    {
        var theme = SpheneTheme.Current;
        PushStyleVar(ImGuiStyleVar.PopupRounding, theme.CompactContextMenuRounding);
        PushStyleVar(ImGuiStyleVar.PopupBorderSize, theme.CompactContextMenuBorderSize);
        PushStyleVar(ImGuiStyleVar.WindowPadding, theme.CompactChildPadding);
        PushStyleVar(ImGuiStyleVar.FrameRounding, theme.CompactFrameRounding);
    }
}
