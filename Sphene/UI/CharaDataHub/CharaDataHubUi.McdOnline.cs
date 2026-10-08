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
    private string _createDescFilter = string.Empty;
    private string _createCodeFilter = string.Empty;
    private bool _createOnlyShowFav = false;
    private bool _createOnlyShowNotDownloadable = false;

    private void DrawEditCharaData(CharaDataFullExtendedDto? dataDto)
    {
        using var imguiid = ImRaii.PushId(dataDto?.Id ?? "NoData");

        if (dataDto == null)
        {
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Text.GroupedCentered("Select an entry above to edit its data.", ImGuiColors.DalamudYellow);
            return;
        }

        var updateDto = _charaDataManager.GetUpdateDto(dataDto.Id);

        if (updateDto == null)
        {
            SpheneUI.Text.GroupedCentered("Something went awfully wrong and there's no update DTO. Try updating Character Data via the button above.", ImGuiColors.DalamudYellow);
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
            ImGuiHelpers.ScaledDummy(5);
        }

        var indent = ImRaii.PushIndent(10f);
        if (canUpdate || _charaDataManager.UploadTask != null)
        {
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Layout.Group(() =>
            {
                if (canUpdate)
                {
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Text.ColoredWrapped("Warning: You have unsaved changes!", ImGuiColors.DalamudRed);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted))
                    {
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowCircleUp, "Save to Server"))
                        {
                            _charaDataManager.UploadCharaData(dataDto.Id);
                        }
                        ImGui.SameLine();
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Undo all changes"))
                        {
                            updateDto.UndoChanges();
                        }
                    }
                    if (_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted)
                    {
                        SpheneUI.Text.ColoredWrapped("Updating data on server, please wait.", ImGuiColors.DalamudYellow);
                    }
                }

                if (!_charaDataManager.UploadTask?.IsCompleted ?? false)
                {
                    DisableDisabled(() =>
                    {
                        if (_charaDataManager.UploadProgress != null)
                        {
                            SpheneUI.Text.ColoredWrapped(_charaDataManager.UploadProgress.Value ?? string.Empty, ImGuiColors.DalamudYellow);
                        }
                        if ((!_charaDataManager.UploadTask?.IsCompleted ?? false) && SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Cancel Upload"))
                        {
                            _charaDataManager.CancelUpload();
                        }
                        else if (_charaDataManager.UploadTask?.IsCompleted ?? false)
                        {
                            var color = SpheneUI.Colors.Bool(_charaDataManager.UploadTask.Result.Success);
                            SpheneUI.Text.ColoredWrapped(_charaDataManager.UploadTask.Result.Output, color);
                        }
                    });
                }
                else if (_charaDataManager.UploadTask?.IsCompleted ?? false)
                {
                    var color = SpheneUI.Colors.Bool(_charaDataManager.UploadTask.Result.Success);
                    SpheneUI.Text.ColoredWrapped(_charaDataManager.UploadTask.Result.Output, color);
                }
            });
        }

        if (otherUpdates > 0)
        {
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Layout.Group(() =>
            {
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.ColoredWrapped($"You have {otherUpdates} other entries with unsaved changes.", ImGuiColors.DalamudYellow);
                ImGui.SameLine();
                using (ImRaii.Disabled(_charaDataManager.CharaUpdateTask != null && !_charaDataManager.CharaUpdateTask.IsCompleted))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowAltCircleUp, "Save all to server"))
                    {
                        _charaDataManager.UploadAllCharaData();
                    }
                }
            });
        }
        indent.Dispose();

        if (canUpdate || otherUpdates > 0 || (!_charaDataManager.CharaUpdateTask?.IsCompleted ?? false))
        {
            ImGuiHelpers.ScaledDummy(5);
        }

        using var child = ImRaii.Child("editChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);

        DrawEditCharaDataGeneral(dataDto, updateDto);
        ImGuiHelpers.ScaledDummy(5);
        DrawEditCharaDataAccessAndSharing(updateDto);
        ImGuiHelpers.ScaledDummy(5);
        DrawEditCharaDataAppearance(dataDto, updateDto);
        ImGuiHelpers.ScaledDummy(5);
        DrawEditCharaDataPoses(updateDto);
    }

    private void DrawEditCharaDataAccessAndSharing(CharaDataExtendedUpdateDto updateDto)
    {
        SpheneUI.Text.Big("Access and Sharing");

        SpheneUI.Layout.ScaledNextItemWidth(200);
        var dtoAccessType = updateDto.AccessType;
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
        SpheneUI.Controls.HelpText("This regulates how you want to distribute this character data." + SpheneUI.TooltipSeparator
            + "Code Only: People require to have the code to download this character data" + Environment.NewLine
            + "Shared: People that are allowed through 'Access Restrictions' will have this character data entry displayed in 'Shared with You' (it can also be accessed through the code)" + SpheneUI.TooltipSeparator
            + "Note: Shared with Access Restriction 'Everyone' is the same as shared with Access Restriction 'All Pairs', it will not show up for everyone but just your pairs.");

        ImGuiHelpers.ScaledDummy(10f);
    }

    private void DrawEditCharaDataAppearance(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        SpheneUI.Text.Big("Appearance");

        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Set Appearance to Current Appearance"))
        {
            _charaDataManager.SetAppearanceData(dataDto.Id);
        }
        SpheneUI.Controls.HelpText("This will overwrite the appearance data currently stored in this Character Data entry with your current appearance.");
        ImGui.SameLine();
        using (ImRaii.Disabled(dataDto.HasMissingFiles || !updateDto.IsAppearanceEqual || _charaDataManager.DataApplicationTask != null))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.CheckCircle, "Preview Saved Apperance on Self"))
            {
                _charaDataManager.ApplyDataToSelf(dataDto);
            }
        }
        SpheneUI.Controls.HelpText("This will download and apply the saved character data to yourself. Once loaded it will automatically revert itself within 15 seconds." + SpheneUI.TooltipSeparator
            + "Note: Weapons will not be displayed correctly unless using the same job as the saved data.");

        SpheneUI.Text.Plain("Contains Glamourer Data");
        ImGui.SameLine();
        bool hasGlamourerdata = !string.IsNullOrEmpty(updateDto.GlamourerData);
        SpheneUI.Layout.ScaledSameLine(200);
        SpheneUI.Icons.BoolIcon(hasGlamourerdata, false);

        SpheneUI.Text.Plain("Contains Files");
        var hasFiles = (updateDto.FileGamePaths ?? []).Any() || (dataDto.OriginalFiles.Any());
        SpheneUI.Layout.ScaledSameLine(200);
        SpheneUI.Icons.BoolIcon(hasFiles, false);
        if (hasFiles && updateDto.IsAppearanceEqual)
        {
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(20, 1);
            ImGui.SameLine();
            var pos = ImGui.GetCursorPosX();
            ImGui.NewLine();
            ImGui.SameLine(pos);
            SpheneUI.Text.Plain($"{dataDto.FileGamePaths.DistinctBy(k => k.HashOrFileSwap).Count()} unique file hashes (original upload: {dataDto.OriginalFiles.DistinctBy(k => k.HashOrFileSwap).Count()} file hashes)");
            ImGui.NewLine();
            ImGui.SameLine(pos);
            SpheneUI.Text.Plain($"{dataDto.FileGamePaths.Count} associated game paths");
            ImGui.NewLine();
            ImGui.SameLine(pos);
            SpheneUI.Text.Plain($"{dataDto.FileSwaps!.Count} file swaps");
            ImGui.NewLine();
            ImGui.SameLine(pos);
            if (!dataDto.HasMissingFiles)
            {
                SpheneUI.Text.ColoredWrapped("All files to download this character data are present on the server", ImGuiColors.HealerGreen);
            }
            else
            {
                SpheneUI.Text.ColoredWrapped($"{dataDto.MissingFiles.DistinctBy(k => k.HashOrFileSwap).Count()} files to download this character data are missing on the server", ImGuiColors.DalamudRed);
                ImGui.NewLine();
                ImGui.SameLine(pos);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowCircleUp, "Attempt to upload missing files and restore Character Data"))
                {
                    _charaDataManager.UploadMissingFiles(dataDto.Id);
                }
            }
        }
        else if (hasFiles && !updateDto.IsAppearanceEqual)
        {
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(20, 1);
            ImGui.SameLine();
            SpheneUI.Text.ColoredWrapped("New data was set. It may contain files that require to be uploaded (will happen on Saving to server)", ImGuiColors.DalamudYellow);
        }

        SpheneUI.Text.Plain("Contains Manipulation Data");
        bool hasManipData = !string.IsNullOrEmpty(updateDto.ManipulationData);
        SpheneUI.Layout.ScaledSameLine(200);
        SpheneUI.Icons.BoolIcon(hasManipData, false);

        SpheneUI.Text.Plain("Contains Customize+ Data");
        ImGui.SameLine();
        bool hasCustomizeData = !string.IsNullOrEmpty(updateDto.CustomizeData);
        SpheneUI.Layout.ScaledSameLine(200);
        SpheneUI.Icons.BoolIcon(hasCustomizeData, false);
    }

    private void DrawEditCharaDataGeneral(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        SpheneUI.Text.Big("General");
        string code = dataDto.FullId;
        using (ImRaii.Disabled())
        {
            SpheneUI.Layout.ScaledNextItemWidth(200);
            SpheneUI.Controls.InputText("##CharaDataCode", ref code, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        SpheneUI.Text.Plain("Chara Data Code");
        ImGui.SameLine();
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Copy))
        {
            ImGui.SetClipboardText(code);
        }
        SpheneUI.AttachTooltip("Copy Code to Clipboard");

        string creationTime = dataDto.CreatedDate.ToLocalTime().ToString();
        string updateTime = dataDto.UpdatedDate.ToLocalTime().ToString();
        string downloadCount = dataDto.DownloadCount.ToString();
        using (ImRaii.Disabled())
        {
            SpheneUI.Layout.ScaledNextItemWidth(200);
            SpheneUI.Controls.InputText("##CreationDate", ref creationTime, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        SpheneUI.Text.Plain("Creation Date");
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(20);
        ImGui.SameLine();
        using (ImRaii.Disabled())
        {
            SpheneUI.Layout.ScaledNextItemWidth(200);
            SpheneUI.Controls.InputText("##LastUpdate", ref updateTime, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        SpheneUI.Text.Plain("Last Update Date");
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(23);
        ImGui.SameLine();
        using (ImRaii.Disabled())
        {
            SpheneUI.Layout.ScaledNextItemWidth(50);
            SpheneUI.Controls.InputText("##DlCount", ref downloadCount, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        SpheneUI.Text.Plain("Download Count");

        string description = updateDto.Description;
        SpheneUI.Layout.ScaledNextItemWidth(735);
        if (SpheneUI.Controls.InputText("##Description", ref description, 200))
        {
            updateDto.Description = description;
        }
        ImGui.SameLine();
        SpheneUI.Text.Plain("Description");
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
        ImGuiHelpers.ScaledDummy(5);

        using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Delete Character Data"))
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
        SpheneUI.Text.Big("Poses");
        var poseCount = updateDto.PoseList.Count();
        using (ImRaii.Disabled(poseCount >= maxPoses))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Add new Pose"))
            {
                updateDto.AddPose();
            }
        }
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, poseCount == maxPoses))
            SpheneUI.Text.Plain($"{poseCount}/{maxPoses} poses attached");
        ImGuiHelpers.ScaledDummy(5);

        using var indent = ImRaii.PushIndent(10f);
        int poseNumber = 1;

        if (!_uiSharedService.IsInGpose && _charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Text.GroupedCentered("To attach pose and world data you need to be in GPose.", ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(5);
        }
        else if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            SpheneUI.Text.GroupedCentered("To attach pose and world data Brio requires to be installed.", ImGuiColors.DalamudRed);
            ImGuiHelpers.ScaledDummy(5);
        }

        foreach (var pose in updateDto.PoseList)
        {
            ImGui.AlignTextToFramePadding();
            using var id = ImRaii.PushId("pose" + poseNumber);
            SpheneUI.Text.Plain(poseNumber.ToString());

            if (pose.Id == null)
            {
                SpheneUI.Layout.ScaledSameLine(50);
                SpheneUI.Icons.Icon(FontAwesomeIcon.Plus, ImGuiColors.DalamudYellow);
                SpheneUI.AttachTooltip("This pose has not been added to the server yet. Save changes to upload this Pose data.");
            }

            bool poseHasChanges = updateDto.PoseHasChanges(pose);
            if (poseHasChanges)
            {
                SpheneUI.Layout.ScaledSameLine(50);
                SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
                SpheneUI.AttachTooltip("This pose has changes that have not been saved to the server yet.");
            }

            SpheneUI.Layout.ScaledSameLine(75);
            if (pose.Description == null && pose.WorldData == null && pose.PoseData == null)
            {
                SpheneUI.Text.Colored("Pose scheduled for deletion", ImGuiColors.DalamudYellow);
            }
            else
            {
                var desc = pose.Description ?? string.Empty;
                if (SpheneUI.Controls.InputTextWithHint("##description", "Description", ref desc, 100))
                {
                    pose.Description = desc;
                    updateDto.UpdatePoseList();
                }
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Delete"))
                {
                    updateDto.RemovePose(pose);
                }

                ImGui.SameLine();
                ImGuiHelpers.ScaledDummy(10, 1);
                ImGui.SameLine();
                bool hasPoseData = !string.IsNullOrEmpty(pose.PoseData);
                SpheneUI.Icons.Icon(FontAwesomeIcon.Running, SpheneUI.Colors.Bool(hasPoseData));
                SpheneUI.AttachTooltip(hasPoseData
                    ? "This Pose entry has pose data attached"
                    : "This Pose entry has no pose data attached");
                ImGui.SameLine();

                using (ImRaii.Disabled(!_uiSharedService.IsInGpose || !(_charaDataManager.AttachingPoseTask?.IsCompleted ?? true) || !_charaDataManager.BrioAvailable))
                {
                    using var poseid = ImRaii.PushId("poseSet" + poseNumber);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                    {
                        _charaDataManager.AttachPoseData(pose, updateDto);
                    }
                    SpheneUI.AttachTooltip("Apply current pose data to pose");
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!hasPoseData))
                {
                    using var poseid = ImRaii.PushId("poseDelete" + poseNumber);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                    {
                        pose.PoseData = string.Empty;
                        updateDto.UpdatePoseList();
                    }
                    SpheneUI.AttachTooltip("Delete current pose data from pose");
                }

                ImGui.SameLine();
                ImGuiHelpers.ScaledDummy(10, 1);
                ImGui.SameLine();
                var worldData = pose.WorldData;
                bool hasWorldData = worldData.HasValue;
                SpheneUI.Icons.Icon(FontAwesomeIcon.Globe, SpheneUI.Colors.Bool(hasWorldData));
                var tooltipText = !hasWorldData ? "This Pose has no world data attached." : "This Pose has world data attached.";
                if (hasWorldData)
                {
                    tooltipText += SpheneUI.TooltipSeparator + "Click to show location on map";
                }
                SpheneUI.AttachTooltip(tooltipText);
                if (hasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    var wd = worldData.GetValueOrDefault();
                    _dalamudUtilService.SetMarkerAndOpenMap(position: new Vector3(wd.PositionX, wd.PositionY, wd.PositionZ),
                        _dalamudUtilService.MapData.Value[wd.LocationInfo.MapId].Map);
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!_uiSharedService.IsInGpose || !(_charaDataManager.AttachingPoseTask?.IsCompleted ?? true) || !_charaDataManager.BrioAvailable))
                {
                    using var worldId = ImRaii.PushId("worldSet" + poseNumber);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                    {
                        _charaDataManager.AttachWorldData(pose, updateDto);
                    }
                    SpheneUI.AttachTooltip("Apply current world position data to pose");
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!hasWorldData))
                {
                    using var worldId = ImRaii.PushId("worldDelete" + poseNumber);
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                    {
                        pose.WorldData = default(WorldData);
                        updateDto.UpdatePoseList();
                    }
                    SpheneUI.AttachTooltip("Delete current world position data from pose");
                }
            }

            if (poseHasChanges)
            {
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Undo"))
                {
                    updateDto.RevertDeletion(pose);
                }
            }

            poseNumber++;
        }
    }

    private void DrawMcdOnline()
    {
        SpheneUI.Text.Big("Sphene Character Data Online");

        DrawHelpFoldout("In this tab you can create, view and edit your own Sphene Character Data that is stored on the server." + Environment.NewLine + Environment.NewLine
            + "Sphene Character Data Online functions similar to the previous MCDF standard for exporting your character, except that you do not have to send a file to the other person but solely a code." + Environment.NewLine + Environment.NewLine
            + "There would be a bit too much to explain here on what you can do here in its entirety, however, all elements in this tab have help texts attached what they are used for. Please review them carefully." + Environment.NewLine + Environment.NewLine
            + "Be mindful that when you share your Character Data with other people there is a chance that, with the help of unsanctioned 3rd party plugins, your appearance could be stolen irreversibly, just like when using MCDF.");

        ImGuiHelpers.ScaledDummy(5);
        using (ImRaii.Disabled((!_charaDataManager.GetAllDataTask?.IsCompleted ?? false)
            || (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowCircleDown, "Download your Character Data from Server"))
            {
                var token = _disposalCts?.Token ?? CancellationToken.None;
                _ = _charaDataManager.GetAllData(token);
            }
        }
        if (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
        {
            SpheneUI.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }

        using (var table = ImRaii.Table("Own Character Data", 12, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
            new Vector2(ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X, 140 * ImGuiHelpers.GlobalScale)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Code");
                ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Created");
                ImGui.TableSetupColumn("Updated");
                ImGui.TableSetupColumn("Download Count", ImGuiTableColumnFlags.WidthFixed, 18 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Downloadable", ImGuiTableColumnFlags.WidthFixed, 18 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Files", ImGuiTableColumnFlags.WidthFixed, 32 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Glamourer", ImGuiTableColumnFlags.WidthFixed, 18 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Customize+", ImGuiTableColumnFlags.WidthFixed, 18 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Expires", ImGuiTableColumnFlags.WidthFixed, 18 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupScrollFreeze(0, 2);
                ImGui.TableHeadersRow();

                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));
                ImGui.TableNextColumn();
                SpheneUI.Controls.Checkbox("###createOnlyShowfav", ref _createOnlyShowFav);
                SpheneUI.AttachTooltip("Filter by favorites");
                ImGui.TableNextColumn();
                var x1 = ImGui.GetContentRegionAvail().X;
                ImGui.SetNextItemWidth(x1);
                SpheneUI.Controls.InputTextWithHint("###createFilterCode", "Filter by code", ref _createCodeFilter, 200);
                ImGui.TableNextColumn();
                var x2 = ImGui.GetContentRegionAvail().X;
                ImGui.SetNextItemWidth(x2);
                SpheneUI.Controls.InputTextWithHint("###createFilterDesc", "Filter by description", ref _createDescFilter, 200);
                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));
                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));
                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));
                ImGui.TableNextColumn();
                SpheneUI.Controls.Checkbox("###createShowNotDl", ref _createOnlyShowNotDownloadable);
                SpheneUI.AttachTooltip("Filter by not downloadable");
                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));
                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));
                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));
                ImGui.TableNextColumn();
                ImGui.Dummy(new(0, 0));


                foreach (var entry in _charaDataManager.OwnCharaData.Values
                    .Where(v =>
                    {
                        bool show = true;
                        if (!string.IsNullOrWhiteSpace(_createCodeFilter))
                        {
                            show &= v.FullId.Contains(_createCodeFilter, StringComparison.OrdinalIgnoreCase);
                        }
                        if (!string.IsNullOrWhiteSpace(_createDescFilter))
                        {
                            show &= v.Description.Contains(_createDescFilter, StringComparison.OrdinalIgnoreCase);
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
                    }).OrderBy(b => b.CreatedDate))
                {
                    var uDto = _charaDataManager.GetUpdateDto(entry.Id);
                    ImGui.TableNextColumn();
                    if (string.Equals(entry.Id, SelectedDtoId, StringComparison.Ordinal))
                        SpheneUI.Icons.Icon(FontAwesomeIcon.CaretRight);

                    ImGui.TableNextColumn();
                    DrawAddOrRemoveFavorite(entry);

                    ImGui.TableNextColumn();
                    var idText = entry.FullId;
                    if (uDto?.HasChanges ?? false)
                    {
                        SpheneUI.Text.Colored(idText, ImGuiColors.DalamudYellow);
                        SpheneUI.AttachTooltip("This entry has unsaved changes");
                    }
                    else
                    {
                        SpheneUI.Text.Plain(idText);
                    }
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain(entry.Description);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    SpheneUI.AttachTooltip(entry.Description);

                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain(entry.CreatedDate.ToLocalTime().ToString());
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain(entry.UpdatedDate.ToLocalTime().ToString());
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain(entry.DownloadCount.ToString());
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;

                    ImGui.TableNextColumn();
                    bool isDownloadable = !entry.HasMissingFiles
                        && !string.IsNullOrEmpty(entry.GlamourerData);
                    SpheneUI.Icons.BoolIcon(isDownloadable, false);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    SpheneUI.AttachTooltip(isDownloadable ? "Can be downloaded by others" : "Cannot be downloaded: Has missing files or data, please review this entry manually");

                    ImGui.TableNextColumn();
                    var count = entry.FileGamePaths.Concat(entry.FileSwaps).Count();
                    SpheneUI.Text.Plain(count.ToString());
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    SpheneUI.AttachTooltip(count == 0 ? "No File data attached" : "Has File data attached");

                    ImGui.TableNextColumn();
                    bool hasGlamourerData = !string.IsNullOrEmpty(entry.GlamourerData);
                    SpheneUI.Icons.BoolIcon(hasGlamourerData, false);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    SpheneUI.AttachTooltip(string.IsNullOrEmpty(entry.GlamourerData) ? "No Glamourer data attached" : "Has Glamourer data attached");

                    ImGui.TableNextColumn();
                    bool hasCustomizeData = !string.IsNullOrEmpty(entry.CustomizeData);
                    SpheneUI.Icons.BoolIcon(hasCustomizeData, false);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    SpheneUI.AttachTooltip(string.IsNullOrEmpty(entry.CustomizeData) ? "No Customize+ data attached" : "Has Customize+ data attached");

                    ImGui.TableNextColumn();
                    FontAwesomeIcon eIcon = FontAwesomeIcon.None;
                    if (!Equals(DateTime.MaxValue, entry.ExpiryDate))
                        eIcon = FontAwesomeIcon.Clock;
                    SpheneUI.Icons.Icon(eIcon, ImGuiColors.DalamudYellow);
                    if (ImGui.IsItemClicked()) SelectedDtoId = entry.Id;
                    if (eIcon != FontAwesomeIcon.None)
                    {
                        SpheneUI.AttachTooltip($"This entry will expire on {entry.ExpiryDate.ToLocalTime()}");
                    }
                }
            }
        }

        using (ImRaii.Disabled(!_charaDataManager.Initialized || _charaDataManager.DataCreationTask != null || _charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "New Character Data Entry"))
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
            SpheneUI.AttachTooltip("Please use the button \"Get Own Chara Data\" once before you can add new data entries.");
        }

        if (_charaDataManager.Initialized)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Wrapped($"Chara Data Entries on Server: {_charaDataManager.OwnCharaData.Count}/{_charaDataManager.MaxCreatableCharaData}");
            if (_charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData)
            {
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.ColoredWrapped("You have reached the maximum Character Data entries and cannot create more.", ImGuiColors.DalamudYellow);
            }
        }

        if (_charaDataManager.DataCreationTask != null && !_charaDataManager.DataCreationTask.IsCompleted)
        {
            SpheneUI.Text.ColoredWrapped("Creating new character data entry on server...", ImGuiColors.DalamudYellow);
        }
        else if (_charaDataManager.DataCreationTask != null && _charaDataManager.DataCreationTask.IsCompleted)
        {
            var color = _charaDataManager.DataCreationTask.Result.Success ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed;
            SpheneUI.Text.ColoredWrapped(_charaDataManager.DataCreationTask.Result.Output, color);
        }

        ImGuiHelpers.ScaledDummy(10);
        SpheneUI.Layout.Separator();

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

    bool _selectNewEntry = false;
    int _dataEntries = 0;

    private void DrawSpecific(CharaDataExtendedUpdateDto updateDto)
    {
        SpheneUI.Layout.Tree("Access for Specific Individuals / Syncshells", () =>
        {
            using (ImRaii.PushId("user"))
            {
                using (ImRaii.Group())
                {
                    InputComboHybrid("##AliasToAdd", "##AliasToAddPicker", ref _specificIndividualAdd, _pairManager.PairsWithGroups.Keys,
                        static pair => (pair.UserData.UID, pair.UserData.Alias, pair.UserData.AliasOrUID, pair.GetNote()));
                    ImGui.SameLine();
                    using (ImRaii.Disabled(string.IsNullOrEmpty(_specificIndividualAdd)
                        || updateDto.UserList.Any(f => string.Equals(f.UID, _specificIndividualAdd, StringComparison.Ordinal) || string.Equals(f.Alias, _specificIndividualAdd, StringComparison.Ordinal))))
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                        {
                            updateDto.AddUserToList(_specificIndividualAdd);
                            _specificIndividualAdd = string.Empty;
                        }
                    }
                    ImGui.SameLine();
                    SpheneUI.Text.Plain("UID/Vanity UID to Add");
                    SpheneUI.Controls.HelpText("Users added to this list will be able to access this character data regardless of your pause or pair state with them." + SpheneUI.TooltipSeparator
                        + "Note: Mistyped entries will be automatically removed on updating data to server.");

                    using (var lb = ImRaii.ListBox("Allowed Individuals", new(200 * ImGuiHelpers.GlobalScale, 200 * ImGuiHelpers.GlobalScale)))
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
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Remove selected User"))
                        {
                            updateDto.RemoveUserFromList(_selectedSpecificUserIndividual);
                            _selectedSpecificUserIndividual = string.Empty;
                        }
                    }

                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                    {
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ExclamationTriangle, "Apply current Allowed Individuals to all MCDO entries"))
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
            }
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(20);
            ImGui.SameLine();

            using (ImRaii.PushId("group"))
            {
                using (ImRaii.Group())
                {
                    InputComboHybrid("##GroupAliasToAdd", "##GroupAliasToAddPicker", ref _specificGroupAdd, _pairManager.Groups.Keys,
                        group => (group.GID, group.Alias, group.AliasOrGID, _serverConfigurationManager.GetNoteForGid(group.GID)));
                    ImGui.SameLine();
                    using (ImRaii.Disabled(string.IsNullOrEmpty(_specificGroupAdd)
                        || updateDto.GroupList.Any(f => string.Equals(f.GID, _specificGroupAdd, StringComparison.Ordinal) || string.Equals(f.Alias, _specificGroupAdd, StringComparison.Ordinal))))
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                        {
                            updateDto.AddGroupToList(_specificGroupAdd);
                            _specificGroupAdd = string.Empty;
                        }
                    }
                    ImGui.SameLine();
                    SpheneUI.Text.Plain("GID/Vanity GID to Add");
                    SpheneUI.Controls.HelpText("Users in Syncshells added to this list will be able to access this character data regardless of your pause or pair state with them." + SpheneUI.TooltipSeparator
                        + "Note: Mistyped entries will be automatically removed on updating data to server.");

                    using (var lb = ImRaii.ListBox("Allowed Syncshells", new(200 * ImGuiHelpers.GlobalScale, 200 * ImGuiHelpers.GlobalScale)))
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
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Remove selected Syncshell"))
                        {
                            updateDto.RemoveGroupFromList(_selectedSpecificGroupIndividual);
                            _selectedSpecificGroupIndividual = string.Empty;
                        }
                    }

                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                    {
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ExclamationTriangle, "Apply current Allowed Syncshells to all MCDO entries"))
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
            }

            SpheneUI.Layout.Separator();
            ImGuiHelpers.ScaledDummy(5);
        });
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
