using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data;
using Sphene.API.Dto.Group;
using System.Numerics;
using System.Runtime.InteropServices;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Dalamud.Utility;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.Services;

namespace Sphene.UI.Syncshell;

/// <summary>
/// Renders a syncshell community page: banner/avatar identity header followed by
/// the owner-configured sections (about, events, news, rules, links, gallery,
/// staff). Shared by the community window, the hub detail page and the admin
/// live preview so every surface presents the same layout.
/// </summary>
internal static partial class SyncshellCommunityView
{
    /// <summary>Media-hash byte source - set once by the hosting window; the
    /// static view keeps base64 as the universal fallback so hash-carrying
    /// rows render identically to old clients.</summary>
    internal static Sphene.Services.CommunityMediaClientService? MediaClient { get; set; }

    /// <summary>Shared markdown composers - instance-per-surface so preview
    /// toggles and pending uploads don't leak between editors.</summary>
    private static readonly UI.Components.MarkdownComposer NewsBodyComposer = new("news");

    /// <summary>Texture data source: file-server bytes when a media hash is
    /// present and already cached, else the legacy base64 payload.</summary>
    internal static Func<byte[]?> ResolveBytes(string? mediaHash, string? base64) => () =>
        mediaHash != null && MediaClient?.TryGetBytes(mediaHash) is { Length: > 0 } bytes
            ? bytes
            : !string.IsNullOrEmpty(base64) ? Convert.FromBase64String(base64) : null;

    /// <summary>Texture-cache key segment versioned by content - hash media
    /// change the key by definition; inline base64 payloads get a cheap
    /// fingerprint so a replaced image can't leave the old texture latched
    /// under the same key. Versioned keys make manual <c>Media.Evict</c>
    /// calls unnecessary.</summary>
    internal static string MediaVersion(string? mediaHash, string? base64)
        => mediaHash ?? $"b64{base64?.Length ?? 0}x{(base64 == null ? 0 : StringComparer.Ordinal.GetHashCode(base64)):x8}";

    /// <summary>DisplayName → Alias → null; a raw uid is never a display
    /// name - surfaces without an identity row render "Unknown user".</summary>
    internal static string? HumanName(UserData? u)
        => u == null ? null
            : !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName
            : !string.IsNullOrWhiteSpace(u.Alias) ? u.Alias : null;

    /// <summary>Resolves a roster uid to a display name via the group's and
    /// community's server-provided identity maps.</summary>
    private static string? IdentityName(SyncshellCommunityDto community,
        GroupFullInfoDto? group, string uid)
    {
        if (group?.MemberIdentities != null
            && group.MemberIdentities.TryGetValue(uid, out var g))
        {
            return HumanName(g);
        }
        return HumanName(community.MemberIdentities?
            .FirstOrDefault(u => string.Equals(u.UID, uid, StringComparison.Ordinal)));
    }

    private static readonly (SyncshellCommunitySection Section, string Label, FontAwesomeIcon Icon)[] SectionOrder =
    [
        (SyncshellCommunitySection.Events, "Events", FontAwesomeIcon.Calendar),
        (SyncshellCommunitySection.News, "News", FontAwesomeIcon.Bullhorn),
        (SyncshellCommunitySection.About, "About", FontAwesomeIcon.Info),
        (SyncshellCommunitySection.Gallery, "Gallery", FontAwesomeIcon.Images),
        (SyncshellCommunitySection.Staff, "Staff", FontAwesomeIcon.UserShield),
        (SyncshellCommunitySection.Rules, "Rules", FontAwesomeIcon.ClipboardList),
        (SyncshellCommunitySection.Links, "Links", FontAwesomeIcon.Link),
    ];

    /// <summary>
    /// Per-surface navigation state for the community page: which tab is open
    /// (null = the Overview digest), the eased entry animation, the smoothed
    /// tab-bar underline position, and the gallery album drill-in.
    /// Each caller owns an instance so embedded, standalone and preview
    /// surfaces navigate independently.
    /// </summary>
    internal sealed class CommunityPageState
    {
        /// <summary>The open section page · null shows the Overview digest.</summary>
        private SyncshellCommunitySection? _tab;

        /// <summary>Active section - switching always tears down the embedded
        /// viewer, an open lightbox belongs to the surface that opened it.</summary>
        public SyncshellCommunitySection? Tab
        {
            get => _tab;
            set
            {
                if (_tab == value) return;
                _tab = value;
                CloseViewer();
            }
        }

        /// <summary>Closes the embedded image viewer - explicit navigation
        /// (page/tab changes, same-page reselects) calls this so a stale
        /// lightbox can never outlive its surface.</summary>
        public void CloseViewer()
        {
            ViewerImages = null;
            ViewerIndex = 0;
            ViewerOpenT = 1f;
            ViewerEditOpen = false;
            ViewerEditImgId = -1;
            ViewerEditError = null;
        }
        /// <summary>0..1 eased entry animation progress of the current tab.</summary>
        public float SwitchT = 1f;
        /// <summary>Scratch list the shared TabBar renders from · rebuilt per frame, no per-frame alloc.</summary>
        public readonly List<SpheneUI.Controls.TabItem> TabItems = [];
        /// <summary>Gallery drill-in · the open album, null shows the album rail.</summary>
        public long? AlbumId;
        /// <summary>Event drill-in · the open event detail page, null shows the tab content.</summary>
        public long? EventId;
        /// <summary>Deep-link focus target on list surfaces (news post id) - feed cards and
        /// notifications land on the tab with this item scrolled into view.</summary>
        public long? FocusItemId;
        /// <summary>Lightbox state · the list the viewer scrolls through, null when closed.</summary>
        public List<SyncshellGalleryImageDto>? ViewerImages;
        public int ViewerIndex;
        /// <summary>Lightbox edit drawer · open flag plus per-photo buffers seeded
        /// whenever <see cref="ViewerEditImgId"/> no longer matches the shown image.</summary>
        public bool ViewerEditOpen;
        public long ViewerEditImgId = -1;
        public string ViewerEditCaption = string.Empty;
        public long? ViewerEditAlbumId;
        public int ViewerEditAudience;
        public readonly HashSet<string> ViewerEditAllowed = new(StringComparer.Ordinal);
        public bool ViewerDeleteArmed;
        /// <summary>Photo-edit save in flight - blocks resubmit until the write resolves.</summary>
        public bool ViewerEditSaving;
        /// <summary>Last save failure shown inline; kept while the editor stays open.</summary>
        public string? ViewerEditError;
        /// <summary>0..1 eased lightbox-open progress · resets on every open.</summary>
        public float ViewerOpenT = 1f;
        /// <summary>Inline post composer · the `+`/pen opens it in place on the News tab.</summary>
        public readonly CommunityPostComposer NewsComposer = new();
        /// <summary>Inline event composer · opened from the Upcoming `+` or the detail pen.</summary>
        public readonly CommunityEventComposer EventComposer = new();
        /// <summary>Event Planner workspace filters + occurrence cache.</summary>
        public readonly EventPlannerView.EventPlannerState Planner = new();
        /// <summary>Events tab's calendar/list mode · persisted on the page state.</summary>
        public bool EventsCalendarMode;
        /// <summary>Events tab free-text filter for the list mode.</summary>
        public string EventFilter = string.Empty;
        /// <summary>Month-grid calendar component · fetches expanded occurrences lazily.</summary>
        public readonly UI.Components.EventsCalendarView EventsCalendar = new();
        /// <summary>Shared comment-thread widget - owns per-target paging state.</summary>
        public readonly UI.Components.CommentThreadView Comments = new();
        /// <summary>Loaded participant summary for the open event detail · keyed by <see cref="ParticipantsFor"/>.</summary>
        public EventParticipantsDto? EventParticipants;
        /// <summary>Event id <see cref="EventParticipants"/> belongs to.</summary>
        public long ParticipantsFor;
        /// <summary>True while a participant load is in flight for the open event.</summary>
        public bool ParticipantsLoading;
        /// <summary>Manage drill-in · replaces the tab content with a structural editor page.</summary>
        public CommunitySubpage Subpage;
        /// <summary>Renders the page as a visitor sees it · local mode only,
        /// suppresses every manage affordance without touching server state.</summary>
        public bool PreviewAsVisitor;
        /// <summary>Wheel delta captured on the pinned overlay child · the
        /// content child consumes it on its next pass (SetScrollY is
        /// current-window only).</summary>
        public float ScrollNudge;
        // Inline identity/section editors · buffers live on the page state so
        // they survive the per-frame rebuild of the manage contract.
        public string NameBuffer = string.Empty;
        public bool EditingName;
        public bool NameFocusPending;
        public string TaglineBuffer = string.Empty;
        public bool EditingTagline;
        public bool TaglineFocusPending;
        public string AboutBuffer = string.Empty;
        public bool EditingAbout;
        public bool AboutFocusPending;
        public string RulesBuffer = string.Empty;
        public bool EditingRules;
        public bool RulesFocusPending;
        /// <summary>Inline links editor · buffered rows while the pen is open.</summary>
        public bool EditingLinks;
        public readonly List<CommunityLinkDraft> LinksBuffer = [];
        /// <summary>Gallery album-manage expand under the tab header.</summary>
        public bool ManagingAlbums;
        /// <summary>Optimistic staff-role sets per uid while an assignment save is in
        /// flight - cleared whenever the assignments list instance changes (refresh).</summary>
        public readonly Dictionary<string, List<long>> PendingStaffRoles = new(StringComparer.Ordinal);
        /// <summary>The <c>StaffAssignments</c> list the pending overrides were computed from.</summary>
        public object? StaffAssignmentSource;
        /// <summary>Staged album edits - drafts survive refresh so typing isn't
        /// reverted; cleared when the album saves or the edit is discarded.</summary>
        public readonly ConcurrentDictionary<long, AlbumEditDraft> AlbumDrafts = new();
        /// <summary>Transient success marker - album id + tick of the last
        /// confirmed save; the manager shows a brief "Saved" flash.</summary>
        public long AlbumSavedId = -1;
        public long AlbumSavedAtTicks;
        /// <summary>Album drill-in's photo picker · assign/remove existing photos.</summary>
        public bool PickingAlbumPhotos;
        public string NewAlbumName = string.Empty;
        /// <summary>Staff subpage's new-job input buffer.</summary>
        public string NewRoleName = string.Empty;
        /// <summary>Staff subpage's selected role in the roles column · 0 picks the top-ranked role.</summary>
        public long SelectedStaffRoleId;
        /// <summary>Role icon picker flyout's search text + category.</summary>
        public string RoleIconFilter = string.Empty;
        public string RoleIconCategory = "All";
        /// <summary>Settings subpage's custom-section rows · seeded on first render.</summary>
        public List<CommunitySectionDraft>? SectionsBuffer;

        // Travel state for the open event detail · keyed so a stale async
        // response can't update a different event/destination.
        /// <summary>Event id the cached <see cref="TravelStatus"/> belongs to.</summary>
        public long TravelFor = -1;
        /// <summary>Hash of the destination the status was requested for.</summary>
        public int TravelKey;
        /// <summary>Last evaluated status · null until the first probe lands.</summary>
        public LifestreamTravelStatus? TravelStatus;
        /// <summary>True while a state probe or travel request is in flight.</summary>
        public bool TravelLoading;
        /// <summary>Bumped by the host on <see cref="Services.Mediator.LifestreamTravelStateChangedMessage"/> -
        /// the view re-probes when it advances.</summary>
        public int TravelVersion;

        /// <summary>Resolved bound-area destinations for the preview travel
        /// CTA · null until the async load lands.</summary>
        public List<EventDestinationDto>? AreaDestinations;
        /// <summary>In-flight bound-area destination load.</summary>
        public bool AreaDestLoading;
        /// <summary>In-flight travel request from the preview CTA.</summary>
        public bool AreaTravelBusy;
        /// <summary>The <see cref="TravelVersion"/> the cached status was evaluated at.</summary>
        public int TravelSeenVersion;

        // Community-location settings editor (page settings subpage) ·
        // reuses the event composer's destination fields; seeded from the
        // DTO and re-seeded whenever the server pushes a fresh community.
        /// <summary>Destination field bag for the location settings editor.</summary>
        public readonly CommunityEventComposer LocationEditor = new();
        /// <summary>The DTO instance the location editor was seeded from -
        /// a fresh fetch re-seeds (ReferenceEquals check).</summary>
        public SyncshellCommunityDto? LocationEditorSeed;
        /// <summary>Publish checkbox state - never auto-enabled.</summary>
        public bool LocationPublish;
        /// <summary>True while a location save is in flight.</summary>
        public bool LocationSaving;
    }

    /// <summary>Manage drill-in pages · same window, deeper than a tab.</summary>
    internal enum CommunitySubpage { None, Settings }

    /// <summary>Editable link row · committed to <c>SyncshellCommunityLinkDto</c> on Done.</summary>
    internal sealed class CommunityLinkDraft
    {
        public string Label = string.Empty;
        public string Url = string.Empty;
        /// <summary>Brand preset key · see <c>LinkBrandPresets</c>; null = custom.</summary>
        public string? Icon;
    }

    /// <summary>Editable custom-section row · committed to <c>SyncshellProfileSectionDto</c> on Done.</summary>
    internal sealed class CommunitySectionDraft
    {
        public string Title = string.Empty;
        public string Body = string.Empty;
    }

    /// <summary>One member entry for the staff subpage's assignment picker.</summary>
    internal sealed record CommunityStaffMember(string Uid, string DisplayName, bool IsModerator);

    /// <summary>Local album-edit draft - changes stage here, not on the wire.
    /// Saved once on Save; a refresh mid-edit must not re-seed over it.</summary>
    internal sealed class AlbumEditDraft
    {
        public string Name = string.Empty;
        public long? LinkedEventId;
        public long? CoverImageId;
        public int Audience;
        public List<string> AllowedUids = [];
        public bool Saving;
        public string? Error;

        public static AlbumEditDraft From(SyncshellGalleryAlbumDto a) => new()
        {
            Name = a.Name,
            LinkedEventId = a.LinkedEventId,
            CoverImageId = a.CoverImageId,
            Audience = (int)a.Audience,
            AllowedUids = a.AllowedUids?.ToList() ?? []
        };

        public bool Differs(SyncshellGalleryAlbumDto a)
            => !string.Equals(Name.Trim(), a.Name, StringComparison.Ordinal)
                || LinkedEventId != a.LinkedEventId
                || CoverImageId != a.CoverImageId
                || Audience != (int)a.Audience
                || !AllowedUids.SequenceEqual(a.AllowedUids ?? [], StringComparer.Ordinal);

        public SyncshellGalleryAlbumDto ApplyTo(SyncshellGalleryAlbumDto a) => a with
        {
            Name = Name.Trim(),
            LinkedEventId = LinkedEventId,
            CoverImageId = CoverImageId,
            Audience = (SyncshellGalleryAudience)Audience,
            AllowedUids = AllowedUids
        };
    }

    /// <summary>
    /// Host-bound manage operations · the page's structural edits. The view
    /// calls these; the host maps them onto API calls and refreshes its
    /// cached community. Built per surface via <c>CommunityComposers</c>.
    /// </summary>
    internal sealed class CommunityManageOps
    {
        /// <summary>Static-parts patch (links, sections, visibility, accent, widgets, listing).</summary>
        public Action<Func<SyncshellCommunityDto, SyncshellCommunityDto>>? Patch;
        public Action<string>? CreateStaffRole;
        public Action<SyncshellStaffRoleDto>? SaveStaffRole;
        public Action<SyncshellStaffRoleDto>? DeleteStaffRole;
        public Action<SyncshellStaffRoleDto, int>? MoveStaffRole;
        /// <summary>Replaces a member's complete role set (empty clears all).</summary>
        public Action<string, IReadOnlyList<long>>? SetStaffAssignment;
        public Func<string, Task<bool>>? CreateAlbum;
        public Func<SyncshellGalleryAlbumDto, Task<bool>>? SaveAlbum;
        public Func<SyncshellGalleryAlbumDto, Task<bool>>? DeleteAlbum;
        /// <summary>Moves a gallery image into an album (null unassigns) · metadata-only update.</summary>
        public Action<SyncshellGalleryImageDto, long?>? AssignImageAlbum;
        /// <summary>Commits a photo's metadata (caption, album, audience, allow-list)  ·
        /// the view passes the DTO already carrying the edited values. Returns
        /// whether the write landed so the editor can close or keep the draft.</summary>
        public Func<SyncshellGalleryImageDto, Task<bool>>? SaveImage;
        /// <summary>Deletes a photo · the view removes it from the open viewer list.</summary>
        public Action<SyncshellGalleryImageDto>? DeleteImage;
        /// <summary>Member list for the assignment picker · null without group context.</summary>
        public Func<IReadOnlyList<CommunityStaffMember>>? GetStaffMembers;
    }

    /// <summary>Persistent inline post composer state · buffers survive across frames.</summary>
    internal sealed class CommunityPostComposer
    {
        public bool Open;
        public long EditId;
        public string Title = string.Empty;
        public string Body = string.Empty;
        public bool Pinned;

        public void OpenNew()
        {
            Open = true;
            EditId = 0;
            Title = Body = string.Empty;
            Pinned = false;
        }

        public void OpenEdit(SyncshellNewsPostDto post)
        {
            Open = true;
            EditId = post.Id;
            Title = post.Title;
            Body = post.Body;
            Pinned = post.IsPinned;
        }

        public void Close() => Open = false;
    }


    /// <summary>
    /// Member services the staff rows can reach · surfaces with access to the
    /// pair/profile stores pass delegates; surfaces without render UID-only
    /// rows without the profile chrome or the open-profile affordance.
    /// </summary>
    internal sealed record CommunityMemberHooks(
        Func<string, Sphene.Services.SpheneProfileData?>? GetProfile = null,
        Func<string, string?>? GetDisplayName = null,
        Func<string, bool>? CanOpenProfile = null,
        Action<string>? OpenProfile = null);

    /// <summary>
    /// Manage contract for <see cref="DrawContent"/> · no global edit mode.
    /// Content affordances (+ New post, section pens, per-item controls) render
    /// whenever their hook is set, so managing is always contextual. Hosts with
    /// <see cref="CanEditIdentity"/> additionally get pen overlays on banner/
    /// avatar plus per-element inputs for tagline/about, and managers see
    /// hidden section tabs dimmed. All buffers are owned by the caller;
    /// commits fire the matching callback.
    /// </summary>
    internal sealed class SyncshellCommunityEdit
    {
        /// <summary>Banner/avatar pens and per-element identity inputs.</summary>
        public bool CanEditIdentity;
        public Action? OnBannerClicked;
        public Action? OnAvatarClicked;
        /// <summary>Commits the shell's display alias when the hero name input closes.</summary>
        public Action<string>? OnNameCommitted;
        /// <summary>Commits the tagline when the hero's inline input closes.</summary>
        public Action<string>? OnTaglineCommitted;
        /// <summary>Commits the about text when its inline editor closes (check button).</summary>
        public Action<string>? OnAboutCommitted;
        /// <summary>Commits the rules text when its inline editor closes.</summary>
        public Action<string>? OnRulesCommitted;

        // Manage permissions · which affordances the page renders at all.
        public bool CanManagePage;
        public bool CanManageNews;
        public bool CanManageEvents;
        public bool CanManageGallery;
        public bool CanManageStaff;
        /// <summary>Member submission affordance - set when the viewer may
        /// submit content for the review queue without manage rights.</summary>
        public bool CanSubmit;
        public bool Listed;

        /// <summary>
        /// Host-bound structural operations · when set, manage affordances act
        /// in place: the hero gear opens the Settings subpage, the staff pen the
        /// Staff subpage, the links pen the inline editor, the gallery folder the
        /// album expand. No separate admin surface is involved.
        /// </summary>
        public CommunityManageOps? Ops;

        // Manage hooks · the caller opens its contextual editor anchored at
        // the affordance (picker, inline expand) when clicked. Post/event
        // composers live inline on the page · the view owns open/seed state,
        // the host only receives the finished save call.
        public Action? OnAddPhoto;

        // Inline composers · the host saves and refreshes; bool = save-as-draft.
        public Action<CommunityPostComposer, bool>? OnSaveNews;
        public Action<CommunityEventComposer>? OnSaveEvent;

        // Per-item affordances · contextual delete on the content itself.
        public Action<SyncshellNewsPostDto>? OnDeleteNews;
        public Action<SyncshellEventDto>? OnDeleteEvent;

        /// <summary>"Edit event" routing - (event, occurrence) → the dedicated
        /// Event Planner editor. Only wired by hosts that host the planner;
        /// null hides the affordance entirely. Null occurrence = series scope.</summary>
        public Action<SyncshellEventDto, SyncshellEventOccurrenceDto?>? OnEditEvent;
        /// <summary>Whether the viewer may edit this event - staff rights, or
        /// author/organizer of that event. Gates the contextual Edit button;
        /// the shortcut never grants permissions.</summary>
        public Func<SyncshellEventDto, bool>? CanEditEvent;
        /// <summary>"View event" routing - the planner's read action opens the
        /// attendee-facing detail (host picks the surface: community tab,
        /// standalone window). Null hides the affordance.</summary>
        public Action<SyncshellEventDto>? OnViewEvent;

        // Event participation - members get these hooks even without manage
        // rights; every call lands back on the participant summary.
        /// <summary>Sets the viewer's RSVP (<see cref="EventRsvpStatus"/> int); the
        /// server may coerce Attending → Waitlist at capacity.</summary>
        public Action<SyncshellEventDto, int>? OnRsvp;
        /// <summary>Withdraws the viewer's participation entirely.</summary>
        public Action<SyncshellEventDto>? OnWithdrawRsvp;
        /// <summary>Loads the participant summary for the detail page - cached on page state.</summary>
        public Func<SyncshellEventDto, Task<EventParticipantsDto?>>? LoadParticipants;
        /// <summary>Staff participant override: (event, target uid, status, remove).</summary>
        public Action<SyncshellEventDto, string, int, bool>? OnUpdateParticipant;
        /// <summary>Cancels the whole event (staff) - members never see the hook.</summary>
        public Action<SyncshellEventDto>? OnCancelEvent;
        /// <summary>Expanded occurrences in [fromUtc,toUtc] for the calendar mode.</summary>
        public Func<DateTime, DateTime, Task<List<SyncshellEventOccurrenceDto>?>>? LoadCalendar;
        /// <summary>Per-occurrence exception write - cancel/restore a series date.</summary>
        public Action<SyncshellEventExceptionDto>? OnEventException;

        /// <summary>Member identities for the organizer picker -
        /// (uid, display label) pairs resolved by the host from its pair
        /// manager; the picker stores UIDs only. Null hides the picker.</summary>
        public Func<IReadOnlyList<(string Uid, string Label)>>? MemberOptions;
        /// <summary>Searchable member + friend identities for @-mention
        /// suggestions - resolved by the host; null hides the picker.</summary>
        public Func<IReadOnlyList<UI.Components.MarkdownComposer.MentionCandidate>>? MentionCandidates;
        /// <summary>uid → display label for organizer/attendee fallback when
        /// the DTO projection is absent (older server payload).</summary>
        public Func<string, string>? MemberLabel;
        /// <summary>Opens the live-preview popout for the current draft -
        /// host-owned window; null hides the affordance.</summary>
        public Action<CommunityEventComposer>? OnPreviewEvent;

        /// <summary>Comment-thread ops factory - (kind, targetId) → bound ops
        /// for the shared <see cref="UI.Components.CommentThreadView"/>; null
        /// hides comment UI entirely.</summary>
        public Func<CommentTargetKind, long, UI.Components.CommentThreadView.CommentOps?>? Comments;

        /// <summary>Optional travel integration - supplied by hosts that can
        /// reach the Lifestream services; null removes every travel
        /// affordance (property picker, travel button, map action).</summary>
        public CommunityTravelHooks? Travel;

        /// <summary>Permanent community location write - (destination,
        /// publish). ManagePage-gated on the server; null on hosts that
        /// don't wire it hides the settings save action.</summary>
        public Action<EventDestinationDto?, bool>? OnSetCommunityLocation;
    }

    /// <summary>
    /// Host-bound travel/address services for the event composer and detail:
    /// game-data lookups for the address pickers, bound-area resolution for
    /// "Use Syncshell property", the optional Lifestream state/travel calls
    /// and the map action. Everything is optional - the view degrades
    /// gracefully per missing hook.
    /// </summary>
    internal sealed record CommunityTravelHooks(
        /// <summary>Current travel state for a destination - evaluated off
        /// the draw path by the caller.</summary>
        Func<EventDestinationDto?, Task<LifestreamTravelStatus>>? GetState = null,
        /// <summary>Explicit user-initiated travel request.</summary>
        Func<EventDestinationDto, Task<LifestreamTravelStatus>>? RequestTravel = null,
        /// <summary>Bound-area locations of the current shell - the source
        /// for "Use Syncshell property" candidates.</summary>
        Func<Task<IReadOnlyList<Sphene.API.Dto.CharaData.LocationInfo>?>>? LoadBoundLocations = null,
        /// <summary>The player's current in-game location (framework thread).</summary>
        Func<Sphene.API.Dto.CharaData.LocationInfo?>? CurrentLocation = null,
        /// <summary>World row id → display name.</summary>
        Func<ushort, string?>? WorldName = null,
        /// <summary>Territory row id → display name.</summary>
        Func<uint, string?>? TerritoryName = null,
        /// <summary>World picker entries - built once by the host.</summary>
        Func<IReadOnlyList<(ushort Id, string Name)>>? WorldChoices = null,
        /// <summary>Territory picker entries for public locations.</summary>
        Func<IReadOnlyList<(uint Id, string Name)>>? TerritoryChoices = null,
        /// <summary>Opens the in-game map at the destination - returns false
        /// when no map can be produced.</summary>
        Func<EventDestinationDto, Task<bool>>? ShowOnMap = null);

    /// <summary>Bound-area travel affordance for non-member previews of
    /// area-bound shells - the split CTA resolves bound locations into
    /// travel destinations. Joining itself still only happens in bounds.</summary>
    internal sealed record CommunityAreaTravel(
        Func<Task<IReadOnlyList<EventDestinationDto>>>? LoadDestinations = null,
        Func<EventDestinationDto, Task<LifestreamTravelStatus>>? RequestTravel = null,
        Func<EventDestinationDto, Task<bool>>? ShowOnMap = null,
        Action<LifestreamTravelStatus>? OnTravelResult = null);

