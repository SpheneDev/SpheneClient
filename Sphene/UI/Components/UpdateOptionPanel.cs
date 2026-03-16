using Sphene.SpheneConfiguration;

namespace Sphene.UI.Components;

public static class UpdateOptionPanel
{
    public const string Tag_1_1_12 = "v.1.1.11.1071";
    public const string Tag_1_1_12_50 = "v.1.1.12.50";

    public sealed record ReleaseOptionGroup(string Tag, IReadOnlyList<Link> Links);

    public enum Link
    {
        SyncIncomingWithoutRedraw,
        SyncOutgoingBatching,
        ShowTestBuildUpdates
    }

    public static readonly Link[] Link_1_1_12 =
    [
        Link.SyncIncomingWithoutRedraw,
        Link.SyncOutgoingBatching
    ];

    public static readonly Link[] Link_1_1_12_50 =
    [
        Link.ShowTestBuildUpdates
    ];

    private static readonly ReleaseOptionGroup[] Releases =
    [
        new(Tag_1_1_12, Link_1_1_12),
        new(Tag_1_1_12_50, Link_1_1_12_50)
    ];

    public static string GetTitle(Link link)
        => link switch
        {
            Link.SyncIncomingWithoutRedraw => "Sync: Incoming Sync (Default: Disabled)",
            Link.SyncOutgoingBatching => "Sync: Outgoing Batching (Default: Disabled)",
            Link.ShowTestBuildUpdates => "Notifications: Testbuild Update Hints (Default: Disabled)",
            _ => "Option"
        };

    public static IReadOnlyList<ReleaseOptionGroup> GetPendingReleaseGroups(SpheneConfigService configService)
    {
        var seenTags = GetSeenTags(configService);
        var pending = new List<ReleaseOptionGroup>();

        foreach (var release in Releases)
        {
            if (!seenTags.Contains(release.Tag))
            {
                pending.Add(release);
            }
        }

        return pending;
    }

    public static IReadOnlyList<Link> GetVisibleLinks(SpheneConfigService configService)
    {
        var visible = new List<Link>();
        var uniqueLinks = new HashSet<Link>();

        foreach (var release in GetPendingReleaseGroups(configService))
        {
            foreach (var link in release.Links)
            {
                if (uniqueLinks.Add(link))
                {
                    visible.Add(link);
                }
            }
        }

        return visible;
    }

    public static void DrawByLink(Link link, SpheneConfigService configService, UiSharedService uiShared, float outgoingSliderWidth)
    {
        switch (link)
        {
            case Link.SyncIncomingWithoutRedraw:
                SyncBehaviorOptionBlock.DrawIncomingSyncWithoutRedraw(configService, uiShared, "UpdateOptionSyncIncomingWithoutRedraw");
                break;
            case Link.SyncOutgoingBatching:
                SyncBehaviorOptionBlock.DrawOutgoingSyncBatching(configService, uiShared, outgoingSliderWidth, "UpdateOptionSyncOutgoingBatching");
                break;
            case Link.ShowTestBuildUpdates:
                NotificationsOptionBlock.DrawShowTestBuildUpdatesOption(configService, uiShared, "UpdateOptionShowTestBuildUpdates");
                break;
        }
    }

    public static bool HasUnseenTag(SpheneConfigService configService)
    {
        return GetPendingReleaseGroups(configService).Count > 0;
    }

    public static void MarkCurrentTagAsSeen(SpheneConfigService configService)
    {
        var seenTags = GetSeenTags(configService);
        var latestSeenTag = configService.Current.LastSeenNewOptionsTag;

        foreach (var release in GetPendingReleaseGroups(configService))
        {
            if (seenTags.Add(release.Tag))
            {
                latestSeenTag = release.Tag;
            }
        }

        configService.Current.SeenNewOptionsTags = GetOrderedSeenTags(seenTags);
        configService.Current.LastSeenNewOptionsTag = latestSeenTag;
        configService.Save();
    }

    private static HashSet<string> GetSeenTags(SpheneConfigService configService)
    {
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in configService.Current.SeenNewOptionsTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                seenTags.Add(tag);
            }
        }

        var lastSeenTag = configService.Current.LastSeenNewOptionsTag;
        if (!string.IsNullOrWhiteSpace(lastSeenTag))
        {
            seenTags.Add(lastSeenTag);
        }

        return seenTags;
    }

    private static List<string> GetOrderedSeenTags(HashSet<string> seenTags)
    {
        var ordered = new List<string>();
        foreach (var release in Releases)
        {
            if (seenTags.Contains(release.Tag))
            {
                ordered.Add(release.Tag);
            }
        }

        foreach (var tag in seenTags)
        {
            if (!ordered.Contains(tag, StringComparer.Ordinal))
            {
                ordered.Add(tag);
            }
        }

        return ordered;
    }
}
