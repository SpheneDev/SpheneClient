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

    /// <summary>Inline reaction pill for one target - the shared capsule
    /// visual (same as feed rows): clamped chips, overflow count, trailing
    /// accent-bordered add button opening the emoji picker. Summary may be
    /// null while loading - the pill still renders the add affordance.</summary>
    public static void DrawInline(ReactionTargetKind targetKind, long targetId,
        ReactionSummaryDto? summary, Action<string, bool> onSet)
    {
        ImGui.PushID($"rxn-{targetKind}-{targetId}");
        var scale = SpheneUI.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var pillH = ReactionPillBar.Height(lineH, scale);
        var chips = OrderedChips(summary);
        var flyoutId = $"rxn-add-{targetKind}-{targetId}";
        var pos = ImGui.GetCursorScreenPos();
        var maxW = ImGui.GetContentRegionAvail().X;
        // one overlay button spans the strip; clicks route through the
        // helper's hit rects - the same idiom the feed rows use
        ImGui.InvisibleButton("##rxnpill", new Vector2(maxW, pillH));
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var chipRects = new List<(string Emoji, Vector4 Rect)>();
        ReactionPillBar.Draw(ImGui.GetWindowDrawList(), pos, maxW, lineH, scale,
            SpheneUI.Colors.Accent, chips, clickable: true,
            out var addRect, chipRects);
        if (clicked)
        {
            var mouse = ImGui.GetIO().MousePos;
            var hit = false;
            foreach (var (emoji, rect) in chipRects)
            {
                if (mouse.X < rect.X || mouse.X >= rect.Z
                    || mouse.Y < rect.Y || mouse.Y >= rect.W) continue;
                var active = chips.Any(c => string.Equals(c.Emoji, emoji, StringComparison.Ordinal) && c.ByMe);
                onSet(emoji, !active);
                hit = true;
                break;
            }
            if (!hit && addRect != Vector4.Zero
                && mouse.X >= addRect.X && mouse.X < addRect.Z
                && mouse.Y >= addRect.Y && mouse.Y < addRect.W)
            {
                SpheneUI.EmojiPickerHook?.OpenFlyout(flyoutId, emoji => onSet(emoji, true));
            }
        }
        if (ImGui.IsItemHovered() && MouseInAdd(addRect))
        {
            SpheneUI.AttachTooltip("Add reaction");
        }
        SpheneUI.EmojiPickerHook?.Draw(flyoutId);
        ImGui.PopID();

        static bool MouseInAdd(Vector4 r)
        {
            if (r == Vector4.Zero) return false;
            var m = ImGui.GetIO().MousePos;
            return m.X >= r.X && m.X < r.Z && m.Y >= r.Y && m.Y < r.W;
        }
    }

    /// <summary>Chips ordered most-reacted first as the shared tuple shape
    /// the pill helper renders.</summary>
    private static List<(string Emoji, int Count, bool ByMe)> OrderedChips(
        ReactionSummaryDto? summary)
    {
        var counts = EmojiCountsOf(summary);
        var mine = ViewerEmojisOf(summary);
        var list = new List<(string, int, bool)>();
        foreach (var kv in counts)
        {
            if (kv.Value <= 0) continue;
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
