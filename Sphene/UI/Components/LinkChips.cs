using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Utility;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Universal renderers for external links  ·  the same brand-chip language
/// everywhere a URL appears (profiles, community pages, directory cards,
/// settings). Click opens the link, hover previews the URL, the card
/// variant carries a brand mark resolved through
/// <see cref="LinkBrandPresets"/>.
/// </summary>
internal static class LinkChips
{
    /// <summary>
    /// Full-width link row (banner-style card): brand mark, label/URL text,
    /// trailing external-link glyph. The canonical look for link lists.
    /// </summary>
    public static void Card(string label, string url, string? icon, Vector4 accent, int index)
    {
        var scale = SpheneUI.Scale;
        var w = SpheneUI.Controls.BoundedAvailX();
        var h = 42f * scale * SpheneUI.Density.Factor;
        var dl = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton($"##link-card-{index}", new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var radius = 8f * scale;

        var hoverT = SpheneUI.Fx.HoverLiftT(ImGui.GetID($"linkcard{index}"), min, max);
        var fill = SpheneUI.Layout.NavRailBandColor();
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), radius);
        SpheneUI.Fx.HoverLift(dl, min, max, hoverT, radius, accent);

        var pad = 10f * scale;
        var markSide = MathF.Min(h - pad, ImGui.GetTextLineHeight() * 1.45f);
        var markMin = new Vector2(min.X + pad * 0.5f, min.Y + (h - markSide) * 0.5f);
        LinkBrandPresets.DrawMark(LinkBrandPresets.Find(icon), dl,
            markMin, markMin + new Vector2(markSide), accent);
        var textY = min.Y + (h - ImGui.GetTextLineHeight()) * 0.5f;
        var arrowW = ImGui.GetTextLineHeight();
        var text = string.IsNullOrWhiteSpace(label) ? url : label;
        var textX = markMin.X + markSide + 8f * scale;
        dl.PushClipRect(new Vector2(textX, min.Y),
            max - new Vector2(arrowW + pad, 0f), true);
        dl.AddText(new Vector2(textX, textY),
            ImGui.GetColorU32(ImGuiCol.Text), text);
        dl.PopClipRect();
        using (SpheneUI.Icons.PushFont())
        {
            dl.AddText(new Vector2(max.X - arrowW - pad * 0.5f, textY - 1f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    hovered ? accent : SpheneUI.Colors.TextMuted, 0.9f)),
                FontAwesomeIcon.ExternalLinkAlt.ToIconString());
        }
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            SpheneUI.AttachTooltip($"{url}--SEP--Right-click to copy");
        }
        if (clicked)
        {
            Util.OpenLink(url);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            ImGui.SetClipboardText(url);
        }
    }

    /// <summary>
    /// Compact inline chip: brand mark + label inside a small pill, for
    /// hero action rows, card footers and anywhere a full card is too loud.
    /// Returns the chip's width so callers can chain SameLine layouts.
    /// </summary>
    public static float Chip(string label, string url, string? icon, Vector4 accent, int index)
    {
        var scale = SpheneUI.Scale;
        var h = ImGui.GetFrameHeight() + 2f * scale;
        var mark = h - 6f * scale;
        var textW = ImGui.CalcTextSize(label).X;
        var w = mark + textW + 14f * scale;
        var dl = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton($"##link-chip-{index}", new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var radius = h * 0.5f;

        var hoverT = SpheneUI.Fx.HoverLiftT(ImGui.GetID($"linkchip{index}"), min, max);
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, 0.10f + 0.10f * hoverT)), radius);
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, 0.30f + 0.30f * hoverT)), radius);

        var markMin = new Vector2(min.X + 4f * scale, min.Y + (h - mark) * 0.5f);
        LinkBrandPresets.DrawMark(LinkBrandPresets.Find(icon), dl,
            markMin, markMin + new Vector2(mark), accent);
        dl.AddText(new Vector2(markMin.X + mark + 4f * scale,
            min.Y + (h - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text), label);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            SpheneUI.AttachTooltip($"{url}--SEP--Right-click to copy");
        }
        if (clicked)
        {
            Util.OpenLink(url);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            ImGui.SetClipboardText(url);
        }
        return w;
    }

    /// <summary>
    /// Card grid for a link collection (community Links tab, profile link
    /// lists). Renders nothing when the list is empty  ·  callers gate on
    /// count and show an <c>EmptyState</c> instead.
    /// </summary>
    public static void Grid(IReadOnlyList<(string Label, string Url, string? Icon)> links,
        Vector4 accent, string idPrefix)
    {
        for (var i = 0; i < links.Count; i++)
        {
            var l = links[i];
            Card(l.Label, l.Url, l.Icon, accent, i);
            if (i + 1 < links.Count)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
        }
    }
}
