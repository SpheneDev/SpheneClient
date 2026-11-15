using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Discord-style unsaved-changes toast · a floating bar that rises from the
/// bottom of a view while there is something to commit: a marker dot and
/// message on the left, capsule action buttons on the right. Rendered as its
/// own always-on-top overlay window, so it floats above scrolled content
/// without shifting the layout and slides/fades back out once the changes
/// resolve. Instantiate once per use site; call <see cref="Draw"/> every
/// frame while the hosting view is visible.
/// </summary>
public sealed class UnsavedChangesToast
{
    /// <summary>One action on the bar · rendered as a capsule button. Set
    /// <paramref name="Solid"/> for a filled CTA (accent bg, dark text) -
    /// tonal buttons wash out on the dark toast.</summary>
    public readonly record struct Button(string Label, FontAwesomeIcon Icon,
        Vector4 Accent, bool Enabled, string? Tooltip, Action OnClick,
        bool Solid = false);

    private float _vis;

    /// <summary>
    /// Draws the toast overlay. <paramref name="centerX"/> and
    /// <paramref name="bottom"/> are screen-space anchors · typically the
    /// hosting view's content-region center and bottom edge.
    /// </summary>
    public void Draw(bool active, string message, float centerX, float bottom,
        params Button[] buttons)
    {
        var scale = SpheneUI.Scale;
        _vis = Math.Clamp(_vis + ImGui.GetIO().DeltaTime * (active ? 6f : -6f),
            0f, 1f);
        if (_vis < 0.001f)
        {
            return;
        }
        var ease = _vis * _vis * (3f - 2f * _vis);

        var barH = 38f * scale;
        var btnH = 26f * scale;
        var padX = 14f * scale;
        var btnGap = 8f * scale;
        var msgW = ImGui.CalcTextSize(message).X;
        var btnsW = -btnGap;
        var btnWs = new float[buttons.Length];
        for (var i = 0; i < buttons.Length; i++)
        {
            btnWs[i] = CapsuleButtonWidth(btnH, buttons[i].Icon,
                buttons[i].Label);
            btnsW += btnWs[i] + btnGap;
        }
        var barW = padX * 2f + 12f * scale + msgW + 14f * scale
            + MathF.Max(0f, btnsW);
        var min = new Vector2(centerX - barW * 0.5f,
            bottom - barH - 8f * scale + (1f - ease) * 18f * scale);

        // Input blocker · the visuals live on the foreground draw list, but
        // an invisible always-on-top window over the bar rect swallows clicks
        // so they can't fall through to the controls underneath (a click on
        // Apply must never also toggle a widget behind the toast).
        ImGui.SetNextWindowPos(min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(barW, barH), ImGuiCond.Always);
        // Grab focus only while the pointer is over the bar: an always-focused
        // blocker would starve the host window of focus and eat interactions
        // elsewhere. While hovered it is raised to the front, so clicks on the
        // toast can't fall through to the controls underneath.
        if (ImGui.IsMouseHoveringRect(min, min + new Vector2(barW, barH)))
        {
            ImGui.SetNextWindowFocus();
        }
        ImGui.Begin($"##unsaved-toast{GetHashCode():x}",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoFocusOnAppearing);
        ImGui.End();

        // foreground drawlist so the bar floats above every window fade -
        // the button hit zones stay ordinary host-window items below
        var drawList = ImGui.GetForegroundDrawList();
        var tMin = min;
        var tMax = tMin + new Vector2(barW, barH);
        var radius = MathF.Min(10f * scale, barH * 0.35f);

        drawList.AddRectFilled(tMin + new Vector2(0f, 3f * scale),
            tMax + new Vector2(0f, 5f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                new Vector4(0f, 0f, 0f, 1f), 0.28f * ease)), radius + 2f * scale);
        var bg = SpheneUI.ColorUtils.LerpColor(SpheneUI.Colors.FrameBg,
            new Vector4(0.01f, 0.01f, 0.02f, 1f), 0.55f);
        bg.W = 0.96f * ease;
        drawList.AddRectFilled(tMin, tMax, SpheneUI.ColorUtils.ToU32(bg), radius);
        drawList.AddRect(tMin, tMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Colors.Border, 0.55f * ease)), radius);

        var textH = ImGui.GetTextLineHeight();
        var midY = tMin.Y + barH * 0.5f;
        drawList.AddCircleFilled(new Vector2(tMin.X + padX, midY), 3f * scale,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Palette.LuminousGold, ease)));
        drawList.AddText(new Vector2(tMin.X + padX + 8f * scale,
                midY - textH * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Colors.TextPrimary, ease)), message);

        var bx = tMax.X - padX * 0.8f - MathF.Max(0f, btnsW);
        for (var i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            if (DrawCapsuleButton(new Vector2(bx, midY - btnH * 0.5f),
                    new Vector2(btnWs[i], btnH), b.Label, b.Icon, b.Accent,
                    b.Tooltip, b.Enabled, ease, b.Solid))
            {
                b.OnClick();
            }
            bx += btnWs[i] + btnGap;
        }
    }

    /// <summary>Solid CTA variant - full accent fill, dark readable content,
    /// hover brightens. Visible on the near-black toast where tonal dies.</summary>
    private static bool DrawSolidCapsule(Vector2 pos, Vector2 size,
        string label, FontAwesomeIcon icon, Vector4 accent, string? tooltip,
        bool enabled, float dim, float radius, float scale)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var min = pos;
        var max = pos + size;
        var rawHovered = ImGui.IsMouseHoveringRect(min, max);
        var clicked = enabled && rawHovered
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var hovered = enabled && rawHovered;
        var held = enabled && rawHovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);

        var fill = SpheneUI.ColorUtils.LerpColor(accent, Vector4.One,
            held ? 0.35f : hovered ? 0.18f : 0f);
        fill.W *= 0.95f * dim;
        drawList.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), radius);
        drawList.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.25f * dim)), radius);

        var content = SpheneUI.ColorUtils.WithAlpha(
            new Vector4(0.10f, 0.08f, 0.03f, 1f), dim);
        var glyph = icon.ToIconString();
        Vector2 glyphSize;
        using (SpheneUI.IconFont.Push())
        {
            glyphSize = ImGui.CalcTextSize(glyph);
        }
        var labelSize = ImGui.CalcTextSize(label);
        var gap = 6f * scale;
        var groupW = glyphSize.X + gap + labelSize.X;
        var gx = min.X + (size.X - groupW) * 0.5f;
        var contentU32 = SpheneUI.ColorUtils.ToU32(content);
        using (SpheneUI.IconFont.Push())
        {
            drawList.AddText(new Vector2(gx, min.Y + (size.Y - glyphSize.Y) * 0.5f),
                contentU32, glyph);
        }
        drawList.AddText(new Vector2(gx + glyphSize.X + gap,
            min.Y + (size.Y - labelSize.Y) * 0.5f), contentU32, label);
        if (tooltip != null)
        {
            SpheneUI.AttachTooltipForeground(tooltip, hovered);
        }
        return clicked;
    }

    /// <summary>Width a capsule button needs for its content.</summary>
    internal static float CapsuleButtonWidth(float height, FontAwesomeIcon icon,
        string label)
    {
        float glyphW;
        using (SpheneUI.IconFont.Push())
        {
            glyphW = ImGui.CalcTextSize(icon.ToIconString()).X;
        }
        return height * 0.60f + glyphW + 6f * SpheneUI.Scale
            + ImGui.CalcTextSize(label).X;
    }

    /// <summary>
    /// Capsule-styled push button · a tonal rounded control with a centered
    /// icon + label in the accent color. The hit zone stays live when
    /// disabled so tooltips still reach the user; the click only counts when
    /// enabled. <paramref name="alpha"/> fades the whole control for
    /// container animations.
    /// </summary>
    internal static bool DrawCapsuleButton(Vector2 pos, Vector2 size,
        string label, FontAwesomeIcon icon, Vector4 accent, string? tooltip,
        bool enabled = true, float alpha = 1f, bool solid = false)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetForegroundDrawList();
        var radius = MathF.Min(SpheneUI.Metrics.ButtonRadius, size.Y * 0.25f);
        var dim = (enabled ? 1f : 0.45f) * alpha;

        if (solid)
        {
            return DrawSolidCapsule(pos, size, label, icon, accent, tooltip,
                enabled, dim, radius, scale);
        }

        // manual hit-test - the bar's visuals float on the foreground list and
        // its buttons must not depend on the host window's clip rect, which
        // cut the click zones whenever the bar crossed a child edge
        var min = pos;
        var max = pos + size;
        var rawHovered = ImGui.IsMouseHoveringRect(min, max);
        var clicked = enabled && rawHovered
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var hovered = enabled && rawHovered;
        var held = enabled && rawHovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);

        var scrim = SpheneUI.ColorUtils.LerpColor(SpheneUI.Colors.FrameBg,
            new Vector4(0.02f, 0.02f, 0.035f, 1f), 0.5f);
        scrim.W = MathF.Max(scrim.W, 0.55f) * alpha;
        drawList.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(scrim), radius);
        var fill = SpheneUI.ColorUtils.WithAlpha(held || hovered ? accent : Vector4.One,
            (held ? 0.22f : hovered ? 0.13f : 0.05f) * dim);
        drawList.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), radius);
        var borderCol = SpheneUI.ColorUtils.WithAlpha(
            SpheneUI.ColorUtils.LerpColor(SpheneUI.Colors.Border, accent,
                held || hovered ? 0.55f : 0.18f), 0.55f * dim);
        drawList.AddRect(min, max, SpheneUI.ColorUtils.ToU32(borderCol), radius);

        var glyph = icon.ToIconString();
        Vector2 glyphSize;
        using (SpheneUI.IconFont.Push())
        {
            glyphSize = ImGui.CalcTextSize(glyph);
        }
        var labelSize = ImGui.CalcTextSize(label);
        var gap = 6f * scale;
        var groupW = glyphSize.X + gap + labelSize.X;
        var gx = min.X + (size.X - groupW) * 0.5f;
        var iconCol = SpheneUI.ColorUtils.WithAlpha(
            SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, hovered ? 0.3f : 0.08f), dim);
        using (SpheneUI.IconFont.Push())
        {
            drawList.AddText(new Vector2(gx, min.Y + (size.Y - glyphSize.Y) * 0.5f),
                SpheneUI.ColorUtils.ToU32(iconCol), glyph);
        }
        var textCol = SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.TextPrimary,
            0.92f * dim);
        drawList.AddText(new Vector2(gx + glyphSize.X + gap,
            min.Y + (size.Y - labelSize.Y) * 0.5f),
            SpheneUI.ColorUtils.ToU32(textCol), label);
        if (tooltip != null)
        {
            SpheneUI.AttachTooltipForeground(tooltip, hovered);
        }
        return clicked;
    }
}
