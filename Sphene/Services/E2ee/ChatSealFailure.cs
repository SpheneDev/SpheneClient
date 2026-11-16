namespace Sphene.Services.E2ee;

public enum ChatSealFailure
{
    None,
    /// <summary>Recipient has published no active device keys - they are on a
    /// client that cannot receive encrypted chat. Never fall back to plaintext.</summary>
    NoRecipientDevices,
    /// <summary>Own device key could not be published/loaded.</summary>
    NoSelfDevices,
    /// <summary>No server connection - the message was not sent at all.</summary>
    NotConnected
}
