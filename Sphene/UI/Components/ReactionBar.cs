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

    /// <summary>Inline chip row for one target - summary may be null while
    /// loading (renders just the add-affordance then).</summary>
    public static void DrawInline(ReactionTargetKind targetKind, long targetId,
        ReactionSummaryDto? summary, Action<string, bool> onSet)
    {
        ImGui.PushID($"rxn-{targetKind}-{targetId}");
        var first = true;
        DrawChips(summary, interactive: true, onSet, ref first);
        var picker = SpheneUI.EmojiPickerHook;
        if (picker != null)
        {
            if (!first) ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            var flyoutId = $"rxn-add-{targetKind}-{targetId}";
            picker.TriggerButton(flyoutId, emoji => onSet(emoji, true),
                "Add reaction", ghost: true);
            picker.Draw(flyoutId);
        }
        ImGui.PopID();
    }

    /// <summary>Passive strip of already-cast reactions - nothing is
    /// clickable. For surfaces where the viewer must not add a reaction
    /// (e.g. their own comment).</summary>
    public static void DrawReadOnly(ReactionTargetKind targetKind, long targetId,
        ReactionSummaryDto? summary)
    {
        ImGui.PushID($"rxn-ro-{targetKind}-{targetId}");
        var first = true;
        DrawChips(summary, interactive: false, null, ref first);
        ImGui.PopID();
    }

    /// <summary>One chip per emoji with a positive count, most-reacted
    /// first - chips the viewer cast render accent-tinted and toggle off
    /// on click, foreign chips toggle the viewer in.</summary>
    private static void DrawChips(ReactionSummaryDto? summary, bool interactive,
        Action<string, bool>? onSet, ref bool first)
    {
        var counts = EmojiCountsOf(summary);
        if (counts.Count == 0) return;
        var mine = ViewerEmojisOf(summary);
        var ordered = counts
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        var rowStartX = ImGui.GetCursorPosX();
        var rowRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        foreach (var (emoji, count) in ordered)
        {
            // chips flow left-to-right and wrap when the next one would
            // overhang the row's right edge - an unbounded emoji vocabulary
            // must never clip off the content region; wrapped lines keep the
            // row's original left indent
            var chipW = ChipWidth(count);
            if (!first)
            {
                if (ImGui.GetCursorScreenPos().X + chipW > rowRight)
                {
                    ImGui.NewLine();
                    ImGui.SetCursorPosX(rowStartX);
                }
                else
                {
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                }
            }
            first = false;
            var active = mine.Contains(emoji, StringComparer.Ordinal);
            var clicked = DrawChip(emoji, count, active, interactive);
            if (clicked) onSet?.Invoke(emoji, !active);
            SpheneUI.AttachTooltip(count + " reaction" + (count == 1 ? "" : "s")
                + (active ? " · you reacted" : string.Empty));
        }
    }

    /// <summary>Chip pixel width - single source for the wrap check and
    /// the plate itself so the two can never disagree.</summary>
    private static float ChipWidth(int count)
    {
        var pad = SpheneUI.Spacing.X4.Scaled;
        var gap = 2f * SpheneUI.Scale;
        return pad + ImGui.GetTextLineHeight() + gap
            + ImGui.CalcTextSize(count.ToString()).X + pad;
    }

    /// <summary>Renders one emoji chip: rounded plate (accent-tinted when
    /// active, brightening on hover) + sprite + count. Returns true when
    /// clicked. Passive chips skip the hit zone but keep the plate.</summary>
    private static bool DrawChip(string emoji, int count, bool active, bool interactive)
    {
        var scale = SpheneUI.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var pad = SpheneUI.Spacing.X4.Scaled;
        var gap = 2f * scale;
        var chipW = ChipWidth(count);
        var countText = count.ToString();
        var chipH = lineH + pad;

        var pos = ImGui.GetCursorScreenPos();
        var clicked = false;
        if (interactive)
        {
            ImGui.InvisibleButton($"##chip-{emoji}", new Vector2(chipW, chipH));
            clicked = ImGui.IsItemClicked();
        }
        else
        {
            ImGui.Dummy(new Vector2(chipW, chipH));
        }
        var hovered = interactive && ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        var accent = SpheneUI.Colors.Accent;
        var fill = active
            ? new Vector4(accent.X, accent.Y, accent.Z, hovered ? 0.45f : 0.3f)
            : new Vector4(1f, 1f, 1f, hovered ? 0.12f : 0.05f);
        var rounding = chipH * 0.5f;
        draw.AddRectFilled(pos, pos + new Vector2(chipW, chipH),
            ImGui.ColorConvertFloat4ToU32(fill), rounding);
        if (active)
        {
            draw.AddRect(pos, pos + new Vector2(chipW, chipH),
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.8f)),
                rounding, ImDrawFlags.None, 1f * scale);
        }

        var spriteY = pos.Y + pad * 0.5f;
        SpheneUI.Emoji.SpriteAt(draw, new Vector2(pos.X + pad, spriteY), lineH, emoji);

        var textX = pos.X + pad + lineH + gap;
        var textY = pos.Y + (chipH - lineH) * 0.5f;
        var textColor = active ? accent : SpheneUI.Colors.TextMuted;
        draw.AddText(new Vector2(textX, textY),
            ImGui.ColorConvertFloat4ToU32(textColor), countText);
        return clicked;
    }
}
