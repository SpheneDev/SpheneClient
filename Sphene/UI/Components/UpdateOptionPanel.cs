using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.Services.Mediator;
using Sphene.UI.Design;

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
        FilterCharacterLegacyShpkOutgoing,
        MismatchTrackerFilters,
        TransfersUseSpheneCdnDirectDownloads,
        NotificationSoundsSpheneDefault,
        SupporterNameplateIndicators,
        SupporterNameplateCustomization,
        SupporterNameplateStyleSync,
        SupporterServerIcons,
        PanelPairListSeparatorPadding,
        SupporterNameplateGlowColors,
        ReopenSpheneHubOnStartup,
        ReleaseNotesPage,
        HubHomePage,
        SupporterTitlePlacement,
        SupporterIconLabelEdge
    }

    private static readonly IReadOnlySet<Link> SupporterFeatureLinks = new HashSet<Link>
    {
        Link.SupporterNameplateIndicators,
        Link.SupporterNameplateCustomization,
        Link.SupporterNameplateStyleSync,
        Link.SupporterServerIcons,
        Link.SupporterNameplateGlowColors,
        Link.SupporterTitlePlacement,
        Link.SupporterIconLabelEdge
    };

    private static readonly ReleaseDefinition[] Releases =
    [
        new("v.1.1.11.1071", [Link.SyncIncomingWithoutRedraw, Link.SyncOutgoingBatching]),
        new("v.1.1.13.0", [Link.ShowTestBuildUpdates, Link.FilterCharacterLegacyShpkOutgoing, Link.MismatchTrackerFilters, Link.TransfersUseSpheneCdnDirectDownloads]),
        new("v.1.1.15", [Link.NotificationSoundsSpheneDefault]),
        new("v.1.1.16", [Link.SupporterNameplateIndicators, Link.SupporterNameplateCustomization, Link.SupporterNameplateStyleSync, Link.SupporterServerIcons]),
        new("v.1.1.17", [Link.PanelPairListSeparatorPadding, Link.SupporterNameplateGlowColors]),
        new("v.1.1.18", [Link.ReopenSpheneHubOnStartup, Link.ReleaseNotesPage, Link.HubHomePage, Link.SupporterTitlePlacement, Link.SupporterIconLabelEdge]),
    ];

    private static readonly IReadOnlyDictionary<Link, string> LinkTitles = new Dictionary<Link, string>
    {
        [Link.SyncIncomingWithoutRedraw] = "Sync: Incoming Sync (Default: Disabled)",
        [Link.SyncOutgoingBatching] = "Sync: Outgoing Batching (Default: Disabled)",
        [Link.ShowTestBuildUpdates] = "Notifications: Testbuild Update Hints (Default: Disabled)",
        [Link.FilterCharacterLegacyShpkOutgoing] = "Sync: Filter characterlegacy.shpk in Sync Data (Experimental, Default: Disabled)",
        [Link.MismatchTrackerFilters] = "Diagnostics: Active Mismatch Tracker Filters (Defaults: Equipment Off, Companions Off)",
        [Link.TransfersUseSpheneCdnDirectDownloads] = "Transfers: Use CDN direct downloads (Default: Enabled)",
        [Link.NotificationSoundsSpheneDefault] = "Notifications: Sphene Default Sound Mode (Default: Sphene Default)",
        [Link.SupporterNameplateIndicators] = "Supporter: Nameplate Indicators & Sphene Icon (Default: Enabled)",
        [Link.SupporterNameplateCustomization] = "Supporter: Symbol, Label & Color Customization",
        [Link.SupporterNameplateStyleSync] = "Supporter: Your style is shared with other Sphene users",
        [Link.SupporterServerIcons] = "Supporter: Server-provided & animated icons",
        [Link.PanelPairListSeparatorPadding] = "Panel: Pair List Separator Padding (Default: 0)",
        [Link.SupporterNameplateGlowColors] = "Supporter: Symbol & Label Glow Colors (Default: Disabled)",
        [Link.ReopenSpheneHubOnStartup] = "Appearance: Reopen Sphene Hub on startup if it was open (Default: Disabled)",
        [Link.ReleaseNotesPage] = "General: Release Notes page (browsable changelog in Settings & Hub)",
        [Link.HubHomePage] = "Sphene Hub: Home landing page & reworked Supporter page",
        [Link.SupporterTitlePlacement] = "Supporter: Nameplate title placement (Default: Game default)",
        [Link.SupporterIconLabelEdge] = "Supporter: Icon label text outline (Default: Enabled)"
    };

    private static readonly IReadOnlyDictionary<Link, Action<SpheneConfigService, UiSharedService, SpheneMediator>> LinkDrawers
        = new Dictionary<Link, Action<SpheneConfigService, UiSharedService, SpheneMediator>>
    {
        [Link.SyncIncomingWithoutRedraw] = (configService, uiShared, _) =>
            SyncBehaviorOptionBlock.DrawIncomingSyncWithoutRedraw(configService, uiShared, "UpdateOptionSyncIncomingWithoutRedraw"),
        [Link.SyncOutgoingBatching] = (configService, uiShared, _) =>
            SyncBehaviorOptionBlock.DrawOutgoingSyncBatching(configService, uiShared, "UpdateOptionSyncOutgoingBatching"),
        [Link.ShowTestBuildUpdates] = (configService, uiShared, _) =>
            NotificationsOptionBlock.DrawShowTestBuildUpdatesOption(configService, uiShared, "UpdateOptionShowTestBuildUpdates"),
        [Link.FilterCharacterLegacyShpkOutgoing] = (configService, uiShared, mediator) =>
            SyncBehaviorOptionBlock.DrawFilterCharacterLegacyShpkInOutgoingCharacterData(configService, uiShared, mediator, "UpdateOptionFilterCharacterLegacyShpk"),
        [Link.MismatchTrackerFilters] = (configService, uiShared, _) =>
            DebugOptionBlock.DrawActiveMismatchTrackerFilterOptions(configService, uiShared, "UpdateOptionMismatchTrackerFilters"),
        [Link.TransfersUseSpheneCdnDirectDownloads] = (configService, uiShared, _) =>
            TransfersOptionBlock.DrawUseSpheneCdnDirectDownloadsOption(configService, uiShared, "UpdateOptionTransfersUseSpheneCdnDirectDownloads"),
        [Link.NotificationSoundsSpheneDefault] = (configService, uiShared, mediator) =>
            NotificationsOptionBlock.DrawSpheneDefaultSoundModeOption(configService, uiShared, mediator, "UpdateOptionNotificationSoundsSpheneDefault"),
        [Link.SupporterNameplateIndicators] = (configService, uiShared, _) =>
        {
            SupporterNameplateOptionBlock.DrawShowSupporterNameplateOption(configService, uiShared, "UpdateOptionShowSupporterNameplate");
            SupporterNameplateOptionBlock.DrawSupporterIconOverlayOption(configService, uiShared, "UpdateOptionSupporterIconOverlay");
            SupporterNameplateOptionBlock.DrawSupporterIconLabelOption(configService, uiShared, "UpdateOptionSupporterIconLabel");
        },
        [Link.SupporterNameplateCustomization] = (configService, uiShared, _) =>
        {
            SupporterNameplateOptionBlock.DrawSupporterCustomizationInfo(configService, uiShared, "UpdateOptionSupporterCustomization");
        },
        [Link.SupporterNameplateStyleSync] = (configService, uiShared, _) =>
        {
            SupporterNameplateOptionBlock.DrawSupporterStyleSyncInfo(configService, uiShared, "UpdateOptionSupporterStyleSync");
        },
        [Link.SupporterServerIcons] = (configService, uiShared, _) =>
        {
            SupporterNameplateOptionBlock.DrawSupporterServerIconsInfo(configService, uiShared, "UpdateOptionSupporterServerIcons");
        },
        [Link.SupporterNameplateGlowColors] = (configService, uiShared, _) =>
        {
            SupporterNameplateOptionBlock.DrawSupporterGlowColorsInfo(configService, uiShared, "UpdateOptionSupporterGlowColors");
        },
        [Link.PanelPairListSeparatorPadding] = (_, _, _) =>
        {
            var theme = SpheneTheme.Current;
            var separatorPadding = theme.CompactPairListSeparatorPadding;
            if (SpheneUI.Controls.SliderFloat("Pair List Separator Padding##UpdateOptionPanelPairListSeparatorPadding", ref separatorPadding, 0.0f, 20.0f, "%.1f"))
            {
                theme.CompactPairListSeparatorPadding = separatorPadding;
                theme.NotifyThemeChanged();
            }
        },
        [Link.ReopenSpheneHubOnStartup] = (configService, _, _) =>
            AppearanceOptionBlock.DrawReopenSpheneHubOnStartupOption(configService, "UpdateOptionReopenSpheneHubOnStartup"),
        [Link.ReleaseNotesPage] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Sphene release notes are now browsable at any time: the dedicated Release Notes page lists every release with its changelog, including testbuild entries when enabled.");
            SpheneUI.Controls.HelpText("Open Settings > Release Notes — also available inside the Sphene Hub's embedded settings.");
        },
        [Link.HubHomePage] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The Sphene Hub now opens on a Home page: live stats, quick access into every section, a news feed slot and a supporter overview — everything converges in one place.");
            SpheneUI.Controls.HelpText("The Supporter settings page was reworked too and now lists every perk your supporter status unlocks.");
        },
        [Link.SupporterTitlePlacement] = (configService, _, _) =>
        {
            var placement = (int)configService.Current.SupporterTitlePlacement;
            if (SpheneUI.Controls.LabeledCombo("Nameplate Title##UpdateOptionSupporterTitlePlacement", ref placement,
                    "Game default\0Above name\0Below name\0Hidden\0"))
            {
                configService.Current.SupporterTitlePlacement = (SupporterTitlePlacement)placement;
                configService.Save();
            }
            SpheneUI.Controls.HelpText("Where your game title appears on your nameplate. Game default keeps your in-game nameplate setting.");
        },
        [Link.SupporterIconLabelEdge] = (configService, _, _) =>
        {
            var enabled = configService.Current.SupporterIconLabelEdgeEnabled;
            if (SpheneUI.Controls.Toggle("Icon Label Outline##UpdateOptionSupporterIconLabelEdge", ref enabled))
            {
                configService.Current.SupporterIconLabelEdgeEnabled = enabled;
                configService.Save();
            }
            SpheneUI.Controls.HelpText("Draws a colored outline around the text next to the Sphene icon on nameplates. Configure text, fill and outline colors in Settings > Supporter.");
        }
    };

    public static string GetTitle(Link link)
        => LinkTitles.TryGetValue(link, out var title) ? title : "Option";

    public static IReadOnlyList<ReleaseOptionGroup> GetPendingReleaseGroups(SpheneConfigService configService, bool supporterFeaturesEnabled)
    {
        var seenTags = GetSeenTags(configService);
        return
        [
            .. Releases
                .Where(release => !seenTags.Contains(release.Tag))
                .Select(release => new ReleaseOptionGroup(release.Tag, FilterLinks(release.Links, supporterFeaturesEnabled)))
                .Where(group => group.Links.Count > 0)
        ];
    }

    private static IReadOnlyList<Link> FilterLinks(IReadOnlyList<Link> links, bool supporterFeaturesEnabled)
    {
        if (supporterFeaturesEnabled)
        {
            return links;
        }

        return [.. links.Where(link => !SupporterFeatureLinks.Contains(link))];
    }

    public static IReadOnlyList<Link> GetVisibleLinks(SpheneConfigService configService, bool supporterFeaturesEnabled)
    {
        return
        [
            .. GetPendingReleaseGroups(configService, supporterFeaturesEnabled)
                .SelectMany(release => release.Links)
                .Distinct()
        ];
    }

    public static void DrawByLink(Link link, SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, bool supporterFeaturesEnabled)
    {
        if (!supporterFeaturesEnabled && SupporterFeatureLinks.Contains(link))
        {
            return;
        }

        if (LinkDrawers.TryGetValue(link, out var drawer))
        {
            drawer(configService, uiShared, mediator);
        }
    }

    public static bool HasUnseenTag(SpheneConfigService configService, bool supporterFeaturesEnabled)
    {
        return GetPendingReleaseGroups(configService, supporterFeaturesEnabled).Count > 0;
    }

    public static void MarkCurrentTagAsSeen(SpheneConfigService configService, bool supporterFeaturesEnabled)
    {
        var seenTags = GetSeenTags(configService);
        var latestSeenTag = configService.Current.LastSeenNewOptionsTag;
        var pendingReleases = GetPendingReleaseGroups(configService, supporterFeaturesEnabled);

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
