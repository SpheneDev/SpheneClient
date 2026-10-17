using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Sphene.UI.Design;
using System.Numerics;
using Sphene.UI.Panels;
using Sphene.UI.CharaDataHub;
using Sphene.UI.Syncshell;

namespace Sphene.UI.Components;

public class TopTabMenu
{
    private readonly ApiController _apiController;

    private readonly SpheneMediator _spheneMediator;

    private readonly PairManager _pairManager;
    private string _filter = string.Empty;
    private int _globalControlCountdown = 0;

    private string _pairToAdd = string.Empty;

    private SelectedTab _selectedTab = SelectedTab.None;
    public TopTabMenu(SpheneMediator spheneMediator, ApiController apiController, PairManager pairManager)
    {
        _spheneMediator = spheneMediator;
        _apiController = apiController;
        _pairManager = pairManager;
    }

    private enum SelectedTab
    {
        None,
        Individual,
        Syncshell,
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
    /// <summary>Horizontal inset so the row breathes inside the edge-to-edge
    /// CompactUI window instead of touching the window border.</summary>
    private const float HorizontalPad = 12f;

    /// <summary>Bounds + interaction state of one rail cell, captured while the
    /// button widget runs so the chrome channel can paint beneath it.</summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct TabCell(Vector2 Min, Vector2 Max, bool Hovered, bool Selected);

    private const int TabCellCount = 5;

    public void Draw(bool drawSeparator = true)
    {
        Draw(0, drawSeparator);
    }

    public void Draw(int pendingModSharingCount, bool drawSeparator = true)
    {
        var pendingPairRequestCount = _pairManager.UnseenInboundIndividualPairRequestCount;
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X
            - HorizontalPad * 2f;
        using var indent = ImRaii.PushIndent(HorizontalPad);
        var spacing = ImGui.GetStyle().ItemSpacing;
        var buttonX = (availableWidth - (spacing.X * 4)) / 5f;
        var buttonY = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Pause).Y;
        var buttonSize = new Vector2(buttonX, buttonY);
        var drawList = ImGui.GetWindowDrawList();
        // The rail owns the visuals — keep every button state transparent.
        var btncolor = ImRaii.PushColor(ImGuiCol.Button, 0u)
            .Push(ImGuiCol.ButtonHovered, 0u)
            .Push(ImGuiCol.ButtonActive, 0u);

        ImGuiHelpers.ScaledDummy(spacing.Y / 2f);

        var rowStart = ImGui.GetCursorScreenPos();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        var cells = new TabCell[TabCellCount];
        using (ImRaii.PushFont(UiBuilder.IconFont))
            cells[0] = DrawIndividualTabButton(buttonSize, drawList, pendingPairRequestCount);
        SpheneUI.AttachTooltip("Individual Pair Menu");

        cells[1] = DrawTabButton(FontAwesomeIcon.Users, SelectedTab.Syncshell, ButtonStyleKeys.TopTab_Users, buttonSize);
        SpheneUI.AttachTooltip("Syncshell Menu");

        ImGui.SameLine();
        cells[2] = DrawModSharingButton(buttonSize, pendingModSharingCount, drawList);
        SpheneUI.AttachTooltip("Open Mod Sharing");

        ImGui.SameLine();
        cells[3] = DrawTabButton(FontAwesomeIcon.Filter, SelectedTab.Filter, ButtonStyleKeys.TopTab_Filter, buttonSize);
        SpheneUI.AttachTooltip("Filter");

        ImGui.SameLine();
        cells[4] = DrawTabButton(FontAwesomeIcon.UserCog, SelectedTab.UserConfig, ButtonStyleKeys.TopTab_Settings, buttonSize);
        SpheneUI.AttachTooltip("Your User Menu");

        ImGui.NewLine();
        btncolor.Dispose();

        drawList.ChannelsSetCurrent(0);
        DrawTabRail(drawList, rowStart, availableWidth, buttonSize.Y, cells);
        drawList.ChannelsMerge();


        if (TabSelection == SelectedTab.Individual)
        {
            DrawAddPair(availableWidth, spacing.X);
            DrawPairRequestsButton(availableWidth, drawList);
            DrawGlobalIndividualButtons(availableWidth, spacing.X);
        }
        else if (TabSelection == SelectedTab.Syncshell)
        {
            DrawSyncshellMenu(availableWidth, spacing.X);
            DrawGlobalSyncshellButtons(availableWidth, spacing.X);
        }
        else if (TabSelection == SelectedTab.Filter)
        {
            DrawFilter(availableWidth, spacing.X);
        }
        else if (TabSelection == SelectedTab.UserConfig)
        {
            DrawUserConfig(availableWidth, spacing.X);
        }

        if (TabSelection != SelectedTab.None) SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
        if (drawSeparator) SpheneUI.Layout.Separator();
    }

