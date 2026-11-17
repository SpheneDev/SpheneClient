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
        SupporterIconLabelEdge,
        PanelTooltipPadding,
        PanelPairListContentPadding,
        PanelPairRowLayout,
        ProfilePopoutRightClick,
        PairRequestsEmbedded,
        DataAnalysisHub,
        SyncshellProfiles,
        ModernUiSurfaces,
        SyncshellCommunityInlineEditing,
        PhotoStudioEditor,
        LinkBrandPresets,
        CommunityVisitorPreview,
        GalleryAudience,
        SupporterDepthOcclusion,
        SupporterGpuDepthOcclusion,
        SupporterBuiltInHalo,
        SupporterIconSize,
        SupporterNameplateFx,
        SupporterNameplateStudio,
        IconNotificationBubble,
        CommunityRefresh,
        NewsAnnouncements,
        LayoutDensity,
        SyncshellMembershipApplications,
        SyncshellPublishingPipeline,
        SyncshellCommunityEvents,
        CommunityCommentsModeration,
        CommunityExperience,
        CommunityHub,
        FixedPageChrome,
        EventTravelDestinations,
        CommunityLocation,
        EventPlanner,
        ChroniclesEditor,
        MarkdownRichText,
        DirectMessaging,
        FollowedCommunities,
        ShrinkUIntegrationToggle,
        DirectMessagingE2ee,
        ChatEmoji,
        ChatGifs
    }

    private static readonly IReadOnlySet<Link> SupporterFeatureLinks = new HashSet<Link>
    {
        Link.SupporterNameplateIndicators,
        Link.SupporterNameplateCustomization,
        Link.SupporterNameplateStyleSync,
        Link.SupporterServerIcons,
        Link.SupporterNameplateGlowColors,
        Link.SupporterIconLabelEdge,
        Link.SupporterDepthOcclusion,
        Link.SupporterGpuDepthOcclusion,
        Link.SupporterBuiltInHalo,
        Link.SupporterNameplateFx,
        Link.SupporterNameplateStudio
    };

    /// <summary>First-seen date of a release tag, if the panel recorded one.</summary>
    public static DateTime? ReleaseDate(string tag, SpheneConfigService configService)
        => configService.Current.ReleaseSeenDates.TryGetValue(tag, out var d) ? d : null;

    private static readonly ReleaseDefinition[] Releases =
    [
        new("v.1.1.11.1071", [Link.SyncIncomingWithoutRedraw, Link.SyncOutgoingBatching]),
        new("v.1.1.13.0", [Link.ShowTestBuildUpdates, Link.FilterCharacterLegacyShpkOutgoing, Link.MismatchTrackerFilters, Link.TransfersUseSpheneCdnDirectDownloads]),
        new("v.1.1.15", [Link.NotificationSoundsSpheneDefault]),
        new("v.1.1.16", [Link.SupporterNameplateIndicators, Link.SupporterNameplateCustomization, Link.SupporterNameplateStyleSync, Link.SupporterServerIcons]),
        new("v.1.1.17", [Link.PanelPairListSeparatorPadding, Link.SupporterNameplateGlowColors]),
        new("v.1.1.18", [Link.ReopenSpheneHubOnStartup, Link.ReleaseNotesPage, Link.HubHomePage, Link.SupporterIconLabelEdge]),
        new("v.1.1.19", [Link.PanelTooltipPadding, Link.PanelPairListContentPadding, Link.PanelPairRowLayout]),
        new("v.1.1.20", [Link.ProfilePopoutRightClick]),
        new("v.1.1.21", [Link.PairRequestsEmbedded, Link.DataAnalysisHub, Link.SyncshellProfiles]),
        new("v.1.1.22", [Link.ModernUiSurfaces]),
        new("v.1.1.23", [Link.SyncshellCommunityInlineEditing, Link.PhotoStudioEditor, Link.LinkBrandPresets, Link.CommunityVisitorPreview, Link.GalleryAudience, Link.SupporterDepthOcclusion]),
        new("v.1.1.24", [Link.SupporterGpuDepthOcclusion]),
        new("v.1.1.25", [Link.SupporterBuiltInHalo]),
        new("v.1.1.26", [Link.SupporterIconSize]),
        new("v.1.1.27", [Link.SupporterNameplateFx]),
        new("v.1.1.28", [Link.SupporterNameplateStudio]),
        new("v.1.1.29", [Link.IconNotificationBubble, Link.CommunityRefresh]),
        new("v.1.1.30", [Link.NewsAnnouncements]),
        new("v.1.1.31", [Link.LayoutDensity]),
        new("v.1.1.32", [Link.SyncshellMembershipApplications]),
        new("v.1.1.33", [Link.SyncshellPublishingPipeline]),
        new("v.1.1.34", [Link.SyncshellCommunityEvents]),
        new("v.1.1.35", [Link.CommunityCommentsModeration]),
        new("v.1.1.36", [Link.CommunityExperience]),
        new("v.1.1.37", [Link.CommunityHub]),
        new("v.1.1.38", [Link.FixedPageChrome]),
        new("v.1.1.39", [Link.EventTravelDestinations, Link.CommunityLocation]),
        new("v.1.1.40", [Link.EventPlanner]),
        new("v.1.1.41", [Link.ChroniclesEditor, Link.MarkdownRichText]),
        new("v.1.1.42", [Link.DirectMessaging]),
        new("v.1.1.43", [Link.FollowedCommunities]),
        new("v.1.1.44", [Link.ShrinkUIntegrationToggle]),
        new("v.1.1.45", [Link.DirectMessagingE2ee]),
        new("v.1.1.46", [Link.ChatEmoji, Link.ChatGifs]),
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
        [Link.SupporterIconLabelEdge] = "Supporter: Icon label text outline (Default: Enabled)",
        [Link.PanelTooltipPadding] = "Panel: Tooltip Padding & more Panel Theme options (Default: 10/8)",
        [Link.PanelPairListContentPadding] = "Panel: Pair List Content Padding & redesigned folder headers (Default: 8)",
        [Link.PanelPairRowLayout] = "Panel: Pair Row Padding, Rounding & Hover color (Defaults: 4 / inherit / subtle)",
        [Link.ProfilePopoutRightClick] = "Panel: Profile popout opens on right-click (hover delay removed)",
        [Link.PairRequestsEmbedded] = "Sphene Hub: Pair Requests embedded into the Friends page",
        [Link.DataAnalysisHub] = "Sphene Hub: Data Analysis page · redesigned with sidebar navigation",
        [Link.SyncshellProfiles] = "Syncshells: Community pages replace welcome pages · banner, avatar, events, news, rules, links & gallery",
        [Link.ModernUiSurfaces] = "Interface: Unified modern surfaces · row groups, cards & segmented controls across all windows",
        [Link.SyncshellCommunityInlineEditing] = "Syncshells: Community pages edit in place · posts, events, links, jobs & page settings live on the page itself",
        [Link.PhotoStudioEditor] = "Interface: Image studio · screen snipping, file browser & live adjustments",
        [Link.LinkBrandPresets] = "Community & Profiles: Link chips with brand marks · Discord, X, Ko-fi, YouTube, Twitch & more",
        [Link.CommunityVisitorPreview] = "Syncshells: Community popout · visitor preview toggle & inline shell rename",
        [Link.GalleryAudience] = "Syncshells: Gallery · per-photo & per-album visibility (everyone / staff / selected members), in-lightbox editing",
        [Link.SupporterDepthOcclusion] = "Supporter: Depth-buffer occlusion paused (using collision checks)",
        [Link.SupporterGpuDepthOcclusion] = "Supporter: GPU occlusion for icons and icon labels (Default: Enabled)",
        [Link.SupporterBuiltInHalo] = "Supporter: Animated Sphene Halo built into the client",
        [Link.SupporterIconSize] = "Supporter: Icon size slider for nameplate icons (Default: 150%)",
        [Link.SupporterNameplateFx] = "Supporter: Animated nameplate effects, synced through your style (Default: None)",
        [Link.SupporterNameplateStudio] = "Supporter: Interactive nameplate editor · live preview with effect, icon & mark galleries",
        [Link.IconNotificationBubble] = "Interface: Icon notification bubble & quick panel (Default: Enabled)",
        [Link.CommunityRefresh] = "Community: Activity feed, notification inbox, linked names & richer pages",
        [Link.NewsAnnouncements] = "News: Persisted announcements · article reader, live push & admin authoring",
        [Link.LayoutDensity] = "Interface: Responsive layout density · auto / compact / balanced / comfortable (Default: Auto)",
        [Link.SyncshellMembershipApplications] = "Syncshells: Join policies & membership applications · public, approval, invite-only or closed",
        [Link.SyncshellPublishingPipeline] = "Syncshells: Publishing pipeline · drafts, staff review, scheduled posts & media uploads",
        [Link.SyncshellCommunityEvents] = "Syncshells: Events · RSVP, capacity & waitlists, calendar, reminders & recurring dates",
        [Link.CommunityCommentsModeration] = "Community: Comments, reactions & moderation on news, events and Chronicles",
        [Link.CommunityExperience] = "Community: Richer feed, discover page, profile activity & notification deep-links",
        [Link.CommunityHub] = "Community: Unified hub page · shared tabs for news, photos, events, Chronicles & activity",
        [Link.FixedPageChrome] = "Interface: Fixed page headers & tabs · page help moves to the title bar, honest member/online counts",
        [Link.EventTravelDestinations] = "Events: Travel destinations & one-click travel with Lifestream (optional)",
        [Link.CommunityLocation] = "Community: Permanent published location & travel for everyone (optional)",
        [Link.EventPlanner] = "Events: Dedicated Event Planner workspace with date/time pickers, organizers & live preview",
        [Link.ChroniclesEditor] = "Chronicles: Dedicated editor, live preview, covers, author identity & Everyone/Friends/Only me visibility",
        [Link.MarkdownRichText] = "Markdown: Text sizes up to 32 pt, text colors, caret-aware formatting & selection wrapping",
        [Link.DirectMessaging] = "Social: Direct messages · unread badges, inbox entries & quick chat access",
        [Link.FollowedCommunities] = "Communities: Follow syncshells, browse area-bound shells & see who is in the area",
        [Link.ShrinkUIntegrationToggle] = "Plugins: ShrinkU integration toggle (intro setup + Settings → Plugins)",
        [Link.DirectMessagingE2ee] = "Social: End-to-end encrypted direct messages · sealed history & key verification",
        [Link.ChatEmoji] = "Social: Emoji picker everywhere you write · chat, comments, Chronicles & bio with color emoji",
        [Link.ChatGifs] = "Social: KLIPY GIFs in private chat · encrypted provider references, bounded animations"
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
            SpheneUI.Controls.HelpText("Open Settings > Release Notes · also available inside the Sphene Hub's embedded settings.");
        },
        [Link.HubHomePage] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The Sphene Hub now opens on a Home page: live stats, quick access into every section, a news feed slot and a supporter overview · everything converges in one place.");
            SpheneUI.Controls.HelpText("The Supporter settings page was reworked too and now lists every perk your supporter status unlocks.");
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
        },
        [Link.PanelTooltipPadding] = (_, _, _) =>
        {
            var theme = SpheneTheme.Current;
            var tooltipPadding = theme.CompactTooltipPadding;
            if (SpheneUI.Controls.SliderFloat2("Panel Tooltip Padding##UpdateOptionPanelTooltipPadding", ref tooltipPadding, 0.0f, 24.0f, "%.1f"))
            {
                theme.CompactTooltipPadding = tooltipPadding;
                theme.NotifyThemeChanged();
            }
            SpheneUI.Controls.HelpText("Content padding of tooltips inside the Panel, independent from the general theme. The Panel Theme settings also expose popup background, accent, hover/active and additional spacing options now.");
        },
        [Link.PanelPairListContentPadding] = (_, _, _) =>
        {
            var theme = SpheneTheme.Current;
            var padding = theme.CompactPairListContentPadding;
            if (SpheneUI.Controls.SliderFloat("Pair List Content Padding##UpdateOptionPanelPairListContentPadding", ref padding, 0.0f, 24.0f, "%.1f"))
            {
                theme.CompactPairListContentPadding = padding;
                theme.NotifyThemeChanged();
            }
            SpheneUI.Controls.HelpText("Horizontal inset of the pair list: rows and folder headers gain breathing room to the window edges. Folder headers are also restyled bands now · hover-reactive, with a count pill, and clickable to collapse.");
        },
        [Link.PanelPairRowLayout] = (_, _, _) =>
        {
            var theme = SpheneTheme.Current;
            var changed = false;
            var rowPadding = theme.CompactPairRowContentPadding;
            if (SpheneUI.Controls.SliderFloat("Pair Row Content Padding##UpdateOptionPanelPairRowPadding", ref rowPadding, 0.0f, 16.0f, "%.1f"))
            {
                theme.CompactPairRowContentPadding = rowPadding;
                changed = true;
            }
            var rowRounding = theme.CompactPairRowRounding;
            if (SpheneUI.Controls.SliderFloat("Pair Row Rounding##UpdateOptionPanelPairRowRounding", ref rowRounding, -1.0f, 30.0f, "%.1f"))
            {
                theme.CompactPairRowRounding = rowRounding;
                changed = true;
            }
            var rowHover = theme.CompactPairRowHover;
            if (SpheneUI.Controls.ColorEdit("Pair Row Hover##UpdateOptionPanelPairRowHover", ref rowHover))
            {
                theme.CompactPairRowHover = rowHover;
                changed = true;
            }
            if (changed)
            {
                theme.NotifyThemeChanged();
            }
            SpheneUI.Controls.HelpText("Pair rows get their own styling: content padding insets avatar and buttons from the row edges, rounding shapes the row (use -1 to follow Child Surface Rounding), and the hover color is independent from the global Hover Highlight.");
        },
        [Link.ProfilePopoutRightClick] = (configService, uiShared, mediator) =>
        {
            AppearanceOptionBlock.DrawShowSpheneProfilesOption(configService, uiShared, mediator, "UpdateOptionShowSpheneProfiles");
            SpheneUI.Controls.HelpText("Right-click a pair row to pop out the profile · it stays open until you click elsewhere or move the mouse off the window. The hover delay option was removed.");
        },
        [Link.PairRequestsEmbedded] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Incoming and outgoing pair requests now live directly on the Sphene Hub's Friends page · accept or decline them without opening a separate window.");
            SpheneUI.Controls.HelpText("The standalone Pair Requests window still exists and can be opened from the Friends page header.");
        },
        [Link.DataAnalysisHub] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Character Data Analysis was rebuilt around a sidebar: an Overview dashboard with per-entity stats, a dedicated Files browser, a Textures page for BC7 conversion and backups, plus separate Transient Files and Recording pages.");
            SpheneUI.Controls.HelpText("Find it in the Sphene Hub under Data Analysis · or pop it out into its own window from the sidebar.");
        },
        [Link.SyncshellProfiles] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Syncshells now have a full community page instead of a plain welcome message · banner, avatar, tagline, accent color, about, rules, links, news posts, scheduled events, a gallery and toggleable sections, no Markdown needed.");
            SpheneUI.Controls.HelpText("Members see the page on join (if enabled) and in the Hub under Syncshells → Community. Owners can opt into the public community directory so others can discover their page. The Markdown Formatter was removed from the Hub.");
        },
        [Link.ModernUiSurfaces] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Every window outside the CompactUI now shares one modern surface language: grouped settings rows with hairline separators, quiet tonal surfaces instead of stacked boxes, adaptive card grids for albums, links, staff and community discovery, and connected segmented controls wherever only a few options exist.");
            SpheneUI.Controls.HelpText("Settings pages, syncshell admin, community pages, profiles, join/create dialogs and the discover rail all follow the same tablet-style grouping · icon actions sit in section headers with tooltips.");
        },
        [Link.SyncshellCommunityInlineEditing] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Community pages are now edited where the content lives: the + on News or Events opens an inline composer (posts can be saved as staff-only drafts), the pens on About, Rules and Links expand an editor right inside the section, and the Manage button opens in-page subpages for page settings and staff jobs · no separate workspace window.");
            SpheneUI.Controls.HelpText("Everyone sees the same page · owners, moderators and delegated staff simply get extra pens, composers and subpages where their permissions allow. Group administration (members, password, area binding) stays in the syncshell admin panel.");
        },
        [Link.PhotoStudioEditor] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The photo studio was rebuilt around three sources: snip a region straight off your screen (drag a box, lock it to an aspect preset, hide the game HUD for a clean shot), open a file through the system dialog, or browse folders with thumbnails and a warning when a file is too small for the target. The editor puts the image on a canvas next to a tool panel: named crop presets for avatar, banner, community and list row · or free-form · plus brightness, saturation and vignette over a live preview. Only the rendered result is uploaded.");
            SpheneUI.Controls.HelpText("The studio is used for community banners, avatars and gallery photos, and for your own profile images too. Animated GIFs can skip the editor with 'Keep animation'.");
        },
        [Link.LinkBrandPresets] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The Links section editor now offers a brand preset per link · pick Discord, X, Ko-fi, YouTube, Twitch, Instagram, Bluesky, TikTok, Patreon, GitHub, Carrd, Lodestone, a venue or shop page, or stay with a custom link. The chip then renders the brand's own mark and color instead of the neutral globe.");
            SpheneUI.Controls.HelpText("Pick a preset before typing the URL · it pre-fills the label and the address hint. Presets are stored per link; more brands can be added over time.");
        },
        [Link.CommunityVisitorPreview] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The community popout gets a small eye chip next to the fold control: one click renders the page exactly as your members and visitors see it · owner pens, composers, visibility toggles and hidden sections all disappear. Click again to get your editing tools back. Nothing is sent to the server; it is a local preview only.");
            SpheneUI.Controls.HelpText("The shell's name is now also edited right in the header · click the pen next to the name (or the name itself) and confirm with Enter, just like the tagline below it.");
        },
        [Link.GalleryAudience] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Photos and albums now carry a visibility: everyone, staff only, or a hand-picked member list · set it in the lightbox's edit drawer (pen top-right) or the album manager, and members simply never see what is not addressed to them. The same drawer brings back caption editing and the two-click delete that had gone missing.");
            SpheneUI.Controls.HelpText("Thumbnails no longer print captions · the caption lives in the lightbox where the photo is big, so the like badge never overlaps text again. Restricted items show a small 'Staff'/'Selected' pill to everyone who can still see them.");
        },
        [Link.SupporterDepthOcclusion] = (_, _, _) =>
        {
            SupporterNameplateOptionBlock.DrawSupporterDepthOcclusionOption();
        },
        [Link.SupporterGpuDepthOcclusion] = (configService, _, _) =>
        {
            SupporterNameplateOptionBlock.DrawGpuDepthOcclusionOption(configService, "UpdateGpuDepthOcclusion");
        },
        [Link.SupporterBuiltInHalo] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Sphene Halo is an animated gold-and-cyan supporter icon bundled with the client. Select it in the nameplate editor's icon gallery (Settings > Supporter); no server icon upload is needed.");
            SpheneUI.Controls.HelpText("Paired users on older clients see the regular Sphene icon until they update.");
        },
        [Link.SupporterIconSize] = (configService, _, _) =>
        {
            SupporterNameplateOptionBlock.DrawSupporterIconSizeOption(configService, "UpdateOptionSupporterIconSize");
        },
        [Link.SupporterNameplateFx] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Supporters can now pick an animated nameplate effect · Bat Orbit, Title Star Ornaments, Rising Embers, Drifting Petals, Floating Hearts or Snow Drift. The choice travels inside the synced supporter style, so paired users see the same animation around your plate.");
            SpheneUI.Controls.HelpText("Pick yours in the nameplate editor's effect gallery (Settings > Supporter). Effects weave around the name · behind and in front of the letters · and respect other nameplates, the target cursor and world depth.");
        },
        [Link.SupporterNameplateStudio] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The Supporter page is now an interactive editor: a live stage shows your real nameplate · mark, label, icon and effect included · while galleries below preview every effect, icon and mark symbol playing on your own name. Click a card to stage it on the preview, then Apply equips.");
            SpheneUI.Controls.HelpText("Open Settings > Supporter and hit Customize next to the preview · or the Supporter entry in the hub rail. Non-supporters can browse the galleries; equipping stays a supporter perk.");
        },
        [Link.IconNotificationBubble] = (configService, _, _) =>
        {
            var bubble = configService.Current.ShowIconNotificationBubble;
            if (SpheneUI.Controls.Toggle("Notification Bubble##UpdateOptionIconBubble", ref bubble))
            {
                configService.Current.ShowIconNotificationBubble = bubble;
            }
            SpheneUI.Controls.HelpText("While anything is pending, a small bubble with the count rides the floating icon · click it for the inbox. Right-click the icon for the styled quick panel.");
        },
        [Link.CommunityRefresh] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The community pass lands: the Hub home is now a real dashboard with a pending-items card and an activity feed of news, events and photos from all your syncshells · each row jumps straight into that shell's community page. The bell in the hub header opens a shared notification inbox, and the floating icon shows a bubble + right-click quick panel with plugin shortcuts.");
            SpheneUI.Controls.HelpText("Feed, inbox and icon bubble share one pending-items source. Community pages gained proper empty states, and every link tile now opens on click and copies on right-click.");
        },
        [Link.NewsAnnouncements] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Announcements on the News page are now real persisted articles stored on the server · they survive restarts and inbox clearing, arrive live the moment they publish, and open into a proper article reader with banner, markdown body and links. Markdown gained lists, blockquotes, rules, links and inline images.");
            SpheneUI.Controls.HelpText("Admins and moderators get a + New button on the News page with a markdown editor and live preview · drafts sit in the feed with their own badge until they go live. Notifications stay in the inbox; announcements live in their own store now.");
        },
        [Link.LayoutDensity] = (configService, _, _) =>
        {
            AppearanceOptionBlock.DrawLayoutDensityOption(configService, "UpdateOptionLayoutDensity");
        },
        [Link.SyncshellMembershipApplications] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Syncshells gained a join policy: besides the classic password or invite, owners can open their shell to everyone, require staff approval, restrict it to invites only, or close it entirely. Approval-gated shells let interested souls file a join request with a message; staff review pending requests from the syncshell administration and approve or decline them.");
            SpheneUI.Controls.HelpText("Owners and moderators set the policy under Syncshell Administration > Settings > Join Policy and review requests under Members > Join Requests. Decisions arrive as notifications.");
        },
        [Link.SyncshellPublishingPipeline] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Community news and gallery images now run through a publishing pipeline: owners pick per-content publish modes - direct, approval-required or staff-only - and members can submit posts that land in a staff review queue. Content supports drafts, scheduled publishing and co-authors; the server enforces visibility and status, so nothing bypasses moderation.");
            SpheneUI.Controls.HelpText("Policy lives under Syncshell Administration > Settings > Publishing Policy; the review queue sits next to Join Requests on the Members page. Chronicles gained draft and visibility-scope controls.");
        },
        [Link.SyncshellCommunityEvents] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Community events are now fully participatory: members RSVP as Going, Interested or Can't go, hosts can set a capacity so latecomers land on a fair first-come waitlist and move up automatically when seats free up. Events carry timezones, reminders, cover images and public or members-only visibility - recurring series repeat daily, weekly, biweekly or monthly, and single dates can be moved or cancelled from the new calendar view. Members may submit events for staff review when the shell allows it.");
            SpheneUI.Controls.HelpText("RSVP on the event's detail page; staff manage the attendee list there too. The Events tab toggles between the familiar list and a month calendar; the Events publish mode lives under Syncshell Administration > Publishing Policy.");
        },
        [Link.CommunityCommentsModeration] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("News posts, community events and Chronicle entries now have discussion threads: members comment and reply in place, react with Like, Love, Haha, Wow or Sad, and report problematic content. Moderators can hide, restore or remove comments and lock whole threads; Chronicle owners moderate their own entries. Every reply, mention and moderation decision lands in the notification inbox - and each notification kind can be muted.");
            SpheneUI.Controls.HelpText("Comment permissions live under Syncshell Administration > Publishing Policy; the new Reports card on the Members page resolves flagged content. Chronicle comments follow the entry's visibility (everyone, friends or only me).");
        },
        [Link.CommunityExperience] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The community experience grows up: the activity feed now carries Chronicles, comments, reactions and RSVPs with endless scroll and kind filters, and every row deep-links into the exact community section it came from. Discover Communities gained search, sorting and join-aware cards - Join, Apply, Password, Invite only or Closed reflect each shell's real join policy. Friend profiles gained an Activity tab showing what they post and interact with, and inbox notifications now jump straight to the post or event they are about.");
            SpheneUI.Controls.HelpText("Feed filters live above the feed on Home and the Community page; the Discover section sits below it. Per-kind notification mutes are under Settings > Notifications > Community Notifications.");
        },
        [Link.CommunityHub] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("The Community page is now a real hub: a compact gradient banner on top, one shared tab strip, and a two-column desktop layout - the feed on the left, a sidebar with your community stats and quick links on the right. Highlights surface the next event, the latest news and the newest photo above the chronological Overview feed.");
            SpheneUI.Controls.HelpText("Photos open a responsive thumbnail grid, Events get their own date-plate cards, and Activity collects comments, reactions and RSVPs on your posts. The selected tab survives back/forward navigation. On narrow windows the sidebar collapses into a slim stats strip above the feed.");
        },
        [Link.FixedPageChrome] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Page banners and tab strips now stay pinned while the content beneath them scrolls - the Community banner and tabs, your profile hero, friend profiles, the News filters and the style editor preview all keep their place. Redundant page titles are gone and every page's \"?\" help now lives next to the breadcrumb in the title bar.");
            SpheneUI.Controls.HelpText("Community, Syncshells and shell cards now count members and online users honestly: distinct people across your shells, including yourself when you are connected.");
        },
        [Link.EventTravelDestinations] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Events can now carry a real travel destination: the Syncshell's bound property, a custom housing address or a public location - including apartments, rooms and subdivisions. A compact Location card on the event detail shows the address and, when the Lifestream plugin is installed, a one-click travel button that walks you to the plot. Destinations are explicit snapshots an organizer chooses - they never leak a shell's bound areas on their own.");
            SpheneUI.Controls.HelpText("Organizers find the new 'Travel destination' picker in the event editor - recurring events can override or clear it per date. Travel needs the Lifestream plugin; without it the address simply stays readable. 'Show on Map' works with or without it.");
        },
        [Link.CommunityLocation] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("A Syncshell can now publish a permanent community location - the shell's bound property or a custom housing address, including apartments and subdivisions. Members and public visitors see a compact 'Community location' card on the Overview and can travel there or open the map - no event or editing rights needed.");
            SpheneUI.Controls.HelpText("Owners and authorized managers configure it under Community > Page Settings > Community location - nothing is published until the toggle is explicitly switched on. Unpublishing hides it again everywhere.");
        },
        [Link.EventPlanner] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Event management now lives in a dedicated Event Planner workspace - a new Syncshell rail entry for owners, staff, organizers and members who submit events. The Community events page stays attendee-facing: browsing, details, location & travel, RSVP, attendees and comments, without the editing clutter.");
            SpheneUI.Controls.HelpText("The editor uses real date, time and time-zone pickers (your zone is detected automatically), resolves organizers and attendees by name instead of raw IDs, keeps recurring-date overrides, and offers a Live preview popout that shows exactly what attendees will see - no saving required.");
        },
        [Link.ChroniclesEditor] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Blog entries are now Chronicles - with a dedicated full-page editor instead of the small compose flyout. Title, cover image and the rich Markdown editor share the main column while visibility, pinning and draft controls sit in a settings rail; a movable Live Preview window shows the entry exactly as readers will see it, before anything is saved.");
            SpheneUI.Controls.HelpText("Every Chronicle names its author with avatar and alias, supports cover images, and offers Everyone / Friends / Only me visibility - enforced server-side on lists, feeds, activity and direct reads. Authors can edit or delete their own entries from the Chronicles tab.");
        },
        [Link.MarkdownRichText] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("All Markdown editors gained richer formatting: text sizes up to 32 pt via <size=N>, theme-friendly preset or custom text colors via <color=#rrggbb>, and toolbar actions that insert at the real caret - or wrap the current selection - instead of appending at the end.");
            SpheneUI.Controls.HelpText("The toolbar's A icon opens the size flyout, the swatch opens color presets plus a custom picker. Formatting markers survive save/reload and render identically in preview and published views.");
        },
        [Link.DirectMessaging] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Direct messages now surface like real conversations: an incoming DM shows a toast (using your Info notification settings, so chat and volume preferences apply), the icon bubble and inbox gain one persistent row per conversation with the latest message preview, and a gold dot marks unread chats right on the pair row in the panel and on friend rows in the Hub.");
            SpheneUI.Controls.HelpText("Every row, inbox entry and the pair's context menu offer a Message shortcut that jumps straight into the chat tab for that friend - the conversation is marked read the moment it is on screen, so unread badges clear on their own.");
        },
        [Link.FollowedCommunities] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Syncshell communities are now readable without membership: any listed or area-bound shell opens its community page for everyone, and a Follow button on the page (or the directory card) bookmarks it into a Followed Communities section on the Syncshells page. Followed shells feed their public events into your Home feed right next to joined ones, and the community page shows how many users follow the shell.");
            SpheneUI.Controls.HelpText("Area-bound shells show who is currently inside the area and only offer the join button while you actually stand inside it - outside the bounds the page stays open for browsing and following instead.");
        },
        [Link.ShrinkUIntegrationToggle] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("ShrinkU integration can now be switched on or off directly: the intro setup offers the choice on its ShrinkU step, and the Plugins page shows a real enable/disable switch on the ShrinkU row.");
            SpheneUI.Controls.HelpText("Turning integration off hides ShrinkU's own windows and automatic optimization - you can still compress textures from the conversion button in the main panel.");
        },
        [Link.DirectMessagingE2ee] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Direct messages are now end-to-end encrypted: text is sealed on your device with keys that never leave it, the server only ever stores and forwards ciphertext, and each installation gets its own device key. A lock banner in the chat shows the encryption status - use Verify there to compare safety numbers with your contact.");
            SpheneUI.Controls.HelpText("Older history stays readable and is upgraded to encrypted storage gradually when both sides have keys - it keeps an honest 'still plaintext' marker until then. Chats with contacts on older versions can't send until they update.");
        },
        [Link.ChatEmoji] = (_, _, _) =>
        {
            SpheneUI.Text.Wrapped("Every text field you write in now has an emoji button: private chat, community comments, the Chronicle editor and your profile bio share one picker with search, categories and recent emojis. Emoji render in color via a bundled Noto Emoji set, survive saving and stay inside the usual character limits.");
            SpheneUI.Controls.HelpText("Skin tones, combined family/profession emoji and emoji flags are supported where the bundled set provides them; unsupported sequences degrade to a simpler emoji instead of a blank box.");
        },
        [Link.ChatGifs] = (configService, _, _) =>
        {
            SpheneUI.Text.Wrapped("Private chats can send GIFs: the GIF button next to the message input searches KLIPY and sends the selection end-to-end encrypted like a normal message. Only a small provider reference travels inside the encrypted message - the animation itself loads from KLIPY on each device.");
            ChatOptionBlock.DrawExternalMediaOption(configService, "UpdateOptionChatExternalMedia");
            ChatOptionBlock.DrawGifAnimationOption(configService, "UpdateOptionChatGifAnimation");
            SpheneUI.Controls.HelpText("GIFs never appear in comments, Chronicles or the community feed. Received GIFs render at most 320x220 and pause off-screen; a missing provider key or disabled external media shows a small placeholder instead. GIF sending is rolled out gradually by the server - the button appears once your account is granted access.");
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

        var seenNow = DateTime.UtcNow;
        foreach (var release in pendingReleases)
        {
            seenTags.Add(release.Tag);
            latestSeenTag = release.Tag;
            if (!configService.Current.ReleaseSeenDates.ContainsKey(release.Tag))
            {
                configService.Current.ReleaseSeenDates[release.Tag] = seenNow;
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
        var orderedReleaseTags = Releases
            .Select(release => release.Tag)
            .Where(seenTags.Contains);

        return [.. orderedReleaseTags, .. seenTags.Except(orderedReleaseTags, StringComparer.Ordinal)];
    }
}
