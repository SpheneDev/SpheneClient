using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public static class StorageOptionBlock
{
    public static void DrawDeleteDownloadedModsAfterSuccessfulInstallOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "DeleteDownloadedModsAfterSuccessfulInstall")
    {
        ImGui.PushID(blockId);
        try
        {
            var deleteAfterInstall = configService.Current.DeletePenumbraModAfterInstall;
            if (SpheneUI.Controls.Toggle("Delete downloaded mods after successful install", ref deleteAfterInstall))
            {
                configService.Current.DeletePenumbraModAfterInstall = deleteAfterInstall;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("If enabled, the downloaded .pmp file will be deleted automatically after it has been successfully imported into Penumbra.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawUseFileCompactorOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "UseFileCompactor",
        string? menuPopupId = null)
    {
        ImGui.PushID(blockId);
        try
        {
            var useFileCompactor = configService.Current.UseCompactor;
            if (SpheneUI.Controls.Toggle("Use file compactor", ref useFileCompactor, menuPopupId: menuPopupId))
            {
                configService.Current.UseCompactor = useFileCompactor;
                configService.Save();
            }

            SpheneUI.Controls.HelpText("The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU." + Environment.NewLine
                + "It is recommended to leave it enabled to save on space.");
            return useFileCompactor;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawReadClearLocalStorageDisclaimerOption(ref bool readClearCache, string blockId = "ReadClearLocalStorageDisclaimer")
    {
        ImGui.PushID(blockId);
        try
        {
            SpheneUI.Controls.Toggle("I understand and accept the risks", ref readClearCache);
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
