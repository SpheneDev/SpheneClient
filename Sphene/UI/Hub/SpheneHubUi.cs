using System.Collections.Concurrent;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Announcement;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.CharaDataHub;
using Sphene.UI.Components;
using Sphene.UI.Components.Popup;
using Sphene.UI.Design;
using Sphene.UI.Housing;
using Sphene.UI.Panels;
using Sphene.UI.Syncshell;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.Data;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text;

namespace Sphene.UI.Hub;

/// <summary>
/// Central "Sphene Hub" window: a nav-rail main UI that hosts the user's
/// profile (always-visible avatar in the rail header), syncshell overview
/// with an inline admin panel, the character data hub and mod sharing  · 
/// one extensible entry point next to the CompactUI.
/// </summary>
internal sealed class SpheneHubUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly SupporterNameplateStudioUi _supporterStudio;
    private readonly PairManager _pairManager;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly UiFactory _uiFactory;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly EditProfileUi _editProfileUi;
    private readonly CharaDataHubUi _charaDataHubUi;
    private readonly DataAnalysisUi _dataAnalysisUi;
    private readonly ModSharingUi _modSharingUi;
    private readonly SettingsUi _settingsUi;
    private readonly CommunityMediaClientService _communityMedia;

    private SpheneHubPage _activePage = SpheneHubPage.Home;
    private bool _profileEditMode;
    private readonly SpheneUI.Profile.HeroEdit _heroEdit = new();

    // Profile extras · Profile/Gallery/Blog sub-tabs on the own + friend pages.
    private int _profilePageTab;
    private int _friendPageTab;
    /// <summary>Deep-link target tab for the friend page - consumed by the
    /// uid-change reset so a feed/blog jump lands on the right sub-tab.</summary>
    private int? _pendingFriendTab;
    private string? _friendPageUid;
    private readonly List<SpheneUI.Controls.TabItem> _profileTabItems = [];
    private readonly List<SpheneUI.Controls.TabItem> _friendTabItems = [];
    // Lightbox shared by both profile surfaces (own manage + friend view).
    private readonly List<SyncshellGalleryImageDto> _profileViewerImages = [];
    private int _profileViewerIndex;
    private float _profileViewerOpenT;
    private string? _profileViewerKey;
    // Viewer edit drawer · caption/album buffer plus two-click delete arming.
    private bool _photoEditOpen;
    private long _photoEditImageId;
    private string _photoEditCaption = string.Empty;
    private long? _photoEditAlbumId;
    private bool _photoEditSaving;
    private string? _photoEditError;
    private bool _photoDeleteArmed;
    private long? _pendingViewerRemoveId;
    private readonly Sphene.UI.Chronicles.ChronicleComposer _chronicleComposer = new();
    /// <summary>Post ids whose bounded Chronicle excerpt is expanded in
    /// place (Show more/less) - per session, shared by own + friend lists.</summary>
    private readonly HashSet<long> _expandedChronicles = new();

    /// <summary>The Chronicle currently open in the detail reader - the
    /// list shows compact feed rows; clicking one opens just that entry
    /// with its full discussion.</summary>
    private (string OwnerUid, long Id)? _openChronicle;
    private readonly PublicProfileService _publicProfiles;
    private string? _publicUid;
    private byte[] _publicAvatarBytes = [];
    private byte[] _publicBannerBytes = [];
    private string? _publicAvatarB64;
    private string? _publicBannerB64;
    private string _publicSearchInput = string.Empty;
    private string? _publicSearchCommitted;
    private string? _publicFollowBusy;
    /// <summary>Shown in the Chronicles surface when a deep-linked post can't
    /// resolve after the list loaded - deleted or no longer visible.</summary>
    private string? _chronicleNotice;
    private const string ChronicleMissingNotice =
        "That Chronicle couldn't be opened - it may have been deleted or is no longer visible to you.";
    private readonly MarkdownComposer _blogComposer = new("own-blog");
    private readonly SpheneUI.Popups.ConfirmDialog _chronicleDeleteConfirm = new();
    private bool _chronicleSaving;
    private string? _chronicleError;
    // Profile gallery · album filter/manage state plus the upload phase label.
    private long? _ownGalleryAlbumFilter;
    private long? _friendGalleryAlbumFilter;
    private string _ownAlbumName = string.Empty;
    private long _ownAlbumEditId;
    private string _ownAlbumEditName = string.Empty;
    private string? _galleryUploadPhase;
    // Scratch · remapped extras DTOs for the shared gallery grid/viewer.
    private readonly List<SyncshellGalleryImageDto> _profileGalleryScratch = [];
    /// <summary>Overview previews · 3-tile grid list (refilled per frame) and the
    /// full-gallery payload handed to the viewer on click.</summary>
    private readonly List<SyncshellGalleryImageDto> _overviewGridScratch = [];
    private readonly List<SyncshellGalleryImageDto> _overviewViewerScratch = [];
    private float _profileTabSwitchT = 1f;
    private float _friendTabSwitchT = 1f;
    private bool _styleUseAccent, _styleUseTop, _styleUseBottom;
    private bool _styleSeeded;
    private bool _styleTopOver, _styleBottomOver;
    private Vector4 _styleAccent = new(0.30f, 0.80f, 0.95f, 1f);
    private Vector4 _styleTop = new(0.30f, 0.80f, 0.95f, 1f);
    private Vector4 _styleBottom = new(0.40f, 0.30f, 0.80f, 1f);
    private SpheneProfileTheme _styleBaseTheme = SpheneProfileTheme.Default;
    private string? _selectedGroupGid;
    private SyncshellDetailTab _syncshellDetailTab = SyncshellDetailTab.Community;
    private long? _pendingEditEventId;
    private SyncshellEventOccurrenceDto? _pendingEditOccurrence;
    private SyncshellHubSubPage _syncshellSubPage = SyncshellHubSubPage.List;
    private readonly JoinSyncshellUI _joinSyncshellUi;
    private readonly CreateSyncshellUI _createSyncshellUi;
    /// <summary>Currently-viewed community's gid - marks which cached entry the
    /// visible page reads and which push-triggered refreshes matter.</summary>
    private string? _communityCacheGid;
    /// <summary>Bounded per-shell community cache (MRU) - survives hub
    /// navigation so revisits don't refetch the full aggregate.</summary>
    private readonly ConcurrentDictionary<string, CommunityEntry> _communityMap = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _communityLoadingMap = new(StringComparer.Ordinal);
    /// <summary>Failure latch per gid - backs off retries instead of
    /// re-firing the request every frame.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _communityFailedAt = new(StringComparer.Ordinal);
    /// <summary>Debounce latch - one in-flight-or-pending refresh per gid.</summary>
    private readonly ConcurrentDictionary<string, byte> _communityRefreshScheduled = new(StringComparer.Ordinal);
    /// <summary>Shells that returned a null community - no page exists; kept
    /// separate from the failure latch so it doesn't render as an error.</summary>
    private readonly ConcurrentDictionary<string, byte> _communityNoPage = new(StringComparer.Ordinal);
    private const int MaxCachedCommunities = 6;
    private static readonly TimeSpan CommunityFailureBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CommunityRefreshDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CommunityFetchTimeout = TimeSpan.FromSeconds(25);

    private sealed class CommunityEntry
    {
        public SyncshellCommunityDto Dto;
        public long LastUsedTicks;
        public CommunityEntry(SyncshellCommunityDto dto) { Dto = dto; }
    }
    private CommunityComposers? _communityComposers;
    private readonly CommunityEditSession _communityEditSession = new();
    private readonly UnsavedChangesToast _communityToast = new();
    private string? _communityEditSessionGid;
    private GroupData? _communityEditSessionGroup;
    // Own-profile staged edits · merged UserSetProfile patch plus the
    // display-name/alias/image side channels that live outside the DTO.
    private UserProfileDto? _profilePatch;
    private bool _profileNameSet;
    private string? _profileName;
    private bool _profileAliasSet;
    private string? _profileAlias;
    private byte[]? _profileAvatarBytes;
    private byte[]? _profileBannerBytes;
    private byte[] _lastDraftAvatarBytes = [];
    private byte[] _lastDraftBannerBytes = [];
    private readonly UnsavedChangesToast _profileToast = new();

    /// <summary>Contextual post/event/photo editors · anchored flyouts on the community page.</summary>
    private CommunityComposers CommunityComposers
        => _communityComposers ??= CreateCommunityComposers();

    private CommunityComposers CreateCommunityComposers()
    {
        var composers = new CommunityComposers(_apiController, Mediator, _logger, _communityMedia);
        composers.OnAuthorClick = NavigateToAuthor;
        return composers;
    }
    private readonly SyncshellCommunityView.CommunityPageState _communityPage = new();
    /// <summary>Shared comment-thread widget for the friend journal surface.</summary>
    private readonly CommentThreadView _blogComments = new();
    // deferred pinned-header overlay · set by the community page while the
    // nav scroll child is open, invoked right after it ends
    private Action? _communityOverlay;
    /// <summary>Community DTO for the viewed shell, or null - reads the
    /// per-gid map so previous shells' entries survive navigation.</summary>
    private SyncshellCommunityDto? CommunityCache
        => _communityCacheGid is { } g ? CommunityFor(g) : null;

    private SyncshellCommunityDto? CommunityFor(string gid)
    {
        if (_communityMap.TryGetValue(gid, out var e))
        {
            e.LastUsedTicks = Environment.TickCount64;
            return e.Dto;
        }
        return null;
    }

    private void PutCommunity(string gid, SyncshellCommunityDto dto)
    {
        _communityMap[gid] = new CommunityEntry(dto) { LastUsedTicks = Environment.TickCount64 };
        _communityFailedAt.TryRemove(gid, out _);
        _communityNoPage.TryRemove(gid, out _);
        if (_communityMap.Count > MaxCachedCommunities)
        {
            var oldest = _communityMap.OrderBy(kv => kv.Value.LastUsedTicks).First().Key;
            _communityMap.TryRemove(oldest, out _);
        }
    }

    /// <summary>Patches a cached community in place - mutation results apply
    /// instantly without waiting for the pushed debounced refresh.</summary>
    private void MutateCommunity(string gid, Func<SyncshellCommunityDto, SyncshellCommunityDto> mutate)
    {
        if (_communityMap.TryGetValue(gid, out var e))
        {
            e.Dto = mutate(e.Dto);
            e.LastUsedTicks = Environment.TickCount64;
        }
    }
    /// <summary>Serialized Discover request state - every directory/preview
    /// fetch binds to the generation that issued it, so a reset can't race
    /// a delayed completion or a queued open.</summary>
    private readonly DiscoverRequestTracker _discover = new();
    private List<SyncshellDirectoryEntryDto>? _favoriteEntries;
    private bool _favoritesLoading;
    private string _discoverSearch = string.Empty;
    private int _discoverSort;
    private List<SyncshellCommunityFeedItemDto>? _friendActivityItems;
    private string? _friendActivityCursor;
    private bool _friendActivityLoading;
    private Pair? _viewingFriend;
    private readonly Dictionary<string, SyncshellAdminUI> _embeddedAdminPanels = new(StringComparer.Ordinal);
    private byte[] _lastAvatarBytes = [];
    private byte[] _lastBannerBytes = [];
    private byte[] _friendAvatarBytes = [];
    private byte[] _friendBannerBytes = [];
    private readonly Dictionary<string, byte[]> _friendListImageBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _friendRowImageBytes = new(StringComparer.Ordinal);
    private string? _noteEditUid;
    private string _noteEditBuffer = string.Empty;
    private List<AreaBoundSyncshellDto>? _areaBoundShells;
    private bool _areaBoundLoading;
    private bool _charaDataVisible;
    private bool _wasOpenBeforeGpose;
    private bool _suppressOpenStatePersist;
    private readonly SpheneConfigService _configService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly UiSharedService _uiShared;
    private readonly AreaBoundSyncshellService _areaBoundService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly NotificationInboxService _notificationInbox;
    private readonly CommunityFeedService _communityFeed;
    private readonly AnnouncementService _announcements;
    private readonly ChangelogService _changelogService;
    private readonly ChatService _chatService;
    private readonly LifestreamTravelService _lifestreamTravel;
    private readonly Interop.Ipc.IpcManager _ipcManager;
    // Rail swap animation: 0 = hub nav, 1 = section nav. The hub list
    // dissolves out under a veil while the section list slides in from the
    // rail edge and materializes through a second veil.
    private float _sectionRailAnim;
    // Which menu set the rail child drew last frame · its scroll offset is
    // reset on change so a leftover offset can't park rows under the header.
    private SpheneHubPage _drawnRailList = SpheneHubPage.Home;

    /// <summary>The last non-section location · the fixed "Back to Hub" strip inside a section
    /// (Settings, Mod Sharing, Character Data) jumps straight here instead of walking the back stack.</summary>
    private HubLocation _lastHubLocation = new(SpheneHubPage.Home, null, null);
    private HubLocation? _lastDrawnLocation;
    private double _pageSwitchAt = -1;

    /// <summary>One location in the hub's back/forward history: page plus its detail context · on
    /// section pages it also carries the active sub-page so back/forward walks through it.
    /// <see cref="CommunitySection"/>/<see cref="CommunityItemId"/> deep-link into a shell's
    /// community tab + drill-in (event detail, focused news post) - feed cards, notification
    /// actions and entity refs all land on the exact surface.</summary>
    private readonly record struct HubLocation(SpheneHubPage Page, string? GroupGid, Pair? Friend,
        int? SubPage = null, string? HousingKey = null,
        string? ReleaseVersion = null,
        long? AnnouncementId = null, bool AnnouncementEdit = false,
        SyncshellCommunitySection? CommunitySection = null, long? CommunityItemId = null,
        /// <summary>Chronicle deep-link - the reader opens this post on the
        /// location's owner (friend or self) and focuses the comment.</summary>
        long? ChroniclePostId = null, long? FocusCommentId = null,
        /// <summary>Public-profile surface - the uid whose shared profile
        /// is on screen (only while Page == PublicProfile).</summary>
        string? PublicUid = null);
    /// <summary>Back/forward history - each entry pairs the location with
    /// the content scroll offset it was captured at, so back/forward lands
    /// where the user actually was (best effort: the offset clamps to the
    /// restored page's real height).</summary>
    private readonly List<(HubLocation Location, float ScrollY)> _navBack = [];
    private readonly List<(HubLocation Location, float ScrollY)> _navForward = [];
    /// <summary>The shared content child's live scroll offset - refreshed
    /// every frame inside the child so captures are right regardless of
    /// which surface triggered the navigation.</summary>
    private float _contentScrollY;
    /// <summary>Scroll offset to apply on the first draw of a restored
    /// location; null resets to the top like a fresh navigation.</summary>
    private float? _pendingScrollRestore;
    /// <summary>List scroll offset stashed when a chronicle reader opens
    /// in place - closing the reader lands back there, not at the top.</summary>
    private float _preReaderScrollY;

    public SpheneHubUi(ILogger<SpheneHubUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        ApiController apiController, PairManager pairManager,
        SpheneProfileManager spheneProfileManager, UiFactory uiFactory,
        DalamudUtilService dalamudUtilService, EditProfileUi editProfileUi,
        CharaDataHubUi charaDataHubUi, DataAnalysisUi dataAnalysisUi,
        ModSharingUi modSharingUi, SettingsUi settingsUi,
        SpheneConfigService configService, AreaBoundSyncshellService areaBoundService,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        UiSharedService uiShared,
        ServerConfigurationManager serverConfigurationManager,
        JoinSyncshellUI joinSyncshellUi, CreateSyncshellUI createSyncshellUi,
        HousingPageView housingPageView,
        SupporterNameplateStudioUi supporterStudio,
        NotificationInboxService notificationInbox,
        CommunityFeedService communityFeed, AnnouncementService announcements,
        ChangelogService changelogService,
        ChatService chatService, CommunityMediaClientService communityMedia,
        LifestreamTravelService lifestreamTravel, Interop.Ipc.IpcManager ipcManager,
        Services.Gifs.IGifProvider gifProvider, PublicProfileService publicProfiles)
        : base(logger, mediator, "Sphene Hub###SpheneHubUI", performanceCollectorService)
    {
        _lifestreamTravel = lifestreamTravel;
        _ipcManager = ipcManager;
        _notificationInbox = notificationInbox;
        _communityFeed = communityFeed;
        _announcements = announcements;
        _changelogService = changelogService;
        _chatService = chatService;
        _communityMedia = communityMedia;
        _publicProfiles = publicProfiles;
        SyncshellCommunityView.MediaClient = communityMedia;
        MarkdownComposer.MediaClient = communityMedia;
        _gifProvider = gifProvider;
        _gifPicker = new UI.Components.GifPicker(gifProvider, configService);
        _stickerPicker = new UI.Components.GifPicker(gifProvider, configService, stickers: true);
        SpheneUI.EmojiPickerHook = new UI.Components.EmojiPicker(configService);
        EntityChipList.Api = _apiController;
        EntityChipList.Navigate = NavigateEntityRef;
        _apiController = apiController;
        _supporterStudio = supporterStudio;
        _housingPageView = housingPageView;
        _housingPageView.OnNavigate = loc => NavigateTo(new HubLocation(
            SpheneHubPage.Housing, null, null,
            HousingKey: loc == null ? null : HousingPageView.KeyOf(loc.Value)));
        _pairManager = pairManager;
        _spheneProfileManager = spheneProfileManager;
        _uiFactory = uiFactory;
        _dalamudUtilService = dalamudUtilService;
        _editProfileUi = editProfileUi;
        _charaDataHubUi = charaDataHubUi;
        _dataAnalysisUi = dataAnalysisUi;
        _modSharingUi = modSharingUi;
        _settingsUi = settingsUi;
        _configService = configService;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _uiShared = uiShared;
        _areaBoundService = areaBoundService;
        _serverConfigurationManager = serverConfigurationManager;
        _joinSyncshellUi = joinSyncshellUi;
        _createSyncshellUi = createSyncshellUi;
        _settingsUi.SettingsPageChanged += OnSettingsPageChanged;
        _modSharingUi.SubPageChanged += OnModSharingPageChanged;
        _charaDataHubUi.SubPageChanged += OnCharaDataPageChanged;
        _dataAnalysisUi.SubPageChanged += OnDataAnalysisPageChanged;

        IsOpen = configService.Current.ReopenSpheneHubOnStartup
            && configService.Current.SpheneHubWasOpen;
        Size = new Vector2(1150, 750);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(1024, 560),
            MaximumSize = new(2000, 1600),
        };

        Mediator.Subscribe<OpenSpheneHubMessage>(this, (msg) =>
        {
            if (!string.IsNullOrEmpty(msg.GroupGid))
            {
                NavigateTo(new HubLocation(SpheneHubPage.Syncshells, msg.GroupGid, null,
                    msg.DetailTab,
                    CommunitySection: msg.CommunitySection, CommunityItemId: msg.CommunityItemId,
                    FocusCommentId: msg.FocusCommentId));
                if (msg.EditEventId is { } editEventId)
                {
                    // The community cache may still be loading - the planner
                    // consumes the pending seed as soon as events land.
                    _pendingEditEventId = editEventId;
                    _pendingEditOccurrence = msg.EditOccurrence;
                }
            }
            else if (msg.SyncshellSub is { } syncshellSub)
            {
                NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null, (int)syncshellSub));
                if (syncshellSub == SyncshellHubSubPage.Join && msg.JoinGid != null)
                {
                    _joinSyncshellUi.PrefillTarget(msg.JoinGid);
                }
            }
            else if (msg.PublicProfileUid is { } pubUid)
            {
                NavigateTo(new HubLocation(SpheneHubPage.PublicProfile, null, null,
                    PublicUid: pubUid, ChroniclePostId: msg.ChroniclePostId,
                    FocusCommentId: msg.FocusCommentId));
            }
            else if (msg.ChroniclePostId is { } chronId)
            {
                // Chronicle deep-link: own notifications land on the profile's
                // Chronicles tab, someone else's on their friend page - the
                // reader opens the post and focuses the comment inline.
                var owner = msg.ChronicleOwnerUid;
                var ownUid = _apiController.UID;
                if (owner == null || string.Equals(owner, ownUid, StringComparison.Ordinal))
                {
                    NavigateTo(new HubLocation(SpheneHubPage.Profile, null, null, 2,
                        ChroniclePostId: chronId, FocusCommentId: msg.FocusCommentId));
                }
                else if (FindPairByUid(owner) is { } ownerPair)
                {
                    NavigateTo(new HubLocation(SpheneHubPage.Friends, null, ownerPair,
                        (int)FriendHubSubPage.Chronicles,
                        ChroniclePostId: chronId, FocusCommentId: msg.FocusCommentId));
                }
                else
                {
                    // Not self and not a pair - the public profile page is the
                    // reader's next stop; a revoked profile lands on its
                    // neutral unavailable state instead of a dead end.
                    NavigateTo(new HubLocation(SpheneHubPage.PublicProfile, null, null,
                        PublicUid: owner, ChroniclePostId: chronId,
                        FocusCommentId: msg.FocusCommentId));
                }
            }
            else if (msg.Friend != null)
            {
                NavigateTo(new HubLocation(SpheneHubPage.Friends, null, msg.Friend,
                    msg.FriendSub is { } friendSub ? (int)friendSub : null));
            }
            else if (msg.Page is { } page)
            {
                SetPage(page);
            }
            if (msg.EditProfile)
            {
                _profileEditMode = true;
                SeedHeroEditBuffers(_spheneProfileManager.GetSpheneProfile(
                    new UserData(_apiController.UID)));
            }
            // Already-open-but-folded must expand too - OnOpen only fires on
            // the closed->open transition, so reset the fold explicitly.
            SpheneUI.Chrome.ResetFold(this, WindowName);
            IsOpen = true;
        });
        // A follow/unfollow from any community page reloads the Followed
        // section - and the directory's star flags go stale at the same time.
        Mediator.Subscribe<SyncshellFavoritesChangedMessage>(this, (_) =>
        {
            _favoriteEntries = null;
            _discover.Reset();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            SetOpenWithoutPersist(false);
            ResetTransientState();
        });
        Mediator.Subscribe<GposeStartMessage>(this, (_) => { _wasOpenBeforeGpose = IsOpen; SetOpenWithoutPersist(false); });
        Mediator.Subscribe<GposeEndMessage>(this, (_) => SetOpenWithoutPersist(_wasOpenBeforeGpose));
        // A shell's community content changed server-side · drop the media
        // header for that shell and refresh the cached page when it's the
        // one being viewed. Other shells' caches stay stale until opened.
        Mediator.Subscribe<LifestreamTravelStateChangedMessage>(this, (_) =>
            _communityPage.TravelVersion++);
        Mediator.Subscribe<CommunityChangedMessage>(this, (msg) =>
        {
            var gid = msg.Community.Group.GID;
            _shellMediaCache.TryRemove(gid, out _);
            // cached thread pages only refetch on invalidate - the pushed
            // change names its target, so drop it even when a different
            // group is on screen (same fix as _blogComments below).
            _communityPage.Comments.InvalidateForChange(msg.Community);
            if (string.Equals(_communityCacheGid, gid, StringComparison.Ordinal))
            {
                _ = RefreshCommunity();
            }
            else
            {
                _communityMap.TryRemove(gid, out _);
                _communityNoPage.TryRemove(gid, out _);
            }
        });
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData == null || string.Equals(msg.UserData.UID, _apiController.UID, StringComparison.Ordinal))
            {
                SpheneUI.Media.Evict(AvatarKey);
                SpheneUI.Media.Evict(BannerKey);
            }
            if (msg.UserData == null
                || (_viewingFriend != null
                    && string.Equals(msg.UserData.UID, _viewingFriend.UserData.UID, StringComparison.Ordinal)))
            {
                SpheneUI.Media.Evict(FriendAvatarKey);
                SpheneUI.Media.Evict(FriendBannerKey);
                _friendAvatarBytes = [];
                _friendBannerBytes = [];
                _friendActivityItems = null;
                _friendActivityCursor = null;
            }
            // Comment threads cache per post and only refetch on invalidate -
            // a profile push for ANY user (own chronicles included) can mean
            // new comments, so drop every cached page lazily. Thread drafts
            // and expansion state survive the reset.
            _blogComments.InvalidateAll();
        });
    }

    public override void OnOpen()
    {
        base.OnOpen();
        // Any way of reaching the hub must land it usable - reopening a
        // folded window expands it instead of leaving a dead strip.
        SpheneUI.Chrome.ResetFold(this, WindowName);
        if (_suppressOpenStatePersist)
        {
            _suppressOpenStatePersist = false;
            return;
        }
        PersistOpenState(true);
    }

    private void SetOpenWithoutPersist(bool open)
    {
        // Programmatic transitions (disconnect, GPose) must not overwrite the
        // remembered open state · they are not the user's intent. The flag is
        // consumed by whichever OnOpen/OnClose the transition actually fires,
        // regardless of whether the window backend calls it synchronously or
        // deferred.
        if (IsOpen == open)
        {
            return;
        }
        _suppressOpenStatePersist = true;
        IsOpen = open;
    }

    private void PersistOpenState(bool open)
    {
        if (_configService.Current.SpheneHubWasOpen == open)
        {
            return;
        }
        _configService.Current.SpheneHubWasOpen = open;
        // OnOpen/OnClose run outside the window draw try/catch and Save()
        // fires ConfigSave synchronously · a throwing subscriber must not
        // crash the WindowSystem pass.
        try
        {
            _configService.Save();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConfigSave subscriber failed while persisting hub open state");
        }
    }

    protected override bool ChromeHasTitleBar => false;

    protected override bool ChromeWindowPattern => true;

    protected override bool DrawCollapsedDuringFold => true;

    protected override float ChromeCollapsedHeight
        => (CollapsedStripHeight + ImGui.GetStyle().WindowBorderSize * 2f) / SpheneUI.Scale;

    /// <summary>
    /// Fixed rail width · the settings vocabulary exactly as the standalone
    /// window computes it (segmented items minus one window padding), plus a
    /// flat bonus so the header profile card has room for a typical
    /// name/status line. Anything longer ellipsizes and shows on hover · the
    /// rail itself never resizes.
    /// </summary>
    private static float CurrentRailWidth =>
        SpheneUI.Layout.NavRailSegmentedWidth(SettingsUi.EmbeddedNavItems)
        - ImGui.GetStyle().WindowPadding.X
        + 48f * SpheneUI.Scale;

    /// <summary>
    /// Smallest content width the hub ever shows · window minimum minus the
    /// rail. Popout surfaces that must render identically to the hub (community
    /// pages) use this as their minimum width.
    /// </summary>
    internal static float MinContentWidth => 1024f - CurrentRailWidth;

    /// <summary>
    /// Width of the header profile card · rail width plus window padding,
    /// minus its insets. The compact identity strip matches this so both
    /// surfaces show the same card size.
    /// </summary>
    internal static float ProfileCardWidth =>
        CurrentRailWidth + ImGui.GetStyle().WindowPadding.X - 8f * SpheneUI.Scale;

    protected override float ChromeCollapsedWidth
    {
        get
        {
            var scale = SpheneUI.Scale;
            var railW = CurrentRailWidth;
            var leftPad = ImGui.GetStyle().WindowPadding.X;
            var buttonCount = ShowCloseButton ? 2f : 1f;
            var buttons = buttonCount * 30f * scale + buttonCount * 8f * scale + 14f * scale;
            var border = ImGui.GetStyle().WindowBorderSize * 2f;
            return (leftPad + railW + buttons + border) / scale;
        }
    }

    private static float CollapsedStripHeight => SpheneUI.Density.Current switch
    {
        SpheneUI.DensityProfile.Compact => 44f,
        SpheneUI.DensityProfile.Comfortable => 58f,
        _ => 52f,
    } * SpheneUI.Scale;

    private string AvatarKey => $"sphenehub:avatar:{_apiController.UID}";
    private string BannerKey => $"sphenehub:banner:{_apiController.UID}";
    private string FriendAvatarKey => $"sphenehub:friendavatar:{_viewingFriend?.UserData.UID}";
    private string FriendBannerKey => $"sphenehub:friendbanner:{_viewingFriend?.UserData.UID}";

    protected override void DrawInternal()
    {
        var theme = SpheneTheme.Current;
        var ownTheme = _spheneProfileManager
            .GetSpheneProfile(new UserData(_apiController.UID)).ProfileTheme.Value;
        var accentMe = ownTheme.AccentColor(theme.AccentCyan);
        var accentCommunity = theme.AccentBlue;
        var accentTools = SpheneUI.Palette.EtherealPurple;

        // The rail pans between the hub nav and the active section nav  · 
        // the hub list slides up and out while the section list slides in
        // from below, attached at its bottom edge. The anim advances before
        // the rail width is sampled so the band eases with the pan.
        var sectionMode = IsSectionPage(_activePage)
            || (_activePage == SpheneHubPage.Syncshells && _selectedGroupGid != null);
        var railTarget = sectionMode ? 1f : 0f;
        if (!MathF.Equals(_sectionRailAnim, railTarget))
        {
            var step = ImGui.GetIO().DeltaTime / 0.22f;
            _sectionRailAnim = sectionMode
                ? MathF.Min(1f, _sectionRailAnim + step)
                : MathF.Max(0f, _sectionRailAnim - step);
        }

        var railWidth = CurrentRailWidth;
        var bandHeight = CollapsedStripHeight;
        // No chrome title bar · the header band is the title bar and owns the
        // fold/close buttons plus the drag region between its widgets.
        var bandMin = ImGui.GetWindowPos();
        var railRightX = ImGui.GetCursorScreenPos().X + railWidth;

        // Reserve the full-width header band; the nav rail begins below it and
        // the band itself is painted after EndNavRail so both share one color.
        // SetCursorScreenPos lands exactly on the band edge · a Dummy would
        // trail ItemSpacing and leave a window-bg strip under the band.
        var reserve = bandMin.Y + bandHeight - ImGui.GetCursorScreenPos().Y;
        if (reserve > 0f)
        {
            ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new Vector2(0f, reserve));
        }

        var railAnim = _sectionRailAnim;
        // Sequential crossfade · the leaving list dissolves during the first
        // half of the pan and the arriving list only appears in the second
        // half, so the two menus are never on screen together. A rail-colored
        // veil peaks at the swap instant so the handoff reads as a dissolve.
        // Entering a section pans content up; returning lets it come back down.
        var pan = railAnim * railAnim * (3f - 2f * railAnim);
        // Swap progress measured from the departing side · entering a section
        // p follows the pan up, returning to the hub p runs it back down.
        var swap = sectionMode ? pan : 1f - pan;
        var outT = Math.Clamp(swap * 2f, 0f, 1f);
        var inT = Math.Clamp(swap * 2f - 1f, 0f, 1f);
        var slideDist = 30f * SpheneUI.Scale;
        var showHub = sectionMode ? outT < 1f : inT > 0f;
        var showSection = sectionMode ? inT > 0f : outT < 1f;
        var hubShiftY = sectionMode ? -outT * slideDist : -(1f - inT) * slideDist;
        var sectionShiftY = sectionMode ? (1f - inT) * slideDist : outT * slideDist;
        var swapVeil = 1f - MathF.Abs(pan * 2f - 1f);
        // Pinned zone at the rail top while a section rail is shown: the
        // "Back to Hub" item plus a pinned category band · same anatomy as the
        // standalone settings sidebar. The scroll top-inset clips items below
        // it and eases in with the pan.
        var pinItemH = SpheneUI.Layout.NavRailItemHeightPx;
        var pinDividerH = SpheneUI.Layout.NavRailDividerHeight;
        var pinBandH = ImGui.GetTextLineHeight() + 8f * SpheneUI.Scale;
        var contentPadY = 8f * SpheneUI.Scale;
        // Distance from the rail top to the first item slot · the pinned
        // "Back to Hub" row lands on the Home slot so the rail's top row
        // stays put when the menus swap.
        var pinOffsetY = SpheneUI.Spacing.X8.Scaled + ImGui.GetStyle().ItemSpacing.Y;

        SpheneUI.Layout.BeginNavRail("sphenehub", railWidth,
            topInset: contentPadY + pinOffsetY + pinItemH
                + (pinDividerH + pinBandH) * railAnim,
            backdropPattern: true, transition: railAnim,
            bottomInset: RailFooterHeight);
        // Window pos inside the rail child · unlike the cursor this is not
        // shifted by the child's own scroll offset, so the pinned strip never
        // rides the scroll.
        var railItemOrigin = ImGui.GetWindowPos() + new Vector2(0f, contentPadY);
        // The nav child keeps its scroll offset across menu swaps · a
        // leftover offset from the longer section list would park the first
        // hub row under the header band. Reset when the drawn set changes.
        var drawnRailList = showSection ? _activePage : SpheneHubPage.Home;
        if (drawnRailList != _drawnRailList)
        {
            _drawnRailList = drawnRailList;
            ImGui.SetScrollY(0f);
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        var blockStart = ImGui.GetCursorScreenPos();
        // Fixed top slot · anchored to the rail window, never to the
        // scrolled cursor: the row never pans or scrolls. It recompiles its
        // own contents between "Home" and the section's back item while the
        // lists beneath it travel; only the section extras ride the pan.
        var pinTop = new Vector2(railItemOrigin.X, railItemOrigin.Y + pinOffsetY);
        var pinnedCenterY = pinTop.Y + sectionShiftY
            + pinItemH + pinDividerH + pinBandH * 0.5f;
        if (showHub)
        {
            ImGui.SetCursorScreenPos(blockStart + new Vector2(0f, hubShiftY));
            // The morphing top slot owns the first row · reserve its height
            // so the list beneath keeps the same rhythm.
            ImGui.Dummy(new Vector2(1f, pinItemH));
            if (SpheneUI.Layout.NavRailSegmentItem("My Profile", FontAwesomeIcon.UserCircle,
                    _activePage is SpheneHubPage.Profile or SpheneHubPage.ProfileStyle, accentMe))
            {
                SetPage(SpheneHubPage.Profile);
            }
            // Until discovered the Supporter entry keeps the special gold
            // label · visiting the page through any route clears it once.
            var supporterPageActive = _apiController.SupporterFeaturesEnabled
                && _activePage == SpheneHubPage.Supporter;
            var supporterSeen = _configService.Current.HasSeenSupporterSection;
            if (supporterPageActive && !supporterSeen)
            {
                _configService.Current.HasSeenSupporterSection = true;
                _configService.Save();
                supporterSeen = true;
            }
            if (_apiController.SupporterFeaturesEnabled
                && SpheneUI.Layout.NavRailSegmentItem("Supporter", FontAwesomeIcon.Crown,
                    supporterPageActive, SpheneUI.Palette.SpheneGold, special: !supporterSeen,
                    helpId: "settings-Supporter"))
            {
                SetPage(SpheneHubPage.Supporter);
            }
            if (SpheneUI.Layout.NavRailSegmentItem("Friends", FontAwesomeIcon.UserFriends,
                    _activePage == SpheneHubPage.Friends, accentMe,
                    helpId: "friends", helpDrawer: FriendsHelpContent))
            {
                SetPage(SpheneHubPage.Friends);
            }
            if (SpheneUI.Layout.NavRailSegmentItem("News & Updates", FontAwesomeIcon.Bullhorn,
                    _activePage == SpheneHubPage.News, accentMe,
                    helpId: "news", helpDrawer: NewsHelpContent))
            {
                SetPage(SpheneHubPage.News);
            }
            SpheneUI.Layout.NavRailDivider(accentMe, accentCommunity);
            if (SpheneUI.Layout.NavRailSegmentItem("Syncshells", FontAwesomeIcon.Users,
                    _activePage == SpheneHubPage.Syncshells, accentCommunity,
                    helpId: "syncshells", helpDrawer: SyncshellsHelpContent))
            {
                SetPage(SpheneHubPage.Syncshells);
            }
            if (SpheneUI.Layout.NavRailSegmentItem("Community", FontAwesomeIcon.Newspaper,
                    _activePage == SpheneHubPage.Community, accentCommunity))
            {
                SetPage(SpheneHubPage.Community);
            }
            if (SpheneUI.Layout.NavRailSegmentItem("Housing", FontAwesomeIcon.Home,
                    _activePage == SpheneHubPage.Housing, accentCommunity,
                    helpId: "housing", helpDrawer: HousingPageView.HousingHelpContent))
            {
                SetPage(SpheneHubPage.Housing);
            }
            SpheneUI.Layout.NavRailDivider(accentCommunity, accentTools);
            if (SpheneUI.Layout.NavRailSegmentItem("Character Data", FontAwesomeIcon.IdCard,
                    _activePage == SpheneHubPage.DataAnalysis, accentTools,
                    helpId: "dataanalysis"))
            {
                SetPage(SpheneHubPage.DataAnalysis);
            }
            if (SpheneUI.Layout.NavRailSegmentItem("Mod Sharing", FontAwesomeIcon.ShareAlt,
                    _activePage == SpheneHubPage.ModSharing, accentTools,
                    helpId: "modsharing", helpDrawer: ModSharingUi.ModSharingHelpContent))
            {
                SetPage(SpheneHubPage.ModSharing);
            }
            SpheneUI.Layout.NavRailDivider(accentTools, accentMe);
            if (SpheneUI.Layout.NavRailSegmentItem("Settings", FontAwesomeIcon.Cog,
                    _activePage == SpheneHubPage.Settings, accentMe))
            {
                SetPage(SpheneHubPage.Settings);
            }
            ImGui.SetCursorScreenPos(blockStart);
        }
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories = null;
        if (showSection)
        {
            ImGui.SetCursorScreenPos(blockStart + new Vector2(0f, sectionShiftY));
            // Reserve the back item + divider + the pin offset so the first
            // category's slot center lands exactly on the band. The X8 spacer
            // and this dummy each carry a trailing ItemSpacing, and the banded
            // label skips its own top spacer · a plain height would park the
            // label's slot below the band and open a gap before the first button.
            ImGui.Dummy(new Vector2(1f,
                pinItemH + pinDividerH + pinOffsetY
                - SpheneUI.Spacing.X4.Value * SpheneUI.Scale
                - ImGui.GetStyle().ItemSpacing.Y * 2f));
            pinnedCategories = [];
            switch (_activePage)
            {
                case SpheneHubPage.Settings:
                    _settingsUi.DrawEmbeddedRail(pinnedCategories, pinnedCenterY);
                    break;
                case SpheneHubPage.ModSharing:
                    _modSharingUi.DrawEmbeddedRail(pinnedCategories, pinnedCenterY);
                    break;
                case SpheneHubPage.CharacterData:
                    _charaDataHubUi.DrawEmbeddedRail(pinnedCategories, pinnedCenterY);
                    break;
                case SpheneHubPage.DataAnalysis:
                    _dataAnalysisUi.DrawEmbeddedRail(pinnedCategories, pinnedCenterY);
                    break;
                case SpheneHubPage.Syncshells:
                    DrawSyncshellRail(pinnedCategories, pinnedCenterY);
                    break;
            }
            ImGui.SetCursorScreenPos(blockStart);
        }
        // The morphing top slot reports its own active state · it lives in
        // an overlay child, outside the scroll rail's report path.
        if (_activePage == SpheneHubPage.Home)
        {
            SpheneUI.Layout.NavRailReportActive("sphenehub", "Home",
                pinTop.Y + pinItemH * 0.5f, accentMe);
        }
        SpheneUI.Layout.EndNavRail(fadeMinY: railItemOrigin.Y
            + pinOffsetY + pinItemH + (pinDividerH + pinBandH) * railAnim);

        var railPad = SpheneUI.Layout.NavRailPadX * SpheneUI.Scale;
        // Items span to railMax-pad; measuring from the right edge keeps the
        // pinned row exactly as wide as the scroll items.
        var pinW = railRightX - railPad - pinTop.X;
        var railDrawList = ImGui.GetWindowDrawList();
        var railBg = SpheneUI.Layout.NavRailBandColor() with { W = 1f };
        var railClipMin = new Vector2(pinTop.X - railPad, railItemOrigin.Y);
        var railClipMax = new Vector2(railRightX, bandMin.Y + ImGui.GetWindowSize().Y);

        // rail footer · brand lockup pinned to the rail's bottom edge so
        // the logo always reads, like the mock's bottom-left mark.
        var footH = RailFooterHeight;
        var logoSz = 40f * SpheneUI.Scale;
        var footY = railClipMax.Y - footH;
        // opaque band behind the lockup · the scroll area already ends
        // above it, this covers the frame where an item slides out.
        railDrawList.AddRectFilled(
            new Vector2(railClipMin.X - railPad, footY),
            new Vector2(railClipMax.X, railClipMax.Y),
            SpheneUI.ColorUtils.ToU32(railBg));
        railDrawList.AddLine(new Vector2(railClipMin.X - railPad, footY),
            new Vector2(railClipMax.X, footY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                Vector4.One, 0.09f)));
        var logo = SpheneBrandIcons.SpheneLogo();
        if (logo != null)
        {
            var logoPos = new Vector2(railClipMin.X + railPad,
                footY + (footH - logoSz) * 0.5f);
            SpheneUI.Media.ImageRoundedInRect(railDrawList, logo,
                logoPos, logoPos + new Vector2(logoSz), 10f * SpheneUI.Scale);
            var tx = logoPos.X + logoSz + 8f * SpheneUI.Scale;
            railDrawList.AddText(new Vector2(tx, footY + footH * 0.5f
                    - ImGui.GetTextLineHeight()),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), "Sphene");
            railDrawList.AddText(new Vector2(tx, footY + footH * 0.5f),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
                "v" + typeof(Plugin).Assembly.GetName().Version);
        }

        // Section extras below the fixed top slot · divider + pinned category
        // band. They belong to the section rail and travel with its pan.
        if (showSection)
        {
            var extrasMin = new Vector2(pinTop.X, pinTop.Y + pinItemH + sectionShiftY);
            var extrasMax = extrasMin + new Vector2(pinW, pinDividerH + pinBandH);
            ImGui.PushClipRect(railClipMin, railClipMax, false);
            railDrawList.AddRectFilled(extrasMin, extrasMax, SpheneUI.ColorUtils.ToU32(railBg));
            ImGui.SetCursorScreenPos(extrasMin);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var extrasBegun = ImGui.BeginChild("##hubnav-pinex",
                new Vector2(pinW, pinDividerH), false,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse);
            ImGui.PopStyleVar();
            if (extrasBegun)
            {
                SpheneUI.Layout.NavRailDivider(accentMe, accentMe);
            }
            ImGui.EndChild();
            if (pinnedCategories != null)
            {
                // Frame spans the whole extras strip so the band can slide up
                // and out when the next category pushes in · clipping it to
                // just the band would cut the transition off.
                SpheneUI.Layout.PinnedSectionLabel(extrasMin, extrasMax, pinnedCategories,
                    bandMin, bandMin + ImGui.GetWindowSize(),
                    baseColor: railBg, pinCenterY: pinnedCenterY);
            }
            ImGui.PopClipRect();
        }

        // Swap veil over the rail column · peaks at the pan midpoint where
        // neither list is drawn, so the handoff reads as the rail dissolving
        // into its next contents instead of one list covering the other.
        if (railAnim is > 0f and < 1f && swapVeil > 0.01f)
        {
            var veilC = SpheneUI.Layout.NavRailBandColor();
            railDrawList.AddRectFilled(
                new Vector2(bandMin.X, bandMin.Y + bandHeight),
                new Vector2(railRightX, bandMin.Y + ImGui.GetWindowSize().Y),
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(veilC, veilC.W * swapVeil * 0.97f)));
        }

        // Fixed top slot · painted after the swap veil so the row's recompile
        // sweep stays visible while the lists dissolve beneath it. Zero
        // padding so the row spans the same width the scroll child offers.
        ImGui.PushClipRect(railClipMin, railClipMax, false);
        ImGui.SetCursorScreenPos(pinTop);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var pinBegun = ImGui.BeginChild("##hubnav-pin", new Vector2(pinW, pinItemH), false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (pinBegun
            && SpheneUI.Layout.NavRailTopSlotItem("Home", FontAwesomeIcon.Home,
                _activePage == SpheneHubPage.Home,
                _activePage == SpheneHubPage.Syncshells ? "Syncshells" : "Back to Hub",
                FontAwesomeIcon.ChevronLeft, false, pan, accentMe,
                helpId: "home", helpDrawer: HomeHelpContent))
        {
            if (sectionMode)
            {
                NavigateTo(_lastHubLocation);
            }
            else
            {
                SetPage(SpheneHubPage.Home);
            }
        }
        ImGui.EndChild();
        ImGui.PopClipRect();

        DrawHubHeaderBand(bandMin, bandHeight, railRightX, accentMe);

        // the perks preview window draws while the hub is open
        _supporterStudio.DrawUnlockWindow();

        // Embedded admin needs a fixed container: it owns its inner rail
        // and scroll regions; an outer scroll area would fight its layout.
        // Same for embedded analysis: its pages own the roster scroll area
        // and fill-to-bottom ScrollY tables · an outer page scroll would
        // slide frozen table headers away and let the page fade cover them.
        var ownsScrolling = _activePage == SpheneHubPage.DataAnalysis;
        // The community detail page draws edge-to-edge chrome (hero banner)  · 
        // the uniform page padding would leave a bare strip along its right
        // edge, so that page opts out and indents itself.
        var communityEdge = _activePage == SpheneHubPage.Syncshells
            && _selectedGroupGid != null
            && _syncshellDetailTab == SyncshellDetailTab.Community;
        // Page-switch transition · a surface veil fades out over the content
        // child so pages materialize in place instead of snapping in. No
        // positional shift: edge-to-edge content (profile banner) would show
        // a gap while travelling.
        var switchEase = 1f;
        if (_pageSwitchAt >= 0)
        {
            var e = Math.Clamp((float)(ImGui.GetTime() - _pageSwitchAt) / 0.22f, 0f, 1f);
            switchEase = 1f - MathF.Pow(1f - e, 3f);
            if (e >= 1f)
            {
                _pageSwitchAt = -1;
            }
        }
        var contentMin = Vector2.Zero;
        var contentMax = Vector2.Zero;
        if (SpheneUI.Layout.BeginNavContent("sphenehub",
                scrollable: !ownsScrolling, padded: !communityEdge,
                contentInset: communityEdge ? 12f * SpheneUI.Scale : -1f,
                fixedContent: o => PageFixedChrome(o, accentMe, accentCommunity)))
        {
            contentMin = ImGui.GetWindowPos();
            contentMax = contentMin + ImGui.GetWindowSize();
            _contentScrollY = ImGui.GetScrollY();
            // One scroll child serves every page · a stale offset would shift
            // fresh content down. Snap to the top on each location change so
            // pages (and embedded tab switches, folded into the location's
            // SubPage) start at their header. Back/forward entries carry the
            // scroll offset they were captured at - restoring it lands the
            // user where they left, clamped to the restored content height.
            var drawn = CurrentLocation;
            if (_lastDrawnLocation != drawn)
            {
                var scroll = _pendingScrollRestore ?? 0f;
                if (_pendingScrollRestore == null)
                {
                    if (_lastDrawnLocation?.ChroniclePostId == null
                        && drawn.ChroniclePostId != null)
                    {
                        // Reader opens in place - stash the list offset so
                        // closing it lands back at the same position.
                        _preReaderScrollY = _contentScrollY;
                    }
                    else if (_lastDrawnLocation?.ChroniclePostId != null
                        && drawn.ChroniclePostId == null)
                    {
                        scroll = _preReaderScrollY;
                    }
                }
                _lastDrawnLocation = drawn;
                _pendingScrollRestore = null;
                ImGui.SetScrollY(scroll);
            }
            switch (_activePage)
            {
                case SpheneHubPage.Home:
                    DrawHomePage(accentMe);
                    break;
                case SpheneHubPage.News:
                    DrawNewsPage(accentMe);
                    break;
                case SpheneHubPage.Community:
                    DrawCommunityPage(accentCommunity);
                    break;
                case SpheneHubPage.PublicProfile:
                    DrawPublicProfilePage(accentCommunity);
                    break;
                case SpheneHubPage.Profile:
                    DrawProfilePage(accentMe);
                    break;
                case SpheneHubPage.ProfileStyle:
                    DrawStylePage(accentMe);
                    break;
                case SpheneHubPage.Friends:
                    DrawFriendsPage(accentCommunity);
                    break;
                case SpheneHubPage.Syncshells:
                    DrawSyncshellsPage();
                    break;
                case SpheneHubPage.Housing:
                    _housingPageView.DrawContent();
                    break;
                case SpheneHubPage.CharacterData:
                    _charaDataVisible = true;
                    _charaDataHubUi.DrawEmbeddedContent();
                    break;
                case SpheneHubPage.DataAnalysis:
                    _dataAnalysisUi.DrawEmbeddedContent();
                    break;
                case SpheneHubPage.ModSharing:
                    _modSharingUi.DrawEmbeddedContent();
                    break;
                case SpheneHubPage.Settings:
                    _settingsUi.DrawEmbeddedContent();
                    break;
                case SpheneHubPage.Supporter:
                    _settingsUi.DrawEmbeddedSupporterContent();
                    break;
            }
        }
        SpheneUI.Layout.EndNavContent();
        // The community page's pinned header · an input-taking child parked
        // over the nav content's pinned zone, so it must run after the
        // scroll child ends. Its absolute positioning moves the flow cursor.
        if (_communityOverlay != null)
        {
            var resumeCursor = ImGui.GetCursorScreenPos();
            _communityOverlay();
            ImGui.SetCursorScreenPos(resumeCursor);
            _communityOverlay = null;
        }
        // Fade veil over the content child · painted after it on the parent's
        // shared draw list, so the new page dissolves in underneath.
        if (switchEase < 1f && contentMax.X > contentMin.X)
        {
            var windowPos = ImGui.GetWindowPos();
            var windowMax = windowPos + ImGui.GetWindowSize();
            var veil = SpheneUI.Chrome.WindowSurfaceColorAt(windowPos, windowMax, contentMin.Y);
            ImGui.GetWindowDrawList().AddRectFilled(contentMin, contentMax,
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(veil, veil.W * (1f - switchEase))));
        }
    }

    public override void OnClose()
    {
        base.OnClose();
        if (_suppressOpenStatePersist)
        {
            _suppressOpenStatePersist = false;
        }
        else
        {
            PersistOpenState(false);
        }
        ResetTransientState();
    }

    private HubLocation CurrentLocation => BuildCurrentLocation();

    /// <summary>Snapshots the live surface into a location for the history
    /// stacks - page + embedded sub-state + the open chronicle reader and a
    /// pending comment focus, so back/forward lands where the user was, not
    /// just on the right page.</summary>
    private HubLocation BuildCurrentLocation()
    {
        // The open chronicle reader rides the location on every
        // reader-hosting page - own profile, friend profile, public
        // profile - matched by owner, so a back press re-opens the same
        // entry instead of the bare list.
        var chronId = _openChronicle is { OwnerUid: var openOwner, Id: var openId }
            && ChronicleOwnerOnPage(openOwner)
            ? openId : (long?)null;
        // FocusCommentId stays unset - the pending focus lives in the
        // thread state keyed by target and survives navigation on its own.
        // Capturing it here would flip the location identity when the
        // focus is consumed and yank the scroll back to the top.
        return new HubLocation(_activePage, _selectedGroupGid, _viewingFriend,
            _activePage switch
            {
                SpheneHubPage.Settings => (int)_settingsUi.ActiveSettingsPageValue,
                SpheneHubPage.Supporter => (int)_settingsUi.ActiveSettingsPageValue,
                SpheneHubPage.ModSharing => (int)_modSharingUi.ActiveSubPageValue,
                SpheneHubPage.CharacterData => (int)_charaDataHubUi.ActiveSubPageValue,
                SpheneHubPage.DataAnalysis => (int)_dataAnalysisUi.ActiveSubPageValue,
                SpheneHubPage.Syncshells => _selectedGroupGid != null
                    ? (int)_syncshellDetailTab : (int)_syncshellSubPage,
                SpheneHubPage.Community => _communityTab,
                // Profile-tab surfaces carry their tab in SubPage so
                // back/forward lands on the same tab - without it the
                // friend-change path resets to the Profile tab every time.
                SpheneHubPage.Friends => _friendPageTab,
                SpheneHubPage.Profile => _profilePageTab,
                _ => null
            },
            _activePage == SpheneHubPage.Housing ? _housingPageView.SelectedKey : null,
            null,
            _activePage == SpheneHubPage.News ? _viewingAnnouncementId : null,
            _activePage == SpheneHubPage.News && _announcementEditMode,
            _activePage == SpheneHubPage.Syncshells && _selectedGroupGid != null
                ? _communityPage.Tab : null,
            _activePage == SpheneHubPage.Syncshells && _selectedGroupGid != null
                ? _communityPage.EventId ?? _communityPage.FocusItemId : null,
            chronId,
            null,
            _activePage == SpheneHubPage.PublicProfile ? _publicUid : null);
    }

    /// <summary>Whether the open chronicle's owner is this page's host -
    /// own uid on Profile, the viewed pair on Friends, the public uid on
    /// PublicProfile. Readers elsewhere never claim the slot.</summary>
    private bool ChronicleOwnerOnPage(string ownerUid) => _activePage switch
    {
        SpheneHubPage.Profile => string.Equals(ownerUid, _apiController.UID,
            StringComparison.Ordinal),
        SpheneHubPage.Friends => string.Equals(ownerUid,
            _viewingFriend?.UserData.UID, StringComparison.Ordinal),
        SpheneHubPage.PublicProfile => string.Equals(ownerUid, _publicUid,
            StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Folds a settings-page switch into the hub history · only for changes that happened on
    /// the embedded surface while the hub shows the settings page, so the standalone Settings window
    /// never hijacks hub navigation. The page is already applied when the event fires, so the previous
    /// location is pushed directly.</summary>
    private void OnSettingsPageChanged(SettingsUi.SettingsPage previous, SettingsUi.SettingsPage next, bool embedded)
    {
        if (!embedded || _activePage is not (SpheneHubPage.Settings or SpheneHubPage.Supporter)) return;
        _navBack.Add((new HubLocation(_activePage, null, null, (int)previous),
            _contentScrollY));
        _navForward.Clear();
    }

    /// <summary>Same fold as <see cref="OnSettingsPageChanged"/> for the mod sharing section.</summary>
    private void OnModSharingPageChanged(ModSharingUi.ModSharingPage previous,
        ModSharingUi.ModSharingPage next, bool embedded)
    {
        if (!embedded || _activePage != SpheneHubPage.ModSharing) return;
        _navBack.Add((new HubLocation(SpheneHubPage.ModSharing, null, null,
            (int)previous), _contentScrollY));
        _navForward.Clear();
    }

    /// <summary>Same fold as <see cref="OnSettingsPageChanged"/> for the character data section.</summary>
    private void OnCharaDataPageChanged(CharaDataHubUi.CharaDataPage previous,
        CharaDataHubUi.CharaDataPage next, bool embedded)
    {
        if (!embedded || _activePage != SpheneHubPage.CharacterData) return;
        _navBack.Add((new HubLocation(SpheneHubPage.CharacterData, null, null,
            (int)previous), _contentScrollY));
        _navForward.Clear();
    }

    /// <summary>Same fold as <see cref="OnSettingsPageChanged"/> for the data analysis section.</summary>
    private void OnDataAnalysisPageChanged(DataAnalysisUi.DataAnalysisPage previous,
        DataAnalysisUi.DataAnalysisPage next, bool embedded)
    {
        if (!embedded || _activePage != SpheneHubPage.DataAnalysis) return;
        _navBack.Add((new HubLocation(SpheneHubPage.DataAnalysis, null, null,
            (int)previous), _contentScrollY));
        _navForward.Clear();
    }

    private void SetPage(SpheneHubPage page)
        => NavigateTo(new HubLocation(page, null, null));

    /// <summary>Tears down every embedded image viewer - explicit user
    /// navigation always closes them, even a reselect of the current page.
    /// Ordinary frame rendering and background refreshes never call this.</summary>
    private void CloseEmbeddedViewers()
    {
        _communityPage.CloseViewer();
        _profileViewerImages.Clear();
        _profileViewerIndex = 0;
        _profileViewerKey = null;
        _profileViewerOpenT = 0f;
        _pendingViewerRemoveId = null;
    }

    /// <summary>User-initiated navigation: pushes the current location onto the back stack.</summary>
    private void NavigateTo(HubLocation target)
    {
        CloseEmbeddedViewers();
        var current = CurrentLocation;
        if (current.Equals(target)) return;
        _navBack.Add((current, _contentScrollY));
        _navForward.Clear();
        _pendingScrollRestore = null;
        ApplyLocation(target);
    }

    private void NavigateBack()
    {
        if (_navBack.Count == 0) return;
        _navForward.Add((CurrentLocation, _contentScrollY));
        var (target, scrollY) = _navBack[^1];
        _navBack.RemoveAt(_navBack.Count - 1);
        CloseEmbeddedViewers();
        _pendingScrollRestore = scrollY;
        ApplyLocation(target);
    }

    private void NavigateForward()
    {
        if (_navForward.Count == 0) return;
        _navBack.Add((CurrentLocation, _contentScrollY));
        var (target, scrollY) = _navForward[^1];
        _navForward.RemoveAt(_navForward.Count - 1);
        CloseEmbeddedViewers();
        _pendingScrollRestore = scrollY;
        ApplyLocation(target);
    }

    /// <summary>Applies a location · leaving-cleanup of the previous page, then the target state.</summary>
    private void ApplyLocation(HubLocation target)
    {
        if (_charaDataVisible)
        {
            _charaDataVisible = false;
            _charaDataHubUi.OnEmbeddedHidden();
        }
        if (_activePage == SpheneHubPage.Syncshells
            && (target.Page != SpheneHubPage.Syncshells
                || !string.Equals(_selectedGroupGid, target.GroupGid, StringComparison.Ordinal)))
        {
            DisposeEmbeddedAdminPanels();
            _syncshellDetailTab = SyncshellDetailTab.Community;
            _communityCacheGid = null;
            _communityPage.Tab = null;
            _communityPage.AlbumId = null;
            _communityPage.EventId = null;
            _communityPage.FocusItemId = null;
            _communityPage.ViewerImages = null;
        }
        // Community deep-links: a location carrying a section lands on the
        // exact tab + drill-in (event detail or focused list item). Plain
        // shell navigations carry neither and leave the surface untouched.
        // Same-shell jumps reuse the page state - stale drill-ins (an open
        // album, a manage subpage, a leftover viewer/focus) would otherwise
        // intercept the target and strand the deep-link.
        if (target.Page == SpheneHubPage.Syncshells && target.CommunitySection.HasValue)
        {
            _communityPage.Tab = target.CommunitySection.Value;
            _communityPage.EventId = target.CommunitySection == SyncshellCommunitySection.Events
                ? target.CommunityItemId : null;
            _communityPage.FocusItemId = target.CommunitySection != SyncshellCommunitySection.Events
                ? target.CommunityItemId : null;
            _communityPage.AlbumId = null;
            _communityPage.ViewerImages = null;
            _communityPage.ManagingAlbums = false;
            _communityPage.AlbumDrafts.Clear();
            _communityPage.PickingAlbumPhotos = false;
            _communityPage.Subpage = SyncshellCommunityView.CommunitySubpage.None;
            _communityPage.SwitchT = 0f;
            // Comment deep-link: arm the shared thread view - it walks the
            // thread to the comment (auto-paginating) once it renders.
            if (target.FocusCommentId is { } focusCommentId
                && target.CommunityItemId is { } commentHostId)
            {
                var commentKind = target.CommunitySection == SyncshellCommunitySection.Events
                    ? CommentTargetKind.Event
                    : target.CommunitySection == SyncshellCommunitySection.News
                        ? CommentTargetKind.News
                        : (CommentTargetKind?)null;
                if (commentKind != null)
                {
                    _communityPage.Comments.FocusComment(
                        commentKind.Value, commentHostId, focusCommentId);
                }
            }
        }
        // Chronicle deep-links: the reader opens once the post list resolves
        // the id; the comment focus waits in the thread view until the
        // discussion loads it (auto-paginating roots/replies meanwhile).
        // Public-profile state: uid swap clears the per-uid texture cache and
        // drops an open reader that belongs to the previous profile; leaving
        // the page entirely clears the surface.
        var nextPublicUid = target.Page == SpheneHubPage.PublicProfile
            ? target.PublicUid : null;
        if (!string.Equals(nextPublicUid, _publicUid, StringComparison.Ordinal))
        {
            if (_openChronicle is { } oc
                && !string.Equals(oc.OwnerUid, nextPublicUid, StringComparison.Ordinal))
            {
                _openChronicle = null;
            }
            _publicAvatarBytes = [];
            _publicBannerBytes = [];
            _publicAvatarB64 = null;
            _publicBannerB64 = null;
        }
        _publicUid = nextPublicUid;
        if (target.ChroniclePostId is { } chronId)
        {
            var ownerUid = target.Page == SpheneHubPage.PublicProfile
                ? target.PublicUid
                : target.Page == SpheneHubPage.Friends
                ? target.Friend?.UserData.UID
                : _apiController.UID;
            if (ownerUid != null)
            {
                _openChronicle = (ownerUid, chronId);
                _chronicleNotice = null;
                if (target.FocusCommentId is { } focusId)
                {
                    _blogComments.FocusComment(CommentTargetKind.Blog, chronId, focusId);
                }
            }
        }
        if (IsProfileFamily(_activePage) && !IsProfileFamily(target.Page))
        {
            _profileEditMode = false;
            _heroEdit.AddingTag = false;
            _profileViewerImages.Clear();
            _profileViewerIndex = 0;
            _profileViewerKey = null;
            _profileViewerOpenT = 0f;
            _photoEditOpen = false;
            _photoEditImageId = 0;
            _photoDeleteArmed = false;
            _pendingViewerRemoveId = null;
        }
        var nextFriend = target.Page == SpheneHubPage.Friends ? target.Friend : null;
        if (!ReferenceEquals(nextFriend, _viewingFriend))
        {
            _viewingFriend = nextFriend;
            _friendAvatarBytes = [];
            _friendBannerBytes = [];
        }
        if (_activePage != target.Page)
        {
            _pageSwitchAt = ImGui.GetTime();
        }
        _activePage = target.Page;
        _selectedGroupGid = target.Page == SpheneHubPage.Syncshells ? target.GroupGid : null;
        _viewingAnnouncementId = target.Page == SpheneHubPage.News
            ? target.AnnouncementId : null;
        _announcementEditMode = target.Page == SpheneHubPage.News
            && target.AnnouncementEdit;
        _housingPageView.SelectProperty(
            target.Page == SpheneHubPage.Housing ? target.HousingKey : null);
        if (!string.IsNullOrWhiteSpace(target.ReleaseVersion))
        {
            _settingsUi.SelectReleaseNote(target.ReleaseVersion);
        }
        if (!IsSectionPage(target.Page)
            && !(target.Page == SpheneHubPage.Syncshells && target.GroupGid != null))
        {
            _lastHubLocation = target;
        }
        // A fresh section entry lands on the surface's entry page · embedded
        // sections otherwise resurrect their last tab, which reads as stale
        // state on a new visit. History restores and deep links carry a
        // SubPage and bypass this.
        if (target.SubPage == null)
        {
            switch (target.Page)
            {
                case SpheneHubPage.Settings:
                    _settingsUi.ResetEmbeddedNavigation(SettingsUi.SettingsPage.Home);
                    break;
                case SpheneHubPage.Supporter:
                    _settingsUi.ResetEmbeddedNavigation(SettingsUi.SettingsPage.Supporter);
                    break;
                case SpheneHubPage.ModSharing:
                    _modSharingUi.ResetEmbeddedNavigation();
                    break;
                case SpheneHubPage.CharacterData:
                    _charaDataHubUi.ResetEmbeddedNavigation();
                    break;
                case SpheneHubPage.DataAnalysis:
                    _dataAnalysisUi.ResetEmbeddedNavigation();
                    break;
                case SpheneHubPage.Syncshells:
                    // Detail navs carry the gid; plain syncshell entries land
                    // on the list · a stale join/create form must not resurrect.
                    _syncshellSubPage = SyncshellHubSubPage.List;
                    break;
                case SpheneHubPage.Community:
                    _communityTab = 0;
                    break;
            }
        }
        if (target.SubPage is { } subPage)
        {
            switch (target.Page)
            {
                case SpheneHubPage.Settings:
                case SpheneHubPage.Supporter:
                    _settingsUi.ApplySettingsPageQuiet((SettingsUi.SettingsPage)subPage);
                    break;
                case SpheneHubPage.ModSharing:
                    _modSharingUi.ApplySubPageQuiet((ModSharingUi.ModSharingPage)subPage);
                    break;
                case SpheneHubPage.CharacterData:
                    _charaDataHubUi.ApplySubPageQuiet((CharaDataHubUi.CharaDataPage)subPage);
                    break;
                case SpheneHubPage.DataAnalysis:
                    _dataAnalysisUi.ApplySubPageQuiet((DataAnalysisUi.DataAnalysisPage)subPage);
                    break;
                case SpheneHubPage.Syncshells:
                    if (target.GroupGid != null)
                    {
                        _syncshellDetailTab = (SyncshellDetailTab)subPage;
                    }
                    else
                    {
                        _syncshellSubPage = (SyncshellHubSubPage)subPage;
                        if (_syncshellSubPage == SyncshellHubSubPage.Join)
                        {
                            _joinSyncshellUi.Reset();
                        }
                        else if (_syncshellSubPage == SyncshellHubSubPage.Create)
                        {
                            _createSyncshellUi.Reset();
                        }
                    }
                    break;
                case SpheneHubPage.Community:
                    _communityTab = Math.Clamp(subPage, 0, 7);
                    break;
                case SpheneHubPage.Friends:
                    _pendingFriendTab = Math.Clamp(subPage, 0, 4);
                    break;
                case SpheneHubPage.Profile:
                    _profilePageTab = Math.Clamp(subPage, 0, 2);
                    break;
            }
        }
    }

    private static bool IsSectionPage(SpheneHubPage page)
        => page is SpheneHubPage.Settings or SpheneHubPage.ModSharing
            or SpheneHubPage.CharacterData or SpheneHubPage.DataAnalysis;

    private static bool IsProfileFamily(SpheneHubPage page)
        => page is SpheneHubPage.Profile or SpheneHubPage.ProfileStyle;

    private void ResetTransientState()
    {
        if (_charaDataVisible)
        {
            _charaDataVisible = false;
            _charaDataHubUi.OnEmbeddedHidden();
        }
        _selectedGroupGid = null;
        _syncshellSubPage = SyncshellHubSubPage.List;
        _communityTab = 0;
        _communityCacheGid = null;
        _communityMap.Clear();
        _communityLoadingMap.Clear();
        _communityFailedAt.Clear();
        _communityNoPage.Clear();
        _communityRefreshScheduled.Clear();
        _shellMediaCache.Clear();
        _shellMediaLoading.Clear();
        _profileViewerImages.Clear();
        _profileViewerKey = null;
        _lastDrawnLocation = null;
        _areaBoundShells = null;
        _areaBoundLoading = false;
        // New generation: in-flight directory/preview completions land as
        // stale and the freed flags let this profile request immediately.
        _discover.Reset();
        _profileEditMode = false;
        _styleSeeded = false;
        _navBack.Clear();
        NotificationsOptionBlock.ResetCommunityMuteCache();
        _navForward.Clear();
        _viewingAnnouncementId = null;
        _announcementEditMode = false;
        _editorSeededId = long.MinValue;
        _editorBusy = false;
        _editorError = null;
        _sectionRailAnim = 0f;
        _heroEdit.AddingTag = false;
        ClearViewingFriend();
        DisposeEmbeddedAdminPanels();
        SpheneUI.Fx.Reset();
        SpheneUI.Popups.ResetDropdowns();
        SpheneUI.Layout.ResetExpanders();
    }

    private void ClearViewingFriend()
    {
        _viewingFriend = null;
        _friendAvatarBytes = [];
        _friendBannerBytes = [];
    }

    protected override void Dispose(bool disposing)
    {
        _gifPicker.Dispose();
        _stickerPicker.Dispose();
        _settingsUi.SettingsPageChanged -= OnSettingsPageChanged;
        _modSharingUi.SubPageChanged -= OnModSharingPageChanged;
        _charaDataHubUi.SubPageChanged -= OnCharaDataPageChanged;
        _dataAnalysisUi.SubPageChanged -= OnDataAnalysisPageChanged;
        base.Dispose(disposing);
    }

    private void DrawHubHeaderBand(Vector2 bandMin, float bandHeight, float railRightX, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var bandMax = new Vector2(windowPos.X + windowSize.X, bandMin.Y + bandHeight);
        var railBg = SpheneUI.Layout.NavRailBandColor();
        var railBgU32 = SpheneUI.ColorUtils.ToU32(railBg);

        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));

        ImGui.PushClipRect(windowPos, windowPos + windowSize, false);
        var windowRounding = ImGui.GetStyle().WindowRounding;
        drawList.AddRectFilled(bandMin, bandMax, railBgU32, windowRounding,
            ImDrawFlags.RoundCornersTop);
        DrawHeaderBackdrop(bandMin, bandMax, railRightX, railBg, profile);
        // Accent seam along the bottom edge of the band.
        drawList.AddRectFilledMultiColor(
            new Vector2(bandMin.X, bandMax.Y - 1f), bandMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.05f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.05f)));
        ImGui.PopClipRect();

        // toolbar row: the profile card claims the band's left (rail-colored)
        // segment · the sidebar owns that part of the header · and the nav
        // cluster sits right behind it at the content edge. Bell/burger
        // mirror it on the right · one row, one height.
        var btnSize = 34f * scale;
        var btnY = bandMin.Y + (bandHeight - btnSize) * 0.5f;
        var bellX = bandMax.X - 8f * scale - btnSize * 4f - 8f * scale * 3f;
        DrawHeaderProfileCard(bandMin, bandHeight, railRightX, profile, accent);
        DrawHeaderNavCluster(new Vector2(railRightX + 12f * scale, btnY),
            btnSize, bellX);
        DrawHeaderActionsZone(bandMin, bandHeight, bandMax);
    }

    /// <summary>
    /// The banner backdrop right of the rail edge inside the header band  · 
    /// shared by the open band and the collapsed strip. Drawn flush to the
    /// window top; the fade keeps the left transition seamless, and corner
    /// spill is masked back to the window rounding so the frame edge stays
    /// clean.
    /// </summary>
    private void DrawHeaderBackdrop(Vector2 bandMin, Vector2 bandMax, float railRightX,
        Vector4 railBg, SpheneProfileData profile)
    {
        var backdrop = GetBannerTexture(profile) ?? GetAvatarTexture(profile);
        if (backdrop == null)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var windowMax = ImGui.GetWindowPos() + ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        var borderSize = MathF.Max(style.WindowBorderSize, 0f);
        // The band's window-side edges belong to the border ring · the image
        // nests inside them and rounds only the corners sitting on the window
        // silhouette, so the configured window arc survives the artwork. The
        // bottom-right arc joins in only once the band reaches the window
        // bottom (the collapsed strip); mid-fold it is an interior edge.
        var reachesBottom = bandMax.Y >= windowMax.Y - 1f;
        var imgMin = new Vector2(railRightX, bandMin.Y + borderSize);
        var imgMax = new Vector2(bandMax.X - borderSize,
            reachesBottom ? bandMax.Y - borderSize : bandMax.Y);
        var imgCorners = ImDrawFlags.RoundCornersTopRight;
        if (reachesBottom)
        {
            imgCorners |= ImDrawFlags.RoundCornersBottomRight;
        }
        var imgRounding = MathF.Max(0f, style.WindowRounding - borderSize);
        var railBgU32 = SpheneUI.ColorUtils.ToU32(railBg);

        SpheneUI.Media.ImageInRect(drawList, backdrop, imgMin, imgMax,
            ImageFit.Cover, new Vector4(1f, 1f, 1f, 0.85f),
            rounding: imgRounding, corners: imgCorners);
        // The blend overlay is a square fill · it must stop short of the
        // rounded corner column or it would repaint the notch the image
        // just gave back.
        var overlayMax = new Vector2(imgMax.X - imgRounding,
            reachesBottom ? imgMax.Y - imgRounding : imgMax.Y);
        drawList.AddRectFilledMultiColor(imgMin, overlayMax,
            railBgU32,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(railBg, 0.5f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(railBg, 0.5f)),
            railBgU32);

        // The blend must stop short of the corner column or it would repaint
        // the notch the image just gave back · so the remaining sliver gets
        // the gradient's edge tone in the same arc instead of the raw
        // artwork. Bottom row joins in only once the band sits on the
        // window's bottom edge (the collapsed strip).
        if (imgRounding > 0f)
        {
            var edgeColor = SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(railBg, 0.5f));
            if (reachesBottom)
            {
                drawList.AddRectFilledMultiColor(
                    new Vector2(imgMin.X, overlayMax.Y),
                    new Vector2(overlayMax.X, imgMax.Y),
                    railBgU32, edgeColor, edgeColor, railBgU32);
            }
            drawList.AddRectFilled(new Vector2(overlayMax.X, imgMin.Y), imgMax,
                edgeColor, imgRounding, imgCorners);
        }
    }

    /// <summary>
    /// The user identity card inside the band's rail-colored left segment
    /// (Discord-style, but top-docked): circular avatar with presence badge
    /// plus name/status, hugging the rail width. Clicking opens the profile
    /// page. Lives in the parent window · no child covers the band area.
    /// </summary>
    private bool DrawHeaderProfileCard(Vector2 bandMin, float bandHeight, float railRightX,
        SpheneProfileData profile, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var cardMin = new Vector2(bandMin.X + 4f * scale, bandMin.Y + 4f * scale);
        var cardMax = new Vector2(railRightX - 4f * scale, bandMin.Y + bandHeight - 4f * scale);
        var cardW = cardMax.X - cardMin.X;
        var cardH = cardMax.Y - cardMin.Y;
        // Inset by 4px, the card arc must run concentric with the window's
        // corner · same center, radius minus the inset.
        var cardRadius = MathF.Max(0f, ImGui.GetStyle().WindowRounding - 4f * scale);

        ImGui.SetCursorScreenPos(cardMin);
        var clicked = ImGui.InvisibleButton("##hub-header-profile", new Vector2(cardW, cardH));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (clicked)
        {
            SetPage(SpheneHubPage.Profile);
        }

        var connected = _apiController.IsConnected;
        var paused = _apiController.IsConnectionPaused;
        var connColor = !connected ? SpheneUI.Palette.NetworkDisconnected
            : paused ? SpheneUI.Palette.NetworkWarning
            : SpheneUI.Palette.NetworkActive;
        var connText = !connected ? "Offline" : paused ? "Paused" : "Online";

        var statusLine = SpheneUI.Profile.StatusLineText(profile, out var statusColor);
        SpheneUI.Profile.IdentityCard(drawList, cardMin, cardMax, 6f * scale,
            new SpheneUI.Profile.IdentityContent(
                GetAvatarTexture(profile),
                SpheneUI.Profile.OwnName(_apiController.OwnDisplayName, _apiController.OwnAlias),
                SpheneUI.Colors.TextPrimary, statusLine, statusColor,
                connColor, connText, _apiController.OwnAlias, accent),
            hovered, cardRadius, ImDrawFlags.RoundCornersAll,
            " · open your profile page");

        return clicked;
    }

    private void DrawHeaderActionsZone(Vector2 bandMin, float bandHeight, Vector2 bandMax)
    {
        var scale = SpheneUI.Scale;
        var btnSize = 34f * scale;
        var gap = 8f * scale;
        // Window controls live on the far right · fold, then close, matching
        // the order the chrome title bar used.
        var closeX = bandMax.X - gap - btnSize;
        var foldX = closeX - gap - btnSize;
        var burgerX = foldX - gap - btnSize;
        var bellX = burgerX - gap - btnSize;
        var spheneX = bellX - gap - btnSize;
        var kofiX = spheneX - gap - btnSize;
        var discordX = kofiX - gap - btnSize;
        var btnY = bandMin.Y + (bandHeight - btnSize) * 0.5f;

        // brand icons + window controls share one dark translucent container -
        // keeps everything readable over any banner backdrop. The pill hugs
        // the window's right edge with a small, even margin on both sides.
        var headerDl = ImGui.GetWindowDrawList();
        var trioMin = new Vector2(discordX - gap * 0.5f, btnY - gap * 0.4f);
        var trioMax = new Vector2(closeX + btnSize + gap * 0.5f,
            btnY + btnSize + gap * 0.4f);
        headerDl.AddRectFilled(trioMin, trioMax,
            SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.45f)), 8f * scale);
        headerDl.AddRect(trioMin, trioMax,
            SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.12f)), 8f * scale);

        ImGui.SetCursorScreenPos(new Vector2(discordX, btnY));
        if (SpheneBrandIcons.IconButton(SpheneBrandIcons.Brand.Discord, "hub-discord",
                btnSize, tooltip: "Open the Sphene Discord community"))
        {
            Util.OpenLink("https://discord.gg/GbnwsP2XsF");
        }
        ImGui.SetCursorScreenPos(new Vector2(kofiX, btnY));
        if (SpheneBrandIcons.IconButton(SpheneBrandIcons.Brand.Kofi, "hub-kofi",
                btnSize, tooltip: "Ko-fi: support · perks: see what it unlocks"))
        {
            _supporterStudio.OpenUnlockWindow();
        }
        ImGui.SetCursorScreenPos(new Vector2(spheneX, btnY));
        if (SpheneBrandIcons.IconButton(SpheneBrandIcons.Brand.Sphene, "hub-sphene",
                btnSize, tint: Vector4.One, tooltip: "Open sphene.online"))
        {
            Util.OpenLink("https://sphene.online");
        }

        var pending = NotificationInbox.PendingCount(_notificationInbox, _pairManager);
        ImGui.SetCursorScreenPos(new Vector2(bellX, btnY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Bell, btnSize, null, null, btnSize))
        {
            SpheneUI.Popups.ToggleFlyout("hub-inbox");
        }
        SpheneUI.AttachTooltip(pending > 0
            ? $"{pending} pending item(s) - open inbox"
            : "Notifications");
        SpheneUI.Chrome.DrawTitleBarButtonBadge(ImGui.GetItemRectMin(), btnSize, pending, scale);

        // Notification inbox flyout · anchored to the bell's right edge,
        // expanding leftward. Flyout (not Dropdown) - the header band has no
        // popup stack, and dropdown windows are Popup-flagged: ImGui::Begin
        // dereferences a garbage PopupId without OpenPopup (native crash).
        if (SpheneUI.Popups.BeginFlyout("hub-inbox"))
        {
            var inboxH = Math.Clamp(pending * 58f * scale
                + SpheneUI.Spacing.X12.Scaled, 0f, 360f * scale);
            // 0-height scroll area = "fill remaining" → flyout grows forever
            if (pending == 0)
            {
                ImGui.Dummy(new Vector2(310f * scale, 0f));
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.BellSlash,
                    "All caught up",
                    "Pair requests, transfers and updates land here.");
            }
            else if (SpheneUI.Layout.BeginScrollArea("hub-inbox-scroll",
                    new Vector2(310f * scale, inboxH)))
            {
                NotificationInbox.Draw(_notificationInbox, _pairManager,
                    _apiController, Mediator);
                SpheneUI.Layout.EndScrollArea();
            }
            SpheneUI.Popups.EndFlyout();
        }

        ImGui.SetCursorScreenPos(new Vector2(burgerX, btnY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Bars, btnSize, null, null, btnSize))
        {
            ImGui.SetNextWindowPos(new Vector2(burgerX + btnSize, btnY + btnSize + 2f * scale),
                ImGuiCond.Always, new Vector2(1f, 0f));
            ImGui.OpenPopup("##hub-header-menu");
        }
        SpheneUI.AttachTooltip("Menu");

        if (SpheneUI.Popups.BeginStyled("##hub-header-menu"))
        {
            if (ImGui.MenuItem(pending > 0
                    ? $"Pair Requests ({pending})###hub-menu-requests"
                    : "Pair Requests###hub-menu-requests"))
            {
                Mediator.Publish(new OpenPairRequestsUiMessage());
            }
            if (ImGui.MenuItem("Edit Profile###hub-menu-profile"))
            {
                SetPage(SpheneHubPage.Profile);
                _profileEditMode = true;
                SeedHeroEditBuffers(_spheneProfileManager.GetSpheneProfile(
                    new UserData(_apiController.UID)));
            }
            ImGui.Separator();
            var reopenOnStart = _configService.Current.ReopenSpheneHubOnStartup;
            if (ImGui.MenuItem("Reopen Hub on startup###hub-menu-reopen", string.Empty, reopenOnStart))
            {
                _configService.Current.ReopenSpheneHubOnStartup = !reopenOnStart;
                _configService.Save();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Settings###hub-menu-settings"))
            {
                SetPage(SpheneHubPage.Settings);
            }
            if (ImGui.MenuItem("Pop out Settings###hub-menu-settings-popout"))
            {
                Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
            }
            SpheneUI.Popups.EndStyled();
        }

        var folded = SpheneUI.Chrome.FoldCollapsed(WindowName);
        ImGui.SetCursorScreenPos(new Vector2(foldX, btnY));
        if (SpheneUI.Icons.Button(
                folded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                btnSize, null, null, btnSize))
        {
            SpheneUI.Chrome.ToggleFold(WindowName);
        }
        SpheneUI.AttachTooltip(folded ? "Expand" : "Collapse");

        if (ShowCloseButton)
        {
            ImGui.SetCursorScreenPos(new Vector2(closeX, btnY));
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Times, btnSize, null, null, btnSize))
            {
                IsOpen = false;
            }
            SpheneUI.AttachTooltip("Close");
        }
    }

    /// <summary>
    /// The folded strip: just the status card plus fold and close · the rest
    /// of the hub is hidden while the window is collapsed.
    /// </summary>
    protected override void DrawCollapsedContent()
    {
        var scale = SpheneUI.Scale;
        var theme = SpheneTheme.Current;
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var accent = profile.ProfileTheme.Value.AccentColor(theme.AccentCyan);
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var bandH = CollapsedStripHeight;
        var drawList = ImGui.GetWindowDrawList();

        // The window draw list clips to the content region · lift it to the
        // full window rect or the strip stops WindowPadding short of the
        // edges and the themed surface/blur peeks through the slivers.
        ImGui.PushClipRect(windowPos, windowPos + windowSize, false);
        // The strip is the whole window · the fill must reach the window
        // bottom or the border-wide sliver below it shows the surface and
        // the bottom arcs float above the frame edge.
        var bandMax = windowPos + windowSize;
        var railBg = SpheneUI.Layout.NavRailBandColor();
        drawList.AddRectFilled(windowPos, bandMax,
            SpheneUI.ColorUtils.ToU32(railBg),
            ImGui.GetStyle().WindowRounding, ImDrawFlags.RoundCornersAll);

        var railW = CurrentRailWidth;
        var cardRightX = windowPos.X + ImGui.GetStyle().WindowPadding.X + railW;
        // Same backdrop language as the open band · the banner folds along
        // with the strip, cropped to the shrunken width.
        DrawHeaderBackdrop(windowPos, bandMax, cardRightX, railBg, profile);
        // The seam sits on the window's bottom edge · both ends pull back
        // by the frame radius so they cannot jut past the corner arcs.
        var seamInset = ImGui.GetStyle().WindowRounding;
        drawList.AddRectFilledMultiColor(
            new Vector2(windowPos.X + seamInset, bandMax.Y - 1f),
            new Vector2(bandMax.X - seamInset, bandMax.Y),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.05f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.05f)));
        if (DrawHeaderProfileCard(windowPos, bandH, cardRightX, profile, accent))
        {
            SpheneUI.Chrome.ToggleFold(WindowName);
        }

        var btnSize = 34f * scale;
        var btnY = windowPos.Y + (bandH - btnSize) * 0.5f;
        var gap = 8f * scale;
        var closeX = bandMax.X - gap - btnSize;
        var foldX = closeX - gap - btnSize;
        var folded = SpheneUI.Chrome.FoldCollapsed(WindowName);

        // same pill the open band's actions zone uses · keeps the controls
        // readable over the backdrop, hugging the window's right edge
        var plateMin = new Vector2(foldX - gap * 0.5f, btnY - gap * 0.4f);
        var plateMax = new Vector2(closeX + btnSize + gap * 0.5f,
            btnY + btnSize + gap * 0.4f);
        drawList.AddRectFilled(plateMin, plateMax,
            SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.45f)), 8f * scale);
        drawList.AddRect(plateMin, plateMax,
            SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.12f)), 8f * scale);

        ImGui.SetCursorScreenPos(new Vector2(foldX, btnY));
        if (SpheneUI.Icons.Button(
                folded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                btnSize, null, null, btnSize))
        {
            SpheneUI.Chrome.ToggleFold(WindowName);
        }
        SpheneUI.AttachTooltip(folded ? "Expand" : "Collapse");

        if (ShowCloseButton)
        {
            ImGui.SetCursorScreenPos(new Vector2(closeX, btnY));
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Times, btnSize, null, null, btnSize))
            {
                IsOpen = false;
            }
            SpheneUI.AttachTooltip("Close");
        }
        ImGui.PopClipRect();
    }

    /// <summary>
    /// Back/forward buttons plus the breadcrumb pill, anchored right of the
    /// profile zone's content edge · replaces the former centered page title.
    /// </summary>
    private void DrawHeaderNavCluster(Vector2 pos, float btnSize, float rightLimitX)
    {
        var scale = SpheneUI.Scale;
        ImGui.SetCursorScreenPos(pos);
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowLeft, btnSize, null, null, btnSize,
                enabled: _navBack.Count > 0))
        {
            NavigateBack();
        }
        SpheneUI.AttachTooltip("Back");

        ImGui.SetCursorScreenPos(pos + new Vector2(btnSize + 4f * scale, 0f));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowRight, btnSize, null, null, btnSize,
                enabled: _navForward.Count > 0))
        {
            NavigateForward();
        }
        SpheneUI.AttachTooltip("Forward");

        ImGui.SetCursorScreenPos(pos + new Vector2(btnSize * 2f + 8f * scale, 0f));
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(
                   CurrentLocation.Page == SpheneHubPage.Home))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Home, btnSize, null, null, btnSize))
            {
                NavigateTo(new HubLocation(SpheneHubPage.Home, null, null));
            }
        }
        SpheneUI.AttachTooltip("Home");

        // breadcrumb continues on the same toolbar row, vertically centered
        // on the buttons · page-level help lives on the nav rail entries.
        var pillH = 26f * scale;
        var crumbX = pos.X + btnSize * 3f + 8f * scale + 10f * scale;
        DrawBreadcrumbPill(new Vector2(crumbX, pos.Y + (btnSize - pillH) * 0.5f),
            MathF.Max(0f, rightLimitX - crumbX - 8f * scale));
    }

    /// <summary>
    /// The current location as a pill ("Page › detail") · resolves the crumb
    /// labels from the hub state and renders via the shared
    /// <see cref="SpheneUI.Chrome.CrumbPill"/>; the parent segment navigates
    /// one level up.
    /// </summary>
    private void DrawBreadcrumbPill(Vector2 pillMin, float maxWidth)
    {
        string? parentLabel = null;
        var parentTarget = default(HubLocation);
        var currentLabel = PageCrumbLabel(_activePage);
        if (_activePage == SpheneHubPage.ProfileStyle)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Profile);
            parentTarget = new HubLocation(SpheneHubPage.Profile, null, null);
        }
        else if (_activePage == SpheneHubPage.Friends && _viewingFriend != null)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Friends);
            parentTarget = new HubLocation(SpheneHubPage.Friends, null, null);
            currentLabel = _viewingFriend.UserData.AliasOrUID;
        }
        else if (_activePage == SpheneHubPage.Syncshells && GetSelectedGroup() is { } group)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Syncshells);
            parentTarget = new HubLocation(SpheneHubPage.Syncshells, null, null);
            currentLabel = group.GroupAliasOrGID;
        }
        else if (_activePage == SpheneHubPage.News
                 && (_viewingAnnouncementId != null || _announcementEditMode))
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.News);
            parentTarget = new HubLocation(SpheneHubPage.News, null, null);
            currentLabel = _announcementEditMode ? "Editor" : "Article";
        }
        else if (_activePage == SpheneHubPage.Settings)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Settings);
            parentTarget = new HubLocation(SpheneHubPage.Settings, null, null, (int)SettingsUi.SettingsPage.Home);
            currentLabel = _settingsUi.ActiveCrumbLabel;
        }
        else if (_activePage == SpheneHubPage.Supporter
            && _settingsUi.ActiveSettingsPageValue is SettingsUi.SettingsPage.SupporterNameplate
                or SettingsUi.SettingsPage.SupporterListRow)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Supporter);
            parentTarget = new HubLocation(SpheneHubPage.Supporter, null, null, (int)SettingsUi.SettingsPage.Supporter);
            currentLabel = _settingsUi.ActiveCrumbLabel;
        }
        else if (_activePage == SpheneHubPage.ModSharing)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.ModSharing);
            parentTarget = new HubLocation(SpheneHubPage.ModSharing, null, null, (int)ModSharingUi.ModSharingPage.Send);
            currentLabel = _modSharingUi.ActiveCrumbLabel;
        }
        else if (_activePage == SpheneHubPage.CharacterData)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.CharacterData);
            parentTarget = new HubLocation(SpheneHubPage.CharacterData, null, null, (int)CharaDataHubUi.CharaDataPage.GposeTogether);
            currentLabel = _charaDataHubUi.ActiveCrumbLabel;
        }
        else if (_activePage == SpheneHubPage.DataAnalysis)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.DataAnalysis);
            parentTarget = new HubLocation(SpheneHubPage.DataAnalysis, null, null, (int)DataAnalysisUi.DataAnalysisPage.Overview);
            currentLabel = _dataAnalysisUi.ActiveCrumbLabel;
        }

        SpheneUI.Chrome.CrumbPill(pillMin, maxWidth, parentLabel, currentLabel,
            parentLabel != null ? () => NavigateTo(parentTarget) : null);
    }

    private static string PageCrumbLabel(SpheneHubPage page) => page switch
    {
        SpheneHubPage.Profile => "My Profile",
        SpheneHubPage.Friends => "Friends",
        SpheneHubPage.Syncshells => "Syncshells",
        SpheneHubPage.CharacterData => "Character Data Legacy",
        SpheneHubPage.DataAnalysis => "Character Data",
        SpheneHubPage.ModSharing => "Mod Sharing",
        SpheneHubPage.Settings => "Settings",
        SpheneHubPage.Supporter => "Supporter",
        SpheneHubPage.ProfileStyle => "Style",
        SpheneHubPage.News => "News",
        _ => "Hub",
    };

    private IDalamudTextureWrap? GetAvatarTexture(SpheneProfileData profile)
    {
        if (!profile.ImageData.Value.SequenceEqual(_lastAvatarBytes))
        {
            _lastAvatarBytes = profile.ImageData.Value;
            SpheneUI.Media.Evict(AvatarKey);
        }
        return SpheneUI.Media.Texture(AvatarKey, _lastAvatarBytes);
    }

    private IDalamudTextureWrap? GetBannerTexture(SpheneProfileData profile)
    {
        var data = profile.BannerImageData.Value;
        if (data.Length == 0)
        {
            return null;
        }
        if (!data.SequenceEqual(_lastBannerBytes))
        {
            _lastBannerBytes = data;
            SpheneUI.Media.Evict(BannerKey);
        }
        return SpheneUI.Media.Texture(BannerKey, _lastBannerBytes);
    }

    private static IDalamudTextureWrap? GetFriendTexture(string key, byte[] data, ref byte[] last)
    {
        if (data.Length == 0)
        {
            return null;
        }
        if (!data.SequenceEqual(last))
        {
            last = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Texture(key, last);
    }

    private SpheneUI.Media.AnimatedImage? GetFriendRowImage(string uid, byte[] data)
    {
        var key = $"friendrow:{uid}";
        if (!_friendRowImageBytes.TryGetValue(uid, out var last)
            || !last.SequenceEqual(data))
        {
            _friendRowImageBytes[uid] = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Animated(key, data);
    }

    private IDalamudTextureWrap? FriendListAvatar(string uid, SpheneProfileData profile)
    {
        var data = profile.ImageData.Value;
        var key = $"hubfriend-list:{uid}";
        if (!_friendListImageBytes.TryGetValue(uid, out var last)
            || !last.SequenceEqual(data))
        {
            _friendListImageBytes[uid] = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Texture(key, data);
    }

    /// <summary>Home world of an online pair · empty while the remote
    /// character is not spawned (offline users expose no world data).</summary>
    private static string FriendWorldName(Pair pair) => pair.HomeWorldName;

    private string OwnHeroName()
    {
        var playerName = _dalamudUtilService.GetPlayerName();
        var hasPlayerName = !string.IsNullOrEmpty(playerName)
            && !string.Equals(playerName, "--", StringComparison.Ordinal);
        return !string.IsNullOrWhiteSpace(_apiController.OwnDisplayName)
            ? _apiController.OwnDisplayName
            : !string.IsNullOrWhiteSpace(_apiController.OwnAlias)
                ? _apiController.OwnAlias
                : hasPlayerName ? playerName! : "--";
    }

    private string OwnHeroSubtitle()
    {
        return !string.IsNullOrWhiteSpace(_apiController.OwnAlias)
            ? "@" + _apiController.OwnAlias
            : "--";
    }

    /// <summary>Start page: own community profile hero (theme + banner + stats) followed by the editor.</summary>
    /// <summary>Fixed chrome of My Profile · hero banner (identity, banner
    /// art, edit actions) plus the Profile/Gallery/Blog tab strip. Drawn in
    /// the nav content's fixed zone so it stays put while the body scrolls.</summary>
    private void DrawProfileChrome(Vector4 accent,
        SpheneUI.Profile.HeroHost heroHost)
    {
        var theme = SpheneTheme.Current;
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var renderProfile = EffectiveProfilePreview(profile);
        var pfpWrap = _profileAvatarBytes != null
            ? GetFriendTexture("hub:profile-draft:avatar", _profileAvatarBytes,
                ref _lastDraftAvatarBytes)
            : GetAvatarTexture(profile);
        var bannerWrap = _profileBannerBytes != null
            ? GetFriendTexture("hub:profile-draft:banner", _profileBannerBytes,
                ref _lastDraftBannerBytes)
            : GetBannerTexture(profile);
        var profileTheme = renderProfile.ProfileTheme.Value;

        var displayName = OwnHeroName();
        if (_profileNameSet)
        {
            var playerName = _dalamudUtilService.GetPlayerName();
            var hasPlayerName = !string.IsNullOrEmpty(playerName)
                && !string.Equals(playerName, "--", StringComparison.Ordinal);
            displayName = !string.IsNullOrWhiteSpace(_profileName) ? _profileName!
                : !string.IsNullOrWhiteSpace(_apiController.OwnAlias) ? _apiController.OwnAlias
                : hasPlayerName ? playerName! : "--";
        }
        var heroSubtitle = _profileAliasSet
            ? (string.IsNullOrWhiteSpace(_profileAlias) ? "--" : "@" + _profileAlias)
            : OwnHeroSubtitle();
        var stagedIsNsfw = renderProfile.IsNSFW;

        _heroEdit.Enabled = _profileEditMode;
        _heroEdit.OnAvatarClicked = () => PickAndCropImage(PhotoStudioTarget.ProfileAvatar);
        _heroEdit.OnBannerClicked = () => PickAndCropImage(PhotoStudioTarget.ProfileBanner);
        _heroEdit.OnStatusCommitted = v => StageProfilePatch(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: null, ProfilePictureBase64: null, Description: null, StatusMessage: v));
        _heroEdit.OnDescriptionCommitted = v => StageProfilePatch(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: null, ProfilePictureBase64: null, Description: v));
        _heroEdit.OnTagsCommitted = v => StageProfilePatch(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: null, ProfilePictureBase64: null, Description: null, Tags: v));
        _heroEdit.OnNsfwToggled = v => StageProfilePatch(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: v, ProfilePictureBase64: null, Description: null));
        _heroEdit.DisplayNameMaxLength = Constants.DisplayNameMaxLength;
        _heroEdit.HandleMaxLength = Constants.AliasMaxLength;
        _heroEdit.OnDisplayNameCommitted = v =>
        {
            var normalized = v.Trim();
            _profileNameSet = true;
            _profileName = normalized.Length == 0 ? null : normalized;
            _heroEdit.Feedback = normalized.Length == 0
                ? "Display name cleared · pending apply."
                : "Display name staged · pending apply.";
            _heroEdit.FeedbackColor = SpheneUI.Palette.Success;
        };
        var renameAt = _apiController.AliasChangeAvailableAt;
        var renameLocked = renameAt.HasValue && renameAt.Value > DateTime.UtcNow;
        _heroEdit.HandleLocked = renameLocked;
        _heroEdit.HandleHint = renameLocked
            ? $"Renames possible once every {Constants.AliasRenameCooldownDays} days · next rename {renameAt!.Value.ToLocalTime():g}."
            : $"Handle · used for pair requests and profile lookup. Click to rename (once every {Constants.AliasRenameCooldownDays} days).";
        _heroEdit.OnHandleCommitted = string.IsNullOrWhiteSpace(_apiController.OwnAlias)
            ? null
            : v =>
            {
                _profileAliasSet = true;
                _profileAlias = v.Trim();
                _heroEdit.Feedback = "Handle staged · pending apply.";
                _heroEdit.FeedbackColor = SpheneUI.Palette.Success;
            };
        _heroEdit.DisplayNameCurrent = _profileNameSet ? _profileName : _apiController.OwnDisplayName;
        _heroEdit.HandleCurrent = _profileAliasSet ? _profileAlias : _apiController.OwnAlias;
        _editProfileUi.CommitOverride = StageProfilePatch;
        _heroEdit.DisplayNameConfirmDetail =
            "Shown to everyone who can see your profile · your handle stays your identity.";
        _heroEdit.HandleConfirmDetail =
            $"Handles identify you for pair requests and profile lookup · renaming starts the {Constants.AliasRenameCooldownDays}-day cooldown.";

        var heroActions = new List<SpheneUI.Profile.HeroAction>
        {
            new(FontAwesomeIcon.Swatchbook, "Style",
                "Accent, banner gradient and list row effects · opens the style page",
                () => NavigateTo(new HubLocation(SpheneHubPage.ProfileStyle, null, null))),
        };
        heroActions.Add(new SpheneUI.Profile.HeroAction(
            _profileEditMode ? FontAwesomeIcon.Check : FontAwesomeIcon.Pen,
            _profileEditMode ? "Done" : "Edit",
            _profileEditMode
                ? "Finish editing your profile"
                : "Edit your profile · click avatar/banner to change, edit status, tags and description inline",
            () =>
            {
                _profileEditMode = !_profileEditMode;
                _heroEdit.AddingTag = false;
                _heroEdit.EditingDisplayName = false;
                _heroEdit.EditingHandle = false;
                if (_profileEditMode)
                {
                    SeedHeroEditBuffers(profile);
                }
                else
                {
                    _styleSeeded = false;
                }
            }));

        SpheneUI.Profile.Hero(displayName, heroSubtitle, renderProfile,
            pfpWrap, bannerWrap, profileTheme, accent, theme.AccentBlue,
            isNsfw: stagedIsNsfw, watermark: SpheneBrandIcons.SpheneLogo(),
            actions: heroActions,
            edit: _heroEdit, host: heroHost);

        // Sub-pages: Profile (classic editor) / Gallery / Blog · same
        // aether-trace strip + slide-in ease as the community pages.
        _profileTabItems.Clear();
        _profileTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.IdCard, "Profile"));
        _profileTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.Images, "Gallery"));
        _profileTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.FeatherAlt, "Chronicles"));
        _profilePageTab = Math.Clamp(_profilePageTab, 0, _profileTabItems.Count - 1);
        var clickedTab = SpheneUI.Controls.TabBar("##profile-tabs", _profileTabItems,
            _profilePageTab, accent);
        if (clickedTab >= 0 && clickedTab != _profilePageTab)
        {
            _profilePageTab = clickedTab;
            _profileTabSwitchT = 0f;
            CloseEmbeddedViewers();
        }
    }

    /// <summary>Scrollable body of My Profile · the active tab's content
    /// (classic editor, gallery or blog) with the tab-switch slide ease,
    /// plus the unsaved-changes toast and the image viewer overlay.</summary>
    private void DrawProfilePage(Vector4 accent)
    {
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var renderProfile = EffectiveProfilePreview(profile);
        _profileTabSwitchT = MathF.Min(1f, _profileTabSwitchT + ImGui.GetIO().DeltaTime / 0.16f);
        var tabEase = 1f - MathF.Pow(1f - _profileTabSwitchT, 3f);
        var tabIndent = (1f - tabEase) * 14f * SpheneUI.Scale;
        if (tabIndent > 0.01f)
        {
            ImGui.Indent(tabIndent);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.35f + 0.65f * tabEase);
        switch (_profilePageTab)
        {
            case 1:
                DrawOwnProfileGallery(accent);
                break;
            case 2:
                DrawOwnProfileBlog(accent);
                break;
            default:
                DrawProfileAboutMe(renderProfile, accent, canEdit: true);
                if (_profileEditMode)
                {
                    _editProfileUi.DrawEmbedded();
                }
                else
                {
                    DrawOwnProfileOverview(renderProfile, accent);
                }
                break;
        }
        ImGui.PopStyleVar();
        if (tabIndent > 0.01f)
        {
            ImGui.Unindent(tabIndent);
        }

        var toastPos = ImGui.GetWindowPos();
        var toastSize = ImGui.GetWindowSize();
        _profileToast.Draw(ProfileDraftDirty(), "You have unsaved profile changes",
            toastPos.X + toastSize.X * 0.5f,
            toastPos.Y + toastSize.Y - 6f * SpheneUI.Scale,
            new UnsavedChangesToast.Button("Reset", FontAwesomeIcon.Undo,
                SpheneUI.Colors.Accent, true,
                "Drop the staged changes.", ResetProfileEdits),
            new UnsavedChangesToast.Button("Apply", FontAwesomeIcon.Check,
                SpheneUI.Palette.LuminousGold, true,
                "Publish the staged profile changes.", () => _ = ApplyProfileDraftAsync(),
                Solid: true));

        DrawProfileViewerOverlay();
    }

    /// <summary>Profile with all staged patch fields applied · the hero, link
    /// card and theme render this so pending edits preview before Apply.</summary>
    private SpheneProfileData EffectiveProfilePreview(SpheneProfileData profile)
    {
        var p = _profilePatch;
        if (p == null)
        {
            return profile;
        }
        return profile with
        {
            IsNSFW = p.IsNSFW ?? profile.IsNSFW,
            Description = p.Description ?? profile.Description,
            StatusMessage = p.StatusMessage ?? profile.StatusMessage,
            Tags = p.Tags ?? profile.Tags,
            Theme = p.Theme ?? profile.Theme,
            Visibility = p.Visibility ?? profile.Visibility,
            Links = p.Links ?? profile.Links,
            Base64RowImage = p.RowImageBase64 ?? profile.Base64RowImage,
            PublicProfile = p.PublicProfile ?? profile.PublicProfile,
        };
    }

    /// <summary>Read-only link chips on the own profile's Profile tab  · 
    /// same card grid as the community links widget.</summary>
    private static void DrawOwnProfileLinks(SpheneProfileData profile, Vector4 accent)
    {
        var links = profile.Links?.Where(l => !string.IsNullOrWhiteSpace(l.Url)).ToList();
        if (links == null || links.Count == 0)
        {
            return;
        }
        SyncshellCommunityView.Widget("profile-links", "Links",
            FontAwesomeIcon.Link, links.Count, accent, () =>
            {
                SpheneUI.Layout.CardGrid(links, 170f * SpheneUI.Scale, (link, i) =>
                    SyncshellCommunityView.DrawLinkCard(link.Label, link.Url, link.Icon, accent, i));
            });
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    /// <summary>Own Profile overview under the About-me section · recent
    /// Chronicles, recent photos, then the existing links and the hint.</summary>
    private void DrawOwnProfileOverview(SpheneProfileData renderProfile, Vector4 accent)
    {
        var me = new UserData(_apiController.UID);
        var extras = _spheneProfileManager.GetProfileExtras(me);
        DrawChroniclePreviewSection(extras, accent, isOwn: true,
            () => NavigateTo(new HubLocation(SpheneHubPage.Profile, null, null,
                SubPage: 2)),
            id =>
            {
                _expandedChronicles.Add(id);
                NavigateTo(new HubLocation(SpheneHubPage.Profile, null, null,
                    SubPage: 2));
            });
        DrawPhotoPreviewSection(extras, _apiController.UID, $"own:{me.UID}",
            accent, isOwn: true,
            () => NavigateTo(new HubLocation(SpheneHubPage.Profile, null, null,
                SubPage: 1)));
        DrawOwnProfileLinks(renderProfile, accent);
        SpheneUI.Controls.HelpText(
            "This is how your profile looks to others · hit Edit in the banner to restyle it, or add photos and Chronicles on the other tabs.");
    }

    /// <summary>About-me card · the full bio as markdown, or a compact prompt.
    /// Editing lives in the hero's About-me panel (same field) - the pen here
    /// just enters that edit flow. Same card chrome as the community widgets.</summary>
    private void DrawProfileAboutMe(SpheneProfileData? profile, Vector4 accent,
        bool canEdit)
    {
        var bio = profile?.Description;
        var hasBio = !string.IsNullOrWhiteSpace(bio)
            && !string.Equals(bio, SpheneProfileManager.NoDescriptionPlaceholder,
                StringComparison.Ordinal);
        var actions = canEdit && !_profileEditMode && profile is { IsFlagged: false }
            ? new (FontAwesomeIcon, string, Action)?[]
            { (FontAwesomeIcon.Pen, "Edit your bio", EnterProfileEditMode) }
            : [];
        SyncshellCommunityView.Widget("profile-about-me", "About me",
            FontAwesomeIcon.User, null, accent, () =>
            {
                if (hasBio)
                {
                    MarkdownRenderer.RenderMarkdown(bio!);
                }
                else
                {
                    SpheneUI.Text.MutedWrapped(canEdit
                        ? "Tell others a little about yourself."
                        : "No introduction yet.");
                }
            }, null, actions);
    }

    /// <summary>Up to two recent Chronicles as compact nav rows · the same
    /// extras the full tab shows (privacy already applied server-side).
    /// A row lands on the full tab with the entry expanded.</summary>
    private static void DrawChroniclePreviewSection(UserProfileExtrasDto? extras,
        Vector4 accent, bool isOwn, Action onViewAll, Action<long> onOpenPost)
    {
        var blog = extras?.Blog;
        if (!isOwn && extras != null && blog is not { Count: > 0 })
        {
            return;
        }
        SyncshellCommunityView.Widget("profile-recent-chronicles",
            "Recent Chronicles", FontAwesomeIcon.FeatherAlt, null, accent, () =>
            {
                if (extras == null)
                {
                    SpheneUI.Fx.SkeletonLine();
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    SpheneUI.Fx.SkeletonLine(0.6f);
                }
                else if (blog is not { Count: > 0 })
                {
                    SpheneUI.Text.MutedWrapped(
                        "No Chronicles yet · the first entry could be a story hook or just a hello.");
                }
                else
                {
                    var shown = Math.Min(2, blog.Count);
                    for (var i = 0; i < shown; i++)
                    {
                        if (i > 0)
                        {
                            SpheneUI.Layout.Separator();
                        }
                        var post = blog[i];
                        // Compact nav row · title + date only, the full
                        // entry lives on the Chronicles tab.
                        var title = string.IsNullOrWhiteSpace(post.Title)
                            ? "Untitled Chronicle"
                            : post.Title;
                        SpheneUI.Rows.Nav(title,
                            SpheneUI.Text.FormatDate(post.CreatedAt),
                            () => onOpenPost(post.Id), FontAwesomeIcon.FeatherAlt);
                    }
                }
            }, null,
            (FontAwesomeIcon.ArrowRight, "View all Chronicles", onViewAll));
    }

    /// <summary>Up to three recent gallery photos through the established grid
    /// · click opens the shared viewer on the FULL gallery collection at the
    /// clicked image's index.</summary>
    private void DrawPhotoPreviewSection(UserProfileExtrasDto? extras,
        string gridGid, string ownerKey, Vector4 accent, bool isOwn,
        Action onViewAll)
    {
        var gallery = extras?.Gallery;
        if (!isOwn && extras != null && gallery is not { Count: > 0 })
        {
            return;
        }
        SyncshellCommunityView.Widget("profile-recent-photos", "Recent photos",
            FontAwesomeIcon.Images, null, accent, () =>
            {
                if (extras == null)
                {
                    SpheneUI.Fx.SkeletonLine(1f, 88f * SpheneUI.Scale);
                }
                else if (gallery is not { Count: > 0 })
                {
                    SpheneUI.Text.MutedWrapped(
                        "No photos yet · venue shots, glamours or event posters make a profile come alive.");
                }
                else
                {
                    var top = gallery.OrderByDescending(g => g.CreatedAt)
                        .Take(3).ToList();
                    ToViewerImages(top, _overviewGridScratch, ownerKey);
                    SyncshellCommunityView.DrawGalleryGrid(_overviewGridScratch,
                        "hubov:", gridGid, onClick: i =>
                        {
                            var img = top[i];
                            ToViewerImages(gallery, _overviewViewerScratch,
                                ownerKey);
                            var idx = gallery.FindIndex(g => g.Id == img.Id);
                            OpenProfileViewer(_overviewViewerScratch,
                                Math.Max(0, idx), ownerKey);
                        });
                }
            }, null,
            (FontAwesomeIcon.ArrowRight, "Open gallery", onViewAll));
    }

    /// <summary>Enters the profile edit flow the banner Edit button drives ·
    /// used by the About-me pen so the bio is editable from its own section.</summary>
    private void EnterProfileEditMode()
    {
        _profileEditMode = true;
        _heroEdit.AddingTag = false;
        _heroEdit.EditingDisplayName = false;
        _heroEdit.EditingHandle = false;
        SeedHeroEditBuffers(_spheneProfileManager.GetSpheneProfile(
            new UserData(_apiController.UID)));
    }

    private void SeedStyleBuffers(SpheneProfileTheme theme)
    {
        _styleBaseTheme = theme;
        _styleUseAccent = theme.Accent.HasValue;
        _styleUseTop = theme.BannerTop.HasValue;
        _styleUseBottom = theme.BannerBottom.HasValue;
        _styleTopOver = theme.BannerTopOver;
        _styleBottomOver = theme.BannerBottomOver;
        if (theme.Accent.HasValue) _styleAccent = SpheneProfileTheme.ToVector4(theme.Accent.Value);
        if (theme.BannerTop.HasValue) _styleTop = SpheneProfileTheme.ToVector4(theme.BannerTop.Value);
        if (theme.BannerBottom.HasValue) _styleBottom = SpheneProfileTheme.ToVector4(theme.BannerBottom.Value);
    }

    /// <summary>The style buffers projected as a live-preview theme · fields
    /// the editor no longer owns (row/card effects, shimmer) ride through
    /// from the stored theme so saving cannot wipe them.</summary>
    private SpheneProfileTheme PreviewStyleTheme() => _styleBaseTheme with
    {
        Accent = _styleUseAccent ? SpheneProfileTheme.FromVector4(_styleAccent) : null,
        BannerTop = _styleUseTop ? SpheneProfileTheme.FromVector4(_styleTop) : null,
        BannerBottom = _styleUseBottom ? SpheneProfileTheme.FromVector4(_styleBottom) : null,
        BannerTopOver = _styleTopOver,
        BannerBottomOver = _styleBottomOver,
    };

    /// <summary>
    /// Dedicated style page reached via the hero's "Style" action or the
    /// "My Profile › Style" breadcrumb. Tab strip + live preview stay pinned
    /// under the header while the settings cards scroll beneath; Save/Reset
    /// persist and return to the previous location.
    /// </summary>
    /// <summary>Scrollable body of the style editor · the staged-theme
    /// settings cards for the active preview tab plus the unsaved-changes
    /// toast.</summary>
    private void DrawStylePage(Vector4 pageAccent)
    {
        var accent = PreviewStyleTheme().AccentColor(pageAccent);
        // the profile card is the only styled surface left - list-row
        // styling moved to the supporter list-row studio entirely
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Profile.ThemeColorRow("Custom accent color",
                ref _styleUseAccent, ref _styleAccent);
            SpheneUI.Profile.ThemeColorRow("Custom banner top color",
                ref _styleUseTop, ref _styleTop);
            if (_styleUseTop)
            {
                SpheneUI.Controls.Toggle("Over banner image##topover", ref _styleTopOver,
                    indented: true);
                SpheneUI.AttachTooltip("Blend the top color over your banner image instead of replacing it");
            }
            SpheneUI.Profile.ThemeColorRow("Custom banner bottom color",
                ref _styleUseBottom, ref _styleBottom);
            if (_styleUseBottom)
            {
                SpheneUI.Controls.Toggle("Over banner image##bottomover",
                    ref _styleBottomOver, indented: true);
                SpheneUI.AttachTooltip("Blend the bottom color over your banner image instead of replacing it");
            }
        }, "Appearance", accent,
            "Colors and banner blending your profile card shows to you and paired users.");

        var toastPos = ImGui.GetWindowPos();
        var toastSize = ImGui.GetWindowSize();
        _profileToast.Draw(ProfileDraftDirty(), "You have unsaved profile changes",
            toastPos.X + toastSize.X * 0.5f,
            toastPos.Y + toastSize.Y - 6f * SpheneUI.Scale,
            new UnsavedChangesToast.Button("Reset", FontAwesomeIcon.Undo,
                SpheneUI.Colors.Accent, true,
                "Drop the staged changes.", ResetProfileEdits),
            new UnsavedChangesToast.Button("Apply", FontAwesomeIcon.Check,
                SpheneUI.Palette.LuminousGold, true,
                "Publish the staged profile changes.", () => _ = ApplyProfileDraftAsync(),
                Solid: true));
    }

    /// <summary>Fixed chrome of the style editor · the preview-context tab
    /// strip with Save/Reset docked in its row, then the live preview of the
    /// staged theme (profile hero card or friends-list row). Drawn in the
    /// nav content's fixed zone · replaces the old reserve-and-redraw sticky
    /// header.</summary>
    private void DrawStyleChrome(Vector4 pageAccent)
    {
        var scale = SpheneUI.Scale;
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var previewProfile = EffectiveProfilePreview(profile);
        if (!_styleSeeded)
        {
            SeedStyleBuffers(profile.ProfileTheme.Value);
            _styleSeeded = true;
        }
        var displayName = OwnHeroName();

        var previewTheme = PreviewStyleTheme();

        var pfpWrap = _profileAvatarBytes != null
            ? GetFriendTexture("hub:profile-draft:avatar", _profileAvatarBytes,
                ref _lastDraftAvatarBytes)
            : GetAvatarTexture(previewProfile);
        var bannerWrap = _profileBannerBytes != null
            ? GetFriendTexture("hub:profile-draft:banner", _profileBannerBytes,
                ref _lastDraftBannerBytes)
            : GetBannerTexture(previewProfile);

        // Save/Reset stay docked right on the action row · the profile card
        // is the only styled surface, so there is no preview tab strip.
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var actionWidth = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Save, "Save style")
            + SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Undo, "Reset") + spacing;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX()
            + MathF.Max(0f, ImGui.GetContentRegionAvail().X - actionWidth));
        if (SpheneUI.Controls.PrimaryButton("Save style", FontAwesomeIcon.Save))
        {
            var theme = PreviewStyleTheme();
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                Theme: theme.Serialize()));
            _styleSeeded = false;
            LeaveStylePage();
        }
        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Reset"))
        {
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                Theme: ""));
            _styleSeeded = false;
            LeaveStylePage();
        }
        // the complete profile surface, live-bound to the unsaved buffers  ·
        // exactly what viewers see (banner, avatar, name, status, tags).
        // Bounded to the preview column so the banner does not bleed
        // mid-page.
        var previewMin = ImGui.GetCursorScreenPos();
        SpheneUI.Profile.Hero(displayName, OwnHeroSubtitle(), previewProfile,
            pfpWrap, bannerWrap, previewTheme,
            pageAccent, SpheneTheme.Current.AccentBlue,
            isNsfw: previewProfile.IsNSFW, watermark: SpheneBrandIcons.SpheneLogo(),
            host: new SpheneUI.Profile.HeroHost(previewMin,
                previewMin + new Vector2(ImGui.GetContentRegionAvail().X, 0f)));
        ImGui.Dummy(new Vector2(0f, 8f * scale));
    }

    /// <summary>Returns from the style page to wherever the user came from.</summary>
    private void LeaveStylePage()
    {
        if (_navBack.Count > 0)
        {
            NavigateBack();
        }
        else
        {
            SetPage(SpheneHubPage.Profile);
        }
    }

    private void SeedHeroEditBuffers(SpheneProfileData profile)
    {
        _heroEdit.StatusBuffer = _profilePatch?.StatusMessage ?? profile.StatusMessage ?? string.Empty;
        var stagedDesc = _profilePatch?.Description;
        _heroEdit.DescriptionBuffer = stagedDesc != null ? stagedDesc
            : string.Equals(profile.Description, SpheneProfileManager.NoDescriptionPlaceholder,
                StringComparison.Ordinal)
                ? string.Empty
                : profile.Description;
        _heroEdit.DisplayNameBuffer = _profileNameSet ? _profileName ?? string.Empty
            : _apiController.OwnDisplayName ?? string.Empty;
        _heroEdit.HandleBuffer = _profileAliasSet ? _profileAlias ?? string.Empty
            : _apiController.OwnAlias ?? string.Empty;
        _heroEdit.EditingDisplayName = false;
        _heroEdit.EditingHandle = false;
        _heroEdit.Feedback = null;
    }

    /// <summary>Stages a <see cref="UserProfileDto"/> patch · non-null fields
    /// merge into the pending patch so nothing reaches the server until the
    /// toast's Apply flushes everything in one <c>UserSetProfile</c>.</summary>
    private void StageProfilePatch(UserProfileDto patch)
    {
        _profilePatch = _profilePatch == null ? patch : _profilePatch with
        {
            IsNSFW = patch.IsNSFW ?? _profilePatch.IsNSFW,
            ProfilePictureBase64 = patch.ProfilePictureBase64 ?? _profilePatch.ProfilePictureBase64,
            Description = patch.Description ?? _profilePatch.Description,
            StatusMessage = patch.StatusMessage ?? _profilePatch.StatusMessage,
            Tags = patch.Tags ?? _profilePatch.Tags,
            BannerImageBase64 = patch.BannerImageBase64 ?? _profilePatch.BannerImageBase64,
            Theme = patch.Theme ?? _profilePatch.Theme,
            Visibility = patch.Visibility ?? _profilePatch.Visibility,
            RowImageBase64 = patch.RowImageBase64 ?? _profilePatch.RowImageBase64,
            Links = patch.Links ?? _profilePatch.Links,
            PublicProfile = patch.PublicProfile ?? _profilePatch.PublicProfile,
        };
    }

    private bool ProfileDraftDirty()
        => _profilePatch != null || _profileNameSet || _profileAliasSet;

    /// <summary>Drops every staged profile change without saving.</summary>
    private void ResetProfileEdits()
    {
        _profilePatch = null;
        _profileNameSet = false;
        _profileName = null;
        _profileAliasSet = false;
        _profileAlias = null;
        _profileAvatarBytes = null;
        _profileBannerBytes = null;
        SpheneUI.Media.Evict("hub:profile-draft:avatar");
        SpheneUI.Media.Evict("hub:profile-draft:banner");
    }

    /// <summary>Commits the staged profile edits: alias rename and display
    /// name through their dedicated endpoints, then one merged
    /// <c>UserSetProfile</c>. A failed alias rename keeps the draft so the
    /// user can fix and retry.</summary>
    private async Task ApplyProfileDraftAsync()
    {
        try
        {
            if (_profileAliasSet && !string.IsNullOrWhiteSpace(_profileAlias))
            {
                var result = await _apiController.UserRenameAlias(_profileAlias)
                    .ConfigureAwait(false);
                if (result != SetAliasResult.Success)
                {
                    _heroEdit.Feedback = result switch
                    {
                        SetAliasResult.Taken => "This alias is already taken.",
                        SetAliasResult.Cooldown => $"Renames are limited to once every {Constants.AliasRenameCooldownDays} days.",
                        _ => "Invalid alias · use 5-15 characters: letters, digits, . _ -",
                    };
                    _heroEdit.FeedbackColor = SpheneUI.Colors.Danger;
                    return;
                }
            }
            if (_profileNameSet)
            {
                await _apiController.UserSetDisplayName(_profileName).ConfigureAwait(false);
            }
            if (_profilePatch != null)
            {
                await _apiController.UserSetProfile(_profilePatch).ConfigureAwait(false);
            }
            ResetProfileEdits();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply staged profile edits");
        }
    }

    /// <summary>Opens the photo studio's file browser for the avatar or banner  · 
    /// the rendered result is uploaded as the profile image.</summary>
    private void PickAndCropImage(PhotoStudioTarget target)
    {
        Mediator.Publish(new OpenPhotoStudioMessage(null, target, prepared =>
        {
            var isAvatar = target == PhotoStudioTarget.ProfileAvatar;
            StageProfilePatch(new UserProfileDto(
                new UserData(_apiController.UID), Disabled: false, IsNSFW: null,
                ProfilePictureBase64: isAvatar ? prepared.Base64 : null,
                Description: null,
                BannerImageBase64: isAvatar ? null : prepared.Base64));
            if (isAvatar)
            {
                _profileAvatarBytes = prepared.Bytes;
            }
            else
            {
                _profileBannerBytes = prepared.Bytes;
            }
        }));
    }

    /// <summary>Friends list of paired users; selecting one shows their themed profile inline.</summary>
    /// <summary>
    /// Landing page: banner hero with avatar and status, live stats, quick
    /// access into every hub section, a community/news area and the supporter
    /// promo · the single place where the hub converges.
    /// </summary>
    /// <summary>Bottom strip of the nav rail reserved for the brand
    /// lockup · scroll content always ends above it.</summary>
    private static float RailFooterHeight => 58f * SpheneUI.Scale;

    private void DrawHomePage(Vector4 accent)
    {
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));

        SpheneUI.Layout.RegisterPageHelp("home", HomeHelpContent);
        DrawHomeHero(profile, accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.CardGap);
        DrawHomeQuickNav();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.CardGap);

        _communityFeed.EnsureRequested();
        var items = _communityFeed.Items;

        // two-column dashboard · feed on the left, a stacked side rail on
        // the right. A single table row gives both columns their own
        // content width + independent auto-height.
        using (SpheneUI.Tables.Begin("##home-cols", 2, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("main", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("side", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawHomeFeedCard(accent);
            ImGui.TableSetColumnIndex(1);
            DrawHomeGlanceCard(accent);
            DrawHomeMomentsCard(accent, items);
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.CardGap);
        DrawHomeDiscoverStrip(accent);
    }

    /// <summary>
    /// Quick-nav tiles under the hero · the dashboard's jump-off points into
    /// the community-facing sections, each with a live count line.
    /// </summary>
    private void DrawHomeQuickNav()
    {
        var online = 0;
        foreach (var p in _pairManager.DirectPairs)
        {
            if (p.IsOnline) online++;
        }
        var shells = _pairManager.GroupPairs.Keys.Count;

        // News tile status · an undismissed update push outranks the
        // announcement count; both collapse into one line when pending.
        _announcements.EnsureRequested();
        var announcements = _announcements.Items;
        var updatePending = false;
        foreach (var inboxItem in _notificationInbox.GetItems())
        {
            if (inboxItem.Kind == InboxKind.Update)
            {
                updatePending = true;
                break;
            }
        }
        var newsSub = updatePending && announcements.Count > 0
            ? $"Update + {announcements.Count} announcements"
            : updatePending
                ? "Update available"
                : announcements.Count > 0
                    ? $"{announcements.Count} announcements"
                    : _announcements.IsLoading
                        ? "Checking…"
                        : _apiController.IsConnected ? "All caught up" : "Offline";

        (FontAwesomeIcon Icon, Vector4 Tint, string Title, string Sub,
            SpheneHubPage Page)[] tiles =
            [
                (FontAwesomeIcon.Users, SpheneUI.Palette.SpheneViolet,
                    "Syncshells", $"{shells} joined", SpheneHubPage.Syncshells),
                (FontAwesomeIcon.UserFriends, SpheneUI.Palette.CrystalBlue,
                    "Friends", $"{online} online", SpheneHubPage.Friends),
                (FontAwesomeIcon.Newspaper, SpheneUI.Palette.SpheneMagenta,
                    "Community", "Discover", SpheneHubPage.Community),
                (FontAwesomeIcon.Bullhorn,
                    updatePending ? SpheneUI.Palette.NetworkActive : SpheneUI.Palette.SpheneGold,
                    "News & Updates", newsSub, SpheneHubPage.News),
            ];
        SpheneUI.Layout.CardGrid(tiles, 170f * SpheneUI.Scale, (tile, _) =>
        {
            if (SpheneUI.Layout.FeatureTile($"qnav-{tile.Title}", tile.Icon, tile.Tint,
                    tile.Title, tile.Sub))
            {
                SetPage(tile.Page);
            }
        }, maxCols: 4);
    }

    /// <summary>
    /// Compact "Overview" stat row for the home side rail - online
    /// friends, joined shells and pending items as clickable figures that
    /// deep-link to their pages.
    /// </summary>
    private void DrawHomeGlanceCard(Vector4 accent)
    {
        var online = 0;
        foreach (var p in _pairManager.DirectPairs)
        {
            if (p.IsOnline) online++;
        }
        var shells = _pairManager.GroupPairs.Keys.Count;
        var pending = NotificationInbox.PendingCount(_notificationInbox, _pairManager);

        SpheneUI.Layout.SectionCard(() =>
        {
            var w = ImGui.GetContentRegionAvail().X / 3f;
            DrawGlanceStat("online", online.ToString(), w,
                FontAwesomeIcon.UserFriends, SpheneUI.Palette.SpheneViolet,
                () => SetPage(SpheneHubPage.Friends));
            ImGui.SameLine(0f, 0f);
            DrawGlanceStat("syncshells", shells.ToString(), w,
                FontAwesomeIcon.Users, SpheneUI.Palette.CrystalBlue,
                () => SetPage(SpheneHubPage.Syncshells));
            ImGui.SameLine(0f, 0f);
            DrawGlanceStat("pending", pending.ToString(), w,
                FontAwesomeIcon.Clock, SpheneUI.Palette.NetworkWarning,
                () => SpheneUI.Popups.ToggleFlyout("hub-inbox"));

        }, title: "Overview", accentColor: accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.CardGap);
    }

    private static void DrawGlanceStat(string label, string value, float w,
        FontAwesomeIcon icon, Vector4 tint, Action onClick)
    {
        var scale = SpheneUI.Scale;
        var min = ImGui.GetCursorScreenPos();
        var plate = 30f * scale;
        var statH = plate + ImGui.GetTextLineHeight() + 3f * scale;
        ImGui.Dummy(new Vector2(w, statH));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            onClick();
        }
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        var dl = ImGui.GetWindowDrawList();
        SpheneUI.Layout.SlotIcon(dl, min, plate, icon,
            SpheneUI.ColorUtils.WithAlpha(tint, hovered ? 1f : 0.75f));
        var textX = min.X + plate + SpheneUI.Spacing.Row.Scaled;
        var valueY = min.Y + (plate - ImGui.GetTextLineHeight()) * 0.5f;
        dl.AddText(new Vector2(textX, valueY),
            SpheneUI.ColorUtils.ToU32(
                hovered ? tint : SpheneUI.Colors.TextPrimary), value);
        dl.AddText(new Vector2(min.X, min.Y + plate + 3f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), label);
    }

    /// <summary>
    /// "Latest moments" - the most recent photo uploads across joined
    /// syncshells as a 2x2 tile grid of real thumbnails. Tiles deep-link
    /// into the owning shell's community gallery.
    /// </summary>
    private void DrawHomeMomentsCard(Vector4 accent,
        IReadOnlyList<SyncshellCommunityFeedItemDto> items)
    {
        var photos = new List<SyncshellCommunityFeedItemDto>(4);
        foreach (var i in items)
        {
            if (i.Kind == SyncshellFeedItemKind.Photo
                && (i.ThumbnailMediaHash != null || !string.IsNullOrEmpty(i.ThumbnailBase64)))
            {
                photos.Add(i);
                if (photos.Count == 4) break;
            }
        }
        if (photos.Count == 0)
        {
            return;
        }

        SpheneUI.Layout.SectionCard(() =>
        {
            var scale = SpheneUI.Scale;
            var dl = ImGui.GetWindowDrawList();
            var gap = SpheneUI.Spacing.X4.Scaled;
            var cellW = (ImGui.GetContentRegionAvail().X - gap) * 0.5f;
            var cellH = cellW * 0.7f;
            for (var i = 0; i < photos.Count; i++)
            {
                var item = photos[i];
                var pos = ImGui.GetCursorScreenPos();
                ImGui.PushID($"moment-{item.Id}");
                var clicked = ImGui.InvisibleButton("##mtile",
                    new Vector2(cellW, cellH));
                var hovered = ImGui.IsItemHovered();
                var rectMax = pos + new Vector2(cellW, cellH);
                var wrap = item.ThumbnailMediaHash != null
                    ? SpheneUI.Media.Texture(
                        $"feed-th:{item.GroupGID}:{item.Id}:{item.ThumbnailMediaHash}",
                        () => MarkdownComposer.MediaImageSource($"media:{item.ThumbnailMediaHash}"))
                    : SpheneUI.Media.Texture(
                        $"feed-th:{item.GroupGID}:{item.Id}",
                        () => Convert.FromBase64String(item.ThumbnailBase64!));
                if (wrap != null)
                {
                    // cover-crop · the tile keeps its shape, the image is
                    // cut to fit, never squished
                    SpheneUI.Media.ImageInRect(dl, wrap, pos, rectMax,
                        ImageFit.Cover, rounding: 8f * scale,
                        tint: hovered
                            ? new Vector4(1f, 1f, 1f, 0.8f) : Vector4.One);
                }
                else
                {
                    dl.AddRectFilled(pos, rectMax,
                        SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()),
                        8f * scale);
                }
                dl.AddRect(pos, rectMax,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                        hovered ? accent : SpheneUI.Colors.Border,
                        hovered ? 0.6f : 0.25f)), 8f * scale);
                if (hovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    SpheneUI.AttachTooltip(item.Title);
                }
                if (clicked)
                {
                    NavigateTo(new HubLocation(SpheneHubPage.Syncshells,
                        item.GroupGID, null));
                }
                ImGui.PopID();
                if (i % 2 == 0)
                {
                    ImGui.SameLine(0f, gap);
                }
                else
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
            }
        }, title: "Latest moments", accentColor: accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.CardGap);
    }

    /// <summary>
    /// The community activity feed: news, events and photos across the
    /// user's syncshells, newest first. The header row carries the title,
    /// a "Community overview" link into the Community page and refresh;
    /// each row deep-links into the owning shell's community page.
    /// </summary>
    private void DrawHomeFeedCard(Vector4 accent)
    {
        _communityFeed.EnsureRequested();
        SpheneUI.Layout.SectionCard(() =>
        {
            // Header row · title left, overview link + refresh docked to the
            // card edge. The cluster wraps onto a second row (still
            // right-aligned) when the card is too narrow to share the line.
            ImGui.SetCursorPosY(ImGui.GetCursorPosY()
                + MathF.Max(0f, (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) * 0.5f));
            SpheneUI.Text.Colored("Community feed", accent);
            var linkW = SpheneUI.Icons.TextButtonWidth(
                FontAwesomeIcon.ArrowRight, "Community overview");
            var refreshW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.SyncAlt).X;
            var clusterGap = SpheneUI.Spacing.X4.Scaled;
            var clusterW = linkW + clusterGap + refreshW;
            SpheneUI.Controls.SameLineIfFits(clusterW + SpheneUI.Spacing.X8.Scaled);
            SpheneUI.Controls.DockToCardEdge(clusterW);
            if (SpheneUI.Controls.SegmentButton("Community overview##feednav",
                    FontAwesomeIcon.ArrowRight, accent: accent))
            {
                NavigateTo(new HubLocation(SpheneHubPage.Community, null, null,
                    SubPage: 0));
            }
            SpheneUI.AttachTooltip("Open the full community overview");
            ImGui.SameLine(0f, clusterGap);
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_communityFeed.IsLoading))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.SyncAlt))
                {
                    _ = _communityFeed.Refresh();
                }
            }
            SpheneUI.AttachTooltip("Refresh feed");

            ImGuiHelpers.ScaledDummy(0, 7);

            var items = _communityFeed.Items;
            if (_communityFeed.IsLoading && items.Count == 0)
            {
                for (var i = 0; i < 3; i++)
                {
                    SpheneUI.Fx.SkeletonLine(0.85f - i * 0.12f);
                }
            }
            else if (items.Count == 0 && _communityFeed.RefreshFailed)
            {
                // First-page outage - an honest error state with a manual
                // retry instead of the misleading "nothing new" empty card.
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.ExclamationTriangle,
                    "Couldn't load the feed",
                    "The community feed could not be reached - it retries on its own, or try again now.",
                    "Retry",
                    () => _ = _communityFeed.Refresh(),
                    FontAwesomeIcon.SyncAlt, accent);
            }
            else if (items.Count == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Newspaper,
                    "Nothing new yet",
                    "News, events and photos from your joined and followed syncshells land here.",
                    "Discover communities",
                    () => SetPage(SpheneHubPage.Syncshells),
                    FontAwesomeIcon.Compass, accent);
            }
            else
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    // actor in the key - reaction/comment rows can share the
                    // same target id across different users.
                    if (FeedItemRow.Draw($"feed-{item.Kind}-{item.Id}-{item.ActorUid}", item, "hub:",
                            fallbackAccent: accent, onActorClick: NavigateToAuthor))
                    {
                        NavigateFeedItem(item);
                    }
                    if (i + 1 < items.Count)
                    {
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    }
                }
                if (_communityFeed.PageFailed)
                {
                    // A failed page append leaves the visible rows and the
                    // cursor untouched - the same page retries here.
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    SpheneUI.Text.Colored("Couldn't load more.",
                        SpheneUI.Palette.Warning);
                }
                if (_communityFeed.HasMore)
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                    using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_communityFeed.IsLoading))
                    {
                        if (SpheneUI.Controls.Button(
                                _communityFeed.PageFailed ? "Retry##feedmore" : "Load more##feedmore"))
                        {
                            _ = _communityFeed.LoadMoreAsync();
                        }
                    }
                }
            }
        }, accentColor: accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.CardGap);
    }

    /// <summary>Deep-link routing for feed rows. Section-tagged rows land on
    /// the owning shell's community tab + drill-in. Chronicle rows - a blog
    /// post or activity on one - route through the same deep-link the
    /// notification inbox uses: the TARGET owner decides the host profile
    /// (never the interactor's), with the post id and comment focus attached.</summary>
    private void NavigateFeedItem(SyncshellCommunityFeedItemDto item)
    {
        if (item.Section != null && !string.IsNullOrEmpty(item.GroupGID))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Syncshells, item.GroupGID, null,
                CommunitySection: item.Section, CommunityItemId: item.ItemId,
                FocusCommentId: item.SubItemId));
            return;
        }
        // Who owns the chronicle this lands on: blog rows are owned by their
        // author; comment/reaction rows carry the host owner - ActorUid is
        // only the person who interacted. Pre-TargetOwnerUid servers fall
        // back to the actor, the best available guess.
        var chronicleOwner = item.Kind switch
        {
            SyncshellFeedItemKind.Blog => item.ActorUid,
            SyncshellFeedItemKind.Comment or SyncshellFeedItemKind.Reaction
                => item.TargetOwnerUid ?? item.ActorUid,
            _ => null,
        };
        if (chronicleOwner != null && item.ItemId is { } chronicleId)
        {
            Mediator.Publish(new OpenSpheneHubMessage(
                ChronicleOwnerUid: chronicleOwner,
                ChroniclePostId: chronicleId,
                FocusCommentId: item.SubItemId));
            return;
        }
        if (!string.IsNullOrEmpty(item.GroupGID))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Syncshells, item.GroupGID, null));
        }
    }

    /// <summary>Pair lookup by UID across direct pairs and shell co-members -
    /// the two populations the blog origin rule can emit.</summary>
    private Pair? FindPairByUid(string uid)
    {
        foreach (var p in _pairManager.DirectPairs)
        {
            if (string.Equals(p.UserData.UID, uid, StringComparison.Ordinal)) return p;
        }
        foreach (var kv in _pairManager.GroupPairs)
        {
            foreach (var p in kv.Value)
            {
                if (string.Equals(p.UserData.UID, uid, StringComparison.Ordinal)) return p;
            }
        }
        return null;
    }

    /// <summary>
    /// Discover strip on the home dashboard: up to four publicly listed
    /// shells the user has not joined, as compact cover cards. Click opens
    /// the public community preview.
    /// </summary>
    private void DrawHomeDiscoverStrip(Vector4 accent)
    {
        EnsureDirectoryLoaded();
        var entries = _discover.Entries;
        if (entries == null || entries.Count == 0)
        {
            return;
        }
        var joinedGids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in _pairManager.GroupPairs)
        {
            if (kv.Key.Group != null)
            {
                joinedGids.Add(kv.Key.Group.GID);
            }
        }
        var listed = entries
            .Where(e => !joinedGids.Contains(e.GroupGID) && !e.CallerIsMember).Take(4).ToList();
        if (listed.Count == 0)
        {
            return;
        }

        SpheneUI.Layout.SectionCard(() =>
        {
            var scale = SpheneUI.Scale;
            SpheneUI.Layout.CardGrid(listed, 150f * scale, (entry, _) =>
            {
                var entryAccent = SyncshellCommunityView.ParseAccent(entry.AccentColor)
                    ?? SpheneUI.Palette.CrystalBlue;
                var cellSize = new Vector2(ImGui.GetContentRegionAvail().X, 140f * scale * SpheneUI.Density.Factor);
                if (SyncshellCard.Cover($"home-dir-{entry.GroupGID}", cellSize,
                        () => DirectoryCover(entry),
                        entry.EffectiveJoinPolicy == SyncshellJoinPolicy.Public ? "Join"
                            : entry.EffectiveJoinPolicy == SyncshellJoinPolicy.ApprovalRequired ? "Apply"
                            : "Open", SpheneUI.Palette.Success,
                        entry.GroupAlias ?? entry.GroupGID,
                        $"{entry.MemberCount} members", entryAccent))
                {
                    OpenPublicCommunity(entry);
                }
            }, gap: 10f * scale, maxCols: 4);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            if (SpheneUI.Controls.SegmentButton("Browse all communities",
                    FontAwesomeIcon.Compass, accent: accent))
            {
                SetPage(SpheneHubPage.Syncshells);
            }
        }, title: "Discover", accentColor: accent);
    }

    /// <summary>
    /// The landing hero: the user's own banner bleeds across the card (with a
    /// scrim for readability), a big avatar with presence dot carries the
    /// greeting and status line · the community-page masthead.
    /// </summary>
    private void DrawHomeHero(SpheneProfileData profile, Vector4 accent)
    {
        var avatar = GetAvatarTexture(profile);
        var supporter = _apiController.IsSupporterEffective;
        var gold = SpheneUI.Palette.SpheneGold;
        SpheneUI.Layout.AccentCard(() =>
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            // ringPad keeps the avatar's outer ring inside the content clip  · 
            // the table cell clips at the content origin and would cut the
            // ring's top/left edge.
            var ringPad = 3f * scale;
            var avatarSize = 58f * scale;
            var avatarPos = ImGui.GetCursorScreenPos() + new Vector2(ringPad, ringPad);

            var connected = _apiController.IsConnected;
            var paused = _apiController.IsConnectionPaused;
            var connColor = !connected ? SpheneUI.Palette.NetworkDisconnected
                : paused ? SpheneUI.Palette.NetworkWarning
                : SpheneUI.Palette.NetworkActive;
            var connText = !connected ? "Offline" : paused ? "Paused" : "Online";

            var avatarCenter = avatarPos + new Vector2(avatarSize * 0.5f);
            var ringRadius = avatarSize * 0.5f + 2.5f * scale;
            drawList.AddCircleFilled(avatarCenter, ringRadius,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    supporter ? gold : accent, supporter ? 0.55f : 0.35f)));
            if (supporter)
            {
                drawList.AddCircle(avatarCenter, ringRadius,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(gold, 0.9f)),
                    0, 1.2f * scale);
            }
            ImGui.SetCursorScreenPos(avatarPos);
            SpheneUI.Media.Avatar(avatar, avatarSize);
            SpheneUI.Media.PresenceDot(drawList, avatarPos, avatarSize, connColor);

            var lineH = ImGui.GetTextLineHeight();
            var textX = avatarPos.X + avatarSize + 14f * scale;
            var statusLine = SpheneUI.Profile.StatusLineText(profile, out _);

            // card's inner right edge · measured from the content region, not
            // the window, so pinned controls never overhang the card
            var rightX = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;

            var titleY = avatarPos.Y;
            ImGui.SetCursorScreenPos(new Vector2(textX, titleY));
            // supporters see no CTA - the card stays clean · everyone else
            // gets the joined Ko-fi|perks split
            var ctaW = supporter ? 0f : 190f * scale;
            var supporterExtraW = supporter
                ? SpheneUI.Layout.BadgeWidth("SUPPORTER") + 28f * scale : 0f;
            var greetMaxW = MathF.Max(60f * scale,
                rightX - textX - ctaW - 14f * scale - supporterExtraW);
            using (SpheneUI.DisplayFont.Push())
            {
                SpheneUI.Text.Colored(
                    $"Welcome back, {SpheneUI.Text.Ellipsize(SpheneUI.Profile.OwnName(
                        _apiController.OwnDisplayName, _apiController.OwnAlias), greetMaxW)}",
                    SpheneUI.Colors.TextPrimary);
            }
            var titleMax = ImGui.GetItemRectMax();
            if (supporter)
            {
                var iconY = titleMax.Y - lineH - 2f * scale;
                ImGui.SetCursorScreenPos(new Vector2(titleMax.X + 8f * scale, iconY));
                SpheneUI.Icons.Icon(FontAwesomeIcon.Crown, gold);
                var crownMax = ImGui.GetItemRectMax();
                ImGui.SetCursorScreenPos(new Vector2(crownMax.X + 6f * scale,
                    titleMax.Y - SpheneUI.Layout.BadgeHeight - 2f * scale));
                SpheneUI.Layout.Badge("SUPPORTER", gold, solid: true);
            }

            ImGui.SetCursorScreenPos(new Vector2(textX, titleMax.Y + 4f * scale));
            SpheneUI.Text.Colored($"\u25CF {connText}", connColor);
            ImGui.SameLine();
            var statusMaxW = rightX - ImGui.GetCursorScreenPos().X - ctaW - 14f * scale;
            SpheneUI.Text.Muted($"· {SpheneUI.Text.Ellipsize(statusLine, MathF.Max(40f * scale, statusMaxW))}");
            var statusMax = ImGui.GetItemRectMax();

            // right-pinned CTA · supporters get none, others the joined
            // Ko-fi|perks split - perks opens the preview window
            var frameH = 30f * scale;
            var ctaY = avatarPos.Y + (avatarSize - frameH) * 0.5f;
            var rightBottom = ctaY + frameH;
            if (!supporter)
            {
                ImGui.SetCursorScreenPos(new Vector2(rightX - ctaW, ctaY));
                var split = SpheneBrandIcons.KofiPerksSplit(ctaW, frameH);
                if (split.kofi)
                {
                    Util.OpenLink("https://ko-fi.com/sphenedev");
                }
                if (split.perks)
                {
                    _supporterStudio.OpenUnlockWindow();
                }
            }

            // friends strip · online friends live inside the hero itself
            var stripY = MathF.Max(avatarPos.Y + avatarSize,
                MathF.Max(statusMax.Y, rightBottom)) + 12f * scale;
            var stripLeft = avatarPos.X - ringPad;
            drawList.AddLine(new Vector2(stripLeft, stripY - 6f * scale),
                new Vector2(rightX, stripY - 6f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    SpheneUI.Colors.TextPrimary, 0.08f)), 1f);
            ImGui.SetCursorScreenPos(new Vector2(stripLeft, stripY));
            DrawHeroFriendsStrip(rightX);
        }, accentColor: supporter ? gold : accent, id: "home-hero");
    }

    /// <summary>
    /// Online friends woven into the hero banner: a mini avatar cluster
    /// (click one to open their profile), the online count, and a right-pinned
    /// "View all" link · the landing page's community signal without a tile.
    /// </summary>
    private void DrawHeroFriendsStrip(float rightX)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var rowTop = ImGui.GetCursorScreenPos();
        var online = 0;
        for (var i = 0; i < _pairManager.DirectPairs.Count; i++)
        {
            if (_pairManager.DirectPairs[i].IsOnline)
            {
                online++;
            }
        }

        var avatarSize = 26f * scale;
        var gap = 5f * scale;
        var clusterMaxW = rightX - rowTop.X;

        if (online == 0)
        {
            var emptyText = _pairManager.DirectPairs.Count == 0
                ? "No paired users yet · pair with someone to build your circle."
                : "No friends online right now.";
            var textY = rowTop.Y + (avatarSize - ImGui.GetTextLineHeight()) * 0.5f;
            DrawHeroTextLink(new Vector2(rowTop.X, textY), emptyText,
                "Open your friends list", () => SetPage(SpheneHubPage.Friends));
            ImGui.SetCursorScreenPos(new Vector2(rowTop.X, rowTop.Y + avatarSize));
            return;
        }

        var chipW = 30f * scale;
        var maxAvatars = Math.Max(1, Math.Min(10,
            (int)((clusterMaxW - chipW) / (avatarSize + gap))));
        var drawn = 0;
        for (var i = 0; i < _pairManager.DirectPairs.Count && drawn < maxAvatars; i++)
        {
            var pair = _pairManager.DirectPairs[i];
            if (!pair.IsOnline)
            {
                continue;
            }
            var pos = rowTop + new Vector2(drawn * (avatarSize + gap), 0f);
            ImGui.SetCursorScreenPos(pos);
            var clicked = ImGui.InvisibleButton(
                $"##home-friend-{pair.UserData.UID}", new Vector2(avatarSize, avatarSize));
            var hovered = ImGui.IsItemHovered();
            ImGui.SetCursorScreenPos(pos);
            var fProfile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
            SpheneUI.Media.Avatar(FriendListAvatar(pair.UserData.UID, fProfile), avatarSize);
            SpheneUI.Media.PresenceDot(drawList, pos, avatarSize,
                SpheneUI.Palette.NetworkActive);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                drawList.AddCircle(pos + new Vector2(avatarSize * 0.5f),
                    avatarSize * 0.5f - 0.5f,
                    SpheneUI.ColorUtils.ToU32(CommunityAccent), 0, 1.5f * scale);
                SpheneUI.AttachTooltip($"{pair.UserData.AliasOrUID} · online");
            }
            if (clicked)
            {
                NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair));
                return;
            }
            drawn++;
        }

        var textX = rowTop.X + drawn * (avatarSize + gap);
        var rest = online - drawn;
        if (rest > 0)
        {
            ImGui.SetCursorScreenPos(new Vector2(textX,
                rowTop.Y + (avatarSize - SpheneUI.Layout.BadgeHeight) * 0.5f));
            SpheneUI.Layout.Badge($"+{rest}", SpheneUI.Colors.TextMuted);
            textX += chipW;
        }
        var countText = online == 1 ? "1 friend online" : $"{online} friends online";
        DrawHeroTextLink(new Vector2(textX,
                rowTop.Y + (avatarSize - ImGui.GetTextLineHeight()) * 0.5f),
            countText, "Open your friends list", () => SetPage(SpheneHubPage.Friends));
        ImGui.SetCursorScreenPos(new Vector2(rowTop.X, rowTop.Y + avatarSize));
    }

    /// <summary>
    /// Muted inline text with a text-width-only invisible button: hover lifts
    /// the background like a toggle row · no visible chrome, still clickable.
    /// </summary>
    private static void DrawHeroTextLink(Vector2 pos, string text, string tooltip, Action onClick)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var textSize = ImGui.CalcTextSize(text);
        var pad = new Vector2(4f * scale, 1f * scale);
        ImGui.SetCursorScreenPos(pos - new Vector2(pad.X, pad.Y));
        var clicked = ImGui.InvisibleButton($"##hero-link-{text}",
            textSize + pad * 2f);
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            drawList.AddRectFilled(pos - pad, pos + textSize + pad,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    SpheneUI.Colors.TextPrimary, 0.07f)), 4f * scale);
        }
        ImGui.SetCursorScreenPos(pos);
        SpheneUI.Text.Colored(text, hovered
            ? SpheneUI.Colors.TextSecondary
            : SpheneUI.Colors.TextMuted);
        SpheneUI.AttachTooltip(tooltip);
        if (clicked)
        {
            onClick();
        }
    }

    private static Vector4 CommunityAccent => SpheneUI.Theme.Current.AccentBlue;

    /// <summary>Page help shared by the nav rail's "?" dock and the page's
    /// own registration · one source so both affordances show the same
    /// flyout.</summary>
    private static void HomeHelpContent()
    {
        SpheneUI.Text.Colored("Home", SpheneUI.Colors.TextPrimary);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        SpheneUI.Text.MutedWrapped(
            "Your profile at a glance - connection status, presence and news. " +
            "Everything else lives in the rail on the left.",
            ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
    }

    /// <summary>See <see cref="HomeHelpContent"/> · Friends flyout.</summary>
    private static void FriendsHelpContent()
    {
        SpheneUI.Text.Colored("Friends", SpheneUI.Colors.TextPrimary);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        SpheneUI.Text.MutedWrapped(
            "Stay connected with your friends across Eorzea.",
            ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
    }

    /// <summary>See <see cref="HomeHelpContent"/> · News flyout.</summary>
    private static void NewsHelpContent()
    {
        SpheneUI.Text.Colored("News & Updates", SpheneUI.Colors.TextPrimary);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        SpheneUI.Text.MutedWrapped(
            "Latest information, announcements and community news about Sphene.",
            ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
    }

    /// <summary>See <see cref="HomeHelpContent"/> · Syncshells flyout.</summary>
    private static void SyncshellsHelpContent()
    {
        SpheneUI.Text.Colored("Syncshells", SpheneUI.Colors.TextPrimary);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        SpheneUI.Text.MutedWrapped(
            "Syncshells keep your appearance synchronized with a whole group - " +
            "everyone inside sees each other's mods automatically.\n\n" +
            "Managed by you - shells you own or moderate.\n" +
            "Member of - shells you joined.\n" +
            "Area-bound - join automatically inside their location.\n" +
            "Discover - public communities you can join.",
            ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
    }

    private void DrawFriendsPage(Vector4 accent)
    {
        if (_viewingFriend != null && !IsStillPaired(_viewingFriend))
        {
            ApplyLocation(new HubLocation(SpheneHubPage.Friends, null, null));
        }
        if (_viewingFriend is { } friend)
        {
            DrawFriendView(friend, accent);
            return;
        }
        SpheneUI.Layout.RegisterPageHelp("friends", FriendsHelpContent);

        var scale = SpheneUI.Scale;
        if (_friendsTab == 1)
        {
            DrawFriendRequestCards(_friendsIncoming, accent);
            return;
        }
        if (_friendsTab == 2)
        {
            DrawPendingOutCards(_friendsOutgoing);
            return;
        }
        if (_friendsTab == 3)
        {
            DrawBlockedList();
            return;
        }
        if (_friendsTab == 4)
        {
            DrawFriendsNotesPage(accent);
            return;
        }
        if (_friendsTab == 5)
        {
            DrawFriendsWhitelistPage(accent);
            return;
        }

        // Friends tab · requests first like the mock, then the list
        if (_friendsIncoming.Count > 0)
        {
            DrawFriendRequestCards(_friendsIncoming, accent, cap: 3);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        if (_friendsDirect.Count == 0 && _friendsGroupOnly.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserFriends,
                "No friends yet",
                "Pair with someone and they will show up here with live presence.",
                "Pair requests", () => Mediator.Publish(new OpenPairRequestsUiMessage()),
                FontAwesomeIcon.UserPlus, accent);
            return;
        }

        var filter = _friendSearch.Trim();
        var rowGap = 2f * scale;
        var drawnAny = DrawFriendSection("Directly Paired", rowGap,
            _friendsDirect, filter);
        drawnAny |= DrawFriendSection("Via Syncshells", rowGap,
            _friendsGroupOnly.OrderBy(p => p.UserData.AliasOrUID,
                StringComparer.OrdinalIgnoreCase), filter);
        if (!drawnAny)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Search,
                "No matches", $"Nobody in your list matches \"{filter}\".");
        }
    }

    /// <summary>
    /// Fixed toolbar of the Friends list · search + Add Friend row and the
    /// section chips. Runs in the nav content's fixed zone and stages the
    /// request/pair snapshots the scrollable body then renders.
    /// </summary>
    private void DrawFriendsToolbar(Vector4 accent)
    {
        _friendsIncoming = _pairManager.GetInboundIndividualPairRequestsSnapshot();
        _friendsOutgoing = _pairManager.GetOutboundIndividualPairRequestsSnapshot();
        var blocked = _configService.Current.BlockedUserUids;
        if (!_blockedSynced)
        {
            _blockedSynced = true;
            _ = SyncBlockedAsync();
        }
        _friendsDirect = _pairManager.DirectPairs;

        // search + add · one row under the header
        var scale = SpheneUI.Scale;
        var addW = 130f * scale;
        var searchW = SpheneUI.Controls.BoundedAvailX() - addW
            - SpheneUI.Spacing.X8.Scaled;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(MathF.Min(searchW, avail - addW
            - SpheneUI.Spacing.X8.Scaled));
        ImGui.InputTextWithHint("##friendsearch", "Search friends by name…",
            ref _friendSearch, 64);
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        if (SpheneUI.Controls.SegmentButton("Add Friend##addfr",
                icon: FontAwesomeIcon.UserPlus,
                size: new Vector2(addW, ImGui.GetFrameHeight()), accent: accent,
                active: true))
        {
            Mediator.Publish(new OpenPairRequestsUiMessage());
        }
        SpheneUI.AttachTooltip("Open pair requests to add a friend");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        // filter chips with counts + icons · wraps to a second row before
        // any chip would overhang, so the minimum window width stays clean
        _friendsGroupOnly.Clear();
        foreach (var p in _pairManager.PairsWithGroups.Keys)
        {
            if (!p.IsDirectlyPaired)
            {
                _friendsGroupOnly.Add(p);
            }
        }
        var whitelistCount = _playerPerformanceConfigService.Current.UIDsToIgnore.Count;
        var chipDefs = new (FontAwesomeIcon Icon, string Label, int Tab)[]
        {
            (FontAwesomeIcon.UserFriends, $"Friends ({_friendsDirect.Count + _friendsGroupOnly.Count})", 0),
            (FontAwesomeIcon.UserPlus, $"Requests ({_friendsIncoming.Count})", 1),
            (FontAwesomeIcon.HourglassHalf, $"Pending ({_friendsOutgoing.Count})", 2),
            (FontAwesomeIcon.Ban, $"Blocked ({blocked.Count})", 3),
            (FontAwesomeIcon.StickyNote, "Notes", 4),
            (FontAwesomeIcon.CheckCircle, $"Whitelist ({whitelistCount})", 5),
        };
        var chipGap = SpheneUI.Spacing.X8.Scaled;
        var chipH = ImGui.GetFrameHeight() + 2f * scale;
        var chipsRight = ImGui.GetCursorPosX() + SpheneUI.Controls.BoundedAvailX();
        for (var i = 0; i < chipDefs.Length; i++)
        {
            var (icon, label, tab) = chipDefs[i];
            if (i > 0
                && ImGui.GetCursorPosX()
                    + SpheneUI.Controls.SegAutoWidth(label, icon, chipH) <= chipsRight)
            {
                ImGui.SameLine(0f, chipGap);
            }
            var active = _friendsTab == tab;
            if (SpheneUI.Controls.SegmentButton($"{label}##ftab{i}",
                    icon: icon, size: new Vector2(0, chipH),
                    accent: accent, active: active))
            {
                _friendsTab = tab;
            }
        }
    }

    private int _friendsTab;
    private IReadOnlyList<Pair> _friendsIncoming = [];
    private IReadOnlyList<Pair> _friendsOutgoing = [];
    private IReadOnlyList<Pair> _friendsDirect = [];
    private readonly List<Pair> _friendsGroupOnly = [];
    private string _friendSearch = string.Empty;
    private bool? _notesSuccessfullyApplied;
    private bool _notesOverwriteExistingLabels;
    private string _friendWhitelistUid = string.Empty;

    /// <summary>
    /// The Notes sub-page of Friends · pair-note backup transfer plus the
    /// defaults that decide when a note popup appears for a new pair.
    /// </summary>
    private void DrawFriendsNotesPage(Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            var overwrite = _notesOverwriteExistingLabels;
            if (SpheneUI.Controls.PrimaryButton("Export Notes", FontAwesomeIcon.FileExport))
            {
                ImGui.SetClipboardText(UiSharedService.GetNotes(
                    _pairManager.DirectPairs.UnionBy(
                        _pairManager.GroupPairs.SelectMany(p => p.Value),
                        p => p.UserData,
                        UserDataComparer.Instance).ToList()));
            }
            ImGui.SameLine();
            if (SpheneUI.Controls.PrimaryButton("Import Notes", FontAwesomeIcon.FileImport))
            {
                _notesSuccessfullyApplied = null;
                var notes = ImGui.GetClipboardText();
                _notesSuccessfullyApplied = _uiShared.ApplyNotesFromClipboard(notes, overwrite);
            }
            ImGui.SameLine();
            PeopleNotesOptionBlock.DrawOverwriteExistingLabelsOption(ref overwrite, _uiShared,
                "FriendsNotesOverwriteExistingLabels");
            _notesOverwriteExistingLabels = overwrite;

            if (_notesSuccessfullyApplied is not null)
            {
                SpheneUI.Layout.VerticalSpace();
                if (_notesSuccessfullyApplied.Value)
                {
                    SpheneUI.Text.Colored("Notes successfully applied.", ImGuiColors.ParsedGreen);
                }
                else
                {
                    SpheneUI.Text.Colored("Failed to apply notes.", ImGuiColors.DalamudRed);
                }
            }
        }, "Note Transfer", accent,
            "Copy pair notes between clients or restore a backup - the export goes to your clipboard.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.SectionCard(() =>
        {
            PeopleNotesOptionBlock.DrawOpenNotesPopupOnUserAdditionOption(_configService, _uiShared,
                "FriendsNotesOpenNotesPopupOnUserAddition");
            PeopleNotesOptionBlock.DrawAutoPopulateNotesUsingPlayerNamesOption(_configService, _uiShared,
                "FriendsNotesAutoPopulateNotesUsingPlayerNames");
        }, "Labels & Popups", accent);
    }

    /// <summary>
    /// The Whitelist sub-page of Friends · the raw handle list backing the
    /// per-friend shield toggle on the rows, for users not in your pair list.
    /// </summary>
    private void DrawFriendsWhitelistPage(Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        SpheneUI.Layout.SectionCard(() =>
        {
            var addUidTrailing = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Plus, "Add handle to whitelist")
                + ImGui.GetStyle().ItemSpacing.X + SpheneUI.Icons.Size(FontAwesomeIcon.QuestionCircle).X
                + ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX() - addUidTrailing);
            SpheneUI.Controls.InputText("##friendwhitelistuid", ref _friendWhitelistUid, 20);
            ImGui.SameLine();
            using (ImRaii.Disabled(string.IsNullOrEmpty(_friendWhitelistUid)))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add handle to whitelist"))
                {
                    if (!_playerPerformanceConfigService.Current.UIDsToIgnore.Contains(
                            _friendWhitelistUid, StringComparer.Ordinal))
                    {
                        _playerPerformanceConfigService.Current.UIDsToIgnore.Add(_friendWhitelistUid);
                        _playerPerformanceConfigService.Save();
                    }
                    _friendWhitelistUid = string.Empty;
                }
            }
            SpheneUI.Controls.HelpText("Hint: handles are case sensitive.");
            var playerList = _playerPerformanceConfigService.Current.UIDsToIgnore;
            if (playerList.Count == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.CheckCircle,
                    "Whitelist empty",
                    "Toggle the gauge on a friend row or add a handle above - whitelisted users are ignored for performance warnings and auto-pause.");
                return;
            }
            var filter = _friendSearch.Trim();
            var rowGap = 2f * scale;
            var drawn = 0;
            for (var i = 0; i < playerList.Count; i++)
            {
                var entry = playerList[i];
                if (filter.Length > 0 && !HandleEntryMatches(entry, filter))
                {
                    continue;
                }
                DrawWhitelistRow(entry, rowGap);
                drawn++;
            }
            if (drawn == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Search,
                    "No matches", $"Nobody on the whitelist matches \"{filter}\".");
            }
        }, "Performance Whitelist", accent,
            "Whitelisted users are ignored for performance warnings and auto-pause - toggle a friend directly from their row or manage handles here.");
    }

    /// <summary>Resolves a handle entry (UID or alias) to a known pair,
    /// or null when it belongs to nobody in the pair list.</summary>
    private Pair? ResolvePairByHandle(string entry)
    {
        var pair = _pairManager.GetPairByUID(entry);
        if (pair != null)
        {
            return pair;
        }
        foreach (var candidate in _pairManager.PairsWithGroups.Keys)
        {
            if (string.Equals(candidate.UserData.Alias, entry,
                    StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        return null;
    }

    private bool HandleEntryMatches(string entry, string filter)
    {
        if (entry.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var pair = ResolvePairByHandle(entry);
        return pair != null
            && (pair.UserData.AliasOrUID.Contains(filter,
                    StringComparison.OrdinalIgnoreCase)
                || (pair.PlayerName?.Contains(filter,
                    StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>
    /// One whitelist entry as a player row - the same <see cref="SpheneUI.Profile.FriendRow"/>
    /// renderer the friends list uses, with pair presence when the handle
    /// resolves and a raw-handle fallback when it does not.
    /// </summary>
    private void DrawWhitelistRow(string entry, float rowGap)
        => DrawUserHandleRow(entry, ResolvePairByHandle(entry),
            "Whitelisted · not in your pairs", FontAwesomeIcon.Trash,
            $"Remove {entry} from the performance whitelist",
            () =>
            {
                _playerPerformanceConfigService.Current.UIDsToIgnore
                    .RemoveAll(u => string.Equals(u, entry, StringComparison.Ordinal));
                _playerPerformanceConfigService.Save();
            },
            rowGap, navigateOnClick: true);

    /// <summary>
    /// One blocked UID as a player row - same renderer and resolution as the
    /// whitelist rows, with live pair presence when the user is still known
    /// and an Unblock action instead of a delete.
    /// </summary>
    private void DrawBlockedRow(string uid, float rowGap)
        => DrawUserHandleRow(uid, ResolvePairByHandle(uid), "Blocked",
            FontAwesomeIcon.Unlock,
            $"Unblock {uid}",
            () =>
            {
                _configService.Current.BlockedUserUids.Remove(uid);
                _configService.Save();
                _ = _apiController.UserUnblock(new UserDto(new UserData(uid)));
            },
            rowGap, navigateOnClick: false);

    /// <summary>
    /// Shared player-row renderer for handle-based lists (whitelist, blocked)
    /// - avatar, resolved name/presence when a pair exists, raw-handle
    /// fallback otherwise, one trailing action button.
    /// </summary>
    private void DrawUserHandleRow(string entry, Pair? pair, string unresolvedPresence,
        FontAwesomeIcon actionIcon, string actionTooltip, Action onAction,
        float rowGap, bool navigateOnClick)
    {
        var scale = SpheneUI.Scale;
        var rowH = 46f * scale;
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();

        var actPad = 10f * scale;
        var actBtn = ImGui.GetFrameHeight();
        var actionsW = actPad + actBtn;

        var userData = pair?.UserData ?? new UserData(entry);
        var rowKey = userData.UID;

        ImGui.SetCursorScreenPos(pos);
        var rowId = ImGui.GetID($"##handle-row-{rowKey}");
        var clicked = ImGui.InvisibleButton($"##handle-row-{rowKey}",
            new Vector2(width - actionsW, rowH));
        var hovered = ImGui.IsItemHovered();
        var t = SpheneUI.Fx.HoverT(rowId, hovered);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var profile = _spheneProfileManager.GetSpheneProfile(userData);
        var accent = profile.ProfileTheme.Value.AccentColor(SpheneUI.Palette.SpheneGold);
        var rowAnim = userData.IsSupporter && profile.RowImageData.Value.Length > 0
            ? GetFriendRowImage(rowKey, profile.RowImageData.Value)
            : null;

        string presence;
        Vector4 presenceColor;
        string? statusMessage = profile.StatusMessage;
        if (pair != null)
        {
            presenceColor = pair.IsMutuallyVisible || pair.IsOnline
                ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted;
            presence = $"\u25CF {(pair.IsMutuallyVisible ? "Visible" : pair.IsOnline ? "Online" : "Offline")}";
            var worldName = FriendWorldName(pair);
            if (worldName.Length > 0)
            {
                presence += " · " + worldName;
            }
            var note = pair.GetNote();
            if (!string.IsNullOrEmpty(note))
            {
                statusMessage = note;
            }
        }
        else
        {
            presence = unresolvedPresence;
            presenceColor = SpheneUI.Colors.TextMuted;
        }

        SpheneUI.Profile.FriendRow(drawList, pos, width, rowH,
            new SpheneUI.Profile.FriendRowContent(
                FriendListAvatar(rowKey, profile),
                pair?.UserData.PreferredName ?? entry,
                presence, presenceColor, statusMessage, profile.IsNSFW),
            accent, hovered, t, profile.ProfileTheme.Value, rowAnim,
            8f * scale);

        if (clicked && navigateOnClick && pair != null)
        {
            NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair));
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - actPad - actBtn,
            pos.Y + (rowH - actBtn) * 0.5f));
        // Icons.Button seeds its widget id from the glyph alone - every row
        // sharing the same action icon would collide on one id, so the
        // button runs under the row's own scope (same fix the friend strip
        // uses); width keeps the widget inside the reserved action column.
        using var handleActionScope = ImRaii.PushId($"handle-actions-{rowKey}");
        if (SpheneUI.Icons.Button(actionIcon, height: actBtn, width: actBtn))
        {
            onAction();
        }
        SpheneUI.AttachTooltip(actionTooltip);
        ImGui.SetCursorScreenPos(pos + new Vector2(0f, rowH + rowGap));
    }


    /// <summary>
    /// Incoming pair requests as cards - avatar, name, status and
    /// Accept/Decline actions · mirrors the mock's request tiles.
    /// </summary>
    private void DrawFriendRequestCards(IReadOnlyList<Pair> incoming,
        Vector4 accent, int cap = 0)
    {
        var shown = new List<Pair>(cap > 0 ? cap : incoming.Count);
        foreach (var p in incoming)
        {
            if (!IsBlocked(p.UserData.UID))
            {
                shown.Add(p);
            }
            if (cap > 0 && shown.Count == cap)
            {
                break;
            }
        }
        if (shown.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserCheck,
                "No requests", "Incoming pair requests land here.");
            return;
        }
        SpheneUI.Text.SectionHeader($"Friend Requests ({incoming.Count})");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Layout.CardGrid(shown, 190f * SpheneUI.Scale,
            (pair, cellW) => DrawRequestCard(pair, cellW, accent));
    }

    private void DrawRequestCard(Pair pair, float cellW, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var pad = SpheneUI.Spacing.X8.Scaled;
        var avatarSz = 44f * scale;
        var btnH = ImGui.GetFrameHeight() + 2f * scale;
        var cardH = pad * 2 + avatarSz + pad + ImGui.GetTextLineHeight() * 3
            + pad + btnH;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        // The card button must not cover the accept/decline strip - the
        // first-submitted widget claims the hover id and would swallow
        // every click meant for the two action buttons.
        var clicked = ImGui.InvisibleButton($"##req{pair.UserData.UID}",
            new Vector2(cellW, cardH - pad - btnH - 2f * scale));
        var hovered = ImGui.IsItemHovered();
        var min = pos;
        var max = pos + new Vector2(cellW, cardH);

        dl.AddRectFilled(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()),
            10f * scale);
        SpheneUI.Fx.HoverLift(dl, min, max,
            SpheneUI.Fx.HoverLiftT(ImGui.GetID($"##reqcard{pair.UserData.UID}"), min, max),
            10f * scale, accent);
        dl.AddRect(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                hovered ? accent : SpheneUI.Colors.Border, 0.4f)), 10f * scale);

        var profile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        var avatarPos = new Vector2(min.X + (cellW - avatarSz) * 0.5f,
            min.Y + pad);
        ImGui.SetCursorScreenPos(avatarPos);
        SpheneUI.Media.Avatar(
            FriendListAvatar(pair.UserData.UID, profile), avatarSz);
        if (pair.UserData.IsSupporter)
        {
            dl.AddText(new Vector2(avatarPos.X + avatarSz - 10f * scale,
                    avatarPos.Y - 4f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Palette.SpheneGold),
                FontAwesomeIcon.Crown.ToIconString());
        }

        var name = !string.IsNullOrEmpty(pair.PlayerName)
            ? pair.PlayerName : pair.UserData.AliasOrUID;
        var nameW = ImGui.CalcTextSize(name).X;
        dl.AddText(new Vector2(min.X + (cellW - nameW) * 0.5f,
                avatarPos.Y + avatarSz + 6f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), name);
        var sub = FriendWorldName(pair);
        if (sub.Length == 0)
        {
            sub = pair.UserData.AliasOrUID;
        }
        var subW = ImGui.CalcTextSize(sub).X;
        dl.AddText(new Vector2(min.X + (cellW - subW) * 0.5f,
                avatarPos.Y + avatarSz + 6f * scale + ImGui.GetTextLineHeight()),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), sub);
        var cardMsg = profile.StatusMessage;
        if (!string.IsNullOrWhiteSpace(cardMsg))
        {
            var msg = SpheneUI.Text.Ellipsize("“" + cardMsg + "”", cellW - pad * 2);
            var msgW = ImGui.CalcTextSize(msg).X;
            dl.AddText(new Vector2(min.X + (cellW - msgW) * 0.5f,
                    avatarPos.Y + avatarSz + 6f * scale
                    + ImGui.GetTextLineHeight() * 2),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), msg);
        }

        var half = (cellW - pad * 2 - SpheneUI.Spacing.X4.Scaled) * 0.5f;
        var btnY = max.Y - pad - btnH;
        ImGui.SetCursorScreenPos(new Vector2(min.X + pad, btnY));
        if (SpheneUI.Controls.SegmentButton($"Accept##acc{pair.UserData.UID}",
                size: new Vector2(half, btnH), accent: accent, active: true))
        {
            _pairManager.MarkInboundIndividualPairRequestSeen(pair.UserData.UID);
            _ = _apiController.UserAddPair(new(pair.UserData));
        }
        ImGui.SetCursorScreenPos(new Vector2(min.X + pad + half
            + SpheneUI.Spacing.X4.Scaled, btnY));
        if (SpheneUI.Controls.SegmentButton($"Decline##dec{pair.UserData.UID}",
                size: new Vector2(half, btnH)))
        {
            _pairManager.DeclineInboundIndividualPairRequest(pair.UserData.UID);
        }
        if (clicked && !ImGui.IsItemHovered()) { /* card click opens profile */ }
        ImGui.SetCursorScreenPos(new Vector2(pos.X, max.Y));
    }

    /// <summary>
    /// Blocked UIDs - local list, request entries and friends carrying a
    /// blocked UID are hidden elsewhere. Unblock restores them.
    /// </summary>
    private void DrawBlockedList()
    {
        var blocked = _configService.Current.BlockedUserUids;
        SpheneUI.Text.SectionHeader($"Blocked ({blocked.Count})");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        if (blocked.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Ban, "Nobody blocked",
                "Block someone from their friend row and they land here.");
            return;
        }
        var filter = _friendSearch.Trim();
        var rowGap = 2f * SpheneUI.Scale;
        var drawn = 0;
        for (var i = blocked.Count - 1; i >= 0; i--)
        {
            var uid = blocked[i];
            if (filter.Length > 0 && !HandleEntryMatches(uid, filter))
            {
                continue;
            }
            DrawBlockedRow(uid, rowGap);
            drawn++;
        }
        if (drawn == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Search,
                "No matches", $"Nobody on the blocked list matches \"{filter}\".");
        }
    }

    private void DrawPendingOutCards(IReadOnlyList<Pair> outgoing)
    {
        SpheneUI.Text.SectionHeader($"Pending ({outgoing.Count})");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        if (outgoing.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Clock,
                "Nothing pending", "Requests you sent land here while unanswered.");
            return;
        }
        foreach (var pair in outgoing)
        {
            DrawFriendRow(pair, 2f * SpheneUI.Scale);
        }
    }

    private bool DrawFriendSection(string title, float rowGap,
        IEnumerable<Pair> pairs, string filter = "")
    {
        var any = false;
        // Online friends first, then the rest · both passes keep list order.
        foreach (var onlinePass in new[] { true, false })
        {
            foreach (var pair in pairs)
            {
                if (pair.IsOnline != onlinePass)
                {
                    continue;
                }
                if (filter.Length > 0
                    && !pair.UserData.AliasOrUID.Contains(filter,
                        StringComparison.OrdinalIgnoreCase)
                    && !(pair.PlayerName?.Contains(filter,
                        StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }
                if (!any)
                {
                    SpheneUI.Text.Muted(title);
                    any = true;
                }
                DrawFriendRow(pair, rowGap);
            }
        }
        return any;
    }

    private bool _blockedSynced;

    private async Task SyncBlockedAsync()
    {
        try
        {
            var remote = await _apiController.UserGetBlockedUsers()
                .ConfigureAwait(false);
            var list = _configService.Current.BlockedUserUids;
            foreach (var dto in remote)
            {
                if (!list.Contains(dto.User.UID, StringComparer.Ordinal))
                {
                    list.Add(dto.User.UID);
                }
            }
            _configService.Save();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Blocked-user sync failed");
        }
    }

    private bool IsBlocked(string uid)
        => _configService.Current.BlockedUserUids.Contains(uid,
            StringComparer.Ordinal);

    private void DrawFriendRow(Pair pair, float rowGap)
    {
        if (IsBlocked(pair.UserData.UID))
        {
            return;
        }
        var scale = SpheneUI.Scale;
        var rowH = 46f * scale;
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();

        // The row button must stop short of the action strip - an
        // InvisibleButton submitted first would claim the hover id and
        // swallow every click meant for the overlay buttons.
        var actPad = 10f * scale;
        var actGap = 6f * scale;
        var actBtn = ImGui.GetFrameHeight();
        var actionsW = actPad + actBtn * 3f + actGap * 2f;

        ImGui.SetCursorScreenPos(pos);
        var rowId = ImGui.GetID($"##friend-{pair.UserData.UID}");
        var clicked = ImGui.InvisibleButton($"##friend-{pair.UserData.UID}",
            new Vector2(width - actionsW, rowH));
        var hovered = ImGui.IsItemHovered();
        var t = SpheneUI.Fx.HoverT(rowId, hovered);

        var profile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        // the friend's own accent drives their hover glow and avatar ring
        var friendAccent = profile.ProfileTheme.Value.AccentColor(SpheneUI.Palette.SpheneGold);

        // the full friends-list row via the shared renderer · identical to
        // what the style preview shows for your own entry
        var friendRowAnim = pair.UserData.IsSupporter && profile.RowImageData.Value.Length > 0
            ? GetFriendRowImage(pair.UserData.UID, profile.RowImageData.Value)
            : null;
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var statusColor = pair.IsMutuallyVisible || pair.IsOnline
            ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted;
        var worldName = FriendWorldName(pair);
        var statusText = pair.IsMutuallyVisible ? "Visible" : pair.IsOnline ? "Online" : "Offline";
        if (worldName.Length > 0)
        {
            statusText += " · " + worldName;
        }
        SpheneUI.Profile.FriendRow(drawList, pos, width, rowH,
            new SpheneUI.Profile.FriendRowContent(
                FriendListAvatar(pair.UserData.UID, profile), pair.UserData.PreferredName,
                $"\u25CF {statusText}", statusColor, profile.StatusMessage, profile.IsNSFW),
            friendAccent, hovered, t, profile.ProfileTheme.Value, friendRowAnim,
            8f * scale);

        if (clicked)
        {
            NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair));
        }
        // DM shortcut as the first overlay action - an accent dot marks
        // unread conversations so the list itself signals pending chats.
        // Icons.Button seeds its widget id from the glyph alone, so the whole
        // strip runs under the friend's UID scope - without it every row
        // shares the same ids and only the topmost buttons respond.
        var hasUnreadChat = _chatService.HasUnread(pair.UserData.UID);
        using var actionScope = ImRaii.PushId($"friend-actions-{pair.UserData.UID}");
        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - actPad - actBtn * 3f - actGap * 2f,
            pos.Y + (rowH - actBtn) * 0.5f));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.CommentDots,
                height: actBtn, width: actBtn))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair,
                (int)FriendHubSubPage.Chat));
        }
        SpheneUI.AttachTooltip(hasUnreadChat
            ? $"Unread messages from {pair.UserData.PreferredName} - open chat"
            : $"Message {pair.UserData.PreferredName}");
        if (hasUnreadChat)
        {
            var chatBtnMax = ImGui.GetItemRectMax();
            var pipR = 3f * scale;
            drawList.AddCircleFilled(
                new Vector2(chatBtnMax.X - pipR - 1f * scale,
                    chatBtnMax.Y - pipR * 2f), pipR,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Palette.SpheneGold));
        }

        // whitelist + block actions overlaid at the row's right edge - the
        // gauge reads as a status: fixed neutral pill off, status green on
        var banH = actBtn;
        var uidsToIgnore = _playerPerformanceConfigService.Current.UIDsToIgnore;
        var whitelisted = uidsToIgnore.Contains(pair.UserData.UID, StringComparer.Ordinal)
            || (!string.IsNullOrEmpty(pair.UserData.Alias)
                && uidsToIgnore.Contains(pair.UserData.Alias!, StringComparer.Ordinal));
        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - actPad - banH * 2f - actGap,
            pos.Y + (rowH - banH) * 0.5f));
        if (SpheneUI.Controls.SegmentButton($"##friendwl{pair.UserData.UID}",
                FontAwesomeIcon.TachometerAlt, size: new Vector2(banH, banH),
                accent: SpheneUI.Palette.NetworkActive, active: whitelisted))
        {
            if (whitelisted)
            {
                uidsToIgnore.RemoveAll(u =>
                    string.Equals(u, pair.UserData.Alias, StringComparison.Ordinal)
                    || string.Equals(u, pair.UserData.UID, StringComparison.Ordinal));
            }
            else
            {
                uidsToIgnore.Add(!string.IsNullOrEmpty(pair.UserData.Alias)
                    ? pair.UserData.Alias! : pair.UserData.UID);
            }
            _playerPerformanceConfigService.Save();
        }
        SpheneUI.AttachTooltip(whitelisted
            ? $"Remove {pair.UserData.AliasOrUID} from the performance whitelist"
            : $"Whitelist {pair.UserData.AliasOrUID} - ignores performance warnings and auto-pause");
        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - actPad - banH,
            pos.Y + (rowH - banH) * 0.5f));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Ban,
                height: actBtn, width: actBtn))
        {
            _configService.Current.BlockedUserUids.Add(pair.UserData.UID);
            _configService.Save();
            _ = _apiController.UserBlock(new UserDto(pair.UserData));
        }
        SpheneUI.AttachTooltip($"Block {pair.UserData.AliasOrUID}");
        ImGui.SetCursorScreenPos(pos + new Vector2(0f, rowH + rowGap));
    }

    private int _newsFilter;
    private long? _viewingAnnouncementId;
    private bool _announcementEditMode;

    // Admin editor buffers · seeded once per editing target, submitted via the
    // hub admin methods. _editorSeededId = which announcement the buffers
    // belong to (-1 = new draft, long.MinValue = not seeded yet).
    private long _editorSeededId = long.MinValue;
    private string _editorTitle = string.Empty;
    private int _editorCategory;
    private string _editorSummary = string.Empty;
    private string _editorBody = string.Empty;
    private string _editorBanner = string.Empty;
    private string _editorLink = string.Empty;
    private bool _editorPinned;
    private bool _editorDraft = true;
    private bool _editorBusy;
    private bool _editorSuccess;
    private string? _editorError;
    private readonly SpheneUI.Popups.ConfirmDialog _announcementDeleteConfirm = new();

    private static readonly string[] AnnouncementCategories =
        ["news", "event", "maintenance", "patch"];

    /// <summary>
    /// News & Updates - Sphene-side announcements: plugin releases,
    /// server notices and community feed highlights as media cards with
    /// badges, like the mock's news wall.
    /// </summary>
    /// <summary>Fixed chrome of News &amp; Updates · release-art hero banner
    /// (identity overlay = the designed page banner) plus the kind filter
    /// chips and the staff create chip. Drawn in the nav content's fixed
    /// zone so it stays put while the article list scrolls.</summary>
    private void DrawNewsChrome(Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var isStaff = _apiController.IsAdmin || _apiController.IsModerator;
        if (_newsChangelog == null)
        {
            _newsChangelog = [];
            _ = LoadChangelogAsync();
        }

        SpheneUI.Layout.RegisterPageHelp("news", NewsHelpContent);

        // hero banner · newest release art with the page identity overlaid,
        // like the release-notes banner (mockup's branded page header).
        var heroUrl = _newsChangelog.FirstOrDefault(
            e => !string.IsNullOrWhiteSpace(e.Banner))?.Banner;
        var heroBytes = heroUrl != null
            ? _changelogService.GetCachedImage(heroUrl) : null;
        var heroWrap = heroBytes != null
            ? SpheneUI.Media.Texture($"clb:{heroUrl}", () => heroBytes) : null;
        if (heroWrap != null)
        {
            var dl0 = ImGui.GetWindowDrawList();
            var bw = ImGui.GetContentRegionAvail().X;
            var bh = MathF.Min(bw * 0.20f, 120f * scale);
            ImGui.Dummy(new Vector2(bw, bh));
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var rnd = 10f * scale;
            SpheneUI.Media.ImageInRect(dl0, heroWrap, min, max, ImageFit.Cover,
                rounding: rnd);
            var darkL = new Vector4(0f, 0f, 0f, 0.85f);
            var clear = new Vector4(0f, 0f, 0f, 0f);
            SpheneUI.Media.FillRoundedGradient(dl0, min,
                new Vector2(min.X + bw * 0.7f, max.Y), darkL, clear, clear, darkL,
                rnd, ImDrawFlags.RoundCornersLeft);
            dl0.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(accent, 0.35f)), rnd);

            var pad2 = 16f * scale;
            var lineH0 = ImGui.GetTextLineHeight();
            var font = ImGui.GetFont();
            var titleSize = lineH0 * 1.7f;
            var chip = titleSize * 1.3f;
            var blockH = titleSize + lineH0 * 1.5f;
            var ty = min.Y + (bh - blockH) * 0.55f;
            var chipMin = new Vector2(min.X + pad2, ty + (titleSize - chip) * 0.5f);
            SpheneUI.Layout.SlotIcon(dl0, chipMin, chip,
                FontAwesomeIcon.Bullhorn, new Vector4(1f, 1f, 1f, 0.92f));
            var tx0 = chipMin.X + chip + 12f * scale;
            dl0.AddText(font, titleSize, new Vector2(tx0, ty),
                SpheneUI.ColorUtils.ToU32(Vector4.One), "News & Updates");
            dl0.AddText(new Vector2(tx0, ty + titleSize + lineH0 * 0.4f),
                SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.82f)),
                "Latest information, announcements and community news about Sphene.");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        // filter chips · icon + label sized to content (labels never clip)
        (string Label, FontAwesomeIcon Icon)[] chips =
        [
            ("All", FontAwesomeIcon.LayerGroup),
            ("Plugin Updates", FontAwesomeIcon.SyncAlt),
            ("Announcements", FontAwesomeIcon.Bullhorn),
        ];
        for (var i = 0; i < chips.Length; i++)
        {
            if (SpheneUI.Controls.SegmentButton(chips[i].Label + $"##nf{i}",
                    chips[i].Icon, accent: accent, active: _newsFilter == i))
            {
                _newsFilter = i;
            }
            if (i + 1 < chips.Length)
            {
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            }
        }

        // admin entry · editors get a create chip next to the filters
        if (isStaff)
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            if (SpheneUI.Controls.SegmentButton("+ New##ann-new",
                    FontAwesomeIcon.Plus, accent: accent))
            {
                NavigateTo(new HubLocation(SpheneHubPage.News, null, null,
                    AnnouncementId: -1L, AnnouncementEdit: true));
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    private void DrawNewsPage(Vector4 accent)
    {
        _communityFeed.EnsureRequested();
        _announcements.EnsureRequested();
        var isStaff = _apiController.IsAdmin || _apiController.IsModerator;
        if (isStaff)
        {
            _announcements.EnsureStaffRequested();
        }

        // Detail branch · the reader/editor replace the feed so back/forward
        // lands on the article, not on the list.
        if (_announcementEditMode && (_apiController.IsAdmin || _apiController.IsModerator))
        {
            DrawAnnouncementEditor(accent);
            _announcementDeleteConfirm.Draw("announcement-delete",
                "Delete announcement?",
                "This removes the article for every client. Draft or published, it cannot be restored.",
                confirmLabel: "Delete", danger: true,
                icon: FontAwesomeIcon.TrashAlt);
            return;
        }
        if (_viewingAnnouncementId is { } readingId)
        {
            DrawAnnouncementReader(readingId, accent);
            return;
        }

        var logoWrap = SpheneBrandIcons.SpheneLogo();

        // unified list, newest first · every card carries a real date so
        // ordering stays honest across kinds.
        var cards = new List<NewsCardData>(16);
        foreach (var rel in _newsChangelog ?? [])
        {
            var date = DateTime.TryParse(rel.Date, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var d)
                ? (DateTime?)d.ToUniversalTime() : null;
            var desc = !string.IsNullOrWhiteSpace(rel.Description)
                ? rel.Description
                : rel.Changes.Count > 0 ? rel.Changes[0].Text : null;
            var thumb = logoWrap;
            if (!string.IsNullOrWhiteSpace(rel.Banner))
            {
                var b = _changelogService.GetCachedImage(rel.Banner);
                if (b != null)
                {
                    var u = rel.Banner;
                    thumb = SpheneUI.Media.Texture($"clb:{u}", () => b);
                }
            }
            cards.Add(new NewsCardData(1, "Plugin Update", accent,
                date ?? DateTime.MinValue,
                !string.IsNullOrWhiteSpace(rel.Title)
                    ? rel.Title : $"Sphene {rel.Version} installed",
                desc, date, thumb,
                () => NavigateTo(new HubLocation(SpheneHubPage.Settings, null,
                    null, (int)SettingsUi.SettingsPage.ReleaseNotes,
                    ReleaseVersion: rel.Version))));
        }
        // staff see drafts merged into the feed · a draft card jumps straight
        // into the editor, published ones open the reader.
        var announcementSource = isStaff && _announcements.StaffItems.Count > 0
            ? _announcements.StaffItems
            : _announcements.Items;
        foreach (var ann in announcementSource)
        {
            var annId = ann.Id;
            var isDraft = ann.IsDraft;
            IDalamudTextureWrap? annThumb = logoWrap;
            if (!string.IsNullOrWhiteSpace(ann.BannerImageUrl))
            {
                var b = _announcements.GetCachedImage(ann.BannerImageUrl);
                if (b != null)
                {
                    var key = $"ann:{annId}";
                    annThumb = SpheneUI.Media.Texture(key, () => b);
                }
            }
            var badgeColor = isDraft
                ? SpheneUI.Colors.TextMuted
                : AnnouncementBadgeColor(ann.Category);
            var badge = isDraft
                ? "Draft"
                : string.IsNullOrWhiteSpace(ann.Category)
                    ? "News"
                    : char.ToUpperInvariant(ann.Category[0]) + ann.Category[1..];
            cards.Add(new NewsCardData(2, badge, badgeColor,
                ann.PublishedAt.ToUniversalTime(), ann.Title, ann.Summary,
                ann.PublishedAt.ToUniversalTime(), annThumb,
                () => NavigateTo(new HubLocation(SpheneHubPage.News, null, null,
                    AnnouncementId: annId, AnnouncementEdit: isDraft)),
                ann.IsPinned));
        }
        cards.Sort(static (a, b) =>
        {
            var pinned = b.Pinned.CompareTo(a.Pinned);
            return pinned != 0 ? pinned : b.SortDate.CompareTo(a.SortDate);
        });

        var any = false;
        foreach (var card in cards)
        {
            if (_newsFilter != 0 && card.FilterIndex != _newsFilter)
            {
                continue;
            }
            DrawNewsCard(card.Badge, card.BadgeColor, card.Title,
                card.Desc, card.DisplayDate, card.Thumb, accent, card.OnClick,
                fallbackIcon: FontAwesomeIcon.Bullhorn,
                id: $"{card.FilterIndex}:{card.Title}:{card.SortDate.Ticks}");
            any = true;
        }
        if (!any)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Newspaper,
                "Nothing in this filter",
                "Plugin updates, announcements and community posts land here.");
        }
    }

    private List<ReleaseChangelogViewEntry>? _newsChangelog;

    private async Task LoadChangelogAsync()
    {
        try
        {
            _newsChangelog = await _changelogService
                .GetChangelogEntriesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "News page changelog fetch failed");
            _newsChangelog = [];
        }
    }

    private sealed record NewsCardData(int FilterIndex, string Badge,
        Vector4 BadgeColor, DateTime SortDate, string Title, string? Desc,
        DateTime? DisplayDate, IDalamudTextureWrap? Thumb, Action? OnClick,
        bool Pinned = false);

    private static Vector4 AnnouncementBadgeColor(string? category)
        => category switch
        {
            "event" => SpheneUI.Palette.SpheneGold,
            "maintenance" => SpheneUI.Palette.NetworkWarning,
            "patch" => SpheneUI.Palette.EtherealPurple,
            _ => SpheneUI.Palette.CrystalBlue,
        };

    /// <summary>
    /// Announcement reader · the release-detail hero language applied to the
    /// persisted article: edge-to-edge banner, left fade + bottom scrim,
    /// category badge, title, date and author, then the markdown body.
    /// </summary>
    private void DrawAnnouncementReader(long id, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var ann = FindAnnouncementAnywhere(id);
        if (ann == null)
        {
            if (_announcements.IsLoading)
            {
                for (var i = 0; i < 3; i++)
                {
                    SpheneUI.Fx.SkeletonLine(0.85f - i * 0.12f);
                }
                return;
            }
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Newspaper,
                "Announcement unavailable",
                "This article was removed or is not published yet.");
            if (SpheneUI.Controls.SegmentButton("Back to News##ann-gone",
                    FontAwesomeIcon.ArrowLeft, accent: accent))
            {
                NavigateBack();
            }
            return;
        }

        if (_apiController.IsAdmin || _apiController.IsModerator)
        {
            if (SpheneUI.Controls.SegmentButton("Edit##ann-edit-entry",
                    FontAwesomeIcon.Edit, accent: accent))
            {
                NavigateTo(new HubLocation(SpheneHubPage.News, null, null,
                    AnnouncementId: id, AnnouncementEdit: true));
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        var lineH = ImGui.GetTextLineHeight();
        var badgeColor = ann.IsDraft
            ? SpheneUI.Colors.TextMuted
            : AnnouncementBadgeColor(ann.Category);
        var badge = ann.IsDraft
            ? "Draft"
            : string.IsNullOrWhiteSpace(ann.Category)
                ? "News"
                : char.ToUpperInvariant(ann.Category[0]) + ann.Category[1..];

        var bannerBytes = _announcements.GetCachedImage(ann.BannerImageUrl);
        var wrap = bannerBytes != null
            ? SpheneUI.Media.Texture($"ann:{id}", () => bannerBytes) : null;
        // The article keeps its "?" locally at the title edge · no rail
        // entry exists for an open announcement, and the dock rides the
        // header row so neither title branch pays an extra line for it.
        var articleHelpId = $"announcement-{id}";
        void DrawArticleHelp()
        {
            SpheneUI.Text.Colored(badge, badgeColor);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
            SpheneUI.Text.Muted(
                $"{SpheneUI.Text.FormatDate(ann.PublishedAt)}  ·  {ann.AuthorDisplay}");
        }
        SpheneUI.Layout.RegisterPageHelp(articleHelpId, DrawArticleHelp);
        var titleRowMin = ImGui.GetCursorScreenPos();
        SpheneUI.Controls.DockToCardEdge(SpheneUI.Metrics.ControlHeight);
        var helpPos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(titleRowMin);
        if (wrap != null)
        {
            var bw = ImGui.GetContentRegionAvail().X;
            var bh = MathF.Min(bw * 0.30f, 190f * scale);
            ImGui.Dummy(new Vector2(bw, bh));
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var rnd = 10f * scale;
            SpheneUI.Media.ImageInRect(dl, wrap, min, max, ImageFit.Cover,
                rounding: rnd);

            var darkL = new Vector4(0f, 0f, 0f, 0.88f);
            var darkL32 = SpheneUI.ColorUtils.ToU32(darkL);
            var darkB = new Vector4(0f, 0f, 0f, 0.55f);
            var clear = new Vector4(0f, 0f, 0f, 0f);
            SpheneUI.Media.FillRoundedGradient(dl, min,
                new Vector2(min.X + bw * 0.34f, max.Y), darkL, darkL, darkL, darkL,
                rnd, ImDrawFlags.RoundCornersLeft);
            dl.AddRectFilledMultiColor(
                new Vector2(min.X + bw * 0.34f, min.Y),
                new Vector2(min.X + bw * 0.78f, max.Y),
                darkL32, 0u, 0u, darkL32);
            SpheneUI.Media.FillRoundedGradient(dl,
                new Vector2(min.X, min.Y + bh * 0.6f), max,
                clear, clear, darkB, darkB, rnd, ImDrawFlags.RoundCornersBottom);
            dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(badgeColor, 0.35f)), rnd);

            var pad = 16f * scale;
            var white = SpheneUI.ColorUtils.ToU32(Vector4.One);
            var subWhite = SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.82f));
            var font = ImGui.GetFont();
            var titleSize = lineH * 1.5f;
            var blockH = lineH * 1.35f + titleSize + lineH * 0.6f + lineH;
            var ty = min.Y + (bh - blockH) * 0.56f;
            var tx = min.X + pad;

            var bw2 = ImGui.CalcTextSize(badge).X + 16f * scale;
            var bh2 = lineH * 1.35f;
            dl.AddRectFilled(new Vector2(tx, ty),
                new Vector2(tx + bw2, ty + bh2),
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(badgeColor, 0.85f)),
                bh2 * 0.5f);
            dl.AddText(new Vector2(tx + 8f * scale, ty + (bh2 - lineH) * 0.5f),
                white, badge);
            var dateText = $"{SpheneUI.Text.FormatDate(ann.PublishedAt)}  ·  {ann.AuthorDisplay}";
            dl.AddText(new Vector2(tx + bw2 + 12f * scale,
                    ty + (bh2 - lineH) * 0.5f), subWhite, dateText);

            var titleY = ty + bh2 + lineH * 0.45f;
            var titleW = MathF.Min(ImGui.CalcTextSize(ann.Title).X
                * (titleSize / lineH), bw * 0.72f);
            var displayTitle = ann.Title;
            while (displayTitle.Length > 4
                   && ImGui.CalcTextSize(displayTitle).X * (titleSize / lineH) > titleW)
            {
                displayTitle = displayTitle[..^1];
            }
            if (displayTitle.Length != ann.Title.Length)
            {
                displayTitle = displayTitle.TrimEnd() + "…";
            }
            dl.AddText(font, titleSize, new Vector2(tx, titleY), white, displayTitle);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }
        else
        {
            SpheneUI.Text.SectionHeader(ann.Title);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }
        var afterTitle = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(helpPos);
        SpheneUI.Layout.PageHelp(articleHelpId, DrawArticleHelp);
        ImGui.SetCursorScreenPos(afterTitle);

        SpheneUI.Layout.SectionCard(() =>
        {
            MarkdownRenderer.RenderMarkdown(ann.Body,
                ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X,
                url => url.StartsWith("media:", StringComparison.Ordinal)
                    ? MarkdownComposer.MediaImageSource(url) : _announcements.GetCachedImage(url));
            EntityChipList.Draw(ann.Body);
        });

        if (!string.IsNullOrWhiteSpace(ann.LinkUrl)
            && SpheneUI.Controls.SegmentButton("Read more##ann-link",
                FontAwesomeIcon.ExternalLinkAlt, accent: accent))
        {
            Util.OpenLink(ann.LinkUrl);
        }
    }

    /// <summary>
    /// Admin announcement editor · create (id −1) or update. Fields seed once
    /// per target; the live preview renders through the same markdown pipeline
    /// the reader uses. Publish/Delete run on a task so the UI never blocks.
    /// </summary>
    private void DrawAnnouncementEditor(Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var editing = _viewingAnnouncementId.GetValueOrDefault(-1);
        var isNew = editing <= 0;

        // never seed buffers from a miss · an empty form would clobber the
        // row on save, so edits wait until the source row is actually known.
        if (!isNew && FindAnnouncementAnywhere(editing) == null)
        {
            if (_announcements.IsLoading)
            {
                for (var i = 0; i < 3; i++)
                {
                    SpheneUI.Fx.SkeletonLine(0.85f - i * 0.12f);
                }
                return;
            }
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Newspaper,
                "Announcement unavailable",
                "This article was removed or is not published yet.");
            if (SpheneUI.Controls.SegmentButton("Back to News##ann-edit-gone",
                    FontAwesomeIcon.ArrowLeft, accent: accent))
            {
                NavigateBack();
            }
            return;
        }
        SeedAnnouncementEditor(editing);

        if (_editorSuccess)
        {
            _editorSuccess = false;
            _editorSeededId = long.MinValue;
            NavigateBack();
            return;
        }

        // Editor keeps its "?" locally at the title edge · no rail entry
        // exists for the editor subpage.
        SpheneUI.Layout.PageHeader(isNew ? "New Announcement" : "Edit Announcement",
            "announcement-editor", () =>
        {
            SpheneUI.Text.Colored(
                isNew ? "New Announcement" : "Edit Announcement",
                SpheneUI.Colors.TextPrimary);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
            SpheneUI.Text.MutedWrapped(
                "Body uses markdown: # headers, **bold**, *italic*, - lists, > quotes, --- rules, [text](url) links and ![alt](url) images.",
                ImGui.GetCursorPosX() + 360f * scale);
        });

        var availW = ImGui.GetContentRegionAvail().X;
        SpheneUI.Text.Muted("Title");
        SpheneUI.Controls.InputText("##ann-title", ref _editorTitle, 200,
            preferredWidth: availW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Muted("Category");
        SpheneUI.Controls.Segmented("##ann-category", ref _editorCategory,
            AnnouncementCategories);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Muted("Summary · shown on the news card");
        SpheneUI.Controls.InputText("##ann-summary", ref _editorSummary, 500,
            preferredWidth: availW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Muted("Body · markdown");
        SpheneUI.Text.Muted(
            "**bold**  *italic*  __underline__  ~~strike~~  ||spoiler||  `code`  ``` block  | table |  :::callout info  :::side img=\"…\" side=\"left\"");
        SpheneUI.Controls.InputTextMultiline("##ann-body", ref _editorBody,
            16000, new Vector2(availW, 200f * scale), preferredWidth: availW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Muted("Banner image URL · https only, hero above the article");
        SpheneUI.Controls.InputText("##ann-banner", ref _editorBanner, 500,
            preferredWidth: availW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Muted("Read-more link URL · optional");
        SpheneUI.Controls.InputText("##ann-link", ref _editorLink, 500,
            preferredWidth: availW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Controls.Toggle("Pin to top##ann-field", ref _editorPinned);
        SpheneUI.Controls.Toggle("Keep as draft##ann-field", ref _editorDraft);

        if (_editorError is { } editorError)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Text.Colored(editorError, SpheneUI.Colors.Danger);
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Muted("Preview");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
            if (string.IsNullOrWhiteSpace(_editorBody))
            {
                SpheneUI.Text.Muted("Body preview appears here.");
            }
            else
            {
                MarkdownRenderer.RenderMarkdown(_editorBody,
                    ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X,
                    _announcements.GetCachedImage);
            }
        });
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var busy = _editorBusy;
        var submitLabel = isNew
            ? (_editorDraft ? "Save draft" : "Publish")
            : "Save";
        if (SpheneUI.Controls.PrimaryButton(
                $"{submitLabel}##ann-submit",
                FontAwesomeIcon.Upload, accent: accent, enabled: !busy))
        {
            SubmitAnnouncementEditor(editing);
        }
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        if (SpheneUI.Controls.SegmentButton("Cancel##ann-submit",
                enabled: !busy))
        {
            NavigateBack();
        }
        if (!isNew)
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            var deleting = editing;
            if (SpheneUI.Controls.Button("Delete##ann-submit", danger: true)
                && !busy)
            {
                _announcementDeleteConfirm.Open(() => DeleteAnnouncement(deleting));
            }
        }
        if (busy)
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            SpheneUI.Text.Muted("Saving…");
        }
    }

    /// <summary>Seeds the editor buffers once per target (-1 = blank create).</summary>
    private void SeedAnnouncementEditor(long editing)
    {
        if (_editorSeededId == editing)
        {
            return;
        }
        _editorSeededId = editing;
        _editorError = null;
        _editorSuccess = false;
        AnnouncementDto? src = editing > 0
            ? FindAnnouncementAnywhere(editing)
            : null;
        _editorTitle = src?.Title ?? string.Empty;
        _editorSummary = src?.Summary ?? string.Empty;
        _editorBody = src?.Body ?? string.Empty;
        _editorBanner = src?.BannerImageUrl ?? string.Empty;
        _editorLink = src?.LinkUrl ?? string.Empty;
        _editorPinned = src?.IsPinned ?? false;
        // new articles start as drafts · publishing is an explicit choice,
        // not a footgun. Edits keep the row's current draft state.
        _editorDraft = src?.IsDraft ?? editing <= 0;
        _editorCategory = 0;
        if (src?.Category is { } cat)
        {
            for (var i = 0; i < AnnouncementCategories.Length; i++)
            {
                if (string.Equals(AnnouncementCategories[i], cat,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _editorCategory = i;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Announcement lookup across the public list and (for staff) the draft-
    /// inclusive admin list · null when the row is gone from both.
    /// </summary>
    private AnnouncementDto? FindAnnouncementAnywhere(long id)
    {
        foreach (var a in _announcements.Items)
        {
            if (a.Id == id)
            {
                return a;
            }
        }
        foreach (var a in _announcements.StaffItems)
        {
            if (a.Id == id)
            {
                return a;
            }
        }
        return null;
    }

    /// <summary>Client-side mirror of the server guards · errors land inline.</summary>
    private string? ValidateAnnouncementEditor()
    {
        if (string.IsNullOrWhiteSpace(_editorTitle) || _editorTitle.Length > 200)
        {
            return "A title of up to 200 characters is required.";
        }
        if (_editorBody.Length > 16000)
        {
            return $"The body is {_editorBody.Length - 16000} characters over the 16000 limit.";
        }
        if (!IsHttpUrl(_editorBanner))
        {
            return "Banner image URL must start with https:// or http:// (or stay empty).";
        }
        if (!IsHttpUrl(_editorLink))
        {
            return "Link URL must start with https:// or http:// (or stay empty).";
        }
        return null;
    }

    private static bool IsHttpUrl(string? url)
        => string.IsNullOrWhiteSpace(url)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private void SubmitAnnouncementEditor(long editing)
    {
        _editorError = ValidateAnnouncementEditor();
        if (_editorError != null)
        {
            return;
        }
        _editorBusy = true;
        var dto = new AnnouncementUpsertDto(editing <= 0 ? 0 : editing,
            _editorTitle.Trim(), _editorSummary.Trim(), _editorBody,
            AnnouncementCategories[_editorCategory],
            string.IsNullOrWhiteSpace(_editorBanner) ? null : _editorBanner.Trim(),
            string.IsNullOrWhiteSpace(_editorLink) ? null : _editorLink.Trim(),
            _editorPinned, _editorDraft);
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = editing <= 0
                    ? await _apiController.AdminCreateAnnouncement(dto)
                        .ConfigureAwait(false) is > 0
                    : await _apiController.AdminUpdateAnnouncement(dto)
                        .ConfigureAwait(false);
                if (ok)
                {
                    await _announcements.Refresh().ConfigureAwait(false);
                    await _announcements.RefreshStaff().ConfigureAwait(false);
                    _editorSuccess = true;
                }
                else
                {
                    _editorError = "The server rejected the announcement.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Announcement save failed");
                _editorError = "Save failed - see log for details.";
            }
            finally
            {
                _editorBusy = false;
            }
        });
    }

    private void DeleteAnnouncement(long id)
    {
        _editorBusy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _apiController.AdminDeleteAnnouncement(id)
                        .ConfigureAwait(false))
                {
                    await _announcements.Refresh().ConfigureAwait(false);
                    await _announcements.RefreshStaff().ConfigureAwait(false);
                    _editorSuccess = true;
                }
                else
                {
                    _editorError = "The server rejected the delete.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Announcement delete failed");
                _editorError = "Delete failed - see log for details.";
            }
            finally
            {
                _editorBusy = false;
            }
        });
    }

    private int _communityTab;
    private readonly List<SpheneUI.Controls.TabItem> _communityTabItems = [];
    private readonly List<SyncshellCommunityFeedItemDto> _communityFeedScratch = [];

    /// <summary>
    /// Fixed page chrome for the nav-content layout · drawn in the parent
    /// window above the scroll child (banner, top tabs). Returns the height
    /// consumed from the content origin; 0 for pages without fixed chrome.
    /// </summary>
    private float PageFixedChrome(Vector2 contentMin, Vector4 accentMe,
        Vector4 accentCommunity)
    {
        var pad = SpheneUI.Spacing.PagePadding.Scaled;
        ImGui.SetCursorScreenPos(contentMin + new Vector2(pad, pad));
        // Hero banner bleeds pane-wide · inside a scroll child the pane IS
        // the child, but the fixed zone's enclosing window is the whole hub
        // frame (rail included), so the bleed rect must be passed
        // explicitly or the banner paints over the sidebar.
        var heroHost = new SpheneUI.Profile.HeroHost(contentMin,
            new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowSize().X,
                contentMin.Y));
        // The fixed zone runs in the hub window's flow - SetCursorScreenPos
        // leaves the line origin at the window edge (under the rail), so
        // every wrapped line and TabBar would snap back there. Re-anchor it
        // to the content edge for the whole chrome dispatch.
        var chromeIndent = SpheneUI.Layout.FlowIndentTo(contentMin.X + pad);
        switch (_activePage)
        {
            case SpheneHubPage.Community:
                DrawCommunityBanner(accentCommunity);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                DrawCommunityTabs(accentCommunity);
                break;
            case SpheneHubPage.PublicProfile:
                DrawPublicProfileChrome(accentCommunity, heroHost);
                break;
            case SpheneHubPage.Profile:
                DrawProfileChrome(accentMe, heroHost);
                break;
            case SpheneHubPage.Friends:
                if (_viewingFriend is { } friendPair)
                {
                    DrawFriendChrome(friendPair, accentCommunity, heroHost);
                }
                else
                {
                    DrawFriendsToolbar(accentCommunity);
                }
                break;
            case SpheneHubPage.News:
                if (_viewingAnnouncementId == null && !_announcementEditMode)
                {
                    DrawNewsChrome(accentMe);
                }
                break;
            case SpheneHubPage.ProfileStyle:
                DrawStyleChrome(accentMe);
                break;
            default:
                SpheneUI.Layout.FlowUnindent(chromeIndent);
                return 0f;
        }
        var fixedBottom = ImGui.GetCursorScreenPos().Y;
        SpheneUI.Layout.FlowUnindent(chromeIndent);
        return fixedBottom - contentMin.Y;
    }

    /// <summary>
    /// Community - the hub where everything the user's syncshells publish
    /// lands. Desktop structure: a compact purple banner carries the
    /// identity, the shared <see cref="SpheneUI.Controls.TabBar"/> strip
    /// sits on its own row below it, and the body is a two-column layout -
    /// flexible feed on the left, a constrained information sidebar on the
    /// right. Only when the content region is genuinely too narrow for both
    /// does the page fall back to a single column that keeps the stats as
    /// a compact strip.
    /// </summary>
    private void DrawCommunityPage(Vector4 accent)
    {
        _communityFeed.EnsureRequested();
        var items = _communityFeed.Items;

        var scale = SpheneUI.Scale;
        var avail = SpheneUI.Controls.BoundedAvailX();
        var gap = SpheneUI.Spacing.X12.Scaled;
        var sideW = Math.Clamp(avail * 0.27f, 210f * scale, 280f * scale);
        var feedW = avail - sideW - gap;

        if (feedW >= CommunityMinFeedWidth * scale)
        {
            var origin = ImGui.GetCursorScreenPos();
            SpheneUI.Layout.ContentRegion(feedW, "community-main",
                () => DrawCommunityMainColumn(items, accent));
            var leftBottom = ImGui.GetCursorScreenPos().Y;
            ImGui.SetCursorScreenPos(
                new Vector2(origin.X + feedW + gap, origin.Y));
            SpheneUI.Layout.ContentRegion(sideW, "community-side",
                () => DrawCommunitySidebar(accent));
            var rightBottom = ImGui.GetCursorScreenPos().Y;
            ImGui.SetCursorScreenPos(
                new Vector2(origin.X, MathF.Max(leftBottom, rightBottom)));
        }
        else
        {
            DrawCommunityStatsStrip(accent);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawCommunityMainColumn(items, accent);
        }
    }

    /// <summary>Smallest usable feed width before the sidebar column is
    /// dropped · unscaled.</summary>
    private const float CommunityMinFeedWidth = 380f;

    /// <summary>The left column: Highlights + section heading + the
    /// selected tab's feed layout, then cursor paging.</summary>
    private void DrawCommunityMainColumn(
        IReadOnlyList<SyncshellCommunityFeedItemDto> items, Vector4 accent)
    {
        switch (_communityTab)
        {
            case 0:
                DrawCommunityHighlights(items, accent);
                SpheneUI.Text.SectionHeader("Recent Activity",
                    icon: FontAwesomeIcon.Clock, accent: accent);
                DrawCommunityCardFeed(items, accent, only: null);
                break;
            case 2:
                SpheneUI.Text.SectionHeader("Photos",
                    icon: FontAwesomeIcon.Images, accent: accent);
                DrawCommunityPhotos(items, accent);
                break;
            case 3:
                SpheneUI.Text.SectionHeader("Upcoming events",
                    icon: FontAwesomeIcon.CalendarAlt, accent: accent);
                DrawCommunityEvents(items, accent);
                break;
            case 5:
                SpheneUI.Text.SectionHeader("Activity",
                    icon: FontAwesomeIcon.Bolt, accent: accent);
                DrawCommunityActivity(items, accent);
                break;
            case 6:
                DrawFollowingFeed(accent);
                return;
            case 7:
                DrawPeopleSearch(accent);
                return;
            default:
                var only = CommunityTabKind(_communityTab);
                SpheneUI.Text.SectionHeader(
                    only == SyncshellFeedItemKind.Blog ? "Latest Chronicles" : "Latest news",
                    icon: only == SyncshellFeedItemKind.Blog
                        ? FontAwesomeIcon.BookOpen : FontAwesomeIcon.Bullhorn,
                    accent: accent);
                DrawCommunityCardFeed(items, accent, only);
                break;
        }

        if (_communityFeed.HasMore)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_communityFeed.IsLoading))
            {
                if (SpheneUI.Controls.Button("Load more##commmore"))
                {
                    _ = _communityFeed.LoadMoreAsync();
                }
            }
        }
    }

    /// <summary>Content type each card-feed tab shows · tabs with their own
    /// layout (photos grid, events list, activity rows) map to null and are
    /// switched on before this is consulted.</summary>
    private static SyncshellFeedItemKind? CommunityTabKind(int tab) => tab switch
    {
        1 => SyncshellFeedItemKind.News,
        4 => SyncshellFeedItemKind.Blog,
        _ => null,
    };

    /// <summary>
    /// The compact hub banner · a purple-magenta gradient card carrying the
    /// Community identity only: icon plate, display title and a one-line
    /// subtitle. The right third is the dedicated visual area reserved for
    /// a future custom banner image - until then a restrained gradient with
    /// a faint watermark glyph fills it. No statistics, no tabs, no feed
    /// artwork: those live below the banner / in the sidebar.
    /// </summary>
    private static void DrawCommunityBanner(Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var w = SpheneUI.Controls.BoundedAvailX();
        var pad = 14f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var radius = 12f * scale;
        var plate = 32f * scale;

        var bannerH = pad + plate + 6f * scale + lineH + pad;
        var max = min + new Vector2(w, bannerH);
        ImGui.Dummy(new Vector2(w, bannerH));

        var surface = SpheneUI.Layout.NavRailBandColor();
        var (deep, hi) = SpheneUI.Colors.AccentRamp(accent);
        var magenta = SpheneUI.ColorUtils.LerpColor(hi, SpheneUI.Palette.SpheneMagenta, 0.5f);
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(surface), radius);
        SpheneUI.Media.FillRoundedGradient(dl, min, max,
            SpheneUI.ColorUtils.WithAlpha(deep, 0.45f),
            SpheneUI.ColorUtils.WithAlpha(magenta, 0.60f),
            SpheneUI.ColorUtils.WithAlpha(surface, 0.94f),
            SpheneUI.ColorUtils.WithAlpha(deep, 0.78f), radius);

        dl.PushClipRect(min, max, true);
        var glyph = FontAwesomeIcon.Users.ToIconString();
        dl.AddText(UiBuilder.IconFont, bannerH * 2.0f,
            new Vector2(max.X - bannerH * 2.1f, max.Y - bannerH * 1.9f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(hi, 0.10f)),
            glyph);
        dl.PopClipRect();
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(hi, 0.28f)), radius);

        var tx = min.X + pad;
        var ty = min.Y + pad;
        SpheneUI.Layout.SlotIcon(dl, new Vector2(tx, ty), plate,
            FontAwesomeIcon.Users, new Vector4(1f, 1f, 1f, 0.92f));
        using (SpheneUI.DisplayFont.Push())
        {
            var titleH = ImGui.CalcTextSize("Community").Y;
            dl.AddText(new Vector2(tx + plate + 10f * scale, ty + (plate - titleH) * 0.5f),
                SpheneUI.ColorUtils.ToU32(Vector4.One), "Community");
        }
        ty += plate + 6f * scale;
        dl.AddText(new Vector2(tx, ty),
            SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.82f)),
            "News, events, photos and Chronicles from your joined and followed syncshells - all in one place.");
    }

    /// <summary>
    /// The single content-type filter: the shared top-tab strip. Labels
    /// collapse to icon-only (with tooltips) before they could clip, so the
    /// strip stays whole at every window width. Tab switches ride the hub's
    /// <see cref="HubLocation"/> sub-state so back/forward restores them.
    /// </summary>
    private void DrawCommunityTabs(Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        Span<(FontAwesomeIcon Icon, string Label)> tabs =
        [
            (FontAwesomeIcon.Home, "Overview"),
            (FontAwesomeIcon.Bullhorn, "News"),
            (FontAwesomeIcon.Images, "Photos"),
            (FontAwesomeIcon.Calendar, "Events"),
            (FontAwesomeIcon.BookOpen, "Chronicles"),
            (FontAwesomeIcon.Bolt, "Activity"),
            (FontAwesomeIcon.Rss, "Following"),
            (FontAwesomeIcon.Users, "People"),
        ];

        var needed = 0f;
        foreach (var (icon, label) in tabs)
        {
            float iconW;
            using (SpheneUI.Icons.PushFont())
            {
                iconW = ImGui.CalcTextSize(icon.ToIconString()).X;
            }
            needed += iconW + 5f * scale + ImGui.CalcTextSize(label).X + 22f * scale;
        }
        var iconOnly = needed > SpheneUI.Controls.BoundedAvailX();

        _communityTabItems.Clear();
        foreach (var (icon, label) in tabs)
        {
            _communityTabItems.Add(new SpheneUI.Controls.TabItem(icon,
                iconOnly ? string.Empty : label, Tooltip: label));
        }
        var hit = SpheneUI.Controls.TabBar("community-feed", _communityTabItems, _communityTab, accent);
        if (hit >= 0 && hit != _communityTab)
        {
            NavigateTo(new HubLocation(SpheneHubPage.Community, null, null, hit));
        }
    }

    private readonly HashSet<string> _memberUidScratch = new(StringComparer.Ordinal);
    private readonly HashSet<string> _onlineUidScratch = new(StringComparer.Ordinal);

    /// <summary>
    /// Folds one syncshell into the member/online UID sets. The honest
    /// definition, same everywhere the stats appear: the server's
    /// <c>GroupPairUserInfos</c> roster only carries pinned/staff entries,
    /// so members = the union of its keys, the client's member pairs and the
    /// local user (joined shells only reach us as members). Online = the
    /// distinct online pair UIDs plus self while connected - no +1 fakes,
    /// no double counting when a pair shares several shells.
    /// </summary>
    private void CollectPresence(GroupFullInfoDto group, List<Pair> pairs)
    {
        foreach (var uid in group.GroupPairUserInfos.Keys)
        {
            _memberUidScratch.Add(uid);
        }
        foreach (var pair in pairs)
        {
            _memberUidScratch.Add(pair.UserData.UID);
            if (pair.IsOnline)
            {
                _onlineUidScratch.Add(pair.UserData.UID);
            }
        }
        var self = _apiController.UID;
        if (!string.IsNullOrEmpty(self))
        {
            _memberUidScratch.Add(self);
            if (_apiController.IsConnected)
            {
                _onlineUidScratch.Add(self);
            }
        }
    }

    /// <summary>Distinct member/online counts for one syncshell.</summary>
    private (int Members, int Online) ShellPresence(GroupFullInfoDto group, List<Pair> pairs)
    {
        _memberUidScratch.Clear();
        _onlineUidScratch.Clear();
        CollectPresence(group, pairs);
        return (_memberUidScratch.Count, _onlineUidScratch.Count);
    }

    /// <summary>The same counts aggregated across every joined syncshell ·
    /// a person in several shells is still counted once.</summary>
    private (int Members, int Online) TotalPresence()
    {
        _memberUidScratch.Clear();
        _onlineUidScratch.Clear();
        foreach (var kv in _pairManager.GroupPairs)
        {
            CollectPresence(kv.Key, kv.Value);
        }
        return (_memberUidScratch.Count, _onlineUidScratch.Count);
    }

    /// <summary>The real membership numbers - the same counts the
    /// Syncshells overview reports.</summary>
    private (int Shells, int Members, int Online) CommunityStats()
    {
        var (members, online) = TotalPresence();
        return (_pairManager.GroupPairs.Count, members, online);
    }

    /// <summary>The right column: a "Your communities" information card
    /// with the real stats and a manual refresh, then a compact quick-links
    /// card of existing navigation targets.</summary>
    private void DrawCommunitySidebar(Vector4 accent)
    {
        var (shells, members, online) = CommunityStats();
        SpheneUI.Layout.SectionCard(() =>
        {
            DrawCommunityStatRow(FontAwesomeIcon.Users, "Communities", shells, accent);
            DrawCommunityHairline();
            DrawCommunityStatRow(FontAwesomeIcon.UserFriends, "Members", members, accent);
            DrawCommunityHairline();
            DrawCommunityStatRow(FontAwesomeIcon.Signal, "Online", online, accent);
            DrawCommunityHairline();
            DrawCommunityLinkRow(FontAwesomeIcon.SyncAlt, "Refresh feed", accent,
                onClick: () => _ = _communityFeed.Refresh(),
                disabled: _communityFeed.IsLoading);
        }, title: "Your communities", accentColor: accent,
            description: "Across your joined and followed syncshells", id: "community-stats");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.SectionCard(() =>
        {
            DrawCommunityLinkRow(FontAwesomeIcon.CalendarAlt, "Upcoming events", accent,
                onClick: () => NavigateTo(new HubLocation(SpheneHubPage.Community, null, null, 3)));
            DrawCommunityLinkRow(FontAwesomeIcon.Compass, "Discover communities", accent,
                onClick: () => NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null)));
        }, title: "Quick links", accentColor: accent, id: "community-links");
    }

    /// <summary>One stat row inside the sidebar card · tinted icon, muted
    /// label and the value docked flush at the card edge.</summary>
    private static void DrawCommunityStatRow(FontAwesomeIcon icon, string label,
        int value, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        SpheneUI.Icons.Icon(icon, SpheneUI.ColorUtils.WithAlpha(accent, 0.85f));
        ImGui.SameLine(0, 8f * scale);
        SpheneUI.Text.Muted(label);
        var text = $"{value}";
        SpheneUI.Controls.DockToCardEdge(ImGui.CalcTextSize(text).X);
        SpheneUI.Text.Colored(text, SpheneUI.Colors.TextPrimary);
    }

    /// <summary>Inset hairline separating sidebar rows.</summary>
    private static void DrawCommunityHairline()
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var w = SpheneUI.Controls.BoundedAvailX();
        dl.AddLine(pos, pos + new Vector2(w, 0f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Colors.Border, 0.5f)));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    /// <summary>Clickable sidebar row · icon + label + a chevron that fades
    /// in on hover, in the same accent hover language as feature tiles.</summary>
    private static void DrawCommunityLinkRow(FontAwesomeIcon icon, string label,
        Vector4 accent, Action onClick, bool disabled = false)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var w = SpheneUI.Controls.BoundedAvailX();
        var lineH = ImGui.GetTextLineHeight();
        var h = lineH + 8f * scale;

        ImGui.PushID($"commlink-{label}");
        var clicked = ImGui.InvisibleButton("##row", new Vector2(w, h));
        var hovered = ImGui.IsItemHovered() && !disabled;
        ImGui.PopID();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        if (hovered)
        {
            dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(accent, 0.08f)), 6f * scale);
        }

        var tx = min.X + 6f * scale;
        var ty = min.Y + (h - lineH) * 0.5f;
        var glyph = icon.ToIconString();
        float iconW;
        using (SpheneUI.Icons.PushFont())
        {
            iconW = ImGui.CalcTextSize(glyph).X;
        }
        var labelColor = disabled
            ? SpheneUI.Colors.TextMuted
            : hovered ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextSecondary;
        using (SpheneUI.Icons.PushFont())
        {
            dl.AddText(new Vector2(tx, ty),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    accent, disabled ? 0.35f : 0.85f)), glyph);
        }
        dl.AddText(new Vector2(tx + iconW + 8f * scale, ty),
            SpheneUI.ColorUtils.ToU32(labelColor), label);

        var chevron = FontAwesomeIcon.ChevronRight.ToIconString();
        float chevronW;
        using (SpheneUI.Icons.PushFont())
        {
            chevronW = ImGui.CalcTextSize(chevron).X;
        }
        using (SpheneUI.Icons.PushFont())
        {
            dl.AddText(new Vector2(max.X - 6f * scale - chevronW, ty),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    accent, hovered ? 0.9f : 0.3f)), chevron);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (clicked && !disabled)
        {
            onClick();
        }
    }

    /// <summary>The collapsed-width stand-in for the sidebar · one slim
    /// strip with the same real stats and a refresh affordance so the
    /// information stays accessible when the two-column layout cannot fit.</summary>
    private void DrawCommunityStatsStrip(Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var w = SpheneUI.Controls.BoundedAvailX();
        var h = ImGui.GetFrameHeight() + 10f * scale;
        var max = min + new Vector2(w, h);
        ImGui.Dummy(new Vector2(w, h));

        var (shells, members, online) = CommunityStats();
        var hi = SpheneUI.Colors.AccentRamp(accent).Hi;
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.Layout.NavRailBandColor()), 8f * scale);
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.4f)), 8f * scale);

        var tx = min.X + 12f * scale;
        var ty = min.Y + (h - ImGui.GetTextLineHeight()) * 0.5f;
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = FontAwesomeIcon.Users.ToIconString();
            dl.AddText(new Vector2(tx, ty),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(hi, 0.85f)), glyph);
            tx += ImGui.CalcTextSize(glyph).X + 8f * scale;
        }
        dl.AddText(new Vector2(tx, ty),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextSecondary),
            $"{shells} communities · {members} members · {online} online");

        var frameH = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new Vector2(max.X - 10f * scale - frameH,
            min.Y + (h - frameH) * 0.5f));
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_communityFeed.IsLoading))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.SyncAlt))
            {
                _ = _communityFeed.Refresh();
            }
        }
        if (ImGui.IsItemHovered())
        {
            SpheneUI.AttachTooltip("Refresh feed");
        }
    }

    /// <summary>
    /// "Highlights" strip on Overview - up to three compact media tiles the
    /// feed payload honestly supports: the next non-cancelled upcoming
    /// event, the latest news post and the latest photo. Labels describe
    /// schedule and recency only · no invented popularity or engagement.
    /// The event tile comes from the dedicated next-event lookup - feed
    /// paging orders newest-first, so the earliest eligible event can live
    /// on a page that was never loaded.
    /// </summary>
    private void DrawCommunityHighlights(
        IReadOnlyList<SyncshellCommunityFeedItemDto> items, Vector4 accent)
    {
        _communityFeed.EnsureNextEventRequested();
        var nextEvent = _communityFeed.NextEvent;
        SyncshellCommunityFeedItemDto? latestNews = null;
        SyncshellCommunityFeedItemDto? latestPhoto = null;
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case SyncshellFeedItemKind.News when latestNews == null:
                    latestNews = item;
                    break;
                case SyncshellFeedItemKind.Photo when latestPhoto == null:
                    latestPhoto = item;
                    break;
            }
        }

        List<(SyncshellCommunityFeedItemDto Item, string Badge)> tiles = [];
        if (nextEvent != null) tiles.Add((nextEvent, "Upcoming event"));
        if (latestNews != null) tiles.Add((latestNews, "Latest news"));
        if (latestPhoto != null) tiles.Add((latestPhoto, "Latest photo"));
        if (tiles.Count == 0)
        {
            return;
        }

        SpheneUI.Text.SectionHeader("Highlights", icon: FontAwesomeIcon.Star, accent: accent);
        SpheneUI.Layout.CardGrid(tiles, 200f * SpheneUI.Scale,
            (tile, _) => DrawCommunityHighlightTile(tile.Item, tile.Badge));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    /// <summary>One highlight tile · cover media (real thumbnail or a
    /// kind-tinted fallback fill with a faint glyph) with the slot badge pinned to its
    /// top-left corner, title and provenance meta below. Click deep-links
    /// into the owning shell.</summary>
    private void DrawCommunityHighlightTile(SyncshellCommunityFeedItemDto item,
        string badge)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var w = SpheneUI.Controls.BoundedAvailX();
        var lineH = ImGui.GetTextLineHeight();
        var mediaH = 92f * scale;
        var pad = 10f * scale;
        var h = mediaH + pad * 1.5f + lineH * 2f + 4f * scale;
        var (kindColor, kindIcon) = CommunityFeedStyle(item.Kind);
        var shellAccent = SyncshellCommunityView.ParseAccent(item.AccentColor) ?? kindColor;
        var radius = 10f * scale;

        ImGui.PushID($"commhl-{item.Kind}-{item.GroupGID}-{item.Id}");
        var clicked = ImGui.InvisibleButton("##tile", new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.Layout.NavRailBandColor()), radius);
        var lift = SpheneUI.Fx.HoverLiftT(
            ImGui.GetID($"##commhl-lift-{item.GroupGID}-{item.Id}"), min, max);
        SpheneUI.Fx.HoverLift(dl, min, max, lift, radius, shellAccent);
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
            hovered ? shellAccent : SpheneUI.Colors.Border, hovered ? 0.7f : 0.4f)), radius);

        var mediaMax = new Vector2(max.X, min.Y + mediaH);
        var thumb = FeedThumb(item);
        if (thumb != null)
        {
            SpheneUI.Media.ImageInRect(dl, thumb, min, mediaMax, ImageFit.Cover,
                rounding: radius, corners: ImDrawFlags.RoundCornersTop,
                zoom: 1f + 0.06f * lift);
        }
        else
        {
            // No art · a restrained kind-tinted fill + faint glyph, the
            // same fallback language as the photos grid.
            dl.AddRectFilled(min, mediaMax, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(kindColor, 0.12f)), radius,
                ImDrawFlags.RoundCornersTop);
            var glyph = kindIcon.ToIconString();
            var glyphH = lineH * 2.4f;
            float glyphW;
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                glyphW = ImGui.CalcTextSize(glyph).X * (glyphH / ImGui.GetTextLineHeight());
            }
            dl.AddText(UiBuilder.IconFont, glyphH,
                new Vector2(min.X + (w - glyphW) * 0.5f,
                    min.Y + (mediaH - glyphH) * 0.5f),
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(kindColor, 0.55f)), glyph);
        }

        var scrim = SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Layout.NavRailBandColor(), 0.9f));
        dl.AddRectFilledMultiColor(
            new Vector2(min.X, mediaMax.Y - mediaH * 0.45f), mediaMax, 0u, 0u, scrim, scrim);

        var badgeW = ImGui.CalcTextSize(badge).X + 12f * scale;
        var badgeH = lineH * 1.25f;
        var bMin = min + new Vector2(6f * scale, 6f * scale);
        dl.AddRectFilled(bMin, bMin + new Vector2(badgeW, badgeH),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(kindColor, 0.92f)),
            badgeH * 0.5f);
        dl.AddText(bMin + new Vector2(6f * scale, (badgeH - lineH) * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.ContrastTextColor(kindColor)),
            badge);

        var tx = min.X + pad;
        var ty = mediaMax.Y + pad * 0.5f;
        dl.PushClipRect(new Vector2(tx, ty),
            new Vector2(max.X - pad, max.Y - pad * 0.5f), true);
        dl.AddText(new Vector2(tx, ty),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
            SpheneUI.Text.Ellipsize(item.Title, max.X - pad - tx));
        dl.AddText(new Vector2(tx, ty + lineH + 2f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
            SpheneUI.Text.Ellipsize(FeedMeta(item), max.X - pad - tx));
        dl.PopClipRect();

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (clicked)
        {
            NavigateFeedItem(item);
        }
    }

    /// <summary>Badge color and kind glyph per feed kind · the shared
    /// palette of feed cards and highlight tiles.</summary>
    private static (Vector4 Color, FontAwesomeIcon Icon) CommunityFeedStyle(
        SyncshellFeedItemKind kind) => kind switch
    {
        SyncshellFeedItemKind.News => (SpheneUI.Palette.EtherealPurple, FontAwesomeIcon.Bullhorn),
        SyncshellFeedItemKind.Photo => (SpheneUI.Palette.CrystalBlue, FontAwesomeIcon.Image),
        SyncshellFeedItemKind.Event => (SpheneUI.Palette.SpheneGold, FontAwesomeIcon.CalendarAlt),
        SyncshellFeedItemKind.Blog => (SpheneUI.Palette.SpheneMagenta, FontAwesomeIcon.BookOpen),
        SyncshellFeedItemKind.Comment => (SpheneUI.Palette.NetworkActive, FontAwesomeIcon.CommentDots),
        SyncshellFeedItemKind.Reaction => (SpheneUI.Palette.NetworkActive, FontAwesomeIcon.Heart),
        _ => (SpheneUI.Palette.NetworkActive, FontAwesomeIcon.CalendarCheck),
    };

    /// <summary>Unified card feed - Overview (all kinds) plus the News and
    /// Blog tabs, which reuse the same card language.</summary>
    private void DrawCommunityCardFeed(
        IReadOnlyList<SyncshellCommunityFeedItemDto> items, Vector4 accent,
        SyncshellFeedItemKind? only)
    {
        var any = false;
        foreach (var item in items)
        {
            if (only.HasValue && item.Kind != only.Value)
            {
                continue;
            }
            DrawCommunityFeedCard(item, accent);
            any = true;
        }
        if (any)
        {
            return;
        }
        if (_communityFeed.IsLoading && items.Count == 0)
        {
            DrawCommunitySkeleton();
            return;
        }
        if (items.Count == 0)
        {
            DrawCommunityFeedEmpty(accent);
            return;
        }
        var (icon, title, hint) = only switch
        {
            SyncshellFeedItemKind.News => (FontAwesomeIcon.Bullhorn,
                "No news yet", "Posts your syncshells publish land here."),
            SyncshellFeedItemKind.Blog => (FontAwesomeIcon.BookOpen,
                "No Chronicles", "Chronicles from people you are connected to land here."),
            _ => (FontAwesomeIcon.Newspaper,
                "Nothing in this tab", "News, photos and events from your joined and followed syncshells land here."),
        };
        SpheneUI.Layout.EmptyState(icon, title, hint, accent: accent);
    }

    /// <summary>One feed entry as a media card - badge, kind icon and
    /// provenance meta map the item kind; the click deep-links.</summary>
    private void DrawCommunityFeedCard(SyncshellCommunityFeedItemDto item, Vector4 accent)
    {
        var badge = item.Kind == SyncshellFeedItemKind.Rsvp ? "RSVP"
            : item.Kind == SyncshellFeedItemKind.Blog ? "Chronicle"
            : item.Kind.ToString();
        var (badgeColor, fallbackIcon) = CommunityFeedStyle(item.Kind);
        // Title-less Chronicles show their body snippet as the title line.
        var cardTitle = string.IsNullOrWhiteSpace(item.Title)
            ? item.Snippet ?? string.Empty : item.Title;
        // The leading actor segment is split off as its own hit target so a
        // name click opens the profile while the rest of the card deep-links.
        var actorLinkable = item.ActorUid != null
            && !string.IsNullOrWhiteSpace(item.ActorDisplayName);
        DrawNewsCard(badge, badgeColor, cardTitle, item.Snippet, item.TimestampUtc,
            FeedThumb(item), accent, () => NavigateFeedItem(item), fallbackIcon,
            actorLinkable ? FeedMetaTail(item) : FeedMeta(item),
            id: $"{item.GroupGID}-{item.Id}",
            actorName: actorLinkable ? item.ActorDisplayName : null,
            actorUid: item.ActorUid, onActorClick: NavigateToAuthor);
    }

    /// <summary>
    /// Photos tab - square-thumb grid over the feed's photo rows (cover-crop,
    /// hover zoom, caption + provenance meta). Click deep-links into the
    /// owning shell's gallery viewer; missing thumbs degrade to a tinted
    /// fill with a faint kind glyph.
    /// </summary>
    private void DrawCommunityPhotos(
        IReadOnlyList<SyncshellCommunityFeedItemDto> items, Vector4 accent)
    {
        var photos = _communityFeedScratch;
        photos.Clear();
        foreach (var item in items)
        {
            if (item.Kind == SyncshellFeedItemKind.Photo)
            {
                photos.Add(item);
            }
        }
        if (photos.Count == 0)
        {
            if (_communityFeed.IsLoading && items.Count == 0)
            {
                DrawCommunitySkeleton();
            }
            else if (items.Count == 0)
            {
                DrawCommunityFeedEmpty(accent);
            }
            else
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Images, "No photos",
                    "Photos shared in your syncshells land here.", accent: accent);
            }
            return;
        }

        var scale = SpheneUI.Scale;
        var tile = 148f * scale * SpheneUI.Density.Factor;
        var gap = SpheneUI.Spacing.X8.Scaled;
        var lineH = ImGui.GetTextLineHeight();
        var metaGap = 4f * scale;
        var cellH = tile + metaGap + lineH * 2f;
        var cols = Math.Max(1, (int)((SpheneUI.Controls.BoundedAvailX() + gap) / (tile + gap)));
        var dl = ImGui.GetWindowDrawList();

        for (var i = 0; i < photos.Count; i++)
        {
            var item = photos[i];
            var cellAccent = SyncshellCommunityView.ParseAccent(item.AccentColor) ?? accent;
            var min = ImGui.GetCursorScreenPos();
            ImGui.PushID($"commphoto-{item.GroupGID}-{item.Id}");
            var clicked = ImGui.InvisibleButton("##tile", new Vector2(tile, cellH));
            var hovered = ImGui.IsItemHovered();
            ImGui.PopID();
            var tMax = min + new Vector2(tile, tile);

            var lift = SpheneUI.Fx.HoverT(
                ImGui.GetID($"##commphoto-lift-{item.GroupGID}-{item.Id}"), hovered);
            var thumb = FeedThumb(item);
            if (thumb != null)
            {
                SpheneUI.Media.ImageInRect(dl, thumb, min, tMax, ImageFit.Cover,
                    rounding: 8f * scale, zoom: 1f + 0.05f * lift);
            }
            else
            {
                dl.AddRectFilled(min, tMax, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(cellAccent, 0.12f)), 8f * scale);
                using (SpheneUI.Icons.PushFont())
                {
                    var glyph = FontAwesomeIcon.Image.ToIconString();
                    var gs = ImGui.CalcTextSize(glyph);
                    dl.AddText(min + (new Vector2(tile, tile) - gs) * 0.5f,
                        SpheneUI.ColorUtils.ToU32(
                            SpheneUI.ColorUtils.WithAlpha(cellAccent, 0.5f)), glyph);
                }
            }
            if (lift > 0.01f)
            {
                dl.AddRect(min, tMax, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.25f * lift)),
                    8f * scale, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }

            var metaY = tMax.Y + metaGap;
            dl.PushClipRect(min, min + new Vector2(tile, cellH), true);
            dl.AddText(new Vector2(min.X, metaY),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
                SpheneUI.Text.Ellipsize(item.Title, tile));
            dl.AddText(new Vector2(min.X, metaY + lineH),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
                SpheneUI.Text.Ellipsize(FeedMeta(item), tile));
            dl.PopClipRect();

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (!string.IsNullOrWhiteSpace(item.Snippet))
                {
                    SpheneUI.AttachTooltip(item.Snippet!);
                }
            }
            if (clicked)
            {
                NavigateFeedItem(item);
            }
            if ((i + 1) % cols != 0 && i + 1 < photos.Count)
            {
                ImGui.SameLine(0f, gap);
            }
        }
    }

    /// <summary>Events tab - date-plate cards nearest-first (the feed's
    /// event rows carry their real <c>StartUtc</c> and location).</summary>
    private void DrawCommunityEvents(
        IReadOnlyList<SyncshellCommunityFeedItemDto> items, Vector4 accent)
    {
        var events = _communityFeedScratch;
        events.Clear();
        foreach (var item in items)
        {
            if (item.Kind == SyncshellFeedItemKind.Event)
            {
                events.Add(item);
            }
        }
        if (events.Count == 0)
        {
            if (_communityFeed.IsLoading && items.Count == 0)
            {
                DrawCommunitySkeleton();
            }
            else if (items.Count == 0)
            {
                DrawCommunityFeedEmpty(accent);
            }
            else
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.CalendarAlt,
                    "No upcoming events", "Events your syncshells schedule land here.",
                    accent: accent);
            }
            return;
        }

        events.Sort(static (a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        foreach (var item in events)
        {
            var evAccent = SyncshellCommunityView.ParseAccent(item.AccentColor) ?? accent;
            if (EventCard.Draw($"commev-{item.GroupGID}-{item.Id}", CommunityFeedEvent(item),
                    evAccent, CommunityFeedShellName(item)))
            {
                NavigateFeedItem(item);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
    }

    /// <summary>Activity tab - compact actor-centric rows for comments,
    /// reactions and RSVPs on the caller's own content.</summary>
    private void DrawCommunityActivity(
        IReadOnlyList<SyncshellCommunityFeedItemDto> items, Vector4 accent)
    {
        var any = false;
        foreach (var item in items)
        {
            if (item.Kind is not (SyncshellFeedItemKind.Comment
                or SyncshellFeedItemKind.Reaction or SyncshellFeedItemKind.Rsvp))
            {
                continue;
            }
            if (FeedItemRow.Draw($"commact-{item.Kind}-{item.Id}-{item.ActorUid}",
                    item, "hub:", fallbackAccent: accent,
                    onActorClick: NavigateToAuthor))
            {
                NavigateFeedItem(item);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            any = true;
        }
        if (any)
        {
            return;
        }
        if (_communityFeed.IsLoading && items.Count == 0)
        {
            DrawCommunitySkeleton();
        }
        else if (items.Count == 0)
        {
            DrawCommunityFeedEmpty(accent);
        }
        else
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Bolt, "No activity yet",
                "Comments, reactions and RSVPs on your posts land here.", accent: accent);
        }
    }

    private static void DrawCommunitySkeleton()
    {
        for (var i = 0; i < 3; i++)
        {
            SpheneUI.Fx.SkeletonLine(0.85f - i * 0.12f);
        }
    }

    /// <summary>The whole feed is empty - usually no joined shells · point
    /// at discovery instead of leaving a dead end.</summary>
    private void DrawCommunityFeedEmpty(Vector4 accent)
    {
        SpheneUI.Layout.EmptyState(FontAwesomeIcon.Newspaper,
            "Nothing new yet",
            "News, events and photos from your joined and followed syncshells land here.",
            "Discover communities",
            () => NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null)),
            FontAwesomeIcon.Compass, accent);
    }

    /// <summary>Display-only event DTO for <see cref="EventCard"/> · feed
    /// rows carry the real start time and location; unknown fields stay empty.</summary>
    private static SyncshellEventDto CommunityFeedEvent(SyncshellCommunityFeedItemDto item)
        => new(item.Id, item.GroupGID, item.Title, null, item.TimestampUtc,
            null, item.Snippet, null, null, item.TimestampUtc, item.TimestampUtc,
            PreviewMediaHash: item.ThumbnailMediaHash);

    private static string? CommunityFeedShellName(SyncshellCommunityFeedItemDto item)
    {
        var shell = string.IsNullOrWhiteSpace(item.GroupAlias) ? item.GroupGID : item.GroupAlias;
        return string.IsNullOrWhiteSpace(shell) ? null : shell;
    }

    /// <summary>"actor · shell · when" meta - blog rows have no shell so the
    /// author carries the origin; activity rows lead with the actor.</summary>
    private static string FeedMeta(SyncshellCommunityFeedItemDto item)
    {
        var actor = item.ActorDisplayName;
        return string.IsNullOrEmpty(actor)
            ? FeedMetaTail(item)
            : $"{actor} · {FeedMetaTail(item)}";
    }

    /// <summary>The meta line without the leading actor segment - used when
    /// the actor name is drawn as its own hit target next to it.</summary>
    private static string FeedMetaTail(SyncshellCommunityFeedItemDto item)
    {
        var when = FeedWhen(item.TimestampUtc);
        var shell = CommunityFeedShellName(item);
        return shell == null || item.Kind == SyncshellFeedItemKind.Blog
            ? when : $"{shell} · {when}";
    }

    /// <summary>"2h ago" for the past, "in 3d" for the near future - events
    /// sort into the feed by their real start time, so future stamps read
    /// forward instead of collapsing into "soon".</summary>
    private static string FeedWhen(DateTime utc)
    {
        var delta = utc - DateTime.UtcNow;
        if (delta <= TimeSpan.Zero)
        {
            return FeedItemRow.RelativeTime(utc);
        }
        if (delta < TimeSpan.FromHours(1)) return $"in {Math.Max(1, (int)delta.TotalMinutes)}m";
        if (delta < TimeSpan.FromDays(1)) return $"in {(int)delta.TotalHours}h";
        if (delta < TimeSpan.FromDays(7)) return $"in {(int)delta.TotalDays}d";
        return SpheneUI.Text.FormatShortDate(utc.ToLocalTime());
    }

    private static IDalamudTextureWrap? FeedThumb(SyncshellCommunityFeedItemDto item)
    {
        // Media-hash art first (events: preview → banner) - resolved through
        // the community media cache; while the download is still in flight
        // this returns null and the caller's plate stays a one-frame fallback.
        if (!string.IsNullOrEmpty(item.ThumbnailMediaHash))
        {
            var wrap = SpheneUI.Media.Texture(
                $"media-th:{item.GroupGID}:{item.Id}:{item.ThumbnailMediaHash}",
                () => MarkdownComposer.MediaImageSource($"media:{item.ThumbnailMediaHash}"));
            if (wrap != null) return wrap;
        }
        return string.IsNullOrEmpty(item.ThumbnailBase64) ? null
            : SpheneUI.Media.Texture($"news-th:{item.GroupGID}:{item.Id}",
                () => Convert.FromBase64String(item.ThumbnailBase64));
    }

    /// <summary>One feed card · a cover-cropped left visual (real thumbnail,
    /// or a kind-tinted fallback fill with a large faint glyph when no art
    /// exists) carrying the kind badge on its top-left corner, then title,
    /// description, provenance meta and a trailing chevron.</summary>
    private static void DrawNewsCard(string badge, Vector4 badgeColor, string title,
        string? desc, DateTime? date, IDalamudTextureWrap? thumb,
        Vector4 accent, Action? onClick,
        FontAwesomeIcon? fallbackIcon = null, string? meta = null, string? id = null,
        string? actorName = null, string? actorUid = null,
        Action<string>? onActorClick = null)
    {
        var scale = SpheneUI.Scale;
        var w = SpheneUI.Controls.BoundedAvailX();
        var pad = SpheneUI.Spacing.X8.Scaled;
        var lineH = ImGui.GetTextLineHeight();
        var visW = 184f * scale;
        var visH = 104f * scale;
        var h = visH + pad * 1.5f;
        var radius = 10f * scale;
        var dl = ImGui.GetWindowDrawList();
        var cardId = id ?? title;
        ImGui.PushID($"newscard-{cardId}");
        var clicked = onClick != null
            && ImGui.InvisibleButton("##nc", new Vector2(w, h));
        var hovered = onClick != null && ImGui.IsItemHovered();
        // The actor link sits inside the card's own hit rect - it can only
        // steal HoveredId when the card button opts into overlap.
        if (onClick != null)
        {
            ImGui.SetItemAllowOverlap();
        }
        ImGui.PopID();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        // The actor hit zone repositions the cursor absolutely - keep the
        // post-card cursor so the next card doesn't slide under this one.
        var resumeCursor = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()), radius);
        var lift = SpheneUI.Fx.HoverLiftT(ImGui.GetID($"ncl{cardId}"), min, max);
        SpheneUI.Fx.HoverLift(dl, min, max, lift, radius, accent);
        dl.AddRect(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                hovered ? accent : SpheneUI.Colors.Border, hovered ? 0.7f : 0.38f)),
            radius);

        // Left visual slot · real art cover-cropped, or the kind's gradient
        // plate with a large centered glyph for entries without images.
        var visMin = min + new Vector2(pad * 0.75f, pad * 0.75f);
        var visMax = visMin + new Vector2(visW, visH);
        if (thumb != null)
        {
            SpheneUI.Media.ImageInRect(dl, thumb, visMin, visMax, ImageFit.Cover,
                rounding: 8f * scale, zoom: 1f + 0.05f * lift);
        }
        else
        {
            // No art · restrained kind-tinted fill + faint glyph, the
            // same fallback language as the photos grid.
            dl.AddRectFilled(visMin, visMax, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(badgeColor, 0.12f)), 8f * scale);
            if (fallbackIcon.HasValue)
            {
                var glyph = fallbackIcon.Value.ToIconString();
                var glyphH = lineH * 2.1f;
                float glyphW;
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    glyphW = ImGui.CalcTextSize(glyph).X
                        * (glyphH / ImGui.GetTextLineHeight());
                }
                dl.AddText(UiBuilder.IconFont, glyphH,
                    new Vector2(visMin.X + (visW - glyphW) * 0.5f,
                        visMin.Y + (visH - glyphH) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(
                        SpheneUI.ColorUtils.WithAlpha(badgeColor, 0.55f)), glyph);
            }
        }

        // Kind badge pinned to the visual's top-left corner.
        var badgeW = ImGui.CalcTextSize(badge).X + 12f * scale;
        var badgeH = lineH * 1.25f;
        var badgeMin = visMin + new Vector2(6f * scale, 6f * scale);
        dl.AddRectFilled(badgeMin, badgeMin + new Vector2(badgeW, badgeH),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(badgeColor, 0.92f)),
            badgeH * 0.5f);
        dl.AddText(badgeMin + new Vector2(6f * scale, (badgeH - lineH) * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.ContrastTextColor(badgeColor)),
            badge);

        // Text column · title leads, meta sits at the top-right, the
        // description wraps underneath into two lines. A linkable actor name
        // leads the meta block as its own hit target.
        var actorLink = onActorClick != null && actorUid != null
            && !string.IsNullOrWhiteSpace(actorName);
        var metaText = meta ?? (date.HasValue
            ? FeedItemRow.RelativeTime(date.Value.ToUniversalTime()) : null);
        var metaSep = actorLink && metaText != null ? " · " : null;
        var actorW = actorLink ? ImGui.CalcTextSize(actorName!).X : 0f;
        var sepW = metaSep != null ? ImGui.CalcTextSize(metaSep).X : 0f;
        var metaW = actorW + sepW
            + (metaText != null ? ImGui.CalcTextSize(metaText).X : 0f);
        var tx = visMax.X + pad;
        var tr = max.X - pad * 2f;
        var ty = visMin.Y;
        var font = ImGui.GetFont();
        var titleSize = lineH * 1.2f;
        var titleW = tr - tx - (metaW > 0f ? metaW + 12f * scale : 0f);
        dl.PushClipRect(new Vector2(tx, ty), new Vector2(tr, max.Y - pad * 0.5f), true);
        dl.AddText(font, titleSize, new Vector2(tx, ty),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
            SpheneUI.Text.Ellipsize(title, titleW / (titleSize / lineH)));
        if (!string.IsNullOrWhiteSpace(desc))
        {
            var lines = SpheneUI.Text.WrapLines(desc!, tr - tx, 2);
            for (var i = 0; i < lines.Count; i++)
            {
                dl.AddText(new Vector2(tx, ty + titleSize + 3f * scale + i * lineH),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextSecondary),
                    lines[i]);
            }
        }
        dl.PopClipRect();
        var actorClicked = false;
        if (metaW > 0f)
        {
            var mx = tr - metaW;
            var my = ty + 1f * scale;
            if (actorLink)
            {
                var actorMin = new Vector2(mx, my);
                var actorHovered = ImGui.IsMouseHoveringRect(actorMin,
                    actorMin + new Vector2(actorW, lineH));
                dl.AddText(actorMin, SpheneUI.ColorUtils.ToU32(
                    actorHovered ? accent : SpheneUI.Colors.TextMuted), actorName!);
                ImGui.PushID($"nca{cardId}");
                ImGui.SetCursorScreenPos(actorMin);
                actorClicked = ImGui.InvisibleButton("##actor",
                    new Vector2(actorW, lineH));
                ImGui.PopID();
                ImGui.SetCursorScreenPos(resumeCursor);
                if (actorHovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    SpheneUI.AttachTooltip("View profile");
                }
                if (actorClicked)
                {
                    onActorClick!(actorUid!);
                }
                mx += actorW;
            }
            if (metaSep != null)
            {
                dl.AddText(new Vector2(mx, my),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), metaSep);
                mx += sepW;
            }
            if (metaText != null)
            {
                dl.AddText(new Vector2(mx, my),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), metaText);
            }
        }

        if (onClick != null)
        {
            var chev = FontAwesomeIcon.ChevronRight.ToIconString();
            using (SpheneUI.Icons.PushFont())
            {
                var cw = ImGui.CalcTextSize(chev).X;
                dl.AddText(new Vector2(max.X - pad - cw,
                        min.Y + (h - lineH) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(
                        hovered ? accent : SpheneUI.Colors.TextMuted), chev);
            }
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (clicked && !actorClicked)
            {
                onClick();
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    /// <summary>A friend's profile rendered inside the hub with their theme and banner.</summary>
    private const double ChatIntroMaxHiddenSeconds = 0.8;
    private const float ChatIntroFadeSeconds = 0.2f;
    private string _chatInput = string.Empty;
    private Helpers.Emoji.EmojiEditQueue _chatEmojiQueue = new();
    private readonly UI.Components.GifPicker _gifPicker;
    private readonly UI.Components.GifPicker _stickerPicker;
    private readonly Services.Gifs.IGifProvider _gifProvider;
    private readonly Dictionary<string, Task<Services.Gifs.GifItem?>>
        _chatGifMeta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<byte[]?>>
        _chatGifBytes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chatGifAnimKeys = new(StringComparer.Ordinal);
    private string? _chatScrollUid;
    private int _chatScrollCount = -1;
    private int _chatScrollFrame;
    private bool _chatScrollPending;
    private float _chatScrollLastMax = -1f;
    private int _chatScrollSettle;
    private float _chatScrollPrevMax = -1f;
    private float _chatScrollDistance;
    private Guid _chatScrollFirstId;
    private Guid _chatScrollLastId;
    private float _chatPrevScrollY;
    private int _chatWheelUpFrame = -1;
    private bool _chatWasAtBottom = true;
    private bool _chatIntroActive;
    private double _chatIntroStart;
    private double _chatRevealStart = -1.0;
    private readonly ConcurrentDictionary<string, (Task<bool> Task, DateTime At)>
        _chatSealChecks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chatKeyWarnDismissed = new(StringComparer.Ordinal);

    /// <summary>Cached per-peer "can we seal for this conversation" probe;
    /// refreshed at most every 30s so a peer that just published keys isn't
    /// locked out for the session. Null while the first check is in flight.</summary>
    private bool? ChatSealReady(string uid)
    {
        if (_chatSealChecks.TryGetValue(uid, out var check)
            && DateTime.UtcNow - check.At < TimeSpan.FromSeconds(30))
        {
            return check.Task.IsCompletedSuccessfully ? check.Task.Result : null;
        }
        var task = _chatService.CanSealForAsync(uid);
        _chatSealChecks[uid] = (task, DateTime.UtcNow);
        return null;
    }

    /// <summary>
    /// Direct messages with this friend - history bubbles (own right,
    /// theirs left) with a fixed-height scroll area and the send row at
    /// the bottom. Opening the view marks the conversation read.
    /// </summary>
    private void DrawFriendChat(Pair pair, Vector4 accent)
    {
        var uid = pair.UserData.UID;
        var scale = SpheneUI.Scale;
        _chatService.EnsureHistory(uid);
        _chatService.MarkOpen(uid);
        ImGui.SetNextFrameWantCaptureKeyboard(true);

        var own = _apiController.UID ?? string.Empty;
        var canSeal = ChatSealReady(uid);

        var w = SpheneUI.Controls.BoundedAvailX();
        // The composer height is content-driven (1-3 wrapped lines) and has
        // to be known before the scroll area so the field grows upward.
        var emojiHook = SpheneUI.EmojiPickerHook;
        var showGif = _gifProvider.IsConfigured;
        var frameH = ImGui.GetFrameHeight();
        var composerPadX = ImGui.GetStyle().FramePadding.X;
        var composerPadY = ImGui.GetStyle().FramePadding.Y;
        var capGap = SpheneUI.Spacing.X4.Scaled;
        var lineH = ImGui.GetTextLineHeight();
        ImGui.SetWindowFontScale(0.58f);
        var gifBadgeW = MathF.Max(frameH,
            ImGui.CalcTextSize("GIF").X + composerPadX * 2f);
        ImGui.SetWindowFontScale(1f);
        var capWidths = (emojiHook != null ? frameH : 0f)
            + (showGif ? gifBadgeW + frameH : 0f);
        var capCount = (emojiHook != null ? 1 : 0) + (showGif ? 2 : 0);
        var capsW = capWidths + capGap * capCount;
        var composerW = w - SpheneUI.Spacing.X8.Scaled;
        var composerInnerW = MathF.Max(60f, composerW - composerPadX * 2f - capsW);
        // matches InputComposerMultiline: the inner child insets text by
        // FramePadding, so the wrap width is the field minus that inset
        var composerTextW = MathF.Max(40f,
            composerInnerW - ImGui.GetStyle().FramePadding.X * 2f);
        var composerView = _chatEmojiQueue.Codec.Encode(_chatInput);
        var composerLines = Math.Clamp(Helpers.Emoji.ChatComposerWrap.Lines(
            Helpers.Emoji.ChatComposerWrap.Wrap(composerView, composerTextW,
                s => ImGui.CalcTextSize(s).X).Buf), 1, 3);
        // the multiline's inner child pads content by FramePadding - the
        // widget must own that space or the child is permanently scrolled
        // (and shows a scrollbar) even on a single line
        var composerInset = ImGui.GetStyle().FramePadding;
        var composerWidgetH = composerLines * lineH + composerInset.Y * 2f;
        // extra air splits evenly so the first line + caps sit vertically
        // centered inside the frame
        var composerTop = composerPadY + SpheneUI.Spacing.X4.Scaled * 0.5f;
        var inputH = composerWidgetH + composerTop + composerPadY
            + SpheneUI.Spacing.X4.Scaled * 0.5f;
        // caps center on the first text band whatever the line count is
        var composerCapY = (lineH + composerInset.Y * 2f - frameH) * 0.5f;
        // The scroll area fills everything above strip + composer - reserving
        // too little pushes them past the view, too much wastes a row. The
        // blocked-chat hint line is only reserved when it can render.
        var bottomReserve = inputH + ImGui.GetTextLineHeightWithSpacing()
            * (canSeal == false ? 2f : 1f) + SpheneUI.Spacing.X4.Scaled;
        var listH = SpheneUI.Layout.FillToViewBottomHeight(bottomReserve);

        if (SpheneUI.Layout.BeginScrollArea("chat-scroll",
                new Vector2(w, listH), chainWheel: false))
        {
            // Draw through the CHILD's draw list - the parent list is not
            // clipped by the scroll area, so bubbles painted there overdraw
            // the strip/composer whenever the content or window resizes.
            var dl = ImGui.GetWindowDrawList();
            var items = _chatService.History(uid);

            // Snap to the newest message on first open, and on new arrivals
            // only while the view already sits at the bottom - scrolling up
            // to read history must not be yanked back every frame/message.
            // Reopening the SAME uid counts as an open too: the child keeps
            // its previous scroll offset, and _chatScrollUid still matches,
            // so without the frame-gap check the view restores mid-history.
            // ScrollMax trails the items drawn this frame by one Begin pass,
            // and EndScrollArea's bottom spacer grows it a frame later still -
            // it is gated on LAST frame's scrollMax, so the range can grow
            // once more AFTER it first looked stable. The snap therefore
            // holds until the view sits at the bottom and the range has not
            // moved for two consecutive frames; releasing earlier strands a
            // few pixels of scroll and keeps the bottom fade lit.
            var frame = ImGui.GetFrameCount();
            var scrollY = ImGui.GetScrollY();
            var scrollMax = ImGui.GetScrollMaxY();
            var now = ImGui.GetTime();
            var switched = !string.Equals(_chatScrollUid, uid, StringComparison.Ordinal)
                || frame != _chatScrollFrame + 1;
            if (switched)
            {
                // ChatScroll telemetry: Debug level, gated by the
                // Settings > Diagnostics "Log Level" option (the
                // DalamudLogger drops Debug before it reaches /xllog).
                _logger.LogDebug(
                    "ChatScroll telemetry active - switch uid={Uid} f={Frame} y={Y:0.0} max={Max:0.0}",
                    uid, frame, scrollY, scrollMax);
                // A freshly opened conversation stays invisible while the
                // scroll snap converges, then reveals through a fade.
                _chatIntroActive = true;
                _chatIntroStart = now;
                _chatRevealStart = -1.0;
                _chatScrollDistance = 0f;
                _chatPrevScrollY = scrollY;
                _chatWheelUpFrame = -1;
                // Animated atlases for the previous conversation free their
                // GPU memory through the deferred media eviction queue.
                foreach (var key in _chatGifAnimKeys)
                {
                    SpheneUI.Media.Evict(key);
                }
                _chatGifAnimKeys.Clear();
                _chatGifMeta.Clear();
                _chatGifBytes.Clear();
            }
            // ImGui wheel scrolling is animated: a wheel tick writes
            // ScrollTarget once and Scroll eases toward it over many
            // frames. MouseWheel is nonzero on the tick frame only, so
            // steering detection must cover the whole sweep - an upward
            // scrollY delta means the user's wheel animation is still in
            // flight. Without that coverage a message arriving mid-sweep
            // re-arms the bottom snap, clobbers the in-flight target and
            // the view visibly sweeps back toward the newest message
            // ("springs back" while wheeling up from the bottom).
            var wheel = ImGui.GetIO().MouseWheel;
            var childHovered = ImGui.IsWindowHovered();
            if (childHovered && wheel > 0f)
            {
                _chatWheelUpFrame = frame;
            }
            if (MathF.Abs(wheel) > 0f)
            {
                // Unconditional: when the wheel visibly scrolls but no line
                // appears here, the input never reaches io.MouseWheel at
                // all; when lines appear with childHovered=false or a
                // foreign hovered window, something else eats the input.
                string hoveredName;
                unsafe
                {
                    var ctx = new ImGuiContextPtr(ImGuiNative.GetCurrentContext());
                    hoveredName = ctx.HoveredWindow.Handle == null
                        ? "-"
                        : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(
                            (IntPtr)ctx.HoveredWindow.Name) ?? "?";
                }
                _logger.LogDebug(
                    "ChatScroll wheel uid={Uid} f={Frame} y={Y:0.0} max={Max:0.0} ch={CH:0.0} wheel={Wheel:0.00} hover={Hover} childHov={ChildHov} pend={Pend} bottom={Bottom}",
                    uid, frame, scrollY, scrollMax, ImGui.GetWindowSize().Y,
                    wheel, hoveredName, childHovered,
                    _chatScrollPending, _chatWasAtBottom);
            }
            var steeringUp = frame - _chatWheelUpFrame <= 1
                || (scrollMax > 0f && scrollY < _chatPrevScrollY - 0.05f);
            var userSteers = steeringUp
                || (ImGui.IsWindowHovered() && MathF.Abs(wheel) > 0f)
                || (SpheneUI.Scrollbar.LastInteractionFrame >= 0
                    && frame - SpheneUI.Scrollbar.LastInteractionFrame <= 1);
            var wasPending = _chatScrollPending;
            var armReason = switched ? "switch"
                : items.Count != _chatScrollCount && _chatWasAtBottom ? "count"
                : null;
            if (armReason != null && (switched || !userSteers))
            {
                _chatScrollPending = true;
                _chatScrollSettle = 0;
                // A stale max from another conversation or an earlier snap
                // must never satisfy the settle check on the first frame.
                _chatScrollLastMax = -1f;
                _logger.LogDebug(
                    "ChatScroll arm({Reason}) uid={Uid} f={Frame} y={Y:0.0} max={Max:0.0} count={Old}->{New} steer={Steer}",
                    armReason, uid, frame, scrollY, scrollMax,
                    _chatScrollCount, items.Count, userSteers);
            }
            if (userSteers)
            {
                if (wasPending && _chatScrollPending)
                {
                    _logger.LogDebug(
                        "ChatScroll disarm-steer uid={Uid} f={Frame} y={Y:0.0} max={Max:0.0} wheel={Wheel:0.0} up={Up}",
                        uid, frame, scrollY, scrollMax, wheel, steeringUp);
                }
                _chatScrollPending = false;
            }
            if (_chatScrollPending)
            {
                ImGui.SetScrollY(scrollMax);
                _logger.LogDebug(
                    "ChatScroll snap uid={Uid} f={Frame} y={Y:0.0}->max={Max:0.0} settle={Settle} steer={Steer}",
                    uid, frame, scrollY, scrollMax, _chatScrollSettle, userSteers);
                if (scrollY >= scrollMax - 1f
                    && MathF.Abs(scrollMax - _chatScrollLastMax) < 0.01f)
                {
                    if (++_chatScrollSettle >= 2)
                    {
                        _chatScrollPending = false;
                        _logger.LogDebug(
                            "ChatScroll settled uid={Uid} f={Frame} y={Y:0.0} max={Max:0.0}",
                            uid, frame, scrollY, scrollMax);
                    }
                }
                else
                {
                    _chatScrollSettle = 0;
                }
                _chatScrollLastMax = scrollMax;
            }
            else if (!switched && MathF.Abs(scrollMax - _chatScrollPrevMax) > 0.5f)
            {
                // The scrollable range moved without a snap running - window
                // resize, reflowed bubble wraps or the bottom spacer toggling.
                // ImGui anchors scroll at the content TOP, so shrinking the
                // view would dump the messages below the composer edge.
                // Restoring the saved distance from the bottom re-applies
                // every frame the range keeps moving, so an interactive
                // resize drag stays pinned to the same bottom-edge message.
                // A pure bottom APPEND (new message while reading history)
                // is the exception: restoring bottom-distance would yank the
                // view down by the new bubble's height every arrival, so the
                // top-anchored scrollY must hold instead.
                // A scroll sweep still moving (either direction) means the
                // user's wheel/drag animation is in flight. Restoring here
                // would clobber its target every frame - and scrollMax
                // drifts by ~1px while scrolling (content-size noise), so
                // the restore used to fire on virtually every frame and
                // yank the view back ("springs back while scrolling").
                // Prepends are structural: a history page landing above
                // must keep the bottom-distance anchor even mid-sweep.
                var sweepInFlight = userSteers
                    || (scrollMax > 0f
                        && MathF.Abs(scrollY - _chatPrevScrollY) > 0.05f);
                var appended = items.Count > 0
                    && items[^1].Dto.Id != _chatScrollLastId;
                var prepended = items.Count > 0
                    && items[0].Dto.Id != _chatScrollFirstId;
                if (prepended || (!appended && !sweepInFlight))
                {
                    var target = scrollMax - _chatScrollDistance;
                    ImGui.SetScrollY(target);
                    _logger.LogDebug(
                        "ChatScroll restore uid={Uid} f={Frame} y={Y:0.0}->{T:0.0} max={Max:0.0} dmax={DMax:0.0} dist={Dist:0.0} app={App} pre={Pre} sweep={Sweep}",
                        uid, frame, scrollY, target, scrollMax,
                        scrollMax - _chatScrollPrevMax, _chatScrollDistance,
                        appended, prepended, sweepInFlight);
                }
                else
                {
                    // Top anchor holds for pure appends; during a sweep the
                    // noise restore is skipped - but the distance must stay
                    // current so a later real resize still pins correctly.
                    _chatScrollDistance = Math.Clamp(scrollMax - scrollY, 0f, scrollMax);
                    _logger.LogDebug(
                        "ChatScroll hold uid={Uid} f={Frame} y={Y:0.0} max={Max:0.0} dmax={DMax:0.0} app={App} pre={Pre} sweep={Sweep}",
                        uid, frame, scrollY, scrollMax,
                        scrollMax - _chatScrollPrevMax, appended, prepended,
                        sweepInFlight);
                }
            }
            else
            {
                // Remember where the user left the view: the distance from
                // the bottom edge is the anchor a later resize restores.
                _chatScrollDistance = Math.Clamp(scrollMax - scrollY, 0f, scrollMax);
            }
            _chatScrollPrevMax = scrollMax;
            // While the user steers the position latches only after the
            // sweep ends: a wheel-up sweep may still sit within the 8px
            // bottom window for its first frames and must not re-arm
            // stick-to-bottom during that approach.
            _chatWasAtBottom = _chatScrollPending
                || (!userSteers && scrollY >= scrollMax - 8f);
            _chatPrevScrollY = scrollY;
            _chatScrollUid = uid;
            _chatScrollCount = items.Count;
            _chatScrollFrame = frame;
            _chatScrollFirstId = items.Count > 0 ? items[0].Dto.Id : Guid.Empty;
            _chatScrollLastId = items.Count > 0 ? items[^1].Dto.Id : Guid.Empty;

            // Intro reveal: the conversation stays hidden until the scroll
            // has snapped to the bottom AND the first history page landed,
            // then fades in - the user sees the settled chat instead of the
            // scroll jump. The alpha only dims the paint: item layout stays
            // identical, so the scroll range converges while still hidden.
            // The wait cap keeps a stalled load from hiding it forever.
            if (_chatIntroActive && _chatRevealStart < 0.0
                && ((!_chatScrollPending && _chatService.IsLoaded(uid))
                    || now - _chatIntroStart > ChatIntroMaxHiddenSeconds))
            {
                _chatRevealStart = now;
            }
            var chatAlpha = 1f;
            if (_chatIntroActive)
            {
                chatAlpha = _chatRevealStart < 0.0
                    ? 0f
                    : Math.Clamp(
                        (float)(now - _chatRevealStart) / ChatIntroFadeSeconds,
                        0f, 1f);
                if (chatAlpha >= 1f)
                {
                    _chatIntroActive = false;
                }
            }
            var alphaPushed = chatAlpha < 0.999f;
            if (alphaPushed)
            {
                // Widget-resolved colors (empty state, body text) ride the
                // global alpha; the explicit draw-list colors are dimmed
                // through chatAlpha below.
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, chatAlpha);
            }
            if (items.Count == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.CommentDots,
                    "No messages yet", $"Say hi to {pair.UserData.AliasOrUID}.");
            }
            var padX = 10f * scale;
            var padY = 7f * scale;
            var gap = 4f * scale;
            var margin = 8f * scale;
            var bwMax = w * 0.62f;
            var wrapW = MathF.Max(40f, bwMax - padX * 2f);
            var round = 10f * scale;
            var ownCol = SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(accent, 0.35f * chatAlpha));
            var peerBase = SpheneUI.Layout.NavRailBandColor();
            var peerCol = SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(peerBase, peerBase.W * chatAlpha));
            // Meta line runs ~22% smaller and dimmer than the body text -
            // present but never competing with the message itself.
            var metaRatio = 0.78f;
            var metaSize = ImGui.GetFontSize() * metaRatio;
            var metaCol = SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.TextMuted,
                    0.65f * chatAlpha));
            // WhatsApp-style state ticks: single check = sent, double check =
            // delivered to the peer's client, blue double = read.
            var checkGlyph = FontAwesomeIcon.Check.ToIconString();
            var checkDoubleGlyph = FontAwesomeIcon.CheckDouble.ToIconString();
            float checkW, checkDoubleW;
            using (SpheneUI.Icons.PushFont())
            {
                checkW = ImGui.CalcTextSize(checkGlyph).X * metaRatio;
                checkDoubleW = ImGui.CalcTextSize(checkDoubleGlyph).X * metaRatio;
            }
            var font = ImGui.GetFont();
            DateTime? lastDay = null;
            for (var i = 0; i < items.Count; i++)
            {
                var m = items[i];
                var undecryptable = m.State == ChatEntryState.Undecryptable;
                var gif = undecryptable ? null : m.Gif;
                var text = undecryptable
                    ? "[encrypted message - cannot be decrypted on this device]"
                    : gif != null ? string.Empty : m.Text ?? string.Empty;
                var mine = string.Equals(m.Dto.Sender.UID, own,
                    StringComparison.Ordinal);
                var local = m.Dto.SentAt.ToLocalTime();
                if (lastDay != local.Date)
                {
                    DrawChatDaySeparator(dl, w, local.Date, margin, scale,
                        chatAlpha);
                    lastDay = local.Date;
                }
                var time = SpheneUI.Text.FormatTime(local);
                var timeW = ImGui.CalcTextSize(time).X * metaRatio;
                var isRead = m.Dto.Read;
                var isDelivered = m.Dto.Delivered || isRead;
                var tickW = mine
                    ? (isDelivered ? checkDoubleW : checkW) : 0f;
                var metaW = timeW + (mine ? tickW + 3f * scale : 0f);
                var gifMeta = gif != null ? ResolveChatGifMeta(gif) : null;
                var gifSize = gif != null
                    ? Helpers.Emoji.GifFit.Fit(gifMeta?.Width ?? gif.Width,
                        gifMeta?.Height ?? gif.Height, bwMax, scale)
                    : Vector2.Zero;
                var ts = gif != null ? gifSize : SpheneUI.Emoji.Measure(text, wrapW);
                // Stickers float bare on the chat surface - no bubble and no
                // media plate; GIFs keep their edge-to-edge bubble, text its
                // padded one. The time/tick line rides bare under the bubble
                // in every mode instead of inside the surface.
                var sticker = gifMeta?.IsSticker == true;
                var bw = MathF.Min(bwMax, gif != null
                    ? sticker ? gifSize.X : MathF.Max(gifSize.X, metaW + padX * 2f)
                    : MathF.Max(ts.X, metaW) + padX * 2f);
                var topPad = gif != null ? 0f : padY;
                var bubbleH = gif != null ? ts.Y : topPad + ts.Y + padY;
                var metaGap = 2f * scale;
                var bh = bubbleH + metaGap + metaSize;
                var bx = mine ? w - bw - margin : margin;
                var pos = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new Vector2(w, bh + gap));
                var next = ImGui.GetCursorPos();
                if (!sticker)
                {
                    dl.AddRectFilled(new Vector2(pos.X + bx, pos.Y),
                        new Vector2(pos.X + bx + bw, pos.Y + bubbleH),
                        mine ? ownCol : peerCol, round);
                }
                var contentPos = new Vector2(pos.X + bx + (gif != null ? 0f : padX),
                    pos.Y + topPad);
                if (gif != null)
                {
                    DrawChatGif(dl, contentPos, gif, gifMeta, gifSize,
                        round, chatAlpha, ImDrawFlags.RoundCornersAll, bare: sticker);
                }
                else
                {
                    SpheneUI.Emoji.DrawAt(dl, contentPos, text, bw - padX * 2f,
                        undecryptable
                            ? SpheneUI.Colors.TextMuted
                            : SpheneUI.Colors.TextPrimary,
                        chatAlpha);
                }
                // Own messages right-align the meta line to the bubble's
                // right edge, received ones to the left edge - the same
                // flush edge for text, GIFs and stickers.
                var metaX = mine
                    ? pos.X + bx + bw - metaW
                    : pos.X + bx;
                var metaY = pos.Y + bubbleH + metaGap;
                dl.AddText(font, metaSize, new Vector2(metaX, metaY),
                    metaCol, time);
                if (mine)
                {
                    var tickBase = isRead
                        ? SpheneUI.Palette.CrystalBlue
                        : SpheneUI.ColorUtils.WithAlpha(
                            SpheneUI.Colors.TextMuted,
                            isDelivered ? 0.7f : 0.45f);
                    var tickCol = SpheneUI.ColorUtils.ToU32(
                        SpheneUI.ColorUtils.WithAlpha(
                            tickBase, tickBase.W * chatAlpha));
                    using (SpheneUI.Icons.PushFont())
                    {
                        dl.AddText(ImGui.GetFont(), metaSize,
                            new Vector2(metaX + timeW + 3f * scale, metaY),
                            tickCol,
                            isDelivered ? checkDoubleGlyph : checkGlyph);
                    }
                }
                ImGui.SetCursorPos(next);
            }
            if (alphaPushed)
            {
                ImGui.PopStyleVar();
            }
            // The edge fades ride the same reveal as the bubbles - while the
            // conversation is still hidden they would only glow over empty
            // space.
            SpheneUI.Layout.EndScrollArea(fadeStrength: chatAlpha);
        }

        DrawChatSecurityStrip(pair, uid);

        var sealedBlocked = canSeal == false;
        if (_chatEmojiQueue.ConsumeRefocus())
        {
            ImGui.SetKeyboardFocusHere();
        }
        var frameMin = ImGui.GetCursorScreenPos();
        var dlFrame = ImGui.GetWindowDrawList();
        var frameMax = frameMin + new Vector2(composerW, inputH);
        // Enclosing rounded frame in the value-control look, drawn by hand
        // so the emoji/GIF/sticker caps live inside the field footprint and
        // ride the top edge while the field grows upward.
        dlFrame.AddRectFilled(frameMin, frameMax,
            ImGui.GetColorU32(ImGuiCol.FrameBg), SpheneUI.Metrics.ControlRadius);
        var borderV = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Border));
        dlFrame.AddRect(frameMin, frameMax,
            ImGui.ColorConvertFloat4ToU32(borderV with { W = MathF.Max(borderV.W, 0.35f) }),
            SpheneUI.Metrics.ControlRadius, ImDrawFlags.None, SpheneUI.Metrics.ControlBorderSize);
        if (sealedBlocked || _chatInput.Length == 0)
        {
            // hint sits where the multiline's inner child starts the text
            dlFrame.AddText(frameMin + new Vector2(
                    composerPadX + composerInset.X + 2f, composerTop + composerInset.Y),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    SpheneUI.Colors.TextMuted, 0.55f)),
                sealedBlocked ? "Encryption unavailable" : "Message");
        }
        using (ImRaii.Disabled(sealedBlocked))
        {
            ImGui.SetCursorScreenPos(frameMin + new Vector2(composerPadX, composerTop));
            SpheneUI.Controls.InputComposerMultiline("##chatinput", ref _chatInput,
                500, new Vector2(composerInnerW, composerWidgetH),
                Helpers.Emoji.EmojiEditQueue.OnCallback, ref _chatEmojiQueue);
            // Enter sends; Shift+Enter keeps the native newline. The check
            // rides IsItemActive so it only fires while the field has focus.
            var io = ImGui.GetIO();
            var enterPressed = (ImGui.IsKeyPressed(ImGuiKey.Enter, false)
                || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)) && !io.KeyShift;
            if (ImGui.IsItemActive() && enterPressed)
            {
                if (!string.IsNullOrWhiteSpace(_chatInput))
                {
                    _ = SendChatAsync(uid, _chatInput.Trim());
                    _chatInput = string.Empty;
                    // An own send always returns the view to the newest
                    // message, even while the user was reading older history.
                    _chatScrollPending = true;
                    _chatScrollSettle = 0;
                    _chatScrollLastMax = -1f;
                }
                else
                {
                    _chatInput = _chatInput.TrimEnd('\n');
                }
            }
            // Ghost caps fill the field's top-right corner right-to-left:
            // sticker outermost, then the GIF badge, emoji leftmost -
            // vertically centered on the first text band.
            var capRight = frameMax.X - composerPadX;
            var capY = frameMin.Y + composerTop + composerCapY;
            if (showGif)
            {
                capRight -= frameH;
                ImGui.SetCursorScreenPos(new Vector2(capRight, capY));
                _stickerPicker.TriggerButton("chat-sticker", item =>
                {
                    _ = SendChatGifAsync(uid, item);
                    _chatScrollPending = true;
                    _chatScrollSettle = 0;
                    _chatScrollLastMax = -1f;
                }, enabled: !sealedBlocked, ghost: true);
                capRight -= capGap + gifBadgeW;
                ImGui.SetCursorScreenPos(new Vector2(capRight, capY));
                _gifPicker.TriggerButton("chat-gif", item =>
                {
                    _ = SendChatGifAsync(uid, item);
                    _chatScrollPending = true;
                    _chatScrollSettle = 0;
                    _chatScrollLastMax = -1f;
                }, enabled: !sealedBlocked, ghost: true);
                capRight -= capGap;
            }
            if (emojiHook != null)
            {
                capRight -= frameH;
                ImGui.SetCursorScreenPos(new Vector2(capRight, capY));
                emojiHook.TriggerButton("chat-emoji", _chatEmojiQueue.Enqueue, ghost: true);
            }
        }
        ImGui.SetCursorScreenPos(frameMax);
        emojiHook?.Draw("chat-emoji");
        if (showGif)
        {
            _gifPicker.Draw("chat-gif");
            _stickerPicker.Draw("chat-sticker");
        }
        if (sealedBlocked)
        {
            SpheneUI.Text.Muted(
                $"Encryption keys are not available for {pair.UserData.AliasOrUID} yet " +
                "(they may need to update Sphene). Messages can't be sent without encryption.");
        }
    }

    /// <summary>Centered day pill between message groups (WhatsApp-style):
    /// "Today"/"Yesterday" for the last two local days, otherwise the
    /// canonical date. Drawn inside the chat scroll area's child. The label
    /// and rules are draw-list primitives, so the intro fade reaches them
    /// through <paramref name="alpha"/> rather than the global style alpha.</summary>
    private static void DrawChatDaySeparator(ImDrawListPtr dl, float w,
        DateTime day, float margin, float scale, float alpha)
    {
        var today = DateTime.Now.Date;
        var label = day == today ? "Today"
            : day == today.AddDays(-1) ? "Yesterday"
            : SpheneUI.Text.FormatDate(day);
        var th = ImGui.GetTextLineHeight();
        var padY = 9f * scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(w, th + padY * 2f));
        var ts = ImGui.CalcTextSize(label);
        var cx = pos.X + w * 0.5f;
        var cy = pos.Y + padY + th * 0.5f;
        var mutedBase = SpheneUI.Colors.TextMuted;
        var ruleBase = SpheneUI.ColorUtils.WithAlpha(mutedBase, 0.28f);
        var muted = SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(mutedBase, mutedBase.W * alpha));
        var rule = SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(ruleBase, ruleBase.W * alpha));
        var rulePad = 8f * scale;
        dl.AddLine(new Vector2(pos.X + margin, cy),
            new Vector2(cx - ts.X * 0.5f - rulePad, cy), rule);
        dl.AddText(new Vector2(cx - ts.X * 0.5f, pos.Y + padY), muted, label);
        dl.AddLine(new Vector2(cx + ts.X * 0.5f + rulePad, cy),
            new Vector2(pos.X + w - margin, cy), rule);
    }

    /// <summary>Sends a sealed message; seal failures surface as a warning
    /// notification instead of silently dropping the text.</summary>
    private async Task SendChatAsync(string uid, string text)
    {
        PublishSealFailure(await _chatService.Send(uid, text).ConfigureAwait(false));
    }

    /// <summary>Sends a GIF through the same sealed path as text - the
    /// provider reference rides inside the encrypted content.</summary>
    private async Task SendChatGifAsync(string uid, Services.Gifs.GifItem item)
    {
        Services.E2ee.ChatSealFailure failure;
        try
        {
            failure = await _chatService.SendGif(uid, _gifProvider.ProviderId,
                item.Id, item.Width, item.Height).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // a dropped send must never die silently on the fire-and-forget
            _logger.LogDebug(ex, "GIF send to {UID} failed for item {Id}",
                uid, item.Id);
            Mediator.Publish(new NotificationMessage("GIF not sent",
                "The GIF could not be sent. Try again.",
                SpheneConfiguration.Models.NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
            return;
        }
        if (failure == Services.E2ee.ChatSealFailure.None)
        {
            _gifProvider.RegisterShare(item);
        }
        PublishSealFailure(failure);
    }

    private void PublishSealFailure(Services.E2ee.ChatSealFailure failure)
    {
        if (failure == Services.E2ee.ChatSealFailure.None)
        {
            return;
        }
        var detail = failure switch
        {
            Services.E2ee.ChatSealFailure.NoRecipientDevices
                => "the recipient has not published encryption keys yet",
            Services.E2ee.ChatSealFailure.NoSelfDevices
                => "your encryption identity is not available",
            Services.E2ee.ChatSealFailure.NotConnected
                => "you are not connected to the server",
            _ => "sealing failed",
        };
        Mediator.Publish(new NotificationMessage("Message not sent",
            $"Could not encrypt the message: {detail}.",
            SpheneConfiguration.Models.NotificationType.Warning,
            TimeSpan.FromSeconds(6)));
    }

    /// <summary>Resolves GIF metadata for a received message; the task is
    /// cached so a scrolling history never refires provider lookups. Only
    /// the active provider's ids are looked up - a payload naming another
    /// provider renders as the bounded placeholder.</summary>
    private Services.Gifs.GifItem? ResolveChatGifMeta(
        Services.E2ee.ChatContentCodec.GifContent gif)
    {
        if (!string.Equals(gif.Provider, _gifProvider.ProviderId, StringComparison.Ordinal)
            || !_gifProvider.ConsentGiven)
        {
            // foreign providers and consent-off users render as the
            // bounded placeholder (the latter gets an enable button)
            return null;
        }
        var key = gif.Provider + ":" + gif.Id;
        if (!_chatGifMeta.TryGetValue(key, out var task)
            || TaskSettledEmpty(task))
        {
            // a settled empty/faulted task must not pin the placeholder
            // forever - the provider's negative cache keeps re-requests cheap
            task = _gifProvider.GetByIdAsync(gif.Id, CancellationToken.None);
            _chatGifMeta[key] = task;
        }
        return task is { IsCompletedSuccessfully: true } ? task.Result : null;
    }

    /// <summary>True for tasks that finished without a usable result
    /// (completed-null, faulted or cancelled) and so deserve a retry.</summary>
    private static bool TaskSettledEmpty<T>(Task<T?> task) where T : class
        => task.IsCompleted && (!task.IsCompletedSuccessfully || task.Result == null);

    /// <summary>Resolves (or queues) the media bytes for a GIF message.</summary>
    private byte[]? ResolveChatGifBytes(string id,
        Services.Gifs.GifItem? meta)
    {
        if (meta == null)
        {
            return null;
        }
        if (!_chatGifBytes.TryGetValue(id, out var task)
            || TaskSettledEmpty(task))
        {
            task = _gifProvider.FetchGifBytesAsync(meta, CancellationToken.None);
            _chatGifBytes[id] = task;
        }
        return task is { IsCompletedSuccessfully: true } ? task.Result : null;
    }

    /// <summary>
    /// GIF content inside a chat bubble: a themed plate underlay, the decoded
    /// animation (or first frame under reduced motion), and a restrained
    /// loading/failed state. Decode only starts once the row is on screen -
    /// scrolled-away history never spins up animation work.
    /// </summary>
    private void DrawChatGif(ImDrawListPtr dl, Vector2 pos,
        Services.E2ee.ChatContentCodec.GifContent gif,
        Services.Gifs.GifItem? meta, Vector2 size,
        float rounding, float alpha,
        ImDrawFlags corners = ImDrawFlags.RoundCornersAll, bool bare = false)
    {
        var max = pos + size;
        var plate = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
            SpheneUI.Layout.NavRailBandColor(), 0.9f * alpha));
        // Stickers draw bare - their own alpha is the shape. The plate still
        // backs every placeholder state (consent prompt, pending, failed)
        // because those need a surface to sit on.
        if (!bare)
        {
            dl.AddRectFilled(pos, max, plate, rounding, corners);
        }
        if (!ImGui.IsRectVisible(pos, max))
        {
            return;
        }
        if (!_gifProvider.ConsentGiven)
        {
            if (bare) dl.AddRectFilled(pos, max, plate, rounding, corners);
            DrawChatGifConsent(dl, pos, size, gif.Id, alpha);
            return;
        }
        var bytes = ResolveChatGifBytes(gif.Provider + ":" + gif.Id, meta);
        var anim = bytes != null
            ? SpheneUI.Media.Animated("chat-gif:" + gif.Id, bytes) : null;
        if (anim == null)
        {
            // bounded pending/failed state - the message stays a small
            // plate with a muted label, never a broken-media crater.
            if (bare) dl.AddRectFilled(pos, max, plate, rounding, corners);
            var labelCol = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Colors.TextMuted, 0.8f * alpha));
            var label = bare || meta?.IsSticker == true ? "Sticker" : "GIF";
            var ls = ImGui.CalcTextSize(label);
            dl.AddText(pos + (size - ls) * 0.5f, labelCol, label);
            return;
        }
        _chatGifAnimKeys.Add("chat-gif:" + gif.Id);
        double? time = _configService.Current.ChatGifAnimation ? null : 0.0;
        SpheneUI.Media.AnimatedInRect(dl, anim, pos, max,
            ImageFit.Fit,
            new Vector4(1f, 1f, 1f, alpha), rounding, time, corners);
        DrawChatGifWatermark(dl, pos, max, alpha);
        DrawChatGifStar(dl, pos, max, gif.Provider, meta, alpha);
    }

    /// <summary>Favorite star glyph at a rendered GIF's top-right corner
    /// (icon only + drop shadow, same language as the picker cells).
    /// Sent and received bubbles both allow pinning - the favorite needs
    /// the resolved <see cref="Services.Gifs.GifItem"/> for its rendition
    /// URLs, so the affordance waits for metadata instead of storing a
    /// dead reference.</summary>
    private void DrawChatGifStar(ImDrawListPtr dl, Vector2 pos, Vector2 max,
        string provider, Services.Gifs.GifItem? meta, float alpha)
    {
        if (meta == null)
        {
            return;
        }
        var scale = SpheneUI.Scale;
        var hit = 20f * scale;
        var hitMin = new Vector2(max.X - hit - 4f * scale, pos.Y + 4f * scale);
        var hot = ImGui.IsMouseHoveringRect(hitMin, hitMin + new Vector2(hit));
        var pinned = Services.Gifs.GifFavorites.IsPinned(
            _configService.Current.GifFavorites, provider, meta.Id);
        if (!pinned && !hot)
        {
            return;
        }
        var glyph = FontAwesomeIcon.Star.ToIconString();
        using (SpheneUI.IconFont.Push())
        {
            var gs = ImGui.CalcTextSize(glyph);
            var glyphMin = hitMin + (new Vector2(hit) - gs) * 0.5f;
            var shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f,
                (pinned || hot ? 0.75f : 0.5f) * alpha));
            var col = pinned
                ? SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Accent)
                : ImGui.GetColorU32(new Vector4(1f, 1f, 1f,
                    (hot ? 0.95f : 0.6f) * alpha));
            dl.AddText(glyphMin + new Vector2(scale), shadow, glyph);
            dl.AddText(glyphMin, col, glyph);
        }
        if (hot)
        {
            SpheneUI.AttachTooltip(pinned ? "Remove from favorites" : "Add to favorites");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                Services.Gifs.GifFavorites.Toggle(
                    _configService.Current.GifFavorites, provider, meta);
                _configService.Save();
            }
        }
    }

    /// <summary>Provider attribution at the bottom-left of a rendered GIF,
    /// as the KLIPY terms require: the official wordmark on a small dark
    /// plate so it stays readable over arbitrary frame content.</summary>
    private static void DrawChatGifWatermark(ImDrawListPtr dl, Vector2 pos,
        Vector2 max, float alpha)
    {
        var wrap = SpheneUI.Media.Texture(KlipyMarkKey, KlipyMarkPng);
        if (wrap == null)
        {
            return;
        }
        var wmH = 11f * SpheneUI.Scale;
        var wmW = wmH * ((float)wrap.Width / wrap.Height);
        var pad = 2.5f * SpheneUI.Scale;
        var inset = 5f * SpheneUI.Scale;
        var plateMin = new Vector2(pos.X + inset,
            max.Y - inset - wmH - pad * 2f);
        var plateMax = plateMin + new Vector2(wmW + pad * 2.5f,
            wmH + pad * 2f);
        dl.AddRectFilled(plateMin, plateMax,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f * alpha)),
            3f * SpheneUI.Scale);
        var imgMin = plateMin + new Vector2(pad * 1.25f, pad);
        SpheneUI.Media.ImageInRect(dl, wrap, imgMin,
            imgMin + new Vector2(wmW, wmH),
            tint: new Vector4(1f, 1f, 1f, alpha));
    }

    private const string KlipyMarkKey = "klipy-watermark";
    private const string KlipyMarkResource = "Sphene.Resources.Gifs.klipy-mark.png";

    private static byte[]? KlipyMarkPng()
    {
        try
        {
            using var stream = typeof(SpheneHubUi).Assembly
                .GetManifestResourceStream(KlipyMarkResource);
            if (stream == null)
            {
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Consent prompt inside a received GIF plate when external
    /// media is disabled: a small label plus an enable button that flips
    /// the same flag as the picker disclosure card and Settings toggle.
    /// </summary>
    private void DrawChatGifConsent(ImDrawListPtr dl, Vector2 pos,
        Vector2 size, string gifId, float alpha)
    {
        // every bubble needs its own ImGui id or the buttons share state
        using var _ = ImRaii.PushId("chatgif-consent-" + gifId);
        var style = ImGui.GetStyle();
        var label = "GIF";
        var ls = ImGui.CalcTextSize(label);
        var labelCol = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
            SpheneUI.Colors.TextMuted, 0.8f * alpha));
        const string btnLabel = "Enable";
        var btnW = ImGui.CalcTextSize(btnLabel).X + style.FramePadding.X * 2f;
        var gap = 6f;
        var totalH = ls.Y + ImGui.GetFrameHeight() + gap;
        var labelY = pos.Y + (size.Y - totalH) * 0.5f;
        dl.AddText(new Vector2(pos.X + (size.X - ls.X) * 0.5f, labelY),
            labelCol, label);
        ImGui.SetCursorScreenPos(new Vector2(
            pos.X + (size.X - btnW) * 0.5f, labelY + ls.Y + gap));
        if (SpheneUI.Controls.Button(btnLabel + "##chatgif-consent"))
        {
            _configService.Current.ChatExternalMediaEnabled = true;
            _configService.Save();
        }
        SpheneUI.AttachTooltip("Loads GIFs from " + _gifProvider.DisplayName
            + " servers. Can be changed in Settings anytime.");
    }

    /// <summary>
    /// One-line encryption status for the conversation plus a verify affordance.
    /// Honest about mixed history: legacy plaintext rows are not E2EE even
    /// though new sends are sealed, and undecryptable rows are called out.
    /// </summary>
    private void DrawChatSecurityStrip(Pair pair, string uid)
    {
        var items = _chatService.History(uid);
        var hasPlain = false;
        var hasSealed = false;
        var hasLocked = false;
        for (var i = 0; i < items.Count; i++)
        {
            hasPlain |= items[i].IsPlaintext;
            hasSealed |= items[i].IsSealed;
            hasLocked |= items[i].State == ChatEntryState.Undecryptable;
        }

        if (!_chatService.PeerKeyChanged(uid))
        {
            // Re-arm the dismiss set: the flag cleared, so the NEXT change
            // must warn again instead of staying hidden by the old dismissal.
            _chatKeyWarnDismissed.Remove(uid);
        }
        else if (!_chatKeyWarnDismissed.Contains(uid))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, SpheneUI.Palette.NetworkWarning))
            {
                SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle);
            }
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            SpheneUI.Text.Muted("Encryption key changed - verify before trusting.");
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Dismiss##keywarn"))
            {
                _chatKeyWarnDismissed.Add(uid);
                _chatService.DismissPeerKeyChange(uid);
            }
        }

        var (icon, label, color) = hasPlain
            ? (FontAwesomeIcon.LockOpen,
               "New messages are end-to-end encrypted · older history is still plaintext",
               SpheneUI.Palette.NetworkWarning)
            : (FontAwesomeIcon.Lock,
               "End-to-end encrypted",
               SpheneUI.Palette.NetworkActive);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            SpheneUI.Icons.Icon(icon);
        }
        // the lock icon itself is the verify affordance - no separate button
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        SpheneUI.AttachTooltip("Verify identity");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ImGui.OpenPopup("chat-verify##" + uid);
        }
        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        SpheneUI.Text.Muted(label + (hasLocked
            ? " · some messages can't be decrypted here"
            : string.Empty));
        DrawChatVerifyPopup(pair, uid);
    }

    private readonly ConcurrentDictionary<string, (Task<string?> Task, DateTime At)>
        _chatSafetyNumbers = new(StringComparer.Ordinal);

    /// <summary>Verification popup: pairwise safety number plus the peer's
    /// newest key fingerprint for out-of-band comparison. The number is
    /// recomputed at most every 30s so a rotated peer key does not leave a
    /// stale value on screen.</summary>
    private void DrawChatVerifyPopup(Pair pair, string uid)
    {
        var popupId = "chat-verify##" + uid;
        if (!SpheneUI.Popups.BeginStyled(popupId))
        {
            return;
        }
        try
        {
            if (!_chatSafetyNumbers.TryGetValue(uid, out var cached)
                || DateTime.UtcNow - cached.At > TimeSpan.FromSeconds(30))
            {
                cached = (_chatService.SafetyNumberFor(uid), DateTime.UtcNow);
                _chatSafetyNumbers[uid] = cached;
            }
            var task = cached.Task;
            var number = task.IsCompletedSuccessfully ? task.Result : null;
            var fingerprint = _chatService.PeerFingerprintFor(uid);
            SpheneUI.Text.Muted($"Verify {pair.UserData.AliasOrUID}");
            if (!task.IsCompleted)
            {
                SpheneUI.Text.Muted("Resolving encryption keys…");
            }
            else if (number == null)
            {
                SpheneUI.Text.Muted("Encryption keys for this contact are not known yet.");
            }
            else
            {
                SpheneUI.Text.Muted("Compare this safety number with your contact -");
                SpheneUI.Text.Muted("if it matches on both sides, messages can't be");
                SpheneUI.Text.Muted("intercepted by the server.");
                SpheneUI.Text.Muted(number);
                if (fingerprint != null)
                {
                    SpheneUI.Text.Muted($"Key: {fingerprint}");
                }
            }
        }
        finally
        {
            SpheneUI.Popups.EndStyled();
        }
    }

    /// <summary>Fixed chrome of the friend detail view · themed hero banner
    /// (name, avatar, banner art, presence) plus the friend tab strip.
    /// Drawn in the nav content's fixed zone.</summary>
    private void DrawFriendChrome(Pair pair, Vector4 accent,
        SpheneUI.Profile.HeroHost heroHost)
    {
        var theme = SpheneTheme.Current;
        var profile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        var friendTheme = profile.ProfileTheme.Value;
        var avatar = GetFriendTexture(FriendAvatarKey, profile.ImageData.Value, ref _friendAvatarBytes);
        var banner = GetFriendTexture(FriendBannerKey, profile.BannerImageData.Value, ref _friendBannerBytes);

        var friendSubtitle = !string.IsNullOrWhiteSpace(pair.UserData.Alias)
            ? "@" + pair.UserData.Alias
            : string.Empty;
        SpheneUI.Profile.Hero(pair.UserData.PreferredName, friendSubtitle, profile,
            avatar, banner, friendTheme, accent, theme.AccentBlue,
            isNsfw: profile.IsNSFW,
            presence: pair.IsMutuallyVisible || pair.IsOnline
                ? SpheneUI.Palette.NetworkActive : (Vector4?)null,
            host: heroHost);

        // Sub-pages reset when navigating between friends - unless a deep
        // link planted a target tab (feed → Chronicles, etc.).
        if (!string.Equals(_friendPageUid, pair.UserData.UID, StringComparison.Ordinal))
        {
            _friendPageUid = pair.UserData.UID;
            _friendPageTab = _pendingFriendTab ?? 0;
            _profileViewerImages.Clear();
            _friendActivityItems = null;
            _friendActivityCursor = null;
        }
        else if (_pendingFriendTab is { } friendTab)
        {
            _friendPageTab = friendTab;
        }
        _pendingFriendTab = null;
        _friendTabItems.Clear();
        _friendTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.IdCard, "Profile"));
        _friendTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.Images, "Gallery"));
        _friendTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.FeatherAlt, "Chronicles"));
        _friendTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.Comment, "Chat"));
        _friendTabItems.Add(new SpheneUI.Controls.TabItem(FontAwesomeIcon.Bolt, "Activity"));
        _friendPageTab = Math.Clamp(_friendPageTab, 0, _friendTabItems.Count - 1);
        var clickedTab = SpheneUI.Controls.TabBar("##friend-tabs", _friendTabItems,
            _friendPageTab, accent);
        if (clickedTab >= 0 && clickedTab != _friendPageTab)
        {
            _friendPageTab = clickedTab;
            _friendTabSwitchT = 0f;
            CloseEmbeddedViewers();
        }
    }

    /// <summary>Scrollable body of the friend detail view · the active tab's
    /// content with the tab-switch slide ease and the image viewer overlay.</summary>
    private void DrawFriendView(Pair pair, Vector4 accent)
    {
        var vis = (ProfileVisibility)_spheneProfileManager
            .GetSpheneProfile(pair.UserData).Visibility;
        _friendTabSwitchT = MathF.Min(1f, _friendTabSwitchT + ImGui.GetIO().DeltaTime / 0.16f);
        var tabEase = 1f - MathF.Pow(1f - _friendTabSwitchT, 3f);
        var tabIndent = (1f - tabEase) * 14f * SpheneUI.Scale;
        if (tabIndent > 0.01f)
        {
            ImGui.Indent(tabIndent);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.35f + 0.65f * tabEase);
        switch (_friendPageTab)
        {
            case 1:
                DrawFriendProfileGallery(pair, accent);
                break;
            case 2:
                DrawFriendProfileBlog(pair, accent);
                break;
            case 3:
                DrawFriendChat(pair, accent);
                break;
            case 4:
                DrawFriendActivity(pair, accent);
                break;
            default:
                DrawFriendProfilePage(pair, vis, accent);
                break;
        }
        ImGui.PopStyleVar();
        if (tabIndent > 0.01f)
        {
            ImGui.Unindent(tabIndent);
        }

        DrawProfileViewerOverlay();
    }

    /// <summary>
    /// Activity tab - the friend's recent public-facing activity (blogs,
    /// posts in shared shells, comments/reactions on visible hosts). Server
    /// applies the full gate chain; invisible profile and empty activity
    /// share the same empty shape, so this draw path needs no special-casing.
    /// </summary>
    private void DrawFriendActivity(Pair pair, Vector4 accent)
    {
        if (_friendActivityItems == null && !_friendActivityLoading
            && _apiController.IsConnected)
        {
            _friendActivityLoading = true;
            var uid = pair.UserData.UID;
            _ = Task.Run(async () =>
            {
                try
                {
                    var page = await _apiController.UserGetProfileActivity(
                        uid, new FeedPageRequestDto()).ConfigureAwait(false);
                    _friendActivityItems = page.Items;
                    _friendActivityCursor = page.NextCursor;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load profile activity for {UID}", uid);
                    _friendActivityItems = [];
                }
                finally { _friendActivityLoading = false; }
            });
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        if (_friendActivityItems == null)
        {
            for (var i = 0; i < 3; i++)
            {
                SpheneUI.Fx.SkeletonLine(0.85f - i * 0.12f);
            }
            return;
        }
        if (_friendActivityItems.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Bolt,
                "No recent activity", "Posts and interactions will land here.");
            return;
        }
        for (var i = 0; i < _friendActivityItems.Count; i++)
        {
            var item = _friendActivityItems[i];
            if (FeedItemRow.Draw($"fact-{item.Kind}-{item.Id}-{i}", item,
                    "hub:", fallbackAccent: accent,
                    onActorClick: NavigateToAuthor))
            {
                NavigateFeedItem(item);
            }
            if (i + 1 < _friendActivityItems.Count)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
        }
        if (_friendActivityCursor != null)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(_friendActivityLoading))
            {
                if (SpheneUI.Controls.Button("Load more##factmore"))
                {
                    _friendActivityLoading = true;
                    var uid = pair.UserData.UID;
                    var cursor = _friendActivityCursor;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var page = await _apiController.UserGetProfileActivity(
                                uid, new FeedPageRequestDto(cursor)).ConfigureAwait(false);
                            _friendActivityItems?.AddRange(page.Items);
                            _friendActivityCursor = page.NextCursor;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to page profile activity for {UID}", uid);
                        }
                        finally { _friendActivityLoading = false; }
                    });
                }
            }
        }
    }

    /// <summary>The Profile sub-tab of the friend view · connection status and privacy hints.</summary>
    private void DrawFriendProfilePage(Pair pair, ProfileVisibility vis, Vector4 accent)
    {
        var statusText = pair.IsMutuallyVisible ? "Visible" : pair.IsOnline ? "Online" : "Offline";
        var statusColor = pair.IsMutuallyVisible || pair.IsOnline
            ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted;
        var note = pair.GetNote();
        var editingNote = string.Equals(_noteEditUid, pair.UserData.UID, StringComparison.Ordinal);

        // Personal overview first - bio, recent Chronicles and photos answer
        // "who is this" before the connection meta below. All content rides
        // the same server-gated extras the dedicated tabs use.
        var friendProfile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        var friendExtras = _spheneProfileManager.GetProfileExtras(pair.UserData);
        DrawProfileAboutMe(friendProfile, accent, canEdit: false);
        DrawChroniclePreviewSection(friendExtras, accent, isOwn: false,
            () => NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair,
                SubPage: 2)),
            id =>
            {
                _expandedChronicles.Add(id);
                NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair,
                    SubPage: 2));
            });
        DrawPhotoPreviewSection(friendExtras, pair.UserData.UID,
            $"friend:{pair.UserData.UID}", accent, isOwn: false,
            () => NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair,
                SubPage: 1)));

        SyncshellCommunityView.Widget("friend-connection", "Connection",
            FontAwesomeIcon.Link, null, accent, () =>
        {
            var statusValue = "\u25CF " + statusText;
            if (pair.IsMutuallyVisible && !string.IsNullOrEmpty(pair.PlayerName))
            {
                statusValue += $" ({pair.PlayerName})";
            }
            SpheneUI.Rows.Value("Status", statusValue, FontAwesomeIcon.InfoCircle,
                valueColor: statusColor);

            SpheneUI.Layout.Separator();
            if (editingNote)
            {
                DrawFriendNoteEditRow(pair);
            }
            else
            {
                SpheneUI.Rows.Nav("Your note", note ?? " · ", () =>
                {
                    _noteEditUid = pair.UserData.UID;
                    _noteEditBuffer = note ?? string.Empty;
                }, FontAwesomeIcon.StickyNote);
            }

            if (pair.UserPair != null)
            {
                SpheneUI.Layout.Separator();
                SpheneUI.Rows.Value("Paired",
                    pair.IsDirectlyPaired ? "Directly" : "Via syncshells", FontAwesomeIcon.UserCheck);
                if (pair.UserPair.OwnPermissions.IsPaused())
                {
                    SpheneUI.Layout.Separator();
                    SpheneUI.Rows.Value("Sync to them", "Paused", FontAwesomeIcon.Pause,
                        valueColor: SpheneUI.Colors.Warning);
                }
                if (pair.UserPair.OtherPermissions.IsPaused())
                {
                    SpheneUI.Layout.Separator();
                    SpheneUI.Rows.Value("Sync to you", "Paused", FontAwesomeIcon.Pause,
                        valueColor: SpheneUI.Colors.Warning);
                }
                foreach (var gid in pair.UserPair.Groups)
                {
                    var shell = _pairManager.GroupPairs.Keys.FirstOrDefault(k =>
                        k.Group != null && string.Equals(k.Group.GID, gid, StringComparison.Ordinal));
                    if (shell?.Group != null)
                    {
                        SpheneUI.Layout.Separator();
                        var capturedGid = gid;
                        SpheneUI.Rows.Nav(shell.GroupAliasOrGID, null,
                            () => NavigateTo(new HubLocation(SpheneHubPage.Syncshells, capturedGid, null)),
                            FontAwesomeIcon.Users);
                    }
                }
            }
        },
        null,
        editingNote ? null : (FontAwesomeIcon.Pen,
            (string.IsNullOrEmpty(note) ? "Add a note for " : "Edit your note for ")
            + pair.UserData.PreferredName,
            (Action)(() =>
            {
                _noteEditUid = pair.UserData.UID;
                _noteEditBuffer = note ?? string.Empty;
            })));

        if (!vis.HasFlag(ProfileVisibility.HideLinks))
        {
            var friendLinks = _spheneProfileManager.GetSpheneProfile(pair.UserData).Links?
                .Where(l => !string.IsNullOrWhiteSpace(l.Url)).ToList();
            if (friendLinks is { Count: > 0 })
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SyncshellCommunityView.Widget("friend-links", "Links",
                    FontAwesomeIcon.Link, friendLinks.Count, accent, () =>
                    {
                        SpheneUI.Layout.CardGrid(friendLinks, 170f * SpheneUI.Scale, (link, i) =>
                            SyncshellCommunityView.DrawLinkCard(link.Label, link.Url, link.Icon,
                                accent, i));
                    });
            }
        }

        var hiddenHints = new List<string>();
        if (vis.HasFlag(ProfileVisibility.HideProfilePicture)) hiddenHints.Add("profile picture");
        if (vis.HasFlag(ProfileVisibility.HideBanner)) hiddenHints.Add("banner");
        if (vis.HasFlag(ProfileVisibility.HideDescription)) hiddenHints.Add("description");
        if (vis.HasFlag(ProfileVisibility.HideStatusMessage)) hiddenHints.Add("status message");
        if (vis.HasFlag(ProfileVisibility.HideTags)) hiddenHints.Add("tags");
        if (vis.HasFlag(ProfileVisibility.HideLinks)) hiddenHints.Add("links");
        if (hiddenHints.Count > 0)
        {
            SpheneUI.Layout.Callout(
                $"{pair.UserData.AliasOrUID} hides their {string.Join(", ", hiddenHints)}.",
                FontAwesomeIcon.EyeSlash, SpheneUI.Colors.TextMuted, title: "Private sections");
        }
    }

    /// <summary>
    /// Inline note editor as a row · enter commits, right-click cancels,
    /// matching the previous card behavior inside the RowGroup frame.
    /// </summary>
    private void DrawFriendNoteEditRow(Pair pair)
    {
        SpheneUI.GroupState.RowStart();
        var inputW = MathF.Min(320f * SpheneUI.Scale, ImGui.GetContentRegionAvail().X);
        if (SpheneUI.Controls.InputTextWithHint("##friendNoteEdit",
                "Your note for them", ref _noteEditBuffer, 100,
                ImGuiInputTextFlags.EnterReturnsTrue, preferredWidth: inputW))
        {
            _serverConfigurationManager.SetNoteForUid(pair.UserData.UID,
                _noteEditBuffer.Trim(), save: true);
            _noteEditUid = null;
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _noteEditUid = null;
        }
        SpheneUI.AttachTooltip("Hit ENTER to save\nRight click to cancel");
        SpheneUI.GroupState.RowEnd();
    }

    /// <summary>Projects profile extras images onto the shared gallery viewer DTO.</summary>
    private static void ToViewerImages(IReadOnlyList<UserProfileGalleryImageDto> images,
        List<SyncshellGalleryImageDto> target, string ownerKey)
    {
        target.Clear();
        foreach (var img in images)
        {
            target.Add(new SyncshellGalleryImageDto(img.Id, ownerKey, img.ImageBase64,
                img.FileName, img.ContentType, img.Size, img.Caption, img.SortOrder,
                img.CreatedAt, img.AlbumId, null, SyncshellGalleryImageStatus.Published,
                img.LikeCount, img.LikedByMe));
        }
    }

    /// <summary>Shared photo lightbox for both profile surfaces · same open ease as community.</summary>
    private void DrawProfileViewerOverlay()
    {
        if (_profileViewerImages.Count == 0)
        {
            return;
        }
        _profileViewerOpenT = MathF.Min(1f, _profileViewerOpenT + ImGui.GetIO().DeltaTime / 0.2f);
        var hostPos = ImGui.GetWindowPos();
        var hostSize = ImGui.GetWindowSize();
        // Child overlay like the community viewer: it lives inside the host's
        // draw list, so it can never lose z-order and turn into an invisible
        // input ghost, and the host's resize hit test pierces child windows
        // so the corner grip keeps working underneath it.
        ImGui.SetCursorScreenPos(hostPos);
        ImGui.BeginChild("##profile-viewer", hostSize, false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground);
        try
        {
            var isOwnViewer = _profileViewerKey?.StartsWith("own:", StringComparison.Ordinal) == true;
            var keepOpen = SyncshellImageViewer.Draw(_profileViewerImages, _profileViewerIndex,
                "hubprofile:", _profileViewerKey ?? string.Empty, closeOnBackdrop: true,
                setIndex: i => _profileViewerIndex = i,
                onClose: () =>
                {
                    _profileViewerImages.Clear();
                    _photoEditOpen = false;
                    _photoEditImageId = 0;
                    _photoEditError = null;
                },
                onPopOut: () =>
                {
                    // The popped-out window keeps the list it receives, so
                    // hand it a copy before clearing the overlay's field.
                    Mediator.Publish(new OpenSyncshellImageViewerMessage(
                        new List<SyncshellGalleryImageDto>(_profileViewerImages),
                        _profileViewerIndex, _profileViewerKey ?? string.Empty,
                        ProfileViewerTitle()));
                    _profileViewerImages.Clear();
                    _photoEditOpen = false;
                    _photoEditImageId = 0;
                    _photoEditError = null;
                },
                openT: _profileViewerOpenT,
                onLike: i => _ = ToggleProfileImageLike(i),
                editOpen: isOwnViewer && _photoEditOpen,
                onEditToggle: isOwnViewer ? () =>
                {
                    _photoEditOpen = !_photoEditOpen;
                    if (!_photoEditOpen) _photoEditImageId = 0;
                }
                : null,
                drawEditor: isOwnViewer ? DrawPhotoEditor : null,
                editHeight: isOwnViewer ? 76f * SpheneUI.Scale
                    + (_photoEditError != null ? ImGui.GetTextLineHeight() + 8f * SpheneUI.Scale : 0f)
                    : null);
            if (_pendingViewerRemoveId is { } removedId)
            {
                _profileViewerImages.RemoveAll(v => v.Id == removedId);
                _pendingViewerRemoveId = null;
                _profileViewerIndex = Math.Clamp(_profileViewerIndex, 0,
                    Math.Max(0, _profileViewerImages.Count - 1));
            }
            if (!keepOpen)
            {
                _profileViewerImages.Clear();
                _photoEditOpen = false;
                _photoEditImageId = 0;
                _photoEditError = null;
            }
        }
        catch (Exception ex)
        {
            // Same contract as the community viewer: a broken overlay must
            // close, not linger invisible while it eats input.
            SpheneUI.LogError(ex, "Profile viewer draw failed");
            _profileViewerImages.Clear();
            _photoEditOpen = false;
            _photoEditImageId = 0;
            _photoEditError = null;
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>Display title for the profile-gallery popout window - a real
    /// name, never the raw owner key the viewer uses internally.</summary>
    private string ProfileViewerTitle()
    {
        var key = _profileViewerKey ?? string.Empty;
        if (key.StartsWith("friend:", StringComparison.Ordinal))
        {
            var pair = _pairManager.GetPairByUID(key["friend:".Length..]);
            return pair != null ? $"{pair.UserData.PreferredName} · Gallery" : "Gallery";
        }
        return "My Gallery";
    }

    /// <summary>
    /// The viewer's edit drawer · caption field, album chips and a two-click
    /// delete rendered inside the lightbox where the photo stays big. No
    /// popups, no context menus: edit at the photo itself.
    /// </summary>
    private void DrawPhotoEditor(SyncshellGalleryImageDto img, Vector2 min, float w)
    {
        var scale = SpheneUI.Scale;
        var gap = SpheneUI.Spacing.X8.Scaled;
        if (_photoEditImageId != img.Id)
        {
            _photoEditImageId = img.Id;
            _photoEditCaption = img.Caption ?? string.Empty;
            _photoEditAlbumId = img.AlbumId;
            _photoDeleteArmed = false;
            _photoEditError = null;
        }
        var rowH = ImGui.GetFrameHeight();
        var saveW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Check).X;
        ImGui.SetCursorScreenPos(min);
        SpheneUI.Controls.InputTextWithHint("##photo-edit-cap", "Caption…",
            ref _photoEditCaption, 200, preferredWidth: w - saveW - gap);
        ImGui.SetCursorScreenPos(new Vector2(min.X + w - saveW, min.Y));
        if (_photoEditSaving)
        {
            SpheneUI.Icons.Icon(FontAwesomeIcon.Spinner, SpheneUI.Colors.TextMuted);
        }
        else if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
        {
            _ = SavePhotoEdit(img);
        }
        SpheneUI.AttachTooltip(_photoEditSaving ? "Saving…" : "Save caption & album");
        var row2Y = min.Y + rowH + 8f * scale;
        ImGui.SetCursorScreenPos(new Vector2(min.X, row2Y));
        var me = new UserData(_apiController.UID);
        DrawAlbumChipRow(_spheneProfileManager.GetProfileExtras(me)?.Albums,
            _photoEditAlbumId, f => _photoEditAlbumId = f, "pea");
        var delW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X;
        ImGui.SetCursorScreenPos(new Vector2(min.X + w - delW, row2Y));
        if (SpheneUI.Icons.Button(
                _photoDeleteArmed ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.Trash))
        {
            if (_photoDeleteArmed)
            {
                _ = DeleteCurrentViewerPhoto(img);
            }
            else
            {
                _photoDeleteArmed = true;
            }
        }
        SpheneUI.AttachTooltip(_photoDeleteArmed
            ? "Click again to delete this photo" : "Delete photo");
        if (_photoEditError is { } photoEditError)
        {
            ImGui.SetCursorScreenPos(new Vector2(min.X, row2Y + rowH + 4f * scale));
            SpheneUI.Text.Colored(photoEditError, SpheneUI.Palette.Error);
        }
    }

    /// <summary>Saves caption + album for the viewed photo and patches both caches.
    /// Success closes the edit drawer; failure keeps it open with the draft intact.</summary>
    private async Task SavePhotoEdit(SyncshellGalleryImageDto img)
    {
        if (_photoEditSaving)
        {
            return;
        }
        _photoEditSaving = true;
        _photoEditError = null;
        try
        {
            var me = new UserData(_apiController.UID);
            var result = await _apiController.UserUpsertProfileGalleryImage(
                new UserProfileGalleryImageUpdateDto(me, img.Id, null, null, null, null,
                    string.IsNullOrWhiteSpace(_photoEditCaption) ? null : _photoEditCaption.Trim(),
                    img.SortOrder, _photoEditAlbumId)).ConfigureAwait(false);
            if (result == null)
            {
                _photoEditError = "Couldn't save the photo - changes kept, try again.";
                return;
            }
            _spheneProfileManager.UpdateProfileExtras(me, e => e with
            {
                Gallery = e.Gallery?.Select(g => g.Id == result.Id ? result : g).ToList()
            });
            var idx = _profileViewerImages.FindIndex(v => v.Id == result.Id);
            if (idx >= 0)
            {
                _profileViewerImages[idx] = _profileViewerImages[idx] with
                {
                    Caption = result.Caption, AlbumId = result.AlbumId, SortOrder = result.SortOrder
                };
            }
            _photoEditOpen = false;
            _photoEditImageId = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save profile photo {ImageId}", img.Id);
            _photoEditError = "Couldn't save the photo - changes kept, try again.";
        }
        finally
        {
            _photoEditSaving = false;
        }
    }

    /// <summary>Deletes the viewed photo · removal from the viewer list defers one frame.</summary>
    private async Task DeleteCurrentViewerPhoto(SyncshellGalleryImageDto img)
    {
        var me = new UserData(_apiController.UID);
        if (!await _apiController.UserDeleteProfileGalleryImage(
                new UserProfileItemDeleteDto(me, img.Id)).ConfigureAwait(false))
        {
            return;
        }
        _photoDeleteArmed = false;
        _pendingViewerRemoveId = img.Id;
        _spheneProfileManager.UpdateProfileExtras(me, e => e with
        {
            Gallery = e.Gallery?.Where(g => g.Id != img.Id).ToList()
        });
    }

    private void OpenProfileViewer(List<SyncshellGalleryImageDto> images, int index, string ownerKey)
    {
        _profileViewerImages.Clear();
        _profileViewerImages.AddRange(images);
        _profileViewerIndex = index;
        _profileViewerOpenT = 0f;
        _profileViewerKey = ownerKey;
        _photoEditOpen = false;
        _photoEditImageId = 0;
        _photoDeleteArmed = false;
    }

    /// <summary>Own Gallery sub-tab · album chips, grid plus add-photo affordance and per-tile manage.</summary>
    private void DrawOwnProfileGallery(Vector4 accent)
    {
        var me = new UserData(_apiController.UID);
        var extras = _spheneProfileManager.GetProfileExtras(me);
        DrawOwnAlbumsFlyout(extras);
        SyncshellCommunityView.Widget("own-gallery", "Photo wall",
            FontAwesomeIcon.Images, extras?.Gallery?.Count, accent, () =>
        {
            if (_galleryUploadPhase != null)
            {
                SyncshellCommunityView.DrawUploadProgressRow(_galleryUploadPhase);
            }
            if (extras == null)
            {
                SpheneUI.Fx.SkeletonLine(1f, 88f * SpheneUI.Scale);
                return;
            }
            DrawAlbumChipRow(extras.Albums, _ownGalleryAlbumFilter,
                f => _ownGalleryAlbumFilter = f, "pga");
            var gallery = extras.Gallery;
            if (gallery is not { Count: > 0 })
            {
                SpheneUI.Text.MutedWrapped(
                    "No photos yet · drop in venue shots, glamours or event posters and your profile starts telling your story.");
                return;
            }
            var filtered = _ownGalleryAlbumFilter is { } ownFilter
                ? gallery.Where(g => g.AlbumId == ownFilter).ToList()
                : gallery;
            if (filtered.Count == 0)
            {
                SpheneUI.Text.MutedWrapped("Nothing in this album yet · upload while it is selected or move photos via their caption editor.");
                return;
            }
            ToViewerImages(filtered, _profileGalleryScratch, $"own:{_apiController.UID}");
            SyncshellCommunityView.DrawGalleryGrid(_profileGalleryScratch, "hubown:", _apiController.UID,
                onClick: i => OpenProfileViewer(_profileGalleryScratch, i, $"own:{_apiController.UID}"),
                onEdit: i =>
                {
                    OpenProfileViewer(_profileGalleryScratch, i, $"own:{_apiController.UID}");
                    _photoEditOpen = true;
                });
        },
        null,
        (FontAwesomeIcon.FolderOpen, "Manage albums",
            () => SpheneUI.Popups.ToggleFlyout("own-gallery-albums")),
        (FontAwesomeIcon.Plus, "Add photo", PickOwnGalleryImage));
    }

    /// <summary>Own Chronicles sub-tab · the published list, or the dedicated
    /// editor workspace while a draft is open.</summary>
    private void DrawOwnProfileBlog(Vector4 accent)
    {
        var me = new UserData(_apiController.UID);
        var extras = _spheneProfileManager.GetProfileExtras(me);
        if (_chronicleComposer.Open)
        {
            DrawChronicleEditor(me, accent);
            return;
        }
        var profile = _spheneProfileManager.GetSpheneProfile(me);
        var avatarBytes = profile.ImageData.Value;
        var avatarWrap = avatarBytes.Length == 0 ? null
            : SpheneUI.Media.Texture($"chronicle-author:{me.UID}", () => avatarBytes);
        // Section header + sibling item surfaces - the Widget helper draws
        // its own Surface, and draw-list channel splits cannot nest, so the
        // per-item cards must live BESIDE it, not inside it.
        SyncshellCommunityView.RailHeader("Chronicles", FontAwesomeIcon.FeatherAlt,
            extras?.Blog?.Count, accent, null,
            (FontAwesomeIcon.Plus, "Write a Chronicle", () =>
            {
                _chronicleComposer.OpenNew();
                _blogComposer.ShowPreview = false;
                _chronicleError = null;
            }));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        if (extras == null)
        {
            SpheneUI.Layout.Surface(() =>
            {
                SpheneUI.Fx.SkeletonLine();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Fx.SkeletonLine(0.6f);
            }, id: "own-chronicles-loading");
        }
        else if (extras.Blog is not { Count: > 0 } blog)
        {
            if (_openChronicle is { OwnerUid: var missingOwner }
                && string.Equals(missingOwner, me.UID, StringComparison.Ordinal))
            {
                // A deep-link arrived but the post isn't in the list - deleted
                // or hidden since the feed row was emitted.
                _openChronicle = null;
                _chronicleNotice = ChronicleMissingNotice;
            }
            if (_chronicleNotice != null)
            {
                SpheneUI.Text.ColoredWrapped(_chronicleNotice, SpheneUI.Palette.Warning);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
            SpheneUI.Layout.Surface(() =>
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.BookOpen, "No Chronicles yet",
                    "Nothing published yet - your first Chronicle could be a venue announcement, a story hook or just a hello.");
            }, id: "own-chronicles-empty");
        }
        else
        {
            var openPost = _openChronicle is { OwnerUid: var openOwner, Id: var openId }
                && string.Equals(openOwner, me.UID, StringComparison.Ordinal)
                ? blog.FirstOrDefault(b => b.Id == openId) : null;
            if (openPost != null)
            {
                // Detail reader: just this entry, discussion below - the
                // excerpt toggle bounds the body, comments always show.
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "Back"))
                {
                    _openChronicle = null;
                }
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var captured = openPost;
                var ops = CommunityComposers.BuildBlogCommentOps(me.UID, captured.Id,
                    () =>
                    {
                        _spheneProfileManager.InvalidateProfileExtras(me);
                        return Task.CompletedTask;
                    },
                    uid => CommunityNameFor(uid, null, null), CommentProfile);
                SpheneUI.Layout.Surface(() =>
                {
                    Sphene.UI.Chronicles.ChronicleReaderView.Draw(captured, me.UID,
                        OwnHeroName(), avatarWrap, accent,
                        _blogComments, ops, excerptLines: 8,
                        isExpanded: id => _expandedChronicles.Contains(id),
                        toggleExpanded: id => ToggleChronicleExpanded(id),
                        actions: () =>
                        {
                            using (ImRaii.PushId($"own-blog-post{captured.Id}"))
                            {
                                if (SpheneUI.Icons.Button(FontAwesomeIcon.Pen))
                                {
                                    _chronicleComposer.OpenEdit(captured);
                                    _blogComposer.ShowPreview = false;
                                    _chronicleError = null;
                                }
                                SpheneUI.AttachTooltip("Edit Chronicle");
                                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                                if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                                {
                                    _chronicleDeleteConfirm.Open(() => _ = DeleteOwnBlogPost(captured.Id));
                                }
                                SpheneUI.AttachTooltip("Delete Chronicle");
                            }
                        });
                }, id: $"own-chronicle-{captured.Id}", border: true,
                    accentColor: accent);
            }
            else
            {
                if (_openChronicle is { OwnerUid: var staleOwner }
                    && string.Equals(staleOwner, me.UID, StringComparison.Ordinal))
                {
                    _openChronicle = null;
                    _chronicleNotice = ChronicleMissingNotice;
                }
                if (_chronicleNotice != null)
                {
                    SpheneUI.Text.ColoredWrapped(_chronicleNotice, SpheneUI.Palette.Warning);
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
                for (var i = 0; i < blog.Count; i++)
                {
                    if (i > 0)
                    {
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    }
                    var captured = blog[i];
                    // Compact activity-feed row per entry - click opens the
                    // dedicated reader with the full discussion below.
                    var item = new SyncshellCommunityFeedItemDto(
                        string.Empty, null, null, SyncshellFeedItemKind.Blog,
                        captured.Id,
                        string.IsNullOrWhiteSpace(captured.Title)
                            ? "Untitled Chronicle" : captured.Title,
                        null, captured.UpdatedAt,
                        ActorDisplayName: OwnHeroName(),
                        ThumbnailMediaHash: captured.CoverMediaHash);
                    if (FeedItemRow.Draw($"own-chron-{captured.Id}", item,
                            "chron:", accent, ChronicleRowMeta(captured)))
                    {
                        _openChronicle = (me.UID, captured.Id);
                    }
                }
            }
        }
        _chronicleDeleteConfirm.Draw("own-chronicle-delete", "Delete Chronicle?",
            "This permanently removes the Chronicle, its comments and reactions.",
            confirmLabel: "Delete", danger: true);
    }

    /// <summary>Friend Gallery sub-tab · readonly album filter, grid feeding the shared lightbox.</summary>
    private void DrawFriendProfileGallery(Pair pair, Vector4 accent)
    {
        var extras = _spheneProfileManager.GetProfileExtras(pair.UserData);
        SyncshellCommunityView.Widget("friend-gallery", "Photo wall",
            FontAwesomeIcon.Images, extras?.Gallery?.Count, accent, () =>
        {
            if (extras == null)
            {
                SpheneUI.Fx.SkeletonLine(1f, 88f * SpheneUI.Scale);
                return;
            }
            DrawAlbumChipRow(extras.Albums, _friendGalleryAlbumFilter,
                f => _friendGalleryAlbumFilter = f, "fpga");
            var gallery = extras.Gallery;
            if (gallery is not { Count: > 0 })
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Images, "No photos",
                    "This wall is still empty.");
                return;
            }
            var filtered = _friendGalleryAlbumFilter is { } friendFilter
                ? gallery.Where(g => g.AlbumId == friendFilter).ToList()
                : gallery;
            if (filtered.Count == 0)
            {
                SpheneUI.Text.MutedWrapped("Nothing in this album yet.");
                return;
            }
            ToViewerImages(filtered, _profileGalleryScratch, $"friend:{pair.UserData.UID}");
            SyncshellCommunityView.DrawGalleryGrid(_profileGalleryScratch, "hubfriend:", pair.UserData.UID,
                onClick: i => OpenProfileViewer(_profileGalleryScratch, i, $"friend:{pair.UserData.UID}"));
        });
    }

    /// <summary>Friend Chronicles sub-tab · reader cards through the shared
    /// <see cref="Sphene.UI.Chronicles.ChronicleReaderView"/> - same surface
    /// the own list and the editor's Live Preview use.</summary>
    private void DrawFriendProfileBlog(Pair pair, Vector4 accent)
    {
        var extras = _spheneProfileManager.GetProfileExtras(pair.UserData);
        var fProfile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        var avatarBytes = fProfile.ImageData.Value;
        var avatarWrap = avatarBytes.Length == 0 ? null
            : SpheneUI.Media.Texture($"chronicle-author:{pair.UserData.UID}", () => avatarBytes);
        // Header + sibling cards - see DrawOwnProfileBlog: channel splits
        // cannot nest, so item surfaces must not sit inside a Widget.
        SyncshellCommunityView.RailHeader("Chronicles", FontAwesomeIcon.FeatherAlt,
            extras?.Blog?.Count, accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        if (extras == null)
        {
            SpheneUI.Layout.Surface(() =>
            {
                SpheneUI.Fx.SkeletonLine();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Fx.SkeletonLine(0.6f);
            }, id: "friend-chronicles-loading");
        }
        else if (extras.Blog is not { Count: > 0 } blog)
        {
            if (_openChronicle is { OwnerUid: var missingOwner }
                && string.Equals(missingOwner, pair.UserData.UID, StringComparison.Ordinal))
            {
                _openChronicle = null;
                _chronicleNotice = ChronicleMissingNotice;
            }
            if (_chronicleNotice != null)
            {
                SpheneUI.Text.ColoredWrapped(_chronicleNotice, SpheneUI.Palette.Warning);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
            SpheneUI.Layout.Surface(() =>
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.BookOpen, "No Chronicles",
                    "Chronicles shared on this profile will land here.");
            }, id: "friend-chronicles-empty");
        }
        else
        {
            var openPost = _openChronicle is { OwnerUid: var openOwner, Id: var openId }
                && string.Equals(openOwner, pair.UserData.UID, StringComparison.Ordinal)
                ? blog.FirstOrDefault(b => b.Id == openId) : null;
            if (openPost != null)
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "Back"))
                {
                    _openChronicle = null;
                }
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var post = openPost;
                var ops = CommunityComposers.BuildBlogCommentOps(pair.UserData.UID, post.Id,
                    () =>
                    {
                        _spheneProfileManager.InvalidateProfileExtras(pair.UserData);
                        return Task.CompletedTask;
                    },
                    uid => CommunityNameFor(uid, null, null), CommentProfile);
                SpheneUI.Layout.Surface(() =>
                {
                    Sphene.UI.Chronicles.ChronicleReaderView.Draw(post,
                        pair.UserData.UID, SyncshellCommunityView.HumanName(pair.UserData)
                            ?? "Unknown user", avatarWrap, accent,
                        _blogComments, ops, excerptLines: 8,
                        isExpanded: id => _expandedChronicles.Contains(id),
                        toggleExpanded: id => ToggleChronicleExpanded(id));
                }, id: $"friend-chronicle-{post.Id}", border: true,
                    accentColor: accent);
            }
            else
            {
                if (_openChronicle is { OwnerUid: var staleOwner }
                    && string.Equals(staleOwner, pair.UserData.UID, StringComparison.Ordinal))
                {
                    _openChronicle = null;
                    _chronicleNotice = ChronicleMissingNotice;
                }
                if (_chronicleNotice != null)
                {
                    SpheneUI.Text.ColoredWrapped(_chronicleNotice, SpheneUI.Palette.Warning);
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
                for (var i = 0; i < blog.Count; i++)
                {
                    if (i > 0)
                    {
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    }
                    var post = blog[i];
                    var item = new SyncshellCommunityFeedItemDto(
                        string.Empty, null, null, SyncshellFeedItemKind.Blog,
                        post.Id,
                        string.IsNullOrWhiteSpace(post.Title)
                            ? "Untitled Chronicle" : post.Title,
                        null, post.UpdatedAt,
                        ActorDisplayName: SyncshellCommunityView.HumanName(pair.UserData)
                            ?? "Unknown user",
                        ThumbnailMediaHash: post.CoverMediaHash);
                    if (FeedItemRow.Draw($"friend-chron-{post.Id}", item,
                            "chron:", accent, ChronicleRowMeta(post)))
                    {
                        _openChronicle = (pair.UserData.UID, post.Id);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _apiController.UserMarkBlogPostViewed(post.Id)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex,
                                    "Failed to mark chronicle {Id} viewed", post.Id);
                            }
                        });
                    }
                }
            }
        }
    }

    /// <summary>Opens the photo studio's file browser · the rendered result is
    /// uploaded as a profile gallery photo.</summary>
    private void PickOwnGalleryImage()
    {
        Mediator.Publish(new OpenPhotoStudioMessage(null, PhotoStudioTarget.ProfileGalleryPhoto,
            prepared => _ = UploadOwnGalleryImage(prepared)));
    }

    private async Task UploadOwnGalleryImage(ImageUploadProcessor.Prepared prepared)
    {
        try
        {
            _galleryUploadPhase = "Uploading photo…";
            var me = new UserData(_apiController.UID);
            var result = await _apiController.UserUpsertProfileGalleryImage(
                new UserProfileGalleryImageUpdateDto(me, 0, prepared.Base64,
                    prepared.FileName, prepared.ContentType, prepared.Bytes.Length, null, 0,
                    _ownGalleryAlbumFilter))
                .ConfigureAwait(false);
            if (result != null)
            {
                _spheneProfileManager.UpdateProfileExtras(me, e => e with
                {
                    Gallery = e.Gallery == null ? [result] : [result, .. e.Gallery]
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profile gallery upload failed");
        }
        finally
        {
            _galleryUploadPhase = null;
        }
    }

    /// <summary>"All" + album filter chips · own galleries get the manage affordance.</summary>
    private static void DrawAlbumChipRow(List<UserProfileGalleryAlbumDto>? albums, long? filter,
        Action<long?> setFilter, string idPrefix)
    {
        // Stale filters · album deleted or a different owner's extras · reset to All.
        if (filter is { } stale && albums?.Any(a => a.Id == stale) != true)
        {
            setFilter(null);
            filter = null;
        }
        var gap = SpheneUI.Spacing.X8.Scaled;
        var allSel = filter == null;
        if (SpheneUI.Controls.ToggleChip($"All##{idPrefix}-all", ref allSel))
        {
            setFilter(null);
        }
        if (albums != null)
        {
            foreach (var album in albums)
            {
                ImGui.SameLine(0f, gap);
                var sel = filter == album.Id;
                if (SpheneUI.Controls.ToggleChip(
                        $"{album.Name} ({album.ImageCount})##{idPrefix}-{album.Id}", ref sel))
                {
                    setFilter(sel ? album.Id : null);
                }
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    /// <summary>Anchored album-manage flyout · create, rename, delete.</summary>
    private void DrawOwnAlbumsFlyout(UserProfileExtrasDto? extras)
    {
        if (!SpheneUI.Popups.BeginFlyout("own-gallery-albums"))
        {
            return;
        }
        var scale = SpheneUI.Scale;
        var gap = SpheneUI.Spacing.X8.Scaled;
        SpheneUI.Text.SectionHeader("Photo albums");
        SpheneUI.Controls.InputTextWithHint("##own-album-new", "New album name…",
            ref _ownAlbumName, 80, preferredWidth: 220f * scale);
        ImGui.SameLine(0f, gap);
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_ownAlbumName)))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus)
                && !string.IsNullOrWhiteSpace(_ownAlbumName))
            {
                _ = CreateOwnAlbum(_ownAlbumName.Trim());
            }
            SpheneUI.AttachTooltip("Create album");
        }
        if (extras?.Albums is { Count: > 0 } albums)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            foreach (var album in albums)
            {
                using (ImRaii.PushId($"own-album{album.Id}"))
                {
                    if (_ownAlbumEditId == album.Id)
                    {
                        SpheneUI.Controls.InputTextWithHint("##own-album-ren", "Album name…",
                            ref _ownAlbumEditName, 80, preferredWidth: 180f * scale);
                        ImGui.SameLine(0f, gap);
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
                        {
                            _ = RenameOwnAlbum(album);
                        }
                        SpheneUI.AttachTooltip("Save name");
                    }
                    else
                    {
                        SpheneUI.Text.Colored($"{album.Name} · {album.ImageCount} photos",
                            SpheneUI.Colors.TextPrimary);
                        ImGui.SameLine(0f, gap);
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Pen))
                        {
                            _ownAlbumEditId = album.Id;
                            _ownAlbumEditName = album.Name;
                        }
                        SpheneUI.AttachTooltip("Rename album");
                        ImGui.SameLine(0f, gap);
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                        {
                            _ = DeleteOwnAlbum(album.Id);
                        }
                        SpheneUI.AttachTooltip("Delete album · photos move back to All");
                    }
                }
            }
        }
        SpheneUI.Popups.EndFlyout();
    }

    private async Task CreateOwnAlbum(string name)
    {
        var me = new UserData(_apiController.UID);
        var result = await _apiController.UserUpsertProfileGalleryAlbum(
            new UserProfileGalleryAlbumUpdateDto(me, 0, name, 0)).ConfigureAwait(false);
        if (result != null)
        {
            _ownAlbumName = string.Empty;
            _spheneProfileManager.UpdateProfileExtras(me, e => e with
            {
                Albums = e.Albums == null ? [result] : [.. e.Albums, result]
            });
        }
    }

    private async Task RenameOwnAlbum(UserProfileGalleryAlbumDto album)
    {
        var name = _ownAlbumEditName.Trim();
        if (name.Length == 0)
        {
            return;
        }
        var me = new UserData(_apiController.UID);
        var result = await _apiController.UserUpsertProfileGalleryAlbum(
            new UserProfileGalleryAlbumUpdateDto(me, album.Id, name, album.SortOrder))
            .ConfigureAwait(false);
        if (result != null)
        {
            _ownAlbumEditId = 0;
            _spheneProfileManager.UpdateProfileExtras(me, e => e with
            {
                Albums = e.Albums?.Select(a => a.Id == result.Id ? result : a).ToList()
            });
        }
    }

    private async Task DeleteOwnAlbum(long albumId)
    {
        var me = new UserData(_apiController.UID);
        if (await _apiController.UserDeleteProfileGalleryAlbum(
                new UserProfileItemDeleteDto(me, albumId)).ConfigureAwait(false))
        {
            if (_ownGalleryAlbumFilter == albumId)
            {
                _ownGalleryAlbumFilter = null;
            }
            _spheneProfileManager.UpdateProfileExtras(me, e => e with
            {
                Albums = e.Albums?.Where(a => a.Id != albumId).ToList(),
                Gallery = e.Gallery?.Select(g => g.AlbumId == albumId
                    ? g with { AlbumId = null } : g).ToList()
            });
        }
    }

    /// <summary>Toggles a profile-photo like · optimistic in the viewer, patched to cache.</summary>
    private async Task ToggleProfileImageLike(int index)
    {
        if (index < 0 || index >= _profileViewerImages.Count)
        {
            return;
        }
        var img = _profileViewerImages[index];
        _profileViewerImages[index] = img with
        {
            LikedByMe = !img.LikedByMe,
            LikeCount = Math.Max(0, img.LikeCount + (img.LikedByMe ? -1 : 1))
        };
        try
        {
            var result = await _apiController.UserToggleProfileGalleryImageLike(
                new UserProfileGalleryImageLikeDto(new UserData(_apiController.UID), img.Id))
                .ConfigureAwait(false);
            if (result == null)
            {
                return;
            }
            if (index < _profileViewerImages.Count && _profileViewerImages[index].Id == result.ImageId)
            {
                _profileViewerImages[index] = _profileViewerImages[index] with
                {
                    LikedByMe = result.LikedByMe, LikeCount = result.LikeCount
                };
            }
            var ownerUid = OwnerUidFromViewerKey();
            if (ownerUid != null)
            {
                _spheneProfileManager.UpdateProfileExtras(new UserData(ownerUid), e => e with
                {
                    Gallery = e.Gallery?.Select(g => g.Id == result.ImageId
                        ? g with { LikedByMe = result.LikedByMe, LikeCount = result.LikeCount }
                        : g).ToList()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Profile photo like toggle failed");
        }
    }

    /// <summary>The profile owner's uid behind "own:{uid}" / "friend:{uid}" viewer keys.</summary>
    private string? OwnerUidFromViewerKey()
    {
        var key = _profileViewerKey;
        var colon = key?.IndexOf(':') ?? -1;
        return colon >= 0 ? key![(colon + 1)..] : null;
    }

    /// <summary>Persists the Chronicle draft through the shared blog upsert -
    /// the server keeps ownership + visibility enforcement; the returned DTO
    /// reseeds the extras cache and the composer baseline.</summary>
    private async Task SaveChronicle()
    {
        if (_chronicleSaving)
        {
            return;
        }
        _chronicleSaving = true;
        _chronicleError = null;
        try
        {
            var me = new UserData(_apiController.UID);
            var c = _chronicleComposer;
            var result = await _apiController.UserUpsertProfileBlogPost(
                new UserProfileBlogPostUpdateDto(me, c.EditId, c.Title.Trim(),
                    string.IsNullOrWhiteSpace(c.Body) ? null : c.Body, c.Pinned,
                    StatusIntent: c.Draft ? (int)ContentPublishStatus.Draft
                        : (int)ContentPublishStatus.Published,
                    VisibilityScope: c.Scope,
                    CoverMediaHash: c.CoverMediaHash))
                .ConfigureAwait(false);
            if (result != null)
            {
                _spheneProfileManager.UpdateProfileExtras(me, e =>
                {
                    var blog = e.Blog ?? [];
                    var idx = blog.FindIndex(b => b.Id == result.Id);
                    var updated = idx >= 0
                        ? blog.Select((b, i) => i == idx ? result : b).ToList()
                        : [result, .. blog];
                    return e with { Blog = updated };
                });
                c.CommitSaved(result);
            }
            else
            {
                _chronicleError = "The server rejected this Chronicle - check the title and try again.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chronicle save failed");
            _chronicleError = "Saving failed - the server could not be reached.";
        }
        finally
        {
            _chronicleSaving = false;
        }
    }

    private void ToggleChronicleExpanded(long id)
    {
        if (!_expandedChronicles.Remove(id))
        {
            _expandedChronicles.Add(id);
        }
    }

    /// <summary>"N views · M comments" meta segment for the compact
    /// Chronicle list rows - singular/plural kept explicit.</summary>
    private static string ChronicleRowMeta(UserProfileBlogPostDto post)
        => $"{post.ViewCount} {(post.ViewCount == 1 ? "view" : "views")} · "
            + $"{post.CommentCount} {(post.CommentCount == 1 ? "comment" : "comments")}";

    private async Task DeleteOwnBlogPost(long id)
    {
        var me = new UserData(_apiController.UID);
        if (await _apiController.UserDeleteProfileBlogPost(
                new UserProfileItemDeleteDto(me, id)).ConfigureAwait(false))
        {
            _spheneProfileManager.UpdateProfileExtras(me, e => e with
            {
                Blog = e.Blog?.Where(b => b.Id != id).ToList()
            });
        }
    }

    /// <summary>Entity-reference chip navigation - maps the server's
    /// <c>NavKey</c> onto <see cref="HubLocation"/>; unpaired users land on the
    /// friends list instead of a specific profile.</summary>
    private void NavigateEntityRef(string navKey)
    {
        var parts = navKey.Split(':');
        switch (parts[0])
        {
            case "user" when parts.Length > 1:
            case "blog" when parts.Length > 2:
            {
                // Chronicle refs land on the author's Chronicles tab; plain
                // user refs keep the default profile tab.
                var isBlog = string.Equals(parts[0], "blog", StringComparison.Ordinal);
                if (string.Equals(parts[1], _apiController.UID, StringComparison.Ordinal))
                {
                    NavigateTo(new HubLocation(SpheneHubPage.Profile, null, null,
                        SubPage: isBlog ? 2 : null));
                    break;
                }
                var pair = _pairManager.DirectPairs
                    .FirstOrDefault(p => string.Equals(p.UserData.UID, parts[1], StringComparison.Ordinal));
                NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair,
                    SubPage: pair != null && isBlog ? 2 : null));
                break;
            }
            case "community" when parts.Length > 1:
                // Deep-linked content rides on the location itself so
                // back/forward restores the drilled-in surface.
                SyncshellCommunitySection? refSection = null;
                long? refItemId = null;
                if (parts.Length > 3 && long.TryParse(parts[3], out var refId))
                {
                    if (string.Equals(parts[2], "event", StringComparison.Ordinal))
                    {
                        refSection = SyncshellCommunitySection.Events;
                        refItemId = refId;
                    }
                    else if (string.Equals(parts[2], "post", StringComparison.Ordinal))
                    {
                        refSection = SyncshellCommunitySection.News;
                        refItemId = refId;
                    }
                }
                NavigateTo(new HubLocation(SpheneHubPage.Syncshells, parts[1], null,
                    CommunitySection: refSection, CommunityItemId: refItemId));
                break;
            case "announcement" when parts.Length > 1 && long.TryParse(parts[1], out var annId):
                NavigateTo(new HubLocation(SpheneHubPage.News, null, null, AnnouncementId: annId));
                break;
        }
    }

    /// <summary>The dedicated Chronicle authoring workspace - ~2/3 editor
    /// column (title, cover, markdown composer) against a ~1/3 settings rail
    /// (visibility, pinned, draft, publishing info), with the action footer
    /// pinned at the bottom. The Live Preview opens as a separate window so
    /// the draft column keeps the full workspace width.</summary>
    private void DrawChronicleEditor(UserData me, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var composer = _chronicleComposer;
        var editing = composer.IsEditing;

        // Breadcrumb - back returns to the list; the dirty check guards the
        // destructive Discard, not navigation (the draft survives switching).
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft,
                editing ? "Chronicles / Edit Chronicle" : "Chronicles / Write a Chronicle"))
        {
            composer.Close();
            _chronicleError = null;
            return;
        }

        var availW = ImGui.GetContentRegionAvail().X;
        var gutter = SpheneUI.Spacing.X16.Scaled;
        var settingsW = Math.Clamp(availW * 0.30f, 200f * scale, 320f * scale);
        var narrow = availW < 560f * scale;
        var mainW = availW - settingsW - gutter;

        void EditorBody(bool stacked)
        {
            var chronicleTitle = composer.Title;
            if (SpheneUI.Controls.InputTextWithHint("##chronicle-title", "Title (optional)…",
                    ref chronicleTitle, 200, preferredWidth: -1f))
            {
                composer.Title = chronicleTitle;
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            DrawChronicleCoverRow(composer);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            _blogComposer.ShowPreview = false;
            var chronicleBody = composer.Body;
            // The text area fills the remaining editor-column height (the
            // child already reserved the footer) - minus the toolbar row that
            // Draw() emits above the input. Stacked mode keeps a fixed tall
            // field inside the page's own scroll instead.
            var bodyH = stacked
                ? 280f * scale
                : MathF.Max(120f * scale, ImGui.GetContentRegionAvail().Y
                    - ImGui.GetFrameHeightWithSpacing() * 2f
                    - SpheneUI.Spacing.X4.Scaled);
            if (_blogComposer.Draw("##chronicle-body",
                    ref chronicleBody, 8000, bodyH) is { Length: > 0 } err)
            {
                _chronicleError = err;
            }
            composer.Body = chronicleBody;
        }

        if (narrow)
        {
            // Stacked fallback - the settings rail moves below the fields
            // inside the host's own scroll; no split children needed.
            EditorBody(stacked: true);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawChronicleSettings(composer);
        }
        else
        {
            using (var main = ImRaii.Child("chronicle-editor-main",
                       new Vector2(mainW, -ImGui.GetFrameHeightWithSpacing()
                           - SpheneUI.Spacing.X8.Scaled), border: false))
            {
                if (main.Success)
                {
                    EditorBody(stacked: false);
                }
            }
            ImGui.SameLine(0f, gutter);
            using (var rail = ImRaii.Child("chronicle-editor-settings",
                       new Vector2(settingsW,
                           -ImGui.GetFrameHeightWithSpacing()
                               - SpheneUI.Spacing.X8.Scaled), border: false))
            {
                if (rail.Success)
                {
                    DrawChronicleSettings(composer);
                }
            }
        }

        // Footer - destructive Discard left, Live Preview + Save/Publish
        // right. Cursor setters take WINDOW-LOCAL x - screen coordinates
        // would push the actions off the visible workspace.
        if (_chronicleError is { Length: > 0 } error)
        {
            SpheneUI.Text.Colored(error, SpheneUI.Palette.Warning);
            ImGui.SameLine(0f, SpheneUI.Spacing.X16.Scaled);
        }
        var saveLabel = _chronicleSaving ? "Saving…"
            : editing ? "Save changes"
            : composer.Draft ? "Save draft" : "Publish";
        // Measure with the REAL button width formula (SegAutoWidth -
        // includes the icon/chevron zone both segmented buttons reserve),
        // else the right dock lands short and Save overflows the edge.
        var frameH = ImGui.GetFrameHeight();
        var previewW = SpheneUI.Controls.SegAutoWidth("Live Preview",
            FontAwesomeIcon.Eye, frameH);
        var saveW = SpheneUI.Controls.SegAutoWidth(saveLabel, null, frameH);
        var actionsW = previewW + saveW + SpheneUI.Spacing.X8.Scaled;
        if (SpheneUI.Controls.Button("Discard", danger: true))
        {
            composer.Close();
            _chronicleError = null;
        }
        // Local x - cursor+avail is the container's inner right edge; the
        // inset keeps the Save button inside the content padding.
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X
            - SpheneUI.Spacing.X8.Scaled;
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(), right - actionsW));
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Eye, "Live Preview"))
        {
            Mediator.Publish(new OpenChroniclePreviewMessage(composer, me.UID,
                OwnHeroName(), accent));
        }
        SpheneUI.AttachTooltip("Opens the draft as a visitor would see it - nothing is saved");
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        // Title is optional - a post needs a title OR meaningful body text.
        var valid = !string.IsNullOrWhiteSpace(composer.Title)
            || !string.IsNullOrWhiteSpace(composer.Body);
        using (ImRaii.Disabled(!valid || _chronicleSaving))
        {
            if (SpheneUI.Controls.PrimaryButton(saveLabel))
            {
                _ = SaveChronicle();
            }
        }
    }

    /// <summary>Settings rail - server-enforced visibility, pinned + draft
    /// toggles and the honest publishing metadata for an existing post.</summary>
    private static void DrawChronicleSettings(Sphene.UI.Chronicles.ChronicleComposer c)
    {
        SpheneUI.Text.SectionHeader("Settings");
        SpheneUI.Text.Muted("Who can see this");
        SpheneUI.Controls.Combo("##chronicle-scope",
            new[] { BlogVisibilityScope.Public, BlogVisibilityScope.Pairs,
                BlogVisibilityScope.Private },
            s => s switch
            {
                BlogVisibilityScope.Pairs => "Friends",
                BlogVisibilityScope.Private => "Only me",
                _ => "Everyone",
            },
            s => c.Scope = (int)s,
            (BlogVisibilityScope)c.Scope);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        var pinned = c.Pinned;
        if (SpheneUI.Controls.Toggle("Pinned to the top", ref pinned)) c.Pinned = pinned;
        var draft = c.Draft;
        if (SpheneUI.Controls.Toggle("Draft (hidden until published)", ref draft)) c.Draft = draft;
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Muted("Publishing");
        if (c.IsEditing)
        {
            SpheneUI.Text.Muted($"Status · {(c.Draft ? "Draft" : "Published")}");
            if (c.CreatedAt is { } created)
            {
                SpheneUI.Text.Muted($"Written · {SpheneUI.Text.FormatDate(created.ToLocalTime())}");
            }
        }
        else
        {
            SpheneUI.Text.Muted("New Chronicle - not saved yet.");
        }
    }

    /// <summary>Cover row - the current cover thumb plus pick/clear actions.
    /// The picker reuses the photo studio's banner preset (wide crop) and the
    /// shared community-media upload, so covers are content-addressed like
    /// every other image.</summary>
    private void DrawChronicleCoverRow(Sphene.UI.Chronicles.ChronicleComposer c)
    {
        if (c.CoverMediaHash is { } hash
            && MarkdownComposer.MediaClient?.TryGetBytes(hash) is { } coverBytes)
        {
            var wrap = SpheneUI.Media.Texture($"chronicle-cover-edit:{hash}",
                () => coverBytes);
            if (wrap != null)
            {
                var dl = ImGui.GetWindowDrawList();
                var w = Math.Min(ImGui.GetContentRegionAvail().X, 220f * SpheneUI.Scale);
                var h = w * 0.38f;
                ImGui.Dummy(new Vector2(w, h));
                SpheneUI.Media.ImageInRect(dl, wrap, ImGui.GetItemRectMin(),
                    ImGui.GetItemRectMax(), ImageFit.Cover,
                    rounding: 6f * SpheneUI.Scale);
            }
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        }
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Image,
                c.CoverMediaHash == null ? "Add cover" : "Replace cover"))
        {
            Mediator.Publish(new OpenPhotoStudioMessage(null,
                PhotoStudioTarget.ProfileBanner,
                prepared => _ = UploadChronicleCover(prepared)));
        }
        if (c.CoverMediaHash != null)
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Remove cover"))
            {
                c.CoverMediaHash = null;
            }
        }
    }

    /// <summary>Uploads the prepared cover through the shared media pipeline -
    /// the hash lands on the draft and travels with the save DTO.</summary>
    private async Task UploadChronicleCover(ImageUploadProcessor.Prepared prepared)
    {
        var media = MarkdownComposer.MediaClient;
        if (media == null)
        {
            _chronicleError = "Media channel unavailable - the cover was not attached.";
            return;
        }
        var hash = await media.UploadImageAsync(prepared.Bytes, prepared.ContentType,
            CancellationToken.None).ConfigureAwait(false);
        if (hash == null)
        {
            _chronicleError = "Cover upload to the media store failed.";
            return;
        }
        _chronicleComposer.CoverMediaHash = hash;
    }

    private bool IsStillPaired(Pair pair)
    {
        var uid = pair.UserData.UID;
        foreach (var p in _pairManager.DirectPairs)
        {
            if (string.Equals(p.UserData.UID, uid, StringComparison.Ordinal)) return true;
        }
        foreach (var p in _pairManager.PairsWithGroups.Keys)
        {
            if (string.Equals(p.UserData.UID, uid, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private GroupFullInfoDto? GetSelectedGroup()
    {
        if (_selectedGroupGid == null)
        {
            return null;
        }
        return _pairManager.GroupPairs.Keys.FirstOrDefault(k =>
            k.Group != null && string.Equals(k.Group.GID, _selectedGroupGid, StringComparison.Ordinal));
    }

    private bool CanAdminister(GroupFullInfoDto? group)
    {
        if (group == null || group.Group == null)
        {
            return false;
        }
        return string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal)
            || group.GroupUserInfo.IsModerator();
    }

    private SyncshellAdminUI GetOrCreateAdminPanel(GroupFullInfoDto group)
    {
        if (!_embeddedAdminPanels.TryGetValue(group.Group.GID, out var panel))
        {
            panel = _uiFactory.CreateSyncshellAdminUi(group);
            _embeddedAdminPanels[group.Group.GID] = panel;
        }
        return panel;
    }

    private void DisposeEmbeddedAdminPanels()
    {
        foreach (var panel in _embeddedAdminPanels.Values)
        {
            panel.Dispose();
        }
        _embeddedAdminPanels.Clear();
    }

    internal enum SyncshellDetailTab
    {
        Community = 0,
        Members = 1,
        Settings = 2,
        AreaBinding = 3,
        Staff = 4,
        EventPlanner = 5,
    }

    /// <summary>
    /// Whether the viewer holds any community staff permission · owner, mod or
    /// a staff role with at least one manage flag. Grants the manage affordances
    /// entry even when the member is not a shell moderator.
    /// </summary>
    private bool CommunityStaffCanManage(GroupFullInfoDto group)
    {
        if (group.Group == null || !string.Equals(_communityCacheGid, group.Group.GID, StringComparison.Ordinal))
        {
            return false;
        }
        return SyncshellCommunityView.MemberPermissionBits(CommunityCache, _apiController.UID)
            != SyncshellStaffPermission.None;
    }

    /// <summary>One staff-permission check against the cached community roles.</summary>
    private bool CommunityStaffPermission(GroupFullInfoDto group, SyncshellStaffPermission flag)
    {
        if (CanAdminister(group))
        {
            return true;
        }
        return (SyncshellCommunityView.MemberPermissionBits(CommunityCache, _apiController.UID)
            & flag) != SyncshellStaffPermission.None;
    }

    /// <summary>
    /// The staff subpage's member list · everyone except the owner, mods
    /// flagged, sorted like the old studio's assignment rows.
    /// </summary>
    /// <summary>Community name resolution: pair preferred name, the
    /// server-provided identity rows, then the profile cache. Never a uid.</summary>
    private string? CommunityNameFor(string uid, GroupFullInfoDto? group,
        SyncshellCommunityDto? community)
    {
        var name = SyncshellCommunityView.HumanName(_pairManager.GetPairByUID(uid)?.UserData);
        if (name != null) return name;
        name = group?.MemberIdentities != null
            && group.MemberIdentities.TryGetValue(uid, out var g)
            ? SyncshellCommunityView.HumanName(g) : null;
        if (name != null) return name;
        name = SyncshellCommunityView.HumanName(community?.MemberIdentities?
            .FirstOrDefault(u => string.Equals(u.UID, uid, StringComparison.Ordinal)));
        return name ?? _spheneProfileManager.GetPreferredName(uid);
    }

    /// <summary>Profile lookup for the inline comment avatar.</summary>
    private SpheneProfileData CommentProfile(string uid)
        => _spheneProfileManager.GetSpheneProfile(new UserData(uid));

    private IReadOnlyList<SyncshellCommunityView.CommunityStaffMember> StaffMembers(
        GroupFullInfoDto group)
        => group.GroupPairUserInfos
            .Where(kv => !string.Equals(kv.Key, group.OwnerUID, StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Value.IsModerator())
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new SyncshellCommunityView.CommunityStaffMember(kv.Key,
                CommunityNameFor(kv.Key, group, null) ?? "Unknown user",
                kv.Value.IsModerator()))
            .ToList();

    private void DrawSyncshellsPage()
    {
        var selected = GetSelectedGroup();
        if (_selectedGroupGid != null && selected == null)
        {
            // Group vanished (left/kicked) · drop back to the list without
            // touching the history stacks.
            ApplyLocation(new HubLocation(SpheneHubPage.Syncshells, null, null));
        }

        if (selected != null)
        {
            DrawSyncshellDetail(selected);
            return;
        }

        switch (_syncshellSubPage)
        {
            case SyncshellHubSubPage.Join:
                _joinSyncshellUi.DrawEmbeddedContent();
                return;
            case SyncshellHubSubPage.Create:
                _createSyncshellUi.DrawEmbeddedContent();
                return;
        }
        DrawSyncshellList();
    }

    private void DrawSyncshellList()
    {
        var theme = SpheneTheme.Current;
        SpheneUI.Layout.RegisterPageHelp("syncshells", SyncshellsHelpContent);
        var shells = _pairManager.GroupPairs;
        var shellCount = shells.Count;
        var maxShells = _apiController.ServerInfo.MaxGroupsJoinedByUser;
        var (totalMembers, onlineMembers) = TotalPresence();
        var managedCount = 0;
        foreach (var kv in shells)
        {
            var group = kv.Key;
            if (string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal)
                || group.GroupUserInfo.IsModerator())
            {
                managedCount++;
            }
        }

        var atCap = shellCount >= maxShells;

        // Dense overview strip · one accent-tinted line, not a stat tile grid.
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        SpheneUI.Layout.AccentCard(() =>
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.Users, theme.AccentBlue);
            ImGui.SameLine();
            SpheneUI.Text.Plain(shellCount == 0 ? "No Syncshells yet"
                : maxShells > 0 ? $"{shellCount} of {maxShells} Syncshells"
                : $"{shellCount} Syncshells");
            ImGui.SameLine();
            SpheneUI.Text.Muted(shellCount == 0
                ? "· pick a way in below"
                : $"· {totalMembers} members · {onlineMembers} online" +
                  (managedCount > 0 ? $" · {managedCount} managed by you" : ""));
        }, accentColor: theme.AccentBlue, animatedBackdrop: false, id: "syncshell-overview");

        // Entry points sit side-by-side as tiles in the row language  · 
        // the longer hint texts live on the info glyph's tooltip.
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        var tileGap = ImGui.GetStyle().ItemSpacing.X;
        var tileW = MathF.Floor((ImGui.GetContentRegionAvail().X - tileGap) * 0.5f);
        if (HubTile("join", FontAwesomeIcon.Users, theme.AccentBlue,
                "Join an existing Syncshell", tileW))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null,
                (int)SyncshellHubSubPage.Join));
        }
        SpheneUI.AttachTooltip("Enter the Syncshell ID and password a member gave you · IDs are case sensitive.");
        ImGui.SameLine();
        bool createClicked;
        using (ImRaii.Disabled(atCap))
        {
            createClicked = HubTile("create", FontAwesomeIcon.Plus, SpheneUI.Palette.EtherealPurple,
                "Create your own Syncshell", tileW);
        }
        if (!atCap)
        {
            SpheneUI.AttachTooltip(
                "You become the owner · invites, permissions, the welcome page and member management live in its admin area.");
        }
        if (createClicked)
        {
            NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null,
                (int)SyncshellHubSubPage.Create));
        }
        if (atCap)
        {
            var limitText = $"Syncshell limit reached ({shellCount}/{maxShells})";
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
            ImGui.SameLine();
            SpheneUI.Text.Colored(limitText, SpheneUI.Colors.Danger);
        }

        // Joined area-bound shells are grouped into their own section below  · 
        // "Your Syncshells" stays classic member-driven shells only.
        EnsureAreaBoundShellsLoaded();
        var areaJoinedGids = new HashSet<string>(StringComparer.Ordinal);
        if (_areaBoundShells != null)
        {
            foreach (var areaShell in _areaBoundShells)
            {
                if (areaShell.Group != null)
                {
                    areaJoinedGids.Add(areaShell.Group.GID);
                }
            }
        }

        var classicShells = shells
            .Where(kv => kv.Key.Group != null && !areaJoinedGids.Contains(kv.Key.Group.GID))
            .OrderBy(k => k.Key.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Managed shells lead · ownership must never drown inside the member
        // list, no matter whether the shell is classic or area-bound.
        var managedCards = new List<(GroupFullInfoDto Group, List<Pair> Members, bool AreaBound)>();
        var managedAreaGids = new HashSet<string>(StringComparer.Ordinal);
        var memberShells = new List<(GroupFullInfoDto, List<Pair>, bool)>();
        foreach (var kv in classicShells)
        {
            (IsManaged(kv.Key) ? managedCards : memberShells).Add((kv.Key, kv.Value, false));
        }
        foreach (var kv in shells)
        {
            if (kv.Key.Group != null && areaJoinedGids.Contains(kv.Key.Group.GID)
                && IsManaged(kv.Key))
            {
                managedCards.Add((kv.Key, kv.Value, true));
                managedAreaGids.Add(kv.Key.Group.GID);
            }
        }
        if (managedCards.Count > 0)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
            SpheneUI.Text.SectionHeader("Managed by you");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            DrawShellCardGrid(managedCards);
        }
        if (memberShells.Count > 0)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
            SpheneUI.Text.SectionHeader(memberShells.Count == classicShells.Count
                ? "Your Syncshells" : "Member of");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            DrawShellCardGrid(memberShells);
        }

        DrawAreaBoundSection(areaJoinedGids, managedAreaGids);
        DrawFollowedSection();
        DrawDiscoverSection();
    }

    /// <summary>Bookmarked shells stay reachable without membership - one
    /// row per favorite, opening the same public community page the
    /// directory cards land on.</summary>
    private void DrawFollowedSection()
    {
        EnsureFavoritesLoaded();
        if (_favoriteEntries == null || _favoriteEntries.Count == 0)
        {
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
        SpheneUI.Text.SectionHeader("Followed Communities");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Layout.CardGrid(_favoriteEntries, 150f * SpheneUI.Scale, (entry, _) =>
        {
            var accent = SyncshellCommunityView.ParseAccent(entry.AccentColor)
                ?? SpheneUI.Palette.CrystalBlue;
            var cellSize = new Vector2(ImGui.GetContentRegionAvail().X,
                190f * SpheneUI.Scale * SpheneUI.Density.Factor);
            var cta = entry.IsAreaBound ? "Area shell" : "Open";
            if (SyncshellCard.Cover($"fav-{entry.GroupGID}", cellSize,
                    () => DirectoryCover(entry), cta, SpheneUI.Palette.SpheneGold,
                    entry.GroupAlias ?? entry.GroupGID,
                    $"{entry.MemberCount} members", accent))
            {
                OpenPublicCommunity(entry);
            }
        }, gap: 10f * SpheneUI.Scale, maxCols: 4);
    }

    private void EnsureFavoritesLoaded()
    {
        if (_favoriteEntries != null || _favoritesLoading || !_apiController.IsConnected)
        {
            return;
        }
        _favoritesLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _favoriteEntries = await _apiController.GroupGetFavorites().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load followed communities");
                _favoriteEntries = [];
            }
            finally
            {
                _favoritesLoading = false;
            }
        });
    }

    /// <summary>Public community directory · opt-in listings from other
    /// syncshells. The section stays mounted in every state - loading,
    /// load error (with retry), empty directory, empty search - so a
    /// transient failure never makes Discover disappear.</summary>
    private void DrawDiscoverSection()
    {
        EnsureDirectoryLoaded();

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
        SpheneUI.Text.SectionHeader("Discover Communities");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.PageIntro(
            "Clubs, venues and groups that listed themselves publicly · open a card to see their community page.");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);

        var previewError = _discover.PreviewError;
        if (previewError != null)
        {
            // A card click that failed to fetch the public page - the card
            // stays mounted, so clicking it again is the retry.
            SpheneUI.Text.ColoredWrapped(previewError, SpheneUI.Palette.Warning);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        if (_discover.DirectoryLoading)
        {
            SpheneUI.Text.Muted("Loading public communities…");
            return;
        }
        if (_discover.DirectoryError)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.ExclamationTriangle,
                "Couldn't load the directory",
                "The public community listing could not be reached.",
                "Retry",
                () =>
                {
                    // Clearing the flag re-arms the loader on the next frame -
                    // search and sort ride on fields and stay untouched.
                    _discover.ClearDirectoryError();
                },
                FontAwesomeIcon.SyncAlt, SpheneUI.Palette.CrystalBlue);
            return;
        }
        var entries = _discover.Entries;
        if (entries == null)
        {
            // Never fetched and not fetching - only reachable while offline.
            SpheneUI.Text.Muted("Connect to browse public communities.");
            return;
        }
        if (entries.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Compass,
                "No public communities yet",
                "Syncshells appear here once their owners list them publicly.");
            return;
        }

        var joinedGids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in _pairManager.GroupPairs)
        {
            if (kv.Key.Group != null)
            {
                joinedGids.Add(kv.Key.Group.GID);
            }
        }

        // Search + sort over the server-listed set · joined shells drop out.
        var scale = SpheneUI.Scale;
        var searchW = MathF.Min(190f * scale,
            SpheneUI.Controls.BoundedAvailX() - 150f * scale - SpheneUI.Spacing.X8.Scaled);
        SpheneUI.Controls.InputTextWithHint("##discoversearch", "Search name or tagline",
            ref _discoverSearch, 80, preferredWidth: MathF.Max(100f * scale, searchW));
        string[] sortLabels = ["Newest", "Most members", "A-Z"];
        var sortOffset = ImGui.GetContentRegionAvail().X - 150f * scale;
        if (sortOffset > 0) ImGui.SameLine(sortOffset);
        _ = SpheneUI.Controls.Combo("##discoversort", sortLabels,
            s => s ?? "Newest",
            preferredWidth: 150f * scale,
            onSelected: s => _discoverSort = Array.IndexOf(sortLabels, s),
            initialSelectedItem: sortLabels[Math.Clamp(_discoverSort, 0, sortLabels.Length - 1)]);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var listed = entries
            .Where(e => !joinedGids.Contains(e.GroupGID) && !e.CallerIsMember)
            .Where(e => string.IsNullOrWhiteSpace(_discoverSearch)
                || (e.GroupAlias ?? e.GroupGID).Contains(_discoverSearch, StringComparison.OrdinalIgnoreCase)
                || (e.Tagline ?? string.Empty).Contains(_discoverSearch, StringComparison.OrdinalIgnoreCase))
            .ToList();
        listed = _discoverSort switch
        {
            1 => listed.OrderByDescending(e => e.MemberCount).ToList(),
            2 => listed.OrderBy(e => e.GroupAlias ?? e.GroupGID, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => listed, // directory already sorts newest-first
        };
        if (listed.Count == 0)
        {
            var searchActive = !string.IsNullOrWhiteSpace(_discoverSearch);
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Compass,
                searchActive ? "No communities match" : "No communities to show",
                searchActive
                    ? "Try a different search - or check back for new public listings."
                    : "Every listed community is already joined.");
            return;
        }

        static string CtaLabel(SyncshellJoinPolicy p) => p switch
        {
            SyncshellJoinPolicy.Public => "Join",
            SyncshellJoinPolicy.ApprovalRequired => "Apply",
            SyncshellJoinPolicy.PasswordProtected => "Password",
            SyncshellJoinPolicy.InviteOnly => "Invite only",
            _ => "Preview",
        };
        static Vector4 CtaTint(SyncshellJoinPolicy p) => p switch
        {
            SyncshellJoinPolicy.Public or SyncshellJoinPolicy.ApprovalRequired
                or SyncshellJoinPolicy.PasswordProtected => SpheneUI.Palette.Success,
            _ => SpheneUI.Colors.TextMuted,
        };

        IDalamudTextureWrap? EntryCover(SyncshellDirectoryEntryDto e) => DirectoryCover(e);

        var featured = listed[0];
        var featuredAccent = SyncshellCommunityView.ParseAccent(featured.AccentColor)
            ?? SpheneUI.Palette.CrystalBlue;
        var featuredSize = new Vector2(ImGui.GetContentRegionAvail().X, 190f * scale * SpheneUI.Density.Factor);
        if (SyncshellCard.Cover($"dir-feat-{featured.GroupGID}", featuredSize,
                () => EntryCover(featured), $"Featured · {CtaLabel(featured.EffectiveJoinPolicy)}",
                SpheneUI.Palette.SpheneGold,
                featured.GroupAlias ?? featured.GroupGID,
                $"{featured.MemberCount} members", featuredAccent))
        {
            OpenPublicCommunity(featured);
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        var rest = listed.Skip(1).ToList();
        SpheneUI.Layout.CardGrid(rest, 150f * scale, (entry, _) =>
        {
            var accent = SyncshellCommunityView.ParseAccent(entry.AccentColor) ?? SpheneUI.Palette.CrystalBlue;
            var cellSize = new Vector2(ImGui.GetContentRegionAvail().X, 190f * scale * SpheneUI.Density.Factor);
            if (SyncshellCard.Cover($"dir-{entry.GroupGID}", cellSize,
                    () => EntryCover(entry), CtaLabel(entry.EffectiveJoinPolicy),
                    CtaTint(entry.EffectiveJoinPolicy),
                    entry.GroupAlias ?? entry.GroupGID,
                    $"{entry.MemberCount} members", accent))
            {
                OpenPublicCommunity(entry);
            }
        }, gap: 10f * scale, maxCols: 4);
    }

    private void EnsureDirectoryLoaded()
    {
        // The error latch needs the Retry button to clear it - without the
        // latch a persistent outage would request per frame. Gate + claim +
        // generation capture are one atomic step inside the tracker.
        if (!_discover.TryBeginDirectory(_discover.Entries != null,
                _apiController.IsConnected, out var generation))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var entries = await _apiController.GroupGetCommunityDirectory().ConfigureAwait(false);
                _discover.ApplyDirectoryResult(generation, entries);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load community directory");
                _discover.ApplyDirectoryFailure(generation);
            }
            finally
            {
                // Generation-bound release: a stale request can't evict a
                // newer one that claimed the slot after a reset.
                _discover.EndDirectory(generation);
            }
        });
    }

    /// <summary>Opens the public page behind a directory/favorite card.
    /// Failures surface as an inline retry hint - the card stays mounted,
    /// so clicking it again is the retry path. A queued notification could
    /// be delivered after a profile switch, so no toast fires here.</summary>
    private void OpenPublicCommunity(SyncshellDirectoryEntryDto entry)
    {
        var gid = entry.GroupGID;
        if (!_discover.TryBeginPreview(gid, out var generation))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var community = await _apiController
                    .GroupGetPublicCommunity(new GroupDto(new GroupData(gid)))
                    .ConfigureAwait(false);
                if (community == null)
                {
                    _discover.ApplyPreviewError(generation, PreviewErrorText(entry));
                    return;
                }
                // The open is queued, so the request identity rides along
                // and is re-validated when the preview actually opens - a
                // reset between completion and consumption drops it.
                Mediator.Publish(new OpenSyncshellCommunityMessage(community, null, JoinPopup: false,
                    IsStillValid: () => _discover.IsCurrent(generation)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load public community for {GID}", gid);
                _discover.ApplyPreviewError(generation, PreviewErrorText(entry));
            }
            finally
            {
                _discover.EndPreview(generation);
            }
        });
    }

    private static string PreviewErrorText(SyncshellDirectoryEntryDto entry)
    {
        var name = entry.GroupAlias ?? entry.GroupGID;
        return $"Couldn't open {name} - the community page could not be loaded. Click its card to retry.";
    }

    /// <summary>
    /// Full-width actionable row in the segmented-button language: icon zone on
    /// the left, title, optional right-aligned meta plus a chevron, and an
    /// optional muted description line. Used for landing-page entry points.
    /// </summary>
    private static bool HubRow(string id, FontAwesomeIcon icon, Vector4 accent,
        string title, string? meta, Vector4 metaColor, string? description = null)
    {
        var scale = SpheneUI.Scale;
        var zoneW = 30f * scale;
        var padX = 10f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var headH = zoneW;
        var descH = description != null ? lineH + 4f * scale : 0f;
        var rowH = headH + descH;

        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##hubrow", new Vector2(-1f, rowH));
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();
        SpheneUI.Lint.CheckBounds(id);

        var drawList = ImGui.GetWindowDrawList();
        var radius = SpheneUI.Metrics.ButtonRadius;
        var pillFill = held ? SpheneUI.ColorUtils.WithAlpha(accent, 0.14f)
            : hovered ? SpheneUI.ColorUtils.WithAlpha(accent, 0.10f)
            : SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.045f);
        drawList.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(pillFill), radius);

        var zoneFill = held ? SpheneUI.ColorUtils.WithAlpha(accent, 0.34f)
            : hovered ? SpheneUI.ColorUtils.WithAlpha(accent, 0.24f)
            : SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.08f);
        var zoneMax = new Vector2(min.X + zoneW, max.Y);
        drawList.AddRectFilled(min, zoneMax, SpheneUI.ColorUtils.ToU32(zoneFill),
            radius, ImDrawFlags.RoundCornersLeft);

        var seamInset = 6f * scale;
        drawList.AddLine(new Vector2(zoneMax.X - 0.5f, min.Y + seamInset),
            new Vector2(zoneMax.X - 0.5f, max.Y - seamInset),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.09f)));

        var glyphCol = SpheneUI.ColorUtils.ToU32(
            hovered ? SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, 0.3f)
            : SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, 0.1f));
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = icon.ToIconString();
            var gSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(new Vector2(min.X + (zoneW - gSize.X) * 0.5f,
                min.Y + (rowH - gSize.Y) * 0.5f), glyphCol, glyph);
        }

        var textX = zoneMax.X + padX;
        var titleY = min.Y + (headH - lineH) * 0.5f;
        drawList.AddText(new Vector2(textX, titleY),
            ImGui.GetColorU32(ImGuiCol.Text), title);

        var chev = FontAwesomeIcon.ChevronRight.ToIconString();
        float chevW;
        using (SpheneUI.Icons.PushFont())
        {
            chevW = ImGui.CalcTextSize(chev).X;
        }
        var chevX = max.X - padX - chevW;
        using (SpheneUI.Icons.PushFont())
        {
            drawList.AddText(new Vector2(chevX, titleY),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), chev);
        }

        if (meta != null)
        {
            var mSize = ImGui.CalcTextSize(meta);
            drawList.AddText(new Vector2(chevX - 6f * scale - mSize.X, titleY),
                SpheneUI.ColorUtils.ToU32(metaColor), meta);
        }

        if (description != null)
        {
            ImGui.PushClipRect(new Vector2(textX, min.Y + headH),
                new Vector2(max.X - padX, max.Y), true);
            drawList.AddText(new Vector2(textX, min.Y + headH + 2f * scale),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), description);
            ImGui.PopClipRect();
        }
        return clicked;
    }

    /// <summary>
    /// Half-width entry tile in the HubRow language · icon zone, single title
    /// line and a trailing info glyph; the explanatory text lives on the
    /// caller-attached tooltip so two tiles share one row.
    /// </summary>
    private static bool HubTile(string id, FontAwesomeIcon icon, Vector4 accent,
        string title, float width)
    {
        var scale = SpheneUI.Scale;
        var zoneW = 30f * scale;
        var padX = 10f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var rowH = zoneW + 6f * scale;

        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##hubtile", new Vector2(width, rowH));
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();
        SpheneUI.Lint.CheckBounds(id);

        var drawList = ImGui.GetWindowDrawList();
        var radius = SpheneUI.Metrics.ButtonRadius;
        var pillFill = held ? SpheneUI.ColorUtils.WithAlpha(accent, 0.14f)
            : hovered ? SpheneUI.ColorUtils.WithAlpha(accent, 0.10f)
            : SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.045f);
        drawList.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(pillFill), radius);

        var zoneFill = held ? SpheneUI.ColorUtils.WithAlpha(accent, 0.34f)
            : hovered ? SpheneUI.ColorUtils.WithAlpha(accent, 0.24f)
            : SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.08f);
        var zoneMax = new Vector2(min.X + zoneW, max.Y);
        drawList.AddRectFilled(min, zoneMax, SpheneUI.ColorUtils.ToU32(zoneFill),
            radius, ImDrawFlags.RoundCornersLeft);

        var seamInset = 6f * scale;
        drawList.AddLine(new Vector2(zoneMax.X - 0.5f, min.Y + seamInset),
            new Vector2(zoneMax.X - 0.5f, max.Y - seamInset),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.09f)));

        var glyphCol = SpheneUI.ColorUtils.ToU32(
            hovered ? SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, 0.3f)
            : SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, 0.1f));
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = icon.ToIconString();
            var gSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(new Vector2(min.X + (zoneW - gSize.X) * 0.5f,
                min.Y + (rowH - gSize.Y) * 0.5f), glyphCol, glyph);
        }

        var textX = zoneMax.X + padX;
        var titleY = min.Y + (rowH - lineH) * 0.5f;

        var infoGlyph = FontAwesomeIcon.InfoCircle.ToIconString();
        float infoW;
        using (SpheneUI.Icons.PushFont())
        {
            infoW = ImGui.CalcTextSize(infoGlyph).X;
        }
        var infoX = max.X - padX - infoW;
        using (SpheneUI.Icons.PushFont())
        {
            drawList.AddText(new Vector2(infoX, titleY),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), infoGlyph);
        }

        ImGui.PushClipRect(new Vector2(textX, min.Y),
            new Vector2(infoX - 4f * scale, max.Y), true);
        drawList.AddText(new Vector2(textX, titleY),
            ImGui.GetColorU32(ImGuiCol.Text), title);
        ImGui.PopClipRect();
        return clicked;
    }

    private bool IsManaged(GroupFullInfoDto group)
        => string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal)
            || group.GroupUserInfo.IsModerator()
            || group.GroupUserInfo.IsStaff();

    /// <summary>Two-column cover-card grid · banner art carries the card.</summary>
    private void DrawShellCardGrid(List<(GroupFullInfoDto Group, List<Pair> Members, bool AreaBound)> cards)
    {
        var gap = ImGui.GetStyle().ItemSpacing.X;
        // Fixed card width · the grid wraps into however many columns fit
        // instead of stretching covers with the window.
        var cardW = 296f * ImGuiHelpers.GlobalScale;
        var cols = Math.Max(1, (int)MathF.Floor(
            (ImGui.GetContentRegionAvail().X + gap) / (cardW + gap)));
        var cardSize = new Vector2(cardW, MathF.Floor(cardW * 0.46f));
        for (var i = 0; i < cards.Count; i++)
        {
            if (i > 0)
            {
                if (i % cols != 0)
                {
                    ImGui.SameLine(0f, gap);
                }
                else
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Inline);
                }
            }
            DrawShellCard(cards[i].Group, cards[i].Members, cardSize, cards[i].AreaBound);
        }
    }

    /// <summary>One cover card: banner cover, role pill, name + member line.</summary>
    private void DrawShellCard(GroupFullInfoDto group, List<Pair> members, Vector2 size,
        bool areaBound)
    {
        if (group.Group == null)
        {
            return;
        }
        var isOwner = string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        var isModerator = group.GroupUserInfo.IsModerator();
        var isStaff = group.GroupUserInfo.IsStaff();
        var accent = isOwner ? SpheneUI.Palette.NetworkWarning
            : isModerator || isStaff ? SpheneUI.Palette.EtherealPurple
            : areaBound ? SpheneUI.Palette.CrystalBlue
            : SpheneUI.Palette.NetworkActive;
        var role = isOwner ? "Owner" : isModerator || isStaff ? "Staff" : null;
        // Server-total membership is the same for every viewer; the local
        // presence union stays only as fallback for old servers.
        var memberCount = group.MemberCount >= 0
            ? group.MemberCount
            : ShellPresence(group, members).Members;
        var onlineCount = ShellPresence(group, members).Online;
        var stat = areaBound
            ? "Area-bound"
            : $"{memberCount} members · {onlineCount} online";

        var media = ShellMedia(group.Group.GID);
        IDalamudTextureWrap? Cover()
        {
            var gid = group.Group.GID;
            if (media is { } p
                && (!string.IsNullOrEmpty(p.BannerMediaHash) || !string.IsNullOrEmpty(p.BannerImageBase64)))
            {
                return SpheneUI.Media.Texture($"hub-shell:cover:{gid}:{SyncshellCommunityView.MediaVersion(p.BannerMediaHash, p.BannerImageBase64)}",
                    SyncshellCommunityView.ResolveBytes(p.BannerMediaHash, p.BannerImageBase64));
            }
            if (media is { } p2
                && (!string.IsNullOrEmpty(p2.AvatarMediaHash) || !string.IsNullOrEmpty(p2.AvatarImageBase64)))
            {
                return SpheneUI.Media.Texture($"hub-shell:avatar:{gid}:{SyncshellCommunityView.MediaVersion(p2.AvatarMediaHash, p2.AvatarImageBase64)}",
                    SyncshellCommunityView.ResolveBytes(p2.AvatarMediaHash, p2.AvatarImageBase64));
            }
            return null;
        }

        if (SyncshellCard.Cover("shellcover-" + group.Group.GID, size, Cover,
                role, accent, group.GroupAliasOrGID, stat, accent))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Syncshells, group.Group.GID, null));
        }
    }



    private readonly HousingPageView _housingPageView;
    private static readonly SyncshellProfileDto EmptyShellProfile = new(string.Empty,
        null, null, null, null, null, null, null, null, null, null, null, null,
        false, false, false, DateTime.UnixEpoch, DateTime.UnixEpoch);
    private readonly ConcurrentDictionary<string, SyncshellProfileDto> _shellMediaCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _shellMediaLoading = new(StringComparer.Ordinal);

    /// <summary>Per-shell avatar/banner for list rows · lazily fetched once each.</summary>
    /// <summary>Card media via the slim profile read - one welcome-page query
    /// per shell instead of the full community aggregate per list row.</summary>
    /// <summary>Directory-card cover texture - hash media resolve through the
    /// shared media client, inline base64 keeps working for pre-migration
    /// shells. Keys are content-versioned, so a replaced image needs no evict.</summary>
    private static IDalamudTextureWrap? DirectoryCover(SyncshellDirectoryEntryDto e)
    {
        if (e.BannerMediaHash != null || !string.IsNullOrEmpty(e.BannerImageBase64))
        {
            return SpheneUI.Media.Texture(
                $"hub-dir:banner:{e.GroupGID}:{SyncshellCommunityView.MediaVersion(e.BannerMediaHash, e.BannerImageBase64)}",
                SyncshellCommunityView.ResolveBytes(e.BannerMediaHash, e.BannerImageBase64));
        }
        if (e.AvatarMediaHash != null || !string.IsNullOrEmpty(e.AvatarImageBase64))
        {
            return SpheneUI.Media.Texture(
                $"hub-dir:avatar:{e.GroupGID}:{SyncshellCommunityView.MediaVersion(e.AvatarMediaHash, e.AvatarImageBase64)}",
                SyncshellCommunityView.ResolveBytes(e.AvatarMediaHash, e.AvatarImageBase64));
        }
        return null;
    }

    private SyncshellProfileDto? ShellMedia(string gid)
    {
        if (_shellMediaCache.TryGetValue(gid, out var media))
        {
            return ReferenceEquals(media, EmptyShellProfile) ? null : media;
        }
        if (_shellMediaLoading.TryAdd(gid, 0))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var profile = await _apiController
                        .GroupGetCommunityProfile(new GroupDto(new GroupData(gid)))
                        .ConfigureAwait(false);
                    _shellMediaCache[gid] = profile ?? EmptyShellProfile;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load shell media for {GID}", gid);
                    _shellMediaCache[gid] = EmptyShellProfile;
                }
                finally
                {
                    _shellMediaLoading.TryRemove(gid, out _);
                }
            });
        }
        return null;
    }

    private void DrawAreaBoundSection(HashSet<string> areaJoinedGids,
        HashSet<string> managedAreaGids)
    {
        // Joined area-bound shells still live in GroupPairs · the fetched
        // GID set just tells us which of them are location-bound. Shells the
        // user owns or moderates were already pinned to "Managed by you".
        var joinedArea = new List<KeyValuePair<GroupFullInfoDto, List<Pair>>>();
        foreach (var kv in _pairManager.GroupPairs)
        {
            if (kv.Key.Group != null && areaJoinedGids.Contains(kv.Key.Group.GID)
                && !managedAreaGids.Contains(kv.Key.Group.GID))
            {
                joinedArea.Add(kv);
            }
        }
        var activeHere = _areaBoundService.IsInAreaSyncshellInCurrentLocation();
        var hasHere = _areaBoundService.HasAvailableAreaSyncshells();
        if (joinedArea.Count == 0 && !activeHere && !hasHere)
        {
            // No joined area shells and nothing bound to this location  · 
            // the section earns its place instead of sitting there empty.
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
        SpheneUI.Text.SectionHeader("Area-Bound Syncshells");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.PageIntro(
            "Bound to in-game locations · they join automatically when you walk in. "
            + "Each joined one opens its own page, just like a classic syncshell.");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);

        // Location-aware status stays · it is the actually actionable bit:
        // shells bound to where you are right now can be joined on the spot.
        if (activeHere)
        {
            StatusLine(FontAwesomeIcon.MapMarkerAlt, SpheneUI.Palette.Success,
                "Active here", "a bound syncshell is live in your current location");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        }
        else if (hasHere)
        {
            if (HubRow("area-here", FontAwesomeIcon.MapMarkerAlt, SpheneUI.Palette.CrystalBlue,
                    "Syncshells bound to this area", null, default,
                    "You're inside a bound location right now · pick one to join on the spot."))
            {
                _areaBoundService.TriggerAreaSyncshellSelection();
            }
            SpheneUI.AttachTooltip("Opens the area syncshell picker for your current location");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        }

        var areaCards = joinedArea
            .OrderBy(k => k.Key.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var areaGap = ImGui.GetStyle().ItemSpacing.X;
        var areaCardW = 296f * ImGuiHelpers.GlobalScale;
        var areaCols = Math.Max(1, (int)MathF.Floor(
            (ImGui.GetContentRegionAvail().X + areaGap) / (areaCardW + areaGap)));
        var areaCardSize = new Vector2(areaCardW, MathF.Floor(areaCardW * 0.46f));
        for (var i = 0; i < areaCards.Count; i++)
        {
            if (i > 0)
            {
                if (i % areaCols != 0)
                {
                    ImGui.SameLine(0f, areaGap);
                }
                else
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Inline);
                }
            }
            var kv = areaCards[i];
            DrawShellCard(kv.Key, kv.Value, areaCardSize, areaBound: true);
        }
    }

    private static void StatusLine(FontAwesomeIcon icon, Vector4 color, string status, string meta)
    {
        ImGui.AlignTextToFramePadding();
        SpheneUI.Icons.Icon(icon, color);
        ImGui.SameLine();
        SpheneUI.Text.Colored(status, color);
        ImGui.SameLine();
        SpheneUI.Text.Muted(" ·  " + meta);
    }

    private void EnsureAreaBoundShellsLoaded()
    {
        if (_areaBoundShells != null || _areaBoundLoading || !_apiController.IsConnected)
        {
            return;
        }
        _areaBoundLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _areaBoundShells = await _apiController.GroupGetAreaBoundSyncshells().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load area-bound syncshells");
                _areaBoundShells = [];
            }
            finally
            {
                _areaBoundLoading = false;
            }
        });
    }

    private void DrawSyncshellRail(
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories, float pinnedCenterY)
    {
        var group = GetSelectedGroup();
        if (group?.Group == null)
        {
            return;
        }
        var admin = CanAdminister(group);
        var isAreaBound = group.GroupPairUserInfos.Count == 0;
        var accent = admin ? SpheneUI.Palette.NetworkWarning : SpheneUI.Palette.CrystalBlue;
        // Staff roles resolve against the community cache · pull it early so
        // manage affordances appear for non-mod staff as soon as data lands.
        EnsureCommunityLoaded(group.Group.GID);

        // The shell's name becomes the pinned band label · the rail slides to
        // shell scope exactly like the settings section rails do.
        SpheneUI.Layout.NavRailCategoryLabel(group.GroupAliasOrGID, accent, pinnedCategories, pinnedCenterY);

        void RailItem(string label, FontAwesomeIcon icon, SyncshellDetailTab tab, Vector4 itemAccent)
        {
            if (SpheneUI.Layout.NavRailSegmentItem(label, icon, _syncshellDetailTab == tab, itemAccent))
            {
                SwitchDetailTab(group, tab);
            }
        }

        // Hub-style grouped accents: shell pages get the community blue
        // family (members in the roster green), management pages the tools
        // purple and window actions the shell accent.
        var accentCommunity = SpheneTheme.Current.AccentBlue;
        var accentMembers = SpheneUI.Palette.NetworkActive;
        var accentTools = SpheneUI.Palette.EtherealPurple;
        var accentArea = new Vector4(0.93f, 0.68f, 0.38f, 1f);

        RailItem("Community", FontAwesomeIcon.IdCard, SyncshellDetailTab.Community, accentCommunity);
        if (!isAreaBound)
        {
            RailItem("Members", FontAwesomeIcon.Users, SyncshellDetailTab.Members, accentMembers);
            // The Event Planner is the members' and staff's shared event
            // workspace - submissions, own-event edits and management all
            // live there now.
            if (CommunityStaffPermission(group, SyncshellStaffPermission.ManageEvents)
                || group.GroupPairUserInfos.ContainsKey(_apiController.UID)
                || admin)
            {
                RailItem("Event Planner", FontAwesomeIcon.CalendarAlt,
                    SyncshellDetailTab.EventPlanner, accentCommunity);
            }
        }
        // Roles & Staff sits above the admin block · reachable for shell
        // admins and for staff whose role carries the Roles right.
        var canRoles = CommunityStaffPermission(group, SyncshellStaffPermission.ManageRoles);
        if (canRoles)
        {
            SpheneUI.Layout.NavRailDivider(accent, accentTools);
            RailItem("Roles & Staff", FontAwesomeIcon.UserTag, SyncshellDetailTab.Staff, accentTools);
        }
        if (admin)
        {
            if (!canRoles)
            {
                SpheneUI.Layout.NavRailDivider(accent, accentTools);
            }
            RailItem("Settings", FontAwesomeIcon.SlidersH, SyncshellDetailTab.Settings, accentTools);
            if (string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal))
            {
                RailItem("Area Binding", FontAwesomeIcon.MapMarkedAlt, SyncshellDetailTab.AreaBinding,
                    accentArea);
            }
        }

        SpheneUI.Layout.NavRailDivider(accent, accent);
        if (CommunityFor(group.Group.GID) is { } popCommunity)
        {
            if (SpheneUI.Layout.NavRailSegmentItem("Preview", FontAwesomeIcon.Eye, false, accent))
            {
                Mediator.Publish(new OpenSyncshellCommunityMessage(popCommunity, group,
                    JoinPopup: false, PreviewAsVisitor: true));
            }
            SpheneUI.AttachTooltip("Preview the Community page as members see it - in a separate window");
        }
        if (admin)
        {
            var manageClicked = SpheneUI.Layout.NavRailSegmentItem("Manage Shell",
                FontAwesomeIcon.ShieldAlt, false, accent);
            SpheneUI.AttachTooltip("Open the management window - the same pages as this rail, minus Community");
            if (manageClicked)
            {
                Mediator.Publish(new OpenSyncshellAdminPanel(group));
            }
        }
    }

    private void DrawSyncshellDetail(GroupFullInfoDto group)
    {
        if (group.Group == null)
        {
            SpheneUI.Text.Muted("Syncshell data unavailable.");
            return;
        }

        var admin = CanAdminister(group);
        var isAreaBound = group.GroupPairUserInfos.Count == 0;

        // Identity strip on every detail page except Community · that one
        // opens with its own banner/avatar header.
        if (_syncshellDetailTab != SyncshellDetailTab.Community)
        {
            DrawShellIdentityHeader(group, isAreaBound);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        }

        switch (_syncshellDetailTab)
        {
            case SyncshellDetailTab.Members when !isAreaBound:
                if (admin)
                {
                    GetOrCreateAdminPanel(group).DrawEmbeddedPage(SyncshellAdminUI.AdminPage.Members);
                }
                else
                {
                    DrawSyncshellMembersTab(group);
                }
                break;
            case SyncshellDetailTab.Staff when CommunityStaffPermission(group, SyncshellStaffPermission.ManageRoles):
                GetOrCreateAdminPanel(group).DrawEmbeddedPage(SyncshellAdminUI.AdminPage.Staff);
                break;
            case SyncshellDetailTab.Settings when admin:
                GetOrCreateAdminPanel(group).DrawEmbeddedPage(SyncshellAdminUI.AdminPage.Settings);
                break;
            case SyncshellDetailTab.AreaBinding when admin:
                GetOrCreateAdminPanel(group).DrawEmbeddedPage(SyncshellAdminUI.AdminPage.AreaBinding);
                break;
            case SyncshellDetailTab.EventPlanner when !isAreaBound
                && (CommunityStaffPermission(group, SyncshellStaffPermission.ManageEvents)
                    || group.GroupPairUserInfos.ContainsKey(_apiController.UID)):
                DrawEventPlannerTab(group);
                break;
            default:
                DrawSyncshellCommunityTab(group, isAreaBound);
                break;
        }
    }

    private void SwitchDetailTab(GroupFullInfoDto group, SyncshellDetailTab tab)
    {
        if (_syncshellDetailTab == tab)
        {
            return;
        }
        NavigateTo(new HubLocation(SpheneHubPage.Syncshells, group.Group.GID, null, (int)tab));
    }

    /// <summary>
    /// Compact identity strip for non-community detail pages · shell avatar,
    /// name and a meta line (owner · members · your role) so every admin page
    /// still reads as "inside this shell".
    /// </summary>
    private void DrawShellIdentityHeader(GroupFullInfoDto group, bool isAreaBound)
    {
        EnsureCommunityLoaded(group.Group.GID);
        var profile = CommunityFor(group.Group.GID)?.Profile;
        var accent = SyncshellCommunityView.ParseAccent(profile?.AccentColor) ?? SpheneUI.Palette.CrystalBlue;

        SpheneUI.Layout.SectionCard(() =>
        {
            var avatarSize = 40f * SpheneUI.Scale;
            IDalamudTextureWrap? avatar = null;
            if (profile != null
                && (!string.IsNullOrEmpty(profile.AvatarMediaHash)
                    || !string.IsNullOrEmpty(profile.AvatarImageBase64)))
            {
                // Same resolution path as the community header - hash-backed
                // media first, legacy base64 as fallback.
                avatar = SpheneUI.Media.Texture($"hub-shell-avatar:{group.Group.GID}",
                    SyncshellCommunityView.ResolveBytes(profile.AvatarMediaHash,
                        profile.AvatarImageBase64));
            }
            SpheneUI.Media.Avatar(avatar, avatarSize, placeholderIcon: FontAwesomeIcon.Users);
            ImGui.SameLine();

            ImGui.BeginGroup();
            SpheneUI.Text.Big(group.GroupAliasOrGID);
            var isOwner = string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal);
            var role = isOwner ? "Owner"
                : group.GroupUserInfo.IsModerator() || group.GroupUserInfo.IsStaff() ? "Staff member"
                : isAreaBound ? "Area-bound" : "Member";
            var identityMembers = group.MemberCount >= 0
                ? group.MemberCount
                : _pairManager.GroupPairs.TryGetValue(group, out var pairs)
                    ? ShellPresence(group, pairs).Members
                    : group.GroupPairUserInfos.Count;
            var meta = $"hosted by {group.OwnerAliasOrUID} · {identityMembers} members · {role}";
            SpheneUI.Text.Colored(meta, SpheneUI.Colors.TextMuted);
            ImGui.EndGroup();
        }, accentColor: accent, animatedBackdrop: false, id: "shell-identity-" + group.Group.GID);
    }

    private void DrawSyncshellCommunityTab(GroupFullInfoDto group, bool isAreaBound)
    {
        EnsureCommunityLoaded(group.Group.GID);
        var community = CommunityFor(group.Group.GID);
        if (community == null)
        {
            if (_communityNoPage.ContainsKey(group.Group.GID))
            {
                SpheneUI.Layout.SectionCard(() =>
                {
                    SpheneUI.Text.MutedWrapped(isAreaBound
                        ? "This area-bound syncshell has no community page yet."
                        : "This syncshell has no community page yet.");
                }, "Community", accentColor: SpheneUI.Palette.CrystalBlue);
            }
            else if (!_communityLoadingMap.ContainsKey(group.Group.GID)
                && _communityFailedAt.ContainsKey(group.Group.GID))
            {
                SpheneUI.Text.MutedWrapped("Community page unavailable right now.");
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                if (SpheneUI.Controls.Button("Retry##communityretry"))
                {
                    _communityFailedAt.TryRemove(group.Group.GID, out _);
                    _ = FetchCommunityAsync(group.Group.GID);
                }
            }
            else
            {
                SpheneUI.Text.Muted("Loading community page...");
            }
            return;
        }

        // Staged community edits ride a per-shell session · switching shells
        // drops the previous draft so nothing leaks across communities.
        var communityGid = community.Profile.GroupGID;
        if (!string.Equals(_communityEditSessionGid, communityGid, StringComparison.Ordinal))
        {
            _communityEditSession.Reset();
            _communityEditSessionGid = communityGid;
            _communityEditSessionGroup = group.Group;
        }
        var effectiveCommunity = _communityEditSession.Effective(community);
        var effectiveGroup = _communityEditSession.Alias is { } stagedAlias
            && !string.Equals(stagedAlias, group.Group.Alias, StringComparison.Ordinal)
                ? group with { Group = group.Group with { Alias = stagedAlias } }
                : group;

        var manageEdit = BuildManageEdit(group, effectiveCommunity);

        _communityOverlay = SyncshellCommunityView.DrawContent(effectiveCommunity, effectiveGroup,
            "hub-community:", manageEdit, page: _communityPage,
            travel: CommunityComposers.BuildTravelHooks(group.Group,
                _lifestreamTravel, _dalamudUtilService, _ipcManager.Lifestream),
            onViewerPopout: (imgs, i) => Mediator.Publish(
                new OpenSyncshellImageViewerMessage(imgs, i, community.Profile.GroupGID)),
            members: new SyncshellCommunityView.CommunityMemberHooks(
                GetProfile: uid => _spheneProfileManager.GetSpheneProfile(new UserData(uid)),
                GetDisplayName: uid => CommunityNameFor(uid, effectiveGroup,
                    effectiveCommunity),
                CanOpenProfile: uid => _pairManager.GetPairByUID(uid) != null,
                OpenProfile: uid =>
                {
                    var pair = _pairManager.GetPairByUID(uid);
                    if (pair != null)
                    {
                        NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair));
                    }
                }),
            onLikeImage: img => ToggleCommunityImageLike(group.Group, img));

        // Area-bound shells have no Members tab · the leave affordance lives
        // on the community page for them; normal shells carry it on Members.
        if (isAreaBound && CanLeave(group))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            DrawLeaveCard(group);
        }

        var toastPos = ImGui.GetWindowPos();
        var toastSize = ImGui.GetWindowSize();
        _communityToast.Draw(_communityEditSession.IsDirty,
            "You have unsaved community changes",
            toastPos.X + toastSize.X * 0.5f,
            toastPos.Y + toastSize.Y - 6f * SpheneUI.Scale,
            new UnsavedChangesToast.Button("Reset", FontAwesomeIcon.Undo,
                SpheneUI.Colors.Accent, true,
                "Drop the staged changes.", ResetHubCommunityEdits),
            new UnsavedChangesToast.Button("Apply", FontAwesomeIcon.Check,
                SpheneUI.Palette.LuminousGold, true,
                "Publish the staged community changes.", () => _ = ApplyHubCommunityEditsAsync(),
                Solid: true));
    }

    /// <summary>
    /// The shared manage contract - the community page and the Event Planner
    /// both build on it so affordances behave identically. Identity/aggregate
    /// edits stage into the session; the planner hooks (edit routing,
    /// organizer picker, live preview) are wired here too.
    /// </summary>
    private SyncshellCommunityView.SyncshellCommunityEdit? BuildManageEdit(
        GroupFullInfoDto group, SyncshellCommunityDto effectiveCommunity)
    {
        var community = CommunityFor(group.Group.GID) ?? effectiveCommunity;
        SyncshellCommunityView.SyncshellCommunityEdit? manageEdit;
        if (CanAdminister(group) || CommunityStaffCanManage(group))
        {
            var communityOps = CommunityComposers.CreateOps(group.Group,
                () => CommunityFor(group.Group.GID) ?? community, RefreshCommunity,
                () => StaffMembers(group),
                mutate: p => MutateCommunity(group.Group.GID, p));
            communityOps.Patch = patch => _communityEditSession.Stage(patch);
            manageEdit = new SyncshellCommunityView.SyncshellCommunityEdit
            {
                CanManagePage = CommunityStaffPermission(group, SyncshellStaffPermission.ManagePage),
                CanManageNews = CommunityStaffPermission(group, SyncshellStaffPermission.ManageNews),
                CanManageEvents = CommunityStaffPermission(group, SyncshellStaffPermission.ManageEvents),
                CanManageGallery = CommunityStaffPermission(group, SyncshellStaffPermission.ManageGallery),
                CanManageStaff = CommunityStaffPermission(group, SyncshellStaffPermission.ManageRoles),
                CanEditIdentity = CommunityStaffPermission(group, SyncshellStaffPermission.ManagePage),
                CanSubmit = !CommunityStaffPermission(group, SyncshellStaffPermission.ManageNews)
                    || !CommunityStaffPermission(group, SyncshellStaffPermission.ManageGallery),
                Listed = effectiveCommunity.IsListedPublicly,
                Ops = communityOps,
                OnAddPhoto = () => CommunityComposers.PickPhoto(group.Group,
                    community.Gallery?.Count ?? 0, RefreshCommunity,
                    CommunityStaffPermission(group, SyncshellStaffPermission.ManageGallery)),
                OnBannerClicked = () => CommunityComposers.PickProfileImage(community, group.Group,
                    "hub-community:", true, RefreshCommunity,
                    staged => StageHubCommunityImage(staged, true)),
                OnAvatarClicked = () => CommunityComposers.PickProfileImage(community, group.Group,
                    "hub-community:", false, RefreshCommunity,
                    staged => StageHubCommunityImage(staged, false)),
                OnNameCommitted = v => _communityEditSession.StageAlias(v),
                OnTaglineCommitted = v => _communityEditSession.Stage(
                    c => c with { Profile = c.Profile with { Tagline = v } }),
                OnAboutCommitted = text => _communityEditSession.Stage(
                    c => c with { Profile = c.Profile with { Description = text } }),
                OnRulesCommitted = text => _communityEditSession.Stage(
                    c => c with { RulesText = text }),
                OnSaveNews = (composer, draft) =>
                    CommunityComposers.SaveNews(group.Group, composer, draft, RefreshCommunity,
                        CommunityStaffPermission(group, SyncshellStaffPermission.ManageNews)),
                OnSaveEvent = composer => CommunityComposers.SaveEvent(group.Group, composer,
                    RefreshCommunity,
                    CommunityStaffPermission(group, SyncshellStaffPermission.ManageEvents)),
                OnDeleteNews = post => CommunityComposers.DeleteNews(group.Group, post, RefreshCommunity),
                OnDeleteEvent = ev => CommunityComposers.DeleteEvent(group.Group, ev, RefreshCommunity),
            };
            CommunityComposers.WireEventOps(manageEdit, group.Group,
                CommunityStaffPermission(group, SyncshellStaffPermission.ManageEvents),
                RefreshCommunity);
            CommunityComposers.WireCommentOps(manageEdit, group.Group, RefreshCommunity,
                uid => CommunityNameFor(uid, group, CommunityFor(group.Group.GID)),
                CommentProfile);
            CommunityComposers.WireTravelOps(manageEdit, group.Group,
                _lifestreamTravel, _dalamudUtilService, _ipcManager.Lifestream,
                RefreshCommunity);
        }
        else if (group.GroupPairUserInfos.ContainsKey(_apiController.UID))
        {
            // Plain member - no manage flags, but RSVP/calendar hooks and
            // member content submissions (news/events → review queue).
            manageEdit = new SyncshellCommunityView.SyncshellCommunityEdit
            {
                CanSubmit = true,
                OnSaveNews = (composer, draft) => CommunityComposers.SaveNews(
                    group.Group, composer, draft, RefreshCommunity, staff: false),
                OnSaveEvent = composer => CommunityComposers.SaveEvent(
                    group.Group, composer, RefreshCommunity, staff: false),
                OnAddPhoto = () => CommunityComposers.PickPhoto(group.Group,
                    community.Gallery?.Count ?? 0, RefreshCommunity, staff: false),
            };
            CommunityComposers.WireEventOps(manageEdit, group.Group, false, RefreshCommunity);
            CommunityComposers.WireCommentOps(manageEdit, group.Group, RefreshCommunity,
                uid => CommunityNameFor(uid, group, CommunityFor(group.Group.GID)),
                CommentProfile);
            CommunityComposers.WireTravelOps(manageEdit, group.Group,
                _lifestreamTravel, _dalamudUtilService, _ipcManager.Lifestream,
                RefreshCommunity);
        }
        else
        {
            return null;
        }

        // Planner hooks - editing routes out of the attendee page into the
        // workspace; the organizer picker resolves member identities; the
        // preview opens a separate inert window.
        manageEdit.OnEditEvent = (ev, occ) =>
        {
            if (occ != null)
            {
                _communityPage.EventComposer.OpenOccurrence(ev, occ);
            }
            else
            {
                _communityPage.EventComposer.OpenEdit(ev);
            }
            SwitchDetailTab(group, SyncshellDetailTab.EventPlanner);
        };
        var ownUid = _apiController.UID;
        manageEdit.CanEditEvent = ev => manageEdit.CanManageEvents
            || string.Equals(ev.AuthorUid, ownUid, StringComparison.Ordinal)
            || (ev.OrganizerUids?.Contains(ownUid, StringComparer.Ordinal) ?? false);
        manageEdit.MemberOptions = () => _pairManager.GroupPairs
            .TryGetValue(group, out var pairs)
                ? pairs.Select(p => (p.UserData.UID, p.UserData.PreferredName))
                    .ToList()
                : (IReadOnlyList<(string Uid, string Label)>)[];
        manageEdit.MemberLabel = uid =>
            _pairManager.GetPairByUID(uid)?.UserData.PreferredName ?? "Unknown user";
        manageEdit.MentionCandidates = () =>
        {
            var list = new List<UI.Components.MarkdownComposer.MentionCandidate>();
            if (_pairManager.GroupPairs.TryGetValue(group, out var gps))
            {
                foreach (var p in gps)
                {
                    list.Add(new(p.UserData.UID, p.UserData.PreferredName, "Syncshell member"));
                }
            }
            foreach (var p in _pairManager.DirectPairs)
            {
                if (list.All(m => !string.Equals(m.Uid, p.UserData.UID, StringComparison.Ordinal)))
                {
                    list.Add(new(p.UserData.UID, p.UserData.PreferredName, "Friend"));
                }
            }
            return list;
        };
        manageEdit.OnPreviewEvent = composer => Mediator.Publish(
            new OpenEventPreviewMessage(group.Group, community, group, composer));
        manageEdit.OnViewEvent = ev =>
        {
            SwitchDetailTab(group, SyncshellDetailTab.Community);
            _communityPage.EventId = ev.Id;
        };
        return manageEdit;
    }

    /// <summary>The Event Planner workspace page - shares the manage contract
    /// and the travel hooks with the community surface.</summary>
    private void DrawEventPlannerTab(GroupFullInfoDto group)
    {
        EnsureCommunityLoaded(group.Group.GID);
        var community = CommunityFor(group.Group.GID);
        if (community == null)
        {
            SpheneUI.Text.Muted("Loading events…");
            return;
        }
        var manageEdit = BuildManageEdit(group, community);
        if (manageEdit == null)
        {
            SpheneUI.Text.MutedWrapped(
                "You don't have event permissions in this Syncshell.");
            return;
        }
        // Consume a pending deep-link edit seed - the composer opens on the
        // requested event (or its occurrence date) before the planner draws.
        if (_pendingEditEventId is { } editEventId)
        {
            var target = community.Events?.FirstOrDefault(e => e.Id == editEventId);
            if (target != null && manageEdit.CanEditEvent?.Invoke(target) == true)
            {
                if (_pendingEditOccurrence is { } occ)
                {
                    _communityPage.EventComposer.OpenOccurrence(target, occ);
                }
                else
                {
                    _communityPage.EventComposer.OpenEdit(target);
                }
            }
            _pendingEditEventId = null;
            _pendingEditOccurrence = null;
        }
        var accent = SyncshellCommunityView.ParseAccent(community.Profile.AccentColor)
            ?? SpheneUI.Palette.CrystalBlue;
        Syncshell.EventPlannerView.Draw(_communityPage, manageEdit,
            CommunityComposers.BuildTravelHooks(group.Group,
                _lifestreamTravel, _dalamudUtilService, _ipcManager.Lifestream),
            community.Events ?? [], accent);
    }

    /// <summary>Stages a picked banner/avatar into the community session and
    /// drops the cached textures so the preview picks up the new image.</summary>
    private void StageHubCommunityImage(SyncshellCommunityDto staged, bool banner)
        => _communityEditSession.StageImage(staged, banner);

    /// <summary>Drops every staged community change without saving.</summary>
    private void ResetHubCommunityEdits() => _communityEditSession.Reset();

    /// <summary>Commits the staged aggregate as one <c>GroupSetCommunity</c>
    /// plus the staged alias via <c>GroupSetAlias</c>, then refreshes. A failed
    /// save keeps the draft so the user can retry.</summary>
    private async Task ApplyHubCommunityEditsAsync()
    {
        var group = _communityEditSessionGroup;
        var community = CommunityCache;
        if (group == null || community == null)
        {
            _communityEditSession.Reset();
            return;
        }
        var gid = community.Profile.GroupGID;
        try
        {
            var toSave = _communityEditSession.Effective(community);
            var ok = await _apiController.GroupSetCommunity(
                CommunityComposers.ToUpdateDto(toSave, group)).ConfigureAwait(false);
            if (!ok)
            {
                return;
            }
            var stagedAlias = _communityEditSession.Alias;
            if (!string.IsNullOrWhiteSpace(stagedAlias)
                && !string.Equals(stagedAlias, group.Alias, StringComparison.Ordinal))
            {
                await _apiController.GroupSetAlias(
                    new GroupAliasDto(group, stagedAlias)).ConfigureAwait(false);
            }
            _communityEditSession.Reset();
            await RefreshCommunity().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply syncshell community edits for {GID}", gid);
        }
    }

    /// <summary>Toggles a gallery like and patches the cached community gallery.</summary>
    private async Task<SyncshellGalleryImageDto?> ToggleCommunityImageLike(
        GroupData groupData, SyncshellGalleryImageDto img)
    {
        if (CommunityCache == null || img.Id <= 0)
        {
            return null;
        }
        try
        {
            var result = await _apiController.GroupToggleCommunityGalleryImageLike(
                new SyncshellGalleryImageLikeDto(groupData, img.Id, false, 0))
                .ConfigureAwait(false);
            if (result == null)
            {
                return null;
            }
            var gallery = CommunityCache?.Gallery;
            if (gallery == null)
            {
                return null;
            }
            for (var i = 0; i < gallery.Count; i++)
            {
                if (gallery[i].Id == result.ImageId)
                {
                    gallery[i] = gallery[i] with { LikedByMe = result.Liked, LikeCount = result.Count };
                    return gallery[i];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle gallery image like");
        }
        return null;
    }

    private void DrawLeaveCard(GroupFullInfoDto group)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            if (SpheneUI.Controls.Button("Leave Syncshell", danger: true))
            {
                _ = _apiController.GroupLeaveFully(new GroupDto(group.Group));
                NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null));
            }
        }, title: "Danger Zone", accentColor: SpheneUI.Colors.Danger);
    }

    private void DrawSyncshellMembersTab(GroupFullInfoDto group)
    {
        if (group.Group == null)
        {
            return;
        }
        _pairManager.GroupPairs.TryGetValue(group, out var members);
        var onlineUids = new HashSet<string>(StringComparer.Ordinal);
        if (members != null)
        {
            foreach (var pair in members)
            {
                if (pair.IsOnline && pair.UserData?.UID != null)
                {
                    onlineUids.Add(pair.UserData.UID);
                }
            }
        }

        var selfUid = _apiController.UID;
        var staff = group.GroupPairUserInfos
            .Where(kv => string.Equals(kv.Key, group.OwnerUID, StringComparison.Ordinal) || kv.Value.IsModerator())
            .OrderByDescending(kv => string.Equals(kv.Key, group.OwnerUID, StringComparison.Ordinal))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var regulars = group.GroupPairUserInfos
            .Where(kv => !string.Equals(kv.Key, group.OwnerUID, StringComparison.Ordinal) && !kv.Value.IsModerator())
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var total = staff.Count + regulars.Count;

        // Same Members widget the admin page uses · gauge and roster
        // inside one tonal card.
        var accent = SpheneUI.Palette.CrystalBlue;
        SyncshellCommunityView.Widget("shell-members", "Members", FontAwesomeIcon.Users,
            total, accent, () =>
        {
            SyncshellRoster.PresenceGauge(onlineUids.Count, total, accent);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain($"{onlineUids.Count} of {total} souls present");

            if (total == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Users, "No member info",
                    "Member details are not available for this shell.");
                return;
            }

            // Roster - staff and regulars share one row list (owner first),
            // the pills carry the staff badges, so no separate chip strip.
            var communityHere = CommunityFor(group.Group.GID);
            foreach (var kv in staff.Concat(regulars))
            {
                var isOwner = string.Equals(kv.Key, group.OwnerUID, StringComparison.Ordinal);
                var memberPair = _pairManager.GetPairByUID(kv.Key);
                var label = isOwner ? group.OwnerAliasOrUID
                    : memberPair?.UserData.PreferredName ?? kv.Key;
                var sub = string.Equals(kv.Key, selfUid, StringComparison.Ordinal) ? "you" : null;
                var mProfile = memberPair != null
                    ? _spheneProfileManager.GetSpheneProfile(memberPair.UserData)
                    : null;
                var mAvatar = mProfile != null && mProfile.ImageData.Value.Length > 0
                    ? SpheneUI.Media.Texture($"roster:{group.Group.GID}:{kv.Key}", mProfile.ImageData.Value)
                    : null;
                var memberRoles = SyncshellCommunityView.MemberRoles(communityHere, kv.Key);
                var staffRole = memberRoles.Count > 0 ? memberRoles[0] : null;
                var isMod = kv.Value.IsModerator();
                var roleColor = isOwner ? SpheneUI.Palette.SpheneGold
                    : staffRole != null ? SyncshellRoster.StaffRoleColor(staffRole.ColorArgb, SpheneUI.Palette.EtherealPurple)
                    : isMod ? SpheneUI.Palette.EtherealPurple
                    : SpheneUI.Colors.TextMuted;
                SyncshellRoster.RoleBadge? staffBadge = isOwner
                    ? new(StaffRoleIcons.Owner, SpheneUI.Palette.SpheneGold, "Owner")
                    : isMod || memberRoles.Count > 0
                        ? new(StaffRoleIcons.StaffFallback, SpheneUI.Palette.EtherealPurple, "Staff member")
                        : null;
                var badges = new List<SyncshellRoster.RoleBadge>(memberRoles.Count + 1);
                foreach (var r in memberRoles)
                {
                    badges.Add(new(StaffRoleIcons.Parse(r.Icon),
                        SyncshellRoster.StaffRoleColor(r.ColorArgb, SpheneUI.Palette.EtherealPurple), r.Name));
                }
                if (!isOwner && kv.Value.IsPinned())
                {
                    badges.Add(new(FontAwesomeIcon.Thumbtack, SpheneUI.Colors.TextMuted, "Pinned"));
                }
                SyncshellRoster.MemberRow("hub-m-" + kv.Key, onlineUids.Contains(kv.Key), label, sub,
                    null, roleColor, null, null,
                    mAvatar, mProfile?.ProfileTheme.Value, mProfile?.IsNSFW ?? false,
                    null, badges, staffBadge: staffBadge);
            }
        });

        if (CanLeave(group))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            DrawLeaveCard(group);
        }
    }

    private bool CanLeave(GroupFullInfoDto group)
        => !string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal);

    /// <summary>Force-refetch after an inline community edit (composer saves).</summary>
    /// <summary>Coalesced community refresh - every mutation path lands here
    /// (composer saves AND pushed <c>CommunityChanged</c>), so a burst of
    /// changes collapses into one aggregate fetch per shell instead of one
    /// fetch per mutation per viewer.</summary>
    private Task RefreshCommunity()
    {
        var gid = _communityCacheGid;
        if (gid == null)
        {
            return Task.CompletedTask;
        }
        if (_communityRefreshScheduled.TryAdd(gid, 0))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(CommunityRefreshDebounce).ConfigureAwait(false);
                }
                finally
                {
                    _communityRefreshScheduled.TryRemove(gid, out _);
                }
                await FetchCommunityAsync(gid).ConfigureAwait(false);
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>The single community fetch - deduped per gid, bounded timeout,
    /// failure latch so a dead server can't spin the request loop.</summary>
    private async Task FetchCommunityAsync(string gid)
    {
        if (!_communityLoadingMap.TryAdd(gid, 0))
        {
            return;
        }
        _communityFailedAt.TryRemove(gid, out _);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(CommunityFetchTimeout);
            var fresh = await _apiController
                .GroupGetCommunity(new GroupDto(new GroupData(gid)), cts.Token)
                .ConfigureAwait(false);
            if (fresh != null)
            {
                PutCommunity(gid, fresh);
                _logger.LogDebug("Community {GID} loaded · {ms}ms · gallery={G} events={E} albums={A}",
                    gid, sw.ElapsedMilliseconds, fresh.Gallery?.Count ?? 0,
                    fresh.Events?.Count ?? 0, fresh.Albums?.Count ?? 0);
            }
            else
            {
                _communityNoPage[gid] = 0;
            }
        }
        catch (Exception ex)
        {
            _communityFailedAt[gid] = DateTimeOffset.UtcNow;
            _logger.LogDebug(ex, "Failed to load syncshell community for {GID} · {ms}ms",
                gid, sw.ElapsedMilliseconds);
        }
        finally
        {
            _communityLoadingMap.TryRemove(gid, out _);
        }
    }

    private void EnsureCommunityLoaded(string gid)
    {
        _communityCacheGid = gid;
        if (_communityMap.TryGetValue(gid, out var entry))
        {
            entry.LastUsedTicks = Environment.TickCount64;
            return;
        }
        if (_communityNoPage.ContainsKey(gid) || _communityLoadingMap.ContainsKey(gid))
        {
            return;
        }
        if (_communityFailedAt.TryGetValue(gid, out var failedAt)
            && DateTimeOffset.UtcNow - failedAt < CommunityFailureBackoff)
        {
            return;
        }
        _ = FetchCommunityAsync(gid);
    }

    /// <summary>Author-name deep-link shared by feed rows, chronicle headers
    /// and comment authors - self keeps the own profile page, pairs the
    /// friend page, everyone else the public profile surface.</summary>
    private void NavigateToAuthor(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        if (string.Equals(uid, _apiController.UID, StringComparison.Ordinal))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Profile, null, null));
            return;
        }
        if (FindPairByUid(uid) is { } authorPair)
        {
            NavigateTo(new HubLocation(SpheneHubPage.Friends, null, authorPair));
            return;
        }
        // Always land on the public page - it resolves to the explicit
        // "unavailable" state for unshared profiles and old servers alike,
        // instead of the click silently doing nothing.
        NavigateTo(new HubLocation(SpheneHubPage.PublicProfile, null, null,
            PublicUid: uid));
    }

    /// <summary>Decodes one public-profile image field; the caller caches
    /// bytes per (uid, payload-hash) so a changed or redacted payload
    /// re-decodes instead of serving the warm previous image.</summary>
    private static byte[] PublicMedia(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return [];
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { return []; }
    }

    /// <summary>Stable content key for a base64 payload - an ordinal hash
    /// suffix keeps texture keys honest when a same-uid image is replaced.</summary>
    private static int ContentKey(string? base64)
        => base64 == null ? 0 : StringComparer.Ordinal.GetHashCode(base64);

    /// <summary>Fixed chrome of the public profile page - the same hero
    /// language as friend/own profiles (banner, avatar, name, bio, tags)
    /// plus the unilateral follow action. Unresolved entries skeleton; a
    /// profile revoked since the click keeps a quiet placeholder hero.</summary>
    private void DrawPublicProfileChrome(Vector4 accent,
        SpheneUI.Profile.HeroHost heroHost)
    {
        var theme = SpheneTheme.Current;
        var (resolved, rawDto) = _publicUid != null
            ? _publicProfiles.EnsureProfile(_publicUid)
            : (true, null);
        if (!resolved)
        {
            SpheneUI.Profile.HeroSkeleton(accent, host: heroHost);
            return;
        }
        var dto = _publicProfiles.RedactIfNsfw(rawDto);
        var name = dto?.User.PreferredName ?? "Profile unavailable";
        // The transient SpheneProfileData only feeds the hero's field reads
        // (bio + tags, both already masked server-side) - status stays empty
        // by contract and it never enters the pair profile caches.
        var profile = new SpheneProfileData(false, dto?.IsNsfw ?? false,
            dto?.ProfilePictureBase64 ?? string.Empty, string.Empty,
            dto?.Description ?? string.Empty, string.Empty,
            dto?.Tags ?? string.Empty, dto?.BannerImageBase64 ?? string.Empty);
        // Content-keyed caches: a changed payload under the SAME uid re-decodes
        // and re-keys the texture - a replaced/revoked image can never linger.
        var picB64 = dto?.ProfilePictureBase64;
        if (!string.Equals(picB64, _publicAvatarB64, StringComparison.Ordinal))
        {
            _publicAvatarB64 = picB64;
            _publicAvatarBytes = PublicMedia(picB64);
        }
        var bannerB64 = dto?.BannerImageBase64;
        if (!string.Equals(bannerB64, _publicBannerB64, StringComparison.Ordinal))
        {
            _publicBannerB64 = bannerB64;
            _publicBannerBytes = PublicMedia(bannerB64);
        }
        var avatar = _publicAvatarBytes.Length == 0 || _publicUid == null ? null
            : SpheneUI.Media.Texture(
                $"public-avatar:{_publicUid}:{ContentKey(_publicAvatarB64)}",
                () => _publicAvatarBytes);
        var banner = _publicBannerBytes.Length == 0 || _publicUid == null ? null
            : SpheneUI.Media.Texture(
                $"public-banner:{_publicUid}:{ContentKey(_publicBannerB64)}",
                () => _publicBannerBytes);
        var actions = dto != null
            ? new List<SpheneUI.Profile.HeroAction>
            {
                new(dto.Following ? FontAwesomeIcon.UserCheck : FontAwesomeIcon.UserPlus,
                    dto.Following ? "Following" : "Follow",
                    dto.Following
                        ? "You follow this profile - click to unfollow"
                        : "Follow this profile's public Chronicles - no pair needed",
                    () => TogglePublicFollow(dto)),
            }
            : null;
        SpheneUI.Profile.Hero(name, string.Empty, profile, avatar, banner,
            profile.ProfileTheme.Value, accent, theme.AccentBlue,
            isNsfw: dto?.IsNsfw ?? false, actions: actions, host: heroHost);
    }

    /// <summary>Optimistic follow/unfollow with a busy guard - a rejected
    /// toggle rolls back inside <see cref="PublicProfileService"/> and the
    /// user hears why.</summary>
    private void TogglePublicFollow(PublicProfileDto dto)
    {
        var uid = dto.User.UID;
        if (_publicFollowBusy != null) return;
        _publicFollowBusy = uid;
        var wantFollow = !dto.Following;
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await _publicProfiles.SetFollowAsync(uid, wantFollow)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    Mediator.Publish(new NotificationMessage(
                        wantFollow ? "Couldn't follow" : "Couldn't unfollow",
                        wantFollow
                            ? "This profile isn't open to new follows right now."
                            : "The unfollow didn't reach the server - try again.",
                        SpheneConfiguration.Models.NotificationType.Warning,
                        TimeSpan.FromSeconds(5)));
                }
            }
            finally { _publicFollowBusy = null; }
        });
    }

    /// <summary>Scrollable body of the public profile page - the shared
    /// hero's content zone: public chronicles as rows, or the open reader
    /// for one. Revoked profiles land on the neutral unavailable card.</summary>
    private void DrawPublicProfilePage(Vector4 accent)
    {
        if (_publicUid == null)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Question,
                "No profile selected",
                "Open a shared profile from People search or an author name.");
            return;
        }
        var uid = _publicUid;
        var (resolved, rawDto) = _publicProfiles.EnsureProfile(uid);
        if (!resolved)
        {
            SpheneUI.Layout.Surface(() =>
            {
                SpheneUI.Fx.SkeletonLine();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Fx.SkeletonLine(0.6f);
            }, id: "public-profile-loading");
            return;
        }
        var dto = _publicProfiles.RedactIfNsfw(rawDto);
        if (dto == null)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserLock,
                "Profile not public",
                "This profile isn't shared in the Community Hub - it may be private, removed, or not supported by this server.",
                "Back", NavigateBack, FontAwesomeIcon.ArrowLeft, accent);
            return;
        }
        var displayName = dto.User.PreferredName;
        var avatarWrap = _publicAvatarBytes.Length == 0 ? null
            : SpheneUI.Media.Texture(
                $"public-avatar:{uid}:{ContentKey(_publicAvatarB64)}",
                () => _publicAvatarBytes);
        var chronicles = dto.Chronicles;
        var openPost = _openChronicle is { OwnerUid: var openOwner, Id: var openId }
            && string.Equals(openOwner, uid, StringComparison.Ordinal)
            ? chronicles.FirstOrDefault(b => b.Id == openId) : null;
        if (openPost != null)
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "Back"))
            {
                _openChronicle = null;
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            var post = openPost;
            var ops = CommunityComposers.BuildBlogCommentOps(uid, post.Id,
                () =>
                {
                    _publicProfiles.InvalidateProfile(uid);
                    return Task.CompletedTask;
                },
                authorUid => CommunityNameFor(authorUid, null, null), CommentProfile);
            SpheneUI.Layout.Surface(() =>
            {
                Sphene.UI.Chronicles.ChronicleReaderView.Draw(post, uid,
                    displayName, avatarWrap, accent, _blogComments, ops,
                    excerptLines: 8,
                    isExpanded: id => _expandedChronicles.Contains(id),
                    toggleExpanded: id => ToggleChronicleExpanded(id),
                    onAuthorClick: NavigateToAuthor);
            }, id: $"public-chronicle-{post.Id}", border: true, accentColor: accent);
            return;
        }
        if (_openChronicle is { OwnerUid: var staleOwner }
            && string.Equals(staleOwner, uid, StringComparison.Ordinal))
        {
            _openChronicle = null;
            _chronicleNotice = ChronicleMissingNotice;
        }
        if (_chronicleNotice != null)
        {
            SpheneUI.Text.ColoredWrapped(_chronicleNotice, SpheneUI.Palette.Warning);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
        if (chronicles.Count == 0)
        {
            SpheneUI.Layout.Surface(() =>
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.BookOpen,
                    "No public Chronicles",
                    "Nothing published publicly yet - public Chronicles will land here.");
            }, id: "public-chronicles-empty");
            return;
        }
        SyncshellCommunityView.RailHeader("Chronicles", FontAwesomeIcon.FeatherAlt,
            chronicles.Count, accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        for (var i = 0; i < chronicles.Count; i++)
        {
            if (i > 0)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
            var captured = chronicles[i];
            var item = new SyncshellCommunityFeedItemDto(
                string.Empty, null, null, SyncshellFeedItemKind.Blog,
                captured.Id,
                string.IsNullOrWhiteSpace(captured.Title)
                    ? "Untitled Chronicle" : captured.Title,
                null, captured.UpdatedAt,
                ActorUid: uid, ActorDisplayName: displayName,
                ThumbnailMediaHash: captured.CoverMediaHash);
            if (FeedItemRow.Draw($"public-chron-{uid}-{captured.Id}", item,
                    "pub:", accent, ChronicleRowMeta(captured),
                    onActorClick: NavigateToAuthor))
            {
                NavigateTo(new HubLocation(SpheneHubPage.PublicProfile, null, null,
                    PublicUid: uid, ChroniclePostId: captured.Id));
            }
        }
    }

    /// <summary>Community → Following - public chronicles from followed
    /// profiles only; its own store and cursor, never the All feed's.</summary>
    private void DrawFollowingFeed(Vector4 accent)
    {
        if (!_publicProfiles.Enabled)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Rss,
                "Following isn't available",
                "This server doesn't support public profiles yet - update the server to follow people.");
            return;
        }
        _publicProfiles.EnsureFollowingRequested();
        var items = _publicProfiles.FollowingItems;
        SpheneUI.Text.SectionHeader("Following",
            icon: FontAwesomeIcon.Rss, accent: accent);
        SpheneUI.Text.MutedWrapped(
            "Public Chronicles from profiles you follow - following is unilateral and grants no pair rights.");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        if (_publicProfiles.FollowingLoading && items.Count == 0)
        {
            for (var i = 0; i < 3; i++)
            {
                SpheneUI.Fx.SkeletonLine(0.85f - i * 0.12f);
            }
        }
        else if (items.Count == 0 && _publicProfiles.FollowingRefreshFailed)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.ExclamationTriangle,
                "Couldn't load the feed",
                "The Following feed could not be reached - try again now.",
                "Retry", () => _ = _publicProfiles.RefreshFollowing(),
                FontAwesomeIcon.SyncAlt, accent);
        }
        else if (items.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Rss,
                "Not following anyone yet",
                "Public Chronicles from profiles you follow land here - find people in the People tab.",
                "Find people",
                () => NavigateTo(new HubLocation(SpheneHubPage.Community, null, null, 7)),
                FontAwesomeIcon.Users, accent);
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                // NSFW-author rows redact title/snippet/thumb at read time -
                // a live settings flip takes effect the same frame.
                var item = _publicProfiles.RedactIfNsfw(items[i]);
                if (FeedItemRow.Draw($"follow-{item.Kind}-{item.Id}-{item.ActorUid}",
                        item, "hub:", fallbackAccent: accent,
                        onActorClick: NavigateToAuthor))
                {
                    NavigateFeedItem(item);
                }
                if (i + 1 < items.Count)
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
            }
            if (_publicProfiles.FollowingPageFailed)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Text.Colored("Couldn't load more.", SpheneUI.Palette.Warning);
            }
            if (_publicProfiles.FollowingHasMore)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                using (ImRaii.Disabled(_publicProfiles.FollowingLoading))
                {
                    if (SpheneUI.Controls.Button(
                            _publicProfiles.FollowingPageFailed
                                ? "Retry##followmore" : "Load more##followmore"))
                    {
                        _ = _publicProfiles.LoadMoreFollowingAsync();
                    }
                }
            }
        }
    }

    /// <summary>Community → People - the public profile search. Results
    /// stay slim (identity, avatar, NSFW flag, follow state); a row click
    /// opens the public profile page, a follow toggle flips in place.</summary>
    private void DrawPeopleSearch(Vector4 accent)
    {
        if (!_publicProfiles.Enabled)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Users,
                "People search isn't available",
                "This server doesn't support public profiles yet - update the server to search people.");
            return;
        }
        SpheneUI.Text.SectionHeader("People",
            icon: FontAwesomeIcon.Users, accent: accent);
        SpheneUI.Text.MutedWrapped(
            "Search profiles shared in the Community Hub by display name.");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        using (ImRaii.PushId("people-search"))
        {
            // EnterReturnsTrue: changed fires once on submit, not per edit.
            var submitted = SpheneUI.Controls.InputTextWithHint("##query",
                "Search public profiles by name…", ref _publicSearchInput, 64,
                ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            // A changed query may supersede a still-flying request - only a
            // repeat of the committed query waits for the current flight.
            var canSearch = !string.IsNullOrWhiteSpace(_publicSearchInput)
                && (!_publicProfiles.SearchLoading
                    || !string.Equals(_publicSearchInput.Trim(),
                        _publicSearchCommitted, StringComparison.Ordinal));
            using (ImRaii.Disabled(!canSearch))
            {
                if (SpheneUI.Controls.Button("Search")) submitted = true;
            }
            if (submitted && canSearch)
            {
                _publicSearchCommitted = _publicSearchInput.Trim();
                _publicProfiles.Search(_publicSearchCommitted);
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        var results = _publicProfiles.SearchResults;
        if (_publicSearchCommitted == null)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Users,
                "Find people",
                "Profiles shared in the Community Hub show up here by display name.");
            return;
        }
        // A transport failure is its own state - it must not read as
        // "no matches"; visible page rows stay and retry reloads page one.
        if (_publicProfiles.SearchFailed)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.ExclamationTriangle,
                "Search unavailable",
                results.Count == 0
                    ? "The people search could not be reached - try again now."
                    : "The next page could not be loaded - try again now.",
                "Retry", () => _publicProfiles.Search(_publicSearchCommitted),
                FontAwesomeIcon.SyncAlt, accent);
            if (results.Count == 0) return;
        }
        else if (results.Count == 0 && _publicProfiles.SearchLoading)
        {
            for (var i = 0; i < 3; i++)
            {
                SpheneUI.Fx.SkeletonLine(0.85f - i * 0.12f);
            }
            return;
        }
        else if (results.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserSlash,
                "No shared profiles found",
                "No public profile matches that name - only profiles that opted into the Community Hub appear.");
            return;
        }
        // Bounded revalidation while the tab sits open - a missed push or
        // a revocation between pushes is caught inside the window.
        _publicProfiles.EnsureSearchFresh();
        for (var i = 0; i < results.Count; i++)
        {
            var hit = _publicProfiles.RedactIfNsfw(results[i]);
            DrawPeopleSearchRow(hit, accent);
            if (i + 1 < results.Count)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
        }
        if (_publicProfiles.SearchHasMore)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            using (ImRaii.Disabled(_publicProfiles.SearchLoading))
            {
                if (SpheneUI.Controls.Button("Load more",
                        new Vector2(-1, 0)))
                {
                    _publicProfiles.LoadMoreSearch();
                }
            }
        }
        else if (_publicProfiles.SearchLoading && results.Count > 0)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Fx.SkeletonLine(0.5f);
        }
    }

    /// <summary>One search-result row - avatar, name, follow state. The row
    /// opens the profile; the follow button is its own hit target.</summary>
    private void DrawPeopleSearchRow(PublicProfileSummaryDto hit, Vector4 accent)
    {
        SpheneUI.Layout.Surface(() =>
        {
            var rowH = 28f * SpheneUI.Scale;
            if (hit.AvatarMediaHash is { } avatarHash && _communityMedia != null)
            {
                var hash = avatarHash;
                // TryGetBytes is the non-blocking path - a miss kicks the
                // background download and the row repaints when bytes land.
                var wrap = SpheneUI.Media.Texture(
                    $"pub-avatar:{hit.User.UID}:{hash}",
                    () => _communityMedia.TryGetBytes(hash) ?? []);
                SpheneUI.Media.Avatar(wrap, rowH);
                if (ImGui.IsItemClicked())
                {
                    NavigateTo(new HubLocation(SpheneHubPage.PublicProfile,
                        null, null, PublicUid: hit.User.UID));
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            }
            var clicked = SpheneUI.Text.Link(hit.User.PreferredName);
            SpheneUI.AttachTooltip("View profile");
            if (hit.IsNsfw)
            {
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                SpheneUI.Layout.Badge("NSFW", SpheneUI.Palette.Warning);
            }
            var rightW = 96f * SpheneUI.Scale;
            var avail = ImGui.GetContentRegionAvail().X;
            if (avail > rightW + 20f * SpheneUI.Scale)
            {
                ImGui.SameLine(ImGui.GetCursorPosX() + avail - rightW);
                var label = hit.Following ? "Following" : "Follow";
                using (ImRaii.Disabled(_publicFollowBusy != null))
                {
                    if (SpheneUI.Controls.Button($"{label}##pf-{hit.User.UID}",
                            new Vector2(rightW, 0)))
                    {
                        TogglePublicFollowSummary(hit);
                    }
                }
            }
            if (clicked)
            {
                NavigateTo(new HubLocation(SpheneHubPage.PublicProfile, null, null,
                    PublicUid: hit.User.UID));
            }
        }, id: $"person-{hit.User.UID}", border: true, accentColor: accent);
    }

    /// <summary>Follow toggle straight from a search row - same path as the
    /// profile hero, optimistic inside <see cref="PublicProfileService"/>.</summary>
    private void TogglePublicFollowSummary(PublicProfileSummaryDto hit)
    {
        if (_publicFollowBusy != null) return;
        _publicFollowBusy = hit.User.UID;
        var wantFollow = !hit.Following;
        _ = Task.Run(async () =>
        {
            try
            {
                await _publicProfiles.SetFollowAsync(hit.User.UID, wantFollow)
                    .ConfigureAwait(false);
            }
            finally { _publicFollowBusy = null; }
        });
    }

    }
