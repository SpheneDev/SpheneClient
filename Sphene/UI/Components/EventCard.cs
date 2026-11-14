using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Date-block event card: a calendar plate (day / month) on the left, title
/// + meta to its right, an optional countdown pill docked right. Shared by
/// community Events tabs, the hub feed and upcoming-event widgets.
/// </summary>
internal static class EventCard
{
    /// <summary>Draws the card; returns true when the body was clicked.</summary>
    public static bool Draw(string id, SyncshellEventDto ev, Vector4 accent,
        string? shellName = null)
    {
        var scale = SpheneUI.Scale;
        var w = SpheneUI.Controls.BoundedAvailX();
        var plate = 44f * scale * SpheneUI.Density.Factor;
        var pad = 8f * scale;
        var hasMeta = shellName != null || ev.Location != null;
        var h = MathF.Max(plate, ImGui.GetTextLineHeight() * (hasMeta ? 2.1f : 1.4f)) + pad * 1.5f;

        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##eventcard", new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();

        var dl = ImGui.GetWindowDrawList();
        var radius = SpheneUI.Metrics.ButtonRadius;
        var hoverT = SpheneUI.Fx.HoverLiftT(ImGui.GetID($"{id}lift"), min, max);
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()), radius);
        SpheneUI.Fx.HoverLift(dl, min, max, hoverT, radius, accent);

        // Calendar badge · day number over month abbrev, a flat accent-tinted
        // fill + hairline that lifts on hover (no saturated gradient tile).
        var local = ev.StartUtc.ToLocalTime();
        var plateMin = new Vector2(min.X + pad * 0.75f, min.Y + (h - plate) * 0.5f);
        var plateMax = plateMin + new Vector2(plate, plate);
        dl.AddRectFilled(plateMin, plateMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, 0.10f + 0.06f * hoverT)), 6f * scale);
        dl.AddRect(plateMin, plateMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, 0.40f + 0.30f * hoverT)), 6f * scale,
            ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
        var day = local.ToString("dd");
        var month = local.ToString("MMM").ToUpperInvariant();
        var pcx = plateMin.X + plate * 0.5f;
        // Real font faces - the day takes the 20px heading face and the
        // month stays on the body face; SetWindowFontScale/AddText-size
        // tricks raster-scale glyphs and read blurry on the plate.
        var dayCol = ImGui.GetColorU32(ImGuiCol.Text);
        Vector2 daySize, monthSize;
        using (SpheneUI.HeadingFont.Push())
        {
            daySize = ImGui.CalcTextSize(day);
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
                new Vector2(pcx - daySize.X * 0.5f, plateMin.Y + plate * 0.30f - daySize.Y * 0.5f),
                dayCol, day);
        }
        monthSize = ImGui.CalcTextSize(month);
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
            new Vector2(pcx - monthSize.X * 0.5f, plateMin.Y + plate * 0.68f - monthSize.Y * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), month);

        // Countdown pill docked to the right edge.
        var countdown = CountdownText(ev.StartUtc);
        var pillW = countdown != null ? ImGui.CalcTextSize(countdown).X + 14f * scale : 0f;
        var textX = plateMax.X + pad;

        // Optional 16:9 preview thumb beside the plate - the dedicated
        // preview image first, the 3:1 banner as fallback, nothing when the
        // event has no media (no broken-image placeholder).
        byte[]? thumbBytes = null;
        var thumbHash = ev.PreviewMediaHash ?? ev.ImageMediaHash;
        if (thumbHash != null)
        {
            thumbBytes = MarkdownComposer.MediaImageSource($"media:{thumbHash}");
        }
        if (thumbBytes == null && ev.PreviewBase64 != null)
        {
            thumbBytes = Convert.FromBase64String(ev.PreviewBase64);
        }
        else if (thumbBytes == null && ev.ImageBase64 != null)
        {
            thumbBytes = Convert.FromBase64String(ev.ImageBase64);
        }
        if (thumbBytes != null)
        {
            // Hash-keyed: replacing the image yields a new cache key, so a
            // stale texture never survives a re-upload.
            var wrap = SpheneUI.Media.Texture($"eventpv:{ev.Id}:{thumbHash}", thumbBytes);
            if (wrap != null)
            {
                var tH = h - pad;
                var tMin = new Vector2(plateMax.X + pad * 0.5f, min.Y + pad * 0.5f);
                var tMax = tMin + new Vector2(tH * 16f / 9f, tH);
                SpheneUI.Media.ImageInRect(dl, wrap, tMin, tMax,
                    ImageFit.Cover, rounding: 6f * scale);
                textX = tMax.X + pad * 0.5f;
            }
        }
        var textRight = max.X - pad - (countdown != null ? pillW + pad * 0.5f : 0f);

        var lineH = ImGui.GetTextLineHeight();
        var title = SpheneUI.Text.Ellipsize(ev.Title, MathF.Max(40f, textRight - textX));
        dl.PushClipRect(new Vector2(textX, min.Y), new Vector2(textRight, max.Y), true);
        dl.AddText(new Vector2(textX, min.Y + (h - lineH * (hasMeta ? 1.9f : 1f)) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text), title);
        if (hasMeta)
        {
            var meta = shellName != null && ev.Location != null
                ? $"{shellName} · {ev.Location}"
                : shellName ?? ev.Location!;
            dl.AddText(new Vector2(textX, min.Y + (h - lineH * (hasMeta ? 1.9f : 1f)) * 0.5f + lineH),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), meta);
        }
        dl.PopClipRect();

        if (countdown != null)
        {
            var pillH = lineH + 6f * scale;
            var pillMin = new Vector2(max.X - pad - pillW, min.Y + (h - pillH) * 0.5f);
            var urgent = ev.StartUtc - DateTime.UtcNow < TimeSpan.FromDays(1);
            var pillTint = urgent ? accent : SpheneUI.Colors.TextMuted;
            dl.AddRectFilled(pillMin, pillMin + new Vector2(pillW, pillH),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(pillTint, 0.16f)), pillH * 0.5f);
            dl.AddText(pillMin + new Vector2(7f * scale, 3f * scale),
                SpheneUI.ColorUtils.ToU32(urgent ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextMuted),
                countdown);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var tip = $"{local:ddd, dd MMM yyyy · HH:mm}"
                + (ev.EndUtc.HasValue ? $" - {ev.EndUtc.Value.ToLocalTime():HH:mm}" : string.Empty)
                + (string.IsNullOrWhiteSpace(ev.Description) ? string.Empty : $"--SEP--{ev.Description}");
            SpheneUI.AttachTooltip(tip);
        }
        return clicked;
    }

    /// <summary>"Today" / "in 2d" / "in 5h"  ·  null for events far out or past.</summary>
    private static string? CountdownText(DateTime startUtc)
    {
        var delta = startUtc - DateTime.UtcNow;
        if (delta < TimeSpan.Zero)
        {
            return delta > -TimeSpan.FromHours(3) ? "Live" : null;
        }
        if (delta < TimeSpan.FromHours(1)) return $"in {Math.Max(1, (int)delta.TotalMinutes)}m";
        if (delta < TimeSpan.FromDays(1)) return $"in {(int)delta.TotalHours}h";
        if (delta < TimeSpan.FromDays(7)) return $"in {(int)delta.TotalDays}d";
        return null;
    }
}
