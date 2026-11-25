using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;

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

    /// <summary>Resolves the reaction emoji a feed row carries in its
    /// snippet. Post-migration servers emit the glyph itself; rows produced
    /// before the migration carried the enum name, which translates through
    /// the canonical map - both render as the emoji the reaction counts as.
    /// Null when the snippet carries no recognizable reaction.</summary>
    public static string? ReactionEmoji(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return null;
        if (Enum.TryParse(snippet, out ReactionKind kind) && Enum.IsDefined(kind))
        {
            return ReactionEmojiMap.ForKind(kind);
        }
        // Anything non-ascii is the glyph itself - ascii free text is no reaction.
        for (var i = 0; i < snippet.Length; i++)
        {
            if (snippet[i] > 0x7F) return snippet;
        }
        return null;
    }

    /// <summary>Maps a reaction host kind (reaction/comment ordinals) onto
    /// the feed-row kind it patches. NOT a cast: the feed enum inserts
    /// Photo at 2, so Blog is 3 there while reactions carry 2 - a blind
    /// cast patches photo rows with blog reaction counts. Null when the
    /// kind has no feed row (comments never render as patchable rows).</summary>
    public static SyncshellFeedItemKind? FeedKindForReactionHost(int hostKind)
        => hostKind switch
        {
            (int)ReactionTargetKind.News => SyncshellFeedItemKind.News,
            (int)ReactionTargetKind.Event => SyncshellFeedItemKind.Event,
            (int)ReactionTargetKind.Blog => SyncshellFeedItemKind.Blog,
            _ => null,
        };

    /// <summary>Server reaction rows arrive titled "Reaction on {target}" -
    /// renders as the activity phrase "Reacted to {target}"; any other
    /// title passes through unchanged.</summary>
    public static string ReactionAction(string title)
        => title.StartsWith("Reaction on ", StringComparison.Ordinal)
            ? "Reacted to " + title[12..]
            : title;
}
