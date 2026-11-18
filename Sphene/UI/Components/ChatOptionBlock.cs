using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>Settings rows for private chat (external GIF media, animation).</summary>
public static class ChatOptionBlock
{
    public static void DrawExternalMediaOption(SpheneConfigService configService, string blockId = "ChatExternalMedia")
    {
        var enabled = configService.Current.ChatExternalMediaEnabled;
        if (SpheneUI.Controls.Toggle("Load external GIFs and stickers (KLIPY)##" + blockId, ref enabled))
        {
            configService.Current.ChatExternalMediaEnabled = enabled;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("GIF and sticker search sends your query to KLIPY and media loading contacts KLIPY's servers. Message contents stay end-to-end encrypted - only the search text and media ids leave this device.");
    }

    public static void DrawGifAnimationOption(SpheneConfigService configService, string blockId = "ChatGifAnimation")
    {
        var enabled = configService.Current.ChatGifAnimation;
        if (SpheneUI.Controls.Toggle("Animate GIFs in chat##" + blockId, ref enabled))
        {
            configService.Current.ChatGifAnimation = enabled;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("Reduced-motion mode: when off, GIFs render their first frame as a static image.");
    }

    /// <summary>Favorite-GIF count plus a clear action. The favorites list is
    /// a metadata reference (provider id + rendition URLs), never media.</summary>
    public static void DrawGifFavoritesOption(SpheneConfigService configService, string blockId = "ChatGifFavorites")
    {
        var count = configService.Current.GifFavorites.Count;
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Muted(count == 0
            ? "No favorites pinned"
            : count + " favorite" + (count == 1 ? "" : "s") + " pinned");
        ImGui.SameLine();
        using (ImRaii.Disabled(count == 0))
        {
            if (SpheneUI.Controls.SmallButton("Clear##" + blockId))
            {
                configService.Current.GifFavorites.Clear();
                configService.Save();
            }
        }
        SpheneUI.Controls.HelpText("Star a GIF or sticker in the picker's grid or preview to pin it under the Favorites tab. Favorites store the provider reference only - never the media itself.");
    }
}
