using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI;

public class TopTabMenu
{
    private readonly ApiController _apiController;

    private readonly SpheneMediator _spheneMediator;

    private readonly PairManager _pairManager;
    private readonly UiSharedService _uiSharedService;
    private string _filter = string.Empty;
    private int _globalControlCountdown = 0;

    private string _pairToAdd = string.Empty;

    private SelectedTab _selectedTab = SelectedTab.None;
    public TopTabMenu(SpheneMediator spheneMediator, ApiController apiController, PairManager pairManager, UiSharedService uiSharedService)
    {
        _spheneMediator = spheneMediator;
        _apiController = apiController;
        _pairManager = pairManager;
        _uiSharedService = uiSharedService;
    }

    // Anchor for sidebar popup positioning (screen coordinates of last clicked button)
    public Vector2? SidebarPopupAnchorMin { get; private set; }
    public Vector2? SidebarPopupAnchorMax { get; private set; }

    private enum SelectedTab
    {
        None,
        Individual,
        Syncshell,
        Game,
        Filter,
        UserConfig
    }

    public string Filter
    {
        get => _filter;
        private set
        {
            if (!string.Equals(_filter, value, StringComparison.OrdinalIgnoreCase))
            {
                _spheneMediator.Publish(new RefreshUiMessage());
            }

            _filter = value;
        }
    }
    private SelectedTab TabSelection
    {
        get => _selectedTab; set
        {
            if (_selectedTab == SelectedTab.Filter && value != SelectedTab.Filter)
            {
                Filter = string.Empty;
            }

            _selectedTab = value;
        }
    }
    public void Draw(bool wideButtons)
    {
        DrawTopNav(wideButtons);
        DrawSelectedContent();
    }

    public void DrawTopNav(bool fullWidthIcons)
    {
        var spacing = ImGui.GetStyle().ItemSpacing;
        var buttonY = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause).Y;
        var buttonSize = new Vector2(0f, buttonY);
        var drawList = ImGui.GetWindowDrawList();
        var underlineColor = ImGui.GetColorU32(ImGuiCol.Separator);
        var btncolor = ImRaii.PushColor(ImGuiCol.Button, ImGui.ColorConvertFloat4ToU32(new(0, 0, 0, 0)));

        ImGuiHelpers.ScaledDummy(spacing.Y / 2f);

        // Use actual current window content width instead of potential available width
        float avail = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        int per = 5;
        float fullWidth = MathF.Max(50f * ImGuiHelpers.GlobalScale, (avail - spacing.X * (per - 1)) / per);