    /// <summary>
    /// <paramref name="heroBleed"/> · when set (standalone windows), the banner
    /// covers the whole window rect including the title bar, and
    /// <paramref name="onClose"/>/<paramref name="onFold"/> draw close and
    /// collapse affordances over the hero. <paramref name="foldCollapsed"/>
    /// is the fold's target state and drives the chevron direction.
    /// <paramref name="page"/> carries the tab navigation state · without it
    /// the page renders the Overview digest only. <paramref name="onViewerPopout"/>
    /// receives (images, startIndex) when the lightbox asks for a standalone window.
    /// <paramref name="onPreviewToggle"/>, when set, adds a subtle eye chip to the
    /// hero chrome that flips the page into its visitor rendering.
    /// Returns the pinned-header/hero-chrome overlay · invoke it on the host
    /// window AFTER the scroll area ends so its items register input outside
    /// the child's clip (null-safe when nothing is pinned).
    /// </summary>
    /// <paramref name="travel"/> carries the read-only Lifestream/address
    /// hooks for every viewer - independent of <paramref name="edit"/>, so
    /// members and public viewers keep travel actions without any editing
    /// rights. The editor-facing pickers still live on
    /// <see cref="SyncshellCommunityEdit.Travel"/>.
    /// </summary>
    public static Action? DrawContent(SyncshellCommunityDto community, GroupFullInfoDto? group,
        string texturePrefix, SyncshellCommunityEdit? edit = null,
        IReadOnlyList<(FontAwesomeIcon Icon, string Label)>? actionTiles = null,
        Action<int>? onActionTile = null,
        (Vector2 Min, Vector2 Size)? heroBleed = null, Action? onClose = null,
        Action? onFold = null, bool foldCollapsed = false,
        CommunityPageState? page = null,
        Action<List<SyncshellGalleryImageDto>, int>? onViewerPopout = null,
        CommunityMemberHooks? members = null,
        Func<SyncshellGalleryImageDto, Task<SyncshellGalleryImageDto?>>? onLikeImage = null,
        Action? onPreviewToggle = null, bool previewActive = false,
        CommunityTravelHooks? travel = null, CommunityAreaTravel? areaTravel = null)
    {
        var scale = SpheneUI.Scale;
        var profile = community.Profile;
        var accent = ParseAccent(profile.AccentColor) ?? SpheneUI.Palette.CrystalBlue;
        var shellName = group?.Group.AliasOrGID ?? community.GroupAlias ?? profile.GroupGID;
        var visible = community.VisibleSections ?? Enum.GetValues<SyncshellCommunitySection>().ToList();
        var visibleSet = new HashSet<SyncshellCommunitySection>(visible);
        var showHidden = edit?.Ops != null;

        // The collapsing-toolbar layout: a spacer reserves the expanded
        // header inside the scroll child while the hero + tabs are drawn as
        // pinned overlays on top of it. The hero retracts into a compact
        // strip as scroll consumes the spacer  ·  1:1 with the wheel, no
        // oscillation, tabs stay docked underneath. Embedded hosts (the
        // hub) pin over their own scroll child - its frame is the bleed.
        var pinned = page != null;
        (Vector2 Min, Vector2 Size)? bleed = pinned
            ? heroBleed ?? (ImGui.GetWindowPos(), ImGui.GetWindowSize())
            : heroBleed;
        var pinnedGeom = default(PinnedHeaderGeom);
        var contentX = ImGui.GetCursorScreenPos().X;
        // Wheel that landed on the pinned overlay child · apply it to this
        // child's scroll before geometry/overlay positions are read.
        if (page != null && MathF.Abs(page.ScrollNudge) > 0.001f)
        {
            var nudge = page.ScrollNudge;
            page.ScrollNudge = 0f;
            var scrollTarget = ImGui.GetScrollY()
                - nudge * ImGui.GetTextLineHeightWithSpacing() * 4f;
            ImGui.SetScrollY(Math.Clamp(scrollTarget, 0f, ImGui.GetScrollMaxY()));
        }
        if (pinned)
        {
            pinnedGeom = ComputePinnedGeom(bleed!.Value.Min,
                (!string.IsNullOrEmpty(profile.BannerImageBase64) || profile.BannerMediaHash != null), scale);
            ImGui.Dummy(new Vector2(0f, pinnedGeom.Spacer));
        }
        else
        {
            DrawHeader(profile, group, texturePrefix, accent, shellName, scale, edit,
                community.IsListedPublicly, community.FollowerCount, heroBleed, page);
        }

        if (actionTiles is { Count: > 0 } && edit?.CanEditIdentity != true)
        {
            var hit = SyncshellCard.ActionTiles("community-actions", actionTiles.ToArray(), accent);
            if (hit >= 0)
            {
                onActionTile?.Invoke(hit);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        // Area-bound preview CTA - "Travel to the area | Show on map" split.
        // The bound area doubles as the published travel target; membership
        // itself still only happens while standing inside the bounds.
        if (areaTravel != null && community.IsAreaBound
            && page != null && edit?.CanEditIdentity != true)
        {
            EnsureAreaDestinations(page, areaTravel);
            var dest = page.AreaDestinations is { Count: > 0 } dd ? dd[0] : (EventDestinationDto?)null;
            var ctaW = SpheneUI.Layout.CardAlignedAvailX();
            var split = SpheneUI.Controls.SplitButton("area-travel",
                FontAwesomeIcon.Route, "Travel to the area", SpheneUI.Palette.CrystalBlue,
                FontAwesomeIcon.MapMarkedAlt, "Show on map", SpheneUI.Palette.SpheneGold,
                ctaW, 34f * scale, enabled: dest != null && !page.AreaTravelBusy);
            if (split.left && dest is { } td && areaTravel.RequestTravel is { } request)
            {
                page.AreaTravelBusy = true;
                _ = Task.Run(async () =>
                {
                    var st = await request(td).ConfigureAwait(false);
                    page.AreaTravelBusy = false;
                    areaTravel.OnTravelResult?.Invoke(st);
                });
            }
            if (split.right && dest is { } md && areaTravel.ShowOnMap is { } show)
            {
                _ = Task.Run(async () => await show(md).ConfigureAwait(false));
            }
            SpheneUI.AttachTooltip(page.AreaDestLoading || page.AreaDestinations == null
                ? "Resolving the bound area..."
                : dest == null
                    ? "This shell has no reachable bound area published"
                    : "Lifestream travel to the bound area · the join prompt appears once you arrive");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        // "In the area" roster for area-bound shells - name/avatar identities
        // only; the live appearance view still requires standing inside the
        // bounds and pairing like any other member.
        if (community.PresentMembers is { Count: > 0 } present)
        {
            var avatars = new List<IDalamudTextureWrap?>(Math.Min(present.Count, 6));
            var names = new List<string>(present.Count);
            foreach (var u in present)
            {
                var p = members?.GetProfile?.Invoke(u.UID);
                avatars.Add(p is { ImageData.Value.Length: > 0 }
                    ? StaffAvatar(texturePrefix, u.UID, p.ImageData.Value)
                    : null);
                names.Add(members?.GetDisplayName?.Invoke(u.UID)
                    ?? u.DisplayName ?? u.Alias ?? "Unknown user");
            }
            Sphene.UI.Components.AvatarStack.Draw("present-stack", avatars,
                20f * scale, maxShown: 6, accent: accent);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Muted($"{present.Count} in the area");
            SpheneUI.AttachTooltip(string.Join(", ", names));
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        if (page == null)
        {
            DrawOverview(community, group, texturePrefix, accent, page: null, edit, members, travel);
            DrawInfoFooter(profile, group);
            return () => DrawChromeOverlay(heroBleed, onClose, onFold, foldCollapsed, scale,
                onPreviewToggle, previewActive);
        }

        // Tab navigation · the community page is navigated like a modern
        // venue page, not scrolled end to end. Pinned surfaces draw the bar
        // in the header overlay instead of inline (see DrawPinnedHeader).
        var tabs = BuildTabs(visibleSet, showHidden);
        if (page.Tab.HasValue && tabs.All(t => t.Section != page.Tab))
        {
            // A pending photo deep-link keeps the Gallery tab alive - the
            // caller navigated to a concrete image, and DrawGallery either
            // opens the viewer or clears the focus when it can't resolve.
            var focusKeepsGallery = page.FocusItemId != null
                && page.Tab == SyncshellCommunitySection.Gallery;
            page.Tab = focusKeepsGallery ? page.Tab : null;
        }
        if (page.Tab != SyncshellCommunitySection.Gallery)
        {
            // Gallery-local modes must not strand across tabs - a leftover
            // edit mode or picker blocks clicks while looking exactly like
            // "the page ignores input". The Tab setter already closed any
            // open lightbox when the section actually changed.
            page.AlbumId = null;
            if (page.ManagingAlbums)
            {
                SpheneUI.LogDebug("[CommUi] ManageAlbums=false");
            }
            page.ManagingAlbums = false;
            page.PickingAlbumPhotos = false;
            page.ViewerEditOpen = false;
            page.ViewerEditImgId = -1;
            page.ViewerEditError = null;
        }
        var selIdx = 0;
        if (tabs.Count > 1)
        {
            page.TabItems.Clear();
            for (var i = 0; i < tabs.Count; i++)
            {
                page.TabItems.Add(tabs[i].Item);
                if (tabs[i].Section == page.Tab)
                {
                    selIdx = i;
                }
            }
            if (!pinned)
            {
                var hit = SpheneUI.Controls.TabBar("community", page.TabItems, selIdx, accent);
                if (hit >= 0)
                {
                    page.CloseViewer();
                    page.Tab = tabs[hit].Section;
                    page.EventId = null;
                    page.Subpage = CommunitySubpage.None;
                    page.SwitchT = 0f;
                }
            }
        }

        // Content entry animation: short ease · the page slides in from the
        // right and fades up while ImGui items respect the style alpha.
        page.SwitchT = MathF.Min(1f, page.SwitchT + ImGui.GetIO().DeltaTime / 0.16f);
        var ease = 1f - MathF.Pow(1f - page.SwitchT, 3f);
        var indent = (1f - ease) * 14f * scale;
        if (indent > 0.01f)
        {
            ImGui.Indent(indent);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.35f + 0.65f * ease);

        // Event drill-in replaces the tab content · any surface can deep-link
        // a single event via page.EventId.
        SyncshellEventDto? openEvent = null;
        if (page.EventId is { } eventId)
        {
            openEvent = community.Events?.FirstOrDefault(e => e.Id == eventId);
            if (openEvent == null)
            {
                page.EventId = null;
            }
        }

        // Manage subpages are in-page drill-ins · they need the ops surface,
        // so without it (public preview, rights gone) fall back to the page.
        // Each subpage additionally checks its own permission flag.
        if (page.Subpage != CommunitySubpage.None && edit?.Ops == null
            || page.Subpage == CommunitySubpage.Settings && edit?.CanManagePage != true)
        {
            page.Subpage = CommunitySubpage.None;
        }
        var drewAny = page.Subpage != CommunitySubpage.None
            ? DrawManageSubpage(page.Subpage, community, accent, edit!, page)
            : openEvent != null
                ? DrawEventDetail(openEvent, community, texturePrefix, accent, edit, page, members, travel)
                : page.Tab is { } section
                    ? DrawSectionWrapped(section, community, group, texturePrefix, accent, edit, page, members, visibleSet)
                    : DrawOverview(community, group, texturePrefix, accent, page, edit, members, travel);

        ImGui.PopStyleVar();
        if (indent > 0.01f)
        {
            ImGui.Unindent(indent);
        }

        if (!drewAny && string.IsNullOrWhiteSpace(profile.Tagline) && edit == null)
        {
            SpheneUI.Text.MutedWrapped("This syncshell hasn't customized its community page yet.");
        }

        DrawInfoFooter(profile, group);

        // The pinned collapsing header · hero apron, docked tab bar and the
        // compact strip · is returned as a deferred overlay: the caller runs
        // it on the host window after EndScrollArea, where it opens its own
        // input-taking child over the pinned zone. Its own window clip and
        // hover context make tabs, pens and the banner zone register  · 
        // drawing into the scroll child or the host window would leave the
        // strip dead to input. scrollY is captured now · the content child's
        // scroll · because the overlay child has none of its own.
        if (pinned)
        {
            var scrollY = ImGui.GetScrollY();
            var listed = community.IsListedPublicly;
            return () =>
            {
                DrawPinnedOverlay(profile, group, texturePrefix, accent, shellName,
                    scale, edit, listed, community.FollowerCount, bleed!.Value,
                    pinnedGeom, page!, tabs, selIdx,
                    contentX, scrollY, onClose, onFold, foldCollapsed,
                    onPreviewToggle, previewActive, standalone: heroBleed.HasValue);
                DrawCommunityViewerOverlay(community, profile, texturePrefix, scale,
                    edit, page!, bleed!.Value, onViewerPopout, onLikeImage);
            };
        }
        return () => DrawChromeOverlay(heroBleed, onClose, onFold, foldCollapsed, scale,
            onPreviewToggle, previewActive);
    }

    /// <summary>Optimistic toggle in the viewer's image list, then the caller's API round-trip.</summary>
    private static async Task ToggleViewerLike(CommunityPageState page, int index,
        Func<SyncshellGalleryImageDto, Task<SyncshellGalleryImageDto?>> like)
    {
        var list = page.ViewerImages;
        if (list == null || index >= list.Count)
        {
            return;
        }
        var img = list[index];
        list[index] = img with
        {
            LikedByMe = !img.LikedByMe,
            LikeCount = Math.Max(0, img.LikeCount + (img.LikedByMe ? -1 : 1))
        };
        var result = await like(img).ConfigureAwait(false);
        if (result != null && page.ViewerImages != null && index < page.ViewerImages.Count
            && page.ViewerImages[index].Id == result.Id)
        {
            page.ViewerImages[index] = result;
        }
    }

    /// <summary>Drawer height · the member allow-list row only exists for
    /// <see cref="SyncshellGalleryAudience.SelectedMembers"/>.</summary>
    private static float CommunityPhotoEditorHeight(CommunityPageState page, float scale)
        => 76f * scale + (page.ViewerEditAudience == (int)SyncshellGalleryAudience.SelectedMembers
            ? ImGui.GetFrameHeight() + 10f * scale : 0f)
            + (page.ViewerEditError != null ? ImGui.GetTextLineHeight() + 6f * scale : 0f);

    /// <summary>
    /// Community photo editor inside the lightbox drawer · caption field,
    /// album picker, the audience segmented control plus its member
    /// allow-list, and the two-click delete. Saves through <see
    /// cref="CommunityManageOps.SaveImage"/> and patches the open viewer list
    /// optimistically; the host's refresh confirms.
    /// </summary>
    private static void DrawCommunityPhotoEditor(SyncshellGalleryImageDto img, Vector2 min,
        float w, SyncshellCommunityDto community, SyncshellCommunityEdit edit,
        CommunityPageState page)
    {
        var scale = SpheneUI.Scale;
        var gap = SpheneUI.Spacing.X8.Scaled;
        var ops = edit.Ops!;
        if (page.ViewerEditImgId != img.Id)
        {
            page.ViewerEditImgId = img.Id;
            page.ViewerEditCaption = img.Caption ?? string.Empty;
            page.ViewerEditAlbumId = img.AlbumId;
            page.ViewerEditAudience = (int)img.Audience;
            page.ViewerEditAllowed.Clear();
            if (img.AllowedUids != null)
            {
                foreach (var u in img.AllowedUids)
                {
                    page.ViewerEditAllowed.Add(u);
                }
            }
            page.ViewerDeleteArmed = false;
            page.ViewerEditError = null;
        }

        var rowH = ImGui.GetFrameHeight();
        var saveW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Check).X;
        ImGui.SetCursorScreenPos(min);
        SpheneUI.Controls.InputTextWithHint("##photo-edit-cap", "Caption…",
            ref page.ViewerEditCaption, 200, preferredWidth: w - saveW - gap);
        ImGui.SetCursorScreenPos(new Vector2(min.X + w - saveW, min.Y));
        if (page.ViewerEditSaving)
        {
            SpheneUI.Icons.Icon(FontAwesomeIcon.Spinner, SpheneUI.Colors.TextMuted);
        }
        else if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
        {
            CommitPhotoEdit(img, page, ops);
        }
        SpheneUI.AttachTooltip(page.ViewerEditSaving
            ? "Saving…" : "Save caption, album & visibility");

        var row2Y = min.Y + rowH + 8f * scale;
        ImGui.SetCursorScreenPos(new Vector2(min.X, row2Y));
        var albums = community.Albums ?? [];
        var photoAlbumLabel = page.ViewerEditAlbumId == null
            ? "No album"
            : $"Album: {albums.FirstOrDefault(a => a.Id == page.ViewerEditAlbumId)?.Name ?? "?"}";
        if (SpheneUI.Controls.BeginComboBox("##phalbum", photoAlbumLabel, 150f * scale))
        {
            if (SpheneUI.Controls.Selectable("No album##noval", page.ViewerEditAlbumId == null))
            {
                page.ViewerEditAlbumId = null;
            }
            foreach (var a in albums)
            {
                if (SpheneUI.Controls.Selectable($"{a.Name}##a{a.Id}",
                        page.ViewerEditAlbumId == a.Id))
                {
                    page.ViewerEditAlbumId = a.Id;
                }
            }
            SpheneUI.Controls.EndComboBox();
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X + 150f * scale + gap, row2Y));
        if (SpheneUI.Controls.Segmented("##phaud", ref page.ViewerEditAudience,
                ["Everyone", "Staff only", "Selected"],
                width: w - 150f * scale - gap - SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X - gap))
        {
            page.ViewerEditAudience = Math.Clamp(page.ViewerEditAudience, 0, 2);
        }
        SpheneUI.AttachTooltip("Everyone: all members · Staff only: staff members & staff roles · Selected: the members you pick");

        var delW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X;
        ImGui.SetCursorScreenPos(new Vector2(min.X + w - delW, row2Y));
        if (SpheneUI.Icons.Button(page.ViewerDeleteArmed
                ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.Trash))
        {
            if (page.ViewerDeleteArmed)
            {
                ops.DeleteImage?.Invoke(img);
                page.ViewerImages?.RemoveAll(v => v.Id == img.Id);
                page.ViewerIndex = Math.Clamp(page.ViewerIndex, 0,
                    Math.Max(0, (page.ViewerImages?.Count ?? 1) - 1));
                page.ViewerDeleteArmed = false;
            }
            else
            {
                page.ViewerDeleteArmed = true;
            }
        }
        SpheneUI.AttachTooltip(page.ViewerDeleteArmed
            ? "Click again to delete this photo" : "Delete photo");

        if (page.ViewerEditAudience == (int)SyncshellGalleryAudience.SelectedMembers)
        {
            var row3Y = row2Y + rowH + 8f * scale;
            ImGui.SetCursorScreenPos(new Vector2(min.X, row3Y));
            var members = ops.GetStaffMembers?.Invoke() ?? [];
            var chipGap = 4f * scale;
            using (ImRaii.Child($"##aud-scroll-{img.Id}",
                       new Vector2(w, rowH + 6f * scale), false,
                       ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoSavedSettings))
            {
                var first = true;
                foreach (var m in members)
                {
                    var on = page.ViewerEditAllowed.Contains(m.Uid);
                    if (!first)
                    {
                        ImGui.SameLine(0f, chipGap);
                    }
                    if (SpheneUI.Controls.ToggleChip($"{m.DisplayName}##aud-{img.Id}-{m.Uid}", ref on))
                    {
                        if (on) page.ViewerEditAllowed.Add(m.Uid);
                        else page.ViewerEditAllowed.Remove(m.Uid);
                    }
                    first = false;
                }
                if (members.Count == 0)
                {
                    SpheneUI.Text.Muted("No members to pick");
                }
            }
            SpheneUI.AttachTooltip("Who may see this photo · staff and the uploader always do");
        }

        if (page.ViewerEditError is { } editError)
        {
            ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y +
                CommunityPhotoEditorHeight(page, scale) - ImGui.GetTextLineHeight() - 4f * scale));
            SpheneUI.Text.Colored(editError, SpheneUI.Palette.Error);
        }
    }

    /// <summary>Writes the buffered photo edit through the ops and patches the
    /// open viewer list so the lightbox reflects the save instantly. Success
    /// closes the edit drawer; failure keeps it open with the draft intact.</summary>
    private static void CommitPhotoEdit(SyncshellGalleryImageDto img,
        CommunityPageState page, CommunityManageOps ops)
    {
        if (page.ViewerEditSaving || ops.SaveImage == null)
        {
            return;
        }
        var audience = (SyncshellGalleryAudience)Math.Clamp(page.ViewerEditAudience, 0, 2);
        var patched = img with
        {
            Caption = string.IsNullOrWhiteSpace(page.ViewerEditCaption)
                ? null
                : page.ViewerEditCaption.Trim(),
            AlbumId = page.ViewerEditAlbumId,
            Audience = audience,
            AllowedUids = audience == SyncshellGalleryAudience.SelectedMembers
                ? page.ViewerEditAllowed.ToList()
                : null,
        };
        var imgId = img.Id;
        page.ViewerEditSaving = true;
        page.ViewerEditError = null;
        SpheneUI.LogDebug("[CommUi] photo save click id={ImageId}", imgId);
        _ = Task.Run(async () =>
        {
            var ok = await ops.SaveImage(patched).ConfigureAwait(false);
            page.ViewerEditSaving = false;
            if (ok)
            {
                SpheneUI.LogDebug("[CommUi] photo save ok id={ImageId}", imgId);
                if (page.ViewerImages != null)
                {
                    var idx = page.ViewerImages.FindIndex(v => v.Id == imgId);
                    if (idx >= 0)
                    {
                        page.ViewerImages[idx] = patched;
                    }
                }
                page.ViewerEditOpen = false;
                page.ViewerEditImgId = -1;
                page.ViewerEditError = null;
            }
            else
            {
                SpheneUI.LogDebug("[CommUi] photo save rejected id={ImageId}", imgId);
                page.ViewerEditError = "Couldn't save the photo - changes kept, try again.";
            }
        });
    }

    /// <summary>
    /// The page's navigation model: Overview plus one tab per visible section.
    /// In edit mode hidden sections stay reachable but render dimmed.
    /// </summary>
    private static List<(SyncshellCommunitySection? Section, SpheneUI.Controls.TabItem Item)>
        BuildTabs(HashSet<SyncshellCommunitySection> visibleSet, bool editing)
    {
        var tabs = new List<(SyncshellCommunitySection?, SpheneUI.Controls.TabItem)>
        {
            (null, new(FontAwesomeIcon.Home, "Overview"))
        };
        foreach (var (section, label, icon) in SectionOrder)
        {
            if (visibleSet.Contains(section))
            {
                tabs.Add((section, new(icon, label)));
            }
            else if (editing)
            {
                tabs.Add((section, new(icon, label, Dimmed: true,
                    Tooltip: "Hidden · shown only while editing")));
            }
        }
        return tabs;
    }

    /// <summary>
    /// Overview widget keys · the owner reorders these in the page's settings
    /// subpage; the persisted order rides <see cref="SyncshellCommunityDto.OverviewWidgets"/>.
    /// </summary>
    internal static readonly string[] DefaultOverviewWidgets =
        ["upnext", "location", "teasers", "about", "staff", "links"];

    /// <summary>
    /// Overview digest · widgets render in the owner-configured order
    /// (<see cref="SyncshellCommunityDto.OverviewWidgets"/>), unknown keys are
    /// skipped and new widgets append at the end so old layouts never break.
    /// </summary>
    private static bool DrawOverview(SyncshellCommunityDto community, GroupFullInfoDto? group,
        string texturePrefix, Vector4 accent, CommunityPageState? page, SyncshellCommunityEdit? edit,
        CommunityMemberHooks? members, CommunityTravelHooks? travel = null)
    {
        var drewAny = false;
        var saved = community.OverviewWidgets;
        if (saved is { Count: > 0 })
        {
            foreach (var key in saved)
            {
                drewAny |= DrawOverviewWidget(key, community, group, texturePrefix, accent, page, edit, members, travel);
            }
            foreach (var key in DefaultOverviewWidgets)
            {
                if (!saved.Contains(key, StringComparer.Ordinal))
                {
                    drewAny |= DrawOverviewWidget(key, community, group, texturePrefix, accent, page, edit, members, travel);
                }
            }
        }
        else
        {
            foreach (var key in DefaultOverviewWidgets)
            {
                drewAny |= DrawOverviewWidget(key, community, group, texturePrefix, accent, page, edit, members, travel);
            }
        }
        return drewAny;
    }

    /// <summary>Renders one overview widget by key. Returns false when it had nothing to show.</summary>
    private static bool DrawOverviewWidget(string key, SyncshellCommunityDto community,
        GroupFullInfoDto? group, string texturePrefix, Vector4 accent, CommunityPageState? page,
        SyncshellCommunityEdit? edit, CommunityMemberHooks? members,
        CommunityTravelHooks? travel = null)
    {
        switch (key)
        {
            case "upnext":
                DrawUpNext(community, accent, page);
                return true;
            case "location":
                return DrawCommunityLocation(community, travel, page, accent);
            case "teasers":
                return DrawOverviewTeasers(community, texturePrefix, accent, page);
            case "about":
                return DrawAbout(community.Profile, accent, edit, teaser: true, page);
            case "staff":
                return DrawStaffTeaser(community, group, accent, page, members);
            case "links":
                return DrawLinksTeaser(community, accent);
            default:
                return false;
        }
    }

    /// <summary>
    /// The shell's published permanent location as a compact overview card -
    /// absent entirely when nothing is published (or the viewer may not see
    /// it). Travel affordances need page state; non-page surfaces render the
    /// address only.
    /// </summary>
    private static bool DrawCommunityLocation(SyncshellCommunityDto community,
        CommunityTravelHooks? travel, CommunityPageState? page, Vector4 accent)
    {
        if (community.CommunityDestination is not { } dest
            || EventDestinationValidator.Sanitize(dest) is not { } clean)
        {
            return false;
        }
        Widget("community-location", "Community location", FontAwesomeIcon.Home,
            null, accent, () =>
        {
            SpheneUI.Text.SectionHeader(DestinationTitle(clean, travel));
            SpheneUI.Text.MutedWrapped(DestinationSubtitle(clean, travel));
            if (!community.CommunityDestinationPublished)
            {
                SpheneUI.Text.MutedWrapped("Not published - only staff see this card.");
            }
            if (page != null)
            {
                DrawDestinationActions(clean, travel, page, accent, scopeKey: 0);
            }
        });
        return true;
    }

    /// <summary>
    /// One staff row's data: the member's affiliation label (Owner, Moderator
    /// or their custom job), its badge color and the list ordering · owner
    /// first, then custom jobs by sort order, plain moderators last.
    /// </summary>
    /// <param name="Tier">0 owner - 1 role holder - 2 staff without a job.</param>
    /// <param name="Since">Earliest assignment row id - grant order breaks rank ties.</param>
    /// <param name="Badge">Owner-only crown pip on the avatar.</param>
    /// <param name="Icons">Every held job glyph - right-aligned in the row.</param>
    private sealed record StaffEntry(string Uid, string? Alias, string Role,
        Vector4 RoleColor, int Tier, int Rank, long Since,
        (FontAwesomeIcon Icon, Vector4 Color)? Badge,
        IReadOnlyList<(FontAwesomeIcon Icon, Vector4 Color, string Name)> Icons);

