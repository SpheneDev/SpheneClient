using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Sphene.UI.Hub;

namespace Sphene.UI.Syncshell;

/// <summary>
/// Syncshell community window. Shown as the join popup (<paramref name="joinPopup"/>)
/// or as a persistent community view when opened from the pair list or hub.
/// </summary>
public sealed class SyncshellCommunityUI : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private readonly ApiController _apiController;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly PlayerData.Pairs.PairManager _pairManager;
    private readonly CommunityComposers _composers;
    private readonly Services.DalamudUtilService _dalamudUtilService;
    private readonly Services.LifestreamTravelService _lifestreamTravel;
    private readonly Interop.Ipc.IpcManager _ipcManager;
    private SyncshellCommunityDto _community;
    private readonly Services.CommunityPageReload _reload = new();
    private GroupFullInfoDto? _group;
    private readonly bool _joinPopup;
    private readonly SyncshellCommunityView.CommunityPageState _page = new();
    private readonly CommunityEditSession _editSession = new();
    private readonly UnsavedChangesToast _communityToast = new();
    private (string Gid, bool Favorite)? _favoriteOverride;
    /// <summary>Window-bottom strip left outside the scroll child so the
    /// parent's bottom-edge and corner resize zones stay hoverable.</summary>
    private const float ResizeStripUnscaled = 10f;

    public SyncshellCommunityUI(ILogger<SyncshellCommunityUI> logger, SpheneMediator mediator,
        SpheneConfigService configService, ApiController apiController,
        SpheneProfileManager spheneProfileManager,
        PlayerData.Pairs.PairManager pairManager, SyncshellCommunityDto community,
        GroupFullInfoDto? group, bool joinPopup,
        PerformanceCollectorService performanceCollectorService,
        Services.CommunityMediaClientService communityMedia,
        Services.DalamudUtilService dalamudUtilService,
        Services.LifestreamTravelService lifestreamTravel,
        Interop.Ipc.IpcManager ipcManager, bool previewAsVisitor = false)
        : base(logger, mediator,
            (joinPopup ? $"Welcome to {CommunityDisplayName(group, community)}!###SyncshellCommunityPopup"
                : $"{CommunityDisplayName(group, community)} · Community###SyncshellCommunity"),
            performanceCollectorService)
    {
        _configService = configService;
        _apiController = apiController;
        _spheneProfileManager = spheneProfileManager;
        _pairManager = pairManager;
        _community = community;
        _group = group;
        _joinPopup = joinPopup;
        _dalamudUtilService = dalamudUtilService;
        _lifestreamTravel = lifestreamTravel;
        _ipcManager = ipcManager;
        _composers = new CommunityComposers(apiController, mediator, logger, communityMedia);
        // author deep-links resolve in the hub - pair rows land on friend
        // pages, everyone else on the public profile
        _composers.OnAuthorClick = uid => mediator.Publish(new OpenAuthorProfileMessage(uid));
        // The hub's "Preview" action lands staff/owners straight in visitor
        // mode - they can flip back via the page's preview toggle.
        _page.PreviewAsVisitor = previewAsVisitor;
        Mediator.Subscribe<LifestreamTravelStateChangedMessage>(this, (_) =>
            _page.TravelVersion++);
        // Another user's save lands here via the pushed change notification -
        // without a reload the open window keeps rendering its stale snapshot
        // (old media hashes, missing banner) until closed and reopened.
        Mediator.Subscribe<CommunityChangedMessage>(this, msg =>
        {
            if (string.Equals(msg.Community.Group.GID, _community.Profile.GroupGID,
                    StringComparison.Ordinal))
            {
                _page.Comments.InvalidateForChange(msg.Community);
                // reaction pushes move counts inside open threads - the page
                // itself carries no reaction state, so reloading it would be
                // pure flicker
                if (!msg.Community.ReactionsOnly)
                {
                    _ = Reload();
                }
            }
        });

        // The community page's width must match the hub's minimum content
        // width · then the venue layout renders identically on both surfaces.
        // The width is locked by min==max; the height range is refreshed live
        // in PreDraw so the window resizes vertically within the work area.
        SizeConstraints = new()
        {
            MinimumSize = new(SpheneHubUi.MinContentWidth, 520),
            MaximumSize = new(SpheneHubUi.MinContentWidth, 10000)
        };

        Flags = ImGuiWindowFlags.NoCollapse;
        IsOpen = true;
    }

    // The hero banner merges into the window top · no chrome band. Folding
    // runs through the collapse state machine; the hero draws the fold and
    // close circles itself, and the folded strip is a thin slice of the hero.
    protected override bool ChromeHasTitleBar => false;
    protected override bool DrawCollapsedDuringFold => true;

    /// <summary>Locks the window width (min.X == max.X) while the height
    /// range tracks the live work area - re-applied after the base pass so
    /// resolution/scale changes stay honest. <see
    /// cref="SpheneUI.Chrome.UpdateCollapse"/> still owns the constraints
    /// during the fold animation itself - it swaps in (1, ∞) constraints
    /// while animating or folded, so this refresh only runs fully open.</summary>
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
        // Captured before the scroll child opens · this is the real window
        // rect, so the hero can bleed over the title bar.
        var heroRect = (ImGui.GetWindowPos(), ImGui.GetWindowSize());
        // Footer reserve must match what the join-popup footer really draws
        // (separator + thin gap + one ActionBar row) - a generous flat value
        // left a dead quarter of the window under the controls.
        var footerReserve = _joinPopup
            ? SpheneUI.Metrics.Gap * 2f + SpheneUI.Scale
                + SpheneUI.Spacing.X2.Scaled
                + ImGui.GetFrameHeight()
                + ImGui.GetStyle().ItemSpacing.Y * 2f
            : 0f;

        // Full-bleed body like the chrome title bar: the scroll child starts
        // at the window's left edge and spans the whole width · the border
        // ring is painted underneath and covered, no blur strips at the sides.
        // The child stops short of the window bottom by the resize strip -
        // an edge-to-edge child covers the parent's resize zones entirely,
        // and that thin exposed strip is what the bottom-edge/corner drag
        // hit-tests against.
        ImGui.SetCursorScreenPos(new Vector2(heroRect.Item1.X,
            ImGui.GetCursorScreenPos().Y));
        var scrollSize = new Vector2(heroRect.Item2.X,
            SpheneUI.Layout.FillToViewBottomHeight(
                footerReserve + ResizeStripUnscaled * SpheneUI.Scale));
        Action? overlay = null;
        if (SpheneUI.Layout.BeginScrollArea("SyncshellCommunityContent", scrollSize,
                noPadding: true, contentInset: 12f * SpheneUI.Scale, edgeToEdge: true))
        {
            // Public previews (no group context) get the venue-style action
            // tiles under the hero · join CTA reads the effective join policy
            // the server resolved (InvitesEnabled=false already folds Closed).
            var effectiveCommunity = _editSession.Effective(_community);
            var effectiveGroup = _editSession.Alias is { } stagedAlias && _group != null
                && !string.Equals(stagedAlias, _group.Group.Alias, StringComparison.Ordinal)
                    ? _group with { Group = _group.Group with { Alias = stagedAlias } }
                    : _group;
            var joinLabel = effectiveCommunity.EffectiveJoinPolicy switch
            {
                SyncshellJoinPolicy.Public => "Join this syncshell",
                SyncshellJoinPolicy.PasswordProtected => "Join with password",
                SyncshellJoinPolicy.ApprovalRequired => "Apply for membership",
                SyncshellJoinPolicy.InviteOnly => "Invite required",
                SyncshellJoinPolicy.Closed => "Membership closed",
                _ => "Join this syncshell",
            };
            var isFavorite = _favoriteOverride is { } o
                && string.Equals(o.Gid, effectiveCommunity.Profile.GroupGID, StringComparison.Ordinal)
                ? o.Favorite : effectiveCommunity.CallerFavorited;
            var isAreaPreview = effectiveCommunity.IsAreaBound && _group == null;
            (FontAwesomeIcon, string)[]? tiles = _group == null && !_joinPopup
                ? (isAreaPreview
                    ? [(isFavorite ? FontAwesomeIcon.StarHalfAlt : FontAwesomeIcon.Star,
                            isFavorite ? "Following" : "Follow"),
                        (FontAwesomeIcon.Copy, "Copy ID")]
                    : [(isFavorite ? FontAwesomeIcon.StarHalfAlt : FontAwesomeIcon.Star,
                            isFavorite ? "Following" : "Follow"),
                        (FontAwesomeIcon.UserPlus, joinLabel),
                        (FontAwesomeIcon.Copy, "Copy ID")])
                : null;
            // Manage affordances open contextual editors right on the page  · 
            // anchored composer flyouts, inline expands, the image picker.
            // The visitor preview suppresses the whole contract locally  · 
            // nothing reaches the server, the page just renders the public view.
            var baseEdit = BuildManageEdit();
            var manageEdit = _page.PreviewAsVisitor ? null : baseEdit;
            var travelHooks = _composers.BuildTravelHooks(
                effectiveGroup?.Group ?? new GroupData(effectiveCommunity.Profile.GroupGID),
                _lifestreamTravel, _dalamudUtilService, _ipcManager.Lifestream);
            overlay = SyncshellCommunityView.DrawContent(effectiveCommunity, effectiveGroup, "syncshell-community:",
                manageEdit,
                travel: travelHooks,
                actionTiles: tiles,
                onActionTile: index =>
                {
                    var tileGid = _community.Profile.GroupGID;
                    if (index == 0)
                    {
                        ToggleFavorite(tileGid, !isFavorite);
                    }
                    else if (index == 1 && isAreaPreview)
                    {
                        ImGui.SetClipboardText(tileGid);
                    }
                    else if (index == 1)
                    {
                        Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Syncshells,
                            SyncshellSub: SyncshellHubSubPage.Join,
                            JoinGid: tileGid));
                    }
                    else if (index == 2)
                    {
                        ImGui.SetClipboardText(tileGid);
                    }
                },
                heroBleed: heroRect, onClose: () => IsOpen = false,
                onFold: () => SpheneUI.Chrome.ToggleFold(WindowName),
                foldCollapsed: SpheneUI.Chrome.FoldCollapsed(WindowName),
                page: _page,
                onViewerPopout: (imgs, i) => Mediator.Publish(
                    new OpenSyncshellImageViewerMessage(imgs, i, _community.Profile.GroupGID)),
                members: new SyncshellCommunityView.CommunityMemberHooks(
                    GetProfile: uid => _spheneProfileManager.GetSpheneProfile(new UserData(uid)),
                    GetDisplayName: uid => CommunityNameFor(uid),
                    CanOpenProfile: uid => _pairManager.GetPairByUID(uid) != null,
                    OpenProfile: uid =>
                    {
                        var pair = _pairManager.GetPairByUID(uid);
                        if (pair != null)
                        {
                            Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Friends, Friend: pair));
                        }
                    }),
                onLikeImage: _group == null ? null : ToggleImageLike,
                areaTravel: isAreaPreview ? BuildAreaTravel(travelHooks) : null,
                onPreviewToggle: baseEdit == null ? null
                    : () => _page.PreviewAsVisitor = !_page.PreviewAsVisitor,
                previewActive: _page.PreviewAsVisitor);
        }
        SpheneUI.Layout.EndScrollArea();

        // Deferred pinned header + hero chrome · the overlay opens its own
        // input-taking child parked over the pinned zone, so tabs, pens and
        // the banner zone get a real hover context. Its absolute positioning
        // moves the flow cursor · saved/restored for the join-popup footer.
        if (overlay != null)
        {
            var resumeCursor = ImGui.GetCursorScreenPos();
            overlay();
            ImGui.SetCursorScreenPos(resumeCursor);
        }

        // Unsaved-changes toast · staged community edits stay local until the
        // manager hits Apply, then land as one GroupSetCommunity (+ alias).
        var toastBottom = heroRect.Item1.Y + heroRect.Item2.Y
            - footerReserve - 6f * SpheneUI.Scale;
        var toastCenterX = heroRect.Item1.X + heroRect.Item2.X * 0.5f;
        _communityToast.Draw(_editSession.IsDirty,
            "You have unsaved community changes", toastCenterX, toastBottom,
            new UnsavedChangesToast.Button("Reset", FontAwesomeIcon.Undo,
                SpheneUI.Colors.Accent, true,
                "Drop the staged changes.", ResetCommunityEdits),
            new UnsavedChangesToast.Button("Apply", FontAwesomeIcon.Check,
                SpheneUI.Palette.LuminousGold, true,
                "Publish the staged community changes.", () => _ = ApplyCommunityEdits(),
                Solid: true));

        if (!_joinPopup)
        {
            return;
        }

        SpheneUI.Layout.Separator();
        SpheneUI.Layout.Space(SpheneUI.Spacing.X2);

        var gotItW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, "Got it!");
        SpheneUI.Layout.ActionBar(gotItW, () =>
        {
            if (SpheneUI.Controls.PrimaryButton("Got it!", FontAwesomeIcon.Check))
            {
                IsOpen = false;
            }
        },
        _ =>
        {
            var showWelcomeMessages = _configService.Current.ShowAreaBoundSyncshellWelcomeMessages;
            if (SpheneUI.Controls.Toggle("Show welcome messages", ref showWelcomeMessages))
            {
                _configService.Current.ShowAreaBoundSyncshellWelcomeMessages = showWelcomeMessages;
                _configService.Save();
            }
            SpheneUI.AttachTooltip("Uncheck this to disable welcome messages for area-bound syncshells");
        });
    }

    /// <summary>Stages a picked banner/avatar into the edit session - the
    /// content-versioned texture keys pick up the new image on their own.</summary>
    /// <summary>Bookmarks the shell for the Followed surface - purely local
    /// viewer state; the community page flips the tile instantly and the hub
    /// list reloads via the published change message.</summary>
    private void ToggleFavorite(string gid, bool favorite)
    {
        _favoriteOverride = (gid, favorite);
        _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupSetSyncshellFavorite(
                    new GroupDto(new GroupData(gid)), favorite).ConfigureAwait(false);
                Mediator.Publish(new SyncshellFavoritesChangedMessage());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Favorite toggle failed for {Gid}", gid);
                _favoriteOverride = null;
            }
        });
    }


    /// <summary>Window-title name for viewers without a GroupFullInfoDto -
    /// the DTO's display alias wins over the raw GID.</summary>
    private static string CommunityDisplayName(GroupFullInfoDto? group,
        SyncshellCommunityDto community)
        => group?.Group.AliasOrGID
            ?? (string.IsNullOrWhiteSpace(community.GroupAlias)
                ? community.Profile.GroupGID : community.GroupAlias);

    /// <summary>Non-member area CTA: bound locations become Lifestream
    /// destinations via the shared mapper; travel results surface as a toast
    /// on terminal failure states so the button never looks dead.</summary>
    private SyncshellCommunityView.CommunityAreaTravel BuildAreaTravel(
        SyncshellCommunityView.CommunityTravelHooks hooks)
        => new(
            LoadDestinations: hooks.LoadBoundLocations is { } load
                ? async () =>
                {
                    var locs = await load().ConfigureAwait(false);
                    var list = new List<EventDestinationDto>();
                    if (locs != null)
                    {
                        foreach (var l in locs)
                        {
                            if (Interop.Ipc.LifestreamAddressMapping.FromLocationInfo(l) is { } d)
                            {
                                list.Add(d);
                            }
                        }
                    }
                    return (IReadOnlyList<EventDestinationDto>)list;
                }
                : null,
            RequestTravel: hooks.RequestTravel,
            ShowOnMap: hooks.ShowOnMap,
            OnTravelResult: st =>
            {
                if (st.State is LifestreamTravelState.TravelFailed
                    or LifestreamTravelState.Unavailable
                    or LifestreamTravelState.DestinationUnsupported)
                {
                    Mediator.Publish(new NotificationMessage("Travel unavailable",
                        st.Detail ?? "Lifestream could not start travel to this area.",
                        SpheneConfiguration.Models.NotificationType.Warning,
                        TimeSpan.FromSeconds(5), SuppressInbox: true));
                }
            });

    private void StageCommunityImage(SyncshellCommunityDto staged, bool banner)
        => _editSession.StageImage(staged, banner);

    /// <summary>Drops every staged community change without saving.</summary>
    private void ResetCommunityEdits() => _editSession.Reset();

    /// <summary>Commits the staged aggregate as one <c>GroupSetCommunity</c>
    /// plus the staged alias via <c>GroupSetAlias</c>, then reloads. A failed
    /// save keeps the draft so the user can retry.</summary>
    private async Task ApplyCommunityEdits()
    {
        if (_group == null)
        {
            _editSession.Reset();
            return;
        }
        var gid = _community.Profile.GroupGID;
        try
        {
            var toSave = _editSession.Effective(_community);
            var ok = await _apiController.GroupSetCommunity(
                CommunityComposers.ToUpdateDto(toSave, _group.Group)).ConfigureAwait(false);
            if (!ok)
            {
                return;
            }
            var stagedAlias = _editSession.Alias;
            if (!string.IsNullOrWhiteSpace(stagedAlias)
                && !string.Equals(stagedAlias, _group.Group.Alias, StringComparison.Ordinal))
            {
                var aliasOk = await _apiController.GroupSetAlias(
                    new GroupAliasDto(_group.Group, stagedAlias)).ConfigureAwait(false);
                if (aliasOk)
                {
                    _group = _group with { Group = _group.Group with { Alias = stagedAlias } };
                }
            }
            _editSession.Reset();
            await Reload().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply syncshell community edits for {GID}", gid);
        }
    }

    /// <summary>Refetches the aggregate after a contextual edit so the page re-renders.</summary>
    private async Task Reload()
    {
        var gid = _community.Profile.GroupGID;
        try
        {
            await _reload.Run(() => _apiController.GroupGetCommunity(new GroupDto(new GroupData(gid))), page =>
            {
                if (page != null) _community = page;
                else IsOpen = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to reload syncshell community for {GID}", gid);
        }
    }

    /// <summary>
    /// Manage contract for this window · content edits run through the
    /// anchored composers inline; only structural editors (links, staff,
    /// albums, activation) still route to the admin window.
    /// </summary>
    private SyncshellCommunityView.SyncshellCommunityEdit? BuildManageEdit()
    {
        var group = _group?.Group;
        if (group == null)
        {
            return null;
        }
        var community = _community;
        var uid = _apiController.UID;
        var isAdmin = string.Equals(_group!.OwnerUID, uid, StringComparison.Ordinal)
            || _group.GroupUserInfo.IsModerator();
        // Every assigned job's flags union - mirrors the server's bits.
        var staffBits = SyncshellCommunityView.MemberPermissionBits(community, uid);
        bool Perm(SyncshellStaffPermission flag) =>
            isAdmin || (staffBits & flag) != SyncshellStaffPermission.None;
        var any = isAdmin || staffBits != SyncshellStaffPermission.None;
        if (!any)
        {
            // Plain member - RSVP/calendar hooks + review-queue submissions.
            var memberEdit = new SyncshellCommunityView.SyncshellCommunityEdit
            {
                CanSubmit = true,
                OnSaveNews = (composer, draft) => _composers.SaveNews(group, composer, draft,
                    Reload, staff: false),
                OnSaveEvent = composer => _composers.SaveEvent(group, composer, Reload,
                    staff: false),
                OnAddPhoto = () => _composers.PickPhoto(group, community.Gallery?.Count ?? 0,
                    Reload, staff: false),
            };
            _composers.WireEventOps(memberEdit, group, false, Reload);
            _composers.WireCommentOps(memberEdit, group, Reload, CommunityNameFor,
                CommentProfile);
            _composers.WireTravelOps(memberEdit, group, _lifestreamTravel,
                _dalamudUtilService, _ipcManager.Lifestream, Reload);
            WirePlannerHooks(memberEdit, group);
            return memberEdit;
        }
        var communityOps = _composers.CreateOps(group, () => _community, Reload, StaffMembers,
            mutate: p => { if (_community != null) _community = p(_community); });
        communityOps.Patch = patch => _editSession.Stage(patch);
        var edit = new SyncshellCommunityView.SyncshellCommunityEdit
        {
            CanManagePage = Perm(SyncshellStaffPermission.ManagePage),
            CanManageNews = Perm(SyncshellStaffPermission.ManageNews),
            CanManageEvents = Perm(SyncshellStaffPermission.ManageEvents),
            CanManageGallery = Perm(SyncshellStaffPermission.ManageGallery),
            CanManageStaff = Perm(SyncshellStaffPermission.ManageRoles),
            CanEditIdentity = Perm(SyncshellStaffPermission.ManagePage),
            CanSubmit = !Perm(SyncshellStaffPermission.ManageNews)
                || !Perm(SyncshellStaffPermission.ManageGallery),
            Listed = _editSession.Effective(community).IsListedPublicly,
            Ops = communityOps,
            OnSaveNews = (composer, draft) => _composers.SaveNews(group, composer, draft, Reload,
                Perm(SyncshellStaffPermission.ManageNews)),
            OnSaveEvent = composer => _composers.SaveEvent(group, composer, Reload,
                Perm(SyncshellStaffPermission.ManageEvents)),
            OnAddPhoto = () => _composers.PickPhoto(group, community.Gallery?.Count ?? 0, Reload,
                Perm(SyncshellStaffPermission.ManageGallery)),
            OnBannerClicked = () => _composers.PickProfileImage(_community, group,
                "syncshell-community:", true, Reload,
                staged => StageCommunityImage(staged, true)),
            OnAvatarClicked = () => _composers.PickProfileImage(_community, group,
                "syncshell-community:", false, Reload,
                staged => StageCommunityImage(staged, false)),
            OnNameCommitted = v => _editSession.StageAlias(v),
            OnTaglineCommitted = v => _editSession.Stage(
                c => c with { Profile = c.Profile with { Tagline = v } }),
            OnDeleteNews = post => _composers.DeleteNews(group, post, Reload),
            OnDeleteEvent = ev => _composers.DeleteEvent(group, ev, Reload),
        };
        edit.OnAboutCommitted = text => _editSession.Stage(
            c => c with { Profile = c.Profile with { Description = text } });
        edit.OnRulesCommitted = text => _editSession.Stage(
            c => c with { RulesText = text });
        _composers.WireEventOps(edit, group, Perm(SyncshellStaffPermission.ManageEvents), Reload);
        _composers.WireCommentOps(edit, group, Reload, CommunityNameFor,
            CommentProfile);
        _composers.WireTravelOps(edit, group, _lifestreamTravel,
            _dalamudUtilService, _ipcManager.Lifestream, Reload);
        WirePlannerHooks(edit, group);
        return edit;
    }

    /// <summary>
    /// The Event Planner hooks - the standalone community window has no
    /// planner page of its own, so "Edit event" deep-links into the hub's
    /// planner tab carrying the event id (+ occurrence scope).
    /// </summary>
    private void WirePlannerHooks(SyncshellCommunityView.SyncshellCommunityEdit edit,
        GroupData group)
    {
        edit.OnEditEvent = (ev, occ) => Mediator.Publish(new OpenSpheneHubMessage(
            SpheneHubPage.Syncshells, group.GID,
            DetailTab: (int)Hub.SpheneHubUi.SyncshellDetailTab.EventPlanner,
            EditEventId: ev.Id,
            EditOccurrence: occ));
        edit.MemberOptions = () => _pairManager.GroupPairs
            .TryGetValue(_group!, out var pairs)
                ? pairs.Select(p => (p.UserData.UID, p.UserData.PreferredName)).ToList()
                : (IReadOnlyList<(string Uid, string Label)>)[];
        edit.MemberLabel = uid =>
            _pairManager.GetPairByUID(uid)?.UserData.PreferredName ?? "Unknown user";
        edit.MentionCandidates = () =>
        {
            var list = new List<UI.Components.MarkdownComposer.MentionCandidate>();
            if (_group != null && _pairManager.GroupPairs.TryGetValue(_group, out var gps))
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
        var ownUid = _apiController.UID;
        edit.CanEditEvent = ev => edit.CanManageEvents
            || string.Equals(ev.AuthorUid, ownUid, StringComparison.Ordinal)
            || (ev.OrganizerUids?.Contains(ownUid, StringComparer.Ordinal) ?? false);
        edit.OnPreviewEvent = composer => Mediator.Publish(
            new OpenEventPreviewMessage(group, _editSession.Effective(_community),
                _group, composer));
        // The standalone window has no rail - "View" drills into the
        // community surface's own event detail.
        edit.OnViewEvent = ev => _page.EventId = ev.Id;
    }

    /// <summary>Member list for the staff subpage's assignment picker · owner excluded.</summary>
    /// <summary>Community name resolution: pair preferred name, the
    /// server-provided identity rows, then the profile cache. Never a uid.</summary>
    private string? CommunityNameFor(string uid)
    {
        var name = SyncshellCommunityView.HumanName(_pairManager.GetPairByUID(uid)?.UserData);
        if (name != null) return name;
        name = _group?.MemberIdentities != null
            && _group.MemberIdentities.TryGetValue(uid, out var g)
            ? SyncshellCommunityView.HumanName(g) : null;
        if (name != null) return name;
        name = SyncshellCommunityView.HumanName(_community.MemberIdentities?
            .FirstOrDefault(u => string.Equals(u.UID, uid, StringComparison.Ordinal)));
        return name ?? _spheneProfileManager.GetPreferredName(uid);
    }

    /// <summary>Profile lookup for the inline comment avatar.</summary>
    private SpheneProfileData CommentProfile(string uid)
        => _spheneProfileManager.GetSpheneProfile(new UserData(uid));

    private IReadOnlyList<SyncshellCommunityView.CommunityStaffMember> StaffMembers()
        => _group == null
            ? []
            : _group.GroupPairUserInfos
                .Where(kv => !string.Equals(kv.Key, _group.OwnerUID, StringComparison.Ordinal))
                .OrderByDescending(kv => kv.Value.IsModerator())
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new SyncshellCommunityView.CommunityStaffMember(kv.Key,
                    CommunityNameFor(kv.Key) ?? "Unknown user",
                    kv.Value.IsModerator()))
                .ToList();

    /// <summary>Toggles the like on the server, then patches this window's community copy.</summary>
    private async Task<SyncshellGalleryImageDto?> ToggleImageLike(SyncshellGalleryImageDto img)
    {
        if (_group == null || img.Id <= 0)
        {
            return null;
        }
        try
        {
            var result = await _apiController.GroupToggleCommunityGalleryImageLike(
                new SyncshellGalleryImageLikeDto(_group.Group, img.Id, false, 0))
                .ConfigureAwait(false);
            if (result == null)
            {
                return null;
            }
            var gallery = _community.Gallery;
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

    /// <summary>
    /// The folded strip: a thin slice of the hero · banner as backdrop, an
    /// expand chevron, the shell name, and close.
    /// </summary>
    protected override void DrawCollapsedContent()
    {
        var scale = SpheneUI.Scale;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        // Same full-bleed treatment as the open hero · covers the border ring.
        var inner = pos;
        var innerMax = pos + size;
        var rounding = ImGui.GetStyle().WindowRounding;
        var dl = ImGui.GetWindowDrawList();

        var banner = _community.Profile.BannerMediaHash == null
                && string.IsNullOrEmpty(_community.Profile.BannerImageBase64)
            ? null
            : SpheneUI.Media.Texture($"syncshell-community:banner:{_community.Profile.GroupGID}:{SyncshellCommunityView.MediaVersion(_community.Profile.BannerMediaHash, _community.Profile.BannerImageBase64)}",
                SyncshellCommunityView.ResolveBytes(_community.Profile.BannerMediaHash,
                    _community.Profile.BannerImageBase64));
        if (banner != null)
        {
            ImGui.PushClipRect(pos, pos + size, false);
            SpheneUI.Media.ImageInRect(dl, banner, inner, innerMax, ImageFit.Cover,
                rounding: rounding, corners: ImDrawFlags.RoundCornersAll);
            dl.AddRectFilled(inner, innerMax,
                SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.45f)),
                rounding, ImDrawFlags.RoundCornersAll);
            ImGui.PopClipRect();
        }

        // Pixel-identical to DrawHeroChrome: same chip size, right pad and
        // spacing so fold + close never move across the fold transition.
        var chip = ImGui.GetFrameHeight() + 8f * scale;
        var pad = 12f * scale;
        var closeX = innerMax.X - pad - chip;
        var expandX = closeX - 8f * scale - chip;
        var rowY = pos.Y + (size.Y - chip) * 0.5f;

        var name = CommunityDisplayName(_group, _community);
        var nameX = inner.X + 8f * scale;
        ImGui.SetCursorScreenPos(new Vector2(nameX, pos.Y + (size.Y - ImGui.GetTextLineHeight()) * 0.5f));
        var nameAvail = expandX - 6f * scale - nameX;
        var nameShown = SpheneUI.Text.Ellipsize(name, MathF.Max(0f, nameAvail));
        SpheneUI.Text.Plain(nameShown);
        if (nameShown.Length != name.Length)
        {
            SpheneUI.AttachTooltip(name);
        }

        ImGui.SetCursorScreenPos(new Vector2(expandX, rowY));
        if (SyncshellCommunityView.ChromeChipButton(FontAwesomeIcon.ChevronDown, "Expand",
                scale, chip))
        {
            SpheneUI.Chrome.ToggleFold(WindowName);
        }

        ImGui.SetCursorScreenPos(new Vector2(closeX, rowY));
        if (SyncshellCommunityView.ChromeChipButton(FontAwesomeIcon.Times, "Close", scale, chip))
        {
            IsOpen = false;
        }
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}
