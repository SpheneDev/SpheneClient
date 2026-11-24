using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Bindings.ImGui;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.UI.Design;
using Sphene.UI.Syncshell;
using Sphene.Utils;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Compact identity-first feed row - one shared skeleton for every feed
/// kind: a fixed 38px identity avatar on the left, an inline
/// "Name · time" identity header, the activity + content text stack next
/// to it and an optional ~4:3 preview in a dedicated right-side media
/// slot. Identity and activity time live on one line; media never
/// competes with timestamps. Draws via the drawlist inside the caller's
/// row so the caller owns spacing and separators.
/// </summary>
public static class FeedItemRow
{
    // Unscaled row geometry - Draw and MeasureHeight share one source of
    // truth so measured heights always match painted rows.
    private const float PadX = 12f;
    private const float PadY = 10f;
    private const float AvatarD = 38f;
    private const float Gutter = 11f;
    private const float MediaGap = 10f;
    private const float IdentGap = 7f;
    private const float LineGap = 4f;
    private const float ThumbW = 104f;
    private const float ThumbH = 78f;
    private const float MinTextW = 160f;

    /// <summary>Draws a feed row; returns true when the row body was
    /// clicked. <paramref name="onActorClick"/> turns the actor avatar +
    /// name into a profile link (avatar and name share one hit zone).
    /// <paramref name="profileFor"/> resolves actor avatars by uid;
    /// <paramref name="shellAvatarFor"/> resolves syncshell avatars for
    /// shell-owned rows - events always wear their syncshell's identity
    /// even when a creator uid is attached. <paramref name="onThumbClick"/>
    /// turns the right-side thumbnail into a photo-preview jump that wins
    /// over the row click.</summary>
    public static bool Draw(string id, SyncshellCommunityFeedItemDto item,
        string texturePrefix, Vector4? fallbackAccent = null,
        string? extraMeta = null, Action<string>? onActorClick = null,
        Func<string, SpheneProfileData?>? profileFor = null,
        Func<string, IDalamudTextureWrap?>? shellAvatarFor = null,
        Action<SyncshellCommunityFeedItemDto>? onThumbClick = null,
        Action<SyncshellCommunityFeedItemDto>? onLikeClick = null,
        Action<SyncshellCommunityFeedItemDto>? onCommentClick = null,
        Action<SyncshellCommunityFeedItemDto, string>? onReactionClick = null)
    {
        var drawList = ImGui.GetWindowDrawList();
        var scale = SpheneUI.Scale;
        var accent = SyncshellCommunityView.ParseAccent(item.AccentColor)
            ?? fallbackAccent ?? SpheneUI.Palette.CrystalBlue;
        var availX = SpheneUI.Controls.BoundedAvailX();

        var padX = PadX * scale;
        var padY = PadY * scale;
        var avatarD = AvatarD * scale;
        var gutter = Gutter * scale;
        var mediaGap = MediaGap * scale;
        var identGap = IdentGap * scale;
        var lineGap = LineGap * scale;
        var lineH = ImGui.GetTextLineHeight();
        var thumbW = ThumbW * scale;
        var thumbH = ThumbH * scale;
        var minTextW = MinTextW * scale;

        var (kindLabel, kindIcon) = KindStyle(item.Kind);

        var hasShell = !string.IsNullOrWhiteSpace(item.GroupGID)
            || !string.IsNullOrWhiteSpace(item.GroupAlias);
        var shellName = string.IsNullOrWhiteSpace(item.GroupAlias)
            ? item.GroupGID : item.GroupAlias;
        var isActorIdentity = item.ActorUid != null
            && item.Kind != SyncshellFeedItemKind.Event;
        var actorLinkable = isActorIdentity && onActorClick != null
            && !string.IsNullOrWhiteSpace(item.ActorDisplayName);
        var actorProfile = isActorIdentity && item.ActorUid != null
            ? profileFor?.Invoke(item.ActorUid) : null;

        var name = isActorIdentity
            ? item.ActorDisplayName ?? (hasShell ? shellName : kindLabel)
            : hasShell ? shellName! : kindLabel;
        var (action, context, actionStrong, contextStrong) = ComposeText(item,
            hasShell ? shellName : null, extraMeta);

        IDalamudTextureWrap? avatar = null;
        if (isActorIdentity && actorProfile != null
            && !string.IsNullOrEmpty(actorProfile.Base64ProfilePicture))
        {
            avatar = SpheneUI.Media.Texture(
                $"{texturePrefix}-avatar:{item.ActorUid}:"
                    + SyncshellCommunityView.MediaVersion(null, actorProfile.Base64ProfilePicture),
                actorProfile.ImageData.Value);
        }
        if (avatar == null && !isActorIdentity && hasShell && item.GroupGID != null)
        {
            avatar = shellAvatarFor?.Invoke(item.GroupGID);
        }

        var thumb = ResolveThumb(item, texturePrefix);
        var showThumb = thumb != null
            && availX - (padX * 2f) - avatarD - gutter - thumbW - mediaGap >= minTextW;
        // Text column width - nameX..bodyRight relative to the row origin.
        var textW = availX - padX * 2f - avatarD - gutter
            - (showThumb ? thumbW + mediaGap : 0f);
        var (snippetLines, hasTail) = ExtraContent(item, textW,
            interactive: onLikeClick != null || onCommentClick != null);

        // Event rows: the schedule lives labeled in the context line; the
        // identity line only carries a real activity timestamp
        // (ActivityUtc), never a fabricated one. Other kinds always carry
        // activity time.
        var tsText = item.Kind == SyncshellFeedItemKind.Event
            ? (FeedItemSemantics.EventTimes(item.TimestampUtc, item.ActivityUtc)
                    .ShowRelativeActivity
                ? RelativeTime(item.ActivityUtc!.Value)
                : "")
            : RelativeTime(item.TimestampUtc);
        var tsW = string.IsNullOrEmpty(tsText) ? 0f : ImGui.CalcTextSize(tsText).X;

        var lines = context != null ? 3 : 2;
        var textH = lines * lineH + identGap + (lines - 2) * lineGap
            + ExtraHeight(snippetLines, hasTail, lineH, lineGap);
        var bodyH = MathF.Max(MathF.Max(avatarD, textH),
            showThumb ? thumbH : 0f);
        var rowH = padY * 2f + bodyH;
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(availX, rowH));
        var rowHovered = ImGui.IsItemHovered();

