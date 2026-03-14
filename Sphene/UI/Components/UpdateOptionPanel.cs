using Sphene.SpheneConfiguration;

namespace Sphene.UI.Components;

public static class UpdateOptionPanel
{
    public const string CurrentTag = "v.1.1.11.1071";

    public enum Link
    {
        SyncIncomingWithoutRedraw,
        SyncOutgoingBatching
    }

    public static readonly Link[] DefaultLinks =
    [
        Link.SyncIncomingWithoutRedraw,
        Link.SyncOutgoingBatching
    ];

    public static string GetTitle(Link link)
        => link switch
        {
            Link.SyncIncomingWithoutRedraw => "Sync: Incoming Sync (Default: Disabled)",
            Link.SyncOutgoingBatching => "Sync: Outgoing Batching (Default: Disabled)",
            _ => "Option"
        };

    public static void DrawByLink(Link link, SpheneConfigService configService, UiSharedService uiShared, float outgoingSliderWidth)
    {
        switch (link)
        {
            case Link.SyncIncomingWithoutRedraw:
                SyncBehaviorOptionBlock.DrawIncomingSyncWithoutRedraw(configService, uiShared, "UpdateOptionSyncIncomingWithoutRedraw");
                break;
            case Link.SyncOutgoingBatching:
                SyncBehaviorOptionBlock.DrawOutgoingSyncBatching(configService, uiShared, outgoingSliderWidth, "UpdateOptionSyncOutgoingBatching");
                break;
        }
    }

    public static bool HasUnseenTag(SpheneConfigService configService)
        => !string.Equals(configService.Current.LastSeenNewOptionsTag, CurrentTag, StringComparison.Ordinal);

    public static void MarkCurrentTagAsSeen(SpheneConfigService configService)
    {
        configService.Current.LastSeenNewOptionsTag = CurrentTag;
        configService.Save();
    }
}
