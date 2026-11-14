using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Sphene.UI.Hub;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;

namespace Sphene.UI.Syncshell;

/// <summary>
/// The Event Planner's Live Preview popout - hosts the real attendee-facing
/// community page (<see cref="SyncshellCommunityView.DrawContent"/>) with the
/// unsaved draft overlaid onto the community aggregate the host carried over.
/// The page is fully navigable but completely inert: RSVP/withdraw clicks land
/// on no-ops, the travel button renders disabled, comment/reaction/moderation
/// write hooks are never supplied, and profile navigation is off. Draft edits
/// appear live because the DTO is rebuilt from the composer every frame;
/// opening or closing the window never mutates the draft or calls a write API.
/// </summary>
public sealed class EventPreviewUi : WindowMediatorSubscriberBase
{
    /// <summary>Window-bottom strip left outside the scroll child so the
    /// parent's bottom-edge and corner resize zones stay hoverable.</summary>
    private const float ResizeStripUnscaled = 10f;
    private const float MinHeightUnscaled = 380f;
    private const float InitialHeightUnscaled = 640f;

    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly Services.DalamudUtilService _dalamudUtilService;
    private readonly CommunityComposers _composers;
    private SyncshellCommunityView.CommunityPageState _page = new();
    private SyncshellCommunityView.CommunityEventComposer? _composer;
    private SyncshellCommunityDto? _community;
    private GroupFullInfoDto? _group;
    private GroupData? _groupData;
    private long _previewEventId = -1;

    public EventPreviewUi(ILogger<EventPreviewUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService, PairManager pairManager,
        ApiController apiController, SpheneProfileManager spheneProfileManager,
        Services.DalamudUtilService dalamudUtilService)
        : base(logger, mediator, "Event Preview###EventPreview", performanceCollectorService)
    {
        _pairManager = pairManager;
        _apiController = apiController;
        _spheneProfileManager = spheneProfileManager;
        _dalamudUtilService = dalamudUtilService;
        _composers = new CommunityComposers(apiController, mediator, logger);
        // Fixed at the community window's presentation width so the page
        // renders exactly as an attendee sees it; the height resizes between
        // a usable floor and the live work-area cap (PreDraw refresh).
        SizeConstraints = new()
        {
            MinimumSize = new(SpheneHubUi.MinContentWidth, MinHeightUnscaled),
            MaximumSize = new(SpheneHubUi.MinContentWidth, 10000)
        };
        Size = new Vector2(SpheneHubUi.MinContentWidth, InitialHeightUnscaled);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
        Mediator.Subscribe<OpenEventPreviewMessage>(this, msg =>
        {
            _composer = msg.Composer;
            _community = msg.Community;
            _group = msg.GroupInfo;
            _groupData = msg.Group;
            _previewEventId = -1;
            _page = new SyncshellCommunityView.CommunityPageState
            {
                Tab = SyncshellCommunitySection.Events,
            };
            WindowName = $"{msg.Group.AliasOrGID} · Event Preview###EventPreview";
            IsOpen = true;
        });
    }

    protected override string? ChromeTrailingText => "Draft preview - nothing is saved";

    /// <summary>Locks the window width (min.X == max.X) while the height
    /// range tracks the live work area - same fixed-width/resizable-height
    /// behavior as the standalone community window. Skipped while the fold
    /// machine owns the constraints (animating or collapsed).</summary>
    public override void PreDraw()
    {
        base.PreDraw();
        if (SpheneUI.Chrome.CollapseAmount(WindowName) <= 0f
            && SizeConstraints is { } constraints)
        {
            constraints.MaximumSize = new Vector2(constraints.MinimumSize.X,
                SpheneUI.Chrome.WorkAreaMax.Y);
        }
    }

