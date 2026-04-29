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
    
    // Multi-location support
    private List<AreaBoundLocationDto> _boundAreas = new();
    
    // Alias setting fields
    private string _newAlias = string.Empty;
    private bool _aliasChangeSuccess = true;
    
    // Welcome page fields
    
    private string _welcomeText = string.Empty;
    private string _welcomeImageBase64 = string.Empty;
    
    // Text selection tracking
    
    
    private IDalamudTextureWrap? _welcomeImageTexture = null;
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
    private const int _userListItemsPerPage = 25;
    
    private readonly FileDialogManager _fileDialogManager;
    
    private readonly HousingOwnershipService _housingOwnershipService;
    private List<HousingOwnershipService.DetectedHousingProperty> _detectedHousingProperties = [];
    private Task? _refreshDetectedHousingPropertiesTask;
    
    // UI-only state for property preferences - immediate UI updates
    
    // Tab switching control
    private bool _shouldSelectAreaBindingTab = false;
    private bool _areaBindingTabWasOpen = false;
    private readonly Dictionary<string, (bool AllowOutdoor, bool AllowIndoor)> _uiPropertyStates = new(StringComparer.Ordinal);
    
    // Track properties that have been deleted in the UI (to hide them until server confirms)
    private readonly HashSet<string> _deletedPropertyKeys = new(StringComparer.Ordinal);
    
    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, SpheneMediator mediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, DalamudUtilService dalamudUtilService, GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService, FileDialogManager fileDialogManager, HousingOwnershipService housingOwnershipService)
        : base(logger, mediator, "Syncshell Admin Panel (" + groupFullInfo.GroupAliasOrGID + ")", performanceCollectorService)
    {
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
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

        using (_uiSharedService.UidFont.Push())
            ImGui.TextUnformatted(GroupFullInfo.GroupAliasOrGID);

        ImGui.Separator();
        var perm = GroupFullInfo.GroupPermissions;

        using var tabbar = ImRaii.TabBar("syncshell_tab_" + GroupFullInfo.GID);

        if (tabbar)
        {
            var homeTab = ImRaii.TabItem("Home");
            if (homeTab)
            {
                UiSharedService.ColorText("Home", ImGuiColors.ParsedBlue);
                UiSharedService.ColorTextWrapped("Quick overview and key syncshell controls.", ImGuiColors.DalamudGrey);

                if (_isOwner)
                {
                    var openAreaBindingWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.MapMarkerAlt, "Area Binding");
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - openAreaBindingWidth);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.MapMarkerAlt, "Area Binding"))
                    {
                        _shouldSelectAreaBindingTab = true;
                    }
                }

                ImGuiHelpers.ScaledDummy(0, 6);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(0, 6);

                UiSharedService.ColorText("Syncshell Status", ImGuiColors.ParsedBlue);
                UiSharedService.ColorTextWrapped("Locking disables new invites and prevents new users from joining.", ImGuiColors.DalamudGrey);

                var isInvitesDisabled = perm.IsDisableInvites();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Invites are");
                ImGui.SameLine();
                UiSharedService.ColorText(isInvitesDisabled ? "locked" : "unlocked", isInvitesDisabled ? ImGuiColors.DalamudRed : ImGuiColors.ParsedGreen);
                var lockButtonText = isInvitesDisabled ? "Unlock Syncshell" : "Lock Syncshell";
                var lockButtonWidth = _uiSharedService.GetIconTextButtonSize(isInvitesDisabled ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock, lockButtonText);
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - lockButtonWidth);
                if (_uiSharedService.IconTextButton(isInvitesDisabled ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock, lockButtonText))
                {
                    perm.SetDisableInvites(!isInvitesDisabled);
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(0, 6);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(0, 6);

                UiSharedService.ColorText("Suggested Sync Preferences", ImGuiColors.ParsedBlue);
                UiSharedService.ColorTextWrapped("These defaults are shown to users when they join this syncshell.", ImGuiColors.DalamudGrey);

                var isDisableAnimations = perm.IsPreferDisableAnimations();
                var isDisableSounds = perm.IsPreferDisableSounds();
                var isDisableVfx = perm.IsPreferDisableVFX();

                ImGui.AlignTextToFramePadding();
                ImGui.Text("Sound Sync:");
                ImGui.SameLine();
                _uiSharedService.BooleanToColoredIcon(!isDisableSounds);
                ImGui.SameLine();
                ImGui.TextColored(!isDisableSounds ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, !isDisableSounds ? "Enabled" : "Disabled");
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute,
                        isDisableSounds ? "Suggest to enable sound sync" : "Suggest to disable sound sync"))
                {
                    perm.SetPreferDisableSounds(!perm.IsPreferDisableSounds());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(2f);

                ImGui.AlignTextToFramePadding();
                ImGui.Text("Animation Sync:");
                ImGui.SameLine();
                _uiSharedService.BooleanToColoredIcon(!isDisableAnimations);
                ImGui.SameLine();
                ImGui.TextColored(!isDisableAnimations ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, !isDisableAnimations ? "Enabled" : "Disabled");
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableAnimations ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop,
                        isDisableAnimations ? "Suggest to enable animation sync" : "Suggest to disable animation sync"))
                {
                    perm.SetPreferDisableAnimations(!perm.IsPreferDisableAnimations());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(2f);

                ImGui.AlignTextToFramePadding();
                ImGui.Text("VFX Sync:");
                ImGui.SameLine();
                _uiSharedService.BooleanToColoredIcon(!isDisableVfx);
                ImGui.SameLine();
                ImGui.TextColored(!isDisableVfx ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, !isDisableVfx ? "Enabled" : "Disabled");
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle,
                        isDisableVfx ? "Suggest to enable vfx sync" : "Suggest to disable vfx sync"))
                {
                    perm.SetPreferDisableVFX(!perm.IsPreferDisableVFX());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(0, 6);

                if (_isOwner)
                {
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 6);

                    UiSharedService.ColorText("Owner Settings", ImGuiColors.ParsedBlue);
                    UiSharedService.ColorTextWrapped("Security and identity settings for this syncshell.", ImGuiColors.DalamudGrey);

                    ImGuiHelpers.ScaledDummy(0, 6);
                    UiSharedService.ColorText("Password", ImGuiColors.ParsedBlue);
                    ImGuiHelpers.ScaledDummy(0, 6);

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

                    ImGuiHelpers.ScaledDummy(0, 6);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 6);

                    UiSharedService.ColorText("Identity", ImGuiColors.ParsedBlue);
                    ImGuiHelpers.ScaledDummy(0, 6);

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

                    ImGuiHelpers.ScaledDummy(0, 6);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 6);

                    UiSharedService.ColorText("Danger Zone", ImGuiColors.DalamudRed);
                    UiSharedService.ColorTextWrapped("This action is irreversible.", ImGuiColors.DalamudGrey);
                    ImGuiHelpers.ScaledDummy(0, 6);

                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Delete Syncshell") && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                    {
                        IsOpen = false;
                        _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
                    }
                    UiSharedService.AttachToolTip("Hold CTRL and Shift and click to delete this Syncshell." + Environment.NewLine + "WARNING: this action is irreversible.");
                }
            }
            homeTab.Dispose();

            var invitesTab = ImRaii.TabItem("Invites");
            if (invitesTab)
            {
                UiSharedService.ColorText("Invites", ImGuiColors.ParsedBlue);
                UiSharedService.ColorTextWrapped("Generate one-time invites for new members.", ImGuiColors.DalamudGrey);

                ImGuiHelpers.ScaledDummy(0, 6);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(0, 6);

                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, "Generate Single Invite"))
                {
                    ImGui.SetClipboardText(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1).Result.FirstOrDefault() ?? string.Empty);
                }
                UiSharedService.AttachToolTip("Creates a single-use invite (valid for 24h) and copies it to the clipboard.");

                ImGuiHelpers.ScaledDummy(0, 6);

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Amount");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                ImGui.InputInt("##amountofinvites", ref _multiInvites);
                ImGui.SameLine();
                using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, "Generate " + _multiInvites + " invites"))
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
                UiSharedService.AttachToolTip($"Generate {_multiInvites} one-time invites (2-100 allowed)");

                if (_oneTimeInvites.Any())
                {
                    ImGuiHelpers.ScaledDummy(0, 6);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 6);

                    UiSharedService.ColorText("Generated", ImGuiColors.ParsedBlue);
                    var invites = string.Join(Environment.NewLine, _oneTimeInvites);
                    ImGui.InputTextMultiline("##generated_invites", ref invites, 5000, new(0, 120), ImGuiInputTextFlags.ReadOnly);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Copy, "Copy All"))
                    {
                        ImGui.SetClipboardText(invites);
                    }
                    UiSharedService.AttachToolTip("Copy all generated invites to clipboard");
                    ImGui.SameLine();
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear"))
                    {
                        _oneTimeInvites.Clear();
                    }
                    UiSharedService.AttachToolTip("Clear the generated invites list");
                }
            }
            invitesTab.Dispose();

            var mgmtTab = ImRaii.TabItem("User Management");
            if (mgmtTab)
            {
                UiSharedService.ColorText("User Management", ImGuiColors.ParsedBlue);
                UiSharedService.ColorTextWrapped("Manage members, roles, pins, and bans.", ImGuiColors.DalamudGrey);

                ImGuiHelpers.ScaledDummy(0, 6);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(0, 6);

                // User List Section
                if (true)
                {
                    UiSharedService.ColorText("Users", ImGuiColors.ParsedBlue);
                    ImGuiHelpers.ScaledDummy(0, 6);
                    
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
                    
                    ImGuiHelpers.ScaledDummy(0, 6);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 6);
                }
                
                // Mass Management Section
                if (true)
                {
                    UiSharedService.ColorText("Mass Management", ImGuiColors.ParsedBlue);
                    UiSharedService.ColorTextWrapped("Bulk operations for this syncshell. Hold CTRL when required.", ImGuiColors.DalamudGrey);
                    ImGuiHelpers.ScaledDummy(0, 6);
                    
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
                    
                    ImGuiHelpers.ScaledDummy(0, 6);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 6);
                }
                
                // User Bans Section
                if (true)
                {
                    UiSharedService.ColorText("User Bans", ImGuiColors.ParsedBlue);
                    
                    var banRefreshWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Sync, "Refresh");
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - banRefreshWidth);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, "Refresh"))
                    {
                        _bannedUsers = _apiController.GroupGetBannedUsers(new GroupDto(GroupFullInfo.Group)).Result;
                    }
                    UiSharedService.AttachToolTip("Refresh the banned users list from the server");

                    ImGuiHelpers.ScaledDummy(0, 6);
                    
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
                                using var idScope = ImRaii.PushId(bannedUser.UID);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserCheck, "Unban"))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await _apiController.GroupUnbanUser(bannedUser).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogError(ex, "Failed to unban user {UID}", bannedUser.UID); }
                    });
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
                    
                    ImGuiHelpers.ScaledDummy(0, 6);
                }
            }
            mgmtTab.Dispose();

            if (_isOwner || _isModerator)
            {
                var welcomePageTab = ImRaii.TabItem("Welcome Page");
                if (welcomePageTab)
                {
                    DrawWelcomePageControls();
                }
                welcomePageTab.Dispose();
            }

            if (_isOwner)
            {
                var isAreaBindingOpen = false;
                var areaBindingTab = ImRaii.TabItem("Area Binding", _shouldSelectAreaBindingTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
                if (areaBindingTab)
                {
                    isAreaBindingOpen = true;
                    if (!_areaBindingTabWasOpen)
                    {
                        RefreshDetectedHousingProperties("tab open");
                        _ = LoadCurrentAreaBinding();
                    }

                    _shouldSelectAreaBindingTab = false;

                    UiSharedService.ColorText("Area Binding", ImGuiColors.ParsedBlue);
                    var refreshButtonWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Sync, "Refresh");
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - refreshButtonWidth);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, "Refresh"))
                    {
                        RefreshDetectedHousingProperties("refresh button");
                        _ = LoadCurrentAreaBinding();
                    }

                    UiSharedService.ColorTextWrapped("Bind this syncshell to specific housing locations. Toggle a property, then select which areas should trigger auto-join.", ImGuiColors.DalamudGrey);

                    ImGuiHelpers.ScaledDummy(0, 6);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 6);

                    UiSharedService.ColorText("Housing Properties", ImGuiColors.ParsedBlue);
                    UiSharedService.ColorTextWrapped("Detected properties show what the game reports. Enable a property to manage its area triggers.", ImGuiColors.DalamudGrey);
                    ImGuiHelpers.ScaledDummy(0, 6);

                    DrawOwnedPropertiesControls();
                }
                areaBindingTab.Dispose();
                _areaBindingTabWasOpen = isAreaBindingOpen;
            }
        }
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

        UiSharedService.ColorText("Welcome Page", ImGuiColors.ParsedBlue);
        UiSharedService.ColorTextWrapped("Configure a page that can be shown when users join this syncshell.", ImGuiColors.DalamudGrey);

        ImGuiHelpers.ScaledDummy(0, 6);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0, 6);

        UiSharedService.ColorText("Configuration", ImGuiColors.ParsedBlue);
        if (ImGui.Checkbox("Enable Welcome Page", ref _welcomePageEnabled))
        {
            _ = SaveWelcomePage();
        }

        ImGuiHelpers.ScaledDummy(0, 6);
        
        if (_welcomePageEnabled)
        {
            // Display Settings Section
            if (true)
            {
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(0, 6);
                UiSharedService.ColorText("Display", ImGuiColors.ParsedBlue);
                
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
                
                ImGuiHelpers.ScaledDummy(0, 6);
            }
            
            // Welcome Message Section
            if (true)
            {
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(0, 6);
                UiSharedService.ColorText("Message", ImGuiColors.ParsedBlue);
                
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
                
                // Store original text for comparison
                string originalText = _welcomeText;
                
                // Input text field
                if (ImGui.InputTextMultiline("##welcometext", ref _welcomeText, 2000, new Vector2(0, 100)) || textChanged)
                {
                    _ = SaveWelcomePage();
                    
                    // Update live preview if it's open and text has changed
                    if (!string.Equals(originalText, _welcomeText, StringComparison.Ordinal))
                    {
                        Mediator.Publish(new UpdateWelcomePageLivePreviewMessage(_welcomeText, _welcomeImageTexture));
                    }
                }
                
                ImGuiHelpers.ScaledDummy(0, 6);
            }
            
            // Welcome Image Section
            if (true)
            {
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(0, 6);
                UiSharedService.ColorText("Image", ImGuiColors.ParsedBlue);
                
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
            
            ImGuiHelpers.ScaledDummy(0, 6);
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
    
    
    
    private void DrawOwnedPropertiesControls()
    {
        var verifiedProperties = GetCachedVerifiedProperties();
        
        
        ImGuiHelpers.ScaledDummy(2f);

        if (_refreshDetectedHousingPropertiesTask == null)
        {
            RefreshDetectedHousingProperties("initial load");
        }

        // Unified properties table (detected + active)
        var combined = new Dictionary<string, (string Type, LocationInfo Location, bool IsActive)>(StringComparer.Ordinal);
        foreach (var d in _detectedHousingProperties)
        {
            var loc = d.Property.Location;
            var key = $"{loc.ServerId}_{loc.TerritoryId}_{loc.WardId}_{loc.HouseId}_{loc.RoomId}";
            var src = d.Source;
            if (string.Equals(src, "FreeCompanyEstate", StringComparison.OrdinalIgnoreCase)) src = "Free Company";
            else if (string.Equals(src, "PersonalChambers", StringComparison.OrdinalIgnoreCase)) src = loc.RoomId > 0 ? $"Raum {loc.RoomId}" : "Raum";
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

        var scale = ImGuiHelpers.GlobalScale;
        var availX = ImGui.GetContentRegionAvail().X;
        var leftWidth = MathF.Min(500f * scale, MathF.Max(320f * scale, availX * 0.45f));
        var childHeight = -8f * scale;

        ImGui.BeginChild("housing-properties-left", new Vector2(leftWidth, childHeight), false, ImGuiWindowFlags.NoBackground);
        try
        {
            using var cellPad = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().CellPadding.X, 5f * ImGuiHelpers.GlobalScale));
            var headerBg = new Vector4(ImGuiColors.ParsedBlue.X, ImGuiColors.ParsedBlue.Y, ImGuiColors.ParsedBlue.Z, 0.0f);
            using var tableHeaderBg = ImRaii.PushColor(ImGuiCol.TableHeaderBg, headerBg);
            using var header = ImRaii.PushColor(ImGuiCol.Header, headerBg);
            using var headerHovered = ImRaii.PushColor(ImGuiCol.HeaderHovered, headerBg);
            using var headerActive = ImRaii.PushColor(ImGuiCol.HeaderActive, headerBg);
            using var tableBorderStrong = ImRaii.PushColor(ImGuiCol.TableBorderStrong, new Vector4(0f, 0f, 0f, 0f));
            using (var unified = ImRaii.Table("UnifiedPropertiesTable", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH))
            {
                if (unified)
                {
                    var tableTop = ImGui.GetCursorScreenPos();
                    var tableWidth = ImGui.GetContentRegionAvail().X;
                    ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() * 2.2f);
                    ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.70f);
                    ImGui.TableSetupColumn("Area Binding", ImGuiTableColumnFlags.WidthStretch, 0.25f);
                    ImGui.TableHeadersRow();
                    var bgCol = ImGui.GetColorU32(ImGuiCol.WindowBg);
                    var headerDrawList = ImGui.GetWindowDrawList();
                    var winPos = ImGui.GetWindowPos();
                    var winSize = ImGui.GetWindowSize();
                    ImGui.PushClipRect(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y), false);
                    headerDrawList.AddRectFilled(tableTop, new Vector2(tableTop.X + tableWidth, tableTop.Y + (2f * ImGuiHelpers.GlobalScale)), bgCol);
                    ImGui.PopClipRect();

                    foreach (var entry in combined.Values.OrderBy(v => v.Location.ServerId)
                                 .ThenBy(v => v.Location.TerritoryId).ThenBy(v => v.Location.WardId)
                                 .ThenBy(v => v.Location.HouseId).ThenBy(v => v.Location.RoomId))
                    {
                        var location = entry.Location;
                        var isActive = entry.IsActive;

                        ImGui.PushID($"unified_{location.ServerId}_{location.TerritoryId}_{location.WardId}_{location.HouseId}_{location.RoomId}");
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        var toggled = DrawToggleSwitch("active_toggle", isActive);
                        if (toggled != isActive)
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

                        ImGui.TableNextColumn();
                        var serverName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)location.ServerId, out var sName) ? sName : $"Server {location.ServerId}";
                        var territoryName = "Territory " + location.TerritoryId;
                        if (_dalamudUtilService.TerritoryData.Value.TryGetValue(location.TerritoryId, out var tName))
                        {
                            var parts = tName.Split(" - ", 2);
                            territoryName = parts.Length == 2 ? $"{parts[1]} ({parts[0]})" : tName;
                        }

                        if (isActive)
                        {
                            ImGui.TextColored(ImGuiColors.ParsedGreen, $"{entry.Type} • {serverName}");
                        }
                        else
                        {
                            ImGui.TextUnformatted($"{entry.Type} • {serverName}");
                        }
                        ImGui.TextUnformatted(territoryName);

                        var sizeLabel = _dalamudUtilService.GetHousingPlotSizeLabel(location);
                        var wardText = location.WardId > 0 ? $"Ward {location.WardId}" : "Ward -";
                        var plotText = location.HouseId > 0 ? (location.HouseId == 100 ? "Apt" : $"Plot {location.HouseId}") : "Plot -";
                        var partsLine = new List<string>(4) { wardText, plotText };
                        if (location.RoomId > 0)
                        {
                            partsLine.Add($"Room {location.RoomId}");
                        }
                        partsLine.Add(sizeLabel);
                        ImGui.TextColored(ImGuiColors.DalamudGrey, string.Join(" • ", partsLine));

                        ImGui.TableNextColumn();
                        using (ImRaii.Disabled(!isActive))
                        {
                            if (!location.IsIndoor)
                            {
                                DrawBoundAreasForProperty(location, isActive, showHeader: false, verticalLayout: true);
                            }
                            else if (location.RoomId > 0)
                            {
                                DrawRoomBoundForProperty(location, isActive, showHeader: false);
                            }
                        }

                        ImGui.PopID();
                    }
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        var leftMin = ImGui.GetItemRectMin();
        var leftMax = ImGui.GetItemRectMax();
        var sepX = leftMax.X + (ImGui.GetStyle().ItemSpacing.X * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(new Vector2(sepX, leftMin.Y), new Vector2(sepX, leftMax.Y), ImGui.GetColorU32(ImGuiCol.Separator));

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (16f * ImGuiHelpers.GlobalScale));
        ImGui.BeginChild("housing-settings-right", new Vector2(0, childHeight), false, ImGuiWindowFlags.NoBackground);
        try
        {
            ImGui.TextColored(ImGuiColors.ParsedBlue, "Settings");
            UiSharedService.ColorTextWrapped("Manage your area Binding settings for this Syncshell", ImGuiColors.DalamudGrey);
            UiSharedService.ColorTextWrapped($"Active bound areas: {GetUniqueBoundAreaCount()}", ImGuiColors.ParsedGreen);
            ImGuiHelpers.ScaledDummy(1f);
            ImGui.Separator();
            DrawAreaBindingAdvancedSettings();

        }
        finally
        {
            ImGui.EndChild();
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

    private void DrawAreaBindingAdvancedSettings()
    {
        var hasBoundAreas = _boundAreas.Count > 0;
        if (!hasBoundAreas)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Select at least one bound area (Outdoor/Ground/Basement/Room) to unlock these settings.");
            ImGuiHelpers.ScaledDummy(1f);
        }

        using (ImRaii.Disabled(!hasBoundAreas))
        {
            var dirty = false;

            ImGui.TextColored(ImGuiColors.ParsedBlue, "Auto-Join");
            ImGui.Indent();
            if (ImGui.Checkbox("Auto Broadcast Enabled", ref _autoBroadcastEnabled)) dirty = true;
            UiSharedService.AttachToolTip("Automatically broadcast this syncshell to users entering any bound area");

            if (ImGui.Checkbox("Require Owner Presence", ref _requireOwnerPresence)) dirty = true;
            UiSharedService.AttachToolTip("Only allow auto-join when the syncshell owner is present in the area");

            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Max Auto Join Users", ref _maxAutoJoinUsers))
            {
                _maxAutoJoinUsers = Math.Max(1, Math.Min(100, _maxAutoJoinUsers));
                dirty = true;
            }
            UiSharedService.AttachToolTip("Maximum number of users that can auto-join this syncshell");
            ImGui.Unindent();

            ImGuiHelpers.ScaledDummy(1f);
            ImGui.Separator();

            ImGui.TextColored(ImGuiColors.ParsedBlue, "Notifications");
            ImGui.Indent();
            if (ImGui.Checkbox("Notify on User Enter", ref _notifyOnUserEnter)) dirty = true;
            if (ImGui.Checkbox("Notify on User Leave", ref _notifyOnUserLeave)) dirty = true;
            ImGui.Unindent();

            ImGuiHelpers.ScaledDummy(1f);
            ImGui.Separator();

            ImGui.TextColored(ImGuiColors.ParsedBlue, "Messages");
            ImGui.Indent();
            ImGui.SetNextItemWidth(420);
            if (ImGui.InputTextWithHint("##custom_join_message", "Optional message shown when users auto-join", ref _customJoinMessage, 200))
            {
                dirty = true;
            }
            ImGui.Unindent();

            ImGuiHelpers.ScaledDummy(1f);
            ImGui.Separator();

            ImGui.TextColored(ImGuiColors.ParsedBlue, "Rules");
            ImGui.Indent();
            if (ImGui.Checkbox("Require Rules Acceptance", ref _requireRulesAcceptance)) dirty = true;
            UiSharedService.AttachToolTip("Require users to accept rules before auto-joining");

            if (_requireRulesAcceptance)
            {
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt("Rules Version", ref _rulesVersion))
                {
                    _rulesVersion = Math.Max(1, _rulesVersion);
                    dirty = true;
                }
                UiSharedService.AttachToolTip("Increment this when rules change to require re-acceptance");

                ImGui.TextUnformatted("Rules Text:");
                ImGui.SetNextItemWidth(-1);
                var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 6);
                if (ImGui.InputTextMultiline("##join_rules", ref _joinRules, 2000, size))
                {
                    ParseRulesIntoList();
                    dirty = true;
                }
            }
            ImGui.Unindent();

            ImGuiHelpers.ScaledDummy(1f);
            if (dirty)
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Apply"))
                {
                    _ = UpdateAreaBindingSettings();
                }
                UiSharedService.AttachToolTip("Apply settings to the server");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "No changes");
            }
        }
    }

    private static bool DrawToggleSwitch(string id, bool value)
    {
        var height = ImGui.GetFrameHeight();
        var width = height * 1.8f;
        var position = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(width, height));

        if (ImGui.IsItemClicked())
        {
            value = !value;
        }

        var drawList = ImGui.GetWindowDrawList();
        var radius = height * 0.5f;
        var bgColor = ImGui.GetColorU32(ImGuiCol.FrameBg);
        drawList.AddRectFilled(position, new Vector2(position.X + width, position.Y + height), bgColor, height * 0.5f);

        var circleX = value ? position.X + width - radius : position.X + radius;
        var circleColor = ImGui.GetColorU32(ImGuiCol.CheckMark);
        drawList.AddCircleFilled(new Vector2(circleX, position.Y + radius), radius - 1.0f, circleColor);

        return value;
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

    private void DrawBoundAreasForProperty(LocationInfo propertyLocation, bool propertyIsActive, bool showHeader = true, bool verticalLayout = false)
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

        var anyOutdoor = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotOutdoor &&
            BoundAreaBelongsToProperty(b, propertyLocation));

        var anyGround = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, propertyLocation) &&
            (b.Location.MapId == 0 || b.Location.MapId == HousingInteriorRelativeMapIds.Ground));

        var anyBasement = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, propertyLocation) &&
            b.Location.MapId == HousingInteriorRelativeMapIds.Basement);

        var anySecond = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, propertyLocation) &&
            b.Location.MapId == HousingInteriorRelativeMapIds.Second);

        if (showHeader)
        {
            ImGuiHelpers.ScaledDummy(1f);
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Bound areas:");
        }

        ImGui.PushID($"bound_areas_{propertyLocation.ServerId}_{propertyLocation.TerritoryId}_{propertyLocation.WardId}_{propertyLocation.HouseId}");

        var outdoor = anyOutdoor;
        if (CheckboxWithStatusColor("Outdoor", ref outdoor) && outdoor != anyOutdoor)
        {
            SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotOutdoor, 0, false, "Outdoor", outdoor);
        }
        if (!verticalLayout) ImGui.SameLine();

        var ground = anyGround;
        if (CheckboxWithStatusColor("Ground", ref ground) && ground != anyGround)
        {
            SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Ground, true, "Ground floor", ground);
        }
        if (!verticalLayout) ImGui.SameLine();

        var basement = anyBasement;
        if (CheckboxWithStatusColor("Basement", ref basement) && basement != anyBasement)
        {
            SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Basement, true, "Basement", basement);
        }

        if (showSecond)
        {
            if (!verticalLayout) ImGui.SameLine();
            var second = anySecond;
            if (CheckboxWithStatusColor("Second", ref second) && second != anySecond)
            {
                SetBoundAreaEnabled(propertyLocation, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Second, true, "Second floor", second);
            }
        }
        else if (!isSmall)
        {
            if (!verticalLayout) ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, plotSize);
        }

        ImGui.PopID();
    }

    private void DrawRoomBoundForProperty(LocationInfo roomLocation, bool propertyIsActive, bool showHeader = true)
    {
        if (roomLocation.RoomId <= 0)
        {
            return;
        }

        var sourceAreas = propertyIsActive
            ? _boundAreas
            : _housingOwnershipService.GetSuspendedAreaBindingLocations(GroupFullInfo.Group.GID, roomLocation);

        var anyThisRoom = sourceAreas.Any(b =>
            b.MatchingMode == AreaMatchingMode.HousingPlotIndoor &&
            BoundAreaBelongsToProperty(b, roomLocation) &&
            b.Location.RoomId == roomLocation.RoomId);

        if (showHeader)
        {
            ImGuiHelpers.ScaledDummy(1f);
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Bound areas:");
        }

        ImGui.PushID($"bound_room_{roomLocation.ServerId}_{roomLocation.TerritoryId}_{roomLocation.WardId}_{roomLocation.HouseId}_{roomLocation.RoomId}");

        var label = roomLocation.HouseId == 100 ? "Apartment" : "Room";
        var thisRoom = anyThisRoom;
        if (CheckboxWithStatusColor(label, ref thisRoom) && thisRoom != anyThisRoom)
        {
            SetRoomBoundEnabled(roomLocation, thisRoom);
        }

        ImGui.PopID();
    }

    private static bool CheckboxWithStatusColor(string label, ref bool value)
    {
        ImGui.PushID(label);
        var changed = ImGui.Checkbox("##cb", ref value);
        ImGui.PopID();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (value)
        {
            var green = ImGuiColors.ParsedGreen;
            ImGui.TextColored(new Vector4(green.X, green.Y, green.Z, 0.85f), label);
        }
        else
        {
            ImGui.TextUnformatted(label);
        }
        return changed;
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
