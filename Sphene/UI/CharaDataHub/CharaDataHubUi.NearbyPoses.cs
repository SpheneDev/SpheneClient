using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.CharaDataHub;

internal partial class CharaDataHubUi
{
    private void DrawNearbyPoses()
    {
        DrawPageTitle("Poses Nearby",
            "Shared world poses detected around you — apply one or spawn its character.");

        if (!_uiSharedService.IsInGpose)
        {
            SpheneUI.Layout.Callout("Spawning and applying pose data is only available in GPose.",
                FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudYellow);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        DrawHelpFoldout("This tab will show you all Shared World Poses nearby you." + Environment.NewLine + Environment.NewLine
                        + "Shared World Poses are poses in character data that have world data attached to them and are set to shared. "
                        + "This means that all data that is in 'Shared with You' that has a pose with world data attached to it will be shown here if you are nearby." + Environment.NewLine
                        + "By default all poses that are shared will be shown. Poses taken in housing areas will by default only be shown on the correct server and location." + Environment.NewLine + Environment.NewLine
                        + "Shared World Poses will appear in the world as floating wisps, as well as in the list below. You can mouse over a Shared World Pose in the list for it to get highlighted in the world." + Environment.NewLine + Environment.NewLine
                        + "You can apply Shared World Poses to yourself or spawn the associated character to pose with them." + Environment.NewLine + Environment.NewLine
                        + "You can adjust the filter and change further settings in the 'Settings & Filter' foldout.");

        SpheneUI.Layout.Collapsible("Settings & Filters", () =>
        {
            string filterByUser = _charaDataNearbyManager.UserNoteFilter;
            if (SpheneUI.Controls.InputTextWithHint("##filterbyuser", "Filter by User", ref filterByUser, 50))
            {
                _charaDataNearbyManager.UserNoteFilter = filterByUser;
            }
            bool onlyCurrent = _configService.Current.NearbyOwnServerOnly;
            if (SpheneUI.Controls.Toggle("Only show Poses on current server", ref onlyCurrent))
            {
                _configService.Current.NearbyOwnServerOnly = onlyCurrent;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("Toggling this off will show you the location of all shared Poses with World Data from all Servers");
            bool showOwn = _configService.Current.NearbyShowOwnData;
            if (SpheneUI.Controls.Toggle("Also show your own data", ref showOwn))
            {
                _configService.Current.NearbyShowOwnData = showOwn;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("Toggling this on will also show you the location of your own Poses");
            bool ignoreHousing = _configService.Current.NearbyIgnoreHousingLimitations;
            if (SpheneUI.Controls.Toggle("Ignore Housing Limitations", ref ignoreHousing))
            {
                _configService.Current.NearbyIgnoreHousingLimitations = ignoreHousing;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("This will display all poses in their location regardless of housing limitations. (Ignoring Ward, Plot, Room)" + SpheneUI.TooltipSeparator
                + "Note: Poses that utilize housing props, furniture, etc. will not be displayed correctly if not spawned in the right location.");
            bool showWisps = _configService.Current.NearbyDrawWisps;
            if (SpheneUI.Controls.Toggle("Show Pose Wisps in the overworld", ref showWisps))
            {
                _configService.Current.NearbyDrawWisps = showWisps;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("When enabled, Sphene will draw floating wisps where other's poses are in the world.");
            int poseDetectionDistance = _configService.Current.NearbyDistanceFilter;
            SpheneUI.Layout.ScaledNextItemWidth(100);
            if (SpheneUI.Controls.SliderInt("Detection Distance", ref poseDetectionDistance, 5, 1000))
            {
                _configService.Current.NearbyDistanceFilter = poseDetectionDistance;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("This setting allows you to change the maximum distance in which poses will be shown. Set it to the maximum if you want to see all poses on the current map.");
            bool alwaysShow = _configService.Current.NearbyShowAlways;
            if (SpheneUI.Controls.Toggle("Keep active outside Poses Nearby tab", ref alwaysShow))
            {
                _configService.Current.NearbyShowAlways = alwaysShow;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("This will allow Sphene to continue the calculation of position of wisps etc. active outside of the 'Poses Nearby' tab." + SpheneUI.TooltipSeparator
                + "Note: The wisps etc. will disappear during combat and performing.");
        }, icon: FontAwesomeIcon.SlidersH);

        // Toolbar: refresh left, detected count right.
        DrawUpdateSharedDataButton();
        if (_charaDataNearbyManager.NearbyData.Count > 0)
        {
            var countText = $"{_charaDataNearbyManager.NearbyData.Count} poses nearby";
            var countW = ImGui.CalcTextSize(countText).X + ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SameLine();
            SpheneUI.Controls.DockToCardEdge(countW);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Muted(countText);
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        if (_charaDataNearbyManager.NearbyData.Count == 0)
        {
            SpheneUI.Layout.Callout("No shared world poses found nearby — they appear here when you get close to one.",
                FontAwesomeIcon.MapMarkedAlt, ImGuiColors.DalamudGrey);
            return;
        }

        bool wasAnythingHovered = false;
        int i = 0;
        foreach (var pose in _charaDataNearbyManager.NearbyData.OrderBy(v => v.Value.Distance))
        {
            using var poseId = ImRaii.PushId("nearbyPose" + (i++));
            var pos = ImGui.GetCursorPos();
            var rowW = SpheneUI.Layout.CardAlignedAvailX();
            var circleDiameter = 60f * SpheneUI.Scale;
            var innerPad = ImGui.GetStyle().ItemInnerSpacing.X;
            var circleX = pos.X + rowW - circleDiameter - innerPad;
            float circleOffsetY = 0;

            SpheneUI.Layout.Group(() =>
            {
                string? userNote = _serverConfigurationManager.GetNoteForUid(pose.Key.MetaInfo.Uploader.UID);
                var noteText = pose.Key.MetaInfo.IsOwnData ? "You" : (userNote == null ? pose.Key.MetaInfo.Uploader.AliasOrUID : $"{userNote} ({pose.Key.MetaInfo.Uploader.AliasOrUID})");

                // Line 1: owner + distance badge + details hint.
                ImGui.AlignTextToFramePadding();
                SpheneUI.Icons.Icon(FontAwesomeIcon.Running, ImGuiColors.ParsedGreen);
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                SpheneUI.Text.Colored(noteText, ImGuiColors.ParsedGreen);
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                SpheneUI.Text.Muted("· pose");
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                SpheneUI.Layout.Badge($"{pose.Value.Distance:0.0}y", ImGuiColors.ParsedGreen);
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                using (ImRaii.Group())
                {
                    SpheneUI.Text.Muted("details");
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    SpheneUI.Icons.Icon(FontAwesomeIcon.ExternalLinkAlt, ImGuiColors.DalamudGrey);
                }
                SpheneUI.AttachTooltip("Character data description" + SpheneUI.TooltipSeparator + pose.Key.MetaInfo.Description);

                // Text wraps just before the compass circle on the right.
                var textRight = circleX - ImGui.GetStyle().ItemSpacing.X;
                if (string.IsNullOrEmpty(pose.Key.Description))
                {
                    SpheneUI.Text.Muted("No pose description was set");
                }
                else
                {
                    SpheneUI.Text.Wrapped(pose.Key.Description, textRight);
                }

                // Line 3: world position descriptor — the location context of
                // the pose, previously only visible in a tooltip.
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
                    SpheneUI.Text.Wrapped(pose.Key.WorldDataDescriptor, textRight);

                var posAfterGroup = ImGui.GetCursorPos();
                // Center the circle against the actual row height and place its
                // dummy at the visual position so the click hitbox matches.
                circleOffsetY = MathF.Max(0f, (posAfterGroup.Y - pos.Y - circleDiameter) * 0.5f);
                ImGui.SetCursorPos(new Vector2(circleX, pos.Y + circleOffsetY));
                ImGui.Dummy(new Vector2(circleDiameter, circleDiameter));
                SpheneUI.AttachTooltip("Click to open corresponding map and set map marker" + SpheneUI.TooltipSeparator
                    + pose.Key.WorldDataDescriptor);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _dalamudUtilService.SetMarkerAndOpenMap(pose.Key.Position, pose.Key.Map);
                }
                ImGui.SetCursorPos(posAfterGroup);
                if (_uiSharedService.IsInGpose)
                {
                    GposePoseAction(() =>
                    {
                        if (SpheneUI.Controls.SegmentButton("Apply Pose", FontAwesomeIcon.ArrowRight,
                                accent: SpheneUI.Palette.EtherealPurple))
                        {
                            _charaDataManager.ApplyFullPoseDataToGposeTarget(pose.Key);
                        }
                    }, $"Apply pose and position to {CharaName(_gposeTarget)}", _hasValidGposeTarget);
                    ImGui.SameLine();
                    GposeMetaInfoAction((_) =>
                    {
                        if (SpheneUI.Controls.SmallButton("Spawn and Pose"))
                        {
                            _charaDataManager.SpawnAndApplyWorldTransform(pose.Key.MetaInfo, pose.Key);
                        }
                    }, "Spawn actor and apply pose and position", pose.Key.MetaInfo, _hasValidGposeTarget, true);
                }
            }, 5f, rowW);
            if (ImGui.IsItemHovered())
            {
                wasAnythingHovered = true;
                _nearbyHovered = pose.Key;
            }
            var drawList = ImGui.GetWindowDrawList();
            var circleRadius = circleDiameter / 2f;
            var windowPos = ImGui.GetWindowPos();
            // GetCursorPos is already scroll-adjusted — subtracting scroll here
            // detached the circle from its card once the list scrolled.
            var circleCenter = new Vector2(windowPos.X + circleX + circleRadius, windowPos.Y + pos.Y + circleRadius + circleOffsetY);
            var rads = pose.Value.Direction * (Math.PI / 180);

            float halfConeAngleRadians = 15f * (float)Math.PI / 180f;
            Vector2 baseDir1 = new Vector2((float)Math.Sin(rads - halfConeAngleRadians), -(float)Math.Cos(rads - halfConeAngleRadians));
            Vector2 baseDir2 = new Vector2((float)Math.Sin(rads + halfConeAngleRadians), -(float)Math.Cos(rads + halfConeAngleRadians));

            Vector2 coneBase1 = circleCenter + baseDir1 * circleRadius;
            Vector2 coneBase2 = circleCenter + baseDir2 * circleRadius;

            // Draw the cone as a filled triangle
            drawList.AddTriangleFilled(circleCenter, coneBase1, coneBase2, SpheneUI.ColorUtils.U32(ImGuiColors.ParsedGreen));
            drawList.AddCircle(circleCenter, circleDiameter / 2, SpheneUI.ColorUtils.U32(ImGuiColors.DalamudWhite), 360, 2);
            var distance = pose.Value.Distance.ToString("0.0") + "y";
            var textSize = ImGui.CalcTextSize(distance);
            drawList.AddText(new Vector2(circleCenter.X - textSize.X / 2, circleCenter.Y + textSize.Y / 3f), SpheneUI.ColorUtils.U32(ImGuiColors.DalamudWhite), distance);

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        if (!wasAnythingHovered) _nearbyHovered = null;
        _charaDataNearbyManager.SetHoveredVfx(_nearbyHovered);
    }

    private void DrawUpdateSharedDataButton()
    {
        using (ImRaii.Disabled(_charaDataManager.GetAllDataTask != null
            || (_charaDataManager.GetSharedWithYouTimeoutTask != null && !_charaDataManager.GetSharedWithYouTimeoutTask.IsCompleted)))
        {
            if (SpheneUI.Controls.SegmentButton("Update Data Shared With You", FontAwesomeIcon.ArrowCircleDown))
            {
                var token = _disposalCts?.Token ?? CancellationToken.None;
                _ = _charaDataManager.GetAllSharedData(token).ContinueWith(u => UpdateFilteredItems());
            }
        }
        if (_charaDataManager.GetSharedWithYouTimeoutTask != null && !_charaDataManager.GetSharedWithYouTimeoutTask.IsCompleted)
        {
            SpheneUI.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }
    }
}
