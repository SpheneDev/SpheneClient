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
    /// <summary>Draws the row; returns true when clicked.
    /// <paramref name="extraMeta"/> appends a caller-supplied segment to the
    /// meta line (e.g. "12 views · 3 comments" on Chronicle list rows).
    /// <paramref name="onActorClick"/> turns the actor-name segment into a
    /// separate hit target (profile link) - the row click stays on the card.</summary>
    public static bool Draw(string id, SyncshellCommunityFeedItemDto item,
        string texturePrefix, Vector4? fallbackAccent = null,
        string? extraMeta = null, Action<string>? onActorClick = null)
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
        // The actor-name hit zone is submitted after the row button and must
        // be allowed to steal HoveredId - without this flag the row claims
        // hover first and the link can never fire.
        ImGui.SetItemAllowOverlap();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();
        // The actor hit zone is re-positioned by absolute cursor later - the
        // post-row cursor must be restored or the next row overlaps this one.
        var resumeCursor = ImGui.GetCursorScreenPos();

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
        var actorName = item.ActorDisplayName;
        var actorLinkable = onActorClick != null && item.ActorUid != null
            && !string.IsNullOrWhiteSpace(actorName);
        var metaTail = item.Kind == SyncshellFeedItemKind.Blog
            ? RelativeTime(item.TimestampUtc)
            : $"{shellName} · {RelativeTime(item.TimestampUtc)}";
        var meta = actorLinkable || string.IsNullOrWhiteSpace(actorName)
            ? $"{shellName} · {RelativeTime(item.TimestampUtc)}"
            : (item.Kind == SyncshellFeedItemKind.Blog
                ? $"{actorName} · {RelativeTime(item.TimestampUtc)}"
                : $"{actorName} · {metaTail}");
        if (!string.IsNullOrEmpty(extraMeta))
        {
            meta += " · " + extraMeta;
        }
        var metaY = titleY + lineH;
        if (actorLinkable)
        {
            // Split meta: the actor name is its own hit rect + accent tint,
            // the remainder stays muted - the author action never overlaps
            // the row click.
            var actorW = MathF.Min(
                ImGui.CalcTextSize(actorName).X + 4f * scale, textRight - textX);
            var actorRect = ImGui.GetID($"{id}actor");
            var actorHovered = ImGui.IsMouseHoveringRect(
                new Vector2(textX, metaY), new Vector2(textX + actorW, metaY + lineH));
            dl.AddText(new Vector2(textX, metaY),
                SpheneUI.ColorUtils.ToU32(actorHovered
                    ? accent : SpheneUI.Colors.TextMuted),
                SpheneUI.Text.Ellipsize(actorName!, MathF.Max(24f, actorW)));
            var tailX = textX + actorW + 2f * scale;
            dl.AddText(new Vector2(tailX, metaY),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
                SpheneUI.Text.Ellipsize("· " + metaTail + (string.IsNullOrEmpty(extraMeta)
                    ? string.Empty : " · " + extraMeta), MathF.Max(24f, textRight - tailX)));
            dl.PopClipRect();
            // The row button above opted into overlap, so this hit zone can
            // claim hover/click inside its own rect despite submitting later.
            ImGui.PushID(actorRect);
            ImGui.SetCursorScreenPos(new Vector2(textX, metaY));
            var actorClicked = ImGui.InvisibleButton("##actor",
                new Vector2(actorW, lineH));
            ImGui.PopID();
            ImGui.SetCursorScreenPos(resumeCursor);
            if (actorHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                SpheneUI.AttachTooltip("View profile");
            }
            if (actorClicked)
            {
                onActorClick!(item.ActorUid!);
            }
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (!actorHovered && !string.IsNullOrWhiteSpace(item.Snippet))
                {
                    SpheneUI.AttachTooltip(item.Snippet!);
                }
            }
            return clicked && !actorClicked;
        }
        dl.AddText(new Vector2(textX, metaY),
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
