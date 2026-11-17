using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// Compact reaction bar - one chip per reaction kind showing its aggregate
/// count; the viewer's own kinds render accent-tinted. The write contract is
/// state-based: clicking an active chip calls <c>onSet(kind, false)</c>,
/// an inactive one <c>onSet(kind, true)</c> - the UI toggles, the API is
/// idempotent Set/Remove.
/// </summary>
public static class ReactionBar
{
    private static readonly (ReactionKind Kind, FontAwesomeIcon Icon, string Tip)[] Kinds =
    [
        (ReactionKind.Like, FontAwesomeIcon.ThumbsUp, "Like"),
        (ReactionKind.Love, FontAwesomeIcon.Heart, "Love"),
        (ReactionKind.Laugh, FontAwesomeIcon.LaughSquint, "Haha"),
        (ReactionKind.Wow, FontAwesomeIcon.Surprise, "Wow"),
        (ReactionKind.Sad, FontAwesomeIcon.SadTear, "Sad"),
    ];

    /// <summary>Inline chip row for one target - summary may be null while
    /// loading (renders the kinds with zero counts).</summary>
    public static void DrawInline(ReactionTargetKind targetKind, long targetId,
        ReactionSummaryDto? summary, Action<ReactionKind, bool> onSet)
    {
        var counts = summary?.Counts ?? [];
        var mine = summary?.ViewerKinds ?? [];
        ImGui.PushID($"rxn-{targetKind}-{targetId}");
        for (var i = 0; i < Kinds.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            var (kind, icon, tip) = Kinds[i];
            var count = counts.GetValueOrDefault((int)kind);
            var active = mine.Contains((int)kind);
            var label = count > 0 ? $"{count}" : string.Empty;
            if (active)
            {
                if (SpheneUI.Icons.TextButton(icon, label.Length > 0 ? label : tip))
                {
                    onSet(kind, false);
                }
            }
            else if (SpheneUI.Icons.Button(icon))
            {
                onSet(kind, true);
            }
            SpheneUI.AttachTooltip(count > 0 ? $"{tip} · {count}" : tip);
        }
        ImGui.PopID();
    }

    /// <summary>Passive strip of already-cast reactions - only kinds with a
    /// positive count render and nothing is clickable. For surfaces where
    /// the viewer must not add a reaction (e.g. their own comment).</summary>
    public static void DrawReadOnly(ReactionTargetKind targetKind, long targetId,
        ReactionSummaryDto? summary)
    {
        var counts = summary?.Counts;
        if (counts == null) return;
        var any = false;
        foreach (var kv in counts)
        {
            if (kv.Value > 0) { any = true; break; }
        }
        if (!any) return;
        ImGui.PushID($"rxn-ro-{targetKind}-{targetId}");
        var first = true;
        for (var i = 0; i < Kinds.Length; i++)
        {
            var (kind, icon, tip) = Kinds[i];
            var count = counts.GetValueOrDefault((int)kind);
            if (count <= 0) continue;
            if (!first) ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            first = false;
            SpheneUI.Icons.Icon(icon, SpheneUI.Colors.TextMuted);
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            SpheneUI.Text.Muted($"{count}");
            SpheneUI.AttachTooltip($"{tip} · {count}");
        }
        ImGui.PopID();
    }
}
