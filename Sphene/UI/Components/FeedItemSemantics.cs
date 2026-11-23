using Sphene.API.Data.Enum;

namespace Sphene.UI.Components;

/// <summary>
/// Dependency-free feed-row decisions: which timestamp an event row
/// carries on its right edge and whether the scheduled start belongs in
/// the context line, plus the reaction-kind parse contract (feed rows
/// carry the enum name in <c>Snippet</c>, never localized text).
/// </summary>
public static class FeedItemSemantics
{
    /// <summary>Event rows sort by scheduled start (<paramref name="timestampUtc"/>)
    /// while <paramref name="activityUtc"/> carries the real feed-activity
    /// time when the server provides it. Returns <c>ShowRelativeActivity</c>
    /// - the right edge renders the activity time (relative) only when the
    /// server supplied it; nothing is fabricated otherwise - and
    /// <c>ShowStartInContext</c> - the context line carries the labeled
    /// scheduled start whenever <paramref name="timestampUtc"/> provably
    /// holds the schedule (anything but the activity time itself, e.g. the
    /// profile-activity projection where both carry CreatedAt).</summary>
    public static (bool ShowRelativeActivity, bool ShowStartInContext) EventTimes(
        DateTime timestampUtc, DateTime? activityUtc)
        => (activityUtc != null, activityUtc != timestampUtc);

    /// <summary>Parses the reaction kind a feed row carries in its
    /// snippet - the server emits <c>((ReactionKind)kind).ToString()</c>,
    /// so only exact enum names resolve; numbers and free text fail.</summary>
    public static bool TryReactionKind(string? snippet, out ReactionKind kind)
    {
        if (Enum.TryParse(snippet, out kind) && Enum.IsDefined(kind))
        {
            return true;
        }
        kind = default;
        return false;
    }

    /// <summary>Server reaction rows arrive titled "Reaction on {target}" -
    /// renders as the activity phrase "Reacted to {target}"; any other
    /// title passes through unchanged.</summary>
    public static string ReactionAction(string title)
        => title.StartsWith("Reaction on ", StringComparison.Ordinal)
            ? "Reacted to " + title[12..]
            : title;
}
