using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>Settings rows for private chat (external GIF media, animation).</summary>
public static class ChatOptionBlock
{
    public static void DrawExternalMediaOption(SpheneConfigService configService, string blockId = "ChatExternalMedia")
    {
        var enabled = configService.Current.ChatExternalMediaEnabled;
        if (SpheneUI.Controls.Toggle("Load external GIFs (KLIPY)##" + blockId, ref enabled))
        {
            configService.Current.ChatExternalMediaEnabled = enabled;
            configService.Save();
        }
        SpheneUI.Controls.HelpText("GIF search sends your query to KLIPY and media loading contacts KLIPY's servers. Message contents stay end-to-end encrypted - only the search text and GIF ids leave this device.");
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
}
