using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sphene.Services.E2ee;

/// <summary>
/// Dependency-free core of the chat key vault: HKDF key derivation,
/// AES-256-GCM wrap/unwrap (account + format bound via AAD), device
/// merge/validation and the pure sync decision. Contains no service or IO
/// dependencies so the entire decision matrix is deterministically testable.
/// </summary>
public static class ChatKeyVaultCore
{
    /// <summary>One backed-up device identity as serialized inside the
    /// encrypted payload - never transmitted to the server in clear.</summary>
    public sealed class VaultDevice
    {
        [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
        [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = string.Empty;
        [JsonPropertyName("privateKey")] public string PrivateKey { get; set; } = string.Empty;
        [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Encrypted payload contract. Version 1 carries devices and the
    /// TOFU baseline; unknown versions are never overwritten.</summary>
    public sealed class VaultPayload
    {
        [JsonPropertyName("version")] public int Version { get; set; } = SupportedVersion;
        [JsonPropertyName("devices")] public List<VaultDevice> Devices { get; set; } = [];
        [JsonPropertyName("tofu")] public string? Tofu { get; set; }
    }

    /// <summary>Mirror of the TOFU file shape (peers: uid → deviceId →
    /// fingerprint) for merge/set comparisons.</summary>
    public sealed class VaultTofu
    {
        [JsonPropertyName("peers")]
        public Dictionary<string, Dictionary<string, string>> Peers { get; set; }
            = new(StringComparer.Ordinal);
    }

    /// <summary>What the last successful server read told us.</summary>
    public sealed record VaultRemote
    {
        public required long Revision { get; init; }
        /// <summary>Set only when the blob unwrapped and parsed; null means
        /// the ciphertext could not be opened with the current key.</summary>
        public VaultPayload? Payload { get; init; }
        /// <summary>Blob used the pre-versioned wrap format - rewriting under
        /// the current format is safe once merged.</summary>
        public bool LegacyFormat { get; init; }
        /// <summary>Blob decrypted but carries a payload version this build
        /// does not understand - a NEWER client owns it; never overwrite.</summary>
        public bool Unsupported { get; init; }
    }

    public enum VaultAction { Create, Update, Skip, Rewrite, Blocked }

    /// <summary>The decided next step; Payload is the exact content to wrap
    /// and upload for Create/Update/Rewrite.</summary>
    public sealed record VaultPlan(VaultAction Action, long ExpectedRevision,
        VaultPayload? Payload, string? Reason = null);

    public enum VaultUnwrap { Ok, Failed, UnsupportedVersion }

    public const int SupportedVersion = 1;
    private const int VaultKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const byte FormatVersion = 1;
    private static readonly byte[] _vaultInfo = "sphene-chat-vault-v1"u8.ToArray();
    private static readonly byte[] _probeInfo = "sphene-chat-vault-probe"u8.ToArray();
    private static readonly byte[] _probePlaintext = [1];
    private const string AadSuffix = "|v1";

    /// <summary>Vault wrapping key: HKDF-SHA256 over the raw secret key -
    /// the server only ever sees the secret's auth hash, so it cannot derive
    /// this key and cannot unwrap the blob.</summary>
    public static byte[] DeriveVaultKey(string secret, string uid)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(secret), VaultKeyLength,
            Encoding.UTF8.GetBytes(uid), _vaultInfo);

    /// <summary>Authenticated metadata for the current blob format: account
    /// identity + format version, so a foreign account's blob or a stale
    /// format fails AEAD instead of being misread.</summary>
    public static byte[] BuildAad(string uid)
        => Encoding.UTF8.GetBytes(uid + AadSuffix);

    /// <summary>AAD of the pre-versioned dev format - kept solely so blobs
    /// written before versioning still unwrap and get upgraded on the next
    /// successful write.</summary>
    public static byte[] BuildLegacyAad(string uid)
        => Encoding.UTF8.GetBytes(uid);

    /// <summary>The sync decision. Preserves data above all: an unreadable
    /// remote is only rewritten when the local marker proves this client
    /// authored that exact revision and the local keyring still covers every
    /// device the blob contained.</summary>
    public static VaultPlan Plan(VaultRemote? remote,
        IReadOnlyList<ChatDeviceKeyMaterial> localDevices,
        string? localTofuJson, long markerRevision,
        IReadOnlyList<string> markerDeviceIds)
    {
        if (remote == null)
        {
            return new VaultPlan(VaultAction.Create, 0,
                new VaultPayload
                {
                    Devices = MergeDevices(localDevices, []),
                    Tofu = localTofuJson,
                });
        }
        if (remote.Payload != null)
        {
            var mergedDevices = MergeDevices(localDevices,
                remote.Payload.Devices);
            var mergedTofu = MergeTofuJson(localTofuJson, remote.Payload.Tofu);
            if (!remote.LegacyFormat
                && SameDeviceIds(mergedDevices, remote.Payload.Devices)
                && SameTofu(mergedTofu, remote.Payload.Tofu))
            {
                return new VaultPlan(VaultAction.Skip, remote.Revision, null);
            }
            return new VaultPlan(VaultAction.Update, remote.Revision,
                new VaultPayload { Devices = mergedDevices, Tofu = mergedTofu });
        }
        if (remote.Unsupported)
        {
            return new VaultPlan(VaultAction.Blocked, remote.Revision, null,
                "unsupported-payload-version");
        }
        if (markerRevision == remote.Revision && markerDeviceIds.Count > 0
            && markerDeviceIds.All(id => localDevices.Any(d =>
                string.Equals(d.DeviceId, id, StringComparison.Ordinal))))
        {
            // The unreadable blob is the exact revision this install wrote
            // and the local keyring still covers everything in it - the
            // rewrite can only add, never lose, devices.
            return new VaultPlan(VaultAction.Rewrite, remote.Revision,
                new VaultPayload
                {
                    Devices = MergeDevices(localDevices, []),
                    Tofu = localTofuJson,
                });
        }
        return new VaultPlan(VaultAction.Blocked, remote.Revision, null,
            "unreadable-not-proven-local");
    }

    /// <summary>Union of local and stored devices, deduplicated by device id
    /// and validated (public point re-derived from the private scalar). Local
    /// entries always win id collisions - the live install's material is
    /// never replaced by stored bytes.</summary>
    public static List<VaultDevice> MergeDevices(
        IReadOnlyList<ChatDeviceKeyMaterial> local, List<VaultDevice> stored)
    {
        var result = new List<VaultDevice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in local)
        {
            if (m.DeviceId.Length == 0 || m.PublicKey.Length != SpheneHpke.PublicKeyLength
                || m.PrivateKey.Length != SpheneHpke.PrivateKeyLength
                || !seen.Add(m.DeviceId))
            {
                continue;
            }
            result.Add(new VaultDevice
            {
                DeviceId = m.DeviceId,
                PublicKey = Convert.ToBase64String(m.PublicKey),
                PrivateKey = Convert.ToBase64String(m.PrivateKey),
                CreatedUtc = m.CreatedUtc,
            });
        }
        foreach (var d in stored)
        {
            if (!seen.Contains(d.DeviceId) && TryMaterialize(d, out _))
            {
                seen.Add(d.DeviceId);
                result.Add(d);
            }
        }
        return result;
    }

    /// <summary>Set-union of two TOFU baselines as canonical JSON. Unknown or
    /// corrupt tofu contributes nothing - trust state is advisory, never an
    /// overwrite of locally learned entries.</summary>
    public static string? MergeTofuJson(string? localJson, string? storedJson)
    {
        var merged = TofuSet(localJson);
        merged.UnionWith(TofuSet(storedJson));
        if (merged.Count == 0) return null;
        var tofu = new VaultTofu();
        foreach (var entry in merged)
        {
            var parts = entry.Split('\x1F');
            if (parts.Length != 3) continue;
            if (!tofu.Peers.TryGetValue(parts[0], out var devices))
            {
                tofu.Peers[parts[0]] = devices = new(StringComparer.Ordinal);
            }
            devices[parts[1]] = parts[2];
        }
        return JsonSerializer.Serialize(tofu);
    }

    /// <summary>Validates a payload device entry and rebuilds the key
    /// material. The stored pair is proven to correspond by sealing a probe
    /// to the public point and opening it with the private scalar - a
    /// tampered or corrupt pair fails AEAD and never enters the store.</summary>
    public static bool TryMaterialize(VaultDevice device,
        out ChatDeviceKeyMaterial material)
    {
        material = null!;
        if (string.IsNullOrEmpty(device.DeviceId)) return false;
        byte[] privateKey, publicKey;
        try
        {
            privateKey = Convert.FromBase64String(device.PrivateKey);
            publicKey = Convert.FromBase64String(device.PublicKey);
        }
        catch (FormatException)
        {
            return false;
        }
        if (privateKey.Length != SpheneHpke.PrivateKeyLength
            || publicKey.Length != SpheneHpke.PublicKeyLength
            || publicKey[0] != 0x04)
        {
            return false;
        }
        try
        {
            var probePrivate = SpheneHpke.GeneratePrivateKey(out var probePublic);
            using var probe = SpheneHpke.CreateKeyPair(probePrivate, probePublic);
            using var candidate = SpheneHpke.CreateKeyPair(privateKey, publicKey);
            var ctTag = SpheneHpke.SealAuth(probe, publicKey, _probeInfo,
                [], _probePlaintext, out var enc);
            _ = SpheneHpke.OpenAuth(candidate, probePublic, enc, ctTag,
                _probeInfo, []);
        }
        catch (Exception)
        {
            return false;
        }
        material = new ChatDeviceKeyMaterial(device.DeviceId, publicKey,
            privateKey, device.CreatedUtc);
        return true;
    }

    /// <summary>Wraps the payload: format byte || nonce || ciphertext || tag.</summary>
    public static byte[] Wrap(byte[] key, byte[] aad, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        var blob = new byte[1 + NonceLength + ciphertext.Length + TagLength];
        blob[0] = FormatVersion;
        nonce.CopyTo(blob, 1);
        ciphertext.CopyTo(blob, 1 + NonceLength);
        tag.CopyTo(blob, 1 + NonceLength + ciphertext.Length);
        return blob;
    }

    /// <summary>Tries the current format, then the pre-versioned dev format.
    /// A GCM failure means the key does not fit (wrong secret) or the
    /// ciphertext is damaged - never a signal to overwrite.</summary>
    public static VaultUnwrap TryUnwrap(byte[] key, byte[] aad, byte[] legacyAad,
        byte[] blob, out VaultPayload? payload, out bool legacyFormat)
    {
        payload = null;
        legacyFormat = false;
        if (blob.Length > 1 + NonceLength + TagLength && blob[0] == FormatVersion)
        {
            var plaintext = Decrypt(key, aad, blob.AsSpan(1));
            if (plaintext != null)
            {
                return Classify(plaintext, out payload);
            }
        }
        var legacy = Decrypt(key, legacyAad, blob);
        if (legacy == null)
        {
            return VaultUnwrap.Failed;
        }
        legacyFormat = true;
        return Classify(legacy, out payload);
    }

    public static byte[] SerializePayload(VaultPayload payload)
        => JsonSerializer.SerializeToUtf8Bytes(payload);

    /// <summary>True when both device lists cover the same device ids.</summary>
    public static bool SameDeviceIds(List<VaultDevice> a, List<VaultDevice> b)
    {
        var ids = new HashSet<string>(a.Select(d => d.DeviceId),
            StringComparer.Ordinal);
        return ids.Count == b.Count && ids.SetEquals(b.Select(d => d.DeviceId));
    }

    /// <summary>True when both tofu JSONs encode the same peer/device/
    /// fingerprint set.</summary>
    public static bool SameTofu(string? mergedJson, string? storedJson)
        => TofuSet(mergedJson).SetEquals(TofuSet(storedJson));

    private static VaultUnwrap Classify(byte[] plaintext,
        out VaultPayload? payload)
    {
        payload = null;
        VaultPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<VaultPayload>(plaintext);
        }
        catch (JsonException)
        {
            return VaultUnwrap.Failed;
        }
        if (parsed == null) return VaultUnwrap.Failed;
        if (parsed.Version != SupportedVersion)
        {
            return VaultUnwrap.UnsupportedVersion;
        }
        payload = parsed;
        return VaultUnwrap.Ok;
    }

    private static byte[]? Decrypt(byte[] key, byte[] aad, ReadOnlySpan<byte> blob)
    {
        if (blob.Length <= NonceLength + TagLength) return null;
        var nonce = blob.Slice(0, NonceLength);
        var ciphertext = blob.Slice(NonceLength,
            blob.Length - NonceLength - TagLength);
        var tag = blob.Slice(blob.Length - TagLength, TagLength);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Flattens a tofu JSON to a (uid|deviceId|fingerprint) set so
    /// merging and equality are order-independent.</summary>
    private static HashSet<string> TofuSet(string? json)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json)) return set;
        VaultTofu? tofu;
        try
        {
            tofu = JsonSerializer.Deserialize<VaultTofu>(json);
        }
        catch (JsonException)
        {
            return set;
        }
        if (tofu == null) return set;
        foreach (var (uid, devices) in tofu.Peers)
        {
            foreach (var (deviceId, fp) in devices)
            {
                set.Add(uid + '\x1F' + deviceId + '\x1F' + fp);
            }
        }
        return set;
    }
}
