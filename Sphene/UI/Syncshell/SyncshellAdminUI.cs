using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.CharaData;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Sphene.Utils;
using Sphene.UI.Syncshell;

namespace Sphene.UI.Components.Popup;

public class SyncshellAdminUI : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly bool _isModerator = false;
    private readonly bool _isOwner = false;
    private readonly List<string> _oneTimeInvites = [];
    private readonly PairManager _pairManager;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private List<BannedGroupUserDto> _bannedUsers = [];
    private Task<List<BannedGroupUserDto>>? _bannedUsersTask;
    private int _multiInvites;
    private string _newPassword;
    private bool _pwChangeSuccess;
    private Task<bool>? _pwChangeTask;
    private Task<bool>? _aliasChangeTask;
    private Task<List<string>>? _singleInviteTask;
    private Task<int>? _pruneTestTask;
    private Task<int>? _pruneTask;
    private int _pruneDays = 14;

    // Community aggregate - lazily pulled so member rows can show staff roles.
    private SyncshellCommunityDto? _community;
    private Task<SyncshellCommunityDto?>? _communityTask;

    // Event Planner - same wiring the community popout uses: shared composers,
    // travel hooks and a per-window page state so planner filters/selection
    // stay independent of the hub's community surface.
    private readonly CommunityComposers _composers;
    private readonly LifestreamTravelService _lifestreamTravel;
    private readonly Interop.Ipc.IpcManager _ipcManager;
    private readonly SyncshellCommunityView.CommunityPageState _plannerPage = new();

    // Membership applications & join policy
    private List<MembershipApplicationDto>? _applications;
    private Task<List<MembershipApplicationDto>>? _applicationsTask;
    private bool _applicationsLoaded;
    private Task<MembershipApplicationDto?>? _decisionTask;
    private bool _decisionFailed;
    private SyncshellJoinPolicy? _joinPolicy;
    private Task<SyncshellJoinPolicy>? _joinPolicyTask;
    private Task<bool>? _joinPolicySetTask;

    // Publishing policy & review queue
    private SyncshellPublishingPolicyDto? _publishPolicy;
    private Task<SyncshellPublishingPolicyDto>? _publishPolicyTask;
    private Task<bool>? _publishPolicySetTask;
    private List<CommunityReviewItemDto>? _reviewItems;
    private List<CommunityReportDto>? _reports;
    private Task<ModerationQueueDto>? _reportsTask;
    private Task<bool>? _reportDecisionTask;
    private bool _reportsLoaded;
    private bool _reportDecisionFailed;
    private Task<CommunityReviewQueueDto>? _reviewTask;
    private bool _reviewLoaded;
    private Task<bool>? _reviewDecisionTask;
    private bool _reviewDecisionFailed;
    private string _reviewNote = string.Empty;

    // Area binding related fields
    
    private bool _areaBindingEnabled = false;
    private bool _autoBroadcastEnabled = true;
    private bool _requireOwnerPresence = false;
    private int _maxAutoJoinUsers = 50;
    private bool _notifyOnUserEnter = true;
    private bool _notifyOnUserLeave = true;
    private string _customJoinMessage = string.Empty;
    
    // Rules and consent system fields
    private string _joinRules = string.Empty;
    private int _rulesVersion = 1;
    private bool _requireRulesAcceptance = false;
    private readonly List<string> _rulesList = new();
    // Sticky dirty flag for the area-binding settings cards · a per-frame
    // local only lives as long as the click frame, which made the old Apply
    // button unreachable the very next frame.
    private bool _areaSettingsDirty;
    
    // Multi-location support
    private List<AreaBoundLocationDto> _boundAreas = new();
    
    // Alias setting fields
    private string _newAlias = string.Empty;
    private bool _aliasChangeSuccess = true;

    private bool _invitePanelOpen;
    private string _rosterFilter = string.Empty;

    // User list pagination fields
    private int _userListCurrentPage = 0;
    private const int _userListItemsPerPage = 20;
    
    private readonly HousingOwnershipService _housingOwnershipService;
    
    // UI-only state for property preferences - immediate UI updates
    
    // Active sidebar page
    private AdminPage _activeAdminPage = AdminPage.Settings;
    private bool _embedded;
    private bool _areaBindingTabWasOpen = false;
    private readonly Dictionary<string, (bool AllowOutdoor, bool AllowIndoor)> _uiPropertyStates = new(StringComparer.Ordinal);
    
    // Track properties that have been deleted in the UI (to hide them until server confirms)
    private readonly HashSet<string> _deletedPropertyKeys = new(StringComparer.Ordinal);
    
    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, SpheneMediator mediator, ApiController apiController,
        PairManager pairManager, DalamudUtilService dalamudUtilService, GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService, HousingOwnershipService housingOwnershipService, SpheneProfileManager spheneProfileManager,
        CommunityMediaClientService communityMedia, LifestreamTravelService lifestreamTravel, Interop.Ipc.IpcManager ipcManager)
        : base(logger, mediator, "Syncshell Management (" + groupFullInfo.GroupAliasOrGID + ")", performanceCollectorService)
    {
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        _housingOwnershipService = housingOwnershipService;
        _spheneProfileManager = spheneProfileManager;
        _composers = new CommunityComposers(apiController, mediator, logger, communityMedia);
        _lifestreamTravel = lifestreamTravel;
        _ipcManager = ipcManager;
        _isOwner = string.Equals(GroupFullInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        _isModerator = GroupFullInfo.GroupUserInfo.IsModerator();
        _newPassword = string.Empty;
        _multiInvites = 30;
        _pwChangeSuccess = true;
        IsOpen = true;
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(1024, 500),
            MaximumSize = new(1024, 2000),
        };
        
        // Load current area binding if exists
        _ = LoadCurrentAreaBinding();

        Mediator.Subscribe<CommunityChangedMessage>(this, msg =>
        {
            if (!string.Equals(msg.Community.Group.GID, GroupFullInfo.Group.GID,
                    StringComparison.Ordinal))
            {
                return;
            }
            _plannerPage.Comments.InvalidateForChange(msg.Community);
            _staffPageState.Comments.InvalidateForChange(msg.Community);
            _ = RefreshCommunityAsync();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _housingOwnershipService.ForceRefreshFromServer().ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors here; UI will still show local properties
            }
            InitializeUiStateFromServer();
        });
    }

    public GroupFullInfoDto GroupFullInfo { get; private set; }
    
    private async Task LoadCurrentAreaBinding()
    {
        _areaSettingsDirty = false;
        try
        {
            var areaBoundSyncshells = await _apiController.GroupGetAreaBoundSyncshells().ConfigureAwait(false);
            var currentBinding = areaBoundSyncshells.FirstOrDefault(x => string.Equals(x.Group.GID, GroupFullInfo.Group.GID, StringComparison.Ordinal));
            
            if (currentBinding != null)
            {
                _areaBindingEnabled = true;
                
                // Load bound areas
                _boundAreas = currentBinding.BoundAreas.ToList();
                
                // Load global settings
                var areaBoundSettings = currentBinding.Settings;
                _autoBroadcastEnabled = areaBoundSettings.AutoBroadcastEnabled;
                _requireOwnerPresence = areaBoundSettings.RequireOwnerPresence;
                _maxAutoJoinUsers = areaBoundSettings.MaxAutoJoinUsers;
                _notifyOnUserEnter = areaBoundSettings.NotifyOnUserEnter;
                _notifyOnUserLeave = areaBoundSettings.NotifyOnUserLeave;
                _customJoinMessage = areaBoundSettings.CustomJoinMessage ?? string.Empty;
                
                // Load rules settings
                _joinRules = areaBoundSettings.JoinRules ?? string.Empty;
                _rulesVersion = areaBoundSettings.RulesVersion;
                _requireRulesAcceptance = areaBoundSettings.RequireRulesAcceptance;
                
                // Parse existing rules into list
                ParseRulesIntoList();

                var before = new HashSet<string>(_boundAreas.Select(GetBoundAreaKey), StringComparer.Ordinal);
                NormalizeBoundAreaMapIdsInPlace();
                PruneUnsupportedBoundAreasInPlace();
                NormalizeBoundAreasInPlace();
                var after = new HashSet<string>(_boundAreas.Select(GetBoundAreaKey), StringComparer.Ordinal);

                if (!before.SetEquals(after))
                {
                    if (_boundAreas.Count == 0)
                    {
                        await RemoveAreaBinding().ConfigureAwait(false);
                        return;
                    }

                    await UpdateAreaBindingSettings().ConfigureAwait(false);
                }
            }
            else
            {
                _areaBindingEnabled = false;
                _boundAreas.Clear();
                
                // Reset UI variables to defaults
                _autoBroadcastEnabled = true;
                _requireOwnerPresence = false;
                _maxAutoJoinUsers = 50;
                _notifyOnUserEnter = true;
                _notifyOnUserLeave = true;
                _customJoinMessage = string.Empty;
                
                // Reset rules settings to defaults
                _joinRules = string.Empty;
                _rulesVersion = 1;
                _requireRulesAcceptance = false;
                _rulesList.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load current area binding for group {GroupId}", GroupFullInfo.Group.GID);
        }
    }

    protected override void DrawInternal()
    {
        SpheneUI.Layout.PageHeader("Syncshell Management", "shelladmin", () =>
        {
            SpheneUI.Text.MutedWrapped("The same management pages as the Hub, in a separate window - invites, members, roles, events and settings. The Community view stays in the Hub.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        if (!_isModerator && !_isOwner) return;

        // Find group by GID instead of GroupData object to handle alias renames
        var updatedGroupFullInfo = _pairManager.Groups.Values.FirstOrDefault(g => 
            string.Equals(g.Group.GID, GroupFullInfo.Group.GID, StringComparison.Ordinal));
        
        if (updatedGroupFullInfo == null)
        {
            return;
        }
        GroupFullInfo = updatedGroupFullInfo;

        using var id = ImRaii.PushId("syncshell_admin_" + GroupFullInfo.GID);

        DrawAdminContent();
    }

    /// <summary>Renders the admin panel inside another window (Sphene Hub); the host provides the surrounding container.</summary>
    internal void DrawEmbedded()
    {
        _embedded = true;
        try
        {
            DrawInternal();
        }
        finally
        {
            _embedded = false;
        }
    }

    internal enum AdminPage
    {
        Settings,
        Members,
        EventPlanner,
        Staff,
        AreaBinding,
    }

    /// <summary>
    /// Renders a single admin page without the internal rail · used when the
    /// hub sidebar drives navigation so the shell's sections live in the
    /// window rail instead of a second nested sidebar.
    /// </summary>
    internal void DrawEmbeddedPage(AdminPage page)
    {
        if (page == AdminPage.AreaBinding && !_isOwner)
        {
            return;
        }
        _embedded = true;
        try
        {
            var onAreaBinding = page == AdminPage.AreaBinding;
            if (onAreaBinding && !_areaBindingTabWasOpen)
            {
                _ = LoadCurrentAreaBinding();
            }
            _areaBindingTabWasOpen = onAreaBinding;

            switch (page)
            {
                case AdminPage.Settings:
                    DrawSettingsPage();
                    break;
                case AdminPage.Members:
                    DrawUserManagementPage();
                    break;
                case AdminPage.EventPlanner:
                    DrawEventPlannerPage();
                    break;
                case AdminPage.Staff:
                    DrawStaffPage();
                    break;
                case AdminPage.AreaBinding:
                    DrawAreaBindingPage();
                    break;
            }
        }
        finally
        {
            _embedded = false;
        }
    }

    private void DrawAdminContent()
    {
        if (_activeAdminPage == AdminPage.AreaBinding && !_isOwner)
        {
            _activeAdminPage = AdminPage.Settings;
        }

        var theme = SpheneTheme.Current;
        var accentHome = theme.AccentBlue;
        var accentMembers = SpheneUI.Palette.NetworkActive;
        var accentShell = SpheneUI.Palette.EtherealPurple;
        var accentArea = new Vector4(0.93f, 0.68f, 0.38f, 1f);

        // Warm the community aggregate so the staff-nav gate resolves for
        // non-owner role managers as soon as data lands.
        EnsureCommunityLoaded();
        var canRoles = CanManageStaffRoles();
        if (_activeAdminPage == AdminPage.Staff && !canRoles && _community != null)
        {
            _activeAdminPage = AdminPage.Settings;
        }

        // Mirrors the hub rail's page order minus Community - the hub keeps
        // that one surface exclusive to its own rail.
        var navItems = (_isOwner
            ? new (FontAwesomeIcon, string)[]
            {
                (FontAwesomeIcon.Users, "Members"),
                (FontAwesomeIcon.CalendarAlt, "Event Planner"),
                (FontAwesomeIcon.UserTag, "Roles & Staff"),
                (FontAwesomeIcon.SlidersH, "Settings"),
                (FontAwesomeIcon.MapMarkedAlt, "Area Binding"),
            }
            : new (FontAwesomeIcon, string)[]
            {
                (FontAwesomeIcon.Users, "Members"),
                (FontAwesomeIcon.CalendarAlt, "Event Planner"),
                (FontAwesomeIcon.UserTag, "Roles & Staff"),
                (FontAwesomeIcon.SlidersH, "Settings"),
            });
        if (!canRoles && _community != null)
        {
            navItems = navItems.Where(i =>
                !string.Equals(i.Item2, "Roles & Staff", StringComparison.Ordinal)).ToArray();
        }
        // Inside the hub the rail stays narrow with icon-only buttons, while
        // the standalone window shows the full segmented items with labels.
        var railWidth = _embedded
            ? SpheneUI.Layout.NavRailWidth(navItems)
            : SpheneUI.Layout.NavRailSegmentedWidth(navItems);
        SpheneUI.Layout.BeginNavRail("syncshell-admin", railWidth, _embedded,
            backdropPattern: !_embedded);
        // Same top offset as the hub rail so both sidebars' first buttons
        // start at the same height when embedded.
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        void AdminNavItem(string label, FontAwesomeIcon icon, AdminPage page, Vector4 accent)
        {
            var active = _activeAdminPage == page;
            var clicked = _embedded
                ? SpheneUI.Layout.NavRailItem(label, active, accent, icon: icon)
                : SpheneUI.Layout.NavRailSegmentItem(label, icon, active, accent);
            if (clicked)
            {
                _activeAdminPage = page;
            }
        }

        AdminNavItem("Members", FontAwesomeIcon.Users, AdminPage.Members, accentMembers);
        // Event Planner sits between the member roster and the management
        // block - same position and community accent as in the hub rail.
        AdminNavItem("Event Planner", FontAwesomeIcon.CalendarAlt, AdminPage.EventPlanner, accentHome);
        SpheneUI.Layout.NavRailDivider(accentShell, accentHome);
        if (canRoles || _community == null)
        {
            AdminNavItem("Roles & Staff", FontAwesomeIcon.UserTag, AdminPage.Staff, accentShell);
        }
        AdminNavItem("Settings", FontAwesomeIcon.SlidersH, AdminPage.Settings, accentHome);
        if (_isOwner)
        {
            AdminNavItem("Area Binding", FontAwesomeIcon.MapMarkedAlt, AdminPage.AreaBinding, accentArea);
        }
        SpheneUI.Layout.EndNavRail();

        var onAreaBinding = _activeAdminPage == AdminPage.AreaBinding;
        if (onAreaBinding && !_areaBindingTabWasOpen)
        {
            _ = LoadCurrentAreaBinding();
        }
        _areaBindingTabWasOpen = onAreaBinding;

        if (SpheneUI.Layout.BeginNavContent("syncshell-admin"))
        {
            switch (_activeAdminPage)
            {
                case AdminPage.Settings:
                    DrawSettingsPage();
                    break;
                case AdminPage.Members:
                    DrawUserManagementPage();
                    break;
                case AdminPage.EventPlanner:
                    DrawEventPlannerPage();
                    break;
                case AdminPage.Staff:
                    DrawStaffPage();
                    break;
                case AdminPage.AreaBinding:
                    DrawAreaBindingPage();
                    break;
            }
        }
        SpheneUI.Layout.EndNavContent();
    }

    /// <summary>
    /// Settings as SectionCards on a two-column board, then a danger-tinted
    /// surface for the destructive action.
    /// </summary>
    private void DrawSettingsPage()
    {
        var perm = GroupFullInfo.GroupPermissions;
        var accent = SpheneTheme.Current.AccentBlue;
        void PushPerms()
            => _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));

        if (_isOwner)
        {
            using var board = SpheneUI.Layout.BeginCardColumns("settings-board-" + GroupFullInfo.Group.GID, 2,
                ImGuiTableFlags.SizingStretchSame);
            if (board.Success)
            {
                // Two balanced, logically grouped columns: everything about
                // who gets in and how on the left, what the shell is and may
                // publish on the right.
                SpheneUI.Tables.NextColumn();
                DrawAccessConsole(perm, PushPerms, accent);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                DrawJoinPolicyConsole(accent);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                DrawSecurityConsole(accent);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                DrawDefaultsConsole(perm, PushPerms, accent);

                SpheneUI.Tables.NextColumn();
                DrawIdentityConsole(accent);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                DrawPublishingPolicyConsole(accent);
            }
        }
        else
        {
            DrawAccessConsole(perm, PushPerms, accent);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawJoinPolicyConsole(accent);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawPublishingPolicyConsole(accent);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawDefaultsConsole(perm, PushPerms, accent);
        }

        if (!_isOwner)
        {
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);

        // Danger strip · the tinted surface variant keeps it visibly apart
        // from the neutral console cards without bespoke chrome.
        SpheneUI.Layout.Surface(() =>
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
            ImGui.SameLine();
            SpheneUI.Text.Plain("Dissolve this syncshell");
            ImGui.SameLine();
            SpheneUI.Text.Muted(" ·  removes it for every member, irreversible");

            var delW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Trash, "Delete Syncshell");
            SpheneUI.Controls.DockToRightEdge(delW);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Delete Syncshell")
                && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
            {
                IsOpen = false;
                _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
            }
            SpheneUI.AttachTooltip("Hold CTRL and Shift and click to delete this Syncshell."
                + Environment.NewLine + "WARNING: this action is irreversible.");
        }, id: "admin-danger", accentColor: SpheneUI.Colors.Danger);
    }

    private static void DrawAccessConsole(GroupPermissions perm, Action pushPerms, Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            var invitesOpen = !perm.IsDisableInvites();
            if (SpheneUI.Controls.Toggle("Open to new souls", ref invitesOpen))
            {
                perm.SetDisableInvites(!invitesOpen);
                pushPerms();
            }
            SpheneUI.AttachTooltip("Locked syncshells cannot be joined by new users.");
            SpheneUI.Text.MutedWrapped("Locking disables invites and prevents new users from joining.");
        }, "Access", accent, icon: FontAwesomeIcon.Lock);
    }

    /// <summary>
    /// How new members may join - maps 1:1 to the server's
    /// <see cref="SyncshellJoinPolicy"/>. Moderator-or-owner gated
    /// server-side; the legacy "Open to new souls" lock still overrides to
    /// Closed regardless of the policy picked here.
    /// </summary>
    private void DrawJoinPolicyConsole(Vector4 accent)
    {
        // lazy-load the current policy once per visit
        if (_joinPolicy == null && _joinPolicyTask == null)
        {
            _joinPolicyTask = _apiController.GroupGetJoinPolicy(new GroupDto(GroupFullInfo.Group));
        }
        if (_joinPolicyTask is { IsCompleted: true })
        {
            if (_joinPolicyTask.IsCompletedSuccessfully) _joinPolicy = _joinPolicyTask.Result;
            _joinPolicyTask = null;
        }
        if (_joinPolicySetTask is { IsCompleted: true })
        {
            if (_joinPolicySetTask is { IsCompletedSuccessfully: true, Result: false })
            {
                _joinPolicy = null; // server rejected - reload truth
            }
            _joinPolicySetTask = null;
        }

        var policies = new[]
        {
            SyncshellJoinPolicy.PasswordProtected,
            SyncshellJoinPolicy.Public,
            SyncshellJoinPolicy.ApprovalRequired,
            SyncshellJoinPolicy.InviteOnly,
            SyncshellJoinPolicy.Closed,
        };
        static string Label(SyncshellJoinPolicy p) => p switch
        {
            SyncshellJoinPolicy.PasswordProtected => "Password or invite",
            SyncshellJoinPolicy.Public => "Public · anyone can join",
            SyncshellJoinPolicy.ApprovalRequired => "Approval required · join requests",
            SyncshellJoinPolicy.InviteOnly => "Invite only · no password",
            _ => "Closed · no new members",
        };

        SpheneUI.Layout.SectionCard(() =>
        {
            var current = _joinPolicy ?? SyncshellJoinPolicy.PasswordProtected;
            using (ImRaii.Disabled(_joinPolicy == null || _joinPolicySetTask is { IsCompleted: false }))
            {
                SpheneUI.Controls.Combo("##joinpolicy-" + GroupFullInfo.Group.GID, policies, Label,
                    onSelected: picked =>
                    {
                        if (picked is SyncshellJoinPolicy p && p != _joinPolicy)
                        {
                            _joinPolicy = p; // optimistic; failure reloads
                            _joinPolicySetTask = _apiController.GroupSetJoinPolicy(
                                new GroupJoinPolicyDto(GroupFullInfo.Group, p));
                        }
                    },
                    initialSelectedItem: current,
                    preferredWidth: 240f * SpheneUI.Scale);
            }
            SpheneUI.Text.MutedWrapped(_joinPolicy == null
                ? "Loading current join policy..."
                : Label(current) + " - takes effect immediately for new joiners. Locking the shell above still overrides this.");
        }, "Join Policy", accent, icon: FontAwesomeIcon.SignInAlt);
    }

    /// <summary>
    /// Per-content-kind publish modes - <see cref="SyncshellPublishMode"/>.
    /// Moderator-or-owner gated server-side; DirectPublishBits stay
    /// owner-managed until the staff-matrix UI lands in Phase 5.
    /// </summary>
    private void DrawPublishingPolicyConsole(Vector4 accent)
    {
        if (_publishPolicy == null && _publishPolicyTask == null)
        {
            _publishPolicyTask = _apiController.GroupGetPublishingPolicy(new GroupDto(GroupFullInfo.Group));
        }
        if (_publishPolicyTask is { IsCompleted: true })
        {
            if (_publishPolicyTask.IsCompletedSuccessfully) _publishPolicy = _publishPolicyTask.Result;
            _publishPolicyTask = null;
        }
        if (_publishPolicySetTask is { IsCompleted: true })
        {
            _publishPolicy = null; // refresh truth after any write
            _publishPolicySetTask = null;
        }

        var modes = new[]
        {
            SyncshellPublishMode.Direct,
            SyncshellPublishMode.ApprovalRequired,
            SyncshellPublishMode.StaffOnly,
        };
        static string Label(SyncshellPublishMode m) => m switch
        {
            SyncshellPublishMode.Direct => "Direct · publishes instantly",
            SyncshellPublishMode.StaffOnly => "Staff only",
            _ => "Approval required · staff review",
        };

        SpheneUI.Layout.SectionCard(() =>
        {
            var busy = _publishPolicy == null || _publishPolicySetTask is { IsCompleted: false };
            using (ImRaii.Disabled(busy))
            {
                var news = _publishPolicy?.NewsMode ?? SyncshellPublishMode.ApprovalRequired;
                var gallery = _publishPolicy?.GalleryMode ?? SyncshellPublishMode.ApprovalRequired;
                var events = _publishPolicy?.EventsMode ?? SyncshellPublishMode.StaffOnly;
                var comments = _publishPolicy?.CommentsMode ?? CommunityCommentsMode.Open;
                SpheneUI.Text.Muted("News posts");
                SpheneUI.Controls.Combo("##pubnews-" + GroupFullInfo.Group.GID, modes, Label,
                    onSelected: picked =>
                    {
                        if (picked is SyncshellPublishMode m && m != news)
                        {
                            _publishPolicySetTask = _apiController.GroupSetPublishingPolicy(
                                new SyncshellPublishingPolicyDto(GroupFullInfo.Group,
                                    m, gallery, _publishPolicy?.DirectPublishBits ?? 0,
                                    EventsMode: events, CommentsMode: comments));
                        }
                    },
                    initialSelectedItem: news, preferredWidth: 240f * SpheneUI.Scale);
                SpheneUI.Text.Muted("Gallery images");
                SpheneUI.Controls.Combo("##pubgal-" + GroupFullInfo.Group.GID, modes, Label,
                    onSelected: picked =>
                    {
                        if (picked is SyncshellPublishMode m && m != gallery)
                        {
                            _publishPolicySetTask = _apiController.GroupSetPublishingPolicy(
                                new SyncshellPublishingPolicyDto(GroupFullInfo.Group,
                                    news, m, _publishPolicy?.DirectPublishBits ?? 0,
                                    EventsMode: events, CommentsMode: comments));
                        }
                    },
                    initialSelectedItem: gallery, preferredWidth: 240f * SpheneUI.Scale);
                SpheneUI.Text.Muted("Events");
                SpheneUI.Controls.Combo("##pubevt-" + GroupFullInfo.Group.GID, modes, Label,
                    onSelected: picked =>
                    {
                        if (picked is SyncshellPublishMode m && m != events)
                        {
                            _publishPolicySetTask = _apiController.GroupSetPublishingPolicy(
                                new SyncshellPublishingPolicyDto(GroupFullInfo.Group,
                                    news, gallery, _publishPolicy?.DirectPublishBits ?? 0,
                                    EventsMode: m, CommentsMode: comments));
                        }
                    },
                    initialSelectedItem: events, preferredWidth: 240f * SpheneUI.Scale);
                var commentModes = new[]
                {
                    CommunityCommentsMode.Open,
                    CommunityCommentsMode.StaffOnly,
                    CommunityCommentsMode.Disabled,
                };
                static string CommentLabel(CommunityCommentsMode m) => m switch
                {
                    CommunityCommentsMode.Open => "All members",
                    CommunityCommentsMode.StaffOnly => "Staff only",
                    _ => "Disabled",
                };
                SpheneUI.Text.Muted("Comments");
                SpheneUI.Controls.Combo("##pubcmt-" + GroupFullInfo.Group.GID, commentModes,
                    CommentLabel,
                    onSelected: picked =>
                    {
                        if (picked is CommunityCommentsMode m && m != comments)
                        {
                            _publishPolicySetTask = _apiController.GroupSetPublishingPolicy(
                                new SyncshellPublishingPolicyDto(GroupFullInfo.Group,
                                    news, gallery, _publishPolicy?.DirectPublishBits ?? 0,
                                    EventsMode: events, CommentsMode: m));
                        }
                    },
                    initialSelectedItem: comments, preferredWidth: 240f * SpheneUI.Scale);
            }
            SpheneUI.Text.MutedWrapped(_publishPolicy == null
                ? "Loading publishing policy..."
                : "Member submissions land in the review queue on the Members page until approved.");
        }, "Publishing Policy", accent, icon: FontAwesomeIcon.UserEdit);
    }

    private void DrawDefaultsConsole(GroupPermissions perm, Action pushPerms, Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            void SuggestedToggle(string label, bool disabled, Action<bool> apply)
            {
                var enabled = !disabled;
                if (SpheneUI.Controls.Toggle(label, ref enabled))
                {
                    apply(!enabled);
                    pushPerms();
                }
            }

            SuggestedToggle("Sound sync", perm.IsPreferDisableSounds(), v => perm.SetPreferDisableSounds(v));
            SuggestedToggle("Animation sync", perm.IsPreferDisableAnimations(), v => perm.SetPreferDisableAnimations(v));
            SuggestedToggle("VFX sync", perm.IsPreferDisableVFX(), v => perm.SetPreferDisableVFX(v));
            SpheneUI.Text.MutedWrapped("Permission suggestions offered on join · members stay free to keep their own.");
        }, "Join Defaults", accent, icon: FontAwesomeIcon.UserCog);
    }

    private void DrawIdentityConsole(Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            var currentAlias = GroupFullInfo.Group.Alias;
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Muted("Current:");
            ImGui.SameLine();
            SpheneUI.Text.Colored(!string.IsNullOrEmpty(currentAlias) ? currentAlias : "no alias set",
                !string.IsNullOrEmpty(currentAlias) ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);

            var trimmed = _newAlias.Trim();
            var aliasValid = trimmed.Length >= 3 && trimmed.Length <= 50;
            SpheneUI.Controls.InputTextWithHint("##changealias", "New alias · 3-50 chars", ref _newAlias, 50);
            ImGui.SameLine();
            using (ImRaii.Disabled(!aliasValid || _aliasChangeTask is { IsCompleted: false }))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Check))
                {
                    _aliasChangeTask = _apiController.GroupSetAlias(new GroupAliasDto(GroupFullInfo.Group, trimmed));
                    _aliasChangeSuccess = true;
                }
            }
            SpheneUI.AttachTooltip("Set the vanity alias (3-50 characters).");
            if (!string.IsNullOrEmpty(currentAlias))
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(!UiSharedService.CtrlPressed() || _aliasChangeTask is { IsCompleted: false }))
                {
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
                    {
                        _aliasChangeTask = _apiController.GroupSetAlias(new GroupAliasDto(GroupFullInfo.Group, null));
                        _aliasChangeSuccess = true;
                    }
                }
                SpheneUI.AttachTooltip("Clear the alias · the syncshell ID shows instead."
                    + SpheneUI.TooltipSeparator + "Hold CTRL to enable");
            }

            if (_aliasChangeTask is { IsCompleted: false })
            {
                SpheneUI.Text.Muted("Setting alias...");
            }
            else if (_aliasChangeTask != null)
            {
                _aliasChangeSuccess = _aliasChangeTask.IsCompletedSuccessfully && _aliasChangeTask.Result;
                if (_aliasChangeSuccess)
                {
                    _newAlias = string.Empty;
                }
                _aliasChangeTask = null;
            }
            if (!_aliasChangeSuccess)
            {
                SpheneUI.Text.Colored("Alias must be 3-50 characters, unique, and needs ManagePage rights.", ImGuiColors.DalamudYellow);
            }
        }, "Identity", accent, icon: FontAwesomeIcon.IdCard);
    }

    private void DrawSecurityConsole(Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Controls.InputTextWithHint("##changepw", "New join password · min 10 chars", ref _newPassword, 50);
            ImGui.SameLine();
            using (ImRaii.Disabled(_newPassword.Length < 10 || _pwChangeTask is { IsCompleted: false }))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Key))
                {
                    _pwChangeTask = _apiController.GroupChangePassword(new GroupPasswordDto(GroupFullInfo.Group, _newPassword));
                    _newPassword = string.Empty;
                    _pwChangeSuccess = true;
                }
            }
            SpheneUI.AttachTooltip("Rotate the join password · takes effect immediately.");

            if (_pwChangeTask is { IsCompleted: false })
            {
                SpheneUI.Text.Muted("Changing password...");
            }
            else if (_pwChangeTask != null)
            {
                _pwChangeSuccess = _pwChangeTask.IsCompletedSuccessfully && _pwChangeTask.Result;
                _pwChangeTask = null;
            }
            if (!_pwChangeSuccess)
            {
                SpheneUI.Text.Colored("Failed · needs at least 10 characters.", ImGuiColors.DalamudYellow);
            }
        }, "Security", accent, icon: FontAwesomeIcon.ShieldAlt);
    }

    /// <summary>Invite generation as a card · lives on the Members page since
    /// it is member-facing tooling, not its own destination.</summary>
    /// <summary>
    /// Collapsible invite band under the command strip · a bordered strip, not
    /// a card, so the roster stays the page's dominant element.
    /// </summary>
    private void DrawInviteStrip()
    {
        var scale = SpheneUI.Scale;

        using (ImRaii.PushId("invite-strip"))
        {
            SpheneUI.Layout.Group(() =>
            {
                using (ImRaii.Disabled(_singleInviteTask is { IsCompleted: false }))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Envelope, "Single invite"))
                    {
                        _singleInviteTask = _apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1);
                    }
                }
                SpheneUI.AttachTooltip("Creates a single-use invite (valid for 24h) and copies it to the clipboard.");
                ImGui.SameLine(0f, SpheneUI.Spacing.X16.Scaled);

                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Muted("Batch:");
                ImGui.SameLine();
                SpheneUI.Controls.InputInt("##amountofinvites", ref _multiInvites, preferredWidth: 60);
                ImGui.SameLine();
                using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Envelope, "Generate " + _multiInvites))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var invites = await _apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), _multiInvites).ConfigureAwait(false);
                                lock (_oneTimeInvites)
                                {
                                    _oneTimeInvites.AddRange(invites);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to generate {Count} one-time invites for group {GID}", _multiInvites, GroupFullInfo.Group.GID);
                            }
                        });
                    }
                }
                SpheneUI.AttachTooltip($"Generate {_multiInvites} one-time invites (2-100 allowed)");

                if (_singleInviteTask is { IsCompleted: false })
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Muted("Generating...");
                }
                else if (_singleInviteTask != null)
                {
                    if (_singleInviteTask.IsCompletedSuccessfully)
                    {
                        ImGui.SetClipboardText(_singleInviteTask.Result.FirstOrDefault() ?? string.Empty);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to generate single invite for group {GID}", GroupFullInfo.Group.GID);
                    }
                    _singleInviteTask = null;
                }

                if (_oneTimeInvites.Count > 0)
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                    var invites = string.Join(Environment.NewLine, _oneTimeInvites);
                    SpheneUI.Controls.InputTextMultiline("##generated_invites", ref invites, 5000,
                        new(0, 100f * scale), ImGuiInputTextFlags.ReadOnly);
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Copy, "Copy all"))
                    {
                        ImGui.SetClipboardText(invites);
                    }
                    ImGui.SameLine();
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear"))
                    {
                        _oneTimeInvites.Clear();
                    }
                }
            });
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    private enum MembersTab
    {
        Members,
        JoinRequests,
        ReviewQueue,
        Reports,
        Housekeeping,
        Banished,
    }

    private MembersTab _membersTab = MembersTab.Members;

    private void DrawUserManagementPage()
    {
        var membersAccent = SpheneUI.Palette.NetworkActive;
        PollMemberQueues();

        var pendingApps = _applications?.Count(a => a.Status == MembershipApplicationStatus.Pending) ?? 0;
        var reviewCount = _reviewItems?.Count ?? 0;
        var reportCount = _reports?.Count ?? 0;
        var tabs = new List<SpheneUI.Controls.TabItem>
        {
            new(FontAwesomeIcon.Users, "Members"),
            new(FontAwesomeIcon.Inbox,
                pendingApps > 0 ? $"Join Requests · {pendingApps}" : "Join Requests"),
            new(FontAwesomeIcon.ClipboardCheck,
                reviewCount > 0 ? $"Review Queue · {reviewCount}" : "Review Queue"),
            new(FontAwesomeIcon.Flag,
                reportCount > 0 ? $"Reports · {reportCount}" : "Reports"),
            new(FontAwesomeIcon.Broom, "Housekeeping"),
            new(FontAwesomeIcon.Ban, "Banished"),
        };
        var hit = SpheneUI.Controls.TabBar("admin-members", tabs, (int)_membersTab, membersAccent);
        if (hit >= 0)
        {
            _membersTab = (MembersTab)hit;
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        switch (_membersTab)
        {
            default:
                DrawMembersTab(membersAccent);
                break;
            case MembersTab.JoinRequests:
                DrawApplicationsPanel(membersAccent);
                break;
            case MembersTab.ReviewQueue:
                DrawReviewQueuePanel(membersAccent);
                break;
            case MembersTab.Reports:
                DrawReportsPanel(membersAccent);
                break;
            case MembersTab.Housekeeping:
                DrawHousekeepingPanel(membersAccent);
                break;
            case MembersTab.Banished:
                DrawBanishedPanel(membersAccent);
                break;
        }
    }

    /// <summary>
    /// Lazy-fetches the community aggregate once so role pills, staff rights
    /// and the staff admin page can resolve roles/assignments.
    /// </summary>
    private void EnsureCommunityLoaded()
    {
        if (_community == null && _communityTask == null)
        {
            _communityTask = _apiController.GroupGetCommunity(new GroupDto(GroupFullInfo.Group));
        }
        if (_communityTask is { IsCompleted: true })
        {
            _community = _communityTask.IsCompletedSuccessfully ? _communityTask.Result : null;
            _communityTask = null;
        }
    }

    /// <summary>Refetches the community aggregate after a staff mutation.</summary>
    private async Task RefreshCommunityAsync()
    {
        try
        {
            _community = await _apiController.GroupGetCommunity(
                new GroupDto(GroupFullInfo.Group)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh community data for {Group}",
                GroupFullInfo.Group.GID);
        }
    }

    /// <summary>
    /// Owner, or a member whose assigned staff role carries the Roles right -
    /// mirrors the server's <c>CanManageStaffRoles</c> gate.
    /// </summary>
    private bool CanManageStaffRoles()
    {
        if (_isOwner)
        {
            return true;
        }
        return (SyncshellCommunityView.MemberPermissionBits(_community, _apiController.UID)
            & SyncshellStaffPermission.ManageRoles) != SyncshellStaffPermission.None;
    }

    /// <summary>Owner/moderator bypass plus the staff-role bit - same gate
    /// the community popout applies for individual permissions.</summary>
    private bool Perm(SyncshellStaffPermission flag)
    {
        if (_isOwner || _isModerator)
        {
            return true;
        }
        return (SyncshellCommunityView.MemberPermissionBits(_community, _apiController.UID)
            & flag) != SyncshellStaffPermission.None;
    }

    /// <summary>
    /// The Event Planner page - identical to the hub's planner tab, hosted
    /// inside this window so the management rail mirrors the hub's page set
    /// (Community stays hub-only). Page state is per-window, so planner
    /// filters and the open composer never leak into the hub surface.
    /// </summary>
    private void DrawEventPlannerPage()
    {
        EnsureCommunityLoaded();
        if (_community == null)
        {
            SpheneUI.Text.Muted("Loading events...");
            return;
        }
        var accent = SyncshellCommunityView.ParseAccent(_community.Profile.AccentColor)
            ?? SpheneUI.Palette.CrystalBlue;
        EventPlannerView.Draw(_plannerPage, BuildPlannerEdit(),
            _composers.BuildTravelHooks(GroupFullInfo.Group, _lifestreamTravel,
                _dalamudUtilService, _ipcManager.Lifestream),
            _community.Events ?? [], accent);
    }

    /// <summary>The event-scoped manage contract for this window's planner -
    /// ops, permissions and hooks map onto the local community cache.</summary>
    private SyncshellCommunityView.SyncshellCommunityEdit BuildPlannerEdit()
    {
        var group = GroupFullInfo.Group;
        var edit = new SyncshellCommunityView.SyncshellCommunityEdit
        {
            CanManageEvents = Perm(SyncshellStaffPermission.ManageEvents),
            CanSubmit = true,
            Listed = _community?.IsListedPublicly ?? false,
            Ops = _composers.CreateOps(group, () => _community, RefreshCommunityAsync,
                StaffMembers, mutate: p => { if (_community != null) _community = p(_community); }),
            OnSaveEvent = composer => _composers.SaveEvent(group, composer,
                RefreshCommunityAsync, Perm(SyncshellStaffPermission.ManageEvents)),
            OnDeleteEvent = ev => _composers.DeleteEvent(group, ev, RefreshCommunityAsync),
        };
        _composers.WireEventOps(edit, group, Perm(SyncshellStaffPermission.ManageEvents),
            RefreshCommunityAsync);
        _composers.WireCommentOps(edit, group, RefreshCommunityAsync,
            AdminNameFor, uid => _spheneProfileManager.GetSpheneProfile(new UserData(uid)));
        _composers.WireTravelOps(edit, group, _lifestreamTravel,
            _dalamudUtilService, _ipcManager.Lifestream, RefreshCommunityAsync);
        WirePlannerHooks(edit, group);
        return edit;
    }

    /// <summary>Admin-surface name resolution: pair preferred name, the
    /// server-provided identity rows, then the profile cache. Never a uid.</summary>
    private string? AdminNameFor(string uid)
    {
        var name = SyncshellCommunityView.HumanName(_pairManager.GetPairByUID(uid)?.UserData);
        if (name != null) return name;
        name = GroupFullInfo.MemberIdentities != null
            && GroupFullInfo.MemberIdentities.TryGetValue(uid, out var g)
            ? SyncshellCommunityView.HumanName(g) : null;
        if (name != null) return name;
        name = SyncshellCommunityView.HumanName(_community?.MemberIdentities?
            .FirstOrDefault(u => string.Equals(u.UID, uid, StringComparison.Ordinal)));
        return name ?? _spheneProfileManager.GetPreferredName(uid);
    }

    /// <summary>
    /// Planner deep-links inside this window: "Edit" opens the composer on
    /// this page, "View" hands off to the hub's community surface (the only
    /// surface that renders the published event), "Preview" reuses the
    /// standalone inert preview window.
    /// </summary>
    private void WirePlannerHooks(SyncshellCommunityView.SyncshellCommunityEdit edit,
        GroupData group)
    {
        edit.OnEditEvent = (ev, occ) =>
        {
            if (occ != null)
            {
                _plannerPage.EventComposer.OpenOccurrence(ev, occ);
            }
            else
            {
                _plannerPage.EventComposer.OpenEdit(ev);
            }
            _activeAdminPage = AdminPage.EventPlanner;
        };
        edit.MemberOptions = () => _pairManager.GroupPairs
            .TryGetValue(GroupFullInfo, out var pairs)
                ? pairs.Select(p => (p.UserData.UID, p.UserData.PreferredName)).ToList()
                : (IReadOnlyList<(string Uid, string Label)>)[];
        edit.MemberLabel = uid =>
            _pairManager.GetPairByUID(uid)?.UserData.PreferredName ?? "Unknown user";
        edit.MentionCandidates = () =>
        {
            var list = new List<UI.Components.MarkdownComposer.MentionCandidate>();
            if (_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var gps))
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
            new OpenEventPreviewMessage(group, _community!, GroupFullInfo, composer));
        edit.OnViewEvent = ev => Mediator.Publish(new OpenSpheneHubMessage(
            SpheneHubPage.Syncshells, group.GID,
            DetailTab: (int)Hub.SpheneHubUi.SyncshellDetailTab.Community,
            CommunitySection: SyncshellCommunitySection.Events, CommunityItemId: ev.Id));
    }

    /// <summary>Keeps queue data warm so the member-page tabs can badge
    /// pending counts before their tab has been opened once.</summary>
    private void PollMemberQueues()
    {
        if (!_applicationsLoaded && _applicationsTask == null)
        {
            _applicationsTask = _apiController.GroupGetMembershipApplications(new GroupDto(GroupFullInfo.Group));
        }
        if (_applicationsTask is { IsCompleted: true })
        {
            _applications = _applicationsTask.IsCompletedSuccessfully ? _applicationsTask.Result : [];
            _applicationsLoaded = true;
            _applicationsTask = null;
        }
        if (_decisionTask is { IsCompleted: true })
        {
            _decisionFailed = !_decisionTask.IsCompletedSuccessfully;
            _decisionTask = null;
            _applicationsLoaded = false;
        }

        if (!_reviewLoaded && _reviewTask == null)
        {
            _reviewTask = _apiController.GroupGetCommunityReviewQueue(new GroupDto(GroupFullInfo.Group));
        }
        if (_reviewTask is { IsCompleted: true })
        {
            _reviewItems = _reviewTask.IsCompletedSuccessfully ? _reviewTask.Result.Items : [];
            _reviewLoaded = true;
            _reviewTask = null;
        }
        if (_reviewDecisionTask is { IsCompleted: true })
        {
            _reviewDecisionFailed = !_reviewDecisionTask.IsCompletedSuccessfully;
            _reviewDecisionTask = null;
            _reviewLoaded = false;
        }

        if (!_reportsLoaded && _reportsTask == null)
        {
            _reportsTask = _apiController.GetModerationQueue(new GroupDto(GroupFullInfo.Group));
        }
        if (_reportsTask is { IsCompleted: true })
        {
            _reports = _reportsTask.IsCompletedSuccessfully ? _reportsTask.Result.Items : [];
            _reportsLoaded = true;
            _reportsTask = null;
        }
        if (_reportDecisionTask is { IsCompleted: true })
        {
            _reportDecisionFailed = !_reportDecisionTask.IsCompletedSuccessfully;
            _reportDecisionTask = null;
            _reportsLoaded = false;
        }
    }

    private enum StaffTab
    {
        Roles,
        Members,
    }

    private StaffTab _staffTab = StaffTab.Roles;
    private readonly SyncshellCommunityView.CommunityPageState _staffPageState = new();
    private SyncshellCommunityView.CommunityManageOps? _staffOps;

    /// <summary>
    /// Roles & rights admin page · the Discord-style role editor plus the
    /// member → job assignment rows, split across two tabs.
    /// </summary>
    private void DrawStaffPage()
    {
        var accent = SpheneUI.Palette.EtherealPurple;
        EnsureCommunityLoaded();

        if (_community == null)
        {
            SpheneUI.Text.Muted("Loading staff data...");
            return;
        }
        if (!CanManageStaffRoles())
        {
            SpheneUI.Text.MutedWrapped(
                "You need the Roles right on this shell to manage jobs and assignments.");
            return;
        }

        var tabs = new List<SpheneUI.Controls.TabItem>
        {
            new(FontAwesomeIcon.UserShield, "Roles"),
            new(FontAwesomeIcon.UserTag, "Staff members"),
        };
        var hit = SpheneUI.Controls.TabBar("admin-staff", tabs, (int)_staffTab, accent);
        if (hit >= 0)
        {
            _staffTab = (StaffTab)hit;
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        _staffOps ??= BuildStaffOps();
        if (_staffTab == StaffTab.Roles)
        {
            SyncshellCommunityView.DrawStaffRolesPanel(_community, _staffOps, _staffPageState, accent);
        }
        else
        {
            SyncshellCommunityView.DrawStaffMembersPanel(_community, _staffOps, StaffMembers(), _staffPageState, accent);
        }
    }

    /// <summary>
    /// Staff ops bound to this group's API surface · mirrors the composer
    /// bundle's role actions with a community refetch after each mutation.
    /// </summary>
    private SyncshellCommunityView.CommunityManageOps BuildStaffOps()
    {
        var group = GroupFullInfo.Group;
        return new SyncshellCommunityView.CommunityManageOps
        {
            CreateStaffRole = name => _ = Task.Run(async () =>
            {
                try
                {
                    var c = _community;
                    var saved = await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(0, group, name, 0xFF85C8FF, null,
                            SyncshellStaffPermission.None,
                            (c?.StaffRoles?.Max(r => (int?)r.SortOrder) ?? 0) + 1))
                        .ConfigureAwait(false);
                    if (saved != null)
                    {
                        await RefreshCommunityAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to create staff role"); }
            }),
            SaveStaffRole = role => _ = Task.Run(async () =>
            {
                try
                {
                    var saved = await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(role.Id, group, role.Name, role.ColorArgb,
                            role.Icon, role.Permissions, role.SortOrder)).ConfigureAwait(false);
                    if (saved != null)
                    {
                        await RefreshCommunityAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save staff role {RoleId}", role.Id); }
            }),
            DeleteStaffRole = role => _ = Task.Run(async () =>
            {
                try
                {
                    await _apiController.GroupDeleteCommunityStaffRole(
                        new SyncshellCommunityItemDeleteDto(group, role.Id)).ConfigureAwait(false);
                    await RefreshCommunityAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to delete staff role {RoleId}", role.Id); }
            }),
            MoveStaffRole = (role, dir) => _ = Task.Run(async () =>
            {
                try
                {
                    var ordered = (_community?.StaffRoles ?? []).OrderBy(r => r.SortOrder).ToList();
                    var idx = ordered.FindIndex(r => r.Id == role.Id);
                    var swap = idx + dir;
                    if (idx < 0 || swap < 0 || swap >= ordered.Count)
                    {
                        return;
                    }
                    var other = ordered[swap];
                    await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(role.Id, group, role.Name, role.ColorArgb,
                            role.Icon, role.Permissions, swap)).ConfigureAwait(false);
                    await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(other.Id, group, other.Name, other.ColorArgb,
                            other.Icon, other.Permissions, idx)).ConfigureAwait(false);
                    await RefreshCommunityAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to reorder staff role {RoleId}", role.Id); }
            }),
            SetStaffAssignment = (uid, roleIds) => _ = Task.Run(async () =>
            {
                try
                {
                    await _apiController.GroupSetCommunityStaffAssignment(
                        new SyncshellStaffAssignmentUpdateDto(group, uid, null,
                            roleIds.ToList())).ConfigureAwait(false);
                    await RefreshCommunityAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to set staff assignment for {Uid}", uid); }
            }),
            GetStaffMembers = StaffMembers,
        };
    }

    /// <summary>
    /// The staff members tab's member list · everyone except the owner, mods
    /// first, sorted like the community page's assignment rows.
    /// </summary>
    private IReadOnlyList<SyncshellCommunityView.CommunityStaffMember> StaffMembers()
        => GroupFullInfo.GroupPairUserInfos
            .Where(kv => !string.Equals(kv.Key, GroupFullInfo.OwnerUID, StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Value.IsModerator())
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new SyncshellCommunityView.CommunityStaffMember(kv.Key,
                _pairManager.GetPairByUID(kv.Key)?.UserData.PreferredName ?? kv.Key,
                kv.Value.IsModerator()))
            .ToList();

    private void DrawMembersTab(Vector4 membersAccent)
    {
        _pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var pairs);
        var memberList = pairs?.ToList() ?? [];

        // Staff roles ride the community aggregate - pull it lazily once so
        // the roster can badge members with their assigned role.
        EnsureCommunityLoaded();

        var onlineCount = 0;
        foreach (var p in memberList)
        {
            if (p.IsOnline) onlineCount++;
        }

        // The owner is part of the roster even when they carry no pair
        // entry (the local user never pairs with themselves) - they get a
        // synthesized first row instead of vanishing from their own shell.
        var ownerInList = memberList.Any(p =>
            string.Equals(p.UserData.UID, GroupFullInfo.OwnerUID, StringComparison.Ordinal));
        var rosterTotal = memberList.Count + (ownerInList ? 0 : 1);
        var rosterOnline = onlineCount
            + (!ownerInList && _isOwner ? 1 : 0);

        SyncshellCommunityView.Widget("members", "Members", FontAwesomeIcon.Users,
            rosterTotal, membersAccent, () =>
        {
            // Command row: presence gauge + count, filter docked right.
            SyncshellRoster.PresenceGauge(rosterOnline, rosterTotal, membersAccent);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain($"{rosterOnline} of {rosterTotal} souls present");

            var filterW = 150f * SpheneUI.Scale;
            SpheneUI.Controls.DockToRightEdge(filterW);
            ImGui.SetNextItemWidth(filterW);
            SpheneUI.Controls.InputTextWithHint("##roster-filter", "Filter souls...", ref _rosterFilter, 64);

            if (_invitePanelOpen)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                DrawInviteStrip();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }

            if (rosterTotal == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserPlus, "No members yet",
                    "Send out an invite to bind souls to this syncshell.");
                return;
            }

        void DrawRosterRow(Pair? p)
        {
            var uid = p?.UserData.UID ?? GroupFullInfo.Owner.UID;
            var isOwner = string.Equals(uid, GroupFullInfo.OwnerUID, StringComparison.Ordinal);
            var isSelf = string.Equals(uid, _apiController.UID, StringComparison.Ordinal);
            GroupFullInfo.GroupPairUserInfos.TryGetValue(uid, out var info);
            var note = p?.GetNote();
            var isOnline = p?.IsOnline ?? isSelf;
            var sub = isOnline
                ? (string.IsNullOrEmpty(p?.PlayerName) ? "online" : p!.PlayerName!)
                : "offline";
            var memberRoles = SyncshellCommunityView.MemberRoles(_community, uid);
            var staffRole = memberRoles.Count > 0 ? memberRoles[0] : null;
            var roleColor = isOwner ? SpheneUI.Palette.SpheneGold
                : staffRole != null ? SyncshellRoster.StaffRoleColor(staffRole.ColorArgb, SpheneUI.Palette.EtherealPurple)
                : info.IsModerator() ? SpheneUI.Palette.EtherealPurple
                : SpheneUI.Colors.TextMuted;

            var targetInfo = info;
            var isMod = info.IsModerator();
            var canModerate = (_isOwner || !isMod) && !isSelf && !isOwner;
            // Staff roles only exist on top of staff membership - the assign
            // action stays hidden until the member is promoted (or the owner,
            // who is staff implicitly). Legacy role holders keep the button so
            // their stale assignment can still be cleared.
            var canAssignRole = CanManageStaffRoles()
                && (isMod || isOwner || memberRoles.Count > 0);
            var actionIcons = new List<FontAwesomeIcon>(5);
            if (_isOwner && !isSelf)
            {
                actionIcons.Add(FontAwesomeIcon.UserShield);
            }
            if (canAssignRole)
            {
                actionIcons.Add(FontAwesomeIcon.IdBadge);
            }
            if (canModerate)
            {
                actionIcons.Add(FontAwesomeIcon.Thumbtack);
                actionIcons.Add(FontAwesomeIcon.Trash);
                actionIcons.Add(FontAwesomeIcon.Ban);
            }

            var mProfile = _spheneProfileManager.GetSpheneProfile(p?.UserData ?? GroupFullInfo.Owner);
            var mAvatar = mProfile.ImageData.Value.Length > 0
                ? SpheneUI.Media.Texture($"roster:{GroupFullInfo.Group.GID}:{uid}", mProfile.ImageData.Value)
                : null;
            // Staff identity rides the avatar pip; every held job gets its
            // own icon chip in the row - names ride the tooltips.
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
            if (!isOwner && info.IsPinned())
            {
                badges.Add(new(FontAwesomeIcon.Thumbtack, SpheneUI.Colors.TextMuted, "Pinned"));
            }
            SyncshellRoster.MemberRow("m-" + uid, isOnline,
                note ?? (p?.UserData.AliasOrUID ?? GroupFullInfo.Owner.AliasOrUID), sub, null, roleColor,
                actionIcons.Count == 0 ? null : () => DrawMemberActions(p, targetInfo, isMod, canModerate, canAssignRole),
                actionIcons, mAvatar, mProfile.ProfileTheme.Value, mProfile.IsNSFW,
                null, badges, actionSlots: 5, staffBadge);
        }

        // ── Roster: hover-revealed action rows, filter-aware, paged ──
        var filter = _rosterFilter.Trim();
        var filtered = memberList
            .Where(p => filter.Length == 0
                || p.UserData.AliasOrUID.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (p.GetNote()?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.PlayerName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(p =>
            {
                if (GroupFullInfo.GroupPairUserInfos.TryGetValue(p.UserData.UID, out var info))
                {
                    if (info.IsModerator()) return 0;
                    if (info.IsPinned()) return 1;
                }
                return 10;
            })
            .ThenBy(p => p.GetNote() ?? p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalPages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)_userListItemsPerPage));
        _userListCurrentPage = Math.Clamp(_userListCurrentPage, 0, totalPages - 1);
        var pageItems = filtered.Skip(_userListCurrentPage * _userListItemsPerPage)
            .Take(_userListItemsPerPage).ToList();

        // Owner leads the roster - drawn first on page one, honor the filter.
        if (!ownerInList && _userListCurrentPage == 0
            && (filter.Length == 0 || GroupFullInfo.Owner.AliasOrUID.Contains(
                filter, StringComparison.OrdinalIgnoreCase)))
        {
            DrawRosterRow(null);
        }

        foreach (var p in pageItems)
        {
            DrawRosterRow(p);
        }

        // Pager sits directly under the list · muted, unobtrusive.
        if (totalPages > 1)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            using (ImRaii.Disabled(_userListCurrentPage <= 0))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.AngleLeft))
                {
                    _userListCurrentPage--;
                }
            }
            ImGui.SameLine();
            SpheneUI.Text.Muted($"{_userListCurrentPage + 1} / {totalPages}");
            ImGui.SameLine();
            using (ImRaii.Disabled(_userListCurrentPage >= totalPages - 1))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.AngleRight))
                {
                    _userListCurrentPage++;
                }
            }
        }
        },
            null,
            (FontAwesomeIcon.Envelope, _invitePanelOpen ? "Close invites" : "Invite members",
                () => _invitePanelOpen = !_invitePanelOpen));
    }

    private void DrawMemberActions(Pair? pair, GroupPairUserInfo? info, bool isMod, bool canModerate, bool canAssignRole)
    {
        var uid = pair?.UserData.UID ?? GroupFullInfo.Owner.UID;
        var userData = pair?.UserData ?? GroupFullInfo.Owner;
        var isSelf = string.Equals(uid, _apiController.UID, StringComparison.Ordinal);
        using var id = ImRaii.PushId("act-" + uid);
        if (_isOwner && !isSelf)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.UserShield))
            {
                var userInfo = info ?? GroupPairUserInfo.None;
                var demoting = userInfo.IsModerator();
                userInfo.SetModerator(!demoting);
                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, userData, userInfo));
                // Staff membership is the base - removing it clears any role
                // assigned on top so no orphaned assignment survives the demote.
                if (demoting)
                {
                    AssignStaffRoles(uid, []);
                }
            }
            SpheneUI.AttachTooltip(isMod ? "Remove staff member status" : "Make staff member");
            ImGui.SameLine(0f, 4f * SpheneUI.Scale);
        }

        if (canAssignRole)
        {
            var flyoutId = $"role-assign:{uid}";
            if (SpheneUI.Icons.Button(FontAwesomeIcon.IdBadge))
            {
                SpheneUI.Popups.ToggleFlyout(flyoutId);
            }
            SpheneUI.AttachTooltip("Assign staff role");
            if (SpheneUI.Popups.BeginFlyout(flyoutId))
            {
                DrawRoleAssignFlyout(uid);
                SpheneUI.Popups.EndFlyout();
            }
            ImGui.SameLine(0f, 4f * SpheneUI.Scale);
        }

        if (canModerate && pair != null)
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Thumbtack))
            {
                var userInfo = info ?? GroupPairUserInfo.None;
                userInfo.SetPinned(!userInfo.IsPinned());
                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, userData, userInfo));
            }
            SpheneUI.AttachTooltip(info is { } i && i.IsPinned() ? "Unpin" : "Pin");
            ImGui.SameLine(0f, 4f * SpheneUI.Scale);

            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                {
                    _ = _apiController.GroupRemoveUser(new GroupPairDto(GroupFullInfo.Group, userData));
                }
            }
            SpheneUI.AttachTooltip("Remove from syncshell" + SpheneUI.TooltipSeparator + "Hold CTRL to enable");
            ImGui.SameLine(0f, 4f * SpheneUI.Scale);

            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Ban))
                {
                    Mediator.Publish(new OpenBanUserPopupMessage(pair, GroupFullInfo));
                }
            }
            SpheneUI.AttachTooltip("Ban from syncshell" + SpheneUI.TooltipSeparator + "Hold CTRL to enable");
        }
    }

    private void DrawRoleAssignFlyout(string uid)
    {
        if (_community == null)
        {
            SpheneUI.Text.Muted("Loading roles...");
            return;
        }

        var roles = _community.StaffRoles;
        var assignments = _community.StaffAssignments ?? [];

        // Same pending-override contract as the staff members panel - toggles
        // computed off a stale snapshot would silently drop earlier picks.
        if (!ReferenceEquals(_staffPageState.StaffAssignmentSource, _community.StaffAssignments))
        {
            _staffPageState.PendingStaffRoles.Clear();
            _staffPageState.StaffAssignmentSource = _community.StaffAssignments;
        }
        var effectiveIds = _staffPageState.PendingStaffRoles.TryGetValue(uid, out var pending)
            ? pending
            : assignments.Where(a => string.Equals(a.UserUID, uid, StringComparison.Ordinal))
                .Select(a => a.RoleId).ToList();

        if (roles == null || roles.Count == 0)
        {
            SpheneUI.Text.Muted("No staff roles yet.");
            SpheneUI.Text.Muted("Create them on the community page's staff area.");
            return;
        }

        if (effectiveIds.Count > 0 && SpheneUI.Controls.Selectable("Clear roles", false))
        {
            _staffPageState.PendingStaffRoles[uid] = [];
            AssignStaffRoles(uid, []);
        }

        var ordered = roles.OrderBy(r => r.SortOrder).ToList();
        var scale = SpheneUI.Scale;
        foreach (var role in ordered)
        {
            var selected = effectiveIds.Contains(role.Id);
            if (SpheneUI.Controls.Selectable($"      {role.Name}##role:{role.Id}", selected))
            {
                var next = effectiveIds.Where(id => id != role.Id).ToList();
                if (!selected)
                {
                    next.Add(role.Id);
                }
                _staffPageState.PendingStaffRoles[uid] = next;
                AssignStaffRoles(uid, next);
            }
            var dot = SyncshellRoster.StaffRoleColor(role.ColorArgb, SpheneUI.Palette.EtherealPurple);
            var rectMin = ImGui.GetItemRectMin();
            var rectMax = ImGui.GetItemRectMax();
            var draw = ImGui.GetWindowDrawList();
            using (SpheneUI.Icons.PushFont())
            {
                var glyphH = ImGui.GetTextLineHeight();
                draw.AddText(
                    new Vector2(rectMin.X + 10f * scale,
                        rectMin.Y + (rectMax.Y - rectMin.Y - glyphH) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(dot),
                    StaffRoleIcons.Parse(role.Icon).ToIconString());
            }
        }
    }

    private void AssignStaffRoles(string uid, IReadOnlyList<long> roleIds)
    {
        var group = GroupFullInfo.Group;
        _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupSetCommunityStaffAssignment(
                    new SyncshellStaffAssignmentUpdateDto(group, uid, null,
                        roleIds.ToList())).ConfigureAwait(false);
                _community = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to assign staff roles for {Uid} in {Group}", uid, group.GID);
            }
        });
    }
    private void DrawApplicationsPanel(Vector4 accent)
    {
        // lazy-load once per shell visit; decisions and the refresh button reload it
        if (!_applicationsLoaded && _applicationsTask == null)
        {
            _applicationsTask = _apiController.GroupGetMembershipApplications(new GroupDto(GroupFullInfo.Group));
        }
        if (_applicationsTask is { IsCompleted: true })
        {
            _applications = _applicationsTask.IsCompletedSuccessfully ? _applicationsTask.Result : [];
            _applicationsLoaded = true;
            _applicationsTask = null;
        }
        if (_decisionTask is { IsCompleted: true })
        {
            _decisionFailed = !_decisionTask.IsCompletedSuccessfully;
            _decisionTask = null;
            _applicationsLoaded = false; // reload after every decision
        }

        var pending = _applications?.Count(a => a.Status == MembershipApplicationStatus.Pending) ?? 0;
        SpheneUI.Layout.SectionCard(() =>
        {
            var refreshW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Redo, "Refresh");
            SpheneUI.Controls.DockToRightEdge(refreshW);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Redo, "Refresh")
                && _applicationsTask == null)
            {
                _applicationsTask = _apiController.GroupGetMembershipApplications(new GroupDto(GroupFullInfo.Group));
            }

            if (_applications == null)
            {
                SpheneUI.Text.Muted("Loading applications...");
                return;
            }
            if (_decisionFailed)
            {
                SpheneUI.Text.Colored("Decision could not be applied.", ImGuiColors.DalamudYellow);
            }
            if (_applications.Count == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserPlus, "No join requests", "New applications appear here when the syncshell requires approval.");
                return;
            }

            foreach (var app in _applications)
            {
                DrawApplicationRow(app, accent);
            }
        }, $"Join Requests{(pending > 0 ? $" · {pending} pending" : string.Empty)}", accent, icon: FontAwesomeIcon.UserPlus);
    }

    /// <summary>
    /// Pending community-content submissions (news + gallery) with
    /// approve/reject and optional review notes - visible to owner and
    /// moderators; the server enforces the narrower staff bits itself.
    /// </summary>
    private void DrawReviewQueuePanel(Vector4 accent)
    {
        if (!_reviewLoaded && _reviewTask == null)
        {
            _reviewTask = _apiController.GroupGetCommunityReviewQueue(new GroupDto(GroupFullInfo.Group));
        }
        if (_reviewTask is { IsCompleted: true })
        {
            _reviewItems = _reviewTask.IsCompletedSuccessfully ? _reviewTask.Result.Items : [];
            _reviewLoaded = true;
            _reviewTask = null;
        }
        if (_reviewDecisionTask is { IsCompleted: true })
        {
            _reviewDecisionFailed = !_reviewDecisionTask.IsCompletedSuccessfully;
            _reviewDecisionTask = null;
            _reviewLoaded = false; // reload after every decision
        }

        var pending = _reviewItems?.Count ?? 0;
        SpheneUI.Layout.SectionCard(() =>
        {
            var refreshW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Redo, "Refresh");
            SpheneUI.Controls.DockToRightEdge(refreshW);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Redo, "Refresh")
                && _reviewTask == null)
            {
                _reviewTask = _apiController.GroupGetCommunityReviewQueue(new GroupDto(GroupFullInfo.Group));
            }

            if (_reviewItems == null)
            {
                SpheneUI.Text.Muted("Loading review queue...");
                return;
            }
            if (_reviewDecisionFailed)
            {
                SpheneUI.Text.Colored("Decision could not be applied.", ImGuiColors.DalamudYellow);
            }
            if (_reviewItems.Count == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.CheckCircle, "Queue is clear",
                    "Member submissions appear here when the syncshell requires approval.");
                return;
            }

            SpheneUI.Controls.InputTextWithHint("##rvnote-" + GroupFullInfo.Group.GID,
                "Review note (optional, applies to the next decision)...", ref _reviewNote, 300,
                preferredWidth: 280f * SpheneUI.Scale);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            foreach (var item in _reviewItems)
            {
                DrawReviewItem(item, accent);
            }
        }, $"Review Queue{(pending > 0 ? $" · {pending} pending" : string.Empty)}", accent, icon: FontAwesomeIcon.ClipboardCheck);
    }

    private void DrawReviewItem(CommunityReviewItemDto item, Vector4 accent)
    {
        SpheneUI.Layout.Surface(() =>
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(item.ContentKind switch
            {
                "gallery" => FontAwesomeIcon.Image,
                "event" => FontAwesomeIcon.Calendar,
                _ => FontAwesomeIcon.Newspaper,
            }, accent);
            ImGui.SameLine();
            SpheneUI.Text.Colored(item.Title, SpheneUI.Colors.TextPrimary);
            if (item.Author != null)
            {
                SpheneUI.Text.Muted("by " + item.Author.AliasOrUID + " · " +
                    SpheneUI.Text.FormatShortDate(item.SubmittedAt));
            }
            if (!string.IsNullOrWhiteSpace(item.Snippet))
            {
                SpheneUI.Text.MutedWrapped(item.Snippet);
            }

            var approveW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, "Approve")
                + SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Times, "Decline")
                + SpheneUI.Spacing.X4;
            SpheneUI.Controls.DockToRightEdge(approveW);
            var decision = _reviewDecisionTask != null;
            using (ImRaii.Disabled(decision))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Approve"))
                {
                    _reviewDecisionTask = DecideAsync(item, true);
                }
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Decline"))
                {
                    _reviewDecisionTask = DecideAsync(item, false);
                }
            }
        });
    }

    /// <summary>
    /// Open content reports for this shell - comments/posts flagged by
    /// members; resolvable by ModerateContent holders. Reporter identity is
    /// shown to moderators only; blog reports never land here (platform queue).
    /// </summary>
    private void DrawReportsPanel(Vector4 accent)
    {
        if (!_reportsLoaded && _reportsTask == null)
        {
            _reportsTask = _apiController.GetModerationQueue(new GroupDto(GroupFullInfo.Group));
        }
        if (_reportsTask is { IsCompleted: true })
        {
            _reports = _reportsTask.IsCompletedSuccessfully ? _reportsTask.Result.Items : [];
            _reportsLoaded = true;
            _reportsTask = null;
        }
        if (_reportDecisionTask is { IsCompleted: true })
        {
            _reportDecisionFailed = !_reportDecisionTask.IsCompletedSuccessfully;
            _reportDecisionTask = null;
            _reportsLoaded = false;
        }

        var pending = _reports?.Count ?? 0;
        SpheneUI.Layout.SectionCard(() =>
        {
            var refreshW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Redo, "Refresh");
            SpheneUI.Controls.DockToRightEdge(refreshW);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Redo, "Refresh") && _reportsTask == null)
            {
                _reportsTask = _apiController.GetModerationQueue(new GroupDto(GroupFullInfo.Group));
            }

            if (_reports == null)
            {
                SpheneUI.Text.Muted("Loading reports...");
                return;
            }
            if (_reportDecisionFailed)
            {
                SpheneUI.Text.Colored("Decision could not be applied.", ImGuiColors.DalamudYellow);
            }
            if (_reports.Count == 0)
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.FlagCheckered, "No open reports",
                    "Content reported by members appears here.");
                return;
            }

            foreach (var report in _reports)
            {
                DrawReportRow(report, accent);
            }
        }, $"Reports{(pending > 0 ? $" · {pending} open" : string.Empty)}", accent, icon: FontAwesomeIcon.Flag);
    }

    private void DrawReportRow(CommunityReportDto report, Vector4 accent)
    {
        SpheneUI.Layout.Surface(() =>
        {
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(report.TargetKind switch
            {
                ReportTargetKind.Comment => FontAwesomeIcon.Comment,
                ReportTargetKind.Event => FontAwesomeIcon.Calendar,
                _ => FontAwesomeIcon.Newspaper,
            }, accent);
            ImGui.SameLine();
            SpheneUI.Text.Colored($"{report.TargetKind} · {report.Reason}", SpheneUI.Colors.TextPrimary);
            if (report.Reporter != null)
            {
                SpheneUI.Text.Muted("by " + report.Reporter.AliasOrUID + " · " +
                    SpheneUI.Text.FormatShortDate(report.CreatedAt));
            }
            if (!string.IsNullOrWhiteSpace(report.TargetSnippet))
            {
                SpheneUI.Text.MutedWrapped(report.TargetSnippet);
            }
            if (!string.IsNullOrWhiteSpace(report.Details))
            {
                SpheneUI.Text.MutedWrapped(report.Details);
            }

            var w = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, "Resolve")
                + SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Times, "Dismiss")
                + SpheneUI.Spacing.X4;
            SpheneUI.Controls.DockToRightEdge(w);
            var deciding = _reportDecisionTask != null;
            using (ImRaii.Disabled(deciding))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Resolve"))
                {
                    _reportDecisionTask = _apiController.ResolveReport(
                        new ReportDecisionDto(report.Id, true, null, GroupFullInfo.Group));
                }
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Dismiss"))
                {
                    _reportDecisionTask = _apiController.ResolveReport(
                        new ReportDecisionDto(report.Id, false, null, GroupFullInfo.Group));
                }
            }
        });
    }

    private Task<bool> DecideAsync(CommunityReviewItemDto item, bool approve)
    {
        var dto = new CommunityReviewDecisionDto(GroupFullInfo.Group, item.ContentKind,
            item.ContentId, approve, string.IsNullOrWhiteSpace(_reviewNote) ? null : _reviewNote);
        _reviewNote = string.Empty;
        return item.ContentKind switch
        {
            "gallery" => _apiController.GroupDecideCommunityGalleryImage(dto),
            "event" => _apiController.GroupDecideCommunityEvent(dto),
            _ => _apiController.GroupDecideCommunityNews(dto),
        };
    }

    private void DrawApplicationRow(MembershipApplicationDto app, Vector4 accent)
    {
        using var id = ImRaii.PushId("mapp-" + app.Id);
        var name = app.Applicant.AliasOrUID;
        var when = SpheneUI.Text.FormatDateTime(app.CreatedAt);

        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain(name);
        ImGui.SameLine();
        SpheneUI.Text.Muted($"· {when}");
        ImGui.SameLine();
        var (statusLabel, statusColor) = app.Status switch
        {
            MembershipApplicationStatus.Pending => ("pending", accent),
            MembershipApplicationStatus.Approved => ("approved", SpheneUI.Palette.NetworkActive),
            MembershipApplicationStatus.Rejected => ("rejected", SpheneUI.Palette.NetworkDisconnected),
            _ => ("withdrawn", SpheneUI.Palette.TextSecondary),
        };
        SpheneUI.Text.Colored($"· {statusLabel}", statusColor);

        if (app.Status == MembershipApplicationStatus.Pending)
        {
            if (!string.IsNullOrWhiteSpace(app.Message))
            {
                SpheneUI.Text.MutedWrapped($"“{app.Message}”", ImGui.GetCursorPosX() + 24f * SpheneUI.Scale);
            }

            var btnW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, "Approve")
                + SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Times, "Decline")
                + SpheneUI.Spacing.X8.Scaled;
            SpheneUI.Controls.DockToRightEdge(btnW);
            using (ImRaii.Disabled(_decisionTask is { IsCompleted: false }))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Approve"))
                {
                    _decisionTask = _apiController.GroupDecideMembershipApplication(
                        new GroupMembershipDecisionDto(GroupFullInfo.Group, app.Id, true, null));
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Decline"))
                {
                    _decisionTask = _apiController.GroupDecideMembershipApplication(
                        new GroupMembershipDecisionDto(GroupFullInfo.Group, app.Id, false, null));
                }
            }
        }
        else if (app.ReviewedBy != null)
        {
            ImGui.SameLine();
            SpheneUI.Text.Muted($"by {app.ReviewedBy.AliasOrUID}");
        }
    }

    private void DrawHousekeepingPanel(Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Broom, "Clear syncshell"))
            {
                _ = _apiController.GroupClear(new(GroupFullInfo.Group));
            }
        }
        SpheneUI.AttachTooltip("Removes all non-pinned, non-staff members."
            + SpheneUI.TooltipSeparator + "Hold CTRL to enable");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Muted("Inactive for");
        ImGui.SameLine();
        SpheneUI.Controls.Combo("##prunedays", [7, 14, 30, 90], count => count + "d", selected =>
        {
            _pruneDays = selected;
            _pruneTestTask = null;
            _pruneTask = null;
        }, _pruneDays);
        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Search, "Scan"))
        {
            _pruneTestTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: false);
            _pruneTask = null;
        }
        SpheneUI.AttachTooltip($"Find members inactive for {_pruneDays} days · you review before anything is removed.");

        if (_pruneTestTask is { IsCompleted: false })
        {
            SpheneUI.Text.Muted("Scanning...");
        }
        else if (_pruneTestTask != null)
        {
            SpheneUI.Text.Wrapped($"{_pruneTestTask.Result} inactive soul(s) found.");
            if (_pruneTestTask.Result > 0
                && ImRaii.Disabled(!UiSharedService.CtrlPressed()) is { } d)
            {
                using (d)
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Broom, $"Prune {_pruneTestTask.Result} inactive"))
                    {
                        _pruneTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: true);
                        _pruneTestTask = null;
                    }
                }
                SpheneUI.AttachTooltip("Hold CTRL to enable");
            }
            _pruneTestTask = null;
        }
        if (_pruneTask is { IsCompleted: false })
        {
            SpheneUI.Text.Muted("Pruning...");
        }
        else if (_pruneTask is { IsCompletedSuccessfully: true })
        {
            SpheneUI.Text.Wrapped($"Pruned {_pruneTask.Result} inactive member(s).");
            _pruneTask = null;
        }
        }, "Housekeeping", accent, icon: FontAwesomeIcon.Broom);
    }

    private void DrawBanishedPanel(Vector4 accent)
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            using (ImRaii.Disabled(_bannedUsersTask is { IsCompleted: false }))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Sync, _bannedUsersTask == null && _bannedUsers.Count == 0 ? "Load ban list" : "Refresh"))
            {
                _bannedUsersTask = _apiController.GroupGetBannedUsers(new GroupDto(GroupFullInfo.Group));
            }
        }

        if (_bannedUsersTask is { IsCompleted: false })
        {
            SpheneUI.Text.Muted("Refreshing...");
        }
        else if (_bannedUsersTask != null)
        {
            if (_bannedUsersTask.IsCompletedSuccessfully)
            {
                _bannedUsers = _bannedUsersTask.Result;
            }
            else
            {
                _logger.LogWarning("Failed to refresh banned users for group {GID}", GroupFullInfo.Group.GID);
            }
            _bannedUsersTask = null;
        }

        if (_bannedUsers.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.UserShield, "No bans",
                "Banished members would be listed here.");
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        foreach (var banned in _bannedUsers.ToList())
        {
            var target = banned;
            SyncshellRoster.MemberRow("ban-" + banned.UID, false,
                banned.UserAliasOrUID,
                $"{banned.BannedOn.ToLocalTime():d} · {(string.IsNullOrWhiteSpace(banned.Reason) ? "no reason" : banned.Reason)}",
                "Banished", SpheneUI.Colors.Danger,
                () =>
                {
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.UserCheck))
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await _apiController.GroupUnbanUser(target).ConfigureAwait(false); }
                            catch (Exception ex) { _logger.LogError(ex, "Failed to unban user {UID}", target.UID); }
                        });
                        _bannedUsers.RemoveAll(b => string.Equals(b.UID, target.UID, StringComparison.Ordinal));
                    }
                    SpheneUI.AttachTooltip("Lift the ban");
                },
                new[] { FontAwesomeIcon.UserCheck },
                actionSlots: 1);
        }
        }, "Banished souls", accent, icon: FontAwesomeIcon.UserSlash);
    }


    private void DrawAreaBindingPage()
    {
        // Command row · one shared line like every other command strip: the
        // bound-area readout sits left, Apply + Refresh dock right on the same
        // row instead of floating on a line of their own above the status.
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var actionWidth = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Save, "Apply")
            + SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Sync, "Refresh") + spacing;

        var boundCount = GetUniqueBoundAreaCount();
        ImGui.AlignTextToFramePadding();
        SpheneUI.Icons.Icon(FontAwesomeIcon.MapMarkedAlt,
            boundCount > 0 ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted);
        ImGui.SameLine();
        var statusText = boundCount > 0
            ? $"{boundCount} bound area(s) live"
            : "No bound areas · enable a property below and pick its trigger zones.";
        // The docked cluster claims the row's tail · ellipsize into the rest
        // so a narrow page never lets the status run under the buttons.
        var statusMaxW = MathF.Max(60f * SpheneUI.Scale,
            SpheneUI.Controls.BoundedAvailX()
            - SpheneUI.Metrics.TrailingIconReserve - actionWidth - spacing);
        var statusShown = SpheneUI.Text.Ellipsize(statusText, statusMaxW);
        SpheneUI.Text.Plain(statusShown);
        if (!string.Equals(statusShown, statusText, StringComparison.Ordinal))
        {
            SpheneUI.AttachTooltip(statusText);
        }

        // Apply stays visible and reachable on this row instead of appearing
        // below the scroll fold only while changes are fresh.
        SpheneUI.Controls.DockToRightEdge(actionWidth);
        if (SpheneUI.Controls.PrimaryButton("Apply", FontAwesomeIcon.Save,
                enabled: _areaSettingsDirty))
        {
            _areaSettingsDirty = false;
            _ = UpdateAreaBindingSettings();
        }
        SpheneUI.AttachTooltip("Apply settings to the server");
        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Sync, "Refresh"))
        {
            _ = LoadCurrentAreaBinding();
        }
        SpheneUI.AttachTooltip("Reload the current binding.");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.Callout(
            "Area bindings moved to the Housing page - pick a property there and assign syncshells with their bound areas and settings.",
            FontAwesomeIcon.Home, SpheneUI.Palette.CrystalBlue, title: "Moved to Housing");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        if (SpheneUI.Controls.PrimaryButton("Open Housing"))
        {
            Mediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Housing));
        }
    }

    private readonly SemaphoreSlim _areaBindingSaveLock = new(1, 1);

    private async Task UpdateAreaBindingSettings()
    {
        // Server broadcasts echo our own writes back - without a lock the
        // second call re-runs and spuriously fails right after the success.
        await _areaBindingSaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            NormalizeBoundAreaMapIdsInPlace();
            PruneUnsupportedBoundAreasInPlace();
            NormalizeBoundAreasInPlace();

            if (_boundAreas.Count == 0)
            {
                if (_areaBindingEnabled)
                {
                    await RemoveAreaBinding().ConfigureAwait(false);
                }
                return;
            }

            var settings = new AreaBoundSettings
            {
                AutoBroadcastEnabled = _autoBroadcastEnabled,
                RequireOwnerPresence = _requireOwnerPresence,
                MaxAutoJoinUsers = _maxAutoJoinUsers,
                NotifyOnUserEnter = _notifyOnUserEnter,
                NotifyOnUserLeave = _notifyOnUserLeave,
                CustomJoinMessage = string.IsNullOrWhiteSpace(_customJoinMessage) ? null : _customJoinMessage,
                JoinRules = string.IsNullOrWhiteSpace(_joinRules) ? null : _joinRules,
                RulesVersion = _rulesVersion,
                RequireRulesAcceptance = _requireRulesAcceptance
            };
            
            var dto = new AreaBoundSyncshellDto(GroupFullInfo.Group, _boundAreas)
            {
                Settings = settings
            };
            await _apiController.GroupSetAreaBinding(dto).ConfigureAwait(false);
            
            _logger.LogDebug("Successfully updated area binding settings for syncshell {GID}", GroupFullInfo.Group.GID);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update area binding settings for syncshell {GID}", GroupFullInfo.Group.GID);
        }
        finally
        {
            _areaBindingSaveLock.Release();
        }
    }
    
    private async Task RemoveAreaBinding()
    {
        try
        {
            await _apiController.GroupRemoveAreaBinding(new(GroupFullInfo.Group)).ConfigureAwait(false);
            _areaBindingEnabled = false;
            _boundAreas.Clear();
            
            // Reset UI variables to defaults
            _autoBroadcastEnabled = true;
            _requireOwnerPresence = false;
            _maxAutoJoinUsers = 50;
            _notifyOnUserEnter = true;
            _notifyOnUserLeave = true;
            _customJoinMessage = string.Empty;
            
            // Reset rules settings to defaults
            _joinRules = string.Empty;
            _rulesVersion = 1;
            _requireRulesAcceptance = false;
            _rulesList.Clear();
            _areaSettingsDirty = false;
            
            _logger.LogDebug("Successfully removed area binding for syncshell {GID}", GroupFullInfo.Group.GID);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove area binding for syncshell {GID}", GroupFullInfo.Group.GID);
        }
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
    
    private void ParseRulesIntoList()
    {
        _rulesList.Clear();
        if (string.IsNullOrWhiteSpace(_joinRules))
        {
            return;
        }
        
        var lines = _joinRules.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
                continue;
                
            // Remove existing numbering if present (e.g., "1. Rule content" -> "Rule content")
            var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"^\d+\.\s*(.*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(2000));
            if (match.Success)
            {
                _rulesList.Add(match.Groups[1].Value);
            }
            else
            {
                _rulesList.Add(trimmedLine);
            }
        }
    }
    
    private int GetUniqueBoundAreaCount()
    {
        if (_boundAreas.Count <= 1)
        {
            return _boundAreas.Count;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in _boundAreas)
        {
            seen.Add(GetBoundAreaKey(b));
        }
        return seen.Count;
    }

    private void NormalizeBoundAreasInPlace()
    {
        if (_boundAreas.Count <= 1)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<AreaBoundLocationDto>(_boundAreas.Count);
        foreach (var b in _boundAreas)
        {
            if (seen.Add(GetBoundAreaKey(b)))
            {
                normalized.Add(b);
            }
        }

        _boundAreas = normalized;
    }

    private static string GetBoundAreaKey(AreaBoundLocationDto b)
    {
        var loc = b.Location;
        return $"{b.MatchingMode}|{loc.ServerId}|{loc.TerritoryId}|{loc.WardId}|{loc.HouseId}|{loc.RoomId}|{loc.MapId}|{loc.IsIndoor}";
    }

    // diagnostics helpers removed

    private void NormalizeBoundAreaMapIdsInPlace()
    {
        if (_boundAreas.Count == 0)
        {
            return;
        }

        var interiorMaps = _dalamudUtilService.GetHousingInteriorFloorMapIdsByTerritoryAndSize();

        for (var i = 0; i < _boundAreas.Count; i++)
        {
            var b = _boundAreas[i];
            if (b.MatchingMode != AreaMatchingMode.HousingPlotIndoor) continue;
            var loc = b.Location;
            if (loc.RoomId != 0) continue; // rooms/apartments use MapId 0
            if (loc.WardId <= 0 || loc.HouseId is < 1 or > 60 || loc.TerritoryId == 0) continue;

            if (loc.MapId == 0
                || loc.MapId == HousingInteriorRelativeMapIds.Ground
                || loc.MapId == HousingInteriorRelativeMapIds.Basement
                || loc.MapId == HousingInteriorRelativeMapIds.Second)
            {
                continue;
            }

            var sizeLabel = _dalamudUtilService.GetHousingPlotSizeLabel(loc);
            var sizeIndex = sizeLabel switch
            {
                "Small" => 0,
                "Medium" => 1,
                "Large" => 2,
                _ => -1
            };
            if (sizeIndex < 0) continue;

            var key = $"{loc.TerritoryId}:{sizeIndex}";
            if (!interiorMaps.TryGetValue(key, out var maps)) continue;

            if (maps.GroundMapId == loc.MapId)
            {
                loc.MapId = HousingInteriorRelativeMapIds.Ground;
            }
            else if (maps.BasementMapId == loc.MapId)
            {
                loc.MapId = HousingInteriorRelativeMapIds.Basement;
            }
            else if (maps.SecondFloorMapId == loc.MapId)
            {
                loc.MapId = HousingInteriorRelativeMapIds.Second;
            }
            else
            {
                loc.MapId = 0; // fallback to entry/any interior
            }
            _boundAreas[i] = new AreaBoundLocationDto
            {
                Id = b.Id,
                MatchingMode = b.MatchingMode,
                LocationName = b.LocationName,
                CreatedAt = b.CreatedAt,
                Location = loc
            };
        }
    }

    private void PruneUnsupportedBoundAreasInPlace()
    {
        if (_boundAreas.Count == 0)
        {
            return;
        }

        var pruned = new List<AreaBoundLocationDto>(_boundAreas.Count);
        foreach (var b in _boundAreas)
        {
            if (b.MatchingMode != AreaMatchingMode.HousingPlotOutdoor && b.MatchingMode != AreaMatchingMode.HousingPlotIndoor)
            {
                continue;
            }

            var loc = b.Location;

            if (b.MatchingMode == AreaMatchingMode.HousingPlotOutdoor)
            {
                loc.IsIndoor = false;
                loc.MapId = 0;
                loc.RoomId = 0;
                pruned.Add(b with { Location = loc });
                continue;
            }

            loc.IsIndoor = true;

            if (loc.RoomId > 0)
            {
                loc.MapId = 0;
                pruned.Add(b with { Location = loc });
                continue;
            }

            if (loc.MapId != 0
                && loc.MapId != HousingInteriorRelativeMapIds.Ground
                && loc.MapId != HousingInteriorRelativeMapIds.Basement
                && loc.MapId != HousingInteriorRelativeMapIds.Second)
            {
                continue;
            }

            pruned.Add(b with { Location = loc });
        }

        _boundAreas = pruned;
    }

    // diagnostics helpers removed
    
    private void InitializeUiStateFromServer()
    {
        // Clear deleted property keys when refreshing from server (server is source of truth)
        _deletedPropertyKeys.Clear();
        
        // Only initialize UI state for properties that don't already have UI overrides
        var serverProperties = _housingOwnershipService.GetVerifiedOwnedProperties();
        foreach (var property in serverProperties)
        {
            var key = $"{property.Location.TerritoryId}_{property.Location.WardId}_{property.Location.HouseId}_{property.Location.RoomId}";
            if (!_uiPropertyStates.ContainsKey(key))
            {
                _uiPropertyStates[key] = (property.AllowOutdoor, property.AllowIndoor);
            }
        }
    }
    
}
