namespace Sphene.Services.E2ee;

/// <summary>A device identity: installation-scoped DH keypair. The private
/// scalar never leaves local storage and is never sent to the server.</summary>
public sealed record ChatDeviceKeyMaterial(string DeviceId, byte[] PublicKey,
    byte[] PrivateKey, DateTime CreatedUtc, bool Unprotected = false);
