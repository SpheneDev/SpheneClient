using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;

namespace Sphene.UI.Components.Popup;

public class SyncshellAdminUI : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly bool _isModerator = false;
    private readonly bool _isOwner = false;
    private readonly List<string> _oneTimeInvites = [];
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private List<BannedGroupUserDto> _bannedUsers = [];
    private int _multiInvites;
    private string _newPassword;
    private bool _pwChangeSuccess;
    private Task<int>? _pruneTestTask;
    private Task<int>? _pruneTask;
    private int _pruneDays = 14;
    
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
    // Sticky dirty flag for the area-binding settings cards — a per-frame
    // local only lives as long as the click frame, which made the old Apply
    // button unreachable the very next frame.
    private bool _areaSettingsDirty;
    
    // Multi-location support
    private List<AreaBoundLocationDto> _boundAreas = new();
    
    // Alias setting fields
    private string _newAlias = string.Empty;
    private bool _aliasChangeSuccess = true;
    
    // Welcome page fields
    
    private string _welcomeText = string.Empty;
    private string _welcomeImageBase64 = string.Empty;
    private string WelcomeImageKey => $"welcome:{GroupFullInfo.Group.GID}";
    private IDalamudTextureWrap? WelcomeImageWrap => string.IsNullOrEmpty(_welcomeImageBase64)
        ? null
        : SpheneUI.Media.Texture(WelcomeImageKey, () => Convert.FromBase64String(_welcomeImageBase64));
    
    // Text selection tracking
    
    
    private string _imageFileName = string.Empty;
    private string _imageContentType = string.Empty;
    private long _imageSize = 0;
    private bool _welcomePageEnabled = false;
    private bool _showOnJoin = true;
    private bool _showOnAreaBoundJoin = true;
    private bool _isLoadingWelcomePage = false;
    private bool _welcomePageSaveSuccess = true;
    
    
    // User list pagination fields
    private int _userListCurrentPage = 0;
    private const int _userListItemsPerPage = 20;
    
    private readonly FileDialogManager _fileDialogManager;
    
    private readonly HousingOwnershipService _housingOwnershipService;
    private List<HousingOwnershipService.DetectedHousingProperty> _detectedHousingProperties = [];
    private Task? _refreshDetectedHousingPropertiesTask;
    
    // UI-only state for property preferences - immediate UI updates
    
    // Active sidebar page
    private AdminPage _activeAdminPage = AdminPage.Home;
    private bool _embedded;
    private bool _areaBindingTabWasOpen = false;
    private readonly Dictionary<string, (bool AllowOutdoor, bool AllowIndoor)> _uiPropertyStates = new(StringComparer.Ordinal);
    
    // Track properties that have been deleted in the UI (to hide them until server confirms)
    private readonly HashSet<string> _deletedPropertyKeys = new(StringComparer.Ordinal);
    
    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, SpheneMediator mediator, ApiController apiController,
        PairManager pairManager, DalamudUtilService dalamudUtilService, GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService, FileDialogManager fileDialogManager, HousingOwnershipService housingOwnershipService)
        : base(logger, mediator, "Syncshell Admin Panel (" + groupFullInfo.GroupAliasOrGID + ")", performanceCollectorService)
    {
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        _housingOwnershipService = housingOwnershipService;
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
        
        // Load current welcome page if exists
        _ = LoadCurrentWelcomePage();

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

    private enum AdminPage
    {
        Home,
        Invites,
        UserManagement,
        WelcomePage,
        AreaBinding,
    }

    private void DrawAdminContent()
    {
        if (_activeAdminPage == AdminPage.AreaBinding && !_isOwner)
        {
            _activeAdminPage = AdminPage.Home;
        }

        var theme = SpheneTheme.Current;
        var accentHome = theme.AccentBlue;
        var accentMembers = SpheneUI.Palette.NetworkActive;
        var accentShell = SpheneUI.Palette.EtherealPurple;
        var accentArea = new Vector4(0.93f, 0.68f, 0.38f, 1f);

        var navItems = _isOwner
            ? new (FontAwesomeIcon, string)[]
            {
                (FontAwesomeIcon.Home, "Home"),
                (FontAwesomeIcon.Envelope, "Invites"),
                (FontAwesomeIcon.Users, "User Management"),
                (FontAwesomeIcon.Book, "Welcome Page"),
                (FontAwesomeIcon.MapMarkedAlt, "Area Binding"),
            }
            : new (FontAwesomeIcon, string)[]
            {
                (FontAwesomeIcon.Home, "Home"),
                (FontAwesomeIcon.Envelope, "Invites"),
                (FontAwesomeIcon.Users, "User Management"),
                (FontAwesomeIcon.Book, "Welcome Page"),
            };
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

        AdminNavItem("Home", FontAwesomeIcon.Home, AdminPage.Home, accentHome);
        AdminNavItem("Invites", FontAwesomeIcon.Envelope, AdminPage.Invites, accentMembers);
        AdminNavItem("User Management", FontAwesomeIcon.Users, AdminPage.UserManagement, accentMembers);
        SpheneUI.Layout.NavRailDivider(accentMembers, accentShell);
        AdminNavItem("Welcome Page", FontAwesomeIcon.Book, AdminPage.WelcomePage, accentShell);
        if (_isOwner)
        {
            AdminNavItem("Area Binding", FontAwesomeIcon.MapMarkedAlt, AdminPage.AreaBinding, accentArea);
        }
        SpheneUI.Layout.EndNavRail();

        var onAreaBinding = _activeAdminPage == AdminPage.AreaBinding;
        if (onAreaBinding && !_areaBindingTabWasOpen)
        {
            RefreshDetectedHousingProperties("nav select");
            _ = LoadCurrentAreaBinding();
        }
        _areaBindingTabWasOpen = onAreaBinding;

        if (SpheneUI.Layout.BeginNavContent("syncshell-admin"))
        {
            switch (_activeAdminPage)
            {
                case AdminPage.Home:
                    DrawHomePage();
                    break;
                case AdminPage.Invites:
                    DrawInvitesPage();
                    break;
                case AdminPage.UserManagement:
                    DrawUserManagementPage();
                    break;
                case AdminPage.WelcomePage:
                    DrawWelcomePageControls();
                    break;
                case AdminPage.AreaBinding:
                    DrawAreaBindingPage();
                    break;
            }
        }
        SpheneUI.Layout.EndNavContent();
    }

    private void DrawHomePage()
    {
        var perm = GroupFullInfo.GroupPermissions;

        SpheneUI.Layout.SectionCard(() =>
        {
            var invitesOpen = !perm.IsDisableInvites();
            if (SpheneUI.Controls.Toggle("Open to new invites", ref invitesOpen))
            {
                perm.SetDisableInvites(!invitesOpen);
                _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
            }
            SpheneUI.AttachTooltip("Locked syncshells cannot be joined by new users.");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Text.Muted("Suggested defaults offered to new members when they join:");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);

            void SuggestedToggle(string label, bool disabled, Action<bool> apply)
            {
                var enabled = !disabled;
                if (SpheneUI.Controls.Toggle(label, ref enabled))
                {
                    apply(!enabled);
                }
            }

            void PushPerms()
                => _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));

            SuggestedToggle("Sound sync", perm.IsPreferDisableSounds(),
                v => { perm.SetPreferDisableSounds(v); PushPerms(); });
            SuggestedToggle("Animation sync", perm.IsPreferDisableAnimations(),
                v => { perm.SetPreferDisableAnimations(v); PushPerms(); });
            SuggestedToggle("VFX sync", perm.IsPreferDisableVFX(),
                v => { perm.SetPreferDisableVFX(v); PushPerms(); });
        }, "Access & Sync Defaults",
            description: "Locking disables new invites and prevents new users from joining. "
                + "Suggested defaults are offered to users when they join this syncshell.");

        if (!_isOwner)
        {
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.SectionHeader("Password");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("New Password:");
            var buttonSize = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Passport, "Change Password");
            var spacing = ImGui.GetStyle().ItemSpacing.X;

            ImGui.SameLine();
            ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - buttonSize - spacing));
            SpheneUI.Controls.InputTextWithHint("##changepw", "Min 10 characters", ref _newPassword, 50);
            ImGui.SameLine();
            using (ImRaii.Disabled(_newPassword.Length < 10))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Passport, "Change Password"))
                {
                    _pwChangeSuccess = _apiController.GroupChangePassword(new GroupPasswordDto(GroupFullInfo.Group, _newPassword)).Result;
                    _newPassword = string.Empty;
                }
            }
            SpheneUI.AttachTooltip("Password requires to be at least 10 characters long. This action is irreversible.");

            if (!_pwChangeSuccess)
            {
                SpheneUI.Text.ColoredWrapped("Failed to change the password. Password requires to be at least 10 characters long.", ImGuiColors.DalamudYellow);
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            SpheneUI.Text.SectionHeader("Identity");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Syncshell Alias:");
            var aliasButtonSize = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Tag, "Set Alias");
            var aliasSpacing = ImGui.GetStyle().ItemSpacing.X;

            ImGui.SameLine();
            ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - aliasButtonSize - aliasSpacing));
            SpheneUI.Controls.InputTextWithHint("##changealias", "3-50 characters (leave empty to clear)", ref _newAlias, 50);
            ImGui.SameLine();
            using (ImRaii.Disabled(_newAlias.Length > 0 && _newAlias.Length < 3))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Tag, "Set Alias"))
                {
                    var aliasToSet = string.IsNullOrWhiteSpace(_newAlias) ? null : _newAlias.Trim();
                    _aliasChangeSuccess = _apiController.GroupSetAlias(new GroupAliasDto(GroupFullInfo.Group, aliasToSet)).Result;
                    if (_aliasChangeSuccess)
                    {
                        _newAlias = string.Empty;
                    }
                }
            }
            SpheneUI.AttachTooltip("Set a custom alias for your syncshell (3-50 characters). Leave empty to clear the alias.");

            if (!_aliasChangeSuccess)
            {
                SpheneUI.Text.ColoredWrapped("Failed to change the alias. Alias must be 3-50 characters long and unique.", ImGuiColors.DalamudYellow);
            }

            var currentAlias = GroupFullInfo.Group.Alias;
            SpheneUI.Text.Colored(!string.IsNullOrEmpty(currentAlias) ? $"Current alias: {currentAlias}" : "No alias set",
                !string.IsNullOrEmpty(currentAlias) ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey);
        }, "Owner Settings", description: "Security and identity settings for this syncshell.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.ColoredWrapped("Deleting removes the syncshell for every member. This action is irreversible.", ImGuiColors.DalamudGrey);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Delete Syncshell") && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
            {
                IsOpen = false;
                _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
            }
            SpheneUI.AttachTooltip("Hold CTRL and Shift and click to delete this Syncshell." + Environment.NewLine + "WARNING: this action is irreversible.");
        }, "Danger Zone", accentColor: ImGuiColors.DalamudRed);
    }

    private void DrawInvitesPage()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Envelope, "Generate Single Invite"))
            {
                ImGui.SetClipboardText(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1).Result.FirstOrDefault() ?? string.Empty);
            }
            SpheneUI.AttachTooltip("Creates a single-use invite (valid for 24h) and copies it to the clipboard.");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Amount");
            ImGui.SameLine();
            SpheneUI.Controls.InputInt("##amountofinvites", ref _multiInvites, preferredWidth: 100);
            ImGui.SameLine();
            using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Envelope, "Generate " + _multiInvites + " invites"))
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
        }, "Generate Invites", description: "Create single-use invites valid for 24 hours.");

        if (!_oneTimeInvites.Any())
        {
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        SpheneUI.Layout.SectionCard(() =>
        {
            var invites = string.Join(Environment.NewLine, _oneTimeInvites);
            SpheneUI.Controls.InputTextMultiline("##generated_invites", ref invites, 5000, new(0, 120), ImGuiInputTextFlags.ReadOnly);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Copy, "Copy All"))
            {
                ImGui.SetClipboardText(invites);
            }
            SpheneUI.AttachTooltip("Copy all generated invites to clipboard");
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear"))
            {
                _oneTimeInvites.Clear();
            }
            SpheneUI.AttachTooltip("Clear the generated invites list");
        }, "Generated", description: "Share these codes with the people you want to invite.");
    }

    private void DrawUserManagementPage()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    
            if (!_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var pairs))
            {
                SpheneUI.Text.ColoredWrapped("No users found in this Syncshell", ImGuiColors.DalamudYellow);
            }
            else
            {
                var groupedPairs = new Dictionary<Pair, GroupPairUserInfo?>(pairs.Select(p => new KeyValuePair<Pair, GroupPairUserInfo?>(p,
                    GroupFullInfo.GroupPairUserInfos.TryGetValue(p.UserData.UID, out GroupPairUserInfo value) ? value : null)));

                var sortedPairs = groupedPairs.OrderBy(p =>
                {
                    if (p.Value == null) return 10;
                    if (p.Value.Value.IsModerator()) return 0;
                    if (p.Value.Value.IsPinned()) return 1;
                    return 10;
                }).ThenBy(p => p.Key.GetNote() ?? p.Key.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase).ToList();

                var totalUsers = sortedPairs.Count;
                var totalPages = (int)Math.Ceiling((double)totalUsers / _userListItemsPerPage);
                        
                // Ensure current page is within bounds
                if (_userListCurrentPage >= totalPages && totalPages > 0)
                    _userListCurrentPage = totalPages - 1;
                if (_userListCurrentPage < 0)
                    _userListCurrentPage = 0;

                // Pagination controls
                SpheneUI.Text.Plain($"{totalUsers} users · page {_userListCurrentPage + 1} of {Math.Max(1, totalPages)}");
                ImGui.SameLine();

                using (ImRaii.Disabled(_userListCurrentPage <= 0))
                {
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.AngleDoubleLeft))
                        _userListCurrentPage = 0;
                    ImGui.SameLine();
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.AngleLeft))
                        _userListCurrentPage--;
                }
                ImGui.SameLine();

                using (ImRaii.Disabled(_userListCurrentPage >= totalPages - 1))
                {
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.AngleRight))
                        _userListCurrentPage++;
                    ImGui.SameLine();
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.AngleDoubleRight))
                        _userListCurrentPage = Math.Max(0, totalPages - 1);
                }

                SpheneUI.Layout.Space(SpheneUI.Spacing.Hairline);

                // Get current page items
                var currentPagePairs = sortedPairs
                    .Skip(_userListCurrentPage * _userListItemsPerPage)
                    .Take(_userListItemsPerPage)
                    .ToList();

                // The table grows with its entries instead of stretching
                // with the window — capped at a page of rows, ScrollY takes
                // over beyond that.
                var rowHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.Y * 2f;
                var visibleRows = Math.Clamp(currentPagePairs.Count, 1, _userListItemsPerPage);
                var tableOuterHeight = rowHeight * (visibleRows + 1);
                using var table = ImRaii.Table("userList#" + GroupFullInfo.Group.GID, 4,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                    new Vector2(0f, tableOuterHeight));
                if (table)
                {
                    ImGui.TableSetupColumn("Handle/Note", ImGuiTableColumnFlags.None, 3);
                    ImGui.TableSetupColumn("Online/Name", ImGuiTableColumnFlags.None, 2);
                    ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.None, 1);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 2);
                    ImGui.TableHeadersRow();

                    foreach (var pair in currentPagePairs)
                    {
                        using var tableId = ImRaii.PushId("userTable_" + pair.Key.UserData.UID);

                        ImGui.TableNextColumn(); // alias/uid/note
                        var note = pair.Key.GetNote();
                        var text = note == null ? pair.Key.UserData.AliasOrUID : note + " (" + pair.Key.UserData.AliasOrUID + ")";
                        ImGui.AlignTextToFramePadding();
                        SpheneUI.Text.Plain(text);

                        ImGui.TableNextColumn(); // online/name
                        string onlineText = pair.Key.IsOnline ? "Online" : "Offline";
                        if (!string.IsNullOrEmpty(pair.Key.PlayerName))
                        {
                            onlineText += " (" + pair.Key.PlayerName + ")";
                        }
                        var boolcolor = SpheneUI.Colors.Bool(pair.Key.IsOnline);
                        ImGui.AlignTextToFramePadding();
                        SpheneUI.Text.Colored(onlineText, boolcolor);

                        ImGui.TableNextColumn(); // special flags
                        if (pair.Value != null && (pair.Value.Value.IsModerator() || pair.Value.Value.IsPinned()))
                        {
                            if (pair.Value.Value.IsModerator())
                            {
                                SpheneUI.Icons.Icon(FontAwesomeIcon.UserShield);
                                SpheneUI.AttachTooltip("Moderator");
                            }
                            if (pair.Value.Value.IsPinned())
                            {
                                SpheneUI.Icons.Icon(FontAwesomeIcon.Thumbtack);
                                SpheneUI.AttachTooltip("Pinned");
                            }
                        }
                        else
                        {
                            SpheneUI.Icons.Icon(FontAwesomeIcon.None);
                        }

                        ImGui.TableNextColumn(); // actions
                        if (_isOwner)
                        {
                            if (SpheneUI.Icons.Button(FontAwesomeIcon.UserShield))
                            {
                                GroupPairUserInfo userInfo = pair.Value ?? GroupPairUserInfo.None;

                                userInfo.SetModerator(!userInfo.IsModerator());

                                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                            }
                            SpheneUI.AttachTooltip(pair.Value != null && pair.Value.Value.IsModerator() ? "Demod user" : "Mod user");
                            ImGui.SameLine();
                        }

                        if (_isOwner || (pair.Value == null || (pair.Value != null && !pair.Value.Value.IsModerator())))
                        {
                            if (SpheneUI.Icons.Button(FontAwesomeIcon.Thumbtack))
                            {
                                GroupPairUserInfo userInfo = pair.Value ?? GroupPairUserInfo.None;

                                userInfo.SetPinned(!userInfo.IsPinned());

                                _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                            }
                            SpheneUI.AttachTooltip(pair.Value != null && pair.Value.Value.IsPinned() ? "Unpin user" : "Pin user");
                            ImGui.SameLine();

                            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                            {
                                if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                                {
                                    _ = _apiController.GroupRemoveUser(new GroupPairDto(GroupFullInfo.Group, pair.Key.UserData));
                                }
                            }
                            SpheneUI.AttachTooltip("Remove user from Syncshell"
                                + SpheneUI.TooltipSeparator + "Hold CTRL to enable this button");

                            ImGui.SameLine();
                            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                            {
                                if (SpheneUI.Icons.Button(FontAwesomeIcon.Ban))
                                {
                                    Mediator.Publish(new OpenBanUserPopupMessage(pair.Key, GroupFullInfo));
                                }
                            }
                            SpheneUI.AttachTooltip("Ban user from Syncshell"
                                + SpheneUI.TooltipSeparator + "Hold CTRL to enable this button");
                        }
                    }
                }
            }
                    
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }, "Users", description: "Members of this syncshell. Pinned users and moderators are listed first.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    
            SpheneUI.Text.Plain("Clear Syncshell:");
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Broom, "Clear Syncshell"))
                {
                    _ = _apiController.GroupClear(new(GroupFullInfo.Group));
                }
            }
            SpheneUI.AttachTooltip("This will remove all non-pinned, non-moderator users from the Syncshell."
                + SpheneUI.TooltipSeparator + "Hold CTRL to enable this button");

            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                    
            SpheneUI.Text.Plain("Inactive User Management:");
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain("Inactivity threshold:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            SpheneUI.Controls.Combo("Days of inactivity", [7, 14, 30, 90], (count) =>
            {
                return count + " days";
            },
            (selected) =>
            {
                _pruneDays = selected;
                _pruneTestTask = null;
                _pruneTask = null;
            },
            _pruneDays);
                    
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Search, "Check Inactive Users"))
            {
                _pruneTestTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: false);
                _pruneTask = null;
            }
            SpheneUI.AttachTooltip($"This will start the prune process for this Syncshell of inactive Sphene users that have not logged in in the past {_pruneDays} days."
                + Environment.NewLine + "You will be able to review the amount of inactive users before executing the prune."
                + SpheneUI.TooltipSeparator + "Note: this check excludes pinned users and moderators of this Syncshell.");

            if (_pruneTestTask != null)
            {
                if (!_pruneTestTask.IsCompleted)
                {
                    SpheneUI.Text.ColoredWrapped("Calculating inactive users...", ImGuiColors.DalamudYellow);
                }
                else
                {
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Text.Wrapped($"Found {_pruneTestTask.Result} user(s) that have not logged into Sphene in the past {_pruneDays} days.");
                    if (_pruneTestTask.Result > 0)
                    {
                        using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                        {
                            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Broom, "Prune Inactive Users"))
                            {
                                _pruneTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: true);
                                _pruneTestTask = null;
                            }
                        }
                        SpheneUI.AttachTooltip($"Pruning will remove {_pruneTestTask?.Result ?? 0} inactive user(s)."
                            + SpheneUI.TooltipSeparator + "Hold CTRL to enable this button");
                    }
                }
            }
            if (_pruneTask != null)
            {
                if (!_pruneTask.IsCompleted)
                {
                    SpheneUI.Text.ColoredWrapped("Pruning Syncshell...", ImGuiColors.DalamudYellow);
                }
                else
                {
                    SpheneUI.Text.Wrapped($"Syncshell was pruned and {_pruneTask.Result} inactive user(s) have been removed.");
                }
            }
                    
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }, "Mass Management", description: "Bulk operations for this syncshell. Hold CTRL when required.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        SpheneUI.Layout.SectionCard(() =>
        {
            var banRefreshWidth = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Sync, "Refresh");
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - banRefreshWidth));
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Sync, "Refresh"))
            {
                _bannedUsers = _apiController.GroupGetBannedUsers(new GroupDto(GroupFullInfo.Group)).Result;
            }
            SpheneUI.AttachTooltip("Refresh the banned users list from the server");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                    
            if (_bannedUsers.Any())
            {
                // Same content-sized growth as the member list above —
                // capped at a page of rows before ScrollY kicks in.
                var banRowHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.Y * 2f;
                var banVisibleRows = Math.Clamp(_bannedUsers.Count, 1, 20);
                var banTableHeight = banRowHeight * (banVisibleRows + 1);
                if (ImGui.BeginTable("bannedusertable" + GroupFullInfo.GID, 6,
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                        new Vector2(0f, banTableHeight)))
                {
                    ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.None, 1);
                    ImGui.TableSetupColumn("Banned By", ImGuiTableColumnFlags.None, 1);
                    ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.None, 2);
                    ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.None, 3);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 1);

                    ImGui.TableHeadersRow();

                    foreach (var bannedUser in _bannedUsers.ToList())
                    {
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Plain(bannedUser.UserAliasOrUID);
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Plain(bannedUser.BannedBy);
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Plain(bannedUser.BannedOn.ToLocalTime().ToString(CultureInfo.CurrentCulture));
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Wrapped(bannedUser.Reason);
                        ImGui.TableNextColumn();
                        using var idScope = ImRaii.PushId(bannedUser.UID);
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.UserCheck, "Unban"))
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await _apiController.GroupUnbanUser(bannedUser).ConfigureAwait(false); }
                                catch (Exception ex) { _logger.LogError(ex, "Failed to unban user {UID}", bannedUser.UID); }
                            });
                            _bannedUsers.RemoveAll(b => string.Equals(b.UID, bannedUser.UID, StringComparison.Ordinal));
                        }
                        SpheneUI.AttachTooltip("Remove this user from the ban list");
                    }

                    ImGui.EndTable();
                }
            }
            else
            {
                SpheneUI.Text.Colored("No banned users", ImGuiColors.DalamudGrey);
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }, "User Bans", description: "Manage the ban list for this syncshell.");
    }

    private void DrawAreaBindingPage()
    {
        // Actions dock top-right: Apply stays visible and reachable instead
        // of appearing below the scroll fold only while changes are fresh.
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var actionWidth = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Save, "Apply")
            + SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Sync, "Refresh") + spacing;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX()
            + MathF.Max(0f, ImGui.GetContentRegionAvail().X - actionWidth));
        using (ImRaii.Disabled(!_areaSettingsDirty))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Apply"))
            {
                _areaSettingsDirty = false;
                _ = UpdateAreaBindingSettings();
            }
        }
        SpheneUI.AttachTooltip("Apply settings to the server");
        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Sync, "Refresh"))
        {
            RefreshDetectedHousingProperties("refresh button");
            _ = LoadCurrentAreaBinding();
        }
        SpheneUI.AttachTooltip("Refresh detected housing properties and reload the current binding.");
        SpheneUI.Text.ColoredWrapped("Bind this syncshell to housing locations you own. Enable a property, then pick which areas should trigger auto-join.", ImGuiColors.DalamudGrey);

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);

        DrawOwnedPropertiesControls();
        DrawAreaBindingSettingsCards();
    }

    private async Task UpdateAreaBindingSettings()
    {
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
    
    private async Task LoadCurrentWelcomePage()
    {
        _isLoadingWelcomePage = true;
        try
        {
            var page = await _apiController.GroupGetWelcomePage(new GroupDto(GroupFullInfo.Group)).ConfigureAwait(false);
            
            if (page != null)
            {
                _welcomeText = page.WelcomeText ?? string.Empty;
                _welcomeImageBase64 = page.WelcomeImageBase64 ?? string.Empty;
                _imageFileName = page.ImageFileName ?? string.Empty;
                _imageContentType = page.ImageContentType ?? string.Empty;
                _imageSize = page.ImageSize ?? 0;
                _welcomePageEnabled = page.IsEnabled;
                _showOnJoin = page.ShowOnJoin;
                _showOnAreaBoundJoin = page.ShowOnAreaBoundJoin;
            }
            else
            {
                _welcomeText = string.Empty;
                _welcomeImageBase64 = string.Empty;
                _imageFileName = string.Empty;
                _imageContentType = string.Empty;
                _imageSize = 0;
                _welcomePageEnabled = false;
                _showOnJoin = true;
                _showOnAreaBoundJoin = true;
                SpheneUI.Media.Evict(WelcomeImageKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load welcome page for group {GroupId}", GroupFullInfo.Group.GID);
        }
        finally
        {
            _isLoadingWelcomePage = false;
        }
    }
    
    private void DrawWelcomePageControls()
    {
        if (_isLoadingWelcomePage)
        {
            SpheneUI.Text.Plain("Loading welcome page settings...");
            return;
        }

        SpheneUI.Layout.SectionCard(() =>
        {
            if (SpheneUI.Controls.Toggle("Enable Welcome Page", ref _welcomePageEnabled))
            {
                _ = SaveWelcomePage();
            }
        }, "Activation", description: "Configure a page that can be shown when users join this syncshell.");

        if (_welcomePageEnabled)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            SpheneUI.Layout.SectionCard(() =>
            {
                if (SpheneUI.Controls.Toggle("Show on regular join", ref _showOnJoin))
                {
                    _ = SaveWelcomePage();
                }
                SpheneUI.AttachTooltip("Show welcome page when users manually join the syncshell");

                if (SpheneUI.Controls.Toggle("Show on area-bound join", ref _showOnAreaBoundJoin))
                {
                    _ = SaveWelcomePage();
                }
                SpheneUI.AttachTooltip("Show welcome page when users auto-join via area binding");
            }, "Display");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            SpheneUI.Layout.SectionCard(() =>
            {
                
                // Markdown Formatter button and Live Preview button
                if (SpheneUI.Controls.Button("Markdown Formatter"))
                {
                    MarkdownFormatterPopup.Open();
                }
                SpheneUI.AttachTooltip("Open advanced Markdown formatter with live preview");
                
                ImGui.SameLine();
                if (SpheneUI.Controls.Button("Live Preview"))
                {
                    Mediator.Publish(new OpenWelcomePageLivePreviewMessage(GroupFullInfo, _welcomeText, WelcomeImageWrap));
                }
                SpheneUI.AttachTooltip("Open live preview of the welcome message");
                
                SpheneUI.Layout.Space(SpheneUI.Spacing.X2);
                
                // Keyboard shortcuts are now handled by the Markdown Formatter Popup
                bool textChanged = false;
                
                // Store original text for comparison
                string originalText = _welcomeText;
                
                // Input text field
                if (SpheneUI.Controls.InputTextMultiline("##welcometext", ref _welcomeText, 2000, new Vector2(0, 100)) || textChanged)
                {
                    _ = SaveWelcomePage();
                    
                    // Update live preview if it's open and text has changed
                    if (!string.Equals(originalText, _welcomeText, StringComparison.Ordinal))
                    {
                        Mediator.Publish(new UpdateWelcomePageLivePreviewMessage(_welcomeText, WelcomeImageWrap));
                    }
                }
                
            }, "Message", description: "Supports Markdown: **bold**, *italic*, `code`, # headers.");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            SpheneUI.Layout.SectionCard(() =>
            {
                
                if (!string.IsNullOrEmpty(_imageFileName))
                {
                    SpheneUI.Text.Colored($"Current image: {_imageFileName}", ImGuiColors.ParsedGreen);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored($"({_imageSize} bytes)", ImGuiColors.DalamudGrey);
                    
                    // Display the actual image if available
                    var welcomeWrap = WelcomeImageWrap;
                    if (welcomeWrap != null)
                    {
                        var maxImageSize = new Vector2(300, 200); // Max display size
                        var imageSize = new Vector2(welcomeWrap.Width, welcomeWrap.Height);

                        // Scale image to fit within max size while maintaining aspect ratio
                        var scale = Math.Min(maxImageSize.X / imageSize.X, maxImageSize.Y / imageSize.Y);
                        if (scale > 1.0f) scale = 1.0f; // Don't upscale

                        SpheneUI.Media.Image(welcomeWrap, imageSize * scale, ImageFit.Stretch);
                    }

                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Remove Image"))
                {
                    _welcomeImageBase64 = string.Empty;
                    _imageFileName = string.Empty;
                    _imageContentType = string.Empty;
                    _imageSize = 0;
                    SpheneUI.Media.Evict(WelcomeImageKey);
                    _ = SaveWelcomePage();
                }
            }
            else
            {
                SpheneUI.Text.Colored("No image selected", ImGuiColors.DalamudGrey);
            }
            
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Upload, "Select Image"))
            {
                _fileDialogManager.OpenFileDialog("Select Welcome Page Image", ".png,.jpg,.jpeg,.gif", (success, file) =>
                {
                    if (!success) return;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var fileContent = File.ReadAllBytes(file);
                            using MemoryStream ms = new(fileContent);
                            var format = await Image.DetectFormatAsync(ms).ConfigureAwait(false);
                            
                            // Check if format is supported
                            var supportedFormats = new[] { "png", "jpg", "jpeg", "gif" };
                            if (!supportedFormats.Any(ext => format.FileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)))
                            {
                                _logger.LogWarning("Unsupported image format: {format}", format.Name);
                                return;
                            }
                            
                            using var image = Image.Load<Rgba32>(fileContent);
                            
                            // Check image size constraints (max 1MB and reasonable dimensions)
                            if (image.Width > 1920 || image.Height > 1080 || fileContent.Length > 1024 * 1024)
                            {
                                _logger.LogWarning("Image too large: {width}x{height}, {size} bytes", image.Width, image.Height, fileContent.Length);
                                return;
                            }
                            
                            // Update welcome page image data
                            _welcomeImageBase64 = Convert.ToBase64String(fileContent);
                            _imageFileName = Path.GetFileName(file);
                            _imageContentType = $"image/{format.FileExtensions.First()}";
                            _imageSize = fileContent.Length;
                            
                            // Invalidate the cached texture so it re-decodes on next draw
                            SpheneUI.Media.Evict(WelcomeImageKey);

                            // Save the welcome page with new image
                            await SaveWelcomePage().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing image file");
                        }
                    });
                });
            }
            SpheneUI.AttachTooltip("Select an image file to display on the welcome page (PNG, JPG, GIF supported)");
        }, "Image", description: "Optional image shown at the top of the page (max 1 MB, 1920x1080).");
        }

        if (!_welcomePageSaveSuccess)
        {
            SpheneUI.Text.ColoredWrapped("Failed to save welcome page settings. Please try again.", ImGuiColors.DalamudRed);
        }
    }
    
    private async Task SaveWelcomePage()
    {
        try
        {
            var updateDto = new SyncshellWelcomePageUpdateDto(
                GroupFullInfo.Group,
                string.IsNullOrWhiteSpace(_welcomeText) ? null : _welcomeText,
                string.IsNullOrWhiteSpace(_welcomeImageBase64) ? null : _welcomeImageBase64,
                string.IsNullOrWhiteSpace(_imageFileName) ? null : _imageFileName,
                string.IsNullOrWhiteSpace(_imageContentType) ? null : _imageContentType,
                _imageSize,
                _welcomePageEnabled,
                _showOnJoin,
                _showOnAreaBoundJoin
            );
            
            _welcomePageSaveSuccess = await _apiController.GroupSetWelcomePage(updateDto).ConfigureAwait(false);
            
            if (_welcomePageSaveSuccess)
            {
                _logger.LogDebug("Welcome page saved for syncshell {GID}", GroupFullInfo.Group.GID);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save welcome page for group {GroupId}", GroupFullInfo.Group.GID);
            _welcomePageSaveSuccess = false;
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SpheneUI.Media.Evict(WelcomeImageKey);
        }
        base.Dispose(disposing);
    }
    
    
    
    private void DrawOwnedPropertiesControls()
    {
        var verifiedProperties = GetCachedVerifiedProperties();

        if (_refreshDetectedHousingPropertiesTask == null)
        {
            RefreshDetectedHousingProperties("initial load");
        }

        // Unified properties list (detected + active)
        var combined = new Dictionary<string, (string Type, LocationInfo Location, bool IsActive)>(StringComparer.Ordinal);
        foreach (var d in _detectedHousingProperties)
        {
            var loc = d.Property.Location;
            var key = $"{loc.ServerId}_{loc.TerritoryId}_{loc.WardId}_{loc.HouseId}_{loc.RoomId}";
            var src = d.Source;
            if (string.Equals(src, "FreeCompanyEstate", StringComparison.OrdinalIgnoreCase)) src = "Free Company";
            else if (string.Equals(src, "PersonalChambers", StringComparison.OrdinalIgnoreCase)) src = loc.RoomId > 0 ? $"Room {loc.RoomId}" : "Room";
            else if (string.Equals(src, "ApartmentRoom", StringComparison.OrdinalIgnoreCase)) src = "Apartment";
            var type = loc.HouseId == 100 ? "Apartment" : loc.RoomId > 0 ? "Room" : src;
            combined[key] = (type, loc, false);
        }
        foreach (var v in verifiedProperties)
        {
            var loc = v.Location;
            var key = $"{loc.ServerId}_{loc.TerritoryId}_{loc.WardId}_{loc.HouseId}_{loc.RoomId}";
            if (combined.TryGetValue(key, out var existing))
            {
                combined[key] = (existing.Type, loc, true);
            }
            else
            {
                var type = loc.HouseId == 100 ? "Apartment" : loc.RoomId > 0 ? "Room" : "House";
                combined[key] = (type, loc, true);
            }
        }

        var entries = combined.Values.OrderBy(v => v.Location.ServerId)
            .ThenBy(v => v.Location.TerritoryId).ThenBy(v => v.Location.WardId)
            .ThenBy(v => v.Location.HouseId).ThenBy(v => v.Location.RoomId).ToList();

        SpheneUI.Layout.SectionCard(() =>
        {
            if (entries.Count == 0)
            {
                SpheneUI.Layout.Callout(
                    "No housing properties detected yet. Stand near a house, apartment or FC room you own and hit Refresh.",
                    FontAwesomeIcon.Home, title: "Nothing detected");
                return;
            }

            SpheneUI.Text.Colored($"{GetUniqueBoundAreaCount()} bound area(s) active", ImGuiColors.ParsedGreen);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            var first = true;
            foreach (var entry in entries)
            {
                if (!first)
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    SpheneUI.Layout.Separator();
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
                first = false;
                DrawPropertyCard(entry);
            }
        }, "Housing Properties",
            description: "Detected properties show what the game reports. Enable a property to manage its area triggers.");
    }

    private void DrawPropertyCard((string Type, LocationInfo Location, bool IsActive) entry)
    {
        var location = entry.Location;
        var isActive = entry.IsActive;
        var id = $"prop_{location.ServerId}_{location.TerritoryId}_{location.WardId}_{location.HouseId}_{location.RoomId}";
        using var idScope = ImRaii.PushId(id);

        var icon = location.HouseId == 100 || location.IsIndoor && location.RoomId > 0
            ? FontAwesomeIcon.Building
            : location.RoomId > 0 ? FontAwesomeIcon.DoorClosed : FontAwesomeIcon.Home;

        var serverName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)location.ServerId, out var sName)
            ? sName
            : $"Server {location.ServerId}";
        var territoryName = "Territory " + location.TerritoryId;
        if (_dalamudUtilService.TerritoryData.Value.TryGetValue(location.TerritoryId, out var tName))
        {
            var parts = tName.Split(" - ", 2);
            territoryName = parts.Length == 2 ? $"{parts[1]} ({parts[0]})" : tName;
        }

        // activation toggle spans the whole header row — icon and name are
        // part of the click target like every other toggle row
        var toggled = isActive;
        if (SpheneUI.Controls.Toggle($"{entry.Type} • {serverName}##activate", ref toggled,
                icon: icon, labelColor: isActive ? ImGuiColors.ParsedGreen : null))
        {
            if (toggled)
            {
                ActivateProperty(location);
            }
            else
            {
                DeactivateProperty(location);
            }
        }
        SpheneUI.AttachTooltip(isActive
            ? "Disable this property (its bound areas are suspended, not deleted)"
            : "Enable this property to bind areas to it");

        SpheneUI.Text.Plain(territoryName);

        var sizeLabel = _dalamudUtilService.GetHousingPlotSizeLabel(location);
        var wardText = location.WardId > 0 ? $"Ward {location.WardId}" : "Ward -";
        var plotText = location.HouseId > 0 ? (location.HouseId == 100 ? "Apt" : $"Plot {location.HouseId}") : "Plot -";
        var partsLine = new List<string>(4) { wardText, plotText };
        if (location.RoomId > 0)
        {
            partsLine.Add($"Room {location.RoomId}");
        }
        partsLine.Add(sizeLabel);
        SpheneUI.Text.Colored(string.Join(" • ", partsLine), ImGuiColors.DalamudGrey);

        // bound-area floor chips — visible even while disabled so suspended
        // configuration stays readable
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Colored("Bound areas:", ImGuiColors.DalamudGrey);
        ImGui.SameLine();
        using (ImRaii.Disabled(!isActive))
        {
            if (!location.IsIndoor)
            {
                DrawBoundAreaChips(location, isActive);
            }
            else if (location.RoomId > 0)
            {
                DrawRoomBoundChip(location, isActive);
            }
        }
    }

    private void DrawBoundAreaChips(LocationInfo propertyLocation, bool propertyIsActive)
    {
        if (propertyLocation.WardId <= 0 || propertyLocation.HouseId <= 0)
        {
            return;
        }

        var sourceAreas = propertyIsActive
            ? _boundAreas
            : _housingOwnershipService.GetSuspendedAreaBindingLocations(GroupFullInfo.Group.GID, propertyLocation);

        var plotSize = _dalamudUtilService.GetHousingPlotSizeLabel(propertyLocation);
        var isSmall = string.Equals(plotSize, "Small", StringComparison.Ordinal);
        var showSecond = string.Equals(plotSize, "Medium", StringComparison.Ordinal) || string.Equals(plotSize, "Large", StringComparison.Ordinal);

        var outdoor = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotOutdoor &&
            BoundAreaBelongsToProperty(b, propertyLocation));
        var ground = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, propertyLocation) &&
            (b.Location.MapId == 0 || b.Location.MapId == HousingInteriorRelativeMapIds.Ground));
        var basement = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, propertyLocation) &&
            b.Location.MapId == HousingInteriorRelativeMapIds.Basement);
        var second = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, propertyLocation) &&
            b.Location.MapId == HousingInteriorRelativeMapIds.Second);

        if (SpheneUI.Controls.ToggleChip("Outdoor", ref outdoor))
        {
            SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotOutdoor, 0, false, "Outdoor", outdoor);
        }
        ImGui.SameLine();

        if (SpheneUI.Controls.ToggleChip("Ground", ref ground))
        {
            SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Ground, true, "Ground floor", ground);
        }
        ImGui.SameLine();

        if (SpheneUI.Controls.ToggleChip("Basement", ref basement))
        {
            SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Basement, true, "Basement", basement);
        }

        if (showSecond)
        {
            ImGui.SameLine();
            if (SpheneUI.Controls.ToggleChip("2nd Floor", ref second))
            {
                SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Second, true, "Second floor", second);
            }
        }
        else if (!isSmall)
        {
            ImGui.SameLine();
            SpheneUI.Text.Colored(plotSize, ImGuiColors.DalamudGrey);
        }
    }

    private void DrawRoomBoundChip(LocationInfo roomLocation, bool propertyIsActive)
    {
        if (roomLocation.RoomId <= 0)
        {
            return;
        }

        var sourceAreas = propertyIsActive
            ? _boundAreas
            : _housingOwnershipService.GetSuspendedAreaBindingLocations(GroupFullInfo.Group.GID, roomLocation);

        var label = "Interior";
        var thisRoom = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, roomLocation) &&
            b.Location.RoomId == roomLocation.RoomId);
        if (SpheneUI.Controls.ToggleChip(label, ref thisRoom))
        {
            SetRoomBoundEnabled(roomLocation, thisRoom);
        }
    }

    private void RefreshDetectedHousingProperties(string source)
    {
        _refreshDetectedHousingPropertiesTask = Task.Run(async () =>
        {
            try
            {
                var detected = await _housingOwnershipService.GetDetectedOwnedHousingPropertiesAsync().ConfigureAwait(false);
                _detectedHousingProperties = detected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh detected housing properties ({Source})", source);
            }
        });
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

    private void DrawAreaBindingSettingsCards()
    {
        var hasBoundAreas = _boundAreas.Count > 0;
        if (!hasBoundAreas)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            SpheneUI.Layout.Callout(
                "Select at least one bound area on a property above (Outdoor, Ground, Basement, 2nd Floor or Interior) to unlock the auto-join settings.",
                FontAwesomeIcon.InfoCircle, title: "No bound areas");
        }

        using (ImRaii.Disabled(!hasBoundAreas))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            SpheneUI.Layout.SectionCard(() =>
            {
                if (SpheneUI.Controls.Toggle("Auto Broadcast Enabled", ref _autoBroadcastEnabled)) _areaSettingsDirty = true;
                SpheneUI.AttachTooltip("Automatically broadcast this syncshell to users entering any bound area");

                if (SpheneUI.Controls.Toggle("Require Owner Presence", ref _requireOwnerPresence)) _areaSettingsDirty = true;
                SpheneUI.AttachTooltip("Only allow auto-join when the syncshell owner is present in the area");

                if (SpheneUI.Controls.InputInt("Max Auto Join Users", ref _maxAutoJoinUsers, preferredWidth: 120))
                {
                    _maxAutoJoinUsers = Math.Max(1, Math.Min(100, _maxAutoJoinUsers));
                    _areaSettingsDirty = true;
                }
                SpheneUI.AttachTooltip("Maximum number of users that can auto-join this syncshell (1-100)");
            }, "Auto-Join",
                description: "What happens when someone walks into a bound area.",
                accentColor: SpheneUI.Palette.NetworkActive);

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            SpheneUI.Layout.SectionCard(() =>
            {
                if (SpheneUI.Controls.Toggle("Notify on User Enter", ref _notifyOnUserEnter)) _areaSettingsDirty = true;
                if (SpheneUI.Controls.Toggle("Notify on User Leave", ref _notifyOnUserLeave)) _areaSettingsDirty = true;
            }, "Notifications",
                accentColor: SpheneUI.Palette.CrystalBlue);

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
            SpheneUI.Layout.SectionCard(() =>
            {
                SpheneUI.Text.Plain("Custom join message");
                ImGui.SetNextItemWidth(-1);
                if (SpheneUI.Controls.InputTextWithHint("##custom_join_message",
                        "Optional message shown when users auto-join", ref _customJoinMessage, 200))
                {
                    _areaSettingsDirty = true;
                }

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                SpheneUI.Layout.Separator();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

                if (SpheneUI.Controls.Toggle("Require Rules Acceptance", ref _requireRulesAcceptance)) _areaSettingsDirty = true;
                SpheneUI.AttachTooltip("Require users to accept rules before auto-joining");

                if (_requireRulesAcceptance)
                {
                    if (SpheneUI.Controls.InputInt("Rules Version", ref _rulesVersion, preferredWidth: 120))
                    {
                        _rulesVersion = Math.Max(1, _rulesVersion);
                        _areaSettingsDirty = true;
                    }
                    SpheneUI.AttachTooltip("Increment this when rules change to require re-acceptance");

                    SpheneUI.Text.Plain("Rules Text:");
                    ImGui.SetNextItemWidth(-1);
                    var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 6);
                    if (SpheneUI.Controls.InputTextMultiline("##join_rules", ref _joinRules, 2000, size))
                    {
                        ParseRulesIntoList();
                        _areaSettingsDirty = true;
                    }
                }
            }, "Message & Rules",
                description: "Shown to users when they auto-join.",
                accentColor: SpheneUI.Palette.EtherealPurple);
        }
    }

    private void ActivateProperty(LocationInfo location)
    {
        var allowOutdoor = location.RoomId == 0;
        var allowIndoor = true;
        var groupGid = GroupFullInfo.Group.GID;
        _ = Task.Run(async () =>
        {
            try
            {
                await _housingOwnershipService.AddVerifiedOwnedPropertyWithPreferences(location, allowOutdoor, allowIndoor).ConfigureAwait(false);
                await _housingOwnershipService.ForceRefreshFromServer().ConfigureAwait(false);

                var restored = _housingOwnershipService.RestoreSuspendedAreaBindingLocations(groupGid, location);
                if (restored.Count > 0)
                {
                    foreach (var boundArea in restored)
                    {
                        if (!_boundAreas.Any(x => x.MatchingMode == boundArea.MatchingMode && LocationsMatch(x.Location, boundArea.Location)))
                        {
                            _boundAreas.Add(boundArea);
                        }
                    }
                    await UpdateAreaBindingSettings().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to activate property {Location}", location);
            }
            InitializeUiStateFromServer();
        });
    }

    private void DeactivateProperty(LocationInfo location)
    {
        var groupGid = GroupFullInfo.Group.GID;
        var areasToSuspend = _boundAreas.Where(b => BoundAreaBelongsToProperty(b, location)).ToList();
        if (areasToSuspend.Count > 0)
        {
            foreach (var boundArea in areasToSuspend)
            {
                _boundAreas.Remove(boundArea);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _housingOwnershipService.RemoveVerifiedOwnedProperty(location).ConfigureAwait(false);
                await _housingOwnershipService.ForceRefreshFromServer().ConfigureAwait(false);
                if (areasToSuspend.Count > 0)
                {
                    _housingOwnershipService.SuspendAreaBindingLocations(groupGid, location, areasToSuspend);
                    if (_boundAreas.Count == 0)
                    {
                        await RemoveAreaBinding().ConfigureAwait(false);
                    }
                    else
                    {
                        await UpdateAreaBindingSettings().ConfigureAwait(false);
                    }
                }
                else if (_boundAreas.Count == 0 && _areaBindingEnabled)
                {
                    await RemoveAreaBinding().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate property {Location}", location);
            }
            InitializeUiStateFromServer();
        });
    }
    
    private static bool LocationsMatch(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId &&
               loc1.TerritoryId == loc2.TerritoryId &&
               loc1.WardId == loc2.WardId &&
               loc1.HouseId == loc2.HouseId &&
               loc1.RoomId == loc2.RoomId;
    }

    private static bool BoundAreaBelongsToProperty(AreaBoundLocationDto boundArea, LocationInfo propertyLocation)
    {
        if (propertyLocation.WardId <= 0 || propertyLocation.HouseId <= 0)
        {
            return false;
        }

        var loc = boundArea.Location;
        if (loc.WardId != 0 && loc.WardId != propertyLocation.WardId)
        {
            return false;
        }

        if (loc.HouseId != 0 && loc.HouseId != propertyLocation.HouseId)
        {
            return false;
        }

        if (loc.ServerId != 0 && loc.ServerId != propertyLocation.ServerId)
        {
            return false;
        }

        if (loc.TerritoryId != 0 && loc.TerritoryId != propertyLocation.TerritoryId)
        {
            return false;
        }

        if (propertyLocation.RoomId == 0)
        {
            if (loc.RoomId != 0)
            {
                return false;
            }
        }
        else
        {
            if (loc.RoomId != 0 && loc.RoomId != propertyLocation.RoomId)
            {
                return false;
            }
        }

        return true;
    }

    private void SetRoomBoundEnabled(LocationInfo roomLocation, bool enabled)
    {
        _boundAreas.RemoveAll(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, roomLocation) &&
            b.Location.RoomId == roomLocation.RoomId);

        if (enabled)
        {
            _areaBindingEnabled = true;
            _boundAreas.Add(new AreaBoundLocationDto
            {
                Id = 0,
                Location = new LocationInfo
                {
                    ServerId = roomLocation.ServerId,
                    TerritoryId = roomLocation.TerritoryId,
                    WardId = roomLocation.WardId,
                    HouseId = roomLocation.HouseId,
                    RoomId = roomLocation.RoomId,
                    MapId = 0,
                    IsIndoor = true
                },
                MatchingMode = AreaMatchingMode.HousingPlotIndoor,
                LocationName = roomLocation.HouseId == 100 ? "Apartment" : $"Room {roomLocation.RoomId}",
                CreatedAt = DateTime.UtcNow
            });
        }

        NormalizeBoundAreasInPlace();

        _ = Task.Run(async () =>
        {
            if (_boundAreas.Count == 0)
            {
                if (_areaBindingEnabled)
                {
                    await RemoveAreaBinding().ConfigureAwait(false);
                }
            }
            else
            {
                await UpdateAreaBindingSettings().ConfigureAwait(false);
            }
        });
    }

    private void SetBoundAreaEnabled(LocationInfo propertyLocation, AreaMatchingMode mode, uint mapId, bool isIndoor, string name, bool enabled)
    {
        if (mode == AreaMatchingMode.HousingPlotIndoor && mapId == HousingInteriorRelativeMapIds.Ground)
        {
            _boundAreas.RemoveAll(b =>
                b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
                BoundAreaBelongsToProperty(b, propertyLocation) &&
                (b.Location.MapId == 0 || b.Location.MapId == HousingInteriorRelativeMapIds.Ground));
        }
        else
        {
            _boundAreas.RemoveAll(b =>
                b.MatchingMode == mode &&
                BoundAreaBelongsToProperty(b, propertyLocation) &&
                (mode == AreaMatchingMode.HousingPlotOutdoor || b.Location.MapId == mapId));
        }

        if (enabled)
        {
            _areaBindingEnabled = true;
            _boundAreas.Add(new AreaBoundLocationDto
            {
                Id = 0,
                Location = new LocationInfo
                {
                    ServerId = propertyLocation.ServerId,
                    TerritoryId = propertyLocation.TerritoryId,
                    WardId = propertyLocation.WardId,
                    HouseId = propertyLocation.HouseId,
                    RoomId = 0,
                    MapId = mapId,
                    IsIndoor = isIndoor
                },
                MatchingMode = mode,
                LocationName = name,
                CreatedAt = DateTime.UtcNow
            });
        }

        NormalizeBoundAreasInPlace();

        _ = Task.Run(async () =>
        {
            if (_boundAreas.Count == 0)
            {
                if (_areaBindingEnabled)
                {
                    await RemoveAreaBinding().ConfigureAwait(false);
                }
            }
            else
            {
                await UpdateAreaBindingSettings().ConfigureAwait(false);
            }
        });
    }

    private List<VerifiedHousingProperty> GetCachedVerifiedProperties()
    {
        // Get fresh data from server - no caching to avoid deletion issues
        var serverProperties = _housingOwnershipService.GetVerifiedOwnedProperties();
        
        // Apply UI-only state overrides for immediate visual feedback
        var result = new List<VerifiedHousingProperty>();
        foreach (var property in serverProperties)
        {
            var key = $"{property.Location.TerritoryId}_{property.Location.WardId}_{property.Location.HouseId}_{property.Location.RoomId}";
            
            // Skip properties that have been deleted in the UI
            if (_deletedPropertyKeys.Contains(key))
            {
                continue;
            }
            
            if (_uiPropertyStates.TryGetValue(key, out var uiState))
            {
                // Use UI state for immediate visual feedback
                result.Add(new VerifiedHousingProperty
                {
                    Location = property.Location,
                    AllowOutdoor = uiState.AllowOutdoor,
                    AllowIndoor = uiState.AllowIndoor,
                    PreferOutdoorSyncshells = property.PreferOutdoorSyncshells,
                    PreferIndoorSyncshells = property.PreferIndoorSyncshells
                });
            }
            else
            {
                // Use server state
                result.Add(property);
            }
        }
        
        return result;
    }
    
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