        var nameW = ImGui.CalcTextSize(name).X;
        var nameX = origin.X + padX + avatarD + gutter;
        var rowRight = origin.X + availX - padX;
        var thumbX = rowRight - thumbW;
        var bodyRight = showThumb ? thumbX - mediaGap : rowRight;
        // Identity line: "Name · ts" - the timestamp rides the same inline
        // flow as the name, so the name must leave it room.
        var sep = " · ";
        var sepW = tsW > 0f ? ImGui.CalcTextSize(sep).X : 0f;
        var nameMax = bodyRight - nameX - sepW - tsW;
        var nameShownW = MathF.Min(nameW, MathF.Max(0f, nameMax));
        var avatarPos = origin + new Vector2(padX, padY);
        // Hit zones ride the row's single InvisibleButton - a second button
        // overlapping it can never win ImGui's first-claimed HoveredId.
        // Rects are screen-space; resolved via the mouse position on click.
        var actorRect = actorLinkable
            ? new Vector4(avatarPos.X, avatarPos.Y,
                avatarPos.X + avatarD + gutter + nameShownW,
                avatarPos.Y + MathF.Max(avatarD, lineH))
            : Vector4.Zero;
        var thumbRect = showThumb && onThumbClick != null
            ? new Vector4(thumbX, origin.Y + padY,
                thumbX + thumbW, origin.Y + padY + thumbH)
            : Vector4.Zero;
        var actorHovered = actorLinkable && ImGui.IsMouseHoveringRect(
            new Vector2(actorRect.X, actorRect.Y),
            new Vector2(actorRect.Z, actorRect.W));
        var thumbHovered = thumbRect != Vector4.Zero && ImGui.IsMouseHoveringRect(
            new Vector2(thumbRect.X, thumbRect.Y),
            new Vector2(thumbRect.Z, thumbRect.W));

