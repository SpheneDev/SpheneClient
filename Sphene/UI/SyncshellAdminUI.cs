using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
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

namespace Sphene.UI.Components.Popup;

public class SyncshellAdminUI : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly bool _isModerator = false;
    private readonly bool _isOwner = false;
    private readonly List<string> _oneTimeInvites = [];
    private readonly PairManager _pairManager;
    private readonly UiSharedService _uiSharedService;
    private readonly DalamudUtilService _dalamudUtilService;
    private List<BannedGroupUserDto> _bannedUsers = [];
    private int _multiInvites;
    private string _newPassword;
    private bool _pwChangeSuccess;
    private Task<int>? _pruneTestTask;
    private Task<int>? _pruneTask;
    private int _pruneDays = 14;
    
    // Area binding related fields
    private LocationInfo? _currentLocation;
    private AreaBoundSyncshellDto? _currentAreaBinding;
    private AreaBoundSettings _areaBoundSettings = new();
    private bool _isAreaBound = false;
    private bool _isLoadingAreaBinding = false;
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
    private List<string> _rulesList = new();
    private bool _rulesChanged = false;
    
    // Multi-location support
    private List<AreaBoundLocationDto> _boundAreas = new();
    private AreaMatchingMode _newLocationMatchingMode = AreaMatchingMode.ExactMatch;
    private string _newLocationName = string.Empty;
    
    // Alias setting fields
    private string _newAlias = string.Empty;
    private bool _aliasChangeSuccess = true;
    
    // Welcome page fields
    private SyncshellWelcomePageDto? _welcomePage;
    private string _welcomeText = string.Empty;
    private string _welcomeImageBase64 = string.Empty;
    
    // Text selection tracking
    private bool _hasSelection = false;
    private Vector4 _selectedColor = new Vector4(1.0f, 0.0f, 0.0f, 1.0f); // Default red color for picker
    private IDalamudTextureWrap? _welcomeImageTexture = null;
    private string _imageFileName = string.Empty;
    private string _imageContentType = string.Empty;
    private long _imageSize = 0;
    private bool _welcomePageEnabled = false;
    private bool _showOnJoin = true;
    private bool _showOnAreaBoundJoin = true;
    private bool _isLoadingWelcomePage = false;
    private bool _welcomePageSaveSuccess = true;
    private bool _showFormattingHelp = false;
    
    // User list pagination fields
    private int _userListCurrentPage = 0;
    private const int _userListItemsPerPage = 25;
    
    private readonly FileDialogManager _fileDialogManager;
    private readonly UiFactory _uiFactory;
    private readonly HousingOwnershipService _housingOwnershipService;
    
    // UI-only state for property preferences - immediate UI updates
    private readonly Dictionary<string, (bool AllowOutdoor, bool AllowIndoor)> _uiPropertyStates = new();
    
    // Track properties that have been deleted in the UI (to hide them until server confirms)
    private readonly HashSet<string> _deletedPropertyKeys = new();
    
    // State for ownership verification checkboxes
    private bool _verificationAllowOutdoor = true;
    private bool _verificationAllowIndoor = true;
    
    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, SpheneMediator mediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, DalamudUtilService dalamudUtilService, GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService, FileDialogManager fileDialogManager, UiFactory uiFactory, HousingOwnershipService housingOwnershipService)
        : base(logger, mediator, "Syncshell Admin Panel (" + groupFullInfo.GroupAliasOrGID + ")", performanceCollectorService)
    {
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        _uiFactory = uiFactory;
        _housingOwnershipService = housingOwnershipService;
        _isOwner = string.Equals(GroupFullInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        _isModerator = GroupFullInfo.GroupUserInfo.IsModerator();
        _newPassword = string.Empty;
        _multiInvites = 30;
        _pwChangeSuccess = true;
        IsOpen = true;
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(700, 500),
            MaximumSize = new(700, 2000),
        };
        
        // Load current area binding if exists
        _ = LoadCurrentAreaBinding();
        
        // Load current welcome page if exists
        _ = LoadCurrentWelcomePage();
    }

    public GroupFullInfoDto GroupFullInfo { get; private set; }
    
    private async Task LoadCurrentAreaBinding()
    {
        _isLoadingAreaBinding = true;
        try
        {
            var areaBoundSyncshells = await _apiController.GroupGetAreaBoundSyncshells().ConfigureAwait(false);
            var currentBinding = areaBoundSyncshells.FirstOrDefault(x => x.Group.GID == GroupFullInfo.Group.GID);
            
            if (currentBinding != null)
            {
                _currentAreaBinding = currentBinding;
                _areaBoundSettings = currentBinding.Settings;
                _isAreaBound = true;
                _areaBindingEnabled = true;
                
                // Load bound areas
                _boundAreas = currentBinding.BoundAreas.ToList();
                
                // Load global settings
                _autoBroadcastEnabled = _areaBoundSettings.AutoBroadcastEnabled;
                _requireOwnerPresence = _areaBoundSettings.RequireOwnerPresence;
                _maxAutoJoinUsers = _areaBoundSettings.MaxAutoJoinUsers;
                _notifyOnUserEnter = _areaBoundSettings.NotifyOnUserEnter;
                _notifyOnUserLeave = _areaBoundSettings.NotifyOnUserLeave;
                _customJoinMessage = _areaBoundSettings.CustomJoinMessage ?? string.Empty;
                
                // Load rules settings
                _joinRules = _areaBoundSettings.JoinRules ?? string.Empty;
                _rulesVersion = _areaBoundSettings.RulesVersion;
                _requireRulesAcceptance = _areaBoundSettings.RequireRulesAcceptance;
                
                // Parse existing rules into list
                ParseRulesIntoList();
            }
            else
            {
                _currentAreaBinding = null;
                _isAreaBound = false;
                _areaBindingEnabled = false;
                _boundAreas.Clear();
                _areaBoundSettings = new AreaBoundSettings();
                
                // Reset UI variables to defaults
                _autoBroadcastEnabled = true;
                _requireOwnerPresence = false;
                _maxAutoJoinUsers = 50;
                _notifyOnUserEnter = true;
                _notifyOnUserLeave = true;
                _customJoinMessage = string.Empty;
                _newLocationMatchingMode = AreaMatchingMode.ExactMatch;
                _newLocationName = string.Empty;
                
                // Reset rules settings to defaults
                _joinRules = string.Empty;
                _rulesVersion = 1;
                _requireRulesAcceptance = false;
                _rulesList.Clear();
                _rulesChanged = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load current area binding for group {GroupId}", GroupFullInfo.Group.GID);
        }
        finally
        {
            _isLoadingAreaBinding = false;
        }
    }

    protected override void DrawInternal()
    {
        if (!_isModerator && !_isOwner) return;

        GroupFullInfo = _pairManager.Groups[GroupFullInfo.Group];

        using var id = ImRaii.PushId("syncshell_admin_" + GroupFullInfo.GID);

        using (_uiSharedService.UidFont.Push())
            ImGui.TextUnformatted(GroupFullInfo.GroupAliasOrGID + " Administrative Panel");

        ImGui.Separator();
        var perm = GroupFullInfo.GroupPermissions;

        using var tabbar = ImRaii.TabBar("syncshell_tab_" + GroupFullInfo.GID);

        if (tabbar)
        {
            var inviteTab = ImRaii.TabItem("Invites");
            if (inviteTab)
            {
                // Syncshell Status Section
                if (ImGui.CollapsingHeader("Syncshell Status", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    bool isInvitesDisabled = perm.IsDisableInvites();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Syncshell is currently");
                    ImGui.SameLine();
                    if (isInvitesDisabled)
                    {
                        ImGui.TextColored(ImGuiColors.DalamudRed, "locked");
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.ParsedGreen, "unlocked");
                    }
                    ImGui.SameLine();
                    if (_uiSharedService.IconTextButton(isInvitesDisabled ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock,
                        isInvitesDisabled ? "Unlock Syncshell" : "Lock Syncshell"))
                    {
                        perm.SetDisableInvites(!isInvitesDisabled);
                        _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                    }

                    ImGuiHelpers.ScaledDummy(2f);
                }
                
                // Invite Generation Section
                if (ImGui.CollapsingHeader("Generate Invites", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    UiSharedService.TextWrapped("One-time invites work as single-use passwords. Use those if you do not want to distribute your Syncshell password.");
                    
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    // Single invite
                    ImGui.TextUnformatted("Single-Use Invite:");
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, "Generate Single Invite"))
                    {
                        ImGui.SetClipboardText(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1).Result.FirstOrDefault() ?? string.Empty);
                    }
                    UiSharedService.AttachToolTip("Creates a single-use password for joining the syncshell which is valid for 24h and copies it to the clipboard.");
                    
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    // Multi invites
                    ImGui.TextUnformatted("Multi-Use Invites:");
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Amount:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(100);
                    ImGui.InputInt("##amountofinvites", ref _multiInvites);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, "Generate " + _multiInvites + " invites"))
                        {
                            _oneTimeInvites.AddRange(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), _multiInvites).Result);
                        }
                    }
                    UiSharedService.AttachToolTip($"Generate {_multiInvites} one-time invites (1-100 allowed)");

                    ImGuiHelpers.ScaledDummy(2f);
                }
                
                // Generated Invites Section
                if (_oneTimeInvites.Any())
                {
                    if (ImGui.CollapsingHeader("Generated Invites", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGuiHelpers.ScaledDummy(2f);
                        
                        var invites = string.Join(Environment.NewLine, _oneTimeInvites);
                        ImGui.InputTextMultiline("##generated_invites", ref invites, 5000, new(0, 100), ImGuiInputTextFlags.ReadOnly);
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Copy, "Copy All Invites"))
                        {
                            ImGui.SetClipboardText(invites);
                        }
                        UiSharedService.AttachToolTip("Copy all generated invites to clipboard");
                        ImGui.SameLine();
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear List"))
                        {
                            _oneTimeInvites.Clear();
                        }
                        UiSharedService.AttachToolTip("Clear the generated invites list");
                        
                        ImGuiHelpers.ScaledDummy(2f);
                    }
                }
            }
            inviteTab.Dispose();

            var mgmtTab = ImRaii.TabItem("User Management");
            if (mgmtTab)
            {
                // User List Section
                if (ImGui.CollapsingHeader("User List & Administration", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    if (!_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var pairs))
                    {
                        UiSharedService.ColorTextWrapped("No users found in this Syncshell", ImGuiColors.DalamudYellow);
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
                        ImGui.Text($"Users: {totalUsers} | Page {_userListCurrentPage + 1} of {Math.Max(1, totalPages)}");
                        ImGui.SameLine();
                        
                        using (ImRaii.Disabled(_userListCurrentPage <= 0))
                        {
                            if (ImGui.Button("<<"))
                                _userListCurrentPage = 0;
                        }
                        ImGui.SameLine();
                        
                        using (ImRaii.Disabled(_userListCurrentPage <= 0))
                        {
                            if (ImGui.Button("<"))
                                _userListCurrentPage--;
                        }
                        ImGui.SameLine();
                        
                        using (ImRaii.Disabled(_userListCurrentPage >= totalPages - 1))
                        {
                            if (ImGui.Button(">"))
                                _userListCurrentPage++;
                        }
                        ImGui.SameLine();
                        
                        using (ImRaii.Disabled(_userListCurrentPage >= totalPages - 1))
                        {
                            if (ImGui.Button(">>"))
                                _userListCurrentPage = Math.Max(0, totalPages - 1);
                        }

                        ImGuiHelpers.ScaledDummy(1f);

                        // Get current page items
                        var currentPagePairs = sortedPairs
                            .Skip(_userListCurrentPage * _userListItemsPerPage)
                            .Take(_userListItemsPerPage)
                            .ToList();

                        using var table = ImRaii.Table("userList#" + GroupFullInfo.Group.GID, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders);
                        if (table)
                        {
                            ImGui.TableSetupColumn("Alias/UID/Note", ImGuiTableColumnFlags.None, 3);
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
                                ImGui.TextUnformatted(text);

                                ImGui.TableNextColumn(); // online/name
                                string onlineText = pair.Key.IsOnline ? "Online" : "Offline";
                                if (!string.IsNullOrEmpty(pair.Key.PlayerName))
                                {
                                    onlineText += " (" + pair.Key.PlayerName + ")";
                                }
                                var boolcolor = UiSharedService.GetBoolColor(pair.Key.IsOnline);
                                ImGui.AlignTextToFramePadding();
                                UiSharedService.ColorText(onlineText, boolcolor);

                                ImGui.TableNextColumn(); // special flags
                                if (pair.Value != null && (pair.Value.Value.IsModerator() || pair.Value.Value.IsPinned()))
                                {
                                    if (pair.Value.Value.IsModerator())
                                    {
                                        _uiSharedService.IconText(FontAwesomeIcon.UserShield);
                                        UiSharedService.AttachToolTip("Moderator");
                                    }
                                    if (pair.Value.Value.IsPinned())
                                    {
                                        _uiSharedService.IconText(FontAwesomeIcon.Thumbtack);
                                        UiSharedService.AttachToolTip("Pinned");
                                    }
                                }
                                else
                                {
                                    _uiSharedService.IconText(FontAwesomeIcon.None);
                                }

                                ImGui.TableNextColumn(); // actions
                                if (_isOwner)
                                {
                                    if (_uiSharedService.IconButton(FontAwesomeIcon.UserShield))
                                    {
                                        GroupPairUserInfo userInfo = pair.Value ?? GroupPairUserInfo.None;

                                        userInfo.SetModerator(!userInfo.IsModerator());

                                        _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                                    }
                                    UiSharedService.AttachToolTip(pair.Value != null && pair.Value.Value.IsModerator() ? "Demod user" : "Mod user");
                                    ImGui.SameLine();
                                }

                                if (_isOwner || (pair.Value == null || (pair.Value != null && !pair.Value.Value.IsModerator())))
                                {
                                    if (_uiSharedService.IconButton(FontAwesomeIcon.Thumbtack))
                                    {
                                        GroupPairUserInfo userInfo = pair.Value ?? GroupPairUserInfo.None;

                                        userInfo.SetPinned(!userInfo.IsPinned());

                                        _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                                    }
                                    UiSharedService.AttachToolTip(pair.Value != null && pair.Value.Value.IsPinned() ? "Unpin user" : "Pin user");
                                    ImGui.SameLine();

                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                                        {
                                            _ = _apiController.GroupRemoveUser(new GroupPairDto(GroupFullInfo.Group, pair.Key.UserData));
                                        }
                                    }
                                    UiSharedService.AttachToolTip("Remove user from Syncshell"
                                        + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");

                                    ImGui.SameLine();
                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Ban))
                                        {
                                            Mediator.Publish(new OpenBanUserPopupMessage(pair.Key, GroupFullInfo));
                                        }
                                    }
                                    UiSharedService.AttachToolTip("Ban user from Syncshell"
                                        + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");
                                }
                            }
                        }
                    }
                    
                    ImGuiHelpers.ScaledDummy(2f);
                }
                
                // Mass Management Section
                if (ImGui.CollapsingHeader("Mass Management", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    ImGui.TextUnformatted("Clear Syncshell:");
                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, "Clear Syncshell"))
                        {
                            _ = _apiController.GroupClear(new(GroupFullInfo.Group));
                        }
                    }
                    UiSharedService.AttachToolTip("This will remove all non-pinned, non-moderator users from the Syncshell."
                        + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");

                    ImGuiHelpers.ScaledDummy(3f);
                    
                    ImGui.TextUnformatted("Inactive User Management:");
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Inactivity threshold:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    _uiSharedService.DrawCombo("Days of inactivity", [7, 14, 30, 90], (count) =>
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
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Search, "Check Inactive Users"))
                    {
                        _pruneTestTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: false);
                        _pruneTask = null;
                    }
                    UiSharedService.AttachToolTip($"This will start the prune process for this Syncshell of inactive Sphene users that have not logged in in the past {_pruneDays} days."
                        + Environment.NewLine + "You will be able to review the amount of inactive users before executing the prune."
                        + UiSharedService.TooltipSeparator + "Note: this check excludes pinned users and moderators of this Syncshell.");

                    if (_pruneTestTask != null)
                    {
                        if (!_pruneTestTask.IsCompleted)
                        {
                            UiSharedService.ColorTextWrapped("Calculating inactive users...", ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            ImGui.AlignTextToFramePadding();
                            UiSharedService.TextWrapped($"Found {_pruneTestTask.Result} user(s) that have not logged into Sphene in the past {_pruneDays} days.");
                            if (_pruneTestTask.Result > 0)
                            {
                                using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                {
                                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, "Prune Inactive Users"))
                                    {
                                        _pruneTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: true);
                                        _pruneTestTask = null;
                                    }
                                }
                                UiSharedService.AttachToolTip($"Pruning will remove {_pruneTestTask?.Result ?? 0} inactive user(s)."
                                    + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");
                            }
                        }
                    }
                    if (_pruneTask != null)
                    {
                        if (!_pruneTask.IsCompleted)
                        {
                            UiSharedService.ColorTextWrapped("Pruning Syncshell...", ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            UiSharedService.TextWrapped($"Syncshell was pruned and {_pruneTask.Result} inactive user(s) have been removed.");
                        }
                    }
                    
                    ImGuiHelpers.ScaledDummy(2f);
                }
                
                // User Bans Section
                if (ImGui.CollapsingHeader("User Bans", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, "Refresh Banlist"))
                    {
                        _bannedUsers = _apiController.GroupGetBannedUsers(new GroupDto(GroupFullInfo.Group)).Result;
                    }
                    UiSharedService.AttachToolTip("Refresh the banned users list from the server");

                    ImGuiHelpers.ScaledDummy(2f);
                    
                    if (_bannedUsers.Any())
                    {
                        if (ImGui.BeginTable("bannedusertable" + GroupFullInfo.GID, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders))
                        {
                            ImGui.TableSetupColumn("UID", ImGuiTableColumnFlags.None, 1);
                            ImGui.TableSetupColumn("Alias", ImGuiTableColumnFlags.None, 1);
                            ImGui.TableSetupColumn("Banned By", ImGuiTableColumnFlags.None, 1);
                            ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.None, 2);
                            ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.None, 3);
                            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 1);

                            ImGui.TableHeadersRow();

                            foreach (var bannedUser in _bannedUsers.ToList())
                            {
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted(bannedUser.UID);
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted(bannedUser.UserAlias ?? string.Empty);
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted(bannedUser.BannedBy);
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted(bannedUser.BannedOn.ToLocalTime().ToString(CultureInfo.CurrentCulture));
                                ImGui.TableNextColumn();
                                UiSharedService.TextWrapped(bannedUser.Reason);
                                ImGui.TableNextColumn();
                                using var _ = ImRaii.PushId(bannedUser.UID);
                                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserCheck, "Unban"))
                                {
                                    _apiController.GroupUnbanUser(bannedUser);
                                    _bannedUsers.RemoveAll(b => string.Equals(b.UID, bannedUser.UID, StringComparison.Ordinal));
                                }
                                UiSharedService.AttachToolTip("Remove this user from the ban list");
                            }

                            ImGui.EndTable();
                        }
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudGrey, "No banned users");
                    }
                    
                    ImGuiHelpers.ScaledDummy(2f);
                }
            }
            mgmtTab.Dispose();

            var permissionTab = ImRaii.TabItem("Permissions");
            if (permissionTab)
            {
                // Sync Preferences Section
                if (ImGui.CollapsingHeader("Suggested Sync Preferences", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGuiHelpers.ScaledDummy(2f);
                    
                    UiSharedService.TextWrapped("Configure the default sync preferences that will be suggested to users when they join this Syncshell.");
                    
                    ImGuiHelpers.ScaledDummy(3f);
                    
                    bool isDisableAnimations = perm.IsPreferDisableAnimations();
                    bool isDisableSounds = perm.IsPreferDisableSounds();
                    bool isDisableVfx = perm.IsPreferDisableVFX();

                    // Sound Sync
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Sound Sync:");
                    ImGui.SameLine();
                    _uiSharedService.BooleanToColoredIcon(!isDisableSounds);
                    ImGui.SameLine();
                    ImGui.TextColored(!isDisableSounds ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, 
                        !isDisableSounds ? "Enabled" : "Disabled");
                    ImGui.SameLine(230);
                    if (_uiSharedService.IconTextButton(isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute,
                        isDisableSounds ? "Suggest to enable sound sync" : "Suggest to disable sound sync"))
                    {
                        perm.SetPreferDisableSounds(!perm.IsPreferDisableSounds());
                        _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(2f);
                
                // Animation Sync
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Animation Sync:");
                ImGui.SameLine();
                _uiSharedService.BooleanToColoredIcon(!isDisableAnimations);
                ImGui.SameLine();
                ImGui.TextColored(!isDisableAnimations ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, 
                    !isDisableAnimations ? "Enabled" : "Disabled");
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableAnimations ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop,
                    isDisableAnimations ? "Suggest to enable animation sync" : "Suggest to disable animation sync"))
                {
                    perm.SetPreferDisableAnimations(!perm.IsPreferDisableAnimations());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(2f);
                
                // VFX Sync
                ImGui.AlignTextToFramePadding();
                ImGui.Text("VFX Sync:");
                ImGui.SameLine();
                _uiSharedService.BooleanToColoredIcon(!isDisableVfx);
                ImGui.SameLine();
                    ImGui.TextColored(!isDisableVfx ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, 
                        !isDisableVfx ? "Enabled" : "Disabled");
                    ImGui.SameLine(230);
                    if (_uiSharedService.IconTextButton(isDisableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle,
                        isDisableVfx ? "Suggest to enable vfx sync" : "Suggest to disable vfx sync"))
                    {
                        perm.SetPreferDisableVFX(!perm.IsPreferDisableVFX());
                        _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                    }

                    ImGuiHelpers.ScaledDummy(3f);
                    
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Note: These suggested permissions will be shown to users when joining the Syncshell.");
                    
                    ImGuiHelpers.ScaledDummy(2f);
                }
            }
            permissionTab.Dispose();

            if (_isOwner)
            {
                var ownerTab = ImRaii.TabItem("Owner Settings");
                if (ownerTab)
                {
                    // Password Management Section
                    if (ImGui.CollapsingHeader("Password Management", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGuiHelpers.ScaledDummy(2f);
                        
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("New Password:");
                        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                        var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Passport, "Change Password");
                        var textSize = ImGui.CalcTextSize("New Password:").X;
                        var spacing = ImGui.GetStyle().ItemSpacing.X;

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(availableWidth - buttonSize - textSize - spacing * 2);
                        ImGui.InputTextWithHint("##changepw", "Min 10 characters", ref _newPassword, 50);
                        ImGui.SameLine();
                        using (ImRaii.Disabled(_newPassword.Length < 10))
                        {
                            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Passport, "Change Password"))
                            {
                                _pwChangeSuccess = _apiController.GroupChangePassword(new GroupPasswordDto(GroupFullInfo.Group, _newPassword)).Result;
                                _newPassword = string.Empty;
                            }
                        }
                        UiSharedService.AttachToolTip("Password requires to be at least 10 characters long. This action is irreversible.");

                        if (!_pwChangeSuccess)
                        {
                            UiSharedService.ColorTextWrapped("Failed to change the password. Password requires to be at least 10 characters long.", ImGuiColors.DalamudYellow);
                        }

                        ImGuiHelpers.ScaledDummy(2f);
                    }

                    // Syncshell Identity Section
                    if (ImGui.CollapsingHeader("Syncshell Identity", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGuiHelpers.ScaledDummy(2f);

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("Syncshell Alias:");
                        var aliasAvailableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                        var aliasButtonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Tag, "Set Alias");
                        var aliasTextSize = ImGui.CalcTextSize("Syncshell Alias:").X;
                        var aliasSpacing = ImGui.GetStyle().ItemSpacing.X;

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(aliasAvailableWidth - aliasButtonSize - aliasTextSize - aliasSpacing * 2);
                        ImGui.InputTextWithHint("##changealias", "3-50 characters (leave empty to clear)", ref _newAlias, 50);
                        ImGui.SameLine();
                        using (ImRaii.Disabled(_newAlias.Length > 0 && _newAlias.Length < 3))
                        {
                            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Tag, "Set Alias"))
                            {
                                var aliasToSet = string.IsNullOrWhiteSpace(_newAlias) ? null : _newAlias.Trim();
                                _aliasChangeSuccess = _apiController.GroupSetAlias(new GroupAliasDto(GroupFullInfo.Group, aliasToSet)).Result;
                                if (_aliasChangeSuccess)
                                {
                                 _newAlias = string.Empty;
                             }
                         }
                     }
                     UiSharedService.AttachToolTip("Set a custom alias for your syncshell (3-50 characters). Leave empty to clear the alias.");

                     if (!_aliasChangeSuccess)
                     {
                         UiSharedService.ColorTextWrapped("Failed to change the alias. Alias must be 3-50 characters long and unique.", ImGuiColors.DalamudYellow);
                     }

                     var currentAlias = GroupFullInfo.Group.Alias;
                     if (!string.IsNullOrEmpty(currentAlias))
                     {
                         ImGui.TextColored(ImGuiColors.ParsedGreen, $"Current alias: {currentAlias}");
                     }
                     else
                     {
                         ImGui.TextColored(ImGuiColors.DalamudGrey, "No alias set");
                     }

                     ImGuiHelpers.ScaledDummy(2f);
                 }

                 // Danger Zone Section
                 if (ImGui.CollapsingHeader("Danger Zone", ImGuiTreeNodeFlags.DefaultOpen))
                 {
                     ImGuiHelpers.ScaledDummy(2f);

                     if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Delete Syncshell") && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                     {
                         IsOpen = false;
                         _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
                     }
                     UiSharedService.AttachToolTip("Hold CTRL and Shift and click to delete this Syncshell." + Environment.NewLine + "WARNING: this action is irreversible.");
                     
                     ImGuiHelpers.ScaledDummy(2f);
                 }
                 
                 // Area Binding Section
                 if (ImGui.CollapsingHeader("Area Binding", ImGuiTreeNodeFlags.DefaultOpen))
                 {
                     ImGuiHelpers.ScaledDummy(2f);
                     
                     UiSharedService.TextWrapped("Bind this syncshell to a specific location. Users will automatically join when entering the area.");
                     ImGuiHelpers.ScaledDummy(3f);
                     
                     if (_isLoadingAreaBinding)
                     {
                         ImGui.TextUnformatted("Loading area binding settings...");
                     }
                     else
                     {
                         DrawAreaBindingControls();
                     }

                     ImGuiHelpers.ScaledDummy(2f);
                 }
                 
                 // Owned Properties Section
                 if (ImGui.CollapsingHeader("Owned Properties", ImGuiTreeNodeFlags.DefaultOpen))
                 {
                     ImGuiHelpers.ScaledDummy(2f);
                     
                     UiSharedService.TextWrapped("Manage your owned housing properties. Only properties in this list can be used for area syncshells.");
                     ImGuiHelpers.ScaledDummy(3f);
                     
                     DrawOwnedPropertiesControls();

                     ImGuiHelpers.ScaledDummy(2f);
                 }
                }
                ownerTab.Dispose();
                
                // Welcome Page Tab (only for owners/moderators)
                var welcomePageTab = ImRaii.TabItem("Welcome Page");
                if (welcomePageTab)
                {
                    DrawWelcomePageControls();
                }
                welcomePageTab.Dispose();
            }
        }
    }
    
    private void DrawAreaBindingControls()
    {
        // Current location display (for reference only, don't overwrite bound location)
        var currentMapLocation = _dalamudUtilService.GetMapData();
        
        ImGui.TextUnformatted("Current Location:");
        ImGui.SameLine();
        var loc = currentMapLocation;
        
        // Get names for display
        var serverName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)loc.ServerId, out var sName) ? sName : $"Server {loc.ServerId}";
        
        // For territory, get only the region name (first part before " - ")
        var territoryName = "Territory " + loc.TerritoryId;
        if (_dalamudUtilService.TerritoryData.Value.TryGetValue(loc.TerritoryId, out var tName))
        {
            var regionName = tName.Split(" - ")[0]; // Take only the region part
            territoryName = regionName;
        }
        
        // For map, get the specific location name (everything after the region)
        var mapName = "Map " + loc.MapId;
        if (_dalamudUtilService.MapData.Value.TryGetValue(loc.MapId, out var mData))
        {
            var fullMapName = mData.MapName; // This is the correct property from the tuple
            var parts = fullMapName.Split(" - ");
            if (parts.Length > 1)
            {
                // Skip the region part and join the rest (could be "PlaceName" or "PlaceName - PlaceNameSub")
                mapName = string.Join(" - ", parts.Skip(1));
            }
            else
            {
                mapName = fullMapName;
            }
        }
        
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"Server: {serverName}");
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"Territory: {territoryName}");
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"Map: {mapName}");
        if (loc.WardId > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Ward: {loc.WardId}");
        }
        if (loc.HouseId > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"House: {loc.HouseId}");
        }
        
        ImGuiHelpers.ScaledDummy(2f);
        
        // Area binding toggle
        var isInHousingArea = _housingOwnershipService.IsHousingArea(loc);
        
        if (!isInHousingArea)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
            ImGui.TextUnformatted("⚠ Area syncshells are only available in housing areas (plots, houses, or rooms)");
            ImGui.PopStyleColor();
            ImGuiHelpers.ScaledDummy(1f);
        }
        
        ImGui.BeginDisabled(!isInHousingArea);
        if (ImGui.Checkbox("Enable Area Binding", ref _areaBindingEnabled))
        {
            if (!_areaBindingEnabled && _currentAreaBinding != null)
            {
                // Remove area binding
                _ = RemoveAreaBinding();
            }
        }
        ImGui.EndDisabled();
        
        if (!isInHousingArea)
        {
            UiSharedService.AttachToolTip("Area syncshells can only be created in housing areas. Use city syncshells or non-area-bound syncshells for other locations.");
        }
        
        if (_areaBindingEnabled)
        {
            ImGuiHelpers.ScaledDummy(2f);
            
            // Global settings section
            ImGui.TextUnformatted("Global Settings:");
            
            if (ImGui.Checkbox("Auto Broadcast Enabled", ref _autoBroadcastEnabled))
            {
                _ = UpdateAreaBindingSettings();
            }
            UiSharedService.AttachToolTip("Automatically broadcast this syncshell to users entering any bound area");
            
            if (ImGui.Checkbox("Require Owner Presence", ref _requireOwnerPresence))
            {
                _ = UpdateAreaBindingSettings();
            }
            UiSharedService.AttachToolTip("Only allow auto-join when the syncshell owner is present in the area");
            
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Max Auto Join Users", ref _maxAutoJoinUsers))
            {
                _maxAutoJoinUsers = Math.Max(1, Math.Min(100, _maxAutoJoinUsers));
                _ = UpdateAreaBindingSettings();
            }
            UiSharedService.AttachToolTip("Maximum number of users that can auto-join this syncshell");
            
            if (ImGui.Checkbox("Notify on User Enter", ref _notifyOnUserEnter))
            {
                _ = UpdateAreaBindingSettings();
            }
            
            if (ImGui.Checkbox("Notify on User Leave", ref _notifyOnUserLeave))
            {
                _ = UpdateAreaBindingSettings();
            }
            
            ImGui.SetNextItemWidth(300);
            if (ImGui.InputTextWithHint("Custom Join Message", "Optional message shown when users auto-join", ref _customJoinMessage, 200))
            {
                _ = UpdateAreaBindingSettings();
            }
            
            ImGuiHelpers.ScaledDummy(2f);
            
            // Rules and consent section
            ImGui.Separator();
            ImGui.TextUnformatted("Rules and Consent:");
            
            if (ImGui.Checkbox("Require Rules Acceptance", ref _requireRulesAcceptance))
            {
                _ = UpdateAreaBindingSettings();
            }
            UiSharedService.AttachToolTip("Require users to accept rules before auto-joining");
            
            if (_requireRulesAcceptance)
            {
                ImGui.SetNextItemWidth(100);
                if (ImGui.InputInt("Rules Version", ref _rulesVersion))
                {
                    _rulesVersion = Math.Max(1, _rulesVersion);
                    _ = UpdateAreaBindingSettings();
                }
                UiSharedService.AttachToolTip("Increment this when rules change to require re-acceptance");
                
                // Structured rules editing
                ImGui.TextUnformatted("Rules:");
                
                // Display existing rules with remove buttons
                for (int i = 0; i < _rulesList.Count; i++)
                {
                    ImGui.PushID($"rule_{i}");
                    
                    // Rule number and content
                    ImGui.Text($"{i + 1}.");
                    ImGui.SameLine();
                    
                    var ruleText = _rulesList[i];
                    ImGui.SetNextItemWidth(-80);
                    if (ImGui.InputText("##rule_content", ref ruleText, 500))
                    {
                        _rulesList[i] = ruleText;
                        _rulesChanged = true;
                    }
                    
                    // Remove button
                    ImGui.SameLine();
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                    {
                        _rulesList.RemoveAt(i);
                        _rulesChanged = true;
                        i--; // Adjust index after removal
                    }
                    UiSharedService.AttachToolTip("Remove this rule");
                    
                    ImGui.PopID();
                }
                
                // Add new rule button
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, "Add Rule"))
                {
                    _rulesList.Add("New rule");
                    _rulesChanged = true;
                }
                
                ImGuiHelpers.ScaledDummy(1f);
                
                // Save button (only show if changes were made)
                if (_rulesChanged)
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save Rules"))
                    {
                        _ = SaveRules();
                    }
                    UiSharedService.AttachToolTip("Save rules and increment version");
                    
                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudYellow, "Unsaved changes");
                }
                
                // Legacy multiline input (for reference/backup)
                if (ImGui.CollapsingHeader("Advanced: Raw Rules Text"))
                {
                    UpdateRulesText(); // Ensure text is up to date
                    if (ImGui.InputTextMultiline("Join Rules", ref _joinRules, 2000, new(0, 100)))
                    {
                        ParseRulesIntoList();
                        _rulesChanged = true;
                    }
                    UiSharedService.AttachToolTip("Direct text editing - changes will be parsed into structured rules");
                }
            }
            
            ImGuiHelpers.ScaledDummy(2f);
            
            // Bound locations section
            ImGui.TextUnformatted("Bound Locations:");
            
            // Add new location section
            ImGui.Separator();
            ImGui.TextUnformatted("Add New Location:");
            
            // Matching mode for new location
            var availableMatchingModes = GetAvailableMatchingModes(currentMapLocation);
            
            var currentModeIndex = (int)_newLocationMatchingMode;
            // Ensure current mode is available, otherwise reset to first available
            if (!availableMatchingModes.Contains(_newLocationMatchingMode))
            {
                _newLocationMatchingMode = availableMatchingModes.First();
                currentModeIndex = (int)_newLocationMatchingMode;
            }
            
            var matchingModeNames = availableMatchingModes.Select(mode => GetMatchingModeDisplayName(mode)).ToArray();
            var availableModeIndices = availableMatchingModes.Select(mode => (int)mode).ToArray();
            
            // Find the index in the filtered array
            var displayIndex = Array.IndexOf(availableModeIndices, currentModeIndex);
            if (displayIndex == -1) displayIndex = 0;
            
            ImGui.SetNextItemWidth(400);
            if (ImGui.Combo("Matching Mode", ref displayIndex, matchingModeNames, matchingModeNames.Length))
            {
                _newLocationMatchingMode = (AreaMatchingMode)availableModeIndices[displayIndex];
            }
            UiSharedService.AttachToolTip("Choose how precisely the location should match for users to auto-join:\n" +
                                        "• Exact Match: Users must be in the exact same location\n" +
                                        "• Territory Only: Users can be anywhere in the same zone\n" +
                                        "• Server & Territory: Same server and zone (for cross-world compatibility)\n" +
                                        "• Housing options: Various levels of housing area matching");
            
            // Optional location name
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("Location Name", "Optional name for this location", ref _newLocationName, 100);
            
            // Auto-suggest location name based on current location
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Magic))
            {
                _newLocationName = LocationDisplayService.GetAutoLocationName(currentMapLocation);
            }
            UiSharedService.AttachToolTip("Auto-generate location name based on current location");
            
            // Add current location button
            bool isInDuty = _dalamudUtilService.IsInDuty;
            
            // Disable button and show different tooltip when in duty
            if (isInDuty)
            {
                ImGui.BeginDisabled();
            }
            
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.MapMarkerAlt, "Add Current Location"))
            {
                // Check if player is in a duty
                if (isInDuty)
                {
                    Mediator.Publish(new NotificationMessage("Cannot create area-bound syncshell", "Area-bound syncshells cannot be created while in a duty.", NotificationType.Error, TimeSpan.FromSeconds(5)));
                }
                else
                {
                    _ = AddLocationBinding(currentMapLocation);
                }
            }
            
            if (isInDuty)
            {
                ImGui.EndDisabled();
                UiSharedService.AttachToolTip("Cannot add locations while in duties or instances");
            }
            else
            {
                UiSharedService.AttachToolTip("Add your current location as a bound area");
            }
            
            ImGuiHelpers.ScaledDummy(2f);
            
            // Display existing bound locations
            if (_boundAreas.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextUnformatted("Current Bound Locations:");
                
                for (int i = 0; i < _boundAreas.Count; i++)
                {
                    var boundArea = _boundAreas[i];
                    var location = boundArea.Location;
                    
                    ImGui.PushID(i);
                    
                    // Location info
                    var locationText = LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService);
                    
                    ImGui.TextColored(ImGuiColors.ParsedGreen, locationText);
                    
                    // Location name and matching mode
                    ImGui.SameLine();
                    var descriptiveName = LocationDisplayService.GetLocationDescriptiveName(location, boundArea.MatchingMode);
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"({descriptiveName})");
                    
                    if (!string.IsNullOrEmpty(boundArea.LocationName))
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ImGuiColors.DalamudYellow, $"[{boundArea.LocationName}]");
                    }
                    
                    // Remove button
                    ImGui.SameLine();
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                    {
                        _ = RemoveLocationBinding(i);
                    }
                    UiSharedService.AttachToolTip("Remove this location binding");
                    
                    ImGui.PopID();
                }
                
                ImGuiHelpers.ScaledDummy(2f);
                
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Unlink, "Remove All Area Bindings"))
                {
                    _ = RemoveAreaBinding();
                }
                UiSharedService.AttachToolTip("Remove all area bindings from this syncshell");
            }
        }
    }
    
    private async Task AddLocationBinding(LocationInfo location)
    {
        try
        {
            // Check if location is a housing area first
            if (!_housingOwnershipService.IsHousingArea(location))
            {
                var message = "Area syncshells can only be created in housing areas (plots, houses, or rooms). For other areas, please use city syncshells or create a non-area-bound syncshell.";
                
                _logger.LogWarning("Attempted to create area syncshell outside housing area: {Location}", location);
                
                // Show error message to user
                Mediator.Publish(new NotificationMessage("Invalid Location", message, NotificationType.Error));
                
                return;
            }
            
            // Check ownership before allowing area binding
            var ownershipResult = await _housingOwnershipService.VerifyOwnershipAsync(location);
            
            if (ownershipResult.IsOwner != true)
            {
                var message = ownershipResult.IsOwner == false 
                    ? "You cannot create area syncshells on properties you don't own."
                    : "Cannot verify property ownership. Please ensure you are the owner of this property.";
                    
                _logger.LogWarning("Ownership verification failed for location {Location}: {Message}", location, message);
                
                // Show error message to user
                Mediator.Publish(new NotificationMessage("Ownership Verification Failed", message, NotificationType.Error));
                
                return;
            }
            
            // Check if the location is allowed based on outdoor/indoor preferences
            if (!_housingOwnershipService.IsLocationVerifiedAndAllowed(location))
            {
                var locationTypeText = location.IsIndoor ? "indoor" : "outdoor";
                var message = $"Area syncshells are not allowed for this {locationTypeText} location based on your verification preferences. " +
                             "Please update your property verification settings to allow syncshells for this area type.";
                             
                _logger.LogWarning("Location not allowed for area syncshell due to outdoor/indoor preferences: {Location}, IsIndoor: {IsIndoor}", location, location.IsIndoor);
                
                // Show error message to user
                Mediator.Publish(new NotificationMessage("Location Not Allowed", message, NotificationType.Error));
                
                return;
            }
            
            _logger.LogInformation("Ownership verified for location {Location}", location);
            
            // Create new location binding
            var newLocation = new AreaBoundLocationDto
            {
                Id = 0, // Will be set by server
                Location = location,
                MatchingMode = _newLocationMatchingMode,
                LocationName = string.IsNullOrWhiteSpace(_newLocationName) ? null : _newLocationName,
                CreatedAt = DateTime.UtcNow
            };
            
            // Add to local list
            _boundAreas.Add(newLocation);
            
            // Update server
            await UpdateAreaBindingSettings();
            
            // Reset input fields
            _newLocationMatchingMode = AreaMatchingMode.ExactMatch;
            _newLocationName = string.Empty;
            
            _logger.LogDebug("Successfully added location binding for syncshell {GID} at location {Location}", GroupFullInfo.Group.GID, location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add location binding for syncshell {GID}", GroupFullInfo.Group.GID);
        }
    }
    
    private async Task RemoveLocationBinding(int index)
    {
        try
        {
            if (index >= 0 && index < _boundAreas.Count)
            {
                _boundAreas.RemoveAt(index);
                await UpdateAreaBindingSettings();
                
                _logger.LogDebug("Successfully removed location binding at index {Index} for syncshell {GID}", index, GroupFullInfo.Group.GID);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove location binding for syncshell {GID}", GroupFullInfo.Group.GID);
        }
    }
    
    private async Task UpdateAreaBindingSettings()
    {
        if (_boundAreas.Count == 0) return;
        
        try
        {
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
            
            await _apiController.GroupSetAreaBinding(dto);
            _currentAreaBinding = dto;
            
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
            await _apiController.GroupRemoveAreaBinding(new(GroupFullInfo.Group));
            _currentAreaBinding = null;
            _areaBindingEnabled = false;
            _isAreaBound = false;
            _boundAreas.Clear();
            
            // Reset UI variables to defaults
            _autoBroadcastEnabled = true;
            _requireOwnerPresence = false;
            _maxAutoJoinUsers = 50;
            _notifyOnUserEnter = true;
            _notifyOnUserLeave = true;
            _customJoinMessage = string.Empty;
            _newLocationMatchingMode = AreaMatchingMode.ExactMatch;
            _newLocationName = string.Empty;
            
            // Reset rules settings to defaults
            _joinRules = string.Empty;
            _rulesVersion = 1;
            _requireRulesAcceptance = false;
            _rulesList.Clear();
            _rulesChanged = false;
            
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
            var match = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"^\d+\.\s*(.*)$");
            if (match.Success)
            {
                _rulesList.Add(match.Groups[1].Value);
            }
            else
            {
                _rulesList.Add(trimmedLine);
            }
        }
        _rulesChanged = false;
    }
    
    private void UpdateRulesText()
    {
        if (_rulesList.Count == 0)
        {
            _joinRules = string.Empty;
        }
        else
        {
            var numberedRules = _rulesList.Select((rule, index) => $"{index + 1}. {rule}");
            _joinRules = string.Join("\n", numberedRules);
        }
    }
    
    private async Task SaveRules()
    {
        if (!_rulesChanged) return;
        
        _rulesVersion++;
        UpdateRulesText();
        _rulesChanged = false;
        
        await UpdateAreaBindingSettings();
        
        _logger.LogDebug("Rules saved with version {Version} for syncshell {GID}", _rulesVersion, GroupFullInfo.Group.GID);
    }
    
    private async Task LoadCurrentWelcomePage()
    {
        _isLoadingWelcomePage = true;
        try
        {
            _welcomePage = await _apiController.GroupGetWelcomePage(new GroupDto(GroupFullInfo.Group)).ConfigureAwait(false);
            
            if (_welcomePage != null)
            {
                _welcomeText = _welcomePage.WelcomeText ?? string.Empty;
                _welcomeImageBase64 = _welcomePage.WelcomeImageBase64 ?? string.Empty;
                _imageFileName = _welcomePage.ImageFileName ?? string.Empty;
                _imageContentType = _welcomePage.ImageContentType ?? string.Empty;
                _imageSize = _welcomePage.ImageSize ?? 0;
                _welcomePageEnabled = _welcomePage.IsEnabled;
                _showOnJoin = _welcomePage.ShowOnJoin;
                _showOnAreaBoundJoin = _welcomePage.ShowOnAreaBoundJoin;
                
                // Create texture from base64 image data if available
                if (!string.IsNullOrEmpty(_welcomeImageBase64))
                {
                    try
                    {
                        var imageData = Convert.FromBase64String(_welcomeImageBase64);
                        _welcomeImageTexture?.Dispose();
                        _welcomeImageTexture = _uiSharedService.LoadImage(imageData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load welcome page image texture");
                        _welcomeImageTexture = null;
                    }
                }
            }
            else
            {
                // Reset to defaults if no welcome page exists
                _welcomeText = string.Empty;
                _welcomeImageBase64 = string.Empty;
                _imageFileName = string.Empty;
                _imageContentType = string.Empty;
                _imageSize = 0;
                _welcomePageEnabled = false;
                _showOnJoin = true;
                _showOnAreaBoundJoin = true;
                _welcomeImageTexture?.Dispose();
                _welcomeImageTexture = null;
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
            ImGui.TextUnformatted("Loading welcome page settings...");
            return;
        }
        
        // Welcome Page Configuration Section
        if (ImGui.CollapsingHeader("Welcome Page Configuration", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGuiHelpers.ScaledDummy(2f);
            
            UiSharedService.TextWrapped("Configure a welcome page that will be shown to users when they join your syncshell.");
            ImGuiHelpers.ScaledDummy(3f);
            
            // Enable/disable welcome page
            if (ImGui.Checkbox("Enable Welcome Page", ref _welcomePageEnabled))
            {
                _ = SaveWelcomePage();
            }
            
            ImGuiHelpers.ScaledDummy(2f);
        }
        
        if (_welcomePageEnabled)
        {
            // Display Settings Section
            if (ImGui.CollapsingHeader("Display Settings", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGuiHelpers.ScaledDummy(2f);
                
                if (ImGui.Checkbox("Show on regular join", ref _showOnJoin))
                {
                    _ = SaveWelcomePage();
                }
                UiSharedService.AttachToolTip("Show welcome page when users manually join the syncshell");
                
                if (ImGui.Checkbox("Show on area-bound join", ref _showOnAreaBoundJoin))
                {
                    _ = SaveWelcomePage();
                }
                UiSharedService.AttachToolTip("Show welcome page when users auto-join via area binding");
                
                ImGuiHelpers.ScaledDummy(2f);
            }
            
            // Welcome Message Section
            if (ImGui.CollapsingHeader("Welcome Message", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGuiHelpers.ScaledDummy(2f);
                
                UiSharedService.AttachToolTip("Supports Markdown formatting:\n**bold**, *italic*, <color=#FF0000>colored text</color>, `code`, # headers\n\nUse the buttons below or keyboard shortcuts:\nCtrl+B: Bold, Ctrl+I: Italic, Ctrl+K: Code");
                
                // Markdown Formatter button and Live Preview button
                if (ImGui.Button("Markdown Formatter"))
                {
                    MarkdownFormatterPopup.Open();
                }
                UiSharedService.AttachToolTip("Open advanced Markdown formatter with live preview");
                
                ImGui.SameLine();
                if (ImGui.Button("Live Preview"))
                {
                    Mediator.Publish(new OpenWelcomePageLivePreviewMessage(GroupFullInfo, _welcomeText, _welcomeImageTexture));
                }
                UiSharedService.AttachToolTip("Open live preview of the welcome message");
                
                ImGuiHelpers.ScaledDummy(2f);
                
                // Keyboard shortcuts are now handled by the Markdown Formatter Popup
                bool textChanged = false;
                
                // Detect if text is selected using keyboard shortcuts
                var io = ImGui.GetIO();
                if (ImGui.IsItemFocused() && io.KeyCtrl)
                {
                    if (ImGui.IsKeyPressed(ImGuiKey.A))
                    {
                        _hasSelection = !string.IsNullOrEmpty(_welcomeText);
                    }
                    else if (ImGui.IsKeyPressed(ImGuiKey.C) || ImGui.IsKeyPressed(ImGuiKey.X))
                    {
                        _hasSelection = true; // Assume text is selected when copying/cutting
                    }
                }
                
                // Store original text for comparison
                string originalText = _welcomeText;
                
                // Input text field
                if (ImGui.InputTextMultiline("##welcometext", ref _welcomeText, 2000, new Vector2(0, 100)) || textChanged)
                {
                    _ = SaveWelcomePage();
                    
                    // Update live preview if it's open and text has changed
                    if (originalText != _welcomeText)
                    {
                        Mediator.Publish(new UpdateWelcomePageLivePreviewMessage(_welcomeText, _welcomeImageTexture));
                    }
                }
                
                ImGuiHelpers.ScaledDummy(2f);
            }
            
            // Welcome Image Section
            if (ImGui.CollapsingHeader("Welcome Image", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGuiHelpers.ScaledDummy(2f);
                
                if (!string.IsNullOrEmpty(_imageFileName))
                {
                    ImGui.TextColored(ImGuiColors.ParsedGreen, $"Current image: {_imageFileName}");
                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudGrey, $"({_imageSize} bytes)");
                    
                    // Display the actual image if available
                    if (_welcomeImageTexture != null)
                    {
                        var maxImageSize = new Vector2(300, 200); // Max display size
                        var imageSize = new Vector2(_welcomeImageTexture.Width, _welcomeImageTexture.Height);
                        
                        // Scale image to fit within max size while maintaining aspect ratio
                        var scale = Math.Min(maxImageSize.X / imageSize.X, maxImageSize.Y / imageSize.Y);
                        if (scale > 1.0f) scale = 1.0f; // Don't upscale
                        
                        var displaySize = imageSize * scale;
                        ImGui.Image(_welcomeImageTexture.Handle, displaySize);
                    }
                    
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Remove Image"))
                {
                    _welcomeImageBase64 = string.Empty;
                    _imageFileName = string.Empty;
                    _imageContentType = string.Empty;
                    _imageSize = 0;
                    _welcomeImageTexture?.Dispose();
                    _welcomeImageTexture = null;
                    _ = SaveWelcomePage();
                }
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "No image selected");
            }
            
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Upload, "Select Image"))
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
                            
                            // Create texture for immediate display
                            try
                            {
                                _welcomeImageTexture?.Dispose();
                                _welcomeImageTexture = _uiSharedService.LoadImage(fileContent);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to create texture from uploaded image");
                                _welcomeImageTexture = null;
                            }
                            
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
            UiSharedService.AttachToolTip("Select an image file to display on the welcome page (PNG, JPG, GIF supported)");
            
            ImGuiHelpers.ScaledDummy(2f);
        }
        }
        
        if (!_welcomePageSaveSuccess)
        {
            UiSharedService.ColorTextWrapped("Failed to save welcome page settings. Please try again.", ImGuiColors.DalamudRed);
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
            _welcomeImageTexture?.Dispose();
        }
        base.Dispose(disposing);
    }
    
    private List<AreaMatchingMode> GetAvailableMatchingModes(LocationInfo location)
    {
        var availableModes = new List<AreaMatchingMode>();
        
        // Always available modes
        availableModes.Add(AreaMatchingMode.ExactMatch);
        availableModes.Add(AreaMatchingMode.TerritoryOnly);
        availableModes.Add(AreaMatchingMode.ServerAndTerritory);
        
        // Housing-specific modes - only show if player is in a housing area
        if (location.WardId > 0)
        {
            availableModes.Add(AreaMatchingMode.HousingWardOnly);
            
            // Plot-specific modes - only show if player is in a specific plot
            if (location.HouseId > 0)
            {
                availableModes.Add(AreaMatchingMode.HousingPlotOnly);
                availableModes.Add(AreaMatchingMode.HousingPlotOutdoor);
                availableModes.Add(AreaMatchingMode.HousingPlotIndoor);
            }
        }
        
        return availableModes;
    }
    
    private string GetMatchingModeDisplayName(AreaMatchingMode mode)
    {
        return mode switch
        {
            AreaMatchingMode.ExactMatch => "Exact Match - Precise location (map, ward, house, room)",
            AreaMatchingMode.TerritoryOnly => "Territory Only - Anywhere in the same zone/area",
            AreaMatchingMode.ServerAndTerritory => "Server & Territory - Same server and zone/area",
            AreaMatchingMode.HousingWardOnly => "Housing Ward Only - Anywhere in the same housing ward",
            AreaMatchingMode.HousingPlotOnly => "Housing Plot Only - Specific housing plot (indoor & outdoor)",
            AreaMatchingMode.HousingPlotOutdoor => "Housing Plot Outdoor - Only outdoor areas of the plot",
            AreaMatchingMode.HousingPlotIndoor => "Housing Plot Indoor - Only inside the house",
            _ => "Unknown"
        };
    }
    
    private void DrawOwnedPropertiesControls()
    {
        var currentLocation = _dalamudUtilService.GetMapData();
        var ownedProperties = _housingOwnershipService.GetOwnedProperties();
        var verifiedProperties = GetCachedVerifiedProperties();
        
        // Current location display
        ImGui.TextUnformatted("Current Location:");
        var locationText = LocationDisplayService.GetLocationDisplayTextWithNames(currentLocation, _dalamudUtilService);
        ImGui.TextColored(ImGuiColors.DalamudYellow, locationText);
        
        ImGuiHelpers.ScaledDummy(2f);
        
        // One-time ownership verification section
        bool isCurrentLocationHousing = currentLocation.WardId > 0;
        bool isCurrentLocationAlreadyVerified = verifiedProperties.Any(prop => LocationsMatch(prop.Location, currentLocation));
        
        if (isCurrentLocationHousing && !isCurrentLocationAlreadyVerified)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Verify Ownership (One-Time Setup):");
            UiSharedService.TextWrapped("To use this housing area for area syncshells, verify your ownership once by entering the housing menu, then choose your preferences and click the button below.");
            
            ImGuiHelpers.ScaledDummy(1f);
            
            // Outdoor/Indoor preference checkboxes - only show when on plot (outdoor)
            // If user is in a room (indoor), don't show any checkboxes and don't set preferences
            // Rooms don't need outdoor/indoor preferences since they are always indoor by nature
            if (currentLocation.IsIndoor)
            {
                // For rooms, we don't set any preferences - they are handled differently
                // No checkboxes shown when in a room
            }
            else
            {
                // User is on plot (outdoor), show both options
                ImGui.Checkbox("Allow Outdoor Syncshells (Plot)", ref _verificationAllowOutdoor);
                UiSharedService.AttachToolTip("Allow area syncshells to be created for the outdoor plot area");
                
                ImGui.Checkbox("Allow Indoor Syncshells (House/Room)", ref _verificationAllowIndoor);
                UiSharedService.AttachToolTip("Allow area syncshells to be created for indoor house/room areas");
            }
            
            ImGuiHelpers.ScaledDummy(1f);
            
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.CheckCircle, "Verify Ownership"))
            {
                if (currentLocation.IsIndoor)
                {
                    // For rooms, verify without outdoor/indoor preferences
                    _ = PerformOwnershipVerificationForRoom(currentLocation);
                }
                else
                {
                    // For plots, use the preference-based verification
                    _ = PerformOwnershipVerificationWithPreferences(currentLocation, _verificationAllowOutdoor, _verificationAllowIndoor);
                }
            }
            UiSharedService.AttachToolTip("Verify ownership of this property by entering the housing menu first, then clicking this button");
            
            ImGuiHelpers.ScaledDummy(2f);
        }
        
        // Display verified properties
        if (verifiedProperties.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Your Verified Properties:");
            
            for (int i = 0; i < verifiedProperties.Count; i++)
            {
                var property = verifiedProperties[i];
                
                ImGui.PushID($"verified_property_{i}");
                
                // Property info
                var propertyText = LocationDisplayService.GetLocationDisplayTextWithNames(property.Location, _dalamudUtilService);
                ImGui.TextColored(ImGuiColors.ParsedGreen, propertyText);
                
                // Show outdoor/indoor preferences
                ImGui.SameLine();
                var preferencesText = "";
                if (property.AllowOutdoor && property.AllowIndoor)
                    preferencesText = "(Outdoor & Indoor)";
                else if (property.AllowOutdoor)
                    preferencesText = "(Outdoor Only)";
                else if (property.AllowIndoor)
                    preferencesText = "(Indoor Only)";
                else
                    preferencesText = "(None - Invalid)";
                    
                ImGui.TextColored(ImGuiColors.DalamudGrey, preferencesText);
                
                // Edit preferences button - only show for plots, not for rooms (rooms are always indoor)
                if (!property.Location.IsIndoor)
                {
                    ImGui.SameLine();
                    if (_uiSharedService.IconButton(FontAwesomeIcon.Edit))
                    {
                        // Toggle edit mode for this property
                        // For now, we'll just show a simple toggle
                        bool newAllowOutdoor = property.AllowOutdoor;
                        bool newAllowIndoor = property.AllowIndoor;
                        
                        // Simple toggle logic - cycle through combinations
                        if (property.AllowOutdoor && property.AllowIndoor)
                        {
                            newAllowOutdoor = true;
                            newAllowIndoor = false;
                        }
                        else if (property.AllowOutdoor && !property.AllowIndoor)
                        {
                            newAllowOutdoor = false;
                            newAllowIndoor = true;
                        }
                        else if (!property.AllowOutdoor && property.AllowIndoor)
                        {
                            newAllowOutdoor = true;
                            newAllowIndoor = true;
                        }
                        
                        // Update UI state immediately for instant visual feedback
                        var key = $"{property.Location.TerritoryId}_{property.Location.WardId}_{property.Location.HouseId}";
                        _uiPropertyStates[key] = (newAllowOutdoor, newAllowIndoor);
                        
                        // Send update to server in background (fire and forget)
                        Task.Run(() =>
                        {
                            try
                            {
                                _housingOwnershipService.UpdateVerifiedPropertyPreferences(
                                    property.Location, 
                                    newAllowOutdoor, 
                                    newAllowIndoor, 
                                    property.PreferOutdoorSyncshells, 
                                    property.PreferIndoorSyncshells);
                                _logger.LogDebug("Property preferences updated on server for {Location}", property.Location);
                             }
                             catch (Exception ex)
                             {
                                 _logger.LogError(ex, "Failed to update property preferences on server for {Location}", property.Location);
                            }
                        });
                    }
                    UiSharedService.AttachToolTip("Click to cycle through outdoor/indoor preferences: Both → Outdoor Only → Indoor Only → Both");
                }
                
                // Remove button - requires Ctrl key to prevent accidental deletion
                ImGui.SameLine();
                if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                {
                    if (ImGui.GetIO().KeyCtrl)
                    {
                        // Mark property as deleted in UI immediately (hide it)
                        var key = $"{property.Location.TerritoryId}_{property.Location.WardId}_{property.Location.HouseId}";
                        _deletedPropertyKeys.Add(key);
                        _uiPropertyStates.Remove(key);
                        
                        // Send removal to server in background
                        Task.Run(() =>
                        {
                            try
                            {
                                _housingOwnershipService.RemoveVerifiedOwnedProperty(property.Location);
                                _logger.LogDebug("Property removed from server for {Location}", property.Location);
                             }
                             catch (Exception ex)
                             {
                                 _logger.LogError(ex, "Failed to remove property from server for {Location}", property.Location);
                            }
                        });
                    }
                    else
                    {
                        // Show warning that Ctrl is required
                        Mediator.Publish(new NotificationMessage("Ctrl Required", "Hold Ctrl while clicking to delete this property", NotificationType.Warning));
                    }
                }
                UiSharedService.AttachToolTip("Hold Ctrl and click to remove this property from your verified properties list");
                
                ImGui.PopID();
            }
            
            ImGuiHelpers.ScaledDummy(2f);
            
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, "Clear All Verified Properties"))
            {
                if (ImGui.GetIO().KeyCtrl)
                {
                    // Mark all properties as deleted in UI immediately (hide them all)
                    var currentProperties = GetCachedVerifiedProperties();
                    foreach (var property in currentProperties)
                    {
                        var key = $"{property.Location.TerritoryId}_{property.Location.WardId}_{property.Location.HouseId}";
                        _deletedPropertyKeys.Add(key);
                    }
                    _uiPropertyStates.Clear();
                    
                    // Send clear to server in background
                    Task.Run(() =>
                    {
                        try
                        {
                            _housingOwnershipService.ClearAllVerifiedProperties();
                            _logger.LogDebug("All properties cleared from server");
                         }
                         catch (Exception ex)
                         {
                             _logger.LogError(ex, "Failed to clear all properties from server");
                        }
                    });
                }
                else
                {
                    // Show warning that Ctrl is required
                    Mediator.Publish(new NotificationMessage("Ctrl Required", "Hold Ctrl while clicking to clear all properties", NotificationType.Warning));
                }
            }
            UiSharedService.AttachToolTip("Hold Ctrl and click to remove all properties from your verified properties list");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No verified properties yet.");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Verify ownership of your properties to enable area syncshell creation.");
        }
    }
    
    private bool LocationsMatch(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId &&
               loc1.TerritoryId == loc2.TerritoryId &&
               loc1.WardId == loc2.WardId &&
               loc1.HouseId == loc2.HouseId &&
               loc1.RoomId == loc2.RoomId;
    }

    private async Task PerformOwnershipVerificationWithPreferences(LocationInfo location, bool allowOutdoor, bool allowIndoor)
    {
        try
        {
            var verificationResult = await _housingOwnershipService.PerformOneTimeOwnershipVerificationAsync(location);
            
            if (verificationResult.IsOwner)
            {
                // Add the property with the specified preferences
                _housingOwnershipService.AddVerifiedOwnedPropertyWithPreferences(location, allowOutdoor, allowIndoor);
                
                // Refresh UI state to show the newly added property
                InitializeUiStateFromServer();
                
                _logger.LogInformation("Successfully verified ownership for location: {Location} with preferences - Outdoor: {AllowOutdoor}, Indoor: {AllowIndoor}. Reason: {Reason}", 
                    LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService),
                    allowOutdoor, allowIndoor, verificationResult.Reason);
            }
            else
            {
                _logger.LogWarning("Failed to verify ownership for location: {Location}. Reason: {Reason}", 
                    LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService),
                    verificationResult.Reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during ownership verification with preferences for location: {Location}", 
                LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService));
        }
    }

    private async Task PerformOwnershipVerificationForRoom(LocationInfo location)
    {
        try
        {
            var verificationResult = await _housingOwnershipService.PerformOneTimeOwnershipVerificationAsync(location);
            
            if (verificationResult.IsOwner)
            {
                // For rooms, add the property without outdoor/indoor preferences
                // Rooms are always indoor by nature and don't need these settings
                _housingOwnershipService.AddVerifiedOwnedRoom(location);
                
                // Refresh UI state to show the newly added property
                InitializeUiStateFromServer();
                
                _logger.LogInformation("Successfully verified ownership for room: {Location}. Reason: {Reason}", 
                    LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService),
                    verificationResult.Reason);
            }
            else
            {
                _logger.LogWarning("Failed to verify ownership for room: {Location}. Reason: {Reason}", 
                    LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService),
                    verificationResult.Reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during room ownership verification for location: {Location}", 
                LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService));
        }
    }

    private async Task PerformOwnershipVerification(LocationInfo location)
    {
        try
        {
            var verificationResult = await _housingOwnershipService.PerformOneTimeOwnershipVerificationAsync(location);
            
            if (verificationResult.IsOwner)
            {
                _logger.LogInformation("Successfully verified ownership for location: {Location}. Reason: {Reason}", 
                    LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService),
                    verificationResult.Reason);
            }
            else
            {
                _logger.LogWarning("Failed to verify ownership for location: {Location}. Reason: {Reason}", 
                    LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService),
                    verificationResult.Reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during ownership verification for location: {Location}", 
                LocationDisplayService.GetLocationDisplayTextWithNames(location, _dalamudUtilService));
        }
    }
    
    private List<VerifiedHousingProperty> GetCachedVerifiedProperties()
    {
        // Get fresh data from server - no caching to avoid deletion issues
        var serverProperties = _housingOwnershipService.GetVerifiedOwnedProperties();
        
        // Apply UI-only state overrides for immediate visual feedback
        var result = new List<VerifiedHousingProperty>();
        foreach (var property in serverProperties)
        {
            var key = $"{property.Location.TerritoryId}_{property.Location.WardId}_{property.Location.HouseId}";
            
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
            var key = $"{property.Location.TerritoryId}_{property.Location.WardId}_{property.Location.HouseId}";
            if (!_uiPropertyStates.ContainsKey(key))
            {
                _uiPropertyStates[key] = (property.AllowOutdoor, property.AllowIndoor);
            }
        }
    }

}
