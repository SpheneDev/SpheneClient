using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Compact reaction bar - one chip per emoji already cast on the target,
/// ordered by count, plus an add-affordance opening the shared chat emoji
/// picker: any catalog emoji is a valid reaction, none are predefined. The
/// viewer's own emojis render accent-tinted; clicking a chip toggles the
/// viewer's own row via <c>onSet(emoji, set)</c> - the API stays idempotent
/// Set/Remove. Summaries from pre-emoji servers translate their int kinds
/// through <see cref="ReactionEmojiMap"/>, so one rendering path covers
/// both generations and migrated reactions display as the emojis they
/// were mapped to.
/// </summary>
public static class ReactionBar
{
    /// <summary>Counts keyed by emoji glyph - prefers the server's emoji
    /// projection, falls back to translating legacy int kinds.</summary>
    public static IReadOnlyDictionary<string, int> EmojiCountsOf(ReactionSummaryDto? summary)
    {
        if (summary == null) return new Dictionary<string, int>(StringComparer.Ordinal);
        if (summary.EmojiCounts != null) return summary.EmojiCounts;
        var translated = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in summary.Counts)
        {
            if (!Enum.IsDefined(typeof(ReactionKind), kv.Key)) continue;
            var emoji = ReactionEmojiMap.ForKind((ReactionKind)kv.Key);
            translated[emoji] = translated.GetValueOrDefault(emoji) + kv.Value;
        }
        return translated;
    }

    /// <summary>The viewer's own reaction glyphs - same legacy fallback.</summary>
    public static IReadOnlyCollection<string> ViewerEmojisOf(ReactionSummaryDto? summary)
    {
        if (summary == null) return [];
        if (summary.ViewerEmojis != null) return summary.ViewerEmojis;
        var translated = new List<string>();
        foreach (var k in summary.ViewerKinds)
        {
            if (!Enum.IsDefined(typeof(ReactionKind), k)) continue;
            var emoji = ReactionEmojiMap.ForKind((ReactionKind)k);
            if (!translated.Contains(emoji, StringComparer.Ordinal)) translated.Add(emoji);
        }
        return translated;
    }

    /// <summary>Inline reaction strip for one target - the shared capsule
    /// visuals (same as feed rows): an optional stats capsule (likes /
    /// comments / views, thumb toggles the viewer's like) followed by the
    /// reactions capsule with clamped chips and the smiley+plus add button.
    /// When the stats capsule renders, thumbs-up reactions fold into it and
    /// leave the emoji chips - the like/emoji separation matches the feed.
    /// Summary may be null while loading - the pills still render.</summary>
    public static void DrawInline(ReactionTargetKind targetKind, long targetId,
        ReactionSummaryDto? summary, Action<string, bool> onSet,
        int? commentCount = null, int? viewCount = null, Action? onComments = null)
    {
        ImGui.PushID($"rxn-{targetKind}-{targetId}");
        var scale = SpheneUI.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var pillH = ReactionPillBar.Height(lineH, scale);
        var withStats = commentCount != null || viewCount != null;
        var counts = EmojiCountsOf(summary);
        var mine = ViewerEmojisOf(summary);
        var chips = OrderedChips(summary, excludeLikes: withStats);
        var flyoutId = $"rxn-add-{targetKind}-{targetId}";
        var pos = ImGui.GetCursorScreenPos();
        var maxW = ImGui.GetContentRegionAvail().X;
        var pillGap = 8f * scale;

        // stats segments mirror the feed: thumb always (the like toggle),
        // comment count always, views only once anyone actually viewed
        var segments = new List<ReactionPillBar.StatsSegment>();
        if (withStats)
        {
            var likeCount = 0;
            var liked = false;
            foreach (var kv in counts)
            {
                if (!kv.Key.StartsWith(ReactionEmojiMap.ThumbsUp, StringComparison.Ordinal))
                {
                    continue;
                }
                likeCount += kv.Value;
                if (mine.Contains(kv.Key, StringComparer.Ordinal)) liked = true;
            }
            segments.Add(new ReactionPillBar.StatsSegment(
                FontAwesomeIcon.ThumbsUp, likeCount, liked, true));
            if (commentCount != null)
            {
                segments.Add(new ReactionPillBar.StatsSegment(
                    FontAwesomeIcon.Comment, commentCount, false, onComments != null));
            }
            if (viewCount is > 0)
            {
                segments.Add(new ReactionPillBar.StatsSegment(
                    FontAwesomeIcon.Eye, viewCount, false, false));
            }
        }

        // overlay sized to the drawn strip exactly - a full-width overlay
        // would push the same-line followers (composer entry) off the card
        var statsW = withStats && segments.Count > 0
            ? ReactionPillBar.MeasureStatsWidth(lineH, scale, segments) : 0f;
        var rxnMaxW = MathF.Max(60f * scale, maxW - statsW
            - (statsW > 0f ? pillGap : 0f));
        var rxnW = ReactionPillBar.MeasureWidth(lineH, scale, chips, true, rxnMaxW);
        var totalW = statsW + (statsW > 0f && rxnW > 0f ? pillGap : 0f) + rxnW;
        if (totalW <= 0f)
        {
            ImGui.PopID();
            return;
        }
        ImGui.InvisibleButton("##rxnpill", new Vector2(totalW, pillH));
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);

        var statRects = new List<(int Slot, Vector4 Rect)>();
        var x = pos.X;
        if (statsW > 0f)
        {
            ReactionPillBar.DrawStats(ImGui.GetWindowDrawList(),
                new Vector2(x, pos.Y), lineH, scale, SpheneUI.Colors.Accent,
                segments, statRects);
            x += statsW + (rxnW > 0f ? pillGap : 0f);
        }
        var chipRects = new List<ReactionPillBar.ChipHit>();
        Vector4 addRect = Vector4.Zero;
        if (rxnW > 0f)
        {
            ReactionPillBar.Draw(ImGui.GetWindowDrawList(),
                new Vector2(x, pos.Y), rxnMaxW, lineH, scale,
                SpheneUI.Colors.Accent, chips, clickable: true,
                out addRect, chipRects);
        }
        var hovered = ImGui.IsItemHovered();
        if (clicked || hovered)
        {
            var mouse = ImGui.GetIO().MousePos;
            var stat = statRects.Find(hit => ReactionPillBar.Contains(hit.Rect, mouse));
            string? tooltip = null;
            if (stat.Rect != Vector4.Zero && stat.Slot == 0)
            {
                var active = segments[0].Active;
                tooltip = active ? "Remove your like" : "Like";
                if (clicked) onSet(ReactionEmojiMap.ThumbsUp, !active);
            }
            else if (stat.Rect != Vector4.Zero && stat.Slot == 1)
            {
                tooltip = "Open comments";
                if (clicked) onComments?.Invoke();
            }
            else if (chipRects.Find(hit => ReactionPillBar.Contains(hit.Rect, mouse)) is { Emoji: not null } chip)
            {
                tooltip = ReactionPillBar.ChipTooltip(chip);
                if (clicked) onSet(chip.Emoji, !chip.ByMe);
            }
            else if (ReactionPillBar.Contains(addRect, mouse))
            {
                tooltip = "Add reaction";
                if (clicked) SpheneUI.EmojiPickerHook?.OpenFlyout(flyoutId, emoji => onSet(emoji, true));
            }
            if (tooltip != null) SpheneUI.AttachTooltip(tooltip, hovered);
        }
        SpheneUI.EmojiPickerHook?.Draw(flyoutId);
        ImGui.PopID();
    }

    /// <summary>Chips ordered most-reacted first as the shared tuple shape
    /// the pill helper renders.</summary>
    private static List<(string Emoji, int Count, bool ByMe)> OrderedChips(
        ReactionSummaryDto? summary, bool excludeLikes = false)
    {
        var counts = EmojiCountsOf(summary);
        var mine = ViewerEmojisOf(summary);
        var list = new List<(string, int, bool)>();
        foreach (var kv in counts)
        {
            if (kv.Value <= 0) continue;
            if (excludeLikes && kv.Key.StartsWith(ReactionEmojiMap.ThumbsUp,
                    StringComparison.Ordinal)) continue;
            list.Add((kv.Key, kv.Value, mine.Contains(kv.Key, StringComparer.Ordinal)));
        }
        list.Sort((a, b) => b.Item2 != a.Item2
            ? b.Item2.CompareTo(a.Item2)
            : string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    /// <summary>Passive pill of already-cast reactions - nothing is
    /// clickable, no add affordance. For surfaces where the viewer must not
    /// react (e.g. their own comment).</summary>
    public static void DrawReadOnly(ReactionTargetKind targetKind, long targetId,
        ReactionSummaryDto? summary)
    {
        ImGui.PushID($"rxn-ro-{targetKind}-{targetId}");
        var scale = SpheneUI.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var pillH = ReactionPillBar.Height(lineH, scale);
        var chips = OrderedChips(summary);
        if (chips.Count > 0)
        {
            var pos = ImGui.GetCursorScreenPos();
            var maxW = ImGui.GetContentRegionAvail().X;
            ImGui.Dummy(new Vector2(maxW, pillH));
            ReactionPillBar.Draw(ImGui.GetWindowDrawList(), pos, maxW, lineH,
                scale, SpheneUI.Colors.Accent, chips, clickable: false,
                out _, null);
        }
        ImGui.PopID();
    }
}
