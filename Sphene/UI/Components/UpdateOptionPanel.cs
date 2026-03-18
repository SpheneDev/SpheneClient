using Sphene.SpheneConfiguration;

namespace Sphene.UI.Components;

public static class UpdateOptionPanel
{
    public sealed record ReleaseOptionGroup(string Tag, IReadOnlyList<Link> Links);
    private sealed record ReleaseDefinition(string Tag, IReadOnlyList<Link> Links);

    public enum Link
    {
        SyncIncomingWithoutRedraw,
        SyncOutgoingBatching,
        ShowTestBuildUpdates,
        DisableRedraws
    }

    private static readonly ReleaseDefinition[] Releases =
    [
        new("v.1.1.11.1071", [Link.SyncIncomingWithoutRedraw, Link.SyncOutgoingBatching]),
        new("v.1.1.12.50", [Link.ShowTestBuildUpdates]),
        new("v.1.1.12.88", [Link.DisableRedraws])
    ];

    private static readonly IReadOnlyDictionary<Link, string> LinkTitles = new Dictionary<Link, string>
    {
        [Link.SyncIncomingWithoutRedraw] = "Sync: Incoming Sync (Default: Disabled)",
        [Link.SyncOutgoingBatching] = "Sync: Outgoing Batching (Default: Disabled)",
        [Link.ShowTestBuildUpdates] = "Notifications: Testbuild Update Hints (Default: Disabled)",
        [Link.DisableRedraws] = "Sync: Disable Redraws (Default: Disabled)"
    };

    private static readonly IReadOnlyDictionary<Link, Action<SpheneConfigService, UiSharedService, float>> LinkDrawers
        = new Dictionary<Link, Action<SpheneConfigService, UiSharedService, float>>
    {
        [Link.SyncIncomingWithoutRedraw] = (configService, uiShared, _) =>
            SyncBehaviorOptionBlock.DrawIncomingSyncWithoutRedraw(configService, uiShared, "UpdateOptionSyncIncomingWithoutRedraw"),
        [Link.SyncOutgoingBatching] = (configService, uiShared, outgoingSliderWidth) =>
            SyncBehaviorOptionBlock.DrawOutgoingSyncBatching(configService, uiShared, outgoingSliderWidth, "UpdateOptionSyncOutgoingBatching"),
        [Link.ShowTestBuildUpdates] = (configService, uiShared, _) =>
            NotificationsOptionBlock.DrawShowTestBuildUpdatesOption(configService, uiShared, "UpdateOptionShowTestBuildUpdates"),
        [Link.DisableRedraws] = (configService, uiShared, _) =>
            SyncBehaviorOptionBlock.DrawDisableRedraws(configService, uiShared, "UpdateOptionDisableRedraws")
    };

    public static string GetTitle(Link link)
        => LinkTitles.TryGetValue(link, out var title) ? title : "Option";

    public static IReadOnlyList<ReleaseOptionGroup> GetPendingReleaseGroups(SpheneConfigService configService)
    {
        var seenTags = GetSeenTags(configService);
        return
        [
            .. Releases
                .Where(release => !seenTags.Contains(release.Tag))
                .Select(release => new ReleaseOptionGroup(release.Tag, release.Links))
        ];
    }

    public static IReadOnlyList<Link> GetVisibleLinks(SpheneConfigService configService)
    {
        return
        [
            .. GetPendingReleaseGroups(configService)
                .SelectMany(release => release.Links)
                .Distinct()
        ];
    }

    public static void DrawByLink(Link link, SpheneConfigService configService, UiSharedService uiShared, float outgoingSliderWidth)
    {
        if (LinkDrawers.TryGetValue(link, out var drawer))
        {
            drawer(configService, uiShared, outgoingSliderWidth);
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
        var pendingReleases = GetPendingReleaseGroups(configService);

        foreach (var release in pendingReleases)
        {
            seenTags.Add(release.Tag);
            latestSeenTag = release.Tag;
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
        var orderedReleaseTags = Releases
            .Select(release => release.Tag)
            .Where(seenTags.Contains);

        return [.. orderedReleaseTags, .. seenTags.Except(orderedReleaseTags, StringComparer.Ordinal)];
    }
}