    private TabCell DrawIndividualTabButton(Vector2 buttonSize, ImDrawListPtr drawList, int pendingPairRequestCount)
    {
        var buttonStart = ImGui.GetCursorScreenPos();
        if (SpheneUI.Icons.Button(FontAwesomeIcon.User, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_User))
        {
            TabSelection = TabSelection == SelectedTab.Individual ? SelectedTab.None : SelectedTab.Individual;
        }
        var cell = new TabCell(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
            ImGui.IsItemHovered(), TabSelection == SelectedTab.Individual);
        ImGui.SameLine();

        if (pendingPairRequestCount > 0)
        {
            DrawBadge(drawList, buttonStart, buttonSize, pendingPairRequestCount);
        }
        return cell;
    }

    private void DrawPairRequestsButton(float availableWidth, ImDrawListPtr drawList)
    {
        var incoming = _pairManager.GetInboundIndividualPairRequestsSnapshot();
        var outgoing = _pairManager.GetOutboundIndividualPairRequestsSnapshot();
        var totalRequests = incoming.Count + outgoing.Count;
        var unseen = _pairManager.UnseenInboundIndividualPairRequestCount;

        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

        var buttonStart = ImGui.GetCursorScreenPos();
        var buttonHeight = ImGui.GetFrameHeight();
        var buttonSize = new Vector2(availableWidth, buttonHeight);
        var label = "Pair Requests";

        using (ImRaii.Disabled(totalRequests == 0))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Inbox, label, width: availableWidth))
            {
                _spheneMediator.Publish(new OpenPairRequestsUiMessage());
            }
        }

        SpheneUI.AttachTooltip(totalRequests == 0
            ? "No pending pair requests"
            : "Open Pair Requests");

        if (unseen > 0)
        {
            DrawBadge(drawList, buttonStart, buttonSize, unseen);
        }
    }

    private TabCell DrawTabButton(FontAwesomeIcon icon, SelectedTab tab, string styleKey, Vector2 buttonSize)
    {
        if (SpheneUI.Icons.Button(icon, buttonSize.Y, null, null, buttonSize.X, styleKey))
        {
            TabSelection = TabSelection == tab ? SelectedTab.None : tab;
        }
        var cell = new TabCell(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
            ImGui.IsItemHovered(), TabSelection == tab);
        ImGui.SameLine();
        return cell;
    }

    private TabCell DrawModSharingButton(Vector2 buttonSize, int pendingModSharingCount, ImDrawListPtr drawList)
    {
        var buttonStart = ImGui.GetCursorScreenPos();
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Share, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_ModSharing))
        {
            var tab = pendingModSharingCount > 0 ? ModSharingTab.Receive : ModSharingTab.Send;
            _spheneMediator.Publish(new OpenModSharingWindow(tab));
        }
        var cell = new TabCell(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
            ImGui.IsItemHovered(), false);
        ImGui.SameLine();

        if (pendingModSharingCount > 0)
        {
            DrawBadge(drawList, buttonStart, buttonSize, pendingModSharingCount);
        }
        return cell;
    }

    /// <summary>
    /// Chrome painted beneath the tab row: one rounded rail spanning the row
    /// plus a per-cell pill for the active tab and a quiet hover fill.
    /// </summary>
    private static void DrawTabRail(ImDrawListPtr drawList, Vector2 rowStart,
        float rowWidth, float rowHeight, TabCell[] cells)
    {
        var theme = SpheneTheme.Current;
        var pad = 3f * SpheneUI.Scale;
        var railMin = rowStart - new Vector2(pad, pad);
        var railMax = rowStart + new Vector2(rowWidth + pad, rowHeight + pad);
        var railRounding = MathF.Min(theme.CompactChildRounding, (railMax.Y - railMin.Y) * 0.5f);

        drawList.AddRectFilled(railMin, railMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(theme.CompactFrameBg, 0.30f)), railRounding);
        drawList.AddRect(railMin, railMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(theme.CompactBorder, 0.16f)), railRounding);

        var cellInset = 2f * SpheneUI.Scale;
        foreach (var cell in cells)
        {
            var pillMin = cell.Min + new Vector2(cellInset, cellInset);
            var pillMax = cell.Max - new Vector2(cellInset, cellInset);
            if (pillMax.X <= pillMin.X || pillMax.Y <= pillMin.Y) continue;
            var pillRounding = MathF.Min(theme.CompactTabRounding, (pillMax.Y - pillMin.Y) * 0.5f);
            if (cell.Selected)
            {
                drawList.AddRectFilled(pillMin, pillMax,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(theme.CompactAccent, 0.22f)), pillRounding);
                drawList.AddRect(pillMin, pillMax,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(theme.CompactAccent, 0.45f)), pillRounding);
            }
            else if (cell.Hovered)
            {
                drawList.AddRectFilled(pillMin, pillMax,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(theme.CompactHover, 0.16f)), pillRounding);
            }
        }
    }

    private static void DrawBadge(ImDrawListPtr drawList, Vector2 buttonStart, Vector2 buttonSize, int count)
    {
        var scale = ImGuiHelpers.GlobalScale * 0.82f;
        var badgeText = count > 99 ? "99+" : count.ToString();
        var padX = 4f * scale;
        var padY = 1f * scale;
        Vector2 textSize;
        using (ImRaii.PushFont(UiBuilder.DefaultFont))
        {
            textSize = ImGui.CalcTextSize(badgeText);
        }
        var height = textSize.Y + padY * 2f;
        var width = MathF.Max(height, textSize.X + padX * 2f);
        var rounding = height / 2f;

        var margin = 2f * scale;
        var min = new Vector2(buttonStart.X + buttonSize.X - width - margin, buttonStart.Y + margin);
        var max = new Vector2(min.X + width, min.Y + height);

        var background = ImGui.ColorConvertFloat4ToU32(ImGuiColors.DalamudRed);
        var foreground = ImGui.ColorConvertFloat4ToU32(ImGuiColors.DalamudWhite);

        drawList.AddRectFilled(min, max, background, rounding);
        var textPos = new Vector2(
            min.X + (width - textSize.X) / 2f,
            min.Y + (height - textSize.Y) / 2f
        );
        using (ImRaii.PushFont(UiBuilder.DefaultFont))
        {
            drawList.AddText(textPos, foreground, badgeText);
        }
    }

    private void DrawAddPair(float availableXWidth, float spacingX)
    {
        var buttonSize = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.UserPlus, "Add");
        ImGui.SetNextItemWidth(availableXWidth - buttonSize - spacingX);
        SpheneUI.Controls.InputTextWithHint("##otheruid", "@handle", ref _pairToAdd, 20);
        ImGui.SameLine();
        var pairInput = _pairToAdd.Trim().TrimStart('@');
        var alreadyExisting = _pairManager.DirectPairs.Exists(p => string.Equals(p.UserData.UID, pairInput, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, pairInput, StringComparison.OrdinalIgnoreCase));
        using (ImRaii.Disabled(alreadyExisting || string.IsNullOrEmpty(pairInput)))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.UserPlus, "Add"))
            {
                _ = _apiController.UserAddPair(new(new(pairInput)));
                _pairToAdd = string.Empty;
            }
        }
        SpheneUI.AttachTooltip("Pair with " + (_pairToAdd.IsNullOrEmpty() ? "other user" : _pairToAdd));
    }

    private void DrawFilter(float availableWidth, float spacingX)
    {
        var buttonSize = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Ban, "Clear");
        ImGui.SetNextItemWidth(availableWidth - buttonSize - spacingX);
        string filter = Filter;
        if (SpheneUI.Controls.InputTextWithHint("##filter", "Filter for handle/notes", ref filter, 255))
        {
            Filter = filter;
        }
        ImGui.SameLine();
        using var disabled = ImRaii.Disabled(string.IsNullOrEmpty(Filter));
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Clear"))
        {
            Filter = string.Empty;
        }
    }

    private void DrawGlobalIndividualButtons(float availableXWidth, float spacingX)
    {
        var buttonX = (availableXWidth - (spacingX * 3)) / 4f;
        var buttonY = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Pause).Y;
        var buttonSize = new Vector2(buttonX, buttonY);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.Pause, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_IndividualPause))
            {
                ImGui.OpenPopup("Individual Pause");
            }
        }
        SpheneUI.AttachTooltip("Globally resume or pause all individual pairs." + SpheneUI.TooltipSeparator
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.VolumeUp, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_IndividualSound))
            {
                ImGui.OpenPopup("Individual Sounds");
            }
        }
        SpheneUI.AttachTooltip("Globally enable or disable sound sync with all individual pairs."
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.Running, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_IndividualAnimations))
            {
                ImGui.OpenPopup("Individual Animations");
            }
        }
        SpheneUI.AttachTooltip("Globally enable or disable animation sync with all individual pairs." + SpheneUI.TooltipSeparator
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.Sun, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_IndividualVFX))
            {
                ImGui.OpenPopup("Individual VFX");
            }
        }
        SpheneUI.AttachTooltip("Globally enable or disable VFX sync with all individual pairs." + SpheneUI.TooltipSeparator
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));


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
        var buttonY = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Pause).Y;
        var buttonSize = new Vector2(buttonX, buttonY);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.Pause, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_SyncshellPause))
            {
                ImGui.OpenPopup("Syncshell Pause");
            }
        }
        SpheneUI.AttachTooltip("Globally resume or pause all syncshells." + SpheneUI.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.VolumeUp, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_SyncshellSound))
            {
                ImGui.OpenPopup("Syncshell Sounds");
            }
        }
        SpheneUI.AttachTooltip("Globally enable or disable sound sync with all syncshells." + SpheneUI.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
                        + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.Running, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_SyncshellAnimations))
            {
                ImGui.OpenPopup("Syncshell Animations");
            }
        }
        SpheneUI.AttachTooltip("Globally enable or disable animation sync with all syncshells." + SpheneUI.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            using var disabled = ImRaii.Disabled(_globalControlCountdown > 0);

            if (SpheneUI.Icons.Button(FontAwesomeIcon.Sun, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_SyncshellVFX))
            {
                ImGui.OpenPopup("Syncshell VFX");
            }
        }
        SpheneUI.AttachTooltip("Globally enable or disable VFX sync with all syncshells." + SpheneUI.TooltipSeparator
                        + "Note: This will not affect users with preferred permissions in syncshells."
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));


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

            if (SpheneUI.Icons.Button(FontAwesomeIcon.Check, buttonSize.Y, null, null, buttonSize.X, ButtonStyleKeys.TopTab_SyncshellAlign))
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
        SpheneUI.AttachTooltip("Globally align syncshell permissions to suggested syncshell permissions." + SpheneUI.TooltipSeparator
            + "Note: This will not affect users with preferred permissions in syncshells." + Environment.NewLine
            + "Note: If multiple users share one syncshell the permissions to that user will be set to " + Environment.NewLine
            + "the ones of the last applied syncshell in alphabetical order." + SpheneUI.TooltipSeparator
            + "Hold CTRL to enable this button"
            + (_globalControlCountdown > 0 ? SpheneUI.TooltipSeparator + "Available again in " + _globalControlCountdown + " seconds." : string.Empty));
    }

    private void DrawSyncshellMenu(float availableWidth, float spacingX)
    {
        var buttonX = (availableWidth - (spacingX)) / 2f;

        using (ImRaii.Disabled(_pairManager.GroupPairs.Select(k => k.Key).Distinct()
            .Count(g => string.Equals(g.OwnerUID, _apiController.UID, StringComparison.Ordinal)) >= _apiController.ServerInfo.MaxGroupsCreatedByUser))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Create new Syncshell", buttonX))
            {
                _spheneMediator.Publish(new UiToggleMessage(typeof(CreateSyncshellUI)));
            }
            ImGui.SameLine();
        }

        using (ImRaii.Disabled(_pairManager.GroupPairs.Select(k => k.Key).Distinct().Count() >= _apiController.ServerInfo.MaxGroupsJoinedByUser))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Users, "Join existing Syncshell", buttonX))
            {
                _spheneMediator.Publish(new UiToggleMessage(typeof(JoinSyncshellUI)));
            }
        }
    }

    private void DrawUserConfig(float availableWidth, float spacingX)
    {
        var buttonX = (availableWidth - spacingX) / 2f;
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.UserCircle, "Edit Sphene Profile", buttonX))
            {
                _spheneMediator.Publish(new OpenSpheneHubMessage(SpheneHubPage.Profile, EditProfile: true));
            }
            SpheneUI.AttachTooltip("Edit your Sphene Profile");
        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.PersonCircleQuestion, "Chara Data Analysis", buttonX))
        {
            _spheneMediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
        }
        SpheneUI.AttachTooltip("View and analyze your generated character data");
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Running, "Character Data Hub", availableWidth))
        {
            _spheneMediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
        }
    }

    private async Task GlobalControlCountdown(int countdown)
    {
        if (System.Diagnostics.Debugger.IsAttached)
            return;

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
        if (SpheneUI.Popups.BeginStyled(popupTitle))
        {
            using (SpheneUI.Theme.ContextMenuScope())
            {
                if (SpheneUI.Icons.TextButton(enableIcon, enableText, null, true))
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

                if (SpheneUI.Icons.TextButton(disableIcon, disableText, null, true))
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
            SpheneUI.Popups.EndStyled();
        }
    }
    private void PopupSyncshellSetting(string popupTitle, string enableText, string disableText,
        FontAwesomeIcon enableIcon, FontAwesomeIcon disableIcon,
        Func<GroupUserPreferredPermissions, GroupUserPreferredPermissions> actEnable,
        Func<GroupUserPreferredPermissions, GroupUserPreferredPermissions> actDisable)
    {
        if (SpheneUI.Popups.BeginStyled(popupTitle))
        {
            using (SpheneUI.Theme.ContextMenuScope())
            {
                if (SpheneUI.Icons.TextButton(enableIcon, enableText, null, true))
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

                if (SpheneUI.Icons.TextButton(disableIcon, disableText, null, true))
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
            SpheneUI.Popups.EndStyled();
        }
    }
}
