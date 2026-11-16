using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sphene.Services.E2ee;

/// <summary>
/// Persists the client's E2EE device keys under
/// {configDir}/chat-e2ee/. Private scalars are stored DPAPI-protected
/// (CurrentUser scope) when the platform supports it; on platforms without
/// DPAPI the file falls back to raw storage and the flag is recorded.
/// Old device keys are kept on rotation so history addressed to them stays
/// decryptable.
/// </summary>
public sealed class ChatKeyStore
{
    private sealed class StoredKey
    {
        [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
        [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = string.Empty;
        [JsonPropertyName("privateKey")] public string PrivateKey { get; set; } = string.Empty;
        [JsonPropertyName("protected")] public bool Protected { get; set; }
        [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    }

    private sealed class CurrentMarker
    {
        [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    }

    private readonly ILogger<ChatKeyStore> _logger;
    private readonly string _root;
    private readonly System.Threading.Lock _ioLock = new();
    private List<ChatDeviceKeyMaterial>? _cache;

    public ChatKeyStore(ILogger<ChatKeyStore> logger, string configDirectory)
    {
        _logger = logger;
        _root = Path.Combine(configDirectory, "chat-e2ee");
    }

    /// <summary>The active device identity - created on first use.</summary>
    public ChatDeviceKeyMaterial GetOrCreateCurrent()
    {
        lock (_ioLock)
        {
            var currentId = ReadCurrentMarker();
            foreach (var material in LoadAll())
            {
                if (string.Equals(material.DeviceId, currentId, StringComparison.Ordinal))
                {
                    return material;
                }
            }
            // No marker or marker pointing at a deleted key - prefer the most
            // recent existing key, else create a fresh identity.
            var existing = LoadAll().OrderByDescending(k => k.CreatedUtc).FirstOrDefault();
            if (existing != null)
            {
                WriteCurrentMarker(existing.DeviceId);
                return existing;
            }
            var fresh = CreateIdentity();
            WriteCurrentMarker(fresh.DeviceId);
            return fresh;
        }
    }

    /// <summary>Every device key ever created on this install - old keys stay
    /// so messages addressed to rotated devices remain decryptable.</summary>
    public IReadOnlyList<ChatDeviceKeyMaterial> GetAll()
    {
        lock (_ioLock)
        {
            return LoadAll();
        }
    }

    /// <summary>Creates a new identity and makes it current. The previous key
    /// is kept for decryption; the caller revokes it server-side.</summary>
    public ChatDeviceKeyMaterial Rotate()
    {
        lock (_ioLock)
        {
            var fresh = CreateIdentity();
            WriteCurrentMarker(fresh.DeviceId);
            return fresh;
        }
    }

    /// <summary>True when private keys are stored without DPAPI protection.</summary>
    public bool AnyUnprotectedKeys => GetAll().Any(k => k.Unprotected);

    private string DevicesDir => Path.Combine(_root, "devices");
    private string CurrentPath => Path.Combine(_root, "current.json");
    private string KeyPath(string deviceId) => Path.Combine(DevicesDir, deviceId + ".key");

    private ChatDeviceKeyMaterial CreateIdentity()
    {
        var privateKey = SpheneHpke.GeneratePrivateKey(out var publicKey);
        var material = new ChatDeviceKeyMaterial(Guid.NewGuid().ToString("N"),
            publicKey, privateKey, DateTime.UtcNow);
        Persist(material);
        return material;
    }

    private void Persist(ChatDeviceKeyMaterial material)
    {
        Directory.CreateDirectory(DevicesDir);
        var (stored, wasProtected) = ProtectPrivateKey(material.PrivateKey);
        var record = new StoredKey
        {
            DeviceId = material.DeviceId,
            PublicKey = Convert.ToBase64String(material.PublicKey),
            PrivateKey = Convert.ToBase64String(stored),
            Protected = wasProtected,
            CreatedUtc = material.CreatedUtc,
        };
        var tmp = KeyPath(material.DeviceId) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record));
        File.Move(tmp, KeyPath(material.DeviceId), overwrite: true);
        _cache = null;
        if (!wasProtected)
        {
            _logger.LogWarning("Chat E2EE private key for device {Device} stored without DPAPI protection",
                material.DeviceId);
        }
    }

    private List<ChatDeviceKeyMaterial> LoadAll()
    {
        if (_cache != null)
        {
            return _cache;
        }
        var result = new List<ChatDeviceKeyMaterial>();
        if (Directory.Exists(DevicesDir))
        {
            foreach (var file in Directory.EnumerateFiles(DevicesDir, "*.key"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<StoredKey>(File.ReadAllText(file));
                    if (record == null || record.DeviceId.Length == 0) continue;
                    var rawPrivate = Convert.FromBase64String(record.PrivateKey);
                    var privateKey = record.Protected
                        ? UnprotectPrivateKey(rawPrivate)
                        : rawPrivate;
                    if (privateKey.Length != SpheneHpke.PrivateKeyLength) continue;
                    result.Add(new(record.DeviceId,
                        Convert.FromBase64String(record.PublicKey),
                        privateKey, record.CreatedUtc, Unprotected: !record.Protected));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping unreadable chat device key {File}", file);
                }
            }
        }
        _cache = result;
        return result;
    }

    private string? ReadCurrentMarker()
    {
        try
        {
            if (!File.Exists(CurrentPath)) return null;
            return JsonSerializer.Deserialize<CurrentMarker>(File.ReadAllText(CurrentPath))?.DeviceId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read chat device marker");
            return null;
        }
    }

    private void WriteCurrentMarker(string deviceId)
    {
        Directory.CreateDirectory(_root);
        var tmp = CurrentPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new CurrentMarker { DeviceId = deviceId }));
        File.Move(tmp, CurrentPath, overwrite: true);
    }

    /// <summary>DPAPI-protects the private scalar when supported. Windows is
    /// the only supported runtime for the plugin; anything else stores raw.</summary>
    private static (byte[] Stored, bool Protected) ProtectPrivateKey(byte[] privateKey)
    {
        try
        {
            return (ProtectedData.Protect(privateKey, optionalEntropy: null,
                DataProtectionScope.CurrentUser), true);
        }
        catch (Exception)
        {
            return (privateKey.ToArray(), false);
        }
    }

    private static byte[] UnprotectPrivateKey(byte[] stored)
        => ProtectedData.Unprotect(stored, optionalEntropy: null,
            DataProtectionScope.CurrentUser);
}
