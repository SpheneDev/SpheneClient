using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Sphene.API.Dto.CharaData;
using Sphene.Services.CharaData.Models;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.CharaDataHub;

internal sealed partial class CharaDataHubUi
{
    private string _createFilter = string.Empty;
    private bool _createOnlyShowFav = false;
    private bool _createOnlyShowNotDownloadable = false;

    private void DrawEditCharaData(CharaDataFullExtendedDto? dataDto)
    {
        using var imguiid = ImRaii.PushId(dataDto?.Id ?? "NoData");

        if (dataDto == null)
        {
            SpheneUI.Layout.Callout("Select an entry above to edit its data.",
                FontAwesomeIcon.Edit, ImGuiColors.DalamudGrey);
            return;
        }

        var updateDto = _charaDataManager.GetUpdateDto(dataDto.Id);

        if (updateDto == null)
        {
            SpheneUI.Text.ColoredWrapped("Something went wrong — there is no update DTO. Try re-downloading your character data.", ImGuiColors.DalamudYellow);
            return;
        }

        int otherUpdates = 0;
        foreach (var item in _charaDataManager.OwnCharaData.Values.Where(v => !string.Equals(v.Id, dataDto.Id, StringComparison.Ordinal)))
        {
            if (_charaDataManager.GetUpdateDto(item.Id)?.HasChanges ?? false)
            {
                otherUpdates++;
            }
        }

        bool canUpdate = updateDto.HasChanges;
        if (canUpdate || otherUpdates > 0 || (!_charaDataManager.CharaUpdateTask?.IsCompleted ?? false))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        if (canUpdate || _charaDataManager.UploadTask != null)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Group(() =>
            {
                if (canUpdate)
                {
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Text.ColoredWrapped("Warning: You have unsaved changes!", ImGuiColors.DalamudRed);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted))
                    {
                        if (SpheneUI.Controls.SegmentButton("Save to Server", FontAwesomeIcon.ArrowCircleUp))
                        {
                            _charaDataManager.UploadCharaData(dataDto.Id);
                        }
                        ImGui.SameLine();
                        if (SpheneUI.Controls.SmallButton("Undo all changes"))
                        {
                            updateDto.UndoChanges();
                        }
                    }
                    if (_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted)
                    {
                        SpheneUI.Text.ColoredWrapped("Updating data on server, please wait.", ImGuiColors.DalamudYellow);
                    }
                }

                var uploadTask = _charaDataManager.UploadTask;
                if (uploadTask is { IsCompleted: false })
                {
                    DisableDisabled(() =>
                    {
                        if (_charaDataManager.UploadProgress != null)
                        {
                            SpheneUI.Text.ColoredWrapped(_charaDataManager.UploadProgress.Value ?? string.Empty, ImGuiColors.DalamudYellow);
                        }
                        if (SpheneUI.Controls.SmallButton("Cancel Upload"))
                        {
                            _charaDataManager.CancelUpload();
                        }
                    });
                }
                else if (uploadTask is { IsCompletedSuccessfully: true })
                {
                    var color = SpheneUI.Colors.Bool(uploadTask.Result.Success);
                    SpheneUI.Text.ColoredWrapped(uploadTask.Result.Output, color);
                }
                else if (uploadTask is { IsFaulted: true })
                {
                    SpheneUI.Text.ColoredWrapped("Upload failed — check the log and try again.", ImGuiColors.DalamudRed);
                }
            }, 5f, SpheneUI.Layout.CardAlignedAvailX());
        }

        if (otherUpdates > 0)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Group(() =>
            {
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.ColoredWrapped($"You have {otherUpdates} other entries with unsaved changes.", ImGuiColors.DalamudYellow);
                ImGui.SameLine();
                using (ImRaii.Disabled(_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted))
                {
                    if (SpheneUI.Controls.SmallButton("Save all to server"))
                    {
                        _charaDataManager.UploadAllCharaData();
                    }
                }
            }, 5f, SpheneUI.Layout.CardAlignedAvailX());
        }

        if (canUpdate || otherUpdates > 0 || (!_charaDataManager.CharaUpdateTask?.IsCompleted ?? false))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        SpheneUI.Layout.SectionCard(() => DrawEditCharaDataGeneral(dataDto, updateDto),
            title: "General", accentColor: SpheneUI.Palette.EtherealPurple);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Layout.SectionCard(() => DrawEditCharaDataAccessAndSharing(updateDto),
            title: "Access and Sharing", accentColor: SpheneUI.Palette.EtherealPurple);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Layout.SectionCard(() => DrawEditCharaDataAppearance(dataDto, updateDto),
            title: "Appearance", accentColor: SpheneUI.Palette.EtherealPurple);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Layout.SectionCard(() => DrawEditCharaDataPoses(updateDto),
            title: "Poses", accentColor: SpheneUI.Palette.EtherealPurple);
    }

    private void DrawEditCharaDataAccessAndSharing(CharaDataExtendedUpdateDto updateDto)
    {

        SpheneUI.Layout.ScaledNextItemWidth(200);
        var dtoAccessType = updateDto.AccessType;
        using (var accessCtl = SpheneUI.Controls.PushValueControlStyle())
        {
            if (ImGui.BeginCombo("Access Restrictions", GetAccessTypeString(dtoAccessType)))
            {
                foreach (var accessType in Enum.GetValues(typeof(AccessTypeDto)).Cast<AccessTypeDto>())
                {
                    if (SpheneUI.Controls.Selectable(GetAccessTypeString(accessType), accessType == dtoAccessType))
                    {
                        updateDto.AccessType = accessType;
                    }
                }

                ImGui.EndCombo();
            }
        }
        SpheneUI.Controls.HelpText("You can control who has access to your character data based on the access restrictions." + SpheneUI.TooltipSeparator
            + "Specified: Only people and syncshells you directly specify in 'Specific Individuals / Syncshells' can access this character data" + Environment.NewLine
            + "Direct Pairs: Only people you have directly paired can access this character data" + Environment.NewLine
            + "All Pairs: All people you have paired can access this character data" + Environment.NewLine
            + "Everyone: Everyone can access this character data" + SpheneUI.TooltipSeparator
            + "Note: To access your character data the person in question requires to have the code. Exceptions for 'Shared' data, see 'Sharing' below." + Environment.NewLine
            + "Note: For 'Direct' and 'All Pairs' the pause state plays a role. Paused people will not be able to access your character data." + Environment.NewLine
            + "Note: Directly specified Individuals or Syncshells in the 'Specific Individuals / Syncshells' list will be able to access your character data regardless of pause or pair state.");

        DrawSpecific(updateDto);

        SpheneUI.Layout.ScaledNextItemWidth(200);
        var dtoShareType = updateDto.ShareType;
        using (var shareCtl = SpheneUI.Controls.PushValueControlStyle())
        {
            if (ImGui.BeginCombo("Sharing", GetShareTypeString(dtoShareType)))
            {
                foreach (var shareType in Enum.GetValues(typeof(ShareTypeDto)).Cast<ShareTypeDto>())
                {
                    if (SpheneUI.Controls.Selectable(GetShareTypeString(shareType), shareType == dtoShareType))
                    {
                        updateDto.ShareType = shareType;
                    }
                }

                ImGui.EndCombo();
            }
        }
        SpheneUI.Controls.HelpText("This regulates how you want to distribute this character data." + SpheneUI.TooltipSeparator
            + "Code Only: People require to have the code to download this character data" + Environment.NewLine
            + "Shared: People that are allowed through 'Access Restrictions' will have this character data entry displayed in 'Shared with You' (it can also be accessed through the code)" + SpheneUI.TooltipSeparator
            + "Note: Shared with Access Restriction 'Everyone' is the same as shared with Access Restriction 'All Pairs', it will not show up for everyone but just your pairs.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X10);
    }

    private void DrawEditCharaDataAppearance(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        if (SpheneUI.Controls.SegmentButton("Set Appearance to Current Appearance", FontAwesomeIcon.Sync,
                accent: SpheneUI.Palette.EtherealPurple))
        {
            _charaDataManager.SetAppearanceData(dataDto.Id);
        }
        SpheneUI.Controls.HelpText("This will overwrite the appearance data currently stored in this Character Data entry with your current appearance.");
        ImGui.SameLine();
        using (ImRaii.Disabled(dataDto.HasMissingFiles || !updateDto.IsAppearanceEqual || _charaDataManager.DataApplicationTask != null))
        {
            if (SpheneUI.Controls.SmallButton("Preview Saved Appearance on Self"))
            {
                _charaDataManager.ApplyDataToSelf(dataDto);
            }
        }
        SpheneUI.Controls.HelpText("This will download and apply the saved character data to yourself. Once loaded it will automatically revert itself within 15 seconds." + SpheneUI.TooltipSeparator
            + "Note: Weapons will not be displayed correctly unless using the same job as the saved data.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        DrawInfoRow("Glamourer Data",
            () => SpheneUI.Icons.BoolIcon(!string.IsNullOrEmpty(updateDto.GlamourerData), false));

        var hasFiles = (updateDto.FileGamePaths ?? []).Any() || dataDto.OriginalFiles.Any();
        DrawInfoRow("Files", () => SpheneUI.Icons.BoolIcon(hasFiles, false));
        if (hasFiles && updateDto.IsAppearanceEqual)
        {
            var fileGamePaths = dataDto.FileGamePaths ?? [];
            var fileSwaps = dataDto.FileSwaps ?? [];
            var uniqueHashes = fileGamePaths.DistinctBy(k => k.HashOrFileSwap).Count();
            var originalHashes = dataDto.OriginalFiles.DistinctBy(k => k.HashOrFileSwap).Count();
            SpheneUI.Text.Muted($"{uniqueHashes} unique file hashes · {fileGamePaths.Count} game paths · {fileSwaps.Count} file swaps (original upload: {originalHashes} hashes)");
            if (!dataDto.HasMissingFiles)
            {
                SpheneUI.Text.Colored("All files to download this character data are present on the server.", ImGuiColors.HealerGreen);
            }
            else
            {
                SpheneUI.Text.Colored($"{dataDto.MissingFiles.DistinctBy(k => k.HashOrFileSwap).Count()} files to download this character data are missing on the server.", ImGuiColors.DalamudRed);
                if (SpheneUI.Controls.SmallButton("Attempt to upload missing files and restore Character Data"))
                {
                    _charaDataManager.UploadMissingFiles(dataDto.Id);
                }
            }
        }
        else if (hasFiles && !updateDto.IsAppearanceEqual)
        {
            SpheneUI.Text.Colored("New data was set — it may contain files that will be uploaded when saving to the server.", ImGuiColors.DalamudYellow);
        }

        DrawInfoRow("Manipulation Data",
            () => SpheneUI.Icons.BoolIcon(!string.IsNullOrEmpty(updateDto.ManipulationData), false));
        DrawInfoRow("Customize+ Data",
            () => SpheneUI.Icons.BoolIcon(!string.IsNullOrEmpty(updateDto.CustomizeData), false));
    }

    private void DrawEditCharaDataGeneral(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        // Identity block — label/value rows instead of disabled input boxes.
        DrawInfoRow("Character Data Code", () =>
        {
            SpheneUI.Text.Plain(dataDto.FullId);
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            if (SpheneUI.Controls.IconButton("##copyCode", FontAwesomeIcon.Copy, size: ImGui.GetFrameHeight()))
            {
                ImGui.SetClipboardText(dataDto.FullId);
            }
            SpheneUI.AttachTooltip("Copy code to clipboard");
        });
        DrawInfoRow("Created", () => SpheneUI.Text.Plain(dataDto.CreatedDate.ToLocalTime().ToString()));
        DrawInfoRow("Last Update", () => SpheneUI.Text.Plain(dataDto.UpdatedDate.ToLocalTime().ToString()));
        DrawInfoRow("Downloads", () => SpheneUI.Text.Plain(dataDto.DownloadCount.ToString()));

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        string description = updateDto.Description;
        if (SpheneUI.Controls.InputText("Description", ref description, 200))
        {
            updateDto.Description = description;
        }
        SpheneUI.Controls.HelpText("Description for this Character Data." + SpheneUI.TooltipSeparator
            + "Note: the description will be visible to anyone who can access this character data. See 'Access Restrictions' and 'Sharing' below.");

        var expiryDate = updateDto.ExpiryDate;
        bool isExpiring = expiryDate != DateTime.MaxValue;
        if (SpheneUI.Controls.Toggle("Expires", ref isExpiring))
        {
            updateDto.SetExpiry(isExpiring);
        }
        SpheneUI.Controls.HelpText("If expiration is enabled, the uploaded character data will be automatically deleted from the server at the specified date.");
        using (ImRaii.Disabled(!isExpiring))
        using (var dateCtl = SpheneUI.Controls.PushValueControlStyle())
        {
            ImGui.SameLine();
            SpheneUI.Layout.ScaledNextItemWidth(100);
            if (ImGui.BeginCombo("Year", expiryDate.Year.ToString()))
            {
                for (int year = DateTime.UtcNow.Year; year < DateTime.UtcNow.Year + 4; year++)
                {
                    if (SpheneUI.Controls.Selectable(year.ToString(), year == expiryDate.Year))
                    {
                        updateDto.SetExpiry(year, expiryDate.Month, expiryDate.Day);
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();

            int daysInMonth = DateTime.DaysInMonth(expiryDate.Year, expiryDate.Month);
            SpheneUI.Layout.ScaledNextItemWidth(100);
            if (ImGui.BeginCombo("Month", expiryDate.Month.ToString()))
            {
                for (int month = 1; month <= 12; month++)
                {
                    if (SpheneUI.Controls.Selectable(month.ToString(), month == expiryDate.Month))
                    {
                        updateDto.SetExpiry(expiryDate.Year, month, expiryDate.Day);
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();

            SpheneUI.Layout.ScaledNextItemWidth(100);
            if (ImGui.BeginCombo("Day", expiryDate.Day.ToString()))
            {
                for (int day = 1; day <= daysInMonth; day++)
                {
                    if (SpheneUI.Controls.Selectable(day.ToString(), day == expiryDate.Day))
                    {
                        updateDto.SetExpiry(expiryDate.Year, expiryDate.Month, day);
                    }
                }
                ImGui.EndCombo();
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Text.Muted("Danger zone");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
        {
            if (SpheneUI.Controls.SmallButton("Delete Character Data"))
            {
                _ = _charaDataManager.DeleteCharaData(dataDto);
                SelectedDtoId = string.Empty;
            }
        }
        if (!UiSharedService.CtrlPressed())
        {
            SpheneUI.AttachTooltip("Hold CTRL and click to delete the current data. This operation is irreversible.");
        }
    }

    private void DrawEditCharaDataPoses(CharaDataExtendedUpdateDto updateDto)
    {
        var poseCount = updateDto.PoseList.Count();
        using (ImRaii.Disabled(poseCount >= maxPoses))
        {
            if (SpheneUI.Controls.SmallButton("Add new Pose"))
            {
                updateDto.AddPose();
            }
        }
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, poseCount == maxPoses))
            SpheneUI.Text.Plain($"{poseCount}/{maxPoses} poses attached");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        int poseNumber = 1;
        var iconBtn = ImGui.GetFrameHeight();
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var rowInset = ImGui.GetStyle().ItemInnerSpacing.X;
        var attachDisabled = !_uiSharedService.IsInGpose
            || !(_charaDataManager.AttachingPoseTask?.IsCompleted ?? true)
            || !_charaDataManager.BrioAvailable;

        if (!_uiSharedService.IsInGpose && _charaDataManager.BrioAvailable)
        {
            SpheneUI.Layout.Callout("To attach pose and world data you need to be in GPose.",
                FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudYellow);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
        else if (!_charaDataManager.BrioAvailable)
        {
            SpheneUI.Layout.Callout("To attach pose and world data Brio requires to be installed.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        foreach (var pose in updateDto.PoseList)
        {
            using var poseId = ImRaii.PushId("pose" + poseNumber);
            bool poseHasChanges = updateDto.PoseHasChanges(pose);
            bool isDeletion = pose.Description == null && pose.WorldData == null && pose.PoseData == null;

            SpheneUI.Layout.Group(() =>
            {
                // Line 1: pose number + pending markers + description, row actions right.
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Muted($"{poseNumber}.");
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);

                if (pose.Id == null)
                {
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Icons.Icon(FontAwesomeIcon.Plus, ImGuiColors.DalamudYellow);
                    SpheneUI.AttachTooltip("This pose has not been added to the server yet. Save changes to upload this Pose data.");
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                }
                if (poseHasChanges)
                {
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
                    SpheneUI.AttachTooltip("This pose has changes that have not been saved to the server yet.");
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                }

                var trailingReserve = iconBtn + rowInset + (poseHasChanges ? iconBtn + spacingX : 0f);
                if (isDeletion)
                {
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Text.Colored("Pose scheduled for deletion", ImGuiColors.DalamudYellow);
                }
                else
                {
                    var desc = pose.Description ?? string.Empty;
                    var descWidth = ImGui.GetContentRegionAvail().X - trailingReserve - spacingX;
                    ImGui.SetNextItemWidth(MathF.Max(descWidth, iconBtn));
                    if (SpheneUI.Controls.InputTextWithHint("##description", "Description", ref desc, 100))
                    {
                        pose.Description = desc;
                        updateDto.UpdatePoseList();
                    }
                }

                ImGui.SameLine();
                SpheneUI.Controls.DockToCardEdge(trailingReserve);
                if (poseHasChanges)
                {
                    if (SpheneUI.Controls.IconButton("##undoPose", FontAwesomeIcon.Undo, size: iconBtn))
                    {
                        updateDto.RevertDeletion(pose);
                    }
                    SpheneUI.AttachTooltip("Undo pending changes / restore this pose");
                    ImGui.SameLine(0f, spacingX);
                }
                if (SpheneUI.Controls.IconButton("##deletePose", FontAwesomeIcon.Trash, size: iconBtn))
                {
                    updateDto.RemovePose(pose);
                }
                SpheneUI.AttachTooltip("Delete this pose entry");

                // Line 2: pose-data and world-data attachment clusters.
                if (!isDeletion)
                {
                    bool hasPoseData = !string.IsNullOrEmpty(pose.PoseData);
                    var worldData = pose.WorldData;
                    bool hasWorldData = worldData.HasValue;

                    SpheneUI.Icons.Icon(FontAwesomeIcon.Running, SpheneUI.Colors.Bool(hasPoseData));
                    SpheneUI.AttachTooltip(hasPoseData
                        ? "This pose entry has pose data attached"
                        : "This pose entry has no pose data attached");
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    using (ImRaii.Disabled(attachDisabled))
                    {
                        if (SpheneUI.Controls.IconButton("##setPoseData", FontAwesomeIcon.Plus, size: iconBtn))
                        {
                            _charaDataManager.AttachPoseData(pose, updateDto);
                        }
                        SpheneUI.AttachTooltip("Apply current pose data to pose");
                    }
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    using (ImRaii.Disabled(!hasPoseData))
                    {
                        if (SpheneUI.Controls.IconButton("##clearPoseData", FontAwesomeIcon.Trash, size: iconBtn))
                        {
                            pose.PoseData = string.Empty;
                            updateDto.UpdatePoseList();
                        }
                        SpheneUI.AttachTooltip("Delete current pose data from pose");
                    }

                    ImGui.SameLine(0f, SpheneUI.Spacing.X16.Scaled);
                    SpheneUI.Icons.Icon(FontAwesomeIcon.Globe, SpheneUI.Colors.Bool(hasWorldData));
                    var tooltipText = hasWorldData
                        ? "This pose has world data attached." + SpheneUI.TooltipSeparator + "Click to show location on map"
                        : "This pose has no world data attached.";
                    SpheneUI.AttachTooltip(tooltipText);
                    if (hasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    {
                        var wd = worldData.GetValueOrDefault();
                        if (_dalamudUtilService.MapData.Value.TryGetValue(wd.LocationInfo.MapId, out var mapInfo))
                        {
                            _dalamudUtilService.SetMarkerAndOpenMap(position: new Vector3(wd.PositionX, wd.PositionY, wd.PositionZ), mapInfo.Map);
                        }
                    }
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    using (ImRaii.Disabled(attachDisabled))
                    {
                        if (SpheneUI.Controls.IconButton("##setWorldData", FontAwesomeIcon.Plus, size: iconBtn))
                        {
                            _charaDataManager.AttachWorldData(pose, updateDto);
                        }
                        SpheneUI.AttachTooltip("Apply current world position data to pose");
                    }
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    using (ImRaii.Disabled(!hasWorldData))
                    {
                        if (SpheneUI.Controls.IconButton("##clearWorldData", FontAwesomeIcon.Trash, size: iconBtn))
                        {
                            pose.WorldData = default(WorldData);
                            updateDto.UpdatePoseList();
                        }
                        SpheneUI.AttachTooltip("Delete current world position data from pose");
                    }
                }
            }, 4f, ImGui.GetContentRegionAvail().X);

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            poseNumber++;
        }
    }

    private void DrawMcdOnline()
    {
        DrawPageTitle("Character Data Online",
            "Your character data stored on the server — create codes, edit entries, and manage sharing.");

        DrawHelpFoldout("In this tab you can create, view and edit your own Sphene Character Data that is stored on the server." + Environment.NewLine + Environment.NewLine
            + "Sphene Character Data Online functions similar to the previous MCDF standard for exporting your character, except that you do not have to send a file to the other person but solely a code." + Environment.NewLine + Environment.NewLine
            + "There would be a bit too much to explain here on what you can do here in its entirety, however, all elements in this tab have help texts attached what they are used for. Please review them carefully." + Environment.NewLine + Environment.NewLine
            + "Be mindful that when you share your Character Data with other people there is a chance that, with the help of unsanctioned 3rd party plugins, your appearance could be stolen irreversibly, just like when using MCDF.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // Toolbar: refresh + create on the left, server capacity on the right.
        using (ImRaii.Disabled((!_charaDataManager.GetAllDataTask?.IsCompleted ?? false)
            || (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)))
        {
            if (SpheneUI.Controls.SegmentButton("Download your Character Data", FontAwesomeIcon.ArrowCircleDown))
            {
                var token = _disposalCts?.Token ?? CancellationToken.None;
                _ = _charaDataManager.GetAllData(token);
            }
        }
        if (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
        {
            SpheneUI.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!_charaDataManager.Initialized || _charaDataManager.DataCreationTask != null
            || _charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData))
        {
            if (SpheneUI.Controls.SegmentButton("New Entry", FontAwesomeIcon.Plus,
                    accent: SpheneUI.Palette.EtherealPurple))
            {
                var token2 = _closalCts?.Token ?? CancellationToken.None;
                _charaDataManager.CreateCharaDataEntry(token2);
                _selectNewEntry = true;
            }
        }
        if (_charaDataManager.DataCreationTask != null)
        {
            SpheneUI.AttachTooltip("You can only create new character data every few seconds. Please wait.");
        }
        if (!_charaDataManager.Initialized)
        {
            SpheneUI.AttachTooltip("Please use the button \"Download your Character Data\" once before you can add new data entries.");
        }

        if (_charaDataManager.Initialized)
        {
            var countText = $"{_charaDataManager.OwnCharaData.Count}/{_charaDataManager.MaxCreatableCharaData} entries";
            var countW = ImGui.CalcTextSize(countText).X + ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SameLine();
            SpheneUI.Controls.DockToCardEdge(countW);
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Muted(countText);
        }

        if (_charaDataManager.Initialized
            && _charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout("You have reached the maximum Character Data entries and cannot create more.",
                FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudYellow);
        }

        if (_charaDataManager.DataCreationTask != null && !_charaDataManager.DataCreationTask.IsCompleted)
        {
            SpheneUI.Text.ColoredWrapped("Creating new character data entry on server...", ImGuiColors.DalamudYellow);
        }
        else if (_charaDataManager.DataCreationTask is { IsCompletedSuccessfully: true } creationTask)
        {
            var color = creationTask.Result.Success ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed;
            SpheneUI.Text.ColoredWrapped(creationTask.Result.Output, color);
        }
        else if (_charaDataManager.DataCreationTask is { IsFaulted: true })
        {
            SpheneUI.Text.ColoredWrapped("Failed to create the entry on server — check the log and try again.", ImGuiColors.DalamudRed);
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        int activeFilters = 0;
        if (!string.IsNullOrEmpty(_createFilter)) activeFilters++;
        if (_createOnlyShowFav) activeFilters++;
        if (_createOnlyShowNotDownloadable) activeFilters++;
        SpheneUI.Layout.Collapsible("Filters##mcdFilters", () =>
        {
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
            SpheneUI.Controls.InputTextWithHint("##createFilterAll", "Filter by code or description", ref _createFilter, 200);
            SpheneUI.Controls.Toggle("Only favorites", ref _createOnlyShowFav);
            SpheneUI.Controls.Toggle("Only entries that cannot be downloaded", ref _createOnlyShowNotDownloadable);
        }, icon: FontAwesomeIcon.Filter,
            hint: activeFilters == 0 ? null : $"{activeFilters} active");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var filteredEntries = _charaDataManager.OwnCharaData.Values
            .Where(v =>
            {
                bool show = true;
                if (!string.IsNullOrWhiteSpace(_createFilter))
                {
                    show &= v.FullId.Contains(_createFilter, StringComparison.OrdinalIgnoreCase)
                        || (v.Description ?? string.Empty).Contains(_createFilter, StringComparison.OrdinalIgnoreCase);
                }
                if (_createOnlyShowFav)
                {
                    show &= _configService.Current.FavoriteCodes.ContainsKey(v.FullId);
                }
                if (_createOnlyShowNotDownloadable)
                {
                    show &= !(!v.HasMissingFiles && !string.IsNullOrEmpty(v.GlamourerData));
                }
                return show;
            }).OrderBy(b => b.CreatedDate).ToList();

        if (!_charaDataManager.Initialized)
        {
            SpheneUI.Layout.Callout("Press \"Download your Character Data\" once to load your entries from the server.",
                FontAwesomeIcon.CloudDownloadAlt, ImGuiColors.DalamudGrey);
        }
        else if (filteredEntries.Count == 0)
        {
            SpheneUI.Layout.Callout(_charaDataManager.OwnCharaData.Count == 0
                    ? "No character data entries yet — create one with \"New Entry\"."
                    : "No entries match the active filters.",
                FontAwesomeIcon.Search, ImGuiColors.DalamudGrey);
        }
        else
        {
            // The list scrolls internally once it exceeds a few rows so the
            // editor below keeps a stable position on the page.
            var rowH = ImGui.GetFrameHeight() * 3f + ImGui.GetStyle().ItemSpacing.Y * 2f
                + ImGui.GetStyle().ItemInnerSpacing.Y * 2f;
            var listH = MathF.Min(filteredEntries.Count * rowH + ImGui.GetStyle().ItemSpacing.Y,
                340f * SpheneUI.Scale);
            if (SpheneUI.Layout.BeginScrollArea("mcdOnlineList",
                    new Vector2(SpheneUI.Layout.CardAlignedAvailX(), listH), border: true))
            {
                foreach (var entry in filteredEntries)
                {
                    DrawMcdOnlineEntry(entry);
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
            }
            SpheneUI.Layout.EndScrollArea();
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var charaDataEntries = _charaDataManager.OwnCharaData.Count;
        if (charaDataEntries != _dataEntries && _selectNewEntry && _charaDataManager.OwnCharaData.Any())
        {
            SelectedDtoId = _charaDataManager.OwnCharaData.OrderBy(o => o.Value.CreatedDate).Last().Value.Id;
            _selectNewEntry = false;
        }
        _dataEntries = _charaDataManager.OwnCharaData.Count;

        _ = _charaDataManager.OwnCharaData.TryGetValue(SelectedDtoId, out var dto);
        DrawEditCharaData(dto);
    }

    /// <summary>
    /// One entry in the online list: code + favorite star, status icons right,
    /// description and meta line below — click anywhere on the text to edit it.
    /// </summary>
    private void DrawMcdOnlineEntry(CharaDataFullExtendedDto entry)
    {
        using var id = ImRaii.PushId(entry.Id);
        var isSelected = string.Equals(entry.Id, SelectedDtoId, StringComparison.Ordinal);
        var uDto = _charaDataManager.GetUpdateDto(entry.Id);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var rowInset = ImGui.GetStyle().ItemInnerSpacing.X;
        var iconW = SpheneUI.Icons.Size(FontAwesomeIcon.Check).X;

        bool isDownloadable = !entry.HasMissingFiles && !string.IsNullOrEmpty(entry.GlamourerData);
        bool hasGlamourerData = !string.IsNullOrEmpty(entry.GlamourerData);
        bool hasCustomizeData = !string.IsNullOrEmpty(entry.CustomizeData);
        bool hasExpiry = !Equals(DateTime.MaxValue, entry.ExpiryDate);

        var clusterW = iconW * 3f + spacingX * 3f + (hasExpiry ? iconW + spacingX : 0f) + rowInset;

        SpheneUI.Layout.Group(() =>
        {
            // Line 1: caret + star + code, status cluster right.
            ImGui.AlignTextToFramePadding();
            SpheneUI.Icons.Icon(FontAwesomeIcon.CaretRight,
                isSelected ? SpheneUI.Palette.EtherealPurple : new Vector4(0f, 0f, 0f, 0f));
            ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            DrawAddOrRemoveFavorite(entry);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            var codeColor = (uDto?.HasChanges ?? false) ? ImGuiColors.DalamudYellow
                : isSelected ? SpheneUI.Palette.EtherealPurple : ImGuiColors.DalamudWhite;
            SpheneUI.Text.Colored(entry.FullId, codeColor);
            if (uDto?.HasChanges ?? false)
            {
                SpheneUI.AttachTooltip("This entry has unsaved changes");
            }
            if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

            ImGui.SameLine();
            SpheneUI.Controls.DockToCardEdge(clusterW);
            SpheneUI.Icons.BoolIcon(isDownloadable, false);
            SpheneUI.AttachTooltip(isDownloadable ? "Can be downloaded by others"
                : "Cannot be downloaded: has missing files or data — review this entry");
            ImGui.SameLine();
            SpheneUI.Icons.BoolIcon(hasGlamourerData, false);
            SpheneUI.AttachTooltip(hasGlamourerData ? "Has Glamourer data attached" : "No Glamourer data attached");
            ImGui.SameLine();
            SpheneUI.Icons.BoolIcon(hasCustomizeData, false);
            SpheneUI.AttachTooltip(hasCustomizeData ? "Has Customize+ data attached" : "No Customize+ data attached");
            if (hasExpiry)
            {
                ImGui.SameLine();
                SpheneUI.Icons.Icon(FontAwesomeIcon.Clock, ImGuiColors.DalamudYellow);
                SpheneUI.AttachTooltip($"This entry will expire on {entry.ExpiryDate.ToLocalTime()}");
            }

            // Line 2: description.
            if (string.IsNullOrEmpty(entry.Description))
            {
                SpheneUI.Text.Muted("No description set");
            }
            else
            {
                SpheneUI.Text.MutedWrapped(entry.Description);
            }
            if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
            SpheneUI.AttachTooltip(entry.Description);

            // Line 3: meta summary.
            var fileCount = (entry.FileGamePaths ?? []).Concat(entry.FileSwaps ?? []).Count();
            SpheneUI.Text.Muted($"Created {entry.CreatedDate.ToLocalTime():g} · Updated {entry.UpdatedDate.ToLocalTime():g}"
                + $" · {entry.DownloadCount} downloads · {fileCount} files");
            if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
        }, 5f, SpheneUI.Layout.CardAlignedAvailX());
    }

    bool _selectNewEntry = false;
    int _dataEntries = 0;

    private void DrawSpecific(CharaDataExtendedUpdateDto updateDto)
    {
        SpheneUI.Layout.Collapsible("Access for Specific Individuals / Syncshells", () =>
        {
            // Two bounded columns — the old SameLine+Space layout overflowed on
            // narrower windows and the list boxes used fixed pixel sizes.
            if (ImGui.BeginTable("specificAccess", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupColumn("Individuals", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableSetupColumn("Syncshells", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableNextColumn();
                DrawSpecificIndividuals(updateDto);
                ImGui.TableNextColumn();
                DrawSpecificGroups(updateDto);
                ImGui.EndTable();
            }

            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }, icon: FontAwesomeIcon.UserLock);
    }

    private void DrawSpecificIndividuals(CharaDataExtendedUpdateDto updateDto)
    {
        using var id = ImRaii.PushId("user");
        SpheneUI.Text.Muted("Allowed Individuals");
        InputComboHybrid("##AliasToAdd", "##AliasToAddPicker", ref _specificIndividualAdd, _pairManager.PairsWithGroups.Keys,
            static pair => (pair.UserData.UID, pair.UserData.Alias, pair.UserData.AliasOrUID, pair.GetNote()));
        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_specificIndividualAdd)
            || updateDto.UserList.Any(f => string.Equals(f.UID, _specificIndividualAdd, StringComparison.Ordinal) || string.Equals(f.Alias, _specificIndividualAdd, StringComparison.Ordinal))))
        {
            if (SpheneUI.Controls.IconButton("##addUser", FontAwesomeIcon.Plus, size: ImGui.GetFrameHeight()))
            {
                updateDto.AddUserToList(_specificIndividualAdd);
                _specificIndividualAdd = string.Empty;
            }
        }
        SpheneUI.Controls.HelpText("Users added to this list will be able to access this character data regardless of your pause or pair state with them." + SpheneUI.TooltipSeparator
            + "Note: Mistyped entries will be automatically removed on updating data to server.");

        var listHeight = 140f * SpheneUI.Scale;
        using (var lb = ImRaii.ListBox("##userList", new(-1f, listHeight)))
        {
            foreach (var user in updateDto.UserList)
            {
                var userString = string.IsNullOrEmpty(user.Alias) ? user.UID : $"{user.Alias} ({user.UID})";
                if (SpheneUI.Controls.Selectable(userString, string.Equals(user.UID, _selectedSpecificUserIndividual, StringComparison.Ordinal)))
                {
                    _selectedSpecificUserIndividual = user.UID;
                }
            }
        }

        using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedSpecificUserIndividual)))
        {
            if (SpheneUI.Controls.SmallButton("Remove selected User"))
            {
                updateDto.RemoveUserFromList(_selectedSpecificUserIndividual);
                _selectedSpecificUserIndividual = string.Empty;
            }
        }

        using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
        {
            if (SpheneUI.Controls.SmallButton("Apply list to all MCDO entries"))
            {
                foreach (var own in _charaDataManager.OwnCharaData.Values.Where(k => !string.Equals(k.Id, updateDto.Id, StringComparison.Ordinal)))
                {
                    var otherUpdateDto = _charaDataManager.GetUpdateDto(own.Id);
                    if (otherUpdateDto == null) continue;
                    foreach (var user in otherUpdateDto.UserList.Select(k => k.UID).Concat(otherUpdateDto.AllowedUsers ?? []).Distinct(StringComparer.Ordinal).ToList())
                    {
                        otherUpdateDto.RemoveUserFromList(user);
                    }
                    foreach (var user in updateDto.UserList.Select(k => k.UID).Concat(updateDto.AllowedUsers ?? []).Distinct(StringComparer.Ordinal).ToList())
                    {
                        otherUpdateDto.AddUserToList(user);
                    }
                }
            }
        }
        SpheneUI.AttachTooltip("This will apply the current list of allowed specific individuals to ALL of your MCDO entries." + SpheneUI.TooltipSeparator
            + "Hold CTRL to enable.");
    }

    private void DrawSpecificGroups(CharaDataExtendedUpdateDto updateDto)
    {
        using var id = ImRaii.PushId("group");
        SpheneUI.Text.Muted("Allowed Syncshells");
        InputComboHybrid("##GroupAliasToAdd", "##GroupAliasToAddPicker", ref _specificGroupAdd, _pairManager.Groups.Keys,
            group => (group.GID, group.Alias, group.AliasOrGID, _serverConfigurationManager.GetNoteForGid(group.GID)));
        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_specificGroupAdd)
            || updateDto.GroupList.Any(f => string.Equals(f.GID, _specificGroupAdd, StringComparison.Ordinal) || string.Equals(f.Alias, _specificGroupAdd, StringComparison.Ordinal))))
        {
            if (SpheneUI.Controls.IconButton("##addGroup", FontAwesomeIcon.Plus, size: ImGui.GetFrameHeight()))
            {
                updateDto.AddGroupToList(_specificGroupAdd);
                _specificGroupAdd = string.Empty;
            }
        }
        SpheneUI.Controls.HelpText("Users in Syncshells added to this list will be able to access this character data regardless of your pause or pair state with them." + SpheneUI.TooltipSeparator
            + "Note: Mistyped entries will be automatically removed on updating data to server.");

        var listHeight = 140f * SpheneUI.Scale;
        using (var lb = ImRaii.ListBox("##groupList", new(-1f, listHeight)))
        {
            foreach (var group in updateDto.GroupList)
            {
                var userString = string.IsNullOrEmpty(group.Alias) ? group.GID : $"{group.Alias} ({group.GID})";
                if (SpheneUI.Controls.Selectable(userString, string.Equals(group.GID, _selectedSpecificGroupIndividual, StringComparison.Ordinal)))
                {
                    _selectedSpecificGroupIndividual = group.GID;
                }
            }
        }

        using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedSpecificGroupIndividual)))
        {
            if (SpheneUI.Controls.SmallButton("Remove selected Syncshell"))
            {
                updateDto.RemoveGroupFromList(_selectedSpecificGroupIndividual);
                _selectedSpecificGroupIndividual = string.Empty;
            }
        }

        using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
        {
            if (SpheneUI.Controls.SmallButton("Apply list to all MCDO entries"))
            {
                foreach (var own in _charaDataManager.OwnCharaData.Values.Where(k => !string.Equals(k.Id, updateDto.Id, StringComparison.Ordinal)))
                {
                    var otherUpdateDto = _charaDataManager.GetUpdateDto(own.Id);
                    if (otherUpdateDto == null) continue;
                    foreach (var group in otherUpdateDto.GroupList.Select(k => k.GID).Concat(otherUpdateDto.AllowedGroups ?? []).Distinct(StringComparer.Ordinal).ToList())
                    {
                        otherUpdateDto.RemoveGroupFromList(group);
                    }
                    foreach (var group in updateDto.GroupList.Select(k => k.GID).Concat(updateDto.AllowedGroups ?? []).Distinct(StringComparer.Ordinal).ToList())
                    {
                        otherUpdateDto.AddGroupToList(group);
                    }
                }
            }
        }
        SpheneUI.AttachTooltip("This will apply the current list of allowed specific syncshells to ALL of your MCDO entries." + SpheneUI.TooltipSeparator
            + "Hold CTRL to enable.");
    }

    private void InputComboHybrid<T>(string inputId, string comboId, ref string value, IEnumerable<T> comboEntries,
        Func<T, (string Id, string? Alias, string AliasOrId, string? Note)> parseEntry)
    {
        const float ComponentWidth = 200;
        SpheneUI.Layout.ScaledNextItemWidth(ComponentWidth - ImGui.GetFrameHeight());
        SpheneUI.Controls.InputText(inputId, ref value, 20);
        ImGui.SameLine(0.0f, 0.0f);

        using var combo = ImRaii.Combo(comboId, string.Empty, ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft);
        if (!combo)
        {
            return;
        }

        if (_openComboHybridEntries is null || !string.Equals(_openComboHybridId, comboId, StringComparison.Ordinal))
        {
            var valueSnapshot = value;
            _openComboHybridEntries = comboEntries
                .Select(parseEntry)
                .Where(entry => entry.Id.Contains(valueSnapshot, StringComparison.OrdinalIgnoreCase)
                    || (entry.Alias is not null && entry.Alias.Contains(valueSnapshot, StringComparison.OrdinalIgnoreCase))
                    || (entry.Note is not null && entry.Note.Contains(valueSnapshot, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => entry.Note is null ? entry.AliasOrId : $"{entry.Note} ({entry.AliasOrId})", StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _openComboHybridId = comboId;
        }
        _comboHybridUsedLastFrame = true;

        // Is there a better way to handle this?
        var width = ComponentWidth - 2 * ImGui.GetStyle().FramePadding.X - (_openComboHybridEntries.Length > 8 ? ImGui.GetStyle().ScrollbarSize : 0);
        foreach (var (id, alias, aliasOrId, note) in _openComboHybridEntries)
        {
            var selected = !string.IsNullOrEmpty(value)
                && (string.Equals(id, value, StringComparison.Ordinal) || string.Equals(alias, value, StringComparison.Ordinal));
            using var font = ImRaii.PushFont(UiBuilder.MonoFont, note is null);
            if (SpheneUI.Controls.Selectable(note is null ? aliasOrId : $"{note} ({aliasOrId})", selected, ImGuiSelectableFlags.None, new(width, 0)))
            {
                value = aliasOrId;
            }
        }
    }
}
