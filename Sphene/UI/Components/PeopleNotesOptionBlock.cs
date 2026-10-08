using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class PeopleNotesOptionBlock
{
    public static void DrawOverwriteExistingLabelsOption(ref bool overwriteExistingLabels, UiSharedService uiShared, string blockId = "OverwriteExistingLabels")
    {
        ImGui.PushID(blockId);
        try
        {
            SpheneUI.Controls.Toggle("Overwrite existing labels", ref overwriteExistingLabels);
            SpheneUI.Controls.HelpText("When enabled, importing notes replaces existing labels for matching users. When disabled, only empty labels are filled.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawOpenNotesPopupOnUserAdditionOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "OpenNotesPopupOnUserAddition")
    {
        ImGui.PushID(blockId);
        try
        {
            var openPopupOnAddition = configService.Current.OpenPopupOnAdd;
            if (SpheneUI.Controls.Toggle("Open Notes Popup on user addition", ref openPopupOnAddition))
            {
                configService.Current.OpenPopupOnAdd = openPopupOnAddition;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("When enabled, the notes popup opens automatically after adding a new user.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAutoPopulateNotesUsingPlayerNamesOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "AutoPopulateNotesUsingPlayerNames")
    {
        ImGui.PushID(blockId);
        try
        {
            var autoPopulateNotes = configService.Current.AutoPopulateEmptyNotesFromCharaName;
            if (SpheneUI.Controls.Toggle("Automatically populate notes using player names", ref autoPopulateNotes))
            {
                configService.Current.AutoPopulateEmptyNotesFromCharaName = autoPopulateNotes;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("When enabled, empty notes are automatically filled with the detected player name.");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