    private static List<StaffEntry> StaffEntries(SyncshellCommunityDto community,
        GroupFullInfoDto? group, Vector4 accent)
    {
        if (group == null)
        {
            return [];
        }
        // Seniority per member - the lowest assignment row id is the first
        // job they ever got, so ties at the same rank fall to grant order.
        var sinceByUid = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var a in community.StaffAssignments ?? [])
        {
            if (!sinceByUid.TryGetValue(a.UserUID, out var prev) || a.Id < prev)
            {
                sinceByUid[a.UserUID] = a.Id;
            }
        }
        var ownerUid = group.Owner.UID;
        var ownerRoles = MemberRoles(community, ownerUid);
        var ownerIcons = ownerRoles.Select(r =>
            (StaffRoleIcons.Parse(r.Icon), StaffColor(r.ColorArgb, accent), r.Name)).ToList();
        var entries = new List<StaffEntry>
        {
            new(ownerUid, HumanName(group.Owner) ?? "Unknown user",
                ownerRoles.Count > 0
                    ? "Owner · " + string.Join(" · ", ownerRoles.Select(r => r.Name))
                    : "Owner",
                SpheneUI.Palette.SpheneGold, 0, 0, 0L,
                (StaffRoleIcons.Owner, SpheneUI.Palette.SpheneGold), ownerIcons)
        };
        // The roster follows the authoritative staff assignments plus the
        // shell's privileged pair roster - never the intersection of both. A
        // staff member without a live pair (left a bound area, offline) keeps
        // their entry; pair metadata only decorates it when present.
        var staffUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in community.StaffAssignments ?? [])
        {
            staffUids.Add(a.UserUID);
        }
        foreach (var uid in group.GroupPairUserInfos.Keys)
        {
            staffUids.Add(uid);
        }
        staffUids.Remove(ownerUid);

        foreach (var uid in staffUids)
        {
            var memberName = IdentityName(community, group, uid);
            var memberRoles = MemberRoles(community, uid);
            var flags = group.GroupPairUserInfos.TryGetValue(uid, out var f)
                ? f
                : GroupPairUserInfo.None;
            var role = memberRoles.Count > 0 ? memberRoles[0] : null;
            if (role != null)
            {
                var icons = memberRoles.Select(r =>
                    (StaffRoleIcons.Parse(r.Icon), StaffColor(r.ColorArgb, accent), r.Name)).ToList();
                entries.Add(new(uid, memberName, string.Join(" · ", memberRoles.Select(r => r.Name)),
                    StaffColor(role.ColorArgb, accent),
                    1, role.SortOrder, sinceByUid.GetValueOrDefault(uid, long.MaxValue),
                    null, icons));
            }
            else if (flags.IsModerator() || flags.IsStaff() || sinceByUid.ContainsKey(uid))
            {
                entries.Add(new(uid, memberName, "Staff member", accent, 2, 0,
                    sinceByUid.GetValueOrDefault(uid, long.MaxValue),
                    null, []));
            }
        }
        return entries.OrderBy(e => e.Tier)
            .ThenBy(e => e.Rank)
            .ThenBy(e => e.Since)
            .ThenBy(e => e.Alias ?? e.Uid, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// All staff roles a member currently holds, ordered by rank (SortOrder,
    /// top of the list = highest). The first entry is the primary role the
    /// pills and staff cards display.
    /// </summary>
    internal static List<SyncshellStaffRoleDto> MemberRoles(SyncshellCommunityDto? community, string uid)
    {
        var result = new List<SyncshellStaffRoleDto>();
        var assignments = community?.StaffAssignments;
        var roles = community?.StaffRoles;
        if (assignments == null || roles == null)
        {
            return result;
        }
        foreach (var a in assignments)
        {
            if (!string.Equals(a.UserUID, uid, StringComparison.Ordinal)) continue;
            var role = roles.FirstOrDefault(r => r.Id == a.RoleId);
            if (role != null)
            {
                result.Add(role);
            }
        }
        result.Sort((x, y) => x.SortOrder.CompareTo(y.SortOrder));
        return result;
    }

    /// <summary>
    /// Effective permission bits of a member - the union over every assigned
    /// role, mirroring the server's <c>GetStaffPermissionBits</c> so client
    /// gates never diverge from server checks when a member holds several jobs.
    /// </summary>
    internal static SyncshellStaffPermission MemberPermissionBits(
        SyncshellCommunityDto? community, string uid)
    {
        var bits = SyncshellStaffPermission.None;
        var assignments = community?.StaffAssignments;
        var roles = community?.StaffRoles;
        if (assignments == null || roles == null)
        {
            return bits;
        }
        foreach (var a in assignments)
        {
            if (!string.Equals(a.UserUID, uid, StringComparison.Ordinal)) continue;
            var role = roles.FirstOrDefault(r => r.Id == a.RoleId);
            if (role != null)
            {
                bits |= role.Permissions;
            }
        }
        return bits;
    }

    /// <summary>Stored ARGB → Vector4; zero/empty falls back to the shell accent.</summary>
    private static Vector4 StaffColor(uint argb, Vector4 fallback)
    {
        if (argb == 0u)
        {
            return fallback;
        }
        return new Vector4(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f,
            (argb & 0xFF) / 255f, MathF.Max(0.25f, ((argb >> 24) & 0xFF) / 255f));
    }

    /// <summary>
    /// Overview teasers: each feed its own widget · "Latest news" and "Next
    /// events" sit side by side when the page is wide enough, the photo strip
    /// gets a widget of its own below. Rows are nav rows: click opens the tab.
    /// </summary>
    private static bool DrawOverviewTeasers(SyncshellCommunityDto community,
        string texturePrefix, Vector4 accent, CommunityPageState? page)
    {
        var now = DateTime.UtcNow;
        var news = community.News?.OrderByDescending(p => p.IsPinned)
            .ThenByDescending(p => p.PublishedAt).ToList();
        var upcoming = community.Events?.Where(e => (e.EndUtc ?? e.StartUtc) >= now.AddHours(-4))
            .OrderBy(e => e.StartUtc).ToList();
        var images = community.Gallery?.Where(g => !string.IsNullOrEmpty(g.ImageBase64) || g.ImageMediaHash != null).ToList();
        var hasNews = news is { Count: > 0 };
        var hasEvents = upcoming is { Count: > 0 };
        var hasGallery = images is { Count: > 0 };
        if (!hasNews && !hasEvents && !hasGallery)
        {
            return false;
        }

        var scale = SpheneUI.Scale;
        var widgets = new List<(string Id, string Label, FontAwesomeIcon Icon,
            int Count, Action Body)>(2);
        if (hasNews)
        {
            widgets.Add(("news-teaser", "Latest news", FontAwesomeIcon.Bullhorn,
                news!.Count, () =>
                {
                    foreach (var post in news.Take(3))
                    {
                        var captured = post;
                        SpheneUI.Rows.Nav(captured.Title,
                            $"{captured.PublishedAt.ToLocalTime():MMM d}",
                            () => { if (page != null) { page.Tab = SyncshellCommunitySection.News; page.SwitchT = 0f; } },
                            captured.IsPinned ? FontAwesomeIcon.Thumbtack : null);
                    }
                    if (page != null && news.Count > 3)
                    {
                        TeaserLink(ImGui.GetCursorScreenPos(), accent, "All news",
                            () => { page.Tab = SyncshellCommunitySection.News; page.SwitchT = 0f; }, scale);
                    }
                }));
        }
        if (hasEvents)
        {
            widgets.Add(("events-teaser", "Next events", FontAwesomeIcon.Calendar,
                upcoming!.Count, () =>
                {
                    foreach (var ev in upcoming.Take(3))
                    {
                        var captured = ev;
                        SpheneUI.Rows.Nav(captured.Title,
                            $"{captured.StartUtc.ToLocalTime():MMM d · HH:mm}",
                            () => { if (page != null) { page.EventId = captured.Id; page.SwitchT = 0f; } },
                            FontAwesomeIcon.CalendarDay);
                    }
                    if (page != null && upcoming.Count > 3)
                    {
                        TeaserLink(ImGui.GetCursorScreenPos(), accent, "All events",
                            () => { page.Tab = SyncshellCommunitySection.Events; page.SwitchT = 0f; }, scale);
                    }
                }));
        }
        if (widgets.Count == 2 && SpheneUI.Controls.BoundedAvailX() >= 480f * scale)
        {
            SpheneUI.Layout.CardGrid(widgets, 230f * scale,
                (w, _) => Widget(w.Id, w.Label, w.Icon, w.Count, accent, w.Body),
                gap: SpheneUI.Spacing.X8.Scaled, maxCols: 2);
        }
        else
        {
            foreach (var w in widgets)
            {
                Widget(w.Id, w.Label, w.Icon, w.Count, accent, w.Body);
            }
        }

        if (hasGallery)
        {
            Widget("photos-teaser", "Photos", FontAwesomeIcon.Images, images!.Count, accent, () =>
            {
                DrawGalleryRail(images, texturePrefix, community.Profile.GroupGID,
                    84f * scale, 118f * scale, onClick: page == null ? null
                        : i => { page.ViewerImages = images; page.ViewerIndex = i; page.ViewerOpenT = 0f; });
            });
        }
        return true;
    }

    /// <summary>"All X →" jump link under a teaser widget's rows.</summary>
    private static void TeaserLink(Vector2 pos, Vector4 accent, string label, Action jump, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var lineH = ImGui.GetTextLineHeight();
        var text = $"{label} →";
        var w = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorScreenPos(pos + new Vector2(0f, 6f * scale));
        var clicked = ImGui.InvisibleButton($"##teaser-{label}", new Vector2(w, lineH));
        var hovered = ImGui.IsItemHovered();
        dl.AddText(pos + new Vector2(0f, 6f * scale), SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, hovered ? 1f : 0.75f)), text);
        if (hovered)
        {
            dl.AddLine(pos + new Vector2(0f, 6f * scale + lineH - 1f),
                pos + new Vector2(w, 6f * scale + lineH - 1f), SpheneUI.ColorUtils.ToU32(accent));
        }
        if (clicked)
        {
            jump();
        }
    }

    /// <summary>
    /// Staff teaser · friend-list rows with the affiliation badge, capped at
    /// four plus a jump to the Staff page. One reorderable overview widget.
    /// </summary>
    private static bool DrawStaffTeaser(SyncshellCommunityDto community, GroupFullInfoDto? group,
        Vector4 accent, CommunityPageState? page, CommunityMemberHooks? members)
    {
        var staff = StaffEntries(community, group, accent);
        if (staff is not { Count: > 0 })
        {
            return false;
        }
        var scale = SpheneUI.Scale;
        Widget("staff-teaser", "Staff", FontAwesomeIcon.UserShield, staff.Count, accent, () =>
        {
            var rowGap = 4f * scale;
            foreach (var member in staff.Take(4))
            {
                DrawStaffRow(member, "community-staff:", rowGap, members);
            }
            if (staff.Count > 4 && page != null
                && SpheneUI.Controls.SegmentButton($"All staff · {staff.Count}", FontAwesomeIcon.AngleRight,
                    accent: accent, active: false))
            {
                page.Tab = SyncshellCommunitySection.Staff;
                page.SwitchT = 0f;
            }
        });
        return true;
    }

    /// <summary>Link chips · one reorderable overview widget.</summary>
    private static bool DrawLinksTeaser(SyncshellCommunityDto community, Vector4 accent)
    {
        var links = community.Links?.Where(l => !string.IsNullOrWhiteSpace(l.Url)).ToList();
        if (links is not { Count: > 0 })
        {
            return false;
        }
        var gap = 6f * SpheneUI.Scale;
        Widget("links-teaser", "Links", FontAwesomeIcon.Link, links.Count, accent, () =>
        {
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                DrawLinkChip(link.Label, link.Url, link.Icon, accent, i);
                ImGui.SameLine(0f, gap);
            }
            ImGui.NewLine();
        });
        return true;
    }

    /// <summary>Icon-only link chip for the overview digest · the preset's
    /// brand mark (FA glyph for unbranded links) on the tonal surface used
    /// by link cards; label + URL live in the tooltip.</summary>
    private static void DrawLinkChip(string label, string url, string? icon,
        Vector4 accent, int index)
    {
        var scale = SpheneUI.Scale;
        var side = 34f * scale;
        var dl = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton($"##link-chip-{index}", new Vector2(side, side));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var radius = 8f * scale;

        var fill = SpheneUI.Layout.NavRailBandColor();
        if (hovered)
        {
            fill = SpheneUI.ColorUtils.WithAlpha(fill, MathF.Min(1f, fill.W + 0.08f));
        }
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), radius);
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(hovered ? accent : SpheneUI.Colors.Border,
                hovered ? 0.5f : 0.35f)), radius);

        var inset = 4f * scale;
        LinkBrandPresets.DrawMark(LinkBrandPresets.Find(icon), dl,
            min + new Vector2(inset), max - new Vector2(inset),
            hovered ? accent : SpheneUI.Colors.TextMuted);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var tip = string.IsNullOrWhiteSpace(label) ||
                      string.Equals(label, url, StringComparison.OrdinalIgnoreCase)
                ? url
                : $"{label}\n{url}";
            SpheneUI.AttachTooltip(tip);
        }
        if (clicked)
        {
            Util.OpenLink(url);
        }
    }

    /// <summary>
    /// Per-uid texture-key byte cache for staff avatars/row images · the
    /// shared media cache keys on the texture name, so we evict when the
    /// profile bytes change. Bounded by staff count per shell.
    /// </summary>
    private static readonly Dictionary<string, byte[]> _staffImageBytes = new(StringComparer.Ordinal);

    private static void EnsureAreaDestinations(CommunityPageState page,
        CommunityAreaTravel area)
    {
        if (area.LoadDestinations == null || page.AreaDestinations != null
            || page.AreaDestLoading) return;
        page.AreaDestLoading = true;
        var load = area.LoadDestinations;
        _ = Task.Run(async () =>
        {
            try
            {
                page.AreaDestinations = (await load().ConfigureAwait(false))?.ToList() ?? [];
            }
            catch
            {
                page.AreaDestinations = [];
            }
            finally
            {
                page.AreaDestLoading = false;
            }
        });
    }

    private static IDalamudTextureWrap? StaffAvatar(string prefix, string uid, byte[] data)
    {
        var key = $"{prefix}av:{uid}";
        if (!_staffImageBytes.TryGetValue(key, out var last) || !last.SequenceEqual(data))
        {
            _staffImageBytes[key] = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Texture(key, data);
    }

    private static SpheneUI.Media.AnimatedImage? StaffRowImage(string prefix, string uid, byte[] data)
    {
        var key = $"{prefix}row:{uid}";
        if (!_staffImageBytes.TryGetValue(key, out var last) || !last.SequenceEqual(data))
        {
            _staffImageBytes[key] = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Animated(key, data);
    }

    /// <summary>
    /// One staff member as a friends-list row: avatar ring, display name,
    /// affiliation in the role's own color as the presence line · the same
    /// shared renderer the hub friend list uses. Opens the member's profile
    /// when the surface wires navigation.
    /// </summary>
    private static void DrawStaffRow(StaffEntry member, string texturePrefix, float rowGap,
        CommunityMemberHooks? members)
    {
        var scale = SpheneUI.Scale;
        var rowH = 46f * scale * SpheneUI.Density.Factor;
        var pos = ImGui.GetCursorScreenPos();
        var width = SpheneUI.Controls.BoundedAvailX();
        var dl = ImGui.GetWindowDrawList();
        var canOpen = members?.CanOpenProfile?.Invoke(member.Uid) == true;
        var rowId = ImGui.GetID($"##staff-{member.Uid}");
        ImGui.InvisibleButton($"##staff-{member.Uid}", new Vector2(width, rowH));
        var hovered = ImGui.IsItemHovered();
        var t = SpheneUI.Fx.HoverT(rowId, hovered);
        var profile = members?.GetProfile?.Invoke(member.Uid);
        var name = members?.GetDisplayName?.Invoke(member.Uid) ?? member.Alias ?? "Unknown user";
        var theme = profile?.ProfileTheme.Value ?? SpheneProfileTheme.Parse(null);
        var rowAccent = profile?.ProfileTheme.Value.AccentColor(member.RoleColor) ?? member.RoleColor;
        var avatar = profile != null && profile.ImageData.Value.Length > 0
            ? StaffAvatar(texturePrefix, member.Uid, profile.ImageData.Value)
            : null;
        var rowImage = profile != null && profile.RowImageData.Value.Length > 0
            ? StaffRowImage(texturePrefix, member.Uid, profile.RowImageData.Value)
            : null;
        if (hovered)
        {
            SpheneUI.AttachTooltip(member.Role);
            if (canOpen)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
        }
        // Held jobs ride the row's right edge like the member list's badge
        // cluster - right-aligned bare glyphs, names on hover. Reserve the
        // width up front so the name line ellipsizes left of them.
        var iconsW = 0f;
        if (member.Icons.Count > 0)
        {
            using (SpheneUI.Icons.PushFont())
            {
                foreach (var (icon, _, _) in member.Icons)
                {
                    iconsW += ImGui.CalcTextSize(icon.ToIconString()).X;
                }
            }
            iconsW += (member.Icons.Count - 1) * 6f * scale;
        }
        var rightReserve = iconsW > 0f ? iconsW + 22f * scale : 0f;

        SpheneUI.Profile.FriendRow(dl, pos, width, rowH,
            new SpheneUI.Profile.FriendRowContent(avatar, name, string.Empty,
                member.RoleColor, profile?.StatusMessage, profile?.IsNSFW ?? false,
                null, null,
                member.Badge is { } crown ? [crown] : null),
            rowAccent, hovered, t, theme, rowImage, 8f * scale,
            rightInset: rightReserve);

        if (iconsW > 0f)
        {
            var glyphH = ImGui.GetTextLineHeight();
            var gx = pos.X + width - 10f * scale - iconsW;
            foreach (var (icon, color, roleName) in member.Icons)
            {
                var glyph = icon.ToIconString();
                float glyphW;
                using (SpheneUI.Icons.PushFont())
                {
                    glyphW = ImGui.CalcTextSize(glyph).X;
                    dl.AddText(new Vector2(gx, pos.Y + (rowH - glyphH) * 0.5f),
                        SpheneUI.ColorUtils.ToU32(color), glyph);
                }
                ImGui.SetCursorScreenPos(new Vector2(gx - 2f * scale,
                    pos.Y + (rowH - glyphH) * 0.5f - 2f * scale));
                ImGui.InvisibleButton($"##rolebadge-{member.Uid}-{roleName}",
                    new Vector2(glyphW + 4f * scale, glyphH + 4f * scale));
                if (ImGui.IsItemHovered())
                {
                    SpheneUI.AttachTooltip(roleName);
                }
                gx += glyphW + 6f * scale;
            }
            ImGui.SetCursorScreenPos(pos);
        }
        if (canOpen && ImGui.IsItemClicked())
        {
            members!.OpenProfile!.Invoke(member.Uid);
        }
        // Right-click · compact profile preview flyout (same convention as
        // the pair-list profile popout).
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            SpheneUI.Popups.ToggleFlyout($"person:{member.Uid}");
        }
        if (SpheneUI.Popups.BeginFlyout($"person:{member.Uid}"))
        {
            PersonCard.Draw($"pc:{member.Uid}", name, profile, avatar, null,
                rowAccent,
                openProfile: canOpen ? () => members!.OpenProfile!.Invoke(member.Uid) : null);
            SpheneUI.Popups.EndFlyout();
        }
        ImGui.SetCursorScreenPos(pos + new Vector2(0f, rowH + rowGap));
    }

    /// <summary>
    /// Horizontal image rail shared by the overview teaser and the gallery
    /// page · caption scrims, hover border, optional click.
    /// </summary>
    private static void DrawGalleryRail(List<SyncshellGalleryImageDto> images, string texturePrefix,
        string gid, float tileH, float tileW, float? width = null, Action<int>? onClick = null)
    {
        var scale = SpheneUI.Scale;
        var gap = SpheneUI.Spacing.X8.Scaled;
        using (ImRaii.Child($"community-gallery-rail-{gid}",
                   new Vector2(width ?? 0f, tileH + 22f * scale), false,
                   ImGuiWindowFlags.HorizontalScrollbar))
        {
            var dl = ImGui.GetWindowDrawList();
            for (var i = 0; i < images.Count; i++)
            {
                if (i > 0)
                {
                    ImGui.SameLine(0f, gap);
                }
                var img = images[i];
                var wrap = SpheneUI.Media.Texture($"{texturePrefix}gallery:{gid}:{img.Id}",
                    ResolveBytes(img.ImageMediaHash, img.ImageBase64));
                var min = ImGui.GetCursorScreenPos();
                var max = min + new Vector2(tileW, tileH);
                if (wrap != null)
                {
                    SpheneUI.Media.ImageInRect(dl, wrap, min, max, ImageFit.Cover,
                        rounding: 8f * scale);
                    dl.AddRect(min, max,
                        SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Border with { W = 0.25f }), 8f * scale);
                    if (img.Audience != Sphene.API.Data.Enum.SyncshellGalleryAudience.Everyone)
                    {
                        SyncshellCard.StatusPill(dl, min + new Vector2(6f * scale, 6f * scale),
                            img.Audience == Sphene.API.Data.Enum.SyncshellGalleryAudience.StaffOnly
                                ? "Staff" : "Selected",
                            SpheneUI.Palette.SpheneGold, dot: false, scale);
                    }
                    if (img.LikeCount > 0)
                    {
                        DrawLikeBadge(dl, max - new Vector2(6f * scale, 6f * scale),
                            img.LikeCount, img.LikedByMe, scale);
                    }
                }
                ImGui.Dummy(new Vector2(tileW, tileH));
                if (onClick != null && ImGui.IsItemClicked())
                {
                    onClick(i);
                }
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    /// <summary>
    /// Venue hero: the banner spans the full window width · with
    /// <paramref name="heroBleed"/> it also covers the title bar so chrome and
    /// cover merge · and the identity block sits on the scrim itself instead of
    /// eating a second block of vertical space below it.
    /// </summary>
    private static void DrawHeader(SyncshellProfileDto profile, GroupFullInfoDto? group,
        string texturePrefix, Vector4 accent, string shellName, float scale,
        SyncshellCommunityEdit? edit, bool listed, int followerCount,
        (Vector2 Min, Vector2 Size)? heroBleed, CommunityPageState? page)
    {
        var banner = profile.BannerMediaHash == null && string.IsNullOrEmpty(profile.BannerImageBase64)
            ? null
            : SpheneUI.Media.Texture($"{texturePrefix}banner:{profile.GroupGID}:{MediaVersion(profile.BannerMediaHash, profile.BannerImageBase64)}",
                ResolveBytes(profile.BannerMediaHash, profile.BannerImageBase64));
        var editing = edit?.CanEditIdentity == true;
        var dl = ImGui.GetWindowDrawList();
        var pad = 12f * scale;
        var density = SpheneUI.Density.Factor;
        var bannerH = 150f * scale * density;
        var avatarSize = 56f * scale * density;
        var contentMin = ImGui.GetCursorScreenPos();

        DrawHeroChrome();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        void DrawHeroChrome()
        {
            var heroMin = heroBleed?.Min ?? new Vector2(ImGui.GetWindowPos().X, contentMin.Y);
            // The bleed hero anchors to the window top only at scroll zero  · 
            // subtract the child scroll so the whole page moves as one
            // surface. Pinning it to the absolute window rect would freeze
            // every item placed from these coordinates (absolute cursor
            // positions ignore scroll) and leave ScrollMax unreachable.
            if (heroBleed.HasValue)
            {
                heroMin.Y -= ImGui.GetScrollY();
            }
            var heroW = heroBleed?.Size.X ?? ImGui.GetWindowSize().X;
            var bleedH = MathF.Max(0f, contentMin.Y - heroMin.Y);
            var heroMax = heroMin + new Vector2(heroW, bleedH + bannerH);

            // Bleed heroes cover the window's side borders like the title
            // bar does · no translucent strips at the edges. Top corners
            // follow the window rounding.
            var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
            var rounding = heroBleed.HasValue
                ? ImGui.GetStyle().WindowRounding
                : 6f * scale;
            var imgMin = heroBleed.HasValue
                ? heroMin
                : heroMin + new Vector2(borderSize, borderSize);
            var imgMax = heroBleed.HasValue
                ? heroMax
                : new Vector2(heroMax.X - borderSize, heroMax.Y);
            var corners = heroBleed.HasValue
                ? ImDrawFlags.RoundCornersTop
                : ImDrawFlags.RoundCornersAll;

            // Everything in the bleed band must be drawn inside the fullscreen
            // clip span · the scroll child's clip rect would cut it otherwise.
            if (heroBleed.HasValue)
            {
                dl.PushClipRectFullScreen();
            }
            if (banner != null)
            {
                SpheneUI.Media.ImageInRect(dl, banner, imgMin, imgMax, ImageFit.Cover,
                    rounding: rounding, corners: corners);
            }
            else
            {
                DrawDefaultBanner(dl, imgMin, imgMax, accent, rounding, corners);
            }
            SyncshellCard.BottomScrim(dl, imgMin, imgMax, 0.62f, rounding,
                corners & ImDrawFlags.RoundCornersBottom);
            DrawStatusPills(dl, imgMin + new Vector2(pad,
                    bleedH + 6f * scale + (heroBleed.HasValue ? borderSize : 0f)),
                group, listed, scale, edit);
            if (heroBleed.HasValue)
            {
                dl.PopClipRect();
            }

            // The banner edit zone stops above the identity strip · avatar,
            // name input and pills live there and must stay clickable. While
            // the listing toggle is live it also starts below the pill row so
            // its button can take the hover there.
            var identityTop = heroMax.Y - pad - avatarSize;
            if (editing)
            {
                var hitMin = new Vector2(heroMin.X, heroMin.Y + bleedH);
                if (edit is { CanManagePage: true, Ops.Patch: not null })
                {
                    hitMin.Y = imgMin.Y + pad + bleedH + 6f * scale
                        + (heroBleed.HasValue ? borderSize : 0f)
                        + ImGui.GetTextLineHeight() + 6f * scale;
                }
                var hitH = MathF.Max(20f * scale, identityTop - hitMin.Y);
                ImGui.SetCursorScreenPos(hitMin);
                if (ImGui.InvisibleButton("##community-banner-edit", new Vector2(heroW, hitH)))
                {
                    SpheneUI.Popups.ToggleFlyout("community-banner-menu");
                }
                if (ImGui.IsItemHovered())
                {
                    DrawEditBadge(heroMax - new Vector2(26f * scale, bannerH * 0.45f), scale,
                        "Change banner");
                }
                DrawImageActionFlyout("community-banner-menu",
                    (!string.IsNullOrEmpty(profile.BannerImageBase64) || profile.BannerMediaHash != null), "banner", edit!,
                    edit!.OnBannerClicked, banner: true);
            }

            DrawIdentity(profile, group, texturePrefix, accent, shellName, scale, edit, followerCount,
                new Vector2(heroMin.X + pad, identityTop),
                heroMin.X + pad + avatarSize + 10f * scale, heroMax.X - pad, avatarSize,
                onBanner: true, page: page);

            ImGui.SetCursorScreenPos(new Vector2(contentMin.X, heroMax.Y + 6f * scale));
        }
    }

    /// <summary>
    /// Designed fallback for shells without a banner image · a diagonal
    /// accent gradient with soft sheen bands and the Sphene logo watermark
    /// on the right, so the hero keeps its full height and still reads as a
    /// branded surface instead of collapsing to an empty row.
    /// </summary>
    private static void DrawDefaultBanner(ImDrawListPtr dl, Vector2 min, Vector2 max,
        Vector4 accent, float rounding, ImDrawFlags corners)
    {
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        var top = new Vector4(accent.X * 0.52f, accent.Y * 0.52f, accent.Z * 0.62f, 1f);
        var bottom = new Vector4(accent.X * 0.14f, accent.Y * 0.14f, accent.Z * 0.18f, 1f);
        SpheneUI.Media.FillRoundedGradient(dl, min, max, top, top, bottom, bottom,
            rounding, corners);
        // Inset the overlay clip so sheen bands and the watermark can never
        // paint into the corner arcs the rounded gradient left open.
        var overlayInset = new Vector2(rounding * 0.4f, rounding * 0.4f);
        dl.PushClipRect(min + overlayInset, max - overlayInset, true);
        var sheen = SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.05f));
        for (var i = 0; i < 3; i++)
        {
            var bx = min.X + w * (0.18f + 0.24f * i);
            var bw = w * 0.07f;
            dl.AddQuadFilled(
                new Vector2(bx, min.Y), new Vector2(bx + bw, min.Y),
                new Vector2(bx + bw + h * 0.4f, max.Y), new Vector2(bx + h * 0.4f, max.Y),
                sheen);
        }

        var logo = SpheneBrandIcons.SpheneLogo();
        if (logo != null)
        {
            var logoH = h * 1.35f;
            var logoW = logoH * ((float)logo.Width / logo.Height);
            var center = new Vector2(min.X + w * 0.74f, min.Y + h * 0.55f);
            var lMin = center - new Vector2(logoW, logoH) * 0.5f;
            var lMax = center + new Vector2(logoW, logoH) * 0.5f;
            SpheneUI.Media.ImageInRect(dl, logo, lMin, lMax, ImageFit.Fit,
                tint: new Vector4(1f, 1f, 1f, 0.16f));
        }
        dl.PopClipRect();
    }

    /// <summary>
    /// Avatar plus name/tagline/meta · drawn at absolute positions so it can sit
    /// on the banner scrim or in normal flow. Returns the block's bottom Y.
    /// </summary>
    private static void DrawIdentity(SyncshellProfileDto profile, GroupFullInfoDto? group,
        string texturePrefix, Vector4 accent, string shellName, float scale,
        SyncshellCommunityEdit? edit, int followerCount, Vector2 avatarMin, float textX,
        float rightEdge, float avatarSize, bool onBanner, CommunityPageState? page)
    {
        var avatar = profile.AvatarMediaHash == null && string.IsNullOrEmpty(profile.AvatarImageBase64)
            ? null
            : SpheneUI.Media.Texture($"{texturePrefix}avatar:{profile.GroupGID}:{MediaVersion(profile.AvatarMediaHash, profile.AvatarImageBase64)}",
                ResolveBytes(profile.AvatarMediaHash, profile.AvatarImageBase64));
        var canIdentity = edit?.CanEditIdentity == true;
        var lineH = ImGui.GetTextLineHeight();
        var metaColor = onBanner
            ? new Vector4(1f, 1f, 1f, 0.72f)
            : SpheneUI.Colors.TextMuted;

        ImGui.SetCursorScreenPos(avatarMin);
        SpheneUI.Media.Avatar(avatar, avatarSize, placeholderIcon: FontAwesomeIcon.Users);
        if (canIdentity)
        {
            ImGui.SetCursorScreenPos(avatarMin);
            if (ImGui.InvisibleButton("##community-avatar-edit", new Vector2(avatarSize, avatarSize)))
            {
                SpheneUI.Popups.ToggleFlyout("community-avatar-menu");
            }
            if (ImGui.IsItemHovered())
            {
                DrawEditBadge(avatarMin + new Vector2(avatarSize, 0f) - new Vector2(8f * scale, 0f),
                    scale, "Change avatar");
            }
            DrawImageActionFlyout("community-avatar-menu",
                (!string.IsNullOrEmpty(profile.AvatarImageBase64) || profile.AvatarMediaHash != null), "avatar", edit!,
                edit!.OnAvatarClicked, banner: false);
        }

        var hasTagline = canIdentity || !string.IsNullOrWhiteSpace(profile.Tagline);
        // Rough vertical centering · the exact heights are measured from the
        // drawn items below so the display font can never overlap its neighbours.
        var blockTop = avatarMin.Y + MathF.Max(0f,
            (avatarSize - lineH * (hasTagline ? 3.2f : 2.3f)) * 0.5f);
        var textW = rightEdge - textX;

        ImGui.SetCursorScreenPos(new Vector2(textX, blockTop));
        var canEditName = canIdentity && page != null && edit!.OnNameCommitted != null;
        float rowBottom;
        if (canEditName && page!.EditingName)
        {
            // In-place rename · same bare-input language as the profile hero:
            // the big font stays, an accent underline marks the live edit.
            if (page.NameFocusPending)
            {
                ImGui.SetKeyboardFocusHere();
                page.NameFocusPending = false;
            }
            var committed = SpheneUI.Profile.DrawInPlaceInput("##community-name",
                "Syncshell name · Enter applies", page.NameBuffer, 50,
                ImGuiInputTextFlags.EnterReturnsTrue,
                MathF.Max(220f * scale, MathF.Min(textW, 420f * scale)),
                accent, scale, out var nameBuf, out var nameCancelled,
                font: SpheneUI.UidFont);
            page.NameBuffer = nameBuf;
            if (nameCancelled)
            {
                page.EditingName = false;
            }
            else if (committed)
            {
                page.EditingName = false;
                var trimmed = nameBuf.Trim();
                // The alias is required · an empty commit would silently fall
                // back to the generated GID, so only a valid name applies.
                if (trimmed.Length >= 3
                    && !string.Equals(trimmed, shellName, StringComparison.Ordinal))
                {
                    edit!.OnNameCommitted!.Invoke(trimmed);
                }
            }
            rowBottom = ImGui.GetItemRectMax().Y;
        }
        else
        {
            string nameShown;
            if (SpheneUI.UidFont is { } bigFont)
            {
                using (bigFont.Push())
                {
                    nameShown = SpheneUI.Text.Ellipsize(shellName, textW);
                }
            }
            else
            {
                nameShown = SpheneUI.Text.Ellipsize(shellName, textW);
            }
            SpheneUI.Text.Big(nameShown);
            if (nameShown.Length != shellName.Length)
            {
                SpheneUI.AttachTooltip(shellName);
            }
            if (canEditName)
            {
                var nameClicked = ImGui.IsItemClicked();
                var pen = SpheneUI.Profile.DrawEditPen(ImGui.GetItemRectSize().Y,
                    accent, scale);
                SpheneUI.AttachTooltip("Rename the syncshell");
                if (nameClicked || pen.Clicked)
                {
                    page!.NameBuffer = shellName;
                    page.EditingName = true;
                    page.NameFocusPending = true;
                }
            }
            rowBottom = ImGui.GetItemRectMax().Y;
        }

        // The display font is taller than the line-height estimate · every
        // following line anchors to the previous row's real item bottom or
        // the tagline lands inside the name's glyphs.
        var tagY = rowBottom + 2f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textX, tagY));
        var canEditTagline = canIdentity && page != null && edit!.OnTaglineCommitted != null;
        if (canEditTagline && page!.EditingTagline)
        {
            var tagline = page.TaglineBuffer;
            ImGui.SetNextItemWidth(MathF.Max(220f * scale, MathF.Min(textW, 360f * scale)));
            if (page.TaglineFocusPending)
            {
                ImGui.SetKeyboardFocusHere();
                page.TaglineFocusPending = false;
            }
            if (SpheneUI.Controls.InputTextWithHint("##community-tagline-edit", "Tagline · shown under the name",
                    ref tagline, 160, ImGuiInputTextFlags.EnterReturnsTrue)
                || ImGui.IsItemDeactivatedAfterEdit())
            {
                page.EditingTagline = false;
                edit!.OnTaglineCommitted?.Invoke(tagline);
            }
            else
            {
                page.TaglineBuffer = tagline;
            }
            rowBottom = ImGui.GetItemRectMax().Y;
        }
        else if (!string.IsNullOrWhiteSpace(profile.Tagline) || canIdentity)
        {
            var shownTag = string.IsNullOrWhiteSpace(profile.Tagline)
                ? "Tagline · click the pen to add one"
                : SpheneUI.Text.Ellipsize(profile.Tagline, textW);
            var tagColor = string.IsNullOrWhiteSpace(profile.Tagline)
                ? SpheneUI.ColorUtils.WithAlpha(metaColor, 0.6f)
                : accent;
            var tagW = canEditTagline ? textW - 22f * scale : textW;
            shownTag = string.IsNullOrWhiteSpace(profile.Tagline) ? shownTag
                : SpheneUI.Text.Ellipsize(profile.Tagline, tagW);
            SpheneUI.Text.Colored(shownTag, tagColor);
            if (!string.IsNullOrEmpty(profile.Tagline) && shownTag.Length != profile.Tagline.Length)
            {
                SpheneUI.AttachTooltip(profile.Tagline);
            }
            if (canEditTagline)
            {
                var tagClicked = ImGui.IsItemClicked();
                ImGui.SameLine(0f, 4f * scale);
                using (SpheneUI.Icons.PushFont())
                {
                    SpheneUI.Text.Colored(FontAwesomeIcon.Pen.ToIconString(), metaColor);
                }
                SpheneUI.AttachTooltip("Edit the tagline");
                if (tagClicked || ImGui.IsItemClicked())
                {
                    page!.TaglineBuffer = profile.Tagline ?? string.Empty;
                    page.EditingTagline = true;
                    page.TaglineFocusPending = true;
                }
            }
            rowBottom = ImGui.GetItemRectMax().Y;
        }

        // Manage pill docks on the meta line's right edge · its width is
        // reserved here so the meta text never runs underneath it.
        var canManagePage = edit is { CanManagePage: true, Ops: not null } && page != null;
        var dockGap = 6f * scale;
        var dockPillW = canManagePage
            ? SpheneUI.Controls.SmallButtonWidth("Manage")
            : 0f;
        var metaAvail = textW - dockPillW - (dockPillW > 0f ? dockGap : 0f);

        var metaY = rowBottom + 2f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textX, metaY));
        // Authoritative total when the server sends it - the local privileged
        // roster count is only a fallback for old servers, not a member count.
        var memberCount = group is { MemberCount: >= 0 } g
            ? g.MemberCount
            : group?.GroupPairUserInfos?.Count ?? 0;
        var meta = string.Join(" · ", new[]
            {
                group != null ? $"Led by {HumanName(group.Owner) ?? "Unknown user"}" : null,
                memberCount > 0 ? $"{memberCount} members" : null,
                followerCount > 0 ? $"{followerCount} followers" : null,
                profile.CreatedAt > DateTime.UnixEpoch ? $"Since {profile.CreatedAt:d}" : null,
            }.Where(s => !string.IsNullOrEmpty(s)));
        if (!string.IsNullOrEmpty(meta))
        {
            var shownMeta = SpheneUI.Text.Ellipsize(meta, metaAvail);
            SpheneUI.Text.Colored(shownMeta, metaColor);
            if (shownMeta.Length != meta.Length)
            {
                SpheneUI.AttachTooltip(meta);
            }
        }
        // The identity strip deliberately excludes the banner hit-zone so the
        // pill stays clickable while the banner itself is a change-banner target.
        if (canManagePage)
        {
            ImGui.SetCursorScreenPos(new Vector2(rightEdge - dockPillW, metaY));
            if (SpheneUI.Controls.SmallButton("Manage"))
            {
                page!.Subpage = CommunitySubpage.Settings;
            }
            if (canManagePage)
            {
                SpheneUI.AttachTooltip("Page settings · activation, sections, accent, layout");
            }
        }
    }

    /// <summary>
    /// Window-chrome controls (close, fold) pinned to the hero's top edge.
    /// Drawn after the page content so the scroll can't overdraw them, and
    /// anchored to the window rect · absolute cursor positions ignore scroll,
    /// which is exactly what keeps the strip fixed while the hero rides away.
    /// </summary>
    private static void DrawHeroChrome((Vector2 Min, Vector2 Size)? heroBleed, Action? onClose,
        Action? onFold, bool foldCollapsed, float scale,
        Action? onPreviewToggle = null, bool previewActive = false)
    {
        if (!heroBleed.HasValue || (onClose == null && onFold == null && onPreviewToggle == null))
        {
            return;
        }
        var size = ImGui.GetFrameHeight() + 8f * scale;
        var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
        var pad = 12f * scale;
        var centerY = heroBleed.Value.Min.Y + borderSize + pad + size * 0.5f;
        var rightX = heroBleed.Value.Min.X + heroBleed.Value.Size.X - pad;
        if (onClose != null)
        {
            rightX -= size;
            ImGui.SetCursorScreenPos(new Vector2(rightX, centerY - size * 0.5f));
            if (ChromeChipButton(FontAwesomeIcon.Times, "Close", scale, size))
            {
                onClose();
            }
            rightX -= 8f * scale;
        }
        if (onFold != null)
        {
            rightX -= size;
            ImGui.SetCursorScreenPos(new Vector2(rightX, centerY - size * 0.5f));
            if (ChromeChipButton(
                    foldCollapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                    foldCollapsed ? "Expand" : "Collapse", scale, size))
            {
                onFold();
            }
            rightX -= 8f * scale;
        }
        // Owner surfaces get a subtle eye chip left of the fold: it flips the
        // page into the exact visitor rendering (no manage affordances, no
        // hidden tabs) without touching anything on the server.
        if (onPreviewToggle != null)
        {
            rightX -= size;
            ImGui.SetCursorScreenPos(new Vector2(rightX, centerY - size * 0.5f));
            if (ChromeChipButton(
                    previewActive ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
                    previewActive
                        ? "Visitor preview · click to return to your owner view"
                        : "Preview as visitor · hides all owner tools", scale, size))
            {
                onPreviewToggle();
            }
        }
    }

    /// <summary>
    /// Dark glass chip icon button for controls that float over imagery  · 
    /// hero chrome (close/fold) and folded strips. A translucent disc plus
    /// hairline keeps the glyph readable on bright banner colors.
    /// The caller positions the cursor at the chip's top-left.
    /// </summary>
    internal static bool ChromeChipButton(FontAwesomeIcon icon, string tooltip, float scale,
        float? size = null)
    {
        var s = size ?? ImGui.GetFrameHeight() + 6f * scale;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##chip-{icon}-{(int)pos.X}-{(int)pos.Y}",
            new Vector2(s, s));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var center = pos + new Vector2(s * 0.5f);
        var bg = SpheneUI.ColorUtils.WithAlpha(new Vector4(0.03f, 0.03f, 0.05f, 1f),
            hovered ? 0.92f : 0.66f);
        dl.AddCircleFilled(center, s * 0.5f, SpheneUI.ColorUtils.ToU32(bg));
        var line = SpheneUI.ColorUtils.WithAlpha(Vector4.One, hovered ? 0.32f : 0.15f);
        dl.AddCircle(center, s * 0.5f, SpheneUI.ColorUtils.ToU32(line), 0,
            MathF.Max(1f, scale));
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            dl.AddText(center - glyphSize * 0.5f, ImGui.GetColorU32(ImGuiCol.Text), glyph);
        }
        if (hovered)
        {
            SpheneUI.AttachTooltip(tooltip);
        }
        return clicked;
    }

    /// <summary>
    /// Geometry for the pinned collapsing header: the expanded hero occupies
    /// <see cref="FullHeroH"/> and retracts into a <see cref="CompactH"/>
    /// strip while the scroll consumes a matching spacer inside the child  · 
    /// the tab bar slides up with the shrinking edge and docks underneath.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private struct PinnedHeaderGeom
    {
        public float PadY;
        public float FullHeroH;
        public float CompactH;
        public float ExpandH;
        public float TabsH;
        public float Spacer;
    }

    /// <summary>
    /// Measures the collapsed-header geometry for the current window. The
    /// spacer inside the scroll child reserves expanded-header room so
    /// content begins exactly under the docked tabs at scroll zero.
    /// </summary>
    private static PinnedHeaderGeom ComputePinnedGeom(Vector2 winPos, bool hasBanner, float scale)
    {
        var padY = ImGui.GetWindowPos().Y - winPos.Y;
        var fullHeroH = padY + (hasBanner ? 150f * scale : 84f * scale);
        var compactH = ImGui.GetFrameHeight() + 14f * scale;
        var expandH = MathF.Max(0f, fullHeroH - compactH);
        var tabsH = ImGui.GetFrameHeight() + 12f * scale;
        return new PinnedHeaderGeom
        {
            PadY = padY,
            FullHeroH = fullHeroH,
            CompactH = compactH,
            ExpandH = expandH,
            TabsH = tabsH,
            Spacer = fullHeroH + tabsH - padY,
        };
    }

    /// <summary>
    /// Owns the pinned zone as its own input-taking child window, parked
    /// exactly over the hero apron + docked tabs on the host window. The
    /// scroll child cannot host interactive items at absolute positions  · 
    /// its clip and hover context belong to the scrolled content · and the
    /// host window loses hover to any child covering the mouse, so the
    /// strip gets a real window of its own. Wheel over the hero forwards to
    /// the page through <see cref="CommunityPageState.ScrollNudge"/>.
    /// </summary>
    private static void DrawPinnedOverlay(SyncshellProfileDto profile, GroupFullInfoDto? group,
        string texturePrefix, Vector4 accent, string shellName, float scale,
        SyncshellCommunityEdit? edit, bool listed, int followerCount,
        (Vector2 Min, Vector2 Size) bleed,
        PinnedHeaderGeom geom, CommunityPageState page,
        List<(SyncshellCommunitySection? Section, SpheneUI.Controls.TabItem Item)> tabs,
        int selIdx, float contentX, float scrollY,
        Action? onClose, Action? onFold, bool foldCollapsed,
        Action? onPreviewToggle, bool previewActive, bool standalone)
    {
        var heroExtra = MathF.Max(0f, geom.ExpandH - scrollY);
        var apronH = geom.CompactH + heroExtra;
        var zoneH = apronH + geom.TabsH;
        var childFlags = ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoSavedSettings;
        ImGui.SetCursorScreenPos(bleed.Min);
        ImGui.BeginChild("##community-pinned", new Vector2(bleed.Size.X, zoneH), false,
            childFlags);
        try
        {
            DrawPinnedHeader(profile, group, texturePrefix, accent, shellName, scale,
                edit, listed, followerCount, bleed, geom, page, scrollY, standalone);
            DrawHeroChrome(bleed, onClose, onFold, foldCollapsed, scale,
                onPreviewToggle, previewActive);
            if (ImGui.IsWindowHovered() && MathF.Abs(ImGui.GetIO().MouseWheel) > 0.001f)
            {
                page.ScrollNudge = ImGui.GetIO().MouseWheel;
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        // The tab bar gets its own overlay child created after the header
        // child - hover resolves topmost-window-first, so no apron item
        // (invisible edit hit zones in owner mode) can ever swallow its
        // clicks again, regardless of how their rects drift.
        var tabsY = bleed.Min.Y + apronH;
        ImGui.SetCursorScreenPos(new Vector2(contentX, tabsY));
        ImGui.BeginChild("##community-tabs",
            new Vector2(bleed.Min.X + bleed.Size.X - contentX, geom.TabsH), false,
            childFlags);
        try
        {
            if (tabs.Count > 1)
            {
                ImGui.SetCursorScreenPos(new Vector2(contentX, tabsY));
                var hit = SpheneUI.Controls.TabBar("community", page.TabItems, selIdx, accent);
                if (hit >= 0)
                {
                    page.CloseViewer();
                    page.Tab = tabs[hit].Section;
                    page.EventId = null;
                    page.Subpage = CommunitySubpage.None;
                    page.SwitchT = 0f;
                }
            }
            if (ImGui.IsWindowHovered() && MathF.Abs(ImGui.GetIO().MouseWheel) > 0.001f)
            {
                page.ScrollNudge = ImGui.GetIO().MouseWheel;
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// The photo lightbox · a child of the HOST window parked over the full
    /// content bleed, created after every sibling overlay (pinned header,
    /// tab strip, hero chrome). Window order makes it the topmost child, so
    /// its Close/Pop Out chrome is always visible and clickable - drawing it
    /// inside the scroll child let the pinned overlays cover that strip.
    /// Being a child it stays inside the host: the host can never sort in
    /// front, stays movable, and no other window is dimmed or blocked.
    /// </summary>
    private static void DrawCommunityViewerOverlay(SyncshellCommunityDto community,
        SyncshellProfileDto profile, string texturePrefix, float scale,
        SyncshellCommunityEdit? edit, CommunityPageState page,
        (Vector2 Min, Vector2 Size) bleed,
        Action<List<SyncshellGalleryImageDto>, int>? onViewerPopout,
        Func<SyncshellGalleryImageDto, Task<SyncshellGalleryImageDto?>>? onLikeImage)
    {
        if (page.ViewerImages is not { Count: > 0 } viewerImgs)
        {
            return;
        }
        var viewerId = $"##community-viewer-{profile.GroupGID}";
        if (page.ViewerOpenT <= 0f)
        {
            // Every open path resets OpenT to 0 - the edge logs exactly once
            // per open instead of per frame.
            SpheneUI.LogDebug("[CommUi] viewer open gid={Gid} count={Count}", profile.GroupGID, viewerImgs.Count);
        }
        page.ViewerOpenT = MathF.Min(1f, page.ViewerOpenT + ImGui.GetIO().DeltaTime / 0.2f);
        ImGui.SetCursorScreenPos(bleed.Min);
        ImGui.BeginChild(viewerId, bleed.Size, false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground);
        // EndChild must run even when the viewer draw throws - a skipped end
        // leaves the child on the stack: an invisible overlay that keeps
        // swallowing input until ImGui resets.
        try
        {
            var canEditPhoto = edit is { CanManageGallery: true, Ops.SaveImage: not null };
            var keepOpen = SyncshellImageViewer.Draw(viewerImgs, page.ViewerIndex,
                texturePrefix, profile.GroupGID, closeOnBackdrop: true,
                setIndex: i => page.ViewerIndex = i,
                onClose: () =>
                {
                    SpheneUI.LogDebug("[CommUi] viewer closed");
                    page.ViewerImages = null;
                    page.ViewerEditOpen = false;
                    page.ViewerEditImgId = -1;
                    page.ViewerEditError = null;
                },
                onPopOut: onViewerPopout == null ? null : () =>
                {
                    SpheneUI.LogDebug("[CommUi] viewer popped out");
                    onViewerPopout(viewerImgs, page.ViewerIndex);
                    page.ViewerImages = null;
                    page.ViewerEditOpen = false;
                    page.ViewerEditImgId = -1;
                },
                openT: page.ViewerOpenT,
                onLike: onLikeImage == null ? null
                    : i => _ = ToggleViewerLike(page, i, onLikeImage),
                editOpen: canEditPhoto && page.ViewerEditOpen,
                onEditToggle: canEditPhoto
                    ? () =>
                    {
                        page.ViewerEditOpen = !page.ViewerEditOpen;
                        page.ViewerEditImgId = -1;
                    }
                    : null,
                drawEditor: canEditPhoto
                    ? (img, min, w) => DrawCommunityPhotoEditor(img, min, w, community, edit!, page)
                    : null,
                editHeight: canEditPhoto ? CommunityPhotoEditorHeight(page, scale) : 0f);
            if (!keepOpen)
            {
                // Backdrop click or Escape handed the close back through the
                // draw return.
                SpheneUI.LogDebug("[CommUi] viewer close via keepOpen");
                page.ViewerImages = null;
                page.ViewerEditOpen = false;
                page.ViewerEditImgId = -1;
                page.ViewerEditError = null;
            }
        }
        catch (Exception ex)
        {
            // A broken viewer must close, not linger invisible while it eats
            // input - the catch keeps the page alive and the log carries the
            // cause.
            SpheneUI.LogError(ex, "Community viewer draw failed");
            page.ViewerImages = null;
            page.ViewerEditOpen = false;
            page.ViewerEditImgId = -1;
            page.ViewerEditError = null;
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// Slim variant for surfaces that only need the hero chrome (no pinned
    /// collapsing header): a small input-taking child covering the chip strip.
    /// </summary>
    private static void DrawChromeOverlay((Vector2 Min, Vector2 Size)? heroBleed, Action? onClose,
        Action? onFold, bool foldCollapsed, float scale,
        Action? onPreviewToggle, bool previewActive)
    {
        if (!heroBleed.HasValue)
        {
            return;
        }
        var chip = ImGui.GetFrameHeight() + 8f * scale;
        var pad = 12f * scale;
        var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
        var stripH = borderSize + pad + chip + pad;
        ImGui.SetCursorScreenPos(heroBleed.Value.Min);
        ImGui.BeginChild("##community-chrome", new Vector2(heroBleed.Value.Size.X, stripH), false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoSavedSettings);
        try
        {
            DrawHeroChrome(heroBleed, onClose, onFold, foldCollapsed, scale,
                onPreviewToggle, previewActive);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// The pinned collapsing header drawn over the scrolled page: an opaque
    /// band covers the pinned zone, the banner apron retracts toward a
    /// compact strip as scroll consumes the spacer, the expanded identity
    /// clips under the shrinking edge, the compact row (avatar, name,
    /// pills) fades in, and the tab bar rides the apron's bottom edge.
    /// </summary>
    private static void DrawPinnedHeader(SyncshellProfileDto profile, GroupFullInfoDto? group,
        string texturePrefix, Vector4 accent, string shellName, float scale,
        SyncshellCommunityEdit? edit, bool listed, int followerCount,
        (Vector2 Min, Vector2 Size) bleed,
        PinnedHeaderGeom geom, CommunityPageState page, float scrollY, bool standalone)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = bleed.Min;
        var winW = bleed.Size.X;
        var heroExtra = MathF.Max(0f, geom.ExpandH - scrollY);
        var apronH = geom.CompactH + heroExtra;
        var collapseT = geom.ExpandH > 0.01f
            ? Math.Clamp(scrollY / geom.ExpandH, 0f, 1f)
            : 1f;
        var pad = 12f * scale;
        var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
        var apronMin = pos;
        var apronMax = new Vector2(pos.X + winW, pos.Y + apronH);
        var tabsY = apronMax.Y;

        var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        var opaqueBg = SpheneUI.ColorUtils.ToU32(new Vector4(bg.X, bg.Y, bg.Z, 1f));

        dl.PushClipRectFullScreen();
        // Opaque band behind apron + tabs · scrolled content never shows
        // through the pinned zone regardless of the page underneath. Only
        // the standalone window rounds its top corners · embedded surfaces
        // park under a flat rail.
        var rounding = standalone ? ImGui.GetStyle().WindowRounding : 0f;
        dl.AddRectFilled(apronMin, new Vector2(apronMax.X, tabsY + geom.TabsH), opaqueBg,
            rounding, ImDrawFlags.RoundCornersTop);

        var banner = profile.BannerMediaHash == null && string.IsNullOrEmpty(profile.BannerImageBase64)
            ? null
            : SpheneUI.Media.Texture($"{texturePrefix}banner:{profile.GroupGID}:{MediaVersion(profile.BannerMediaHash, profile.BannerImageBase64)}",
                ResolveBytes(profile.BannerMediaHash, profile.BannerImageBase64));
        if (banner != null)
        {
            SpheneUI.Media.ImageInRect(dl, banner, apronMin, apronMax, ImageFit.Cover,
                rounding: rounding, corners: ImDrawFlags.RoundCornersTop);
            SyncshellCard.BottomScrim(dl, apronMin, apronMax, 0.62f);
        }

        // Expanded and compact content crossfade on the collapse: the
        // identity text is gone before the compact strip fades in over the
        // same zone - without the fade both sets of text draw on top of
        // each other while the apron shrinks.
        var compactA = Math.Clamp((collapseT - 0.55f) / 0.35f, 0f, 1f);
        var expandA = 1f - Math.Clamp((collapseT - 0.1f) / 0.45f, 0f, 1f);

        // Expanded hero content · the apron clip retracts it under the
        // shrinking edge like a folding toolbar.
        var avatarSize = 56f * scale * SpheneUI.Density.Factor;
        dl.PushClipRect(apronMin, apronMax, false);
        if (expandA > 0.01f)
        {
            // Pills draw straight to the list and ignore style alpha · they
            // stay until the compact strip claims the row, then drop.
            if (compactA <= 0.01f)
            {
                DrawStatusPills(dl,
                    new Vector2(apronMin.X + pad, pos.Y + geom.PadY + 6f * scale + borderSize),
                    group, listed, scale, edit);
            }
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, expandA);
            // The identity block anchors to the avatar's bottom edge · lift
            // it by a fraction of a line so the three text lines (name,
            // tagline, meta) always clear the apron's clip edge instead of
            // shaving pixels.
            var identityTop = apronMax.Y - pad - avatarSize
                - ImGui.GetTextLineHeight() * 0.6f;
            DrawIdentity(profile, group, texturePrefix, accent, shellName, scale, edit, followerCount,
                new Vector2(apronMin.X + pad, identityTop),
                apronMin.X + pad + avatarSize + 10f * scale, apronMax.X - pad,
                avatarSize, onBanner: banner != null, page: page);
            // The banner edit zone must exist with or without a banner -
            // otherwise a shell whose image is missing (or never set) loses
            // its only upload affordance.
            if (edit?.CanEditIdentity == true)
            {
                var hitMin = new Vector2(apronMin.X, pos.Y + geom.PadY);
                // The clickable listing pill sits on this strip · keep the
                // banner hit zone below it so the pill can take its hover.
                if (edit.CanManagePage && edit.Ops?.Patch != null)
                {
                    hitMin.Y = pos.Y + geom.PadY + 6f * scale + borderSize
                        + ImGui.GetTextLineHeight() + 6f * scale;
                }
                // The hit zone covers the banner strip above the
                // identity · clamped to the apron edge so a collapsed
                // header can never park an invisible button over the
                // docked tab bar and eat its clicks.
                var hitH = MathF.Min(identityTop, apronMax.Y) - hitMin.Y;
                if (hitH >= 8f * scale)
                {
                    ImGui.SetCursorScreenPos(hitMin);
                    if (ImGui.InvisibleButton("##community-banner-edit", new Vector2(winW, hitH)))
                    {
                        SpheneUI.Popups.ToggleFlyout("community-banner-menu");
                    }
                    if (ImGui.IsItemHovered())
                    {
                        DrawEditBadge(
                            new Vector2(apronMax.X - 26f * scale,
                                hitMin.Y + (apronMax.Y - hitMin.Y) * 0.45f),
                            scale, "Change banner");
                    }
                }
                DrawImageActionFlyout("community-banner-menu",
                    (!string.IsNullOrEmpty(profile.BannerImageBase64) || profile.BannerMediaHash != null), "banner", edit,
                    edit.OnBannerClicked, banner: true);
            }
            ImGui.PopStyleVar();
        }
        dl.PopClipRect();

        // Compact strip · avatar, name and pills fading in over the last
        // stretch of the collapse.
        if (compactA > 0.01f)
        {
            DrawCompactRow(dl, profile, group, texturePrefix, shellName, listed, scale,
                stripMin: pos, stripH: geom.CompactH,
                pad: pad, alpha: compactA);
        }
        dl.PopClipRect();

        // The docked tab bar renders in its own overlay child parked at
        // tabsY - see DrawPinnedOverlay, which owns hit priority over any
        // apron item.
    }

    /// <summary>
    /// The collapsed hero strip: mini avatar, shell name and the status
    /// pills · the reduced form the banner folds into, kept fixed at the
    /// window top.
    /// </summary>
    private static void DrawCompactRow(ImDrawListPtr dl, SyncshellProfileDto profile,
        GroupFullInfoDto? group, string texturePrefix, string shellName, bool listed,
        float scale, Vector2 stripMin, float stripH, float pad, float alpha)
    {
        var ava = MathF.Min(30f * scale, stripH - 12f * scale);
        var avatar = profile.AvatarMediaHash == null && string.IsNullOrEmpty(profile.AvatarImageBase64)
            ? null
            : SpheneUI.Media.Texture($"{texturePrefix}avatar:{profile.GroupGID}:{MediaVersion(profile.AvatarMediaHash, profile.AvatarImageBase64)}",
                ResolveBytes(profile.AvatarMediaHash, profile.AvatarImageBase64));
        var avMin = new Vector2(stripMin.X + pad, stripMin.Y + (stripH - ava) * 0.5f);
        var avCenter = avMin + new Vector2(ava * 0.5f);
        var textCol = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, alpha));
        if (avatar != null)
        {
            SpheneUI.Media.ImageInRect(dl, avatar, avMin, avMin + new Vector2(ava),
                ImageFit.Cover, rounding: ava * 0.5f);
        }
        else
        {
            dl.AddCircleFilled(avCenter, ava * 0.5f,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Accent, 0.28f * alpha)));
            using (SpheneUI.Icons.PushFont())
            {
                var glyph = FontAwesomeIcon.Users.ToIconString();
                var glyphSize = ImGui.CalcTextSize(glyph);
                dl.AddText(avCenter - glyphSize * 0.5f, textCol, glyph);
            }
        }
        var lineH = ImGui.GetTextLineHeight();
        var nameX = avMin.X + ava + 10f * scale;
        dl.AddText(new Vector2(nameX, stripMin.Y + (stripH - lineH) * 0.5f), textCol, shellName);
        // The status pills that must never scroll away · they live on the
        // compact strip once the hero has folded.
        if (alpha > 0.5f)
        {
            var nameW = ImGui.CalcTextSize(shellName).X;
            DrawStatusPills(dl,
                new Vector2(nameX + nameW + 10f * scale,
                    stripMin.Y + (stripH - 18f * scale) * 0.5f),
                group, listed, scale);
        }
    }

    /// <summary>
    /// Venue-style status pills on the banner · open/closed + listed. For
    /// managers the listed pill becomes its own toggle: it always shows the
    /// directory state and a click flips it.
    /// </summary>
    private static void DrawStatusPills(ImDrawListPtr dl, Vector2 topLeft, GroupFullInfoDto? group,
        bool listed, float scale, SyncshellCommunityEdit? edit = null)
    {
        var x = topLeft.X;
        if (group != null)
        {
            var open = !group.GroupPermissions.IsDisableInvites();
            x += SyncshellCard.StatusPill(dl, new Vector2(x, topLeft.Y),
                open ? "Open to join" : "Closed",
                open ? SpheneUI.Palette.Success : SpheneUI.Colors.TextMuted,
                dot: open, scale) + 6f * scale;
        }
        var canList = edit is { CanManagePage: true, Ops.Patch: not null };
        if (listed || canList)
        {
            var tint = listed ? SpheneUI.Palette.CrystalBlue : SpheneUI.Colors.TextMuted;
            var pillPos = new Vector2(x, topLeft.Y);
            var w = SyncshellCard.StatusPill(dl, pillPos,
                listed ? "Listed" : "Not listed", tint, dot: false, scale);
            if (canList)
            {
                var pillH = ImGui.GetTextLineHeight() + 6f * scale;
                ImGui.SetCursorScreenPos(pillPos);
                ImGui.InvisibleButton("##community-listing-toggle", new Vector2(w, pillH));
                if (ImGui.IsItemHovered())
                {
                    dl.AddRect(pillPos, pillPos + new Vector2(w, pillH),
                        SpheneUI.ColorUtils.ToU32(
                            SpheneUI.ColorUtils.WithAlpha(tint, 0.8f)), pillH * 0.5f);
                }
                if (ImGui.IsItemClicked())
                {
                    edit!.Ops!.Patch!(c => c with { IsListedPublicly = !c.IsListedPublicly });
                }
                SpheneUI.AttachTooltip(listed
                    ? "Listed in the public directory · click to unlist"
                    : "Click to list this page in the public directory");
            }
            x += w + 6f * scale;
        }
        if (group != null && group.GroupPairUserInfos.Count == 0)
        {
            SyncshellCard.StatusPill(dl, new Vector2(x, topLeft.Y), "Area-bound",
                SpheneUI.Palette.NetworkWarning, dot: false, scale);
        }
    }

    /// <summary>Small pencil badge drawn at a corner while an edit overlay is hovered.</summary>
    private static void DrawEditBadge(Vector2 center, float scale, string tooltip)
    {
        var dl = ImGui.GetWindowDrawList();
        var r = 11f * scale;
        var bg = SpheneUI.ColorUtils.WithAlpha(new Vector4(0f, 0f, 0f, 1f), 0.55f);
        dl.AddCircleFilled(center, r, SpheneUI.ColorUtils.ToU32(bg));
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = FontAwesomeIcon.Pen.ToIconString();
            var gSize = ImGui.CalcTextSize(glyph);
            dl.AddText(center - gSize * 0.5f, ImGui.GetColorU32(ImGuiCol.Text), glyph);
        }
        SpheneUI.AttachTooltip(tooltip);
    }

    /// <summary>
    /// The banner/avatar action menu · anchored at the clicked hit zone with
    /// upload (or clear when an image exists). Removing goes through the ops
    /// patch so the host refreshes like every other structural edit.
    /// </summary>
    private static void DrawImageActionFlyout(string id, bool hasImage, string noun,
        SyncshellCommunityEdit edit, Action? upload, bool banner)
    {
        if (!SpheneUI.Popups.BeginFlyout(id))
        {
            return;
        }
        if (upload != null && SpheneUI.Icons.TextButton(FontAwesomeIcon.Upload, $"Upload new {noun}"))
        {
            SpheneUI.Popups.ToggleFlyout(id);
            upload();
        }
        if (hasImage && edit.Ops?.Patch != null
            && SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, $"Remove {noun}"))
        {
            SpheneUI.Popups.ToggleFlyout(id);
            edit.Ops.Patch.Invoke(c => c with
            {
                Profile = banner
                    ? c.Profile with
                    {
                        BannerImageBase64 = null, BannerMediaHash = null, BannerFileName = null,
                        BannerContentType = null, BannerSize = null
                    }
                    : c.Profile with
                    {
                        AvatarImageBase64 = null, AvatarMediaHash = null, AvatarFileName = null,
                        AvatarContentType = null, AvatarSize = null
                    }
            });
        }
        SpheneUI.Popups.EndFlyout();
    }

    /// <summary>
    /// Wraps a section tab's content · managers get a visibility bar at the top
    /// so a hidden section is toggled (and noticed) at its source instead of
    /// only inside the settings subpage.
    /// </summary>
    private static bool DrawSectionWrapped(SyncshellCommunitySection section,
        SyncshellCommunityDto community, GroupFullInfoDto? group, string texturePrefix,
        Vector4 accent, SyncshellCommunityEdit? edit, CommunityPageState? page,
        CommunityMemberHooks? members, HashSet<SyncshellCommunitySection> visibleSet)
    {
        WidgetVis? vis = null;
        if (edit is { CanManagePage: true, Ops.Patch: not null })
        {
            var visible = visibleSet.Contains(section);
            var label = Array.Find(SectionOrder, e => e.Section == section).Label;
            vis = new WidgetVis(visible, label, on =>
            {
                if (on) visibleSet.Add(section);
                else visibleSet.Remove(section);
                var ordered = visibleSet
                    .OrderBy(s => Array.FindIndex(SectionOrder, e => e.Section == s)).ToList();
                edit.Ops.Patch.Invoke(c => c with { VisibleSections = ordered });
            });
        }
        return DrawSection(section, community, group, texturePrefix, accent, edit, page, members, vis);
    }

    private static bool DrawSection(SyncshellCommunitySection section, SyncshellCommunityDto community,
        GroupFullInfoDto? group, string texturePrefix, Vector4 accent, SyncshellCommunityEdit? edit,
        CommunityPageState? page, CommunityMemberHooks? members, WidgetVis? vis = null)
    {
        return section switch
        {
            SyncshellCommunitySection.About => DrawAbout(community.Profile, accent, edit, teaser: false, page, vis),
            SyncshellCommunitySection.Events => DrawEvents(community, accent, page, edit, vis),
            SyncshellCommunitySection.News => DrawNews(community, accent, page, edit, vis),
            SyncshellCommunitySection.Rules => DrawRules(community, accent, edit, page, vis),
            SyncshellCommunitySection.Links => DrawLinks(community, accent, edit, page, vis),
            SyncshellCommunitySection.Gallery => DrawGallery(community, texturePrefix, accent, page, edit, vis),
            SyncshellCommunitySection.Staff => DrawStaff(community, group, texturePrefix, accent, members, vis),
            _ => false,
        };
    }

    private static bool DrawAbout(SyncshellProfileDto profile, Vector4 accent, SyncshellCommunityEdit? edit,
        bool teaser, CommunityPageState? page = null, WidgetVis? vis = null)
    {
        var sections = profile.Sections?.Where(s =>
            !string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Body)).ToList();
        var hasAbout = !string.IsNullOrWhiteSpace(profile.Description);
        var canEditAbout = edit?.CanEditIdentity == true
            && edit.OnAboutCommitted != null && page != null;
        var editingAbout = canEditAbout && page!.EditingAbout;
        if (!hasAbout && (sections == null || sections.Count == 0) && !canEditAbout)
        {
            return false;
        }

        if (teaser)
        {
            // Overview teaser · a short excerpt plus a jump to the About tab.
            if (hasAbout)
            {
                var plain = MarkdownRenderer.ToPlainText(profile.Description!);
                var cut = plain.Length > 220 ? plain[..220].TrimEnd() + "…" : plain;
                Widget("about-teaser", "About", FontAwesomeIcon.Info, null, accent, () =>
                {
                    SpheneUI.Text.MutedWrapped(cut);
                    if (page != null && plain.Length > 220
                        && SpheneUI.Controls.SegmentButton("Read more", FontAwesomeIcon.ArrowRight, accent: accent))
                    {
                        page.Tab = SyncshellCommunitySection.About;
                        page.SwitchT = 0f;
                    }
                });
            }
            return hasAbout;
        }

        if (hasAbout || canEditAbout)
        {
            Widget("about", "About", FontAwesomeIcon.Info, null, accent, () =>
            {
                if (editingAbout)
                {
                    var about = page!.AboutBuffer;
                    if (page.AboutFocusPending)
                    {
                        ImGui.SetKeyboardFocusHere();
                        page.AboutFocusPending = false;
                    }
                    if (SpheneUI.Controls.InputTextMultiline("##community-about-edit", ref about,
                            4000, new(0, 110f * SpheneUI.Scale)))
                    {
                        page.AboutBuffer = about;
                    }
                    using (ImRaii.Group())
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
                        {
                            page.EditingAbout = false;
                            edit!.OnAboutCommitted?.Invoke(about);
                        }
                        SpheneUI.AttachTooltip("Done editing");
                        ImGui.SameLine(0f, SpheneUI.Spacing.X12.Scaled);
                        SpheneUI.Text.Muted("Markdown is supported · the live view renders it.");
                    }
                }
                else if (hasAbout)
                {
                    MarkdownRenderer.RenderMarkdown(profile.Description ?? string.Empty, 0,
                        UI.Components.MarkdownComposer.MediaImageSource);
                    UI.Components.EntityChipList.Draw(profile.Description);
                }
                else
                {
                    SpheneUI.Layout.EmptyState(FontAwesomeIcon.InfoCircle, "No about text",
                        canEditAbout
                            ? "Use the pen on the section header to tell people who you are."
                            : "The crew has not introduced itself yet.", accent: accent);
                }
            },
            vis,
            canEditAbout && !editingAbout
                ? (FontAwesomeIcon.Pen, "Edit about", () =>
                {
                    page!.AboutBuffer = profile.Description ?? string.Empty;
                    page.EditingAbout = true;
                    page.AboutFocusPending = true;
                })
                : null);
        }
        if (sections != null)
        {
            foreach (var section in sections)
            {
                var capturedSection = section;
                Widget($"section-{capturedSection.Title}", capturedSection.Title,
                    FontAwesomeIcon.LayerGroup, null, accent, () =>
                    {
                        SpheneUI.Text.Wrapped(capturedSection.Body);
                    });
            }
        }
        return true;
    }

    /// <summary>
    /// Accent-edged spotlight strip under the hero · the next upcoming event,
    /// or the latest pinned post when nothing is scheduled. Venue "now playing".
    /// </summary>
    private static void DrawUpNext(SyncshellCommunityDto community, Vector4 accent,
        CommunityPageState? page)
    {
        var now = DateTime.UtcNow;
        var next = community.Events?.Where(e => (e.EndUtc ?? e.StartUtc) >= now.AddHours(-4))
            .OrderBy(e => e.StartUtc).FirstOrDefault();
        var pinned = next == null
            ? community.News?.Where(n => n.IsPinned).OrderByDescending(n => n.PublishedAt).FirstOrDefault()
            : null;
        if (next == null && pinned == null)
        {
            return;
        }

        var scale = SpheneUI.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var dl = ImGui.GetWindowDrawList();
        // The accent-tinted widget surface: same quiet shape as every other
        // block, the color wash marks it as the page's "now playing" spotlight.
        SpheneUI.Layout.SurfaceFlat(() =>
        {
            var min = ImGui.GetCursorScreenPos();
            var w = SpheneUI.Controls.BoundedAvailX();
            var h = lineH + 8f * scale + lineH;
            var icon = next != null ? FontAwesomeIcon.CalendarDay : FontAwesomeIcon.Thumbtack;
            using (SpheneUI.Icons.PushFont())
            {
                dl.AddText(new Vector2(min.X, min.Y + 2f * scale),
                    SpheneUI.ColorUtils.ToU32(accent), icon.ToIconString());
            }
            SyncshellCard.StatusPill(dl, new Vector2(min.X + 20f * scale, min.Y),
                next != null ? "Up next" : "Pinned", accent, dot: next != null, scale);
            var body = next != null
                ? $"{next.Title} · {next.StartUtc.ToLocalTime():ddd, MMM d · HH:mm}" +
                  (string.IsNullOrWhiteSpace(next.Location) ? string.Empty : $" @ {next.Location}")
                : pinned!.Title;
            dl.PushClipRect(min, min + new Vector2(w, h), true);
            dl.AddText(new Vector2(min.X, min.Y + lineH + 8f * scale),
                ImGui.GetColorU32(ImGuiCol.Text), body);
            dl.PopClipRect();

            ImGui.Dummy(new Vector2(w, h));
            // Events drill into their detail page; pinned posts jump to News.
            if (ImGui.IsItemClicked() && page != null)
            {
                if (next != null)
                {
                    page.EventId = next.Id;
                }
                page.Tab = next != null
                    ? SyncshellCommunitySection.Events
                    : SyncshellCommunitySection.News;
                page.SwitchT = 0f;
            }
            if (ImGui.IsItemHovered())
            {
                SpheneUI.AttachTooltip(next != null ? "Open the event" : "Open the News page");
            }
        }, id: "community-upnext", accentColor: accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    /// <summary>Aetherphone-style rail header: icon + micro label + count + hairline.</summary>
    /// <summary>Owner-facing section-visibility state · rendered as a subtle
    /// eye toggle in the widget header instead of a dedicated card.</summary>
    internal sealed record WidgetVis(bool Visible, string Label, Action<bool> Toggle);

    internal static void RailHeader(string label, FontAwesomeIcon icon, int? count, Vector4 accent,
        WidgetVis? vis = null,
        params (FontAwesomeIcon Icon, string Tooltip, Action OnClick)?[] actions)
    {
        var lineY = ImGui.GetCursorScreenPos().Y;
        SpheneUI.Icons.Icon(icon, accent);
        ImGui.SameLine(0f, 6f * SpheneUI.Scale);
        SpheneUI.Text.Colored(label.ToUpperInvariant(), accent);
        if (count.HasValue)
        {
            ImGui.SameLine(0f, 6f * SpheneUI.Scale);
            SpheneUI.Text.Muted($"· {count.Value}");
        }
        var actionCount = 0;
        foreach (var a in actions)
        {
            if (a.HasValue)
            {
                actionCount++;
            }
        }
        if (actionCount > 0 || vis != null)
        {
            var gap = 4f * SpheneUI.Scale;
            var total = -gap;
            if (vis != null)
            {
                total += SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Eye).X + gap;
            }
            foreach (var a in actions)
            {
                if (a.HasValue)
                {
                    total += SpheneUI.Icons.ButtonSize(a.Value.Icon).X + gap;
                }
            }
            var x = ImGui.GetCursorScreenPos().X + SpheneUI.Controls.BoundedAvailX() - total;
            var y = lineY;
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            var first = true;
            if (vis != null)
            {
                DrawVisToggle(vis, accent);
                first = false;
            }
            foreach (var a in actions)
            {
                if (!a.HasValue)
                {
                    continue;
                }
                if (!first)
                {
                    ImGui.SameLine(0f, gap);
                }
                first = false;
                if (SpheneUI.Icons.Button(a.Value.Icon))
                {
                    a.Value.OnClick();
                }
                SpheneUI.AttachTooltip(a.Value.Tooltip);
            }
            ImGui.NewLine();
        }
        SpheneUI.Layout.Separator();
    }

    /// <summary>Subtle eye toggle docked into a widget header · muted eye
    /// while the section is visible, an amber slashed eye when it is hidden
    /// so staff spot staff-only tabs at a glance.</summary>
    private static void DrawVisToggle(WidgetVis vis, Vector4 accent)
    {
        var size = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Eye);
        if (ImGui.InvisibleButton("##vis-toggle", size))
        {
            vis.Toggle(!vis.Visible);
        }
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        var icon = vis.Visible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
        var tint = vis.Visible
            ? (hovered ? accent : SpheneUI.Colors.TextMuted)
            : SpheneUI.Colors.Warning;
        if (hovered || !vis.Visible)
        {
            var fill = SpheneUI.ColorUtils.WithAlpha(
                vis.Visible ? accent : SpheneUI.Colors.Warning, hovered ? 0.16f : 0.10f);
            dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), size.Y * 0.3f);
        }
        using (SpheneUI.Icons.PushFont())
        {
            var iconStr = icon.ToIconString();
            var iconSize = ImGui.CalcTextSize(iconStr);
            dl.AddText(new Vector2(min.X + (size.X - iconSize.X) * 0.5f,
                min.Y + (size.Y - iconSize.Y) * 0.5f),
                SpheneUI.ColorUtils.ToU32(tint), iconStr);
        }
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        SpheneUI.AttachTooltip(vis.Visible
            ? $"{vis.Label} is visible to visitors · click to hide (staff only)"
            : $"{vis.Label} is hidden · staff only, click to show visitors");
    }

    /// <summary>
    /// One page block as a tonal widget · the surface carries its own header,
    /// so the header hairline lands as an inset line inside the block and the
    /// whole unit reads as one bounded region without a visible card border.
    /// Every page block uses this · teasers, text sections and the collection
    /// feeds alike · so the page reads as one stack of equal-shaped cards.
    /// Content must stay inside the surface: no nested <c>Surface</c>/
    /// <c>RowGroup</c>/<c>SurfaceFlat</c> · draw-list channels cannot nest,
    /// so inner blocks use <see cref="SpheneUI.Layout.Separator"/> hairlines
    /// or item-level visuals (flyers, photo tiles) instead.
    /// </summary>
    internal static void Widget(string id, string label, FontAwesomeIcon icon, int? count,
        Vector4 accent, Action content, WidgetVis? vis = null,
        params (FontAwesomeIcon Icon, string Tooltip, Action OnClick)?[] actions)
    {
        SpheneUI.Layout.Surface(() =>
        {
            RailHeader(label, icon, count, accent, vis, actions);
            content();
        }, id: $"community-widget-{id}");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    private static bool DrawEvents(SyncshellCommunityDto community, Vector4 accent,
        CommunityPageState? page, SyncshellCommunityEdit? edit = null, WidgetVis? vis = null)
    {
        var events = community.Events ?? [];
        var canEvents = edit is { CanManageEvents: true };
        if (events.Count == 0 && !canEvents)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var upcoming = events.Where(e => (e.EndUtc ?? e.StartUtc) >= now.AddHours(-4))
            .OrderBy(e => e.StartUtc).ToList();
        var past = events.Except(upcoming).OrderByDescending(e => e.StartUtc).ToList();
        var memories = community.Albums?.Where(a => a.LinkedEventId.HasValue)
            .ToDictionary(a => a.LinkedEventId!.Value) ?? [];

        if (page?.EventsCalendarMode == true)
        {
            // Calendar mode · server-expanded month grid replaces the rail.
            Widget("events-calendar", "Calendar", FontAwesomeIcon.CalendarAlt, null,
                accent, () =>
                {
                    DrawEventsModeToggle(page, accent);
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                    page.EventsCalendar.Fetch = edit?.LoadCalendar;
                    // Per-date editing routes into the planner - the attendee
                    // calendar keeps no manage affordances of its own.
                    Action<SyncshellEventOccurrenceDto>? onEditOccurrence = null;
                    if (edit?.OnEditEvent != null)
                    {
                        onEditOccurrence = occ =>
                        {
                            var ev = events.FirstOrDefault(e => e.Id == occ.EventId);
                            if (ev == null || edit.CanEditEvent?.Invoke(ev) != true) return;
                            edit.OnEditEvent(ev, occ);
                        };
                    }
                    page.EventsCalendar.Draw(accent,
                        onOpen: id => { page.EventId = id; page.SwitchT = 0f; },
                        onException: null,
                        onEditOccurrence: onEditOccurrence);
                },
                vis);
            return true;
        }
        Widget("events-upcoming", "Upcoming", FontAwesomeIcon.Calendar, upcoming.Count,
            accent, () =>
            {
                DrawEventsModeToggle(page, accent);
                // Free-text filter narrows the rail + past list - derived per
                // frame so clearing the box restores the full set.
                var shown = upcoming;
                if (!string.IsNullOrEmpty(page?.EventFilter))
                {
                    var needle = page.EventFilter;
                    shown = upcoming.Where(e =>
                        e.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || (e.Location?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                }
                if (shown.Count > 0)
                {
                    var scale = SpheneUI.Scale;
                    var cardH = 86f * scale * SpheneUI.Density.Factor;
                    using (ImRaii.Child("community-events-rail",
                               new Vector2(0f, cardH + 22f * scale), false,
                               ImGuiWindowFlags.HorizontalScrollbar))
                    {
                        for (var i = 0; i < shown.Count; i++)
                        {
                            if (i > 0)
                            {
                                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                            }
                            DrawEventFlyer(shown[i], accent, cardH,
                                memories.TryGetValue(shown[i].Id, out var album) ? album : null,
                                community.Gallery, page);
                        }
                    }
                }
                else
                {
                    SpheneUI.Text.Muted("Nothing scheduled yet.");
                }
            },
            vis);
        var filteredPast = string.IsNullOrEmpty(page?.EventFilter)
            ? past
            : past.Where(e =>
                e.Title.Contains(page!.EventFilter, StringComparison.OrdinalIgnoreCase)
                || (e.Location?.Contains(page.EventFilter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (filteredPast.Count > 0 && page?.EventsCalendarMode != true)
        {
            Widget("events-past", "Past events", FontAwesomeIcon.History, filteredPast.Count,
                accent, () =>
                {
                    for (var i = 0; i < filteredPast.Count; i++)
                    {
                        if (i > 0)
                        {
                            SpheneUI.Layout.Separator();
                        }
                        var ev = filteredPast[i];
                        var local = ev.StartUtc.ToLocalTime();
                        var time = ev.EndUtc != null
                            ? $"{local:ddd, MMM d · HH:mm} · {ev.EndUtc.Value.ToLocalTime():HH:mm}"
                            : $"{local:ddd, MMM d · HH:mm}";
                        var album = memories.TryGetValue(ev.Id, out var a) ? a : null;
                        var memCount = album != null
                            ? community.Gallery?.Count(g => g.AlbumId == album.Id) ?? 0
                            : 0;
                        var value = time + (string.IsNullOrWhiteSpace(ev.Location)
                            ? string.Empty : $" · {ev.Location}")
                            + (album != null ? $" · {memCount} memories" : string.Empty);
                        var captured = ev;
                        SpheneUI.Rows.Nav(captured.Title, value, () =>
                        {
                            if (page != null)
                            {
                                page.EventId = captured.Id;
                                page.SwitchT = 0f;
                            }
                        }, FontAwesomeIcon.CalendarDay);
                    }
                });
        }
        return true;
    }

    /// <summary>List/Calendar segment + free-text filter on the events tab.</summary>
    private static void DrawEventsModeToggle(CommunityPageState? page, Vector4 accent)
    {
        if (page == null)
        {
            return;
        }
        if (SpheneUI.Controls.SegmentButton("List", FontAwesomeIcon.List,
                accent: accent, active: !page.EventsCalendarMode))
        {
            page.EventsCalendarMode = false;
        }
        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        if (SpheneUI.Controls.SegmentButton("Calendar", FontAwesomeIcon.CalendarAlt,
                accent: accent, active: page.EventsCalendarMode))
        {
            page.EventsCalendarMode = true;
        }
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        SpheneUI.Controls.InputTextWithHint("##ev-filter-" + (page.GetHashCode() & 0xffff),
            "Filter events…", ref page.EventFilter, 100,
            preferredWidth: 170f * SpheneUI.Scale);
    }

    /// <summary>Mini flyer card in the events rail · date block, title, when/where, memories chip.</summary>
    private static void DrawEventFlyer(SyncshellEventDto ev, Vector4 accent, float cardH,
        SyncshellGalleryAlbumDto? album, List<SyncshellGalleryImageDto>? gallery,
        CommunityPageState? page)
    {
        var scale = SpheneUI.Scale;
        var cardW = 168f * scale * SpheneUI.Density.Factor;
        var local = ev.StartUtc.ToLocalTime();
        var dl = ImGui.GetWindowDrawList();

        // The memories chip submits first · it owns the top-right corner's
        // hover claim so its click never falls through to the card button.
        var showChip = album != null && page != null;
        var pillW = 34f * scale;
        var pillH = ImGui.GetTextLineHeight() + 6f * scale;
        var chipClicked = false;
        var chipMin = Vector2.Zero;
        if (showChip)
        {
            var count = gallery?.Count(g => g.AlbumId == album!.Id) ?? 0;
            pillW += ImGui.CalcTextSize(count.ToString()).X;
            chipMin = ImGui.GetCursorScreenPos() + new Vector2(cardW - pillW - 8f * scale, 8f * scale);
            ImGui.SetCursorScreenPos(chipMin);
            chipClicked = ImGui.InvisibleButton($"##flyer-mem-{ev.Id}", new Vector2(pillW, pillH));
            ImGui.SetCursorScreenPos(chipMin - new Vector2(cardW - pillW - 8f * scale, 8f * scale));
        }

        var clicked = ImGui.InvisibleButton($"##flyer-{ev.Id}", new Vector2(cardW, cardH));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var radius = 10f * scale;

        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, hovered ? 0.16f : 0.09f)), radius);

        // 16:9 preview image fills the flyer when present - banner as
        // fallback, matching EventCard's preference order. A bottom scrim
        // keeps the date/title/meta legible over the image.
        var thumbHash = ev.PreviewMediaHash ?? ev.ImageMediaHash;
        IDalamudTextureWrap? wrap = null;
        if (thumbHash != null || ev.ImageBase64 != null)
        {
            wrap = SpheneUI.Media.Texture($"eventflyer:{ev.Id}:{thumbHash}",
                ResolveBytes(thumbHash, ev.ImageBase64));
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, min, max,
                    ImageFit.Cover, rounding: radius);
                dl.AddRectFilledMultiColor(
                    new Vector2(min.X, min.Y + (max.Y - min.Y) * 0.3f), max,
                    0, 0,
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.72f)),
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.72f)));
            }
        }

        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, hovered ? 0.55f : 0.25f)), radius, ImDrawFlags.None, 1f);

        var pad = 10f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var day = local.Day.ToString("00");
        var mon = local.ToString("MMM").ToUpperInvariant();
        var dateLabel = $"{day} {mon}";
        if (wrap != null)
        {
            var ds = ImGui.CalcTextSize(dateLabel);
            var platePad = 4f * scale;
            dl.AddRectFilled(
                new Vector2(min.X + pad - platePad, min.Y + pad - platePad),
                new Vector2(min.X + pad + ds.X + platePad, min.Y + pad + ds.Y + platePad),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)), 5f * scale);
        }
        dl.AddText(new Vector2(min.X + pad, min.Y + pad),
            SpheneUI.ColorUtils.ToU32(accent), dateLabel);

        var time = ev.EndUtc != null
            ? $"{local:HH:mm} · {ev.EndUtc.Value.ToLocalTime():HH:mm}"
            : $"{local:HH:mm}";
        dl.PushClipRect(min + new Vector2(pad, 0f), max - new Vector2(pad, 0f), true);
        dl.AddText(new Vector2(min.X + pad, min.Y + pad + lineH + 2f * scale),
            ImGui.GetColorU32(ImGuiCol.Text), ev.Title);
        dl.AddText(new Vector2(min.X + pad, max.Y - pad - lineH),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
            string.IsNullOrWhiteSpace(ev.Location) ? time : $"{time} · {ev.Location}");
        dl.PopClipRect();

        if (clicked && page != null)
        {
            page.EventId = ev.Id;
            page.SwitchT = 0f;
        }
        else if (clicked && !string.IsNullOrWhiteSpace(ev.Url))
        {
            Util.OpenLink(ev.Url!);
        }
        if (hovered && !string.IsNullOrWhiteSpace(ev.Description))
        {
            SpheneUI.AttachTooltip(MarkdownRenderer.ToPlainText(ev.Description!));
        }
        if (showChip)
        {
            DrawMemoryChip(chipMin, pillW, pillH, album!, gallery, accent, page!,
                chipClicked, dl, scale);
        }
    }

    /// <summary>
    /// The inline event composer · one card with all fields. New events open
    /// it under the Upcoming header; editing opens it on the detail page.
    /// </summary>
    /// <summary>
    /// Structured travel destination inside the event composer - the
    /// machine-readable target that stays independent of the free-text
    /// Location label. Occurrence edits pick the scope first (inherit /
    /// override / clear); hidden entirely when the host supplies no travel
    /// hooks.
    /// </summary>
    internal static void DrawEventDestinationEditor(CommunityEventComposer composer,
        SyncshellCommunityEdit edit, bool occurrenceEdit, float scale,
        bool showHeader = true)
    {
        var travel = edit.Travel;
        if (travel == null) return;

        if (showHeader)
        {
            SpheneUI.Text.SectionHeader("Travel destination");
            SpheneUI.Text.MutedWrapped(
                "Optional structured target for one-click travel with Lifestream - " +
                "the location text stays the human-readable description.");
        }
        if (occurrenceEdit)
        {
            var scopes = new[]
            {
                "Use the series destination",
                "Override destination for this date",
                "No destination on this date",
            };
            SpheneUI.Controls.Combo("Date scope##ec-dscope", scopes, s => s ?? string.Empty,
                onSelected: p => composer.DestScope = p is string s
                    ? Array.IndexOf(scopes, s) : 0,
                initialSelectedItem: scopes[Math.Clamp(composer.DestScope, 0,
                    scopes.Length - 1)],
                preferredWidth: 260f * scale);
            if (composer.DestScope != 1) return;
            SpheneUI.Text.MutedWrapped("Only this date's destination changes.");
        }

        var kinds = new[]
        {
            "No travel destination", "Use Syncshell property",
            "Custom housing address", "Public location",
        };
        SpheneUI.Controls.Combo("Destination##ec-dkind", kinds, s => s ?? string.Empty,
            onSelected: p => composer.DestKind = p is string s
                ? Array.IndexOf(kinds, s) : 0,
            initialSelectedItem: kinds[Math.Clamp(composer.DestKind, 0,
                kinds.Length - 1)],
            preferredWidth: 260f * scale);

        switch (composer.DestKind)
        {
            case 1:
                DrawBoundPropertyPicker(composer, travel, scale);
                break;
            case 2:
                DrawCustomAddressEditor(composer, travel, scale);
                break;
            case 3:
                DrawPublicLocationEditor(composer, travel);
                break;
            default:
                return;
        }

        SpheneUI.Controls.InputTextWithHint("Label##ec-dlabel", "Label (optional)…",
            ref composer.DestLabel, EventDestinationValidator.MaxDisplayLabelLength,
            preferredWidth: 340f * scale);

        var built = composer.BuildDestination();
        if (built != null)
        {
            SpheneUI.Text.Muted($"{DestinationTitle(built, travel)} - {DestinationSubtitle(built, travel)}");
        }
        else
        {
            SpheneUI.Text.Colored(composer.DestKind == 1
                ? "Pick a property to travel to."
                : "Complete all required fields - this destination can't be saved yet.",
                SpheneUI.Colors.Danger);
        }
    }

    /// <summary>Bound-area resolution + property picker for "Use Syncshell
    /// property" - loads once per composer open, preselects a single
    /// candidate, requires an explicit pick when several exist.</summary>
    private static void DrawBoundPropertyPicker(CommunityEventComposer composer,
        CommunityTravelHooks travel, float scale)
    {
        if (!composer.PropertyCandidatesLoaded && !composer.PropertyCandidatesLoading
            && travel.LoadBoundLocations != null)
        {
            composer.PropertyCandidatesLoading = true;
            var load = travel.LoadBoundLocations;
            _ = Task.Run(async () =>
            {
                var locs = await load().ConfigureAwait(false);
                composer.PropertyCandidates = locs == null
                    ? []
                    : Interop.Ipc.EventDestinationGrouping.PropertyCandidates(locs);
                if (composer.PropertyCandidates.Count == 1)
                {
                    composer.DestPropertyIndex = 0;
                }
                composer.PropertyCandidatesLoading = false;
                composer.PropertyCandidatesLoaded = true;
            });
        }

        if (composer.PropertyCandidatesLoading)
        {
            SpheneUI.Text.Muted("Resolving bound properties…");
            return;
        }
        if (travel.LoadBoundLocations == null)
        {
            SpheneUI.Text.MutedWrapped("Bound-area data isn't available on this surface.");
            return;
        }
        if (composer.PropertyCandidates is not { Count: > 0 } candidates)
        {
            SpheneUI.Text.MutedWrapped(
                "No bound area of this Syncshell resolves to a travelable housing address.");
            return;
        }
        var items = candidates
            .Select(c => DestinationAddressLine(c, travel)).ToArray();
        SpheneUI.Controls.Combo("Property##ec-dprop", items, s => s ?? string.Empty,
            onSelected: p => composer.DestPropertyIndex = p is string s
                ? Array.IndexOf(items, s) : -1,
            initialSelectedItem: composer.DestPropertyIndex >= 0
                && composer.DestPropertyIndex < items.Length
                    ? items[composer.DestPropertyIndex]
                    : "Pick a property…",
            preferredWidth: 340f * scale);
        if (candidates.Count > 1 && composer.DestPropertyIndex < 0)
        {
            SpheneUI.Text.MutedWrapped(
                "This Syncshell binds several properties - pick the event's address explicitly.");
        }
        else
        {
            SpheneUI.Text.MutedWrapped(
                "Saved as a snapshot - later changes to the Syncshell's bound areas won't move this event.");
        }
    }

    /// <summary>Free housing-address editor - world/district selectors plus
    /// constrained numeric inputs; the optional "Use current location" fills
    /// the fields on explicit click only.</summary>
    private static void DrawCustomAddressEditor(CommunityEventComposer composer,
        CommunityTravelHooks travel, float scale)
    {
        var worlds = travel.WorldChoices?.Invoke();
        if (worlds is { Count: > 0 })
        {
            SpheneUI.Controls.Combo("World##ec-dworld", worlds,
                w => w is { } v ? v.Name : string.Empty,
                onSelected: w => { if (w is { } p) composer.DestWorldId = p.Id; },
                initialSelectedItem: worlds.FirstOrDefault(w => w.Id == composer.DestWorldId));
        }
        var districts = DestinationDistricts.Select(d => d.Name).ToArray();
        SpheneUI.Controls.Combo("District##ec-ddistrict", districts, s => s ?? string.Empty,
            onSelected: p => composer.DestDistrict = p is string s
                ? Array.IndexOf(districts, s) : 0,
            initialSelectedItem: districts[Math.Clamp(composer.DestDistrict, 0,
                districts.Length - 1)]);
        SpheneUI.Controls.InputInt("Ward##ec-dward", ref composer.DestWard, 1, 5,
            preferredWidth: 90f * scale);
        if (SpheneUI.Controls.Toggle("Apartment##ec-dapt", ref composer.DestApartment))
        {
            composer.DestRoom = Math.Clamp(composer.DestRoom,
                EventDestinationValidator.MinRoom, EventDestinationValidator.MaxRoom);
        }
        if (composer.DestApartment)
        {
            SpheneUI.Controls.InputInt("Room##ec-droom", ref composer.DestRoom, 1, 50,
                preferredWidth: 90f * scale);
            SpheneUI.Controls.Toggle("Subdivision##ec-dsub", ref composer.DestApartmentSubdivision);
        }
        else
        {
            SpheneUI.Controls.InputInt("Plot##ec-dplot", ref composer.DestPlot, 1, 10,
                preferredWidth: 90f * scale);
        }

        var current = travel.CurrentLocation?.Invoke();
        var here = current is { } cur
            ? Interop.Ipc.LifestreamAddressMapping.FromLocationInfo(cur)
            : null;
        if (here != null)
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.MapMarkerAlt, "Use current location"))
            {
                composer.DestWorldId = (int)here.WorldId;
                for (var i = 0; i < DestinationDistricts.Length; i++)
                {
                    if (DestinationDistricts[i].TerritoryId == here.DistrictTerritoryId)
                    {
                        composer.DestDistrict = i;
                        break;
                    }
                }
                composer.DestWard = here.Ward;
                composer.DestApartment = here.IsApartment;
                composer.DestPlot = here.Plot;
                composer.DestRoom = here.Room;
                composer.DestApartmentSubdivision = here.ApartmentSubdivision;
            }
            SpheneUI.AttachTooltip($"Fill from where you stand - {DestinationAddressLine(here, travel)}");
        }
    }

    /// <summary>Public-location editor - display-first MVP: a territory pick
    /// plus the optional label; automated travel isn't promised.</summary>
    private static void DrawPublicLocationEditor(CommunityEventComposer composer,
        CommunityTravelHooks travel)
    {
        var territories = travel.TerritoryChoices?.Invoke();
        if (territories is { Count: > 0 })
        {
            SpheneUI.Controls.Combo("Territory##ec-dterr", territories,
                t => t is { } v ? v.Name : string.Empty,
                onSelected: t => { if (t is { } p) composer.DestTerritoryId = (int)p.Id; },
                initialSelectedItem: territories.FirstOrDefault(
                    t => t.Id == composer.DestTerritoryId));
        }
        else
        {
            SpheneUI.Text.MutedWrapped("Territory data isn't available on this surface.");
        }
        SpheneUI.Text.MutedWrapped(
            "Public locations show as information - automated travel arrives with a later update.");
    }

    /// <summary>One-line address summary: "Lavender Beds · Bahamut · Ward 12 · Plot 35".</summary>
    private static string DestinationAddressLine(EventDestinationDto d,
        CommunityTravelHooks? travel)
    {
        var district = d.DistrictTerritoryId != 0
            ? travel?.TerritoryName?.Invoke(d.DistrictTerritoryId)
                ?? DestinationDistricts.FirstOrDefault(x => x.TerritoryId == d.DistrictTerritoryId).Name
                ?? $"Territory {d.DistrictTerritoryId}"
            : null;
        var world = d.WorldId != 0
            ? travel?.WorldName?.Invoke((ushort)d.WorldId) ?? $"World {d.WorldId}"
            : null;
        var where = d.IsApartment
            ? $"Ward {d.Ward} · Room {d.Room}{(d.ApartmentSubdivision ? " (subdivision)" : string.Empty)}"
            : $"Ward {d.Ward} · Plot {d.Plot}";
        return string.Join(" · ", new[] { district, world, where }.Where(s => s != null));
    }

    /// <summary>
    /// Compact location card on the event detail - the stored destination
    /// plus the optional Lifestream travel affordance.
    /// </summary>
    private static void DrawEventLocation(SyncshellEventDto ev, EventDestinationDto dest,
        Vector4 accent, CommunityTravelHooks? travel, CommunityPageState page)
    {
        var publicLoc = (EventDestinationKind)dest.Kind == EventDestinationKind.PublicLocation;
        Widget($"event-destination-{ev.Id}", "Location", FontAwesomeIcon.MapMarkerAlt,
            null, accent, () =>
        {
            SpheneUI.Text.SectionHeader(DestinationTitle(dest, travel));
            SpheneUI.Text.MutedWrapped(DestinationSubtitle(dest, travel));
            if (publicLoc)
            {
                SpheneUI.Text.MutedWrapped(
                    "Automated travel to public locations isn't supported yet.");
            }
            DrawDestinationActions(dest, travel, page, accent, scopeKey: ev.Id,
                suppressTravel: publicLoc);
        });
    }

    /// <summary>
    /// Shared travel-action block for every destination card (event detail,
    /// community location): the availability probe, the explicit Travel
    /// button, Show on Map and the state line. State is probed once per
    /// (scope, destination) pair and re-probed when the host bumps
    /// <see cref="CommunityPageState.TravelVersion"/> on a mediator update -
    /// never per frame, never automatically. <paramref name="scopeKey"/>
    /// identifies the owning surface (event id; 0 = community location) so a
    /// stale async answer can't land on the wrong card.
    /// </summary>
    private static void DrawDestinationActions(EventDestinationDto dest,
        CommunityTravelHooks? travel, CommunityPageState page, Vector4 accent,
        long scopeKey, bool suppressTravel = false)
    {
        var key = dest.GetHashCode();
        var stale = page.TravelFor != scopeKey || page.TravelKey != key
            || page.TravelSeenVersion != page.TravelVersion;
        if (travel?.GetState != null && stale && !page.TravelLoading)
        {
            page.TravelFor = scopeKey;
            page.TravelKey = key;
            page.TravelSeenVersion = page.TravelVersion;
            page.TravelLoading = true;
            var probe = travel.GetState;
            _ = Task.Run(async () =>
            {
                var s = await probe(dest).ConfigureAwait(false);
                if (page.TravelFor == scopeKey && page.TravelKey == key)
                {
                    page.TravelStatus = s;
                }
                page.TravelLoading = false;
            });
        }

        var status = page.TravelFor == scopeKey && page.TravelKey == key
            ? page.TravelStatus : null;
        var state = status?.State;

        using (ImRaii.Group())
        {
            var canTravel = travel?.RequestTravel != null && !suppressTravel;
            var busy = state is LifestreamTravelState.Busy
                or LifestreamTravelState.TravelRequested;
            using (ImRaii.Disabled(!canTravel || busy || page.TravelLoading))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Route,
                        "Travel with Lifestream"))
                {
                    page.TravelLoading = true;
                    var request = travel!.RequestTravel!;
                    _ = Task.Run(async () =>
                    {
                        var s = await request(dest).ConfigureAwait(false);
                        if (page.TravelFor == scopeKey && page.TravelKey == key)
                        {
                            page.TravelStatus = s;
                        }
                        page.TravelLoading = false;
                    });
                }
            }
            if (travel?.ShowOnMap != null)
            {
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.MapMarkedAlt,
                        "Show on Map"))
                {
                    var show = travel.ShowOnMap;
                    _ = Task.Run(async () => await show(dest).ConfigureAwait(false));
                }
                SpheneUI.AttachTooltip(suppressTravel
                    ? "Opens the area map"
                    : "Opens the map at the plot entrance (or the district map)");
            }
        }

        if (page.TravelLoading && state == null)
        {
            SpheneUI.Text.Muted("Checking travel availability…");
        }
        else
        {
            switch (state)
            {
                case LifestreamTravelState.Unavailable:
                    SpheneUI.Text.MutedWrapped(
                        "Lifestream isn't available - enable the plugin to travel here.");
                    break;
                case LifestreamTravelState.DestinationUnsupported:
                    SpheneUI.Text.MutedWrapped(status?.Detail
                        ?? "Automatic travel isn't supported for this destination.");
                    break;
                case LifestreamTravelState.AlreadyAtDestination:
                    SpheneUI.Text.Colored("You're already here.", accent);
                    break;
                case LifestreamTravelState.Busy:
                    SpheneUI.Text.MutedWrapped(status?.Detail
                        ?? "Lifestream is busy - try again in a moment.");
                    break;
                case LifestreamTravelState.TravelRequested:
                    SpheneUI.Text.Muted("Travel in progress…");
                    break;
                case LifestreamTravelState.TravelFailed:
                    SpheneUI.Text.Colored(status?.Detail ?? "Travel failed.",
                        SpheneUI.Colors.Danger);
                    break;
                case LifestreamTravelState.Ready:
                    if (!string.IsNullOrWhiteSpace(status?.Detail))
                    {
                        SpheneUI.Text.Muted(status!.Detail);
                    }
                    break;
            }
        }
    }

    /// <summary>Card heading for a destination - the label or place name.</summary>
    internal static string DestinationTitle(EventDestinationDto d, CommunityTravelHooks? travel)
    {
        if (!string.IsNullOrWhiteSpace(d.DisplayLabel)) return d.DisplayLabel!;
        if ((EventDestinationKind)d.Kind == EventDestinationKind.PublicLocation)
        {
            return travel?.TerritoryName?.Invoke(d.TerritoryId) ?? $"Territory {d.TerritoryId}";
        }
        return d.DistrictTerritoryId != 0
            ? travel?.TerritoryName?.Invoke(d.DistrictTerritoryId)
                ?? DestinationDistricts.FirstOrDefault(x => x.TerritoryId == d.DistrictTerritoryId).Name
                ?? $"Territory {d.DistrictTerritoryId}"
            : "Housing address";
    }

    /// <summary>Card sub-line - the address components.</summary>
    internal static string DestinationSubtitle(EventDestinationDto d, CommunityTravelHooks? travel)
    {
        if ((EventDestinationKind)d.Kind == EventDestinationKind.PublicLocation)
        {
            var terr = travel?.TerritoryName?.Invoke(d.TerritoryId);
            return terr != null
                && !string.Equals(terr, d.DisplayLabel, StringComparison.Ordinal)
                    ? terr : "Public location";
        }
        return DestinationAddressLine(d, travel);
    }

    /// <summary>
    /// Event drill-in · opened via <see cref="CommunityPageState.EventId"/> from
    /// the flyer rail, past rows or the Up-next strip. Hero block with the big
    /// date, meta pills, description card, linked-album memories and the
    /// manage affordances. The layout is intentionally widget-ready: future
    /// blocks (RSVP, attendees, gallery wall) slot in below the meta row.
    /// </summary>
    private static bool DrawEventDetail(SyncshellEventDto ev, SyncshellCommunityDto community,
        string texturePrefix, Vector4 accent, SyncshellCommunityEdit? edit,
        CommunityPageState page, CommunityMemberHooks? members = null,
        CommunityTravelHooks? travel = null)
    {
        var scale = SpheneUI.Scale;
        var local = ev.StartUtc.ToLocalTime();
        var past = (ev.EndUtc ?? ev.StartUtc) < DateTime.UtcNow.AddHours(-4);

        if (SpheneUI.Controls.SegmentButton("Events", FontAwesomeIcon.ChevronLeft, accent: accent))
        {
            page.EventId = null;
            page.SwitchT = 0f;
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        // Hero block · big date tile + title + meta, accent-tinted like the
        // event flyers but page-width.
        var dl = ImGui.GetWindowDrawList();
        var pad = 12f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var heroMin = ImGui.GetCursorScreenPos();
        var heroW = SpheneUI.Controls.BoundedAvailX();
        var dateW = 64f * scale;
        var heroH = pad * 2f + lineH * 3.4f;
        var heroMax = heroMin + new Vector2(heroW, heroH);
        dl.AddRectFilled(heroMin, heroMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, past ? 0.05f : 0.1f)), 10f * scale);
        dl.AddRect(heroMin, heroMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, past ? 0.15f : 0.3f)), 10f * scale);

        var tileMin = heroMin + new Vector2(pad, pad);
        var tileMax = tileMin + new Vector2(dateW, heroH - pad * 2f);
        dl.AddRectFilled(tileMin, tileMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, past ? 0.12f : 0.22f)), 8f * scale);
        dl.AddRect(tileMin, tileMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, 0.5f)), 8f * scale);
        var dayStr = local.Day.ToString("00");
        if (SpheneUI.UidFont is { } dayFont)
        {
            using (dayFont.Push())
            {
                var ds = ImGui.CalcTextSize(dayStr);
                dl.AddText(new Vector2(tileMin.X + (dateW - ds.X) * 0.5f,
                    tileMin.Y + (tileMax.Y - tileMin.Y) * 0.5f - ds.Y - 2f * scale),
                    SpheneUI.ColorUtils.ToU32(accent), dayStr);
            }
        }
        else
        {
            var ds = ImGui.CalcTextSize(dayStr);
            dl.AddText(new Vector2(tileMin.X + (dateW - ds.X) * 0.5f,
                tileMin.Y + (tileMax.Y - tileMin.Y) * 0.5f - ds.Y - 2f * scale),
                SpheneUI.ColorUtils.ToU32(accent), dayStr);
        }
        var monStr = local.ToString("MMM").ToUpperInvariant();
        var monW = ImGui.CalcTextSize(monStr).X;
        dl.AddText(new Vector2(tileMin.X + (dateW - monW) * 0.5f,
            tileMax.Y - ImGui.GetTextLineHeight() - 8f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.LerpColor(accent, Vector4.One, 0.3f)), monStr);

        var tx = tileMax.X + pad;
        var ty = tileMin.Y;
        dl.PushClipRect(new Vector2(tx, ty), new Vector2(heroMax.X - pad, ty + lineH * 1.4f), true);
        dl.AddText(new Vector2(tx, ty), ImGui.GetColorU32(ImGuiCol.Text), ev.Title);
        dl.PopClipRect();
        var time = ev.EndUtc != null
            ? $"{local:dddd, MMM d · HH:mm} · {ev.EndUtc.Value.ToLocalTime():HH:mm}"
            : $"{local:dddd, MMM d · HH:mm}";
        var metaY = ty + lineH + 6f * scale;
        var metaX = tx;
        var cancelled = ev.EventStatus == (int)EventLifecycleStatus.Cancelled;
        metaX += SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY),
            cancelled ? "Cancelled" : past ? "Past" : "Upcoming",
            cancelled ? SpheneUI.Colors.Danger
            : past ? SpheneUI.Colors.TextMuted : accent,
            dot: !past && !cancelled, scale) + 6f * scale;
        metaX += SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY), time,
            SpheneUI.Colors.TextMuted, dot: false, scale) + 6f * scale;
        if (!string.IsNullOrWhiteSpace(ev.TimezoneId))
        {
            metaX += SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY),
                ev.TimezoneId!, SpheneUI.Colors.TextMuted, dot: false, scale) + 6f * scale;
        }
        if (!string.IsNullOrWhiteSpace(ev.RecurrenceRule))
        {
            metaX += SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY),
                "Repeats", accent, dot: false, scale) + 6f * scale;
        }
        if (ev.PublishStatus == (int)ContentPublishStatus.PendingApproval)
        {
            metaX += SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY),
                "Awaiting review", SpheneUI.Palette.CrystalBlue, dot: true, scale) + 6f * scale;
        }
        if (ev.PublishStatus == (int)ContentPublishStatus.Rejected
            && !string.IsNullOrWhiteSpace(ev.ReviewNote))
        {
            metaX += SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY),
                "Rejected", SpheneUI.Colors.Danger, dot: false, scale) + 6f * scale;
        }
        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY), ev.Location!,
                SpheneUI.Colors.TextMuted, dot: false, scale);
        }
        if (ev.OrganizerUsers is { Count: > 0 } organizers)
        {
            var names = string.Join(", ", organizers.Select(o =>
                HumanName(o) ?? "Unknown user"));
            SyncshellCard.StatusPill(dl, new Vector2(metaX, metaY),
                $"by {names}", SpheneUI.Colors.TextMuted, dot: false, scale);
        }
        ImGui.SetCursorScreenPos(new Vector2(heroMin.X, heroMax.Y));

        // Cover image under the hero · hash-backed through the shared media
        // pipeline, base64 rows keep working pre-migration.
        if (!string.IsNullOrEmpty(ev.ImageMediaHash) || !string.IsNullOrEmpty(ev.ImageBase64))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            var coverMin = ImGui.GetCursorScreenPos();
            var coverSize = new Vector2(SpheneUI.Controls.BoundedAvailX(), 140f * scale);
            var coverMax = coverMin + coverSize;
            var wrap = SpheneUI.Media.Texture($"{texturePrefix}event:{ev.Id}:{ev.ImageMediaHash}",
                ResolveBytes(ev.ImageMediaHash, ev.ImageBase64));
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, coverMin, coverMax,
                    ImageFit.Cover, rounding: 10f * scale);
            }
            ImGui.SetCursorScreenPos(coverMax);
            ImGui.Dummy(Vector2.Zero);
        }

        // Actions row · external link, memories jump, manage affordances.
        var actions = false;
        using (ImRaii.Group())
        {
            if (!string.IsNullOrWhiteSpace(ev.Url))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.ExternalLinkAlt))
                {
                    Util.OpenLink(ev.Url!);
                }
                SpheneUI.AttachTooltip("Open link");
                actions = true;
            }
            // One discreet management affordance - routes into the Event
            // Planner; shown only when the viewer may edit this event.
            if (edit?.OnEditEvent != null && edit.CanEditEvent?.Invoke(ev) == true)
            {
                if (actions) ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Pen))
                {
                    edit.OnEditEvent(ev, null);
                }
                SpheneUI.AttachTooltip("Edit event in the planner");
                actions = true;
            }
        }
        if (actions)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        if (ev.Destination is { } dest && EventDestinationValidator.Sanitize(dest) is { } cleanDest)
        {
            DrawEventLocation(ev, cleanDest, accent, travel, page);
        }

        DrawEventRsvp(ev, accent, edit, page, members);

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            Widget($"event-details-{ev.Id}", "Details", FontAwesomeIcon.Info, null, accent,
                () =>
                {
                    MarkdownRenderer.RenderMarkdown(ev.Description!, 0,
                        UI.Components.MarkdownComposer.MediaImageSource);
                    UI.Components.EntityChipList.Draw(ev.Description);
                });
        }

        var album = community.Albums?.FirstOrDefault(a => a.LinkedEventId == ev.Id);
        if (album != null)
        {
            var albumImgs = community.Gallery?.Where(g => g.AlbumId == album.Id
                && (!string.IsNullOrEmpty(g.ImageBase64) || g.ImageMediaHash != null)).ToList() ?? [];
            var albumId = album.Id;
            Widget($"event-memories-{ev.Id}", "Memories", FontAwesomeIcon.Images,
                albumImgs.Count, accent, () =>
                {
                    if (albumImgs.Count > 0)
                    {
                        DrawGalleryRail(albumImgs, texturePrefix, community.Profile.GroupGID,
                            84f * scale, 118f * scale,
                            onClick: i => { page.ViewerImages = albumImgs; page.ViewerIndex = i; page.ViewerOpenT = 0f; });
                    }
                    else
                    {
                        SpheneUI.Text.Muted("No photos linked yet.");
                    }
                },
                null,
                (FontAwesomeIcon.FolderOpen, "Open album", (Action)(() =>
                {
                    page.EventId = null;
                    page.Tab = SyncshellCommunitySection.Gallery;
                    page.AlbumId = albumId;
                    page.PickingAlbumPhotos = false;
                    page.SwitchT = 0f;
                })));
        }

        // Discussion thread - shared component, ops bound by the host.
        var commentOps = edit?.Comments?.Invoke(CommentTargetKind.Event, ev.Id);
        if (commentOps != null)
        {
            Widget($"event-comments-{ev.Id}", "Discussion", FontAwesomeIcon.Comments,
                ev.CommentCount, accent, () => page.Comments.Draw(CommentTargetKind.Event,
                    ev.Id, null, ev.GroupGID, commentOps, ev.CommentsLocked,
                    ev.CommentCount));
        }
        return true;
    }

    /// <summary>
    /// RSVP + participants block on the event detail page - rendered for
    /// members (participation hooks) even when they hold no manage rights.
    /// The participant summary loads lazily via <see cref="SyncshellCommunityEdit.LoadParticipants"/>
    /// and is cached on the page state per event.
    /// </summary>
    private static void DrawEventRsvp(SyncshellEventDto ev, Vector4 accent,
        SyncshellCommunityEdit? edit, CommunityPageState page, CommunityMemberHooks? members)
    {
        var canRsvp = edit?.OnRsvp != null;
        var cancelled = ev.EventStatus == (int)EventLifecycleStatus.Cancelled;
        var past = (ev.EndUtc ?? ev.StartUtc) < DateTime.UtcNow.AddHours(-4);
        var open = !cancelled && !past;

        if (canRsvp || ev.MaxAttendees.HasValue || ev.AttendingCount > 0 || ev.WaitlistCount > 0)
        {
            Widget($"event-rsvp-{ev.Id}", "Participation", FontAwesomeIcon.Users, null,
                accent, () =>
                {
                    // Capacity line - server-authoritative counters.
                    var parts = new List<string> { $"{ev.AttendingCount} going" };
                    if (ev.MaxAttendees is { } cap)
                    {
                        parts[0] = $"{ev.AttendingCount}/{cap} going";
                        if (ev.AttendingCount >= cap)
                        {
                            parts.Add("full");
                        }
                    }
                    if (ev.WaitlistCount > 0)
                    {
                        parts.Add($"{ev.WaitlistCount} waitlisted");
                    }
                    SpheneUI.Text.Muted(string.Join(" · ", parts));

                    if (canRsvp && open)
                    {
                        var my = ev.ViewerRsvpStatus;
                        bool Btn(string label, int status, FontAwesomeIcon icon)
                            => SpheneUI.Controls.SegmentButton(label, icon,
                                accent: accent, active: my == status);
                        if (Btn("Going", (int)EventRsvpStatus.Attending, FontAwesomeIcon.Check))
                        {
                            edit!.OnRsvp!(ev, (int)EventRsvpStatus.Attending);
                        }
                        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                        if (Btn("Interested", (int)EventRsvpStatus.Interested, FontAwesomeIcon.Star))
                        {
                            edit!.OnRsvp!(ev, (int)EventRsvpStatus.Interested);
                        }
                        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                        if (Btn("Can't go", (int)EventRsvpStatus.NotAttending, FontAwesomeIcon.Times))
                        {
                            edit!.OnRsvp!(ev, (int)EventRsvpStatus.NotAttending);
                        }
                        if (my.HasValue && edit?.OnWithdrawRsvp != null)
                        {
                            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.SignOutAlt, "Leave"))
                            {
                                edit.OnWithdrawRsvp(ev);
                            }
                            SpheneUI.AttachTooltip("Withdraw your RSVP - frees your seat if you hold one");
                        }
                        if (my == (int)EventRsvpStatus.Waitlist)
                        {
                            var pos = page.EventParticipants?.ViewerWaitlistPosition;
                            SpheneUI.Text.Colored(pos.HasValue
                                    ? $"You're #{pos.Value} on the waitlist - you'll move up automatically."
                                    : "You're on the waitlist - you'll move up automatically.",
                                SpheneUI.Palette.CrystalBlue);
                        }
                        else if (my == (int)EventRsvpStatus.Attending
                            && ev.MaxAttendees.HasValue && ev.WaitlistCount > 0)
                        {
                            SpheneUI.Text.Muted("Leaving frees your seat to the next person in line.");
                        }
                    }
                    else if (cancelled)
                    {
                        SpheneUI.Text.Colored("This event was cancelled.", SpheneUI.Colors.Danger);
                    }
                });
        }

        // Participant roster - the server decides what the viewer may see
        // (members: attendees only; staff: every status incl. waitlist order).
        if (edit?.LoadParticipants != null && !cancelled)
        {
            Widget($"event-attendees-{ev.Id}", "Attendees", FontAwesomeIcon.UserFriends,
                ev.AttendingCount, accent, () => DrawParticipantList(ev, edit, page, members));
        }
    }

    /// <summary>Lazy participant fetch + roster rendering · staff rows carry
    /// move/remove affordances, member rows are read-only.</summary>
    private static void DrawParticipantList(SyncshellEventDto ev,
        SyncshellCommunityEdit edit, CommunityPageState page, CommunityMemberHooks? members)
    {
        if (page.ParticipantsFor != ev.Id && !page.ParticipantsLoading)
        {
            page.ParticipantsLoading = true;
            var captured = ev;
            _ = Task.Run(async () =>
            {
                var result = await edit.LoadParticipants!(captured).ConfigureAwait(false);
                page.EventParticipants = result;
                page.ParticipantsFor = captured.Id;
                page.ParticipantsLoading = false;
            });
        }
        var summary = page.ParticipantsFor == ev.Id ? page.EventParticipants : null;
        if (page.ParticipantsLoading && summary == null)
        {
            SpheneUI.Text.Muted("Loading attendees...");
            return;
        }
        if (summary == null)
        {
            SpheneUI.Text.Muted("No participants yet.");
            return;
        }

        var staff = edit.CanManageEvents && edit.OnUpdateParticipant != null;
        foreach (var p in summary.Participants.OrderBy(p => p.Status)
                     .ThenBy(p => p.WaitlistPosition ?? int.MaxValue)
                     .ThenBy(p => p.UpdatedAt))
        {
            var name = p.User?.PreferredName
                ?? members?.GetDisplayName?.Invoke(p.UserUID) ?? "Unknown user";
            var (statusLabel, statusColor) = (EventRsvpStatus)p.Status switch
            {
                EventRsvpStatus.Attending => ("going", SpheneUI.Palette.NetworkActive),
                EventRsvpStatus.Interested => ("interested", SpheneUI.Palette.CrystalBlue),
                EventRsvpStatus.Waitlist => (
                    $"waitlist{(p.WaitlistPosition.HasValue ? $" #{p.WaitlistPosition.Value}" : string.Empty)}",
                    SpheneUI.Palette.CrystalBlue),
                _ => ("not going", SpheneUI.Colors.TextMuted),
            };
            using var rowId = ImRaii.PushId($"evp-{p.UserUID}");
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.User, statusColor);
            ImGui.SameLine();
            SpheneUI.Text.Colored(name, SpheneUI.Colors.TextPrimary);
            ImGui.SameLine();
            SpheneUI.Text.Muted(statusLabel);
            if (staff)
            {
                var opsW = ImGui.GetFrameHeight() * 3f + SpheneUI.Spacing.X8.Scaled;
                SpheneUI.Controls.DockToRightEdge(opsW);
                var captured = p;
                if (p.Status != (int)EventRsvpStatus.Attending
                    && SpheneUI.Icons.Button(FontAwesomeIcon.Check))
                {
                    edit.OnUpdateParticipant!(ev, captured.UserUID,
                        (int)EventRsvpStatus.Attending, false);
                }
                if (p.Status != (int)EventRsvpStatus.Attending)
                {
                    SpheneUI.AttachTooltip("Seat this member (waitlist jump)");
                }
                ImGui.SameLine();
                if (p.Status != (int)EventRsvpStatus.Waitlist
                    && SpheneUI.Icons.Button(FontAwesomeIcon.UserClock))
                {
                    edit.OnUpdateParticipant!(ev, captured.UserUID,
                        (int)EventRsvpStatus.Waitlist, false);
                }
                if (p.Status != (int)EventRsvpStatus.Waitlist)
                {
                    SpheneUI.AttachTooltip("Move to waitlist end");
                }
                ImGui.SameLine();
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
                {
                    edit.OnUpdateParticipant!(ev, captured.UserUID,
                        (int)EventRsvpStatus.NotAttending, true);
                }
                SpheneUI.AttachTooltip("Remove participant");
            }
        }
        if (summary.Participants.Count == 0)
        {
            SpheneUI.Text.Muted("Nobody signed up yet - be the first.");
        }
    }

    /// <summary>Compact past-event row under the rail · dimmed one-liner + memories chip.</summary>
    /// <summary>
    /// Photos pill visuals on an event flyer · the hit zone was already
    /// submitted before the card button; this draws and reacts.
    /// </summary>
    private static void DrawMemoryChip(Vector2 pillMin, float pillW, float pillH,
        SyncshellGalleryAlbumDto album, List<SyncshellGalleryImageDto>? gallery, Vector4 accent,
        CommunityPageState page, bool clicked, ImDrawListPtr dl, float scale)
    {
        var count = gallery?.Count(g => g.AlbumId == album.Id) ?? 0;
        var pillMax = pillMin + new Vector2(pillW, pillH);
        var hovered = ImGui.IsMouseHoveringRect(pillMin, pillMax);
        dl.AddRectFilled(pillMin, pillMax, SpheneUI.ColorUtils.ToU32(
            new Vector4(0f, 0f, 0f, hovered ? 0.75f : 0.55f)), pillH * 0.5f);
        var x = pillMin.X + 8f * scale;
        using (SpheneUI.Icons.PushFont())
        {
            var g = FontAwesomeIcon.Images.ToIconString();
            dl.AddText(new Vector2(x, pillMin.Y + (pillH - ImGui.GetTextLineHeight()) * 0.5f),
                SpheneUI.ColorUtils.ToU32(accent), g);
            x += ImGui.CalcTextSize(g).X + 4f * scale;
        }
        dl.AddText(new Vector2(x, pillMin.Y + (pillH - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text), count.ToString());
        if (hovered)
        {
            SpheneUI.AttachTooltip($"{count} photos in “{album.Name}”");
        }
        if (clicked)
        {
            page.Tab = SyncshellCommunitySection.Gallery;
            page.AlbumId = album.Id;
            page.PickingAlbumPhotos = false;
            page.SwitchT = 0f;
        }
    }

    private static bool DrawNews(SyncshellCommunityDto community, Vector4 accent,
        CommunityPageState? page, SyncshellCommunityEdit? edit = null, WidgetVis? vis = null)
    {
        var news = community.News;
        var canNews = edit is { OnSaveNews: not null } && page != null
            && (edit.CanManageNews || edit.CanSubmit);
        if ((news == null || news.Count == 0) && !canNews)
        {
            return false;
        }
        var composer = page?.NewsComposer;

        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var managePost = canNews || edit?.OnDeleteNews != null;
        Widget("news", "News", FontAwesomeIcon.Bullhorn, news?.Count, accent, () =>
        {
            // A brand-new post opens its entry right where it will land · at
            // the top of the feed, drafted or published in the same block.
            if (composer?.Open == true && composer.EditId == 0)
            {
                DrawNewsComposer(composer, edit!);
                SpheneUI.Layout.Separator();
            }
            if (news == null || news.Count == 0)
            {
                SpheneUI.Text.Muted("Nothing posted yet · the first announcement lands here.");
                return;
            }
            var ordered = news.OrderByDescending(p => p.IsPinned)
                .ThenByDescending(p => p.PublishedAt).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var post = ordered[i];
                if (i > 0)
                {
                    SpheneUI.Layout.Separator();
                }
                // Editing swaps the entry for its composer in place · the
                // block IS the editor while the pen is active.
                if (composer?.Open == true && composer.EditId == post.Id)
                {
                    DrawNewsComposer(composer, edit!);
                    continue;
                }
                var captured = post;
                // Deep-link focus: a feed/notification navigation carries the
                // post id - scroll it into view once, then release the focus.
                if (page?.FocusItemId == captured.Id)
                {
                    ImGui.SetScrollHereY(0.2f);
                    page.FocusItemId = null;
                }
                var rowMin = ImGui.GetCursorScreenPos();
                using (ImRaii.Group())
                {
                    var badgeW = 0f;
                    if (captured.IsPinned)
                    {
                        // Pinned reads as a badge, not a hard edge.
                        badgeW = SyncshellCard.StatusPill(dl, rowMin, "Pinned",
                            SpheneUI.Palette.SpheneGold, dot: false, scale);
                    }
                    if (captured.IsDraft)
                    {
                        badgeW += SyncshellCard.StatusPill(dl,
                            rowMin + new Vector2(badgeW > 0 ? badgeW + 6f * scale : 0f, 0f),
                            "Draft", SpheneUI.Colors.TextMuted, dot: false, scale);
                    }
                    if (badgeW > 0)
                    {
                        ImGui.SetCursorScreenPos(rowMin + new Vector2(badgeW + 8f * scale, 3f * scale));
                    }
                    SpheneUI.Text.Plain(captured.Title);
                    ImGui.SameLine();
                    SpheneUI.Text.Muted($"· {captured.PublishedAt.ToLocalTime():MMMM d}");
                    if (managePost)
                    {
                        ImGui.SameLine(0f, 10f * scale);
                        using (ImRaii.PushId($"news-{captured.Id}"))
                        {
                            if (canNews && SpheneUI.Icons.Button(FontAwesomeIcon.Pen))
                            {
                                composer!.OpenEdit(captured);
                            }
                            if (ImGui.IsItemHovered())
                            {
                                SpheneUI.AttachTooltip("Edit post");
                            }
                            if (edit!.OnDeleteNews != null)
                            {
                                ImGui.SameLine(0f, 4f * scale);
                                if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                                {
                                    edit.OnDeleteNews(captured);
                                }
                                if (ImGui.IsItemHovered())
                                {
                                    SpheneUI.AttachTooltip("Delete post");
                                }
                            }
                        }
                    }
                    if (badgeW > 0)
                    {
                        ImGui.SetCursorScreenPos(new Vector2(rowMin.X, ImGui.GetCursorScreenPos().Y));
                    }
                    MarkdownRenderer.RenderMarkdown(captured.Body, 0,
                        UI.Components.MarkdownComposer.MediaImageSource);
                    UI.Components.EntityChipList.Draw(captured.Body);
                    var commentOps = page != null
                        ? edit?.Comments?.Invoke(CommentTargetKind.News, captured.Id)
                        : null;
                    if (commentOps != null)
                    {
                        Widget($"news-comments-{captured.Id}", "Discussion",
                            FontAwesomeIcon.Comments, captured.CommentCount, accent,
                            () => page!.Comments.Draw(CommentTargetKind.News, captured.Id,
                                null, captured.GroupGID, commentOps,
                                captured.CommentsLocked, captured.CommentCount));
                    }
                }
            }
        },
        vis,
        canNews ? (FontAwesomeIcon.Plus, "New post", composer!.OpenNew) : null);
        return true;
    }

    /// <summary>
    /// The inline post composer · a surface card holding title, body and the
    /// pinned toggle with Publish / Save draft / Cancel. New posts render it
    /// at the feed's top; editing swaps the post's card for this in place.
    /// </summary>
    private static void DrawNewsComposer(CommunityPostComposer composer,
        SyncshellCommunityEdit edit)
    {
        var scale = SpheneUI.Scale;
        SpheneUI.Text.SectionHeader(composer.EditId == 0 ? "New post" : "Edit post");
        SpheneUI.Controls.InputTextWithHint("##nc-title", "Title…", ref composer.Title, 200,
            preferredWidth: 340f * scale);
        NewsBodyComposer.MentionProvider = () => edit.MentionCandidates?.Invoke() ?? [];
        NewsBodyComposer.Draw("##nc-body", ref composer.Body, 8000, 100f * scale);
        SpheneUI.Controls.Toggle("Pinned", ref composer.Pinned);
        var valid = !string.IsNullOrWhiteSpace(composer.Title)
            && !string.IsNullOrWhiteSpace(composer.Body);
        using (ImRaii.Group())
        {
            using (ImRaii.Disabled(!valid))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.PaperPlane, "Publish"))
                {
                    edit.OnSaveNews?.Invoke(composer, false);
                }
                if (ImGui.IsItemHovered())
                {
                    SpheneUI.AttachTooltip("Publish now · everyone sees it");
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileAlt, "Save draft"))
                {
                    edit.OnSaveNews?.Invoke(composer, true);
                }
                if (ImGui.IsItemHovered())
                {
                    SpheneUI.AttachTooltip("Keep it as a staff-only draft · publish later");
                }
            }
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
            {
                composer.Close();
            }
            SpheneUI.AttachTooltip("Discard");
        }
    }

    private static bool DrawRules(SyncshellCommunityDto community, Vector4 accent,
        SyncshellCommunityEdit? edit = null, CommunityPageState? page = null, WidgetVis? vis = null)
    {
        var inline = edit is { CanManagePage: true }
            && edit.OnRulesCommitted != null && page != null;
        if (string.IsNullOrWhiteSpace(community.RulesText) && !inline)
        {
            return false;
        }
        var editingRules = inline && page!.EditingRules;
        Widget("rules", "Rules", FontAwesomeIcon.Scroll, null, accent, () =>
        {
            if (editingRules)
            {
                var rules = page!.RulesBuffer;
                if (page.RulesFocusPending)
                {
                    ImGui.SetKeyboardFocusHere();
                    page.RulesFocusPending = false;
                }
                if (SpheneUI.Controls.InputTextMultiline("##community-rules-edit", ref rules,
                        4000, new(0, 90f * SpheneUI.Scale)))
                {
                    page.RulesBuffer = rules;
                }
                using (ImRaii.Group())
                {
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
                    {
                        page.EditingRules = false;
                        edit!.OnRulesCommitted?.Invoke(rules);
                    }
                    SpheneUI.AttachTooltip("Done editing");
                    ImGui.SameLine(0f, SpheneUI.Spacing.X12.Scaled);
                    SpheneUI.Text.Muted("Markdown is supported · shown on the join popup too.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(community.RulesText))
            {
                MarkdownRenderer.RenderMarkdown(community.RulesText!);
            }
            else
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Scroll, "No house rules",
                    "Nothing pinned here yet.", accent: accent);
            }
        },
        vis,
        inline && !editingRules
            ? (FontAwesomeIcon.Pen, "Edit rules", () =>
            {
                page!.RulesBuffer = community.RulesText ?? string.Empty;
                page.EditingRules = true;
                page.RulesFocusPending = true;
            })
            : null);
        return true;
    }

    private static bool DrawLinks(SyncshellCommunityDto community, Vector4 accent,
        SyncshellCommunityEdit? edit = null, CommunityPageState? page = null, WidgetVis? vis = null)
    {
        var links = community.Links?.Where(l => !string.IsNullOrWhiteSpace(l.Url)).ToList();
        var canEdit = edit is { CanManagePage: true, Ops.Patch: not null } && page != null;
        var editing = canEdit && page!.EditingLinks;
        if ((links == null || links.Count == 0) && !canEdit)
        {
            return false;
        }
        Widget("links", "Links", FontAwesomeIcon.Link, links?.Count, accent, () =>
        {
            if (editing)
            {
                DrawLinksEditor(page!, edit!);
            }
            else if (links == null || links.Count == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Link, "No links yet",
                    canEdit
                        ? "Use the pen on the section header to add Discord, website and socials."
                        : "The crew has not shared any links yet.", accent: accent);
            }
            else
            {
                SpheneUI.Layout.CardGrid(links, 170f * SpheneUI.Scale, (link, i) =>
                    DrawLinkCard(link.Label, link.Url, link.Icon, accent, i));
            }
        },
        vis,
        canEdit && !editing
            ? (FontAwesomeIcon.Pen, "Edit links", () =>
            {
                page!.LinksBuffer.Clear();
                if (community.Links != null)
                {
                    foreach (var l in community.Links)
                    {
                        page.LinksBuffer.Add(new CommunityLinkDraft { Label = l.Label, Url = l.Url, Icon = l.Icon });
                    }
                }
                page.EditingLinks = true;
            })
            : (System.ValueTuple<FontAwesomeIcon, string, Action>?)null);
        return true;
    }

    /// <summary>
    /// The inline links editor · label + URL rows with add/remove; Done
    /// patches the community's link list in one shot.
    /// </summary>
    private static void DrawLinksEditor(CommunityPageState page, SyncshellCommunityEdit edit)
    {
        var scale = SpheneUI.Scale;
        var buf = page.LinksBuffer;
        for (var i = 0; i < buf.Count; i++)
        {
            var link = buf[i];
            using (ImRaii.PushId($"communitylink{i}"))
            {
                var iconW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X;
                var preset = LinkBrandPresets.Find(link.Icon) ?? LinkBrandPresets.Custom;
                SpheneUI.Controls.Combo($"##preset{i}", LinkBrandPresets.All, p => p?.Label ?? "",
                    selected =>
                    {
                        if (selected == null)
                        {
                            return;
                        }
                        link.Icon = string.Equals(selected.Key, LinkBrandPresets.Custom.Key,
                            StringComparison.Ordinal) ? null : selected.Key;
                        if (string.IsNullOrWhiteSpace(link.Label))
                        {
                            link.Label = selected.Label;
                        }
                    }, preset, preferredWidth: 140f * scale);
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                var labelW = MathF.Max(140f * scale,
                    SpheneUI.Controls.BoundedAvailX() - iconW - SpheneUI.Spacing.X4.Scaled);
                _ = SpheneUI.Controls.InputTextWithHint("##label", "Label…", ref link.Label, 60,
                    trailingWidth: iconW, preferredWidth: labelW);
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                {
                    buf.RemoveAt(i);
                    break;
                }
                SpheneUI.AttachTooltip("Remove link");
                _ = SpheneUI.Controls.InputTextWithHint("##url", preset.UrlHint, ref link.Url, 300);
            }
            if (i < buf.Count - 1)
            {
                SpheneUI.Layout.Separator();
            }
        }
        using (ImRaii.Group())
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add link"))
            {
                buf.Add(new CommunityLinkDraft());
            }
            SpheneUI.AttachTooltip("Discord, website, venue listing · rendered as clickable chips");
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            if (SpheneUI.Controls.PrimaryButton("Done", FontAwesomeIcon.Check))
            {
                page.EditingLinks = false;
                var committed = buf
                    .Where(l => !string.IsNullOrWhiteSpace(l.Label) || !string.IsNullOrWhiteSpace(l.Url))
                    .Select(l => new SyncshellCommunityLinkDto(l.Label.Trim(), l.Url.Trim(),
                        string.IsNullOrWhiteSpace(l.Icon) ? null : l.Icon))
                    .ToList();
                edit.Ops?.Patch?.Invoke(c => c with { Links = committed });
            }
        }
    }

    /// <summary>
    /// Manage drill-ins · the same page, one level deeper. Back row up top,
    /// then the structural editor. Only reachable while <c>edit.Ops</c> is set.
    /// </summary>
    private static bool DrawManageSubpage(CommunitySubpage subpage, SyncshellCommunityDto community,
        Vector4 accent, SyncshellCommunityEdit edit, CommunityPageState page)
    {
        if (SpheneUI.Controls.SegmentButton("Back to page", FontAwesomeIcon.ChevronLeft,
                accent: accent))
        {
            page.Subpage = CommunitySubpage.None;
            page.SwitchT = 0f;
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        switch (subpage)
        {
            case CommunitySubpage.Settings:
                DrawSettingsSubpage(community, edit, page, accent);
                break;
        }
        return true;
    }

    /// <summary>
    /// Page settings · the structural half of the old studio: activation,
    /// discovery, visible sections, accent, widget order, custom sections.
    /// Discrete edits patch immediately; text lists buffer until Done.
    /// </summary>
    private static void DrawSettingsSubpage(SyncshellCommunityDto community,
        SyncshellCommunityEdit edit, CommunityPageState page, Vector4 accent)
    {
        var profile = community.Profile;
        var patch = edit.Ops!.Patch!;

        RailHeader("Activation", FontAwesomeIcon.PowerOff, null, accent);
        SpheneUI.Layout.RowGroup(() =>
        {
            var enabled = profile.IsEnabled;
            if (SpheneUI.Controls.Toggle("Community page enabled", ref enabled))
            {
                patch(c => c with { Profile = c.Profile with { IsEnabled = enabled } });
            }
            SpheneUI.Controls.HelpText("Off · members see nothing; staff keep editing.");
            var showOnJoin = profile.ShowOnJoin;
            if (SpheneUI.Controls.Toggle("Show on join", ref showOnJoin))
            {
                patch(c => c with { Profile = c.Profile with { ShowOnJoin = showOnJoin } });
            }
            SpheneUI.Controls.HelpText("Show this page when someone joins the syncshell manually.");
            var showOnArea = profile.ShowOnAreaBoundJoin;
            if (SpheneUI.Controls.Toggle("Show on area-bound join", ref showOnArea))
            {
                patch(c => c with
                {
                    Profile = c.Profile with { ShowOnAreaBoundJoin = showOnArea }
                });
            }
            SpheneUI.Controls.HelpText("Show this page when users auto-join via area binding.");
            var listed = community.IsListedPublicly;
            if (SpheneUI.Controls.Toggle("List in community directory", ref listed))
            {
                patch(c => c with { IsListedPublicly = listed });
            }
            SpheneUI.Controls.HelpText("Make this syncshell discoverable in the community listing.");
        }, id: "community-set-activation");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        DrawCommunityLocationEditor(community, edit, page, accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        RailHeader("Visible sections", FontAwesomeIcon.Eye, null, accent);
        SpheneUI.Text.Muted("Sections visitors can open · hidden ones stay editable here:");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        var visible = community.VisibleSections
            ?? Enum.GetValues<SyncshellCommunitySection>().ToList();
        var visibleSet = new HashSet<SyncshellCommunitySection>(visible);
        foreach (var (section, label, _) in SectionOrder)
        {
            var on = visibleSet.Contains(section);
            if (SpheneUI.Controls.SegmentButton(label,
                    on ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                    accent: on ? accent : SpheneUI.Colors.TextMuted, active: on))
            {
                if (on) visibleSet.Remove(section);
                else visibleSet.Add(section);
                var ordered = visibleSet
                    .OrderBy(s => Array.FindIndex(SectionOrder, e => e.Section == s)).ToList();
                patch(c => c with { VisibleSections = ordered });
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        RailHeader("Design", FontAwesomeIcon.Palette, null, accent);
        SpheneUI.Layout.RowGroup(() =>
        {
            var accentOn = profile.AccentColor != null;
            if (SpheneUI.Controls.Toggle("Custom accent color", ref accentOn))
            {
                patch(c => c with
                {
                    Profile = c.Profile with
                    {
                        AccentColor = accentOn ? ToHexRgb(SpheneUI.Palette.CrystalBlue) : null
                    }
                });
            }
            if (accentOn)
            {
                var color = ParseAccent(profile.AccentColor) ?? SpheneUI.Palette.CrystalBlue;
                if (SpheneUI.Controls.ColorEdit("Accent", ref color))
                {
                    patch(c => c with { Profile = c.Profile with { AccentColor = ToHexRgb(color) } });
                }
            }
        }, id: "community-set-design");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        DrawOverviewWidgetsEditor(community, patch, accent);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
        DrawSectionsEditor(community, edit, page, accent);
    }

    /// <summary>
    /// Community location management inside page settings - publish toggle,
    /// source pick (bound property / custom housing) with the shared address
    /// editors, preview + validation, and a dedicated save that writes the
    /// snapshot through <see cref="SyncshellCommunityEdit.OnSetCommunityLocation"/>.
    /// Publication is never implicit; "No location" unpublishes and clears.
    /// </summary>
    private static void DrawCommunityLocationEditor(SyncshellCommunityDto community,
        SyncshellCommunityEdit edit, CommunityPageState page, Vector4 accent)
    {
        RailHeader("Community location", FontAwesomeIcon.Home, null, accent);
        var travel = edit.Travel;
        var composer = page.LocationEditor;
        var scale = SpheneUI.Scale;
        if (!ReferenceEquals(page.LocationEditorSeed, community))
        {
            page.LocationEditorSeed = community;
            composer.SeedDestinationFields(community.CommunityDestination);
            page.LocationPublish = community.CommunityDestinationPublished;
            page.LocationSaving = false;
        }

        var stored = community.CommunityDestination;
        SpheneUI.Text.MutedWrapped(community.CommunityDestinationPublished && stored != null
            ? "Published - members and permitted visitors see it on the page and can travel there."
            : stored != null
                ? "Configured but not published - only staff can see it."
                : "No community location configured.");

        var kinds = new[] { "No location", "Use Syncshell property", "Custom housing address" };
        SpheneUI.Controls.Combo("##cl-kind", kinds, s => s ?? string.Empty,
            onSelected: p => composer.DestKind = p is string s
                ? Array.IndexOf(kinds, s) : 0,
            initialSelectedItem: kinds[Math.Clamp(composer.DestKind, 0, kinds.Length - 1)],
            preferredWidth: 260f * scale);

        if (composer.DestKind != 0 && travel == null)
        {
            SpheneUI.Text.MutedWrapped("Address services aren't available on this surface.");
        }
        else
        {
            switch (composer.DestKind)
            {
                case 1:
                    DrawBoundPropertyPicker(composer, travel!, scale);
                    break;
                case 2:
                    DrawCustomAddressEditor(composer, travel!, scale);
                    break;
                default:
                    if (stored != null)
                    {
                        SpheneUI.Text.MutedWrapped(
                            "Saving removes the stored address and unpublishes the location.");
                    }
                    break;
            }

            if (composer.DestKind != 0)
            {
                SpheneUI.Controls.InputTextWithHint("##cl-label", "Label (optional)…",
                    ref composer.DestLabel, EventDestinationValidator.MaxDisplayLabelLength,
                    preferredWidth: 340f * scale);
            }
        }

        var built = composer.BuildDestination();
        if (composer.DestKind != 0)
        {
            if (built != null)
            {
                SpheneUI.Text.Muted($"{DestinationTitle(built, travel)} - {DestinationSubtitle(built, travel)}");
            }
            else
            {
                SpheneUI.Text.Colored(composer.DestKind == 1
                    ? "Pick a property to publish."
                    : "Complete all required fields - the address can't be saved yet.",
                    SpheneUI.Colors.Danger);
            }
        }

        if (composer.DestKind == 0)
        {
            page.LocationPublish = false;
        }
        using (ImRaii.Disabled(built == null && composer.DestKind != 0 && stored == null))
        {
            SpheneUI.Controls.Toggle("Publish community location", ref page.LocationPublish);
        }
        SpheneUI.Controls.HelpText(
            "Off · the address stays a staff-only draft. Publishing is always an explicit choice.");

        // Save intent - clear (kind 0) sends the None sentinel; a valid build
        // replaces the snapshot; an invalid build writes null = keep stored.
        var destChanged = composer.DestKind == 0
            ? stored != null
            : !SameDestination(built, stored);
        var dirty = destChanged
            || page.LocationPublish != community.CommunityDestinationPublished;
        EventDestinationDto? saveDest = composer.DestKind == 0
            ? new EventDestinationDto { Kind = (int)EventDestinationKind.None }
            : built;
        using (ImRaii.Disabled(!dirty || page.LocationSaving || edit.OnSetCommunityLocation == null))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Save location"))
            {
                page.LocationSaving = true;
                edit.OnSetCommunityLocation?.Invoke(saveDest, page.LocationPublish);
            }
        }
        SpheneUI.AttachTooltip(edit.OnSetCommunityLocation == null
            ? "Not available on this surface"
            : "Writes the address snapshot + publish state to the server");
    }

    /// <summary>Field-level equality for destination drafts - drives the
    /// settings editor's dirty check so unchanged values aren't re-saved.</summary>
    private static bool SameDestination(EventDestinationDto? a, EventDestinationDto? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.Kind == b.Kind && a.WorldId == b.WorldId
            && a.DistrictTerritoryId == b.DistrictTerritoryId && a.Ward == b.Ward
            && a.IsApartment == b.IsApartment && a.Plot == b.Plot && a.Room == b.Room
            && a.ApartmentSubdivision == b.ApartmentSubdivision
            && a.TerritoryId == b.TerritoryId && a.AetheryteId == b.AetheryteId
            && MathF.Abs(a.MapX - b.MapX) < 0.01f && MathF.Abs(a.MapY - b.MapY) < 0.01f
            && string.Equals(a.DisplayLabel, b.DisplayLabel, StringComparison.Ordinal);
    }

    /// <summary>Overview widget order · the digest renders in exactly this order.</summary>
    private static void DrawOverviewWidgetsEditor(SyncshellCommunityDto community,
        Action<Func<SyncshellCommunityDto, SyncshellCommunityDto>> patch, Vector4 accent)
    {
        var layout = community.OverviewWidgets?.Count > 0
            ? community.OverviewWidgets.ToList()
            : DefaultOverviewWidgets.ToList();
        RailHeader("Overview layout", FontAwesomeIcon.ThLarge, null, accent);
        SpheneUI.Text.Muted("The Overview digest renders its widgets in this order:");
        SpheneUI.Layout.RowGroup(() =>
        {
            var scale = SpheneUI.Scale;
            for (var i = 0; i < layout.Count; i++)
            {
                var key = layout[i];
                var (icon, label) = key switch
                {
                    "upnext" => (FontAwesomeIcon.Bolt, "Up next spotlight"),
                    "location" => (FontAwesomeIcon.Home, "Community location"),
                    "teasers" => (FontAwesomeIcon.Newspaper, "News & events teasers"),
                    "about" => (FontAwesomeIcon.Info, "About excerpt"),
                    "staff" => (FontAwesomeIcon.UserShield, "Staff rows"),
                    "links" => (FontAwesomeIcon.Link, "Link chips"),
                    _ => (FontAwesomeIcon.Cube, key),
                };
                SpheneUI.Icons.Icon(icon, SpheneUI.Colors.TextMuted);
                ImGui.SameLine(0f, 8f * scale);
                SpheneUI.Text.Plain(label);
                var bw = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ChevronUp).X;
                var right = ImGui.GetCursorScreenPos().X + SpheneUI.Controls.BoundedAvailX();
                ImGui.SetCursorScreenPos(new Vector2(right - bw * 2f - 4f * scale,
                    ImGui.GetCursorScreenPos().Y - 2f * scale));
                using (ImRaii.PushId($"widget-{i}"))
                {
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronUp, enabled: i > 0))
                    {
                        (layout[i - 1], layout[i]) = (layout[i], layout[i - 1]);
                        patch(c => c with { OverviewWidgets = layout.ToList() });
                    }
                    ImGui.SameLine(0f, 2f * scale);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronDown,
                            enabled: i < layout.Count - 1))
                    {
                        (layout[i + 1], layout[i]) = (layout[i], layout[i + 1]);
                        patch(c => c with { OverviewWidgets = layout.ToList() });
                    }
                }
            }
        }, id: "community-set-widgets");
    }

    /// <summary>Buffered custom-section rows · Done commits the whole list.</summary>
    private static void DrawSectionsEditor(SyncshellCommunityDto community,
        SyncshellCommunityEdit edit, CommunityPageState page, Vector4 accent)
    {
        page.SectionsBuffer ??= (community.Profile.Sections ?? [])
            .Select(s => new CommunitySectionDraft { Title = s.Title, Body = s.Body }).ToList();
        var buf = page.SectionsBuffer;
        RailHeader("Custom sections", FontAwesomeIcon.LayerGroup, buf.Count, accent);
        SpheneUI.Text.Muted("Free-form titled cards inside the About area:");
        SpheneUI.Layout.Surface(() =>
        {
            var scale = SpheneUI.Scale;
            for (var i = 0; i < buf.Count; i++)
            {
                var section = buf[i];
                using (ImRaii.PushId($"profilesection{i}"))
                {
                    var iconGap = SpheneUI.Spacing.X4.Scaled;
                    var iconW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ChevronUp).X
                        + SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ChevronDown).X
                        + SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Trash).X + iconGap * 2f;
                    _ = SpheneUI.Controls.InputText("##title", ref section.Title, 80,
                        trailingWidth: iconW);
                    ImGui.SameLine(0f, iconGap);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronUp, enabled: i > 0))
                    {
                        (buf[i - 1], buf[i]) = (buf[i], buf[i - 1]);
                    }
                    ImGui.SameLine(0f, iconGap);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronDown,
                            enabled: i < buf.Count - 1))
                    {
                        (buf[i + 1], buf[i]) = (buf[i], buf[i + 1]);
                    }
                    ImGui.SameLine(0f, iconGap);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                    {
                        buf.RemoveAt(i);
                        break;
                    }
                    _ = SpheneUI.Controls.InputTextMultiline("##body", ref section.Body, 2000,
                        new Vector2(0, 60f * scale));
                }
                if (i < buf.Count - 1)
                {
                    SpheneUI.Layout.Separator();
                }
            }
            using (ImRaii.Group())
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add section"))
                {
                    buf.Add(new CommunitySectionDraft());
                }
                SpheneUI.AttachTooltip("Venue hours, dress code · a titled content card");
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (SpheneUI.Controls.PrimaryButton("Done", FontAwesomeIcon.Check))
                {
                    var committed = buf
                        .Where(s => !string.IsNullOrWhiteSpace(s.Title)
                            || !string.IsNullOrWhiteSpace(s.Body))
                        .Select(s => new SyncshellProfileSectionDto(s.Title.Trim(), s.Body.Trim()))
                        .ToList();
                    page.SectionsBuffer = null;
                    edit.Ops?.Patch?.Invoke(c => c with
                    {
                        Profile = c.Profile with { Sections = committed }
                    });
                }
            }
        }, id: "community-set-sections");
    }

    /// <summary>
    /// Jobs & rights · the role editor half of the staff admin page. Ranked
    /// role list on the left, the selected role's color/name/rank/rights on
    /// the right. Shared by every host that embeds the same editor.
    /// </summary>
    internal static void DrawStaffRolesPanel(SyncshellCommunityDto community,
        CommunityManageOps ops, CommunityPageState page, Vector4 accent)
    {
        var roles = community.StaffRoles ?? [];
        var assignments = community.StaffAssignments ?? [];

        SpheneUI.Text.MutedWrapped(
            "Roles work like Discord roles · order is rank (top = highest) and each role carries the rights its holders get on this community.");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var ordered = roles.OrderBy(r => r.SortOrder).ToList();
        if (ordered.All(r => r.Id != page.SelectedStaffRoleId))
        {
            page.SelectedStaffRoleId = ordered.Count > 0 ? ordered[0].Id : 0;
        }
        var selected = ordered.FirstOrDefault(r => r.Id == page.SelectedStaffRoleId);
        var scale = SpheneUI.Scale;
        var paneH = 320f * scale;

        using (var panes = ImRaii.Child($"staff-roles-{community.Profile.GroupGID}", new Vector2(0f, paneH), false))
        {
            if (panes.Success)
            {
                // Left column · the ranked role list, Discord-style.
                using (ImRaii.Child("staff-role-list", new Vector2(200f * scale, 0f), true,
                           ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                {
                    SpheneUI.Text.Muted("ROLES");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var role = ordered[i];
                        var holders = assignments.Count(a => a.RoleId == role.Id);
                        using (ImRaii.PushId($"staffrolelist{role.Id}"))
                        {
                            var rowH = ImGui.GetFrameHeight() + 6f * scale;
                            var rowW = SpheneUI.Controls.BoundedAvailX();
                            var dl = ImGui.GetWindowDrawList();
                            var min = ImGui.GetCursorScreenPos();
                            if (ImGui.InvisibleButton("##rolesel", new Vector2(rowW, rowH)))
                            {
                                page.SelectedStaffRoleId = role.Id;
                            }
                            var hovered = ImGui.IsItemHovered();
                            var max = min + new Vector2(rowW, rowH);
                            var isSel = role.Id == page.SelectedStaffRoleId;
                            if (isSel || hovered)
                            {
                                dl.AddRectFilled(min, max,
                                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent,
                                        isSel ? 0.18f : 0.08f)), 5f * scale);
                            }
                            var cy = min.Y + rowH * 0.5f;
                            var roleColor = StaffRoleColorVec(role.ColorArgb);
                            if (roleColor.W <= 0.01f)
                            {
                                roleColor = accent;
                            }
                            dl.AddCircleFilled(new Vector2(min.X + 10f * scale, cy), 4f * scale,
                                SpheneUI.ColorUtils.ToU32(roleColor));
                            var countText = holders > 0 ? holders.ToString() : null;
                            var countW = countText != null ? ImGui.CalcTextSize(countText).X + 10f * scale : 0f;
                            if (countText != null)
                            {
                                dl.AddText(new Vector2(max.X - countW, min.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f),
                                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), countText);
                            }
                            var nameX = min.X + 20f * scale;
                            var roleIcon = StaffRoleIcons.Parse(role.Icon);
                            var textY = min.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
                            float iconW;
                            using (SpheneUI.Icons.PushFont())
                            {
                                var glyph = roleIcon.ToIconString();
                                dl.AddText(new Vector2(nameX, textY),
                                    SpheneUI.ColorUtils.ToU32(roleColor), glyph);
                                iconW = ImGui.CalcTextSize(glyph).X;
                            }
                            nameX += iconW + 5f * scale;
                            dl.AddText(new Vector2(nameX, textY),
                                SpheneUI.ColorUtils.ToU32(isSel ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextSecondary),
                                SpheneUI.Text.Ellipsize(role.Name, max.X - countW - nameX - 6f * scale));
                            ImGui.SetCursorScreenPos(min + new Vector2(0f, rowH));
                        }
                    }

                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    _ = SpheneUI.Controls.InputTextWithHint("##newrole", "New role…",
                        ref page.NewRoleName, 60, preferredWidth: 120f * scale);
                    ImGui.SameLine(0f, 6f * scale);
                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(page.NewRoleName)))
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                        {
                            var name = page.NewRoleName.Trim();
                            page.NewRoleName = string.Empty;
                            ops.CreateStaffRole?.Invoke(name);
                        }
                    }
                    SpheneUI.AttachTooltip("Create role · it lands at the bottom of the rank order");
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);

                // Right column · the selected role's editor.
                using (ImRaii.Child("staff-role-edit", new Vector2(0f, 0f), true))
                {
                    if (selected == null)
                    {
                        SpheneUI.Text.MutedWrapped(
                            "No roles yet · create one on the left, then give it rights here.");
                    }
                    else
                    {
                        var color = StaffRoleColorVec(selected.ColorArgb);
                        if (SpheneUI.Controls.ColorPickerSwatch("##rolecolor", ref color))
                        {
                            ops.SaveStaffRole?.Invoke(selected with { ColorArgb = StaffRoleColorArgb(color) });
                        }
                        ImGui.SameLine(0f, 6f * scale);
                        // Role icon · the VenuePlus-style symbol shown next to
                        // the role everywhere it appears, synced via the
                        // role's Icon field.
                        var iconFlyoutId = $"roleicon:{selected.Id}";
                        if (SpheneUI.Icons.Button(StaffRoleIcons.Parse(selected.Icon)))
                        {
                            page.RoleIconFilter = string.Empty;
                            page.RoleIconCategory = "All";
                            SpheneUI.Popups.ToggleFlyout(iconFlyoutId);
                        }
                        SpheneUI.AttachTooltip("Role icon · shown next to the role's name");
                        if (SpheneUI.Popups.BeginFlyout(iconFlyoutId))
                        {
                            if (StaffRoleIcons.DrawPicker(ref page.RoleIconFilter,
                                    ref page.RoleIconCategory, out var pickedIcon))
                            {
                                ops.SaveStaffRole?.Invoke(selected with { Icon = pickedIcon });
                                SpheneUI.Popups.CloseFlyout(iconFlyoutId);
                            }
                            SpheneUI.Popups.EndFlyout();
                        }
                        ImGui.SameLine(0f, 6f * scale);
                        var name = selected.Name;
                        if (SpheneUI.Controls.InputTextWithHint("##rolename", "Role name…", ref name, 60,
                                preferredWidth: 180f * scale)
                            && !string.IsNullOrWhiteSpace(name)
                            && !string.Equals(name, selected.Name, StringComparison.Ordinal))
                        {
                            ops.SaveStaffRole?.Invoke(selected with { Name = name.Trim() });
                        }
                        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                        var rank = ordered.IndexOf(selected);
                        SpheneUI.Text.Muted($"Rank #{rank + 1}");
                        SpheneUI.AttachTooltip("Position in the staff order · top role outranks the rest");

                        var btnW = ImGui.GetFrameHeight() * 3f + 10f * scale;
                        SpheneUI.Controls.DockToRightEdge(btnW);
                        using (ImRaii.Disabled(rank <= 0))
                        {
                            if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronUp))
                            {
                                ops.MoveStaffRole?.Invoke(selected, -1);
                            }
                        }
                        SpheneUI.AttachTooltip("Move up · higher rank");
                        ImGui.SameLine(0f, 2f * scale);
                        using (ImRaii.Disabled(rank >= ordered.Count - 1))
                        {
                            if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronDown))
                            {
                                ops.MoveStaffRole?.Invoke(selected, +1);
                            }
                        }
                        SpheneUI.AttachTooltip("Move down · lower rank");
                        ImGui.SameLine(0f, 6f * scale);
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                        {
                            ops.DeleteStaffRole?.Invoke(selected);
                            page.SelectedStaffRoleId = 0;
                        }
                        SpheneUI.AttachTooltip("Delete this role · holders keep their membership");

                        SpheneUI.Layout.Separator();
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

                        string? lastGroup = null;
                        foreach (var (grp, flag, label, tip) in StaffRoleFlags)
                        {
                            if (!string.Equals(grp, lastGroup, StringComparison.Ordinal))
                            {
                                if (lastGroup != null)
                                {
                                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                                }
                                SpheneUI.Text.Muted(grp.ToUpperInvariant());
                                lastGroup = grp;
                            }
                            var on = selected.Permissions.HasFlag(flag);
                            if (SpheneUI.Controls.Toggle(label, ref on))
                            {
                                var perms = on ? selected.Permissions | flag : selected.Permissions & ~flag;
                                ops.SaveStaffRole?.Invoke(selected with { Permissions = perms });
                            }
                            SpheneUI.AttachTooltip(tip);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Member → job assignment rows · the members half of the staff admin
    /// page. Each row's dropdown previews the member's currently assigned
    /// role (live server state), never a stale "Unset Value".
    /// </summary>
    internal static void DrawStaffMembersPanel(SyncshellCommunityDto community,
        CommunityManageOps ops, IReadOnlyList<CommunityStaffMember> members,
        CommunityPageState page, Vector4 accent)
    {
        var roles = community.StaffRoles ?? [];
        var assignments = community.StaffAssignments ?? [];

        // Pending multi-toggles survive until the refreshed snapshot lands -
        // computing the next set off a stale list would drop earlier picks.
        if (!ReferenceEquals(page.StaffAssignmentSource, community.StaffAssignments))
        {
            page.PendingStaffRoles.Clear();
            page.StaffAssignmentSource = community.StaffAssignments;
        }

        // Roles only exist on top of staff membership - the panel lists staff
        // members (plus any member still holding a legacy assignment so it can
        // be cleared), not the full roster.
        var listed = members.Where(m => m.IsModerator || assignments.Any(a =>
            string.Equals(a.UserUID, m.Uid, StringComparison.Ordinal))).ToList();
        if (members.Count == 0)
        {
            SpheneUI.Text.Muted("No member list available · open this from your own syncshell.");
            return;
        }
        if (listed.Count == 0)
        {
            SpheneUI.Text.Muted("No staff members yet - promote a member to staff first.");
            return;
        }
        SpheneUI.Text.Muted("Assign jobs to staff members · one member may hold several, their rights union.");
        SpheneUI.Layout.RowGroup(() =>
        {
            var scale = SpheneUI.Scale;
            var ordered = roles.OrderBy(r => r.SortOrder).ToList();
            foreach (var member in listed)
            {
                using (ImRaii.PushId($"staffassign{member.Uid}"))
                {
                    var nameMax = 200f * scale;
                    var nameText = SpheneUI.Text.Ellipsize(member.DisplayName, nameMax);
                    SpheneUI.Text.Colored(nameText, SpheneUI.Colors.TextPrimary);
                    if (!string.Equals(nameText, member.DisplayName, StringComparison.Ordinal))
                    {
                        SpheneUI.AttachTooltip(member.DisplayName);
                    }
                    if (member.IsModerator)
                    {
                        ImGui.SameLine(0f, 6f * scale);
                        SpheneUI.Text.Muted("(staff)");
                    }

                    var effectiveIds = page.PendingStaffRoles.TryGetValue(member.Uid, out var pending)
                        ? pending
                        : assignments.Where(a => string.Equals(a.UserUID, member.Uid,
                            StringComparison.Ordinal)).Select(a => a.RoleId).ToList();
                    var memberRoles = ordered.Where(r => effectiveIds.Contains(r.Id)).ToList();

                    // Every shell role inline as an icon toggle - filled chip
                    // in the role's color when held, dim outline when free.
                    ImGui.SameLine(nameMax + 20f * scale);
                    var pillH = ImGui.GetTextLineHeight() + 6f * scale;
                    var firstPill = true;
                    foreach (var role in ordered)
                    {
                        var held = memberRoles.Any(r => r.Id == role.Id);
                        var rc = StaffRoleColorVec(role.ColorArgb);
                        if (rc.W <= 0.01f)
                        {
                            rc = accent;
                        }
                        var glyph = StaffRoleIcons.Parse(role.Icon).ToIconString();
                        float glyphW;
                        using (SpheneUI.Icons.PushFont())
                        {
                            glyphW = ImGui.CalcTextSize(glyph).X;
                        }
                        var pillW = glyphW + 12f * scale;
                        if (!firstPill)
                        {
                            ImGui.SameLine(0f, 6f * scale);
                        }
                        firstPill = false;

                        var pmin = ImGui.GetCursorScreenPos();
                        if (ImGui.InvisibleButton($"##rolepill{role.Id}", new Vector2(pillW, pillH)))
                        {
                            var next = effectiveIds.Where(id => id != role.Id).ToList();
                            if (!held)
                            {
                                next.Add(role.Id);
                            }
                            page.PendingStaffRoles[member.Uid] = next;
                            ops.SetStaffAssignment?.Invoke(member.Uid, next);
                        }
                        var pillHovered = ImGui.IsItemHovered();
                        if (pillHovered)
                        {
                            SpheneUI.AttachTooltip($"{(held ? "Remove" : "Assign")} {role.Name}");
                        }
                        var pmax = pmin + new Vector2(pillW, pillH);
                        var dl = ImGui.GetWindowDrawList();
                        var fg = held ? rc : SpheneUI.Colors.TextMuted;
                        dl.AddRectFilled(pmin, pmax,
                            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(rc,
                                held ? 0.18f : pillHovered ? 0.10f : 0.05f)), pillH * 0.5f);
                        dl.AddRect(pmin, pmax,
                            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(rc,
                                held ? 0.55f : 0.22f)), pillH * 0.5f);
                        using (SpheneUI.Icons.PushFont())
                        {
                            dl.AddText(new Vector2(pmin.X + (pillW - glyphW) * 0.5f,
                                    pmin.Y + (pillH - ImGui.GetTextLineHeight()) * 0.5f),
                                SpheneUI.ColorUtils.ToU32(fg), glyph);
                        }
                    }
                }
            }
        }, id: "community-staff-assign");
    }

    /// <summary>
    /// The gallery's album-manage expand · rename, event link, cover pick,
    /// delete + create. Ported from the old studio's album editor.
    /// </summary>
    private static void DrawAlbumManager(SyncshellCommunityDto community,
        SyncshellCommunityEdit edit, CommunityPageState page, Vector4 accent)
    {
        var ops = edit.Ops!;
        var albums = community.Albums ?? [];
        var events = community.Events?.Where(e => !string.IsNullOrWhiteSpace(e.Title)).ToList() ?? [];
        var images = community.Gallery ?? [];
        var scale = SpheneUI.Scale;
        for (var i = 0; i < albums.Count; i++)
        {
            if (i > 0)
            {
                SpheneUI.Layout.Separator();
            }
            var album = albums[i];
            var draft = page.AlbumDrafts.GetValueOrDefault(album.Id);
            AlbumEditDraft EnsureDraft()
            {
                if (draft == null)
                {
                    draft = AlbumEditDraft.From(album);
                    page.AlbumDrafts[album.Id] = draft;
                }
                return draft;
            }
            using (ImRaii.PushId($"communityalbum{album.Id}"))
            {
                SpheneUI.Icons.Icon(FontAwesomeIcon.Images, accent);
                ImGui.SameLine(0f, 6f * scale);
                var name = draft?.Name ?? album.Name;
                if (SpheneUI.Controls.InputTextWithHint("##albumname", "Album name…", ref name, 100,
                        preferredWidth: 150f * scale))
                {
                    EnsureDraft().Name = name;
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);

                var linked = draft?.LinkedEventId ?? album.LinkedEventId;
                var linkedLabel = linked == null
                    ? "No event link"
                    : $"Event: {events.FirstOrDefault(e => e.Id == linked)?.Title ?? "#" + linked}";
                if (SpheneUI.Controls.BeginComboBox("##albumevent", linkedLabel, 190f * scale))
                {
                    if (SpheneUI.Controls.Selectable("No event link##noval", linked == null))
                    {
                        EnsureDraft().LinkedEventId = null;
                    }
                    foreach (var ev in events)
                    {
                        if (SpheneUI.Controls.Selectable($"{ev.Title}##e{ev.Id}", linked == ev.Id))
                        {
                            EnsureDraft().LinkedEventId = ev.Id;
                        }
                    }
                    SpheneUI.Controls.EndComboBox();
                }
                SpheneUI.AttachTooltip("Link this album to an event · it surfaces as the event's memories");

                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                var cover = draft?.CoverImageId ?? album.CoverImageId;
                var coverLabel = cover == null
                    ? "Cover: first photo"
                    : $"Cover: {images.FirstOrDefault(g => g.Id == cover)?.FileName ?? "#" + cover}";
                if (SpheneUI.Controls.BeginComboBox("##albumcover", coverLabel, 150f * scale))
                {
                    if (SpheneUI.Controls.Selectable("Cover: first photo##noval", cover == null))
                    {
                        EnsureDraft().CoverImageId = null;
                    }
                    foreach (var g in images)
                    {
                        if (g.AlbumId != album.Id)
                        {
                            continue;
                        }
                        if (SpheneUI.Controls.Selectable($"{g.FileName}##g{g.Id}", cover == g.Id))
                        {
                            EnsureDraft().CoverImageId = g.Id;
                        }
                    }
                    SpheneUI.Controls.EndComboBox();
                }

                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                var audienceIdx = draft?.Audience ?? (int)album.Audience;
                var audienceNames = new[] { "Everyone", "Staff only", "Selected" };
                var audienceLabel = $"Visible: {audienceNames[Math.Clamp(audienceIdx, 0, audienceNames.Length - 1)]}";
                if (SpheneUI.Controls.BeginComboBox("##albumaud", audienceLabel, 140f * scale))
                {
                    for (var a = 0; a < audienceNames.Length; a++)
                    {
                        if (SpheneUI.Controls.Selectable($"{audienceNames[a]}##a{a}",
                                audienceIdx == a))
                        {
                            EnsureDraft().Audience = a;
                        }
                    }
                    SpheneUI.Controls.EndComboBox();
                }
                SpheneUI.AttachTooltip("Who may see this album · staff and album managers always do");

                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (draft?.Saving == true)
                {
                    SpheneUI.Icons.Icon(FontAwesomeIcon.Spinner, SpheneUI.Colors.TextMuted);
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    SpheneUI.Text.Muted("Saving…");
                }
                else
                {
                    var dirty = draft != null && draft.Differs(album);
                    using (ImRaii.Disabled(!dirty))
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Save))
                        {
                            var pending = EnsureDraft();
                            pending.Saving = true;
                            pending.Error = null;
                            var albumId = album.Id;
                            var toSave = pending.ApplyTo(album);
                            _ = Task.Run(async () =>
                            {
                                var ok = ops.SaveAlbum != null
                                    && await ops.SaveAlbum(toSave).ConfigureAwait(false);
                                pending.Saving = false;
                                if (ok)
                                {
                                    page.AlbumDrafts.TryRemove(albumId, out _);
                                    page.AlbumSavedId = albumId;
                                    page.AlbumSavedAtTicks = Environment.TickCount64;
                                }
                                else
                                {
                                    pending.Error = "Couldn't save the album - changes kept, try again.";
                                }
                            });
                        }
                    }
                    SpheneUI.AttachTooltip("Save changes");

                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    using (ImRaii.Disabled(!dirty))
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
                        {
                            page.AlbumDrafts.TryRemove(album.Id, out _);
                        }
                    }
                    SpheneUI.AttachTooltip("Discard changes");

                    if (page.AlbumSavedId == album.Id
                        && Environment.TickCount64 - page.AlbumSavedAtTicks < 2500)
                    {
                        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                        SpheneUI.Icons.Icon(FontAwesomeIcon.Check, SpheneUI.Palette.Success);
                        ImGui.SameLine(0f, 2f * scale);
                        SpheneUI.Text.Muted("Saved");
                    }
                }

                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                {
                    _ = ops.DeleteAlbum?.Invoke(album);
                }
                SpheneUI.AttachTooltip("Delete album (photos are kept)");

                if (draft?.Error is { } errorText)
                {
                    SpheneUI.Text.ColoredWrapped(errorText, SpheneUI.Palette.Error);
                }

                var allowed = draft?.AllowedUids ?? album.AllowedUids;
                if (audienceIdx == (int)SyncshellGalleryAudience.SelectedMembers
                    && ops.GetStaffMembers?.Invoke() is { } albumMembers)
                {
                    var chipGap = 4f * scale;
                    var first = true;
                    foreach (var m in albumMembers)
                    {
                        var on = allowed?.Contains(m.Uid, StringComparer.Ordinal) == true;
                        if (!first)
                        {
                            ImGui.SameLine(0f, chipGap);
                        }
                        if (SpheneUI.Controls.ToggleChip($"{m.DisplayName}##al-{album.Id}-{m.Uid}", ref on))
                        {
                            var uids = EnsureDraft().AllowedUids;
                            if (on && !uids.Contains(m.Uid, StringComparer.Ordinal)) uids.Add(m.Uid);
                            if (!on)
                            {
                                uids.RemoveAll(u => string.Equals(u, m.Uid, StringComparison.Ordinal));
                            }
                        }
                        first = false;
                    }
                }
            }
        }

        if (albums.Count > 0)
        {
            SpheneUI.Layout.Separator();
        }
        _ = SpheneUI.Controls.InputTextWithHint("##newalbum", "New album name…",
            ref page.NewAlbumName, 100, preferredWidth: 150f * scale);
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(page.NewAlbumName)))
        {
            if (SpheneUI.Controls.PrimaryButton("Create album", FontAwesomeIcon.FolderPlus))
            {
                var name = page.NewAlbumName.Trim();
                page.NewAlbumName = string.Empty;
                _ = ops.CreateAlbum?.Invoke(name);
            }
        }
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        if (SpheneUI.Controls.SegmentButton("Done editing", FontAwesomeIcon.Check))
        {
            SpheneUI.LogDebug("[CommUi] ManageAlbums=false");
            page.ManagingAlbums = false;
            page.AlbumDrafts.Clear();
        }
    }

    private static readonly (string Group, SyncshellStaffPermission Flag, string Label, string Tooltip)[] StaffRoleFlags =
    [
        ("Content", SyncshellStaffPermission.ManagePage, "Page",
            "Edit the community page itself - sections, texts, branding"),
        ("Content", SyncshellStaffPermission.ManageNews, "News",
            "Create, edit and delete news posts"),
        ("Content", SyncshellStaffPermission.ManageEvents, "Events",
            "Create, edit and cancel events"),
        ("Content", SyncshellStaffPermission.ManageGallery, "Gallery",
            "Manage photos and albums, approve submissions"),
        ("Members", SyncshellStaffPermission.ManageMembership, "Join requests",
            "Review and decide membership applications"),
        ("Members", SyncshellStaffPermission.ManageMembers, "Kick & pin",
            "Pin members and remove non-staff members"),
        ("Members", SyncshellStaffPermission.ManageBans, "Bans",
            "Ban and unban members"),
        ("Members", SyncshellStaffPermission.ModerateContent, "Moderation",
            "Hide comments, lock threads, resolve reports"),
        ("Access", SyncshellStaffPermission.AccessStaffArea, "Staff area",
            "See staff-only pages and tooling"),
        ("Access", SyncshellStaffPermission.ManageInvites, "Invites",
            "Create one-time invites"),
        ("Access", SyncshellStaffPermission.ManageRoles, "Roles",
            "Edit jobs and assign them to members"),
        ("Access", SyncshellStaffPermission.ManageSettings, "Shell settings",
            "Join policy, publishing and listing settings"),
    ];

    private static Vector4 StaffRoleColorVec(uint argb)
        => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f,
            (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);

    private static uint StaffRoleColorArgb(Vector4 color)
    {
        static uint Byte(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return (Byte(color.W) << 24) | (Byte(color.X) << 16) | (Byte(color.Y) << 8) | Byte(color.Z);
    }

    private static string ToHexRgb(Vector4 color) =>
        $"{(int)(color.X * 255f + 0.5f):X2}{(int)(color.Y * 255f + 0.5f):X2}{(int)(color.Z * 255f + 0.5f):X2}";

    /// <summary>One link tile in the grid · forwards to the shared
    /// <see cref="LinkChips.Card"/> so every link in the app looks alike.</summary>
    internal static void DrawLinkCard(string label, string url, string? icon, Vector4 accent, int index)
        => LinkChips.Card(label, url, icon, accent, index);

    /// <summary>
    /// Gallery page: an album cover rail (event-linked albums first) with an
    /// "All photos" card, and a grid for the drilled-in album. No albums →
    /// straight to the grid.
    /// </summary>
    private static bool DrawGallery(SyncshellCommunityDto community, string texturePrefix,
        Vector4 accent, CommunityPageState? page, SyncshellCommunityEdit? edit = null,
        WidgetVis? vis = null)
    {
        var images = community.Gallery?.Where(g => !string.IsNullOrEmpty(g.ImageBase64) || g.ImageMediaHash != null).ToList();
        // Blank-name albums stay navigable - auto-created event "memories"
        // albums can arrive unnamed and filtering them out strands the
        // FocusItemId album-scope check below.
        var albums = community.Albums?.OrderBy(a => a.SortOrder).ToList();
        var canGallery = edit is { CanManageGallery: true };
        var canSubmit = edit is { CanSubmit: true, OnAddPhoto: not null };
        if ((images == null || images.Count == 0) && (albums == null || albums.Count == 0)
            && !canGallery && !canSubmit)
        {
            return false;
        }
        images ??= [];

        // Photo deep-link · a feed/notification FocusItemId opens the image
        // straight in the viewer - its album context when it has one. Once the
        // gallery payload is in, an unfound id clears instead of stranding
        // (audience-filtered or deleted photos can't resolve).
        if (page != null && page.FocusItemId is { } focusId && community.Gallery != null)
        {
            page.FocusItemId = null;
            if (images.FirstOrDefault(g => g.Id == focusId) is { } focusImg)
            {
                var inAlbum = focusImg.AlbumId is { } fa
                    && albums?.Any(a => a.Id == fa) == true;
                var scope = inAlbum
                    ? images.Where(g => g.AlbumId == focusImg.AlbumId).ToList()
                    : images;
                page.AlbumId = inAlbum ? focusImg.AlbumId : null;
                page.PickingAlbumPhotos = false;
                page.ViewerImages = scope;
                page.ViewerIndex = Math.Max(0, scope.IndexOf(focusImg));
                page.ViewerOpenT = 0f;
            }
        }

        var managePhotos = (canGallery || canSubmit) && edit!.OnAddPhoto != null
            ? (FontAwesomeIcon.Plus, "Add photo", edit.OnAddPhoto)
            : ((FontAwesomeIcon, string, Action)?)null;
        var manageAlbums = canGallery && edit!.Ops != null && page != null
            ? (page.ManagingAlbums ? FontAwesomeIcon.Check : FontAwesomeIcon.FolderOpen,
                page.ManagingAlbums ? "Done editing albums" : "Manage albums",
                (Action)(() =>
                {
                    page.ManagingAlbums = !page.ManagingAlbums;
                    SpheneUI.LogDebug("[CommUi] ManageAlbums={Managing}", page.ManagingAlbums);
                    if (!page.ManagingAlbums)
                    {
                        page.AlbumDrafts.Clear();
                    }
                }))
            : ((FontAwesomeIcon, string, Action)?)null;

        var gid = community.Profile.GroupGID;

        if (page != null && page.AlbumId is { } openId
            && albums?.FirstOrDefault(a => a.Id == openId) is { } open)
        {
            // Album drill-in · back chip, then the album itself as a widget.
            if (SpheneUI.Controls.SegmentButton("Albums", FontAwesomeIcon.ChevronLeft,
                    accent: accent))
            {
                page.AlbumId = null;
                page.PickingAlbumPhotos = false;
                page.SwitchT = 0f;
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            var linked = open.LinkedEventId.HasValue
                ? community.Events?.FirstOrDefault(e => e.Id == open.LinkedEventId.Value)
                : null;
            var count = images.Count(g => g.AlbumId == open.Id);
            var canPick = edit is { CanManageGallery: true, Ops.AssignImageAlbum: not null };
            var albumImgs = images.Where(g => g.AlbumId == open.Id).ToList();
            Widget($"album-{open.Id}",
                string.IsNullOrWhiteSpace(open.Name) ? "Album" : open.Name,
                FontAwesomeIcon.Images, count, accent, () =>
            {
                if (linked != null)
                {
                    SpheneUI.Text.Colored($"memories of {linked.Title}", accent);
                }
                SpheneUI.Text.Muted($"{count} photos" +
                    (string.IsNullOrWhiteSpace(open.Description) ? string.Empty : $" · {open.Description}"));
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                if (page.PickingAlbumPhotos && canPick)
                {
                    // Assign mode · the album's own photos unassign on click,
                    // every other photo (including other albums') moves in.
                    if (albumImgs.Count > 0)
                    {
                        SpheneUI.Text.Muted("In this album · click to remove:");
                        DrawGalleryGrid(albumImgs, texturePrefix, gid,
                            i => edit!.Ops!.AssignImageAlbum!(albumImgs[i], null));
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    }
                    var others = images.Where(g => g.AlbumId != open.Id).ToList();
                    if (others.Count > 0)
                    {
                        SpheneUI.Text.Muted("Available photos · click to add:");
                        DrawGalleryGrid(others, texturePrefix, gid,
                            i => edit!.Ops!.AssignImageAlbum!(others[i], open.Id));
                    }
                    else if (albumImgs.Count == 0)
                    {
                        SpheneUI.Text.Muted("No photos yet · upload one with the + on the gallery header.");
                    }
                }
                else
                {
                    DrawGalleryGrid(albumImgs, texturePrefix, gid,
                        i => { page.ViewerImages = albumImgs; page.ViewerIndex = i; page.ViewerOpenT = 0f; },
                        canGallery
                            ? i =>
                            {
                                page.ViewerImages = albumImgs; page.ViewerIndex = i;
                                page.ViewerOpenT = 0f; page.ViewerEditOpen = true;
                            }
                            : null);
                }
            },
            vis,
            canPick
                ? (page.PickingAlbumPhotos ? FontAwesomeIcon.Check : FontAwesomeIcon.Plus,
                    page.PickingAlbumPhotos ? "Done picking" : "Add photos",
                    (Action)(() =>
                    {
                        page.PickingAlbumPhotos = !page.PickingAlbumPhotos;
                    }))
                : null);
            return true;
        }

        if (page != null && (albums is { Count: > 0 } || manageAlbums.HasValue))
        {
            Widget("albums", "Albums", FontAwesomeIcon.Images, albums?.Count ?? 0, accent, () =>
            {
                if (page.ManagingAlbums && edit?.Ops != null)
                {
                    DrawAlbumManager(community, edit, page, accent);
                }
                else if (albums is { Count: > 0 })
                {
                    DrawAlbumRail(community, albums, images, texturePrefix, gid, accent, page);
                }
                else
                {
                    SpheneUI.Text.Muted("No albums yet · the folder action creates one.");
                }
            }, vis, manageAlbums);
            Widget("photos", "All photos", FontAwesomeIcon.PhotoVideo, images.Count, accent,
                () => DrawGalleryGrid(images, texturePrefix, gid,
                    i => { page.ViewerImages = images; page.ViewerIndex = i; page.ViewerOpenT = 0f; },
                    canGallery
                        ? i =>
                        {
                            page.ViewerImages = images; page.ViewerIndex = i;
                            page.ViewerOpenT = 0f; page.ViewerEditOpen = true;
                        }
                        : null),
                null, managePhotos);
        }
        else
        {
            Widget("photos", "Photos", FontAwesomeIcon.PhotoVideo, images.Count, accent,
                () => DrawGalleryGrid(images, texturePrefix, gid,
                    page == null ? null
                        : i => { page.ViewerImages = images; page.ViewerIndex = i; page.ViewerOpenT = 0f; },
                    page != null && canGallery
                        ? i =>
                        {
                            page.ViewerImages = images; page.ViewerIndex = i;
                            page.ViewerOpenT = 0f; page.ViewerEditOpen = true;
                        }
                        : null),
                vis, managePhotos);
        }
        return true;
    }

    /// <summary>Album cover cards rail · cover image, name, photo count, event tag.</summary>
    /// <summary>Albums as an adaptive card grid · cover, name, count, hover lift.</summary>
    private static void DrawAlbumRail(SyncshellCommunityDto community,
        List<SyncshellGalleryAlbumDto> albums, List<SyncshellGalleryImageDto> images,
        string texturePrefix, string gid, Vector4 accent, CommunityPageState page)
    {
        var scale = SpheneUI.Scale;
        SpheneUI.Layout.CardGrid(albums, 190f * scale, (album, i) =>
        {
            var cover = album.CoverImageId.HasValue
                ? images.FirstOrDefault(g => g.Id == album.CoverImageId.Value)
                : images.FirstOrDefault(g => g.AlbumId == album.Id);
            var count = images.Count(g => g.AlbumId == album.Id);
            var eventTitle = album.LinkedEventId.HasValue
                ? community.Events?.FirstOrDefault(e => e.Id == album.LinkedEventId.Value)?.Title
                : null;
            var cardW = SpheneUI.Controls.BoundedAvailX();
            DrawAlbumCard(album, cover, count, eventTitle, cardW, cardW * 0.58f,
                texturePrefix, gid, accent, page);
        });
    }

    /// <summary>One album cover card · shared by the grid cell renderer.</summary>
    private static void DrawAlbumCard(SyncshellGalleryAlbumDto album,
        SyncshellGalleryImageDto? cover, int count, string? eventTitle,
        float cardW, float cardH, string texturePrefix, string gid,
        Vector4 accent, CommunityPageState page)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton($"##album-{album.Id}", new Vector2(cardW, cardH));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var radius = 10f * scale;

        var lift = SpheneUI.Fx.HoverT(ImGui.GetID($"##album-lift-{album.Id}"), hovered);

        if (cover != null)
        {
            var wrap = SpheneUI.Media.Texture(
                $"{texturePrefix}gallery:{gid}:{cover.Id}:{MediaVersion(cover.ImageMediaHash, cover.ImageBase64)}",
                ResolveBytes(cover.ImageMediaHash, cover.ImageBase64));
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, min, max, ImageFit.Cover,
                    rounding: radius, zoom: 1f + 0.07f * lift);
            }
        }
        else
        {
            var (aDeep, aHi) = SpheneUI.Colors.AccentRamp(accent);
            aDeep.W = 0.10f;
            aHi.W = 0.18f;
            SpheneUI.Chrome.RoundedHorizontalGradient(dl, min, max, radius, aDeep, aHi);
            using (SpheneUI.Icons.PushFont())
            {
                var g = FontAwesomeIcon.PhotoVideo.ToIconString();
                var gs = ImGui.CalcTextSize(g);
                dl.AddText(new Vector2(min.X + (cardW - gs.X) * 0.5f, min.Y + cardH * 0.35f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.6f)), g);
            }
        }
        SyncshellCard.BottomScrim(dl, min, max, 0.55f + 0.10f * lift, radius);
        if (lift > 0.01f)
        {
            dl.AddRect(min + new Vector2(1.5f, 1.5f), max - new Vector2(1.5f, 1.5f),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.16f * lift)),
                radius - 1f);
        }
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, 0.22f + 0.48f * lift)), radius,
            ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));

        var pad = 8f * scale;
        dl.PushClipRect(min + new Vector2(pad, 0f), max - new Vector2(pad, 0f), true);
        dl.AddText(new Vector2(min.X + pad, max.Y - ImGui.GetTextLineHeight() * 2f - pad - 2f * scale),
            ImGui.GetColorU32(ImGuiCol.Text), album.Name);
        dl.PopClipRect();
        var pillX = min.X + pad;
        if (eventTitle != null)
        {
            pillX += SyncshellCard.StatusPill(dl, new Vector2(pillX, min.Y + pad), "Event",
                accent, dot: false, scale) + 4f * scale;
        }
        if (album.Audience != Sphene.API.Data.Enum.SyncshellGalleryAudience.Everyone)
        {
            SyncshellCard.StatusPill(dl, new Vector2(pillX, min.Y + pad),
                album.Audience == Sphene.API.Data.Enum.SyncshellGalleryAudience.StaffOnly
                    ? "Staff" : "Selected",
                SpheneUI.Palette.SpheneGold, dot: false, scale);
        }
        dl.AddText(new Vector2(min.X + pad, max.Y - ImGui.GetTextLineHeight() - pad),
            SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.75f)), $"{count} photos");

        if (hovered)
        {
            SpheneUI.AttachTooltip(eventTitle != null
                ? $"Memories of {eventTitle}"
                : string.IsNullOrWhiteSpace(album.Description)
                    ? album.Name
                    : album.Description!);
        }
        if (clicked)
        {
            page.AlbumId = album.Id;
            page.PickingAlbumPhotos = false;
        }
    }

    /// <summary>Responsive photo grid · wraps tiles to the row width.</summary>
    /// <param name="onEdit">Hover "Edit" chip · opens the viewer's edit drawer at the photo.</param>
    internal static void DrawGalleryGrid(List<SyncshellGalleryImageDto> images, string texturePrefix,
        string gid, Action<int>? onClick = null, Action<int>? onEdit = null)
    {
        if (images.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Images, "No photos",
                "Snapshots shared in this shell will land here.");
            return;
        }
        var scale = SpheneUI.Scale;
        var tile = 128f * scale * SpheneUI.Density.Factor;
        var gap = SpheneUI.Spacing.X8.Scaled;
        var cols = Math.Max(1, (int)((SpheneUI.Controls.BoundedAvailX() + gap) / (tile + gap)));
        var dl = ImGui.GetWindowDrawList();

        for (var i = 0; i < images.Count; i++)
        {
            var img = images[i];
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(tile, tile);
            // Rect-based hover: IsItemHovered would flicker off once the edit
            // chip steals the hover id · the affordance would vanish
            // mid-press and could never be clicked.
            var hovered = ImGui.IsMouseHoveringRect(min, max);
            var lift = SpheneUI.Fx.HoverT(ImGui.GetID($"##tile-lift-{gid}-{img.Id}"), hovered);
            var wrap = SpheneUI.Media.Texture($"{texturePrefix}gallery:{gid}:{img.Id}",
                ResolveBytes(img.ImageMediaHash, img.ImageBase64));
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, min, max, ImageFit.Cover,
                    rounding: 8f * scale, zoom: 1f + 0.05f * lift);
                if (lift > 0.01f)
                {
                    dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                        SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.30f * lift)),
                        8f * scale, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
                }
                if (img.Status == Sphene.API.Data.Enum.SyncshellGalleryImageStatus.PendingApproval)
                {
                    SyncshellCard.StatusPill(dl, min + new Vector2(6f * scale, 6f * scale),
                        "Pending", SpheneUI.Palette.SpheneGold, dot: false, scale);
                }
                else if (img.Audience != Sphene.API.Data.Enum.SyncshellGalleryAudience.Everyone)
                {
                    SyncshellCard.StatusPill(dl, min + new Vector2(6f * scale, 6f * scale),
                        img.Audience == Sphene.API.Data.Enum.SyncshellGalleryAudience.StaffOnly
                            ? "Staff" : "Selected",
                        SpheneUI.Palette.SpheneGold, dot: false, scale);
                }
                if (img.LikeCount > 0)
                {
                    DrawLikeBadge(dl, max - new Vector2(6f * scale, 6f * scale),
                        img.LikeCount, img.LikedByMe, scale);
                }
            }
            ImGui.Dummy(new Vector2(tile, tile));
            var tileClicked = ImGui.IsItemClicked();
            if ((i + 1) % cols != 0 && i + 1 < images.Count)
            {
                ImGui.SameLine(0f, gap);
            }
            var manageClicked = false;
            if (hovered && onEdit != null)
            {
                manageClicked = DrawTileEditChip(dl,
                    new Vector2(max.X - 6f * scale, min.Y + 6f * scale), scale, i, onEdit);
            }
            if (onClick != null && tileClicked && !manageClicked)
            {
                onClick(i);
            }
        }
    }

    /// <summary>
    /// The "Edit" pill that fades in on tile hover · opens the photo inside
    /// the viewer's edit drawer instead of a separate context menu. Purely
    /// rect-driven: no ImGui item, so it can never disturb the grid's
    /// layout state (an InvisibleButton mid-table leaked line metrics and
    /// grew the section card by a few pixels while hovered).
    /// </summary>
    private static bool DrawTileEditChip(ImDrawListPtr dl, Vector2 topRight,
        float scale, int index, Action<int> onEdit)
    {
        var label = "Edit";
        var h = ImGui.GetFrameHeight() * 0.92f;
        var textW = ImGui.CalcTextSize(label).X;
        var padH = 8f * scale;
        var w = padH * 2f + ImGui.GetFrameHeight() * 0.7f + textW;
        var chipMin = topRight - new Vector2(w, 0f);
        var chipMax = chipMin + new Vector2(w, h);
        var chipHov = ImGui.IsMouseHoveringRect(chipMin, chipMax);
        var clicked = chipHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (chipHov)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        dl.AddRectFilled(chipMin, chipMax, SpheneUI.ColorUtils.ToU32(
            new Vector4(0f, 0f, 0f, chipHov ? 0.85f : 0.6f)), h * 0.5f);
        var textY = chipMin.Y + (h - ImGui.GetTextLineHeight()) * 0.5f;
        using (SpheneUI.Icons.PushFont())
        {
            dl.AddText(new Vector2(chipMin.X + padH, textY),
                SpheneUI.ColorUtils.ToU32(Vector4.One), FontAwesomeIcon.Pen.ToIconString());
        }
        dl.AddText(new Vector2(chipMin.X + padH + ImGui.GetFrameHeight() * 0.7f, textY),
            SpheneUI.ColorUtils.ToU32(Vector4.One), label);
        SpheneUI.AttachTooltip("Edit photo", chipHov);
        if (clicked)
        {
            onEdit(index);
        }
        return clicked;
    }

    /// <summary>
    /// Indeterminate upload-progress row · muted label plus an animated
    /// sweep bar while image prep + upload run off the draw path.
    /// </summary>
    internal static void DrawUploadProgressRow(string label)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        SpheneUI.Text.Muted(label);
        var pos = ImGui.GetCursorScreenPos();
        var w = MathF.Min(240f * scale, SpheneUI.Controls.BoundedAvailX());
        var h = MathF.Max(3f, 4f * scale);
        var t = (float)(ImGui.GetTime() % 1.4) / 1.4f;
        var phase = 0.5f - 0.5f * MathF.Cos(t * MathF.PI * 2f);
        var segW = w * 0.32f;
        var segX = pos.X + (w - segW) * phase;
        dl.AddRectFilled(pos, pos + new Vector2(w, h), SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.FrameBg, 0.9f)), h * 0.5f);
        dl.AddRectFilled(new Vector2(segX, pos.Y), new Vector2(segX + segW, pos.Y + h),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Accent, 0.9f)),
            h * 0.5f);
        ImGui.Dummy(new Vector2(w, h + 4f * scale));
    }

    /// <summary>Small "♥ N" pill anchored at its bottom-right · fills danger-tinted when liked.</summary>
    private static void DrawLikeBadge(ImDrawListPtr dl, Vector2 bottomRight, int count,
        bool liked, float scale)
    {
        var label = count.ToString();
        var heart = FontAwesomeIcon.Heart.ToIconString();
        float heartW;
        using (SpheneUI.Icons.PushFont())
        {
            heartW = ImGui.CalcTextSize(heart).X;
        }
        var labelW = ImGui.CalcTextSize(label).X;
        var padH = 6f * scale;
        var h = ImGui.GetTextLineHeight() + 5f * scale;
        var w = padH * 2f + heartW + 3f * scale + labelW;
        var min = bottomRight - new Vector2(w, h);
        dl.AddRectFilled(min, bottomRight, SpheneUI.ColorUtils.ToU32(
            liked ? SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Danger, 0.85f)
                  : new Vector4(0f, 0f, 0f, 0.55f)), h * 0.5f);
        using (SpheneUI.Icons.PushFont())
        {
            dl.AddText(new Vector2(min.X + padH, min.Y + 2.5f * scale),
                SpheneUI.ColorUtils.ToU32(liked ? Vector4.One
                    : SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Danger, 0.95f)), heart);
        }
        dl.AddText(new Vector2(min.X + padH + heartW + 3f * scale, min.Y + 2.5f * scale),
            SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.9f)), label);
    }

    /// <summary>
    /// Staff page · full friends-list rows grouped by affiliation. Read-only
    /// directory - jobs and rights live on the shell's own Staff admin page.
    /// </summary>
    private static bool DrawStaff(SyncshellCommunityDto community, GroupFullInfoDto? group,
        string texturePrefix, Vector4 accent, CommunityMemberHooks? members, WidgetVis? vis = null)
    {
        var staff = StaffEntries(community, group, accent);
        if (staff.Count == 0)
        {
            return false;
        }

        var scale = SpheneUI.Scale;
        Widget("staff", "Staff", FontAwesomeIcon.UserShield, staff.Count, accent, () =>
        {
            SpheneUI.Layout.CardGrid(staff, 250f * scale,
                (member, _) => DrawStaffRow(member, texturePrefix, 0f, members));
        }, vis);
        return true;
    }

    /// <summary>Venue "info" block · shell ID (click to copy), member count, founded.</summary>
    private static void DrawInfoFooter(SyncshellProfileDto profile, GroupFullInfoDto? group)
    {
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Layout.Separator();
        var scale = SpheneUI.Scale;
        using (SpheneUI.Icons.PushFont())
        {
            SpheneUI.Text.Muted(FontAwesomeIcon.Fingerprint.ToIconString());
        }
        ImGui.SameLine(0f, 6f * scale);
        SpheneUI.Text.Muted(profile.GroupGID);
        if (ImGui.IsItemClicked())
        {
            ImGui.SetClipboardText(profile.GroupGID);
        }
        SpheneUI.AttachTooltip("Click to copy the shell ID");
        // Authoritative total when the server sends it - the local privileged
        // roster count is only a fallback for old servers, not a member count.
        var memberCount = group is { MemberCount: >= 0 } g
            ? g.MemberCount
            : group?.GroupPairUserInfos?.Count ?? 0;
        var tail = string.Join("   ·   ", new[]
            {
                memberCount > 0 ? $"{memberCount} members" : null,
                profile.CreatedAt > DateTime.UnixEpoch ? $"since {profile.CreatedAt:MMM yyyy}" : null,
            }.Where(s => s != null));
        if (tail.Length > 0)
        {
            var w = ImGui.CalcTextSize(tail).X;
            var right = ImGui.GetCursorScreenPos().X + SpheneUI.Controls.BoundedAvailX() - w;
            ImGui.SetCursorScreenPos(new Vector2(right, ImGui.GetCursorScreenPos().Y));
            SpheneUI.Text.Muted(tail);
        }
        ImGui.NewLine();
    }

    public static Vector4? ParseAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var value = hex.TrimStart('#');
        if (value.Length is not (6 or 8)
            || !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var packed))
        {
            return null;
        }
        return value.Length == 6
            ? new Vector4(
                (packed >> 16 & 0xFF) / 255f,
                (packed >> 8 & 0xFF) / 255f,
                (packed & 0xFF) / 255f,
                1f)
            : new Vector4(
                (packed >> 24 & 0xFF) / 255f,
                (packed >> 16 & 0xFF) / 255f,
                (packed >> 8 & 0xFF) / 255f,
                (packed & 0xFF) / 255f);
    }

}