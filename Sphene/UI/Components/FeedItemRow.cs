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
    /// even when a creator uid is attached.</summary>
    public static bool Draw(string id, SyncshellCommunityFeedItemDto item,
        string texturePrefix, Vector4? fallbackAccent = null,
        string? extraMeta = null, Action<string>? onActorClick = null,
        Func<string, SpheneProfileData?>? profileFor = null,
        Func<string, IDalamudTextureWrap?>? shellAvatarFor = null)
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
        var textH = lines * lineH + identGap + (lines - 2) * lineGap;
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
        var actorHovered = false;
        var actorHit = false;
        if (actorLinkable)
        {
            ImGui.SetCursorScreenPos(avatarPos);
            actorHit = ImGui.InvisibleButton("##actor-" + id,
                new Vector2(avatarD + gutter + nameShownW, MathF.Max(avatarD, lineH)));
            actorHovered = ImGui.IsItemHovered();
            ImGui.SetCursorScreenPos(origin + new Vector2(availX, 0f));
        }

        if (rowHovered || actorHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            drawList.AddRectFilled(origin, origin + new Vector2(availX, rowH),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    SpheneUI.Colors.TextPrimary, 0.05f)), 4f * scale);
            ImGui.SetTooltip(actorLinkable && actorHovered
                ? name!
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

        var nameColor = SpheneUI.Colors.TextPrimary;
        if (actorLinkable && actorHovered)
        {
            drawList.AddLine(new Vector2(nameX, l1Y + lineH - 1f),
                new Vector2(nameX + nameShownW, l1Y + lineH - 1f),
                SpheneUI.ColorUtils.ToU32(nameColor));
        }
        EllipsisEnd(drawList, new Vector2(nameX, l1Y), nameMax, name!, nameColor,
            medium: true);
        if (tsW > 0f)
        {
            var muted = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted);
            drawList.AddText(new Vector2(nameX + nameShownW, l1Y), muted, sep);
            drawList.AddText(new Vector2(nameX + nameShownW + sepW, l1Y),
                muted, tsText);
        }

        var actionX = nameX;
        var actionW = bodyRight - actionX;
        if (item.Kind is SyncshellFeedItemKind.Comment
            or SyncshellFeedItemKind.Reaction or SyncshellFeedItemKind.Rsvp)
        {
            // Reaction rows carry the enum kind name in Snippet - show the
            // actual reaction glyph (Wow!=heart), a neutral face otherwise.
            var glyphIcon = item.Kind == SyncshellFeedItemKind.Reaction
                && FeedItemSemantics.TryReactionKind(item.Snippet, out var rk)
                ? ReactionBar.IconFor(rk)
                : item.Kind == SyncshellFeedItemKind.Reaction
                    ? FontAwesomeIcon.Smile : kindIcon;
            var glyph = glyphIcon.ToIconString();
            using (SpheneUI.Icons.PushFont())
            {
                var gW = ImGui.CalcTextSize(glyph).X;
                var gY = l2Y + MathF.Max(0f, (lineH - ImGui.GetTextLineHeight()) * 0.5f);
                drawList.AddText(new Vector2(actionX, gY),
                    SpheneUI.ColorUtils.ToU32(accent), glyph);
                actionX += gW + 5f * scale;
                actionW -= gW + 5f * scale;
            }
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

        if (showThumb)
        {
            DrawThumbFit(drawList, thumb!, new Vector2(thumbX, origin.Y + padY),
                new Vector2(thumbW, thumbH), accent);
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, rowH));
        return clicked && !actorHit;
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
        SyncshellFeedItemKind.Reaction => ("Reaction", FontAwesomeIcon.Heart),
        SyncshellFeedItemKind.Rsvp => ("RSVP", FontAwesomeIcon.TicketAlt),
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
            action = "Published a Chronicle";
            context = item.Title;
            cStrong = true;
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
        var textH = lines * lineH + IdentGap * scale + (lines - 2) * LineGap * scale;
        var showThumb = (item.ThumbnailMediaHash != null
                || !string.IsNullOrEmpty(item.ThumbnailBase64))
            && availX - PadX * 2f * scale - AvatarD * scale - Gutter * scale
                - ThumbW * scale - MediaGap * scale >= MinTextW * scale;
        var bodyH = MathF.Max(MathF.Max(AvatarD * scale, textH),
            showThumb ? ThumbH * scale : 0f);
        return PadY * 2f * scale + bodyH;
    }

    /// <summary>Short relative activity time ("now" / "5m" / "3h" / "2d"),
    /// switching to a short date past a week. Shared with hub call sites
    /// that render feed timestamps outside a row.</summary>
    public static string RelativeTime(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 0) return "soon";
        if (delta.TotalMinutes < 1) return "now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d";
        return SpheneUI.Text.FormatShortDate(utc.ToLocalTime());
    }

    /// <summary>Ellipsized drawlist text (UTF-8 safe). <paramref name="medium"/>
    /// double-draws with a 1px offset - the codebase's faux-medium emphasis
    /// (no semibold face is loaded).</summary>
    private static void EllipsisEnd(ImDrawListPtr drawList, Vector2 pos,
        float maxW, string text, Vector4 color, bool medium = false)
    {
        var col = SpheneUI.ColorUtils.ToU32(color);
        if (maxW <= 0f) return;
        string shown;
        if (ImGui.CalcTextSize(text).X <= maxW)
        {
            shown = text;
        }
        else
        {
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
            shown = t[..lo].ToString() + "…";
        }
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
