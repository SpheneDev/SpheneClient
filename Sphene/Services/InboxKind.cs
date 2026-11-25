namespace Sphene.Services;

/// <summary>Kind of pending item surfaced by the notification inbox.</summary>
public enum InboxKind
{
    ModTransfer = 0,
    ModTransferDone = 1,
    Update = 2,
    Announcement = 3,
    AreaBoundConsent = 4,
    /// <summary>Durable server-side notification · read state lives on the server.</summary>
    Server = 5,
    /// <summary>Unread direct-message conversation · payload carries the
    /// <see cref="Sphene.PlayerData.Pairs.Pair"/> for the deep-link.</summary>
    Chat = 6,
}