        bool DrawTopButton(FontAwesomeIcon icon, string label, SelectedTab tab)
        {
            bool clicked = false;
            float btnWidth = fullWidth; // Always use available width per button
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));
                clicked = ImGui.Button(icon.ToIconString(), new Vector2(btnWidth, buttonY));
                ImGui.PopStyleVar();
            }
            if (clicked)
            {
                TabSelection = TabSelection == tab ? SelectedTab.None : tab;
            }
            return clicked;
        }

        var x = ImGui.GetCursorScreenPos();
        var clicked1 = DrawTopButton(FontAwesomeIcon.User, "Individual", SelectedTab.Individual);
        UiSharedService.AttachToolTip("Individual Pair Menu");
        ImGui.SameLine();
        var xAfter = ImGui.GetCursorScreenPos();
        if (TabSelection == SelectedTab.Individual)
            drawList.AddLine(x with { Y = x.Y + buttonSize.Y + spacing.Y },
                xAfter with { Y = xAfter.Y + buttonSize.Y + spacing.Y, X = xAfter.X - spacing.X },
                underlineColor, 2);

        var x2 = ImGui.GetCursorScreenPos();
        var clicked2 = DrawTopButton(FontAwesomeIcon.Users, "Syncshell", SelectedTab.Syncshell);
        UiSharedService.AttachToolTip("Syncshell Menu");
        ImGui.SameLine();
        var x2After = ImGui.GetCursorScreenPos();
        if (TabSelection == SelectedTab.Syncshell)
            drawList.AddLine(x2 with { Y = x2.Y + buttonSize.Y + spacing.Y },
                x2After with { Y = x2After.Y + buttonSize.Y + spacing.Y, X = x2After.X - spacing.X },
                underlineColor, 2);

        ImGui.SameLine();
        var x3 = ImGui.GetCursorScreenPos();
        var clicked3 = DrawTopButton(FontAwesomeIcon.Dice, "Game", SelectedTab.Game);
        UiSharedService.AttachToolTip("Game Menu");
        ImGui.SameLine();
        var x3After = ImGui.GetCursorScreenPos();
        if (TabSelection == SelectedTab.Game)
            drawList.AddLine(x3 with { Y = x3.Y + buttonSize.Y + spacing.Y },
                x3After with { Y = x3After.Y + buttonSize.Y + spacing.Y, X = x3After.X - spacing.X },
                underlineColor, 2);

        ImGui.SameLine();
        var x4 = ImGui.GetCursorScreenPos();
        var clicked4 = DrawTopButton(FontAwesomeIcon.Filter, "Filter", SelectedTab.Filter);
        UiSharedService.AttachToolTip("Filter");
        ImGui.SameLine();
        var x4After = ImGui.GetCursorScreenPos();
        if (TabSelection == SelectedTab.Filter)
            drawList.AddLine(x4 with { Y = x4.Y + buttonSize.Y + spacing.Y },
                x4After with { Y = x4After.Y + buttonSize.Y + spacing.Y, X = x4After.X - spacing.X },
                underlineColor, 2);

        ImGui.SameLine();
        var x5 = ImGui.GetCursorScreenPos();
        var clicked5 = DrawTopButton(FontAwesomeIcon.UserCog, "User Config", SelectedTab.UserConfig);
        UiSharedService.AttachToolTip("Your User Menu");
        ImGui.SameLine();
        var x5After = ImGui.GetCursorScreenPos();
        if (TabSelection == SelectedTab.UserConfig)
            drawList.AddLine(x5 with { Y = x5.Y + buttonSize.Y + spacing.Y },
                x5After with { Y = x5After.Y + buttonSize.Y + spacing.Y, X = x5After.X - spacing.X },
                underlineColor, 2);

        ImGui.NewLine();
        btncolor.Dispose();

        ImGuiHelpers.ScaledDummy(spacing);
    }

    public void DrawSelectedContent()
    {
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;

        if (TabSelection == SelectedTab.Individual)
        {
            DrawAddPair(availableWidth, spacingX);
            DrawGlobalIndividualButtons(availableWidth, spacingX);
        }
        else if (TabSelection == SelectedTab.Syncshell)
        {
            DrawSyncshellMenu(availableWidth, spacingX);
            DrawGlobalSyncshellButtons(availableWidth, spacingX);
        }
        else if (TabSelection == SelectedTab.Filter)
        {
            DrawFilter(availableWidth, spacingX);
        }
        else if (TabSelection == SelectedTab.Game)
        {
            DrawGameMenu(availableWidth, spacingX);
        }
        else if (TabSelection == SelectedTab.UserConfig)
        {
            DrawUserConfig(availableWidth, spacingX);
        }

        if (TabSelection != SelectedTab.None) ImGuiHelpers.ScaledDummy(3f);
        ImGui.Separator();
    }

    public void DrawSidebar(bool expanded, float sidebarWidth)
    {
        var spacing = ImGui.GetStyle().ItemSpacing;
        var frame = ImGui.GetStyle().FramePadding;
        var buttonY = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause).Y;
        var contentWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

        void SidebarEntry(FontAwesomeIcon icon, string label, SelectedTab tab)
        {
            bool clicked = false;
            if (expanded)
            {
                // In expanded mode, use full-width icon+text buttons
                if (_uiSharedService.IconTextButton(icon, label, contentWidth))
                {
                    clicked = true;
                }
                // Keep anchor updated every frame for the selected tab so the context tracks window movement
                if (TabSelection == tab)
                {
                    SidebarPopupAnchorMin = ImGui.GetItemRectMin();
                    SidebarPopupAnchorMax = ImGui.GetItemRectMax();
                }
                using (SpheneCustomTheme.ApplyTooltipTheme())
                {
                    UiSharedService.AttachToolTip(label);
                }
            }
            else
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    var iconBtnWidth = buttonY + frame.X * 2f; // square-ish icon-only button
                    if (ImGui.Button(icon.ToIconString(), new Vector2(iconBtnWidth, buttonY)))
                    {
                        clicked = true;
                    }
                    // Keep anchor updated every frame for the selected tab so the context tracks window movement
                    if (TabSelection == tab)
                    {
                        SidebarPopupAnchorMin = ImGui.GetItemRectMin();
                        SidebarPopupAnchorMax = ImGui.GetItemRectMax();
                    }
                }
                using (SpheneCustomTheme.ApplyTooltipTheme())
                {
                    UiSharedService.AttachToolTip(label);
                }
            }

            if (clicked)
            {
                // Ensure any existing popup closes so the new one opens immediately on single click
                ImGui.CloseCurrentPopup();
                // Remember the clicked button rectangle to anchor the popup next to it
                SidebarPopupAnchorMin = ImGui.GetItemRectMin();
                SidebarPopupAnchorMax = ImGui.GetItemRectMax();
                TabSelection = tab;
                ImGui.OpenPopup("compact-tab-popup");
            }
            ImGuiHelpers.ScaledDummy(spacing.Y * 0.5f);
        }

        SidebarEntry(FontAwesomeIcon.User, "Individual", SelectedTab.Individual);
        SidebarEntry(FontAwesomeIcon.Users, "Syncshell", SelectedTab.Syncshell);
        SidebarEntry(FontAwesomeIcon.Dice, "Game", SelectedTab.Game);
        SidebarEntry(FontAwesomeIcon.Filter, "Filter", SelectedTab.Filter);
        SidebarEntry(FontAwesomeIcon.UserCog, "User Config", SelectedTab.UserConfig);
    }

    public void CloseContext()
    {
        SidebarPopupAnchorMin = null;
        SidebarPopupAnchorMax = null;
        TabSelection = SelectedTab.None;
    }

    private void DrawAddPair(float availableXWidth, float spacingX)
    {
        var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.UserPlus, "Add");
        ImGui.SetNextItemWidth(availableXWidth - buttonSize - spacingX);
        ImGui.InputTextWithHint("##otheruid", "Other players UID/Alias", ref _pairToAdd, 20);
        ImGui.SameLine();
        var alreadyExisting = _pairManager.DirectPairs.Exists(p => string.Equals(p.UserData.UID, _pairToAdd, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, _pairToAdd, StringComparison.Ordinal));
        using (ImRaii.Disabled(alreadyExisting || string.IsNullOrEmpty(_pairToAdd)))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserPlus, "Add"))
            {
                _ = _apiController.UserAddPair(new(new(_pairToAdd)));
                _pairToAdd = string.Empty;
            }
        }
        UiSharedService.AttachToolTip("Pair with " + (_pairToAdd.IsNullOrEmpty() ? "other user" : _pairToAdd));
    }

    private void DrawFilter(float availableWidth, float spacingX)
    {
        var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Ban, "Clear");
        ImGui.SetNextItemWidth(availableWidth - buttonSize - spacingX);
        string filter = Filter;
        if (ImGui.InputTextWithHint("##filter", "Filter for UID/notes", ref filter, 255))
        {
            Filter = filter;
        }
        ImGui.SameLine();
        using var disabled = ImRaii.Disabled(string.IsNullOrEmpty(Filter));
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Ban, "Clear"))
        {
            Filter = string.Empty;
        }
    }

    private void DrawGlobalIndividualButtons(float availableXWidth, float spacingX)
    {
        var buttonX = (availableXWidth - (spacingX * 3)) / 4f;
        var buttonY = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause).Y;
        var buttonSize = new Vector2(buttonX, buttonY);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.Pause.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Individual Pause");
            }
        }
        UiSharedService.AttachToolTip("Globally resume or pause all individual pairs." + UiSharedService.TooltipSeparator
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.VolumeUp.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Individual Sounds");
            }
        }
        UiSharedService.AttachToolTip("Globally enable or disable sound sync with all individual pairs."
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.Running.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Individual Animations");
            }
        }
        UiSharedService.AttachToolTip("Globally enable or disable animation sync with all individual pairs." + UiSharedService.TooltipSeparator
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.Sun.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Individual VFX");
            }
        }
        UiSharedService.AttachToolTip("Globally enable or disable VFX sync with all individual pairs." + UiSharedService.TooltipSeparator
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));


        PopupIndividualSetting("Individual Pause", "Unpause all individuals", "Pause all individuals",
            FontAwesomeIcon.Play, FontAwesomeIcon.Pause,
            (perm) =>
            {
                perm.SetPaused(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetPaused(true);
                return perm;
            });
        PopupIndividualSetting("Individual Sounds", "Enable sounds for all individuals", "Disable sounds for all individuals",
            FontAwesomeIcon.VolumeUp, FontAwesomeIcon.VolumeMute,
            (perm) =>
            {
                perm.SetDisableSounds(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetDisableSounds(true);
                return perm;
            });
        PopupIndividualSetting("Individual Animations", "Enable animations for all individuals", "Disable animations for all individuals",
            FontAwesomeIcon.Running, FontAwesomeIcon.Stop,
            (perm) =>
            {
                perm.SetDisableAnimations(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetDisableAnimations(true);
                return perm;
            });
        PopupIndividualSetting("Individual VFX", "Enable VFX for all individuals", "Disable VFX for all individuals",
            FontAwesomeIcon.Sun, FontAwesomeIcon.Circle,
            (perm) =>
            {
                perm.SetDisableVFX(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetDisableVFX(true);
                return perm;
            });
    }

    private void DrawGlobalSyncshellButtons(float availableXWidth, float spacingX)
    {
        var buttonX = (availableXWidth - (spacingX * 4)) / 5f;
        var buttonY = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Pause).Y;
        var buttonSize = new Vector2(buttonX, buttonY);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.Pause.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Syncshell Pause");
            }
        }
        UiSharedService.AttachToolTip("Globally resume or pause all syncshells." + UiSharedService.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.VolumeUp.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Syncshell Sounds");
            }
        }
        UiSharedService.AttachToolTip("Globally enable or disable sound sync with all syncshells." + UiSharedService.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
                        + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.Running.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Syncshell Animations");
            }
        }
        UiSharedService.AttachToolTip("Globally enable or disable animation sync with all syncshells." + UiSharedService.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (ImGui.Button(FontAwesomeIcon.Sun.ToIconString(), buttonSize))
            {
                ImGui.OpenPopup("Syncshell VFX");
            }
        }
        UiSharedService.AttachToolTip("Globally enable or disable VFX sync with all syncshells." + UiSharedService.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));


        PopupSyncshellSetting("Syncshell Pause", "Unpause all syncshells", "Pause all syncshells",
            FontAwesomeIcon.Play, FontAwesomeIcon.Pause,
            (perm) =>
            {
                perm.SetPaused(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetPaused(true);
                return perm;
            });
        PopupSyncshellSetting("Syncshell Sounds", "Enable sounds for all syncshells", "Disable sounds for all syncshells",
            FontAwesomeIcon.VolumeUp, FontAwesomeIcon.VolumeMute,
            (perm) =>
            {
                perm.SetDisableSounds(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetDisableSounds(true);
                return perm;
            });
        PopupSyncshellSetting("Syncshell Animations", "Enable animations for all syncshells", "Disable animations for all syncshells",
            FontAwesomeIcon.Running, FontAwesomeIcon.Stop,
            (perm) =>
            {
                perm.SetDisableAnimations(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetDisableAnimations(true);
                return perm;
            });
        PopupSyncshellSetting("Syncshell VFX", "Enable VFX for all syncshells", "Disable VFX for all syncshells",
            FontAwesomeIcon.Sun, FontAwesomeIcon.Circle,
            (perm) =>
            {
                perm.SetDisableVFX(false);
                return perm;
            },
            (perm) =>
            {
                perm.SetDisableVFX(true);
                return perm;
            });

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0 || !UiSharedService.CtrlPressed());

            if (ImGui.Button(FontAwesomeIcon.Check.ToIconString(), buttonSize))
            {
                _ = GlobalControlCountdown(10);
                var bulkSyncshells = _pairManager.GroupPairs.Keys.OrderBy(g => g.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Group.GID, g =>
                    {
                        var perm = g.GroupUserPermissions;
                        perm.SetDisableSounds(g.GroupPermissions.IsPreferDisableSounds());
                        perm.SetDisableAnimations(g.GroupPermissions.IsPreferDisableAnimations());
                        perm.SetDisableVFX(g.GroupPermissions.IsPreferDisableVFX());
                        return perm;
                    }, StringComparer.Ordinal);

                _ = _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), bulkSyncshells)).ConfigureAwait(false);
            }
        }
        UiSharedService.AttachToolTip("Globally align syncshell permissions to suggested syncshell permissions." + UiSharedService.TooltipSeparator
            + "Note: This will not affect users with preferred permissions in syncshells." + Environment.NewLine
            + "Note: If multiple users share one syncshell the permissions to that user will be set to " + Environment.NewLine
            + "the ones of the last applied syncshell in alphabetical order." + UiSharedService.TooltipSeparator
            + "Hold CTRL to enable this button"
            + (_globalControlCountdown > 0 ? UiSharedService.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));
    }

    private void DrawSyncshellMenu(float availableWidth, float spacingX)
    {
        var buttonX = (availableWidth - (spacingX)) / 2f;

        using (ImRaii.Disabled(_pairManager.GroupPairs.Select(k => k.Key).Distinct()
            .Count(g => string.Equals(g.OwnerUID, _apiController.UID, StringComparison.Ordinal)) >= _apiController.ServerInfo.MaxGroupsCreatedByUser))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, "Create new Syncshell", buttonX))
            {
                _spheneMediator.Publish(new UiToggleMessage(typeof(CreateSyncshellUI)));
            }
            ImGui.SameLine();
        }

        using (ImRaii.Disabled(_pairManager.GroupPairs.Select(k => k.Key).Distinct().Count() >= _apiController.ServerInfo.MaxGroupsJoinedByUser))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Users, "Join existing Syncshell", buttonX))
            {
                _spheneMediator.Publish(new UiToggleMessage(typeof(JoinSyncshellUI)));
            }
        }
    }

    private void DrawUserConfig(float availableWidth, float spacingX)
    {
        var buttonX = (availableWidth - spacingX) / 2f;
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserCircle, "Edit Sphene Profile", buttonX))
            {
                _spheneMediator.Publish(new UiToggleMessage(typeof(EditProfileUi)));
            }
            UiSharedService.AttachToolTip("Edit your Sphene Profile");
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion, "Chara Data Analysis", buttonX))
        {
            _spheneMediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
        }
        UiSharedService.AttachToolTip("View and analyze your generated character data");
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Running, "Character Data Hub", availableWidth))
        {
            _spheneMediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
        }
    }

    private void DrawGameMenu(float availableWidth, float spacingX)
    {
        // First entry for games: Deathroll
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Dice, "Deathroll", availableWidth))
        {
            _spheneMediator.Publish(new UiToggleMessage(typeof(DeathrollLobbyUI)));
        }
        UiSharedService.AttachToolTip("Open Deathroll lobby and game window");
    }

    private async Task GlobalControlCountdown(int countdown)
    {
#if DEBUG
        return;
#endif

        _globalControlCountdown = countdown;
        while (_globalControlCountdown > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            _globalControlCountdown--;
        }
    }

    private void PopupIndividualSetting(string popupTitle, string enableText, string disableText,
                    FontAwesomeIcon enableIcon, FontAwesomeIcon disableIcon,
        Func<UserPermissions, UserPermissions> actEnable, Func<UserPermissions, UserPermissions> actDisable)
    {
        if (ImGui.BeginPopup(popupTitle))
        {
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                if (_uiSharedService.IconTextButton(enableIcon, enableText, null, true))
                {
                    _ = GlobalControlCountdown(10);
                    var bulkIndividualPairs = _pairManager.PairsWithGroups.Keys
                        .Where(g => g.IndividualPairStatus == IndividualPairStatus.Bidirectional)
                        .ToDictionary(g => g.UserPair.User.UID, g =>
                        {
                            return actEnable(g.UserPair.OwnPermissions);
                        }, StringComparer.Ordinal);

                    _ = _apiController.SetBulkPermissions(new(bulkIndividualPairs, new(StringComparer.Ordinal))).ConfigureAwait(false);
                    ImGui.CloseCurrentPopup();
                }

                if (_uiSharedService.IconTextButton(disableIcon, disableText, null, true))
                {
                    _ = GlobalControlCountdown(10);
                    var bulkIndividualPairs = _pairManager.PairsWithGroups.Keys
                        .Where(g => g.IndividualPairStatus == IndividualPairStatus.Bidirectional)
                        .ToDictionary(g => g.UserPair.User.UID, g =>
                        {
                            return actDisable(g.UserPair.OwnPermissions);
                        }, StringComparer.Ordinal);

                    _ = _apiController.SetBulkPermissions(new(bulkIndividualPairs, new(StringComparer.Ordinal))).ConfigureAwait(false);
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
    }
    private void PopupSyncshellSetting(string popupTitle, string enableText, string disableText,
        FontAwesomeIcon enableIcon, FontAwesomeIcon disableIcon,
        Func<GroupUserPreferredPermissions, GroupUserPreferredPermissions> actEnable,
        Func<GroupUserPreferredPermissions, GroupUserPreferredPermissions> actDisable)
    {
        if (ImGui.BeginPopup(popupTitle))
        {
            using (SpheneCustomTheme.ApplyContextMenuTheme())
            {
                if (_uiSharedService.IconTextButton(enableIcon, enableText, null, true))
                {
                    _ = GlobalControlCountdown(10);
                    var bulkSyncshells = _pairManager.GroupPairs.Keys
                        .OrderBy(u => u.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Group.GID, g =>
                        {
                            return actEnable(g.GroupUserPermissions);
                        }, StringComparer.Ordinal);

                    _ = _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), bulkSyncshells)).ConfigureAwait(false);
                    ImGui.CloseCurrentPopup();
                }

                if (_uiSharedService.IconTextButton(disableIcon, disableText, null, true))
                {
                    _ = GlobalControlCountdown(10);
                    var bulkSyncshells = _pairManager.GroupPairs.Keys
                        .OrderBy(u => u.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Group.GID, g =>
                        {
                            return actDisable(g.GroupUserPermissions);
                        }, StringComparer.Ordinal);

                    _ = _apiController.SetBulkPermissions(new(new(StringComparer.Ordinal), bulkSyncshells)).ConfigureAwait(false);
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
    }
}