        if (rowHovered || actorHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            drawList.AddRectFilled(origin, origin + new Vector2(availX, rowH),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    SpheneUI.Colors.TextPrimary, 0.05f)), 4f * scale);
            ImGui.SetTooltip(thumbHovered ? "Open photo"
                : actorLinkable && actorHovered ? name!
                : tsText.Length > 0 ? $"{kindLabel} · {tsText}" : kindLabel);
        }

        if (avatar != null)
        {
            drawList.AddImageRounded(avatar.Handle, avatarPos,
                avatarPos + new Vector2(avatarD), Vector2.Zero, Vector2.One,
                SpheneUI.ColorUtils.ToU32(Vector4.One), avatarD * 0.5f);
            drawList.AddCircle(avatarPos + new Vector2(avatarD * 0.5f), avatarD * 0.5f - 0.5f,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Border));
        }
        else
        {
            SpheneUI.Media.PlaceholderInRect(drawList, avatarPos,
                avatarPos + new Vector2(avatarD), avatarD * 0.5f,
                border: true,
                icon: isActorIdentity ? FontAwesomeIcon.User : kindIcon);
        }

        // All columns share one top origin (rowTopY): avatar, identity
        // line and thumbnail start at padY; extra height from a taller
        // thumbnail extends below the text, never centers it. identGap
        // separates the creator from the content; lineGap spaces content
        // lines.
        var l1Y = origin.Y + padY;
        var l2Y = l1Y + lineH + identGap;
        var l3Y = l2Y + lineH + lineGap;

        // Actor names wear the user's chosen profile accent; shell-owned
        // rows (venue/syncshell identities) get a Users glyph marker so
        // a community name reads differently from a person at a glance.
        var nameColor = SpheneUI.Colors.TextPrimary;
        if (isActorIdentity && actorProfile != null)
        {
            nameColor = actorProfile.ProfileTheme.Value.AccentColor(accent);
        }
        var nameDrawX = nameX;
        if (!isActorIdentity && hasShell)
        {
            var marker = FontAwesomeIcon.Users.ToIconString();
            float markerW;
            using (SpheneUI.Icons.PushFont())
            {
                markerW = ImGui.CalcTextSize(marker).X + 5f * scale;
                drawList.AddText(new Vector2(nameX, l1Y),
                    SpheneUI.ColorUtils.ToU32(accent), marker);
            }
            nameDrawX += markerW;
        }
        if (actorLinkable && actorHovered)
        {
            drawList.AddLine(new Vector2(nameDrawX, l1Y + lineH - 1f),
                new Vector2(nameDrawX + nameShownW, l1Y + lineH - 1f),
                SpheneUI.ColorUtils.ToU32(nameColor));
        }
        EllipsisEnd(drawList, new Vector2(nameDrawX, l1Y),
            nameMax - (nameDrawX - nameX), name!, nameColor, medium: true);
        if (tsW > 0f)
        {
            var muted = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted);
            drawList.AddText(new Vector2(nameDrawX + nameShownW, l1Y), muted, sep);
            drawList.AddText(new Vector2(nameDrawX + nameShownW + sepW, l1Y),
                muted, tsText);
        }

        var actionX = nameX;
        var actionW = bodyRight - actionX;
        if (item.Kind is SyncshellFeedItemKind.Comment
            or SyncshellFeedItemKind.Reaction or SyncshellFeedItemKind.Rsvp)
        {
            // Reaction rows carry the emoji glyph in Snippet - draw the
            // sprite; legacy enum-name rows resolve through the canonical
            // map, unparseable snippets get the row's kind icon.
            var reactionEmoji = item.Kind == SyncshellFeedItemKind.Reaction
                ? FeedItemSemantics.ReactionEmoji(item.Snippet) : null;
            var gW = 0f;
            var gY = l2Y + MathF.Max(0f, (lineH - ImGui.GetTextLineHeight()) * 0.5f);
            if (reactionEmoji != null)
            {
                gW = lineH;
                SpheneUI.Emoji.SpriteAt(drawList, new Vector2(actionX, gY), lineH, reactionEmoji);
            }
            else
            {
                var glyph = kindIcon.ToIconString();
                using (SpheneUI.Icons.PushFont())
                {
                    gW = ImGui.CalcTextSize(glyph).X;
                    drawList.AddText(new Vector2(actionX, gY),
                        SpheneUI.ColorUtils.ToU32(accent), glyph);
                }
            }
            actionX += gW + 5f * scale;
            actionW -= gW + 5f * scale;
        }
        EllipsisEnd(drawList, new Vector2(actionX, l2Y), actionW, action,
            actionStrong ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextSecondary,
            medium: actionStrong);
        if (context != null)
        {
            EllipsisEnd(drawList, new Vector2(nameX, l3Y), bodyRight - nameX,
                context, contextStrong
                    ? SpheneUI.Colors.TextPrimary
                    : SpheneUI.Colors.TextSecondary,
                medium: contextStrong);
        }

        // Chronicle preview + engagement strip: wrapped snippet lines tuck
        // under the title, the last line ends in an inline accent "… more"
        // jump (X-style); the tail line carries muted icon/count stats.
        // The whole row is the click target, so the link is honest.
        var tailAnchor = context != null ? l3Y : l2Y;
        if (snippetLines is { Count: > 0 })
        {
            var sy = tailAnchor + lineH + lineGap;
            var secU = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextSecondary);
            for (var i = 0; i < snippetLines.Count - 1; i++)
            {
                drawList.AddText(new Vector2(nameX, sy + i * lineH),
                    secU, snippetLines[i]);
            }
            var lastY = sy + (snippetLines.Count - 1) * lineH;
            var moreW = ImGui.CalcTextSize(" more").X;
            var dotsW = ImGui.CalcTextSize("…").X;
            var lastShown = FitToWidth(snippetLines[^1], textW - moreW - dotsW);
            if (!lastShown.EndsWith('…'))
            {
                lastShown += "…";
            }
            drawList.AddText(new Vector2(nameX, lastY), secU, lastShown);
            drawList.AddText(
                new Vector2(nameX + ImGui.CalcTextSize(lastShown).X, lastY),
                SpheneUI.ColorUtils.ToU32(accent), " more");
            tailAnchor = lastY;
        }
        var likeRect = Vector4.Zero;
        var commentRect = Vector4.Zero;
        var chipRects = new List<(string Emoji, Vector4 Rect)>();
        var addRect = Vector4.Zero;
        if (hasTail)
        {
            var engageable = item.Kind is SyncshellFeedItemKind.News
                or SyncshellFeedItemKind.Event or SyncshellFeedItemKind.Blog;
            chipRects.Clear();
            DrawStatsLine(drawList, item, nameX,
                tailAnchor + lineH + lineGap, lineH, scale, accent, textW,
                likeClickable: engageable && onLikeClick != null,
                commentClickable: engageable && onCommentClick != null,
                out likeRect, out commentRect, out addRect,
                chipsClickable: engageable && onReactionClick != null,
                chipRects: chipRects);
            SpheneUI.EmojiPickerHook?.Draw(FeedPickerId(id));
        }

        if (showThumb)
        {
            var thumbPos = new Vector2(thumbX, origin.Y + padY);
            DrawThumbFit(drawList, thumb!, thumbPos,
                new Vector2(thumbW, thumbH), accent);
            if (thumbHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                drawList.AddRect(thumbPos,
                    thumbPos + new Vector2(thumbW, thumbH),
                    SpheneUI.ColorUtils.ToU32(accent), 6f * scale,
                    ImDrawFlags.None, 1.5f);
            }
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, rowH));
        if (!clicked) return false;
        var mouse = ImGui.GetIO().MousePos;
        bool Inside(Vector4 r) => r != Vector4.Zero
            && mouse.X >= r.X && mouse.X < r.Z
            && mouse.Y >= r.Y && mouse.Y < r.W;
        if (Inside(likeRect))
        {
            onLikeClick!(item);
            return false;
        }
        if (Inside(commentRect))
        {
            onCommentClick!(item);
            return false;
        }
        foreach (var (emoji, rect) in chipRects)
        {
            if (!Inside(rect)) continue;
            onReactionClick!(item, emoji);
            return false;
        }
        if (addRect != Vector4.Zero && Inside(addRect))
        {
            SpheneUI.EmojiPickerHook?.OpenFlyout(FeedPickerId(id),
                emoji => onReactionClick!(item, emoji));
            return false;
        }
        if (thumbRect != Vector4.Zero
            && mouse.X >= thumbRect.X && mouse.X < thumbRect.Z
            && mouse.Y >= thumbRect.Y && mouse.Y < thumbRect.W)
        {
            onThumbClick!(item);
            return false;
        }
        if (actorRect != Vector4.Zero
            && mouse.X >= actorRect.X && mouse.X < actorRect.Z
            && mouse.Y >= actorRect.Y && mouse.Y < actorRect.W)
        {
            onActorClick!(item.ActorUid!);
            return false;
        }
        return true;
    }

    /// <summary>Optional content the row appends under the base stack:
    /// a Chronicle preview (server-stripped body wrapped to at most two
    /// text-column lines, ending in the inline "… more" jump) plus a
    /// stats tail on any row that carries engagement counters.</summary>
    private static (List<string>? Snippet, bool Tail) ExtraContent(
        SyncshellCommunityFeedItemDto item, float textW, bool interactive = false)
    {
        List<string>? snippet = null;
        if (item.Kind == SyncshellFeedItemKind.Blog
            && !string.IsNullOrWhiteSpace(item.Snippet))
        {
            var wrapped = SpheneUI.Text.WrapLines(item.Snippet, textW, 2);
            if (wrapped.Count > 0) snippet = wrapped;
        }
        // Interactive tails paint like/comment icons even at zero, so the
        // line exists whenever the row can react at all - MeasureHeight
        // must compute with the same flag or sizing drifts a line off.
        var engageable = interactive
            && item.Kind is SyncshellFeedItemKind.News
                or SyncshellFeedItemKind.Event or SyncshellFeedItemKind.Blog;
        var tail = item.ReactionCount > 0 || item.CommentCount > 0
            || item.ViewCount > 0 || engageable;
        return (snippet, tail);
    }

    /// <summary>Vertical space <see cref="ExtraContent"/> adds to the text
    /// column: snippet lines stack tightly at lineH, every block is one
    /// lineGap below the line above it.</summary>
    private static float ExtraHeight(List<string>? snippet, bool tail,
        float lineH, float lineGap)
        => (snippet is { Count: > 0 } ? lineGap + snippet.Count * lineH : 0f)
            + (tail ? lineGap + lineH + 2f * PillPadY * SpheneUI.Scale : 0f);

    /// <summary>Unscaled vertical air above and below the pill contents -
    /// the engagement capsules are noticeably taller than the bare text
    /// line was, so the tail metric carries the extra on both the measure
    /// and the draw path through <see cref="ExtraHeight"/>.</summary>
    private const float PillPadY = 3.5f;

    /// <summary>X-style engagement strip: muted icon+count pairs. Reaction
    /// and comment icons paint even at zero when their click callback is
    /// wired - a quiet row still invites the first like/jump - while bare
    /// counters (views) stay zero-suppressed. <paramref name="likeRect"/>/
    /// <paramref name="commentRect"/> report the icon+count hit boxes so the
    /// row's single invisible button can route clicks; hovered stats tint
    /// accent and show the hand.</summary>
    /// <summary>Per-row emoji picker flyout id - stable across frames so the
    /// overlay stays attached to the row that opened it.</summary>
    private static string FeedPickerId(string rowId) => $"feed-add-{rowId}";

    private static void DrawStatsLine(ImDrawListPtr drawList,
        SyncshellCommunityFeedItemDto item, float x, float y,
        float lineH, float scale, Vector4 accent, float width,
        bool likeClickable, bool commentClickable,
        out Vector4 likeRect, out Vector4 commentRect, out Vector4 addRect,
        bool chipsClickable = false,
        List<(string Emoji, Vector4 Rect)>? chipRects = null)
    {
        // Two capsules: the stats pill (like/comment/views) and the reactions
        // pill (emoji chips + trailing add affordance). Both render inside
        // the existing line metric - the row height contract is unchanged.
        var iconGap = 4f * scale;
        var pillGap = 8f * scale;
        likeRect = Vector4.Zero;
        commentRect = Vector4.Zero;
        addRect = Vector4.Zero;

        // ---- measure the stats segments first so the capsule wraps them
        var likeCount = item.LikeCount ?? item.ReactionCount;
        var segs = new List<(FontAwesomeIcon Icon, int? Count, bool AlwaysShow,
            bool Clickable, bool Active, int Slot)>();
        segs.Add((FontAwesomeIcon.ThumbsUp, likeCount, likeClickable,
            likeClickable, item.LikedByMe, 0));
        segs.Add((FontAwesomeIcon.Comment, item.CommentCount, commentClickable,
            commentClickable, false, 1));
        segs.Add((FontAwesomeIcon.Eye, item.ViewCount, false, false, false, 2));
        var shown = new List<(FontAwesomeIcon Icon, int? Count, bool Clickable,
            bool Active, int Slot, float W)>();
        foreach (var s in segs)
        {
            if (!s.AlwaysShow && s.Count is not > 0) continue;
            var gw = ReactionPillBar.CenteredIconWidth(s.Icon, lineH);
            var num = s.Count is > 0 ? s.Count.Value.ToString() : null;
            var w = gw + (num != null ? iconGap + ImGui.CalcTextSize(num).X : 0f);
            shown.Add((s.Icon, s.Count, s.Clickable, s.Active, s.Slot, w));
        }

        var sx = x;
        if (shown.Count > 0)
        {
            // the shared stats capsule - one implementation for feed rows
            // and comment-thread strips; slot indices map back to the
            // like/comment hit outs
            var statSegs = new List<ReactionPillBar.StatsSegment>(shown.Count);
            var slotIndex = new[] { -1, -1, -1 };
            foreach (var s in shown)
            {
                slotIndex[s.Slot] = statSegs.Count;
                statSegs.Add(new ReactionPillBar.StatsSegment(
                    s.Icon, s.Count, s.Active, s.Clickable));
            }
            var statRects = new List<(int Slot, Vector4 Rect)>();
            var statsW = ReactionPillBar.DrawStats(drawList, new Vector2(sx, y),
                lineH, scale, accent, statSegs, statRects);
            foreach (var (slot, r) in statRects)
            {
                if (slot == slotIndex[0]) likeRect = r;
                else if (slot == slotIndex[1]) commentRect = r;
            }
            sx += statsW;
        }

        // ---- reactions pill: the shared capsule helper - same visual and
        // behavior in feed rows and comment-thread bars
        var chips = item.ReactionEmojis;
        var hasChips = chips is { Count: > 0 };
        if (!hasChips && !chipsClickable) return;
        sx += pillGap;
        if (sx >= x + width) return;
        var pillY = y;
        var adapted = new List<(string Emoji, int Count, bool ByMe)>();
        if (hasChips)
        {
            foreach (var c in chips!)
            {
                adapted.Add((c.Emoji, c.Count, c.ByMe));
            }
        }
        ReactionPillBar.Draw(drawList, new Vector2(sx, pillY), x + width - sx,
            lineH, scale, accent, adapted, chipsClickable,
            out addRect, chipRects);
    }

    /// <summary>Per-kind display label + glyph for context text, badges
    /// and the identity-slot fallback.</summary>
    private static (string Label, FontAwesomeIcon Icon) KindStyle(
        SyncshellFeedItemKind kind) => kind switch
    {
        SyncshellFeedItemKind.News => ("News", FontAwesomeIcon.Bullhorn),
        SyncshellFeedItemKind.Event => ("Event", FontAwesomeIcon.CalendarAlt),
        SyncshellFeedItemKind.Photo => ("Photo", FontAwesomeIcon.Camera),
        SyncshellFeedItemKind.Blog => ("Chronicle", FontAwesomeIcon.FeatherAlt),
        SyncshellFeedItemKind.Comment => ("Comment", FontAwesomeIcon.Comment),
        SyncshellFeedItemKind.Reaction => ("Reaction", FontAwesomeIcon.Smile),
        SyncshellFeedItemKind.Rsvp => ("Sign-up", FontAwesomeIcon.TicketAlt),
        _ => ("Post", FontAwesomeIcon.Bolt),
    };

    /// <summary>WHAT / CONTEXT per kind. The line carrying the user's
    /// actual content (event/news/chronicle/rsvp titles, photo captions,
    /// comment bodies) gets the strong primary-medium style; generic
    /// activity phrases and meta context stay secondary. Reactions drop
    /// their kind name from context - the glyph already says it.</summary>
    private static (string Action, string? Context, bool ActionStrong,
        bool ContextStrong) ComposeText(SyncshellCommunityFeedItemDto item,
        string? shellName, string? extraMeta)
    {
        string action;
        string? context;
        var aStrong = false;
        var cStrong = false;
        if (item.Kind == SyncshellFeedItemKind.Event)
        {
            action = item.Title;
            aStrong = true;
            // TimestampUtc provably holds the schedule when it differs from
            // the activity time - show it labeled in context, never twice.
            var showStart = FeedItemSemantics
                .EventTimes(item.TimestampUtc, item.ActivityUtc).ShowStartInContext;
            context = "Event" + (showStart
                ? $" · {FormatEventStart(item.TimestampUtc)}" : "");
            if (!string.IsNullOrWhiteSpace(item.Snippet))
            {
                context += $" · {item.Snippet}";
            }
        }
        else if (item.Kind == SyncshellFeedItemKind.News)
        {
            action = item.Title;
            aStrong = true;
            context = string.IsNullOrWhiteSpace(item.Snippet)
                ? "News" : $"News · {item.Snippet}";
        }
        else if (item.Kind == SyncshellFeedItemKind.Photo)
        {
            action = "Shared a photo";
            cStrong = !string.IsNullOrWhiteSpace(item.Title);
            context = cStrong ? item.Title : "Photo";
        }
        else if (item.Kind == SyncshellFeedItemKind.Blog)
        {
            // Chronicle rows lead with the title - a "Published a
            // Chronicle" label adds nothing the FeatherAlt glyph + snippet
            // don't already say.
            action = item.Title;
            aStrong = true;
            context = null;
        }
        else if (item.Kind == SyncshellFeedItemKind.Comment)
        {
            action = item.Title;
            cStrong = !string.IsNullOrWhiteSpace(item.Snippet);
            context = JoinContext(item.Snippet, shellName);
        }
        else if (item.Kind == SyncshellFeedItemKind.Reaction)
        {
            action = FeedItemSemantics.ReactionAction(item.Title);
            context = shellName;
        }
        else
        {
            action = item.Title;
            aStrong = item.Kind == SyncshellFeedItemKind.Rsvp;
            context = JoinContext(item.Snippet, shellName);
        }
        if (extraMeta != null)
        {
            context = context != null ? $"{context} · {extraMeta}" : extraMeta;
        }
        return (action, context, aStrong, cStrong);
    }

    /// <summary>Joins optional context segments with the meta separator,
    /// skipping empties.</summary>
    private static string? JoinContext(params string?[] parts)
    {
        string? joined = null;
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            joined = joined == null ? p : $"{joined} · {p}";
        }
        return joined;
    }

    /// <summary>Event schedule for the labeled context line:
    /// "Dec 12, 00:00" in local time, invariant.</summary>
    private static string FormatEventStart(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return $"{SpheneUI.Text.FormatShortDate(local)}, {SpheneUI.Text.FormatTime(local)}";
    }

    /// <summary>The optional preview thumbnail: media-hash first, base64
    /// fallback, stable texture key per item + prefix so feed lists share
    /// decoded textures with the surfaces that showed the same art.</summary>
    private static IDalamudTextureWrap? ResolveThumb(
        SyncshellCommunityFeedItemDto item, string texturePrefix)
    {
        if (item.ThumbnailMediaHash != null
            || !string.IsNullOrEmpty(item.ThumbnailBase64))
        {
            return SpheneUI.Media.Texture(
                $"{texturePrefix}:{item.Kind}-{item.Id}:"
                    + SyncshellCommunityView.MediaVersion(item.ThumbnailMediaHash, item.ThumbnailBase64),
                SyncshellCommunityView.ResolveBytes(item.ThumbnailMediaHash, item.ThumbnailBase64));
        }
        return null;
    }

    /// <summary>The height <see cref="Draw"/> produces for <paramref name="item"/>
    /// at the given content width, before caller spacing - lets containers
    /// size the scroll viewport to real content instead of a fixed cap.
    /// Mirrors the same geometry constants the renderer uses.</summary>
    public static float MeasureHeight(SyncshellCommunityFeedItemDto item,
        float availX, string? extraMeta = null)
    {
        var scale = SpheneUI.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var hasShell = !string.IsNullOrWhiteSpace(item.GroupGID)
            || !string.IsNullOrWhiteSpace(item.GroupAlias);
        var shellName = string.IsNullOrWhiteSpace(item.GroupAlias)
            ? item.GroupGID : item.GroupAlias;
        var (_, context, _, _) = ComposeText(item, hasShell ? shellName : null,
            extraMeta);
        var lines = context != null ? 3 : 2;
        var showThumb = (item.ThumbnailMediaHash != null
                || !string.IsNullOrEmpty(item.ThumbnailBase64))
            && availX - PadX * 2f * scale - AvatarD * scale - Gutter * scale
                - ThumbW * scale - MediaGap * scale >= MinTextW * scale;
        var textW = availX - PadX * 2f * scale - AvatarD * scale
            - Gutter * scale
            - (showThumb ? ThumbW * scale + MediaGap * scale : 0f);
        var (snippetLines, hasTail) = ExtraContent(item, textW, interactive: true);
        var textH = lines * lineH + IdentGap * scale + (lines - 2) * LineGap * scale
            + ExtraHeight(snippetLines, hasTail, lineH, LineGap * scale);
        var bodyH = MathF.Max(MathF.Max(AvatarD * scale, textH),
            showThumb ? ThumbH * scale : 0f);
        return PadY * 2f * scale + bodyH;
    }

    /// <summary>Short relative activity time ("just now" / "5 min ago" /
    /// "3 h ago" / "2 d ago"), switching to a short date past a week.
    /// Shared with hub call sites that render feed timestamps outside a
    /// row.</summary>
    public static string RelativeTime(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 0) return "soon";
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} d ago";
        return SpheneUI.Text.FormatShortDate(utc.ToLocalTime());
    }

    /// <summary>The substring of <paramref name="text"/> fitting
    /// <paramref name="maxW"/>, ellipsized on overflow (UTF-8 safe).
    /// Shared by <see cref="EllipsisEnd"/> and inline tails that append
    /// their own suffix (like the Chronicle preview's "… more").</summary>
    private static string FitToWidth(string text, float maxW)
    {
        if (maxW <= 0f) return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxW) return text;
        var t = text.AsSpan();
        var lo = 0; var hi = t.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(t[..mid].ToString() + "…").X <= maxW)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return t[..lo].ToString() + "…";
    }

    /// <summary>Ellipsized drawlist text (UTF-8 safe). <paramref name="medium"/>
    /// double-draws with a 1px offset - the codebase's faux-medium emphasis
    /// (no semibold face is loaded).</summary>
    private static void EllipsisEnd(ImDrawListPtr drawList, Vector2 pos,
        float maxW, string text, Vector4 color, bool medium = false)
    {
        var col = SpheneUI.ColorUtils.ToU32(color);
        var shown = FitToWidth(text, maxW);
        if (shown.Length == 0) return;
        drawList.AddText(pos, col, shown);
        if (medium)
        {
            drawList.AddText(pos + new Vector2(MathF.Max(1f, SpheneUI.Scale), 0f),
                col, shown);
        }
    }

    /// <summary>Thumbnail in the dedicated media slot: cover-crops into
    /// the box like card covers, but never upscales - media smaller than
    /// the slot in both axes draws centered at native size.</summary>
    private static void DrawThumbFit(ImDrawListPtr drawList,
        IDalamudTextureWrap wrap, Vector2 pos, Vector2 box, Vector4 accent)
    {
        var tint = SpheneUI.ColorUtils.ToU32(Vector4.One);
        var corner = 6f * SpheneUI.Scale;
        var border = SpheneUI.ColorUtils.ToU32(accent with { W = 0.35f });
        if (wrap.Width <= box.X && wrap.Height <= box.Y)
        {
            var native = new Vector2(wrap.Width, wrap.Height);
            var nPos = pos + (box - native) * 0.5f;
            drawList.AddImageRounded(wrap.Handle, nPos, nPos + native,
                Vector2.Zero, Vector2.One, tint, corner);
            drawList.AddRect(nPos, nPos + native, border, corner);
            return;
        }
        var u0 = Vector2.Zero;
        var u1 = Vector2.One;
        var imgAspect = (float)wrap.Width / wrap.Height;
        var boxAspect = box.X / box.Y;
        if (imgAspect > boxAspect)
        {
            var span = boxAspect / imgAspect;
            u0.X = (1f - span) * 0.5f;
            u1.X = 1f - u0.X;
        }
        else
        {
            var span = imgAspect / boxAspect;
            u0.Y = (1f - span) * 0.5f;
            u1.Y = 1f - u0.Y;
        }
        drawList.AddImageRounded(wrap.Handle, pos, pos + box, u0, u1, tint,
            corner);
        drawList.AddRect(pos, pos + box, border, corner);
    }
}