    protected override void DrawInternal()
    {
        var composer = _composer;
        var community = _community;
        if (composer == null || !composer.Open || community == null)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.EyeSlash, "No draft",
                "The editor this preview was bound to has been closed.");
            return;
        }

        // Draft overlay - the saved event's server fields (attendees, RSVP
        // state, comment count, media hash) stay; the draft projects over
        // them. A brand-new draft (EditId 0) splices in as a first-class
        // entry with honest empty counts.
        var draft = composer.BuildPreviewDto(uid =>
            _pairManager.GetPairByUID(uid)?.UserData);
        var events = community.Events?.ToList() ?? [];
        var idx = events.FindIndex(e => e.Id == draft.Id);
        if (idx >= 0)
        {
            events[idx] = OverlayDraft(events[idx], draft);
        }
        else
        {
            events.Insert(0, draft);
        }
        var previewCommunity = community with { Events = events };

        // Rebind the detail only when the draft's target event changed -
        // Back/tabs/event-rail clicks stay usable within one session.
        if (_previewEventId != draft.Id)
        {
            _previewEventId = draft.Id;
            _page.EventId = draft.Id;
            _page.SwitchT = 0f;
        }

        // Full-bleed body identical to the standalone community window: the
        // scroll child starts at the window's left edge and spans the whole
        // width; the pinned hero apron + tab bar draw over it as a deferred
        // overlay. The child stops short of the window bottom so the
        // parent's resize zones stay reachable.
        var scale = SpheneUI.Scale;
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X,
            ImGui.GetCursorScreenPos().Y));
        var scrollMin = ImGui.GetCursorScreenPos();
        var scrollSize = new Vector2(ImGui.GetWindowSize().X,
            SpheneUI.Layout.FillToViewBottomHeight(ResizeStripUnscaled * scale));
        Action? overlay = null;
        if (SpheneUI.Layout.BeginScrollArea("EventPreviewContent", scrollSize,
                noPadding: true, contentInset: 12f * scale, edgeToEdge: true))
        {
            overlay = SyncshellCommunityView.DrawContent(previewCommunity, _group,
                "event-preview:",
                edit: BuildPreviewEdit(),
                heroBleed: (scrollMin, scrollSize),
                page: _page,
                members: PreviewMemberHooks(),
                travel: PreviewTravelHooks());
        }
        SpheneUI.Layout.EndScrollArea();
        overlay?.Invoke();
    }

    /// <summary>The inert preview contract: attendee-visible controls draw in
    /// their normal positions - RSVP segments carry the viewer's real state
    /// and clicks land on no-ops - while every write, manage and staff hook
    /// stays unset so those affordances never render. Loads that only read
    /// (participants, calendar, comments) stay real for fidelity.</summary>
    private SyncshellCommunityView.SyncshellCommunityEdit BuildPreviewEdit()
    {
        var edit = new SyncshellCommunityView.SyncshellCommunityEdit
        {
            OnRsvp = (_, _) => { },
            OnWithdrawRsvp = _ => { },
            LoadParticipants = ev => ev.Id == 0 || _groupData == null
                ? Task.FromResult<EventParticipantsDto?>(null)
                : LoadParticipantsAsync(ev),
            CanEditEvent = _ => false,
            MemberLabel = uid =>
                _pairManager.GetPairByUID(uid)?.UserData.PreferredName ?? "Unknown user",
        };
        var groupData = _groupData;
        if (groupData != null)
        {
            edit.LoadCalendar = (fromUtc, toUtc) => Task.Run(async () =>
            {
                try
                {
                    return (List<SyncshellEventOccurrenceDto>?)await _apiController
                        .GroupGetEventCalendar(
                            new SyncshellEventCalendarRequestDto(groupData, fromUtc, toUtc))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load preview calendar for {GID}",
                        groupData.GID);
                    return null;
                }
            });
            edit.Comments = (kind, targetId) => _composers.BuildReadOnlyCommentOps(
                kind, targetId, groupData,
                uid => SyncshellCommunityView.HumanName(
                    _pairManager.GetPairByUID(uid)?.UserData)
                    ?? _spheneProfileManager.GetPreferredName(uid),
                uid => _spheneProfileManager.GetSpheneProfile(new UserData(uid)));
        }
        return edit;
    }

    /// <summary>Read-only participant fetch - the same GET the attendee page
    /// uses, never a write. A null result draws the truthful "No
    /// participants yet." state.</summary>
    private Task<EventParticipantsDto?> LoadParticipantsAsync(SyncshellEventDto ev)
        => Task.Run(async () =>
        {
            try
            {
                return (EventParticipantsDto?)await _apiController.GroupGetEventParticipants(
                    new EventRsvpDto(_groupData!, ev.Id, 0)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load preview participants for event {Id}",
                    ev.Id);
                return null;
            }
        });

    /// <summary>Name/avatar resolution stays real so roster rows and comment
    /// authors look like the attendee page; profile navigation is off.</summary>
    private SyncshellCommunityView.CommunityMemberHooks PreviewMemberHooks()
        => new(
            GetProfile: uid => _spheneProfileManager.GetSpheneProfile(new UserData(uid)),
            GetDisplayName: uid => SyncshellCommunityView.HumanName(
                _pairManager.GetPairByUID(uid)?.UserData)
                ?? _spheneProfileManager.GetPreferredName(uid),
            CanOpenProfile: _ => false,
            OpenProfile: null);

    /// <summary>Address names resolve through game data like the real page,
    /// but nothing probes, travels or opens the map - the Travel button
    /// renders disabled and Show on Map is a silent no-op.</summary>
    private SyncshellCommunityView.CommunityTravelHooks PreviewTravelHooks()
        => new(
            GetState: null,
            RequestTravel: null,
            LoadBoundLocations: null,
            CurrentLocation: null,
            WorldName: id => _dalamudUtilService.WorldData.Value.TryGetValue(id, out var n)
                ? n : null,
            TerritoryName: id => _dalamudUtilService.TerritoryData.Value.TryGetValue(id, out var n)
                ? n : null,
            WorldChoices: null,
            TerritoryChoices: null,
            ShowOnMap: _ => Task.FromResult(false));

    /// <summary>Applies the draft's editable fields onto the saved event so
    /// server-owned data (attendance, viewer RSVP, comments, author, review
    /// state, stored cover) survives the overlay untouched. A newly picked
    /// cover replaces the stored image and drops the media hash; an unpicked
    /// cover keeps both - the same semantics the save path applies.</summary>
    private static SyncshellEventDto OverlayDraft(SyncshellEventDto saved,
        SyncshellEventDto draft)
    {
        List<UserData>? organizerUsers = null;
        if (draft.OrganizerUids is { Count: > 0 } uids)
        {
            organizerUsers = uids
                .Select(u => draft.OrganizerUsers?.FirstOrDefault(o =>
                        string.Equals(o.UID, u, StringComparison.Ordinal))
                    ?? saved.OrganizerUsers?.FirstOrDefault(o =>
                        string.Equals(o.UID, u, StringComparison.Ordinal)))
                .Where(o => o != null).Cast<UserData>().ToList();
        }
        return saved with
        {
            Title = draft.Title,
            Description = draft.Description,
            StartUtc = draft.StartUtc,
            EndUtc = draft.EndUtc,
            Location = draft.Location,
            Url = draft.Url,
            ImageBase64 = draft.ImageBase64 ?? saved.ImageBase64,
            ImageMediaHash = draft.ImageBase64 != null ? null : saved.ImageMediaHash,
            PreviewMediaHash = draft.PreviewBase64 != null ? null : saved.PreviewMediaHash,
            PreviewBase64 = draft.PreviewBase64,
            OrganizerUids = draft.OrganizerUids,
            OrganizerUsers = organizerUsers,
            MaxAttendees = draft.MaxAttendees,
            Visibility = draft.Visibility,
            EventStatus = draft.EventStatus,
            PublishStatus = draft.PublishStatus,
            TimezoneId = draft.TimezoneId,
            RecurrenceRule = draft.RecurrenceRule,
            Reminders = draft.Reminders,
            Destination = draft.Destination,
        };
    }
}
