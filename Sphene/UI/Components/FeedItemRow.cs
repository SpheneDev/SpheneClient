using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using Sphene.UI.Syncshell;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Activity-feed row: a bare kind glyph on the left (news / event / photo
/// glyph tinted by the owning shell's accent), title + meta line ("Shell · 2h"),
/// optional photo thumbnail on the right. Feeds the hub dashboard and any
/// "what's new" surface. Click → caller deep-links into the shell page.
/// </summary>
internal static class FeedItemRow
{
    /// <summary>Draws the row; returns true when clicked.</summary>
    public static bool Draw(string id, SyncshellCommunityFeedItemDto item,
        string texturePrefix, Vector4? fallbackAccent = null)
    {
        var scale = SpheneUI.Scale;
        var accent = SyncshellCommunityView.ParseAccent(item.AccentColor)
            ?? fallbackAccent ?? SpheneUI.Palette.CrystalBlue;
        var w = SpheneUI.Controls.BoundedAvailX();
        // photo rows get a big left cover thumb · others keep the kind glyph
        // - events join via hash-addressed preview art (community media cache)
        var mediaHash = item.ThumbnailMediaHash;
        var bigThumb = mediaHash != null
            || (item.Kind == SyncshellFeedItemKind.Photo
                && !string.IsNullOrEmpty(item.ThumbnailBase64));
        var density = SpheneUI.Density.Factor;
        var thumbW = bigThumb ? 128f * scale * density : 0f;
        var slot = bigThumb ? 0f : 30f * scale * density;
        var pad = 8f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var h = bigThumb
            ? 84f * scale * density
            : slot > 0
                ? MathF.Max(slot, lineH * 2.1f) + pad * 1.4f
                : lineH * 2.1f + pad * 1.4f;

        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##feedrow", new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();

        var dl = ImGui.GetWindowDrawList();
        var radius = SpheneUI.Metrics.ButtonRadius;
        var hoverT = SpheneUI.Fx.HoverLiftT(ImGui.GetID($"{id}lift"), min, max);
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()), radius);
        SpheneUI.Fx.HoverLift(dl, min, max, hoverT, radius, accent);

        float textX;
        if (bigThumb)
        {
            // large left cover · rounded, cropped, never squished
            var wrap = SpheneUI.Media.Texture($"{texturePrefix}feed:{item.Kind}:{item.Id}:{mediaHash}",
                mediaHash != null
                    ? () => MarkdownComposer.MediaImageSource($"media:{mediaHash}")
                    : () => Convert.FromBase64String(item.ThumbnailBase64!));
            var tMin = new Vector2(min.X + pad * 0.5f, min.Y + (h - 72f * scale * density) * 0.5f);
            var tMax = tMin + new Vector2(thumbW - pad, 72f * scale * density);
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, tMin, tMax,
                    ImageFit.Cover, rounding: 8f * scale);
            }
            else
            {
                dl.AddRectFilled(tMin, tMax,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()),
                    8f * scale);
            }
            dl.AddRect(tMin, tMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.35f)),
                8f * scale);
            textX = tMax.X + pad;
        }
        else
        {
            // Kind glyph · reads the item type at a glance.
            var slotMin = new Vector2(min.X + pad * 0.75f, min.Y + (h - slot) * 0.5f);
            var slotMax = slotMin + new Vector2(slot, slot);
            var kindIcon = item.Kind switch
            {
                SyncshellFeedItemKind.Event => FontAwesomeIcon.CalendarAlt,
                SyncshellFeedItemKind.Photo => FontAwesomeIcon.Image,
                SyncshellFeedItemKind.Blog => FontAwesomeIcon.BookOpen,
                SyncshellFeedItemKind.Comment => FontAwesomeIcon.CommentDots,
                SyncshellFeedItemKind.Reaction => FontAwesomeIcon.Heart,
                SyncshellFeedItemKind.Rsvp => FontAwesomeIcon.CalendarCheck,
                _ => FontAwesomeIcon.Bullhorn,
            };
            // Bare kind glyph in the slot · the shell accent stays the
            // only kind tint; hover eases it brighter.
            SpheneUI.Layout.SlotIcon(dl, slotMin, slot, kindIcon,
                SpheneUI.ColorUtils.WithAlpha(accent, 0.65f + 0.35f * hoverT));
            textX = slotMax.X + pad;
        }

        // Text block · title over "Shell · kind · time" meta.
        var textRight = max.X - pad;
        var titleY = min.Y + (h - lineH * 2f) * 0.5f;
        dl.PushClipRect(new Vector2(textX, min.Y), new Vector2(textRight, max.Y), true);
        dl.AddText(new Vector2(textX, titleY),
            ImGui.GetColorU32(ImGuiCol.Text),
            SpheneUI.Text.Ellipsize(item.Title, MathF.Max(40f, textRight - textX)));
        // Activity rows lead with the actor; blog rows have no shell so the
        // author name carries the origin. v1 rows keep "shell · time".
        var shellName = string.IsNullOrWhiteSpace(item.GroupAlias)
            ? item.GroupGID : item.GroupAlias!;
        var meta = !string.IsNullOrWhiteSpace(item.ActorDisplayName)
            && item.ActorDisplayName is { } actor
            ? (item.Kind == SyncshellFeedItemKind.Blog
                ? $"{actor} · {RelativeTime(item.TimestampUtc)}"
                : $"{actor} · {shellName} · {RelativeTime(item.TimestampUtc)}")
            : $"{shellName} · {RelativeTime(item.TimestampUtc)}";
        dl.AddText(new Vector2(textX, titleY + lineH),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
            SpheneUI.Text.Ellipsize(meta, MathF.Max(40f, textRight - textX)));
        dl.PopClipRect();

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (!string.IsNullOrWhiteSpace(item.Snippet))
            {
                SpheneUI.AttachTooltip(item.Snippet!);
            }
        }
        return clicked;
    }

    /// <summary>"just now" / "12m" / "3h" / "Mon 14:00" style short forms.</summary>
    public static string RelativeTime(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta < TimeSpan.Zero) return "soon";
        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours}h ago";
        if (delta < TimeSpan.FromDays(7)) return $"{(int)delta.TotalDays}d ago";
        var local = utc.ToLocalTime();
        return SpheneUI.Text.FormatShortDate(local);
    }
}
