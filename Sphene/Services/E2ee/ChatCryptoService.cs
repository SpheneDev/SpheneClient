using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sphene.Services.E2ee;

/// <summary>
/// Client-side E2EE coordinator for private chat. Owns the device identity
/// (via <see cref="ChatKeyStore"/>), publishes the public key, seals outgoing
/// messages to every active device of both participants (HPKE RFC 9180 Auth
/// mode), opens incoming copies and tracks peer key material with a
/// trust-on-first-use cache so unexpected key changes surface in the UI.
/// </summary>
public sealed class ChatCryptoService : DisposableMediatorSubscriberBase, IHostedService
{
    private sealed class TofuFile
    {
        [JsonPropertyName("peers")]
        public Dictionary<string, Dictionary<string, string>> Peers { get; set; }
            = new(StringComparer.Ordinal);
    }

    private static readonly TimeSpan _keyCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _keyCacheNegativeTtl = TimeSpan.FromSeconds(30);

    private readonly ApiController _apiController;
    private readonly ChatKeyStore _keyStore;
    private readonly string _tofuPath;

    /// <summary>uid → (deviceId → key) session cache.</summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, ChatDeviceKeyDto>> _keyCache
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _keyFetchedAt
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _keyChanged
        = new(StringComparer.Ordinal);

    private readonly System.Threading.Lock _identityLock = new();
    private ChatDeviceKeyMaterial? _identity;
    private ECDiffieHellman? _identityHandle;
    private volatile bool _publishedThisSession;

    private readonly System.Threading.Lock _tofuLock = new();
    private TofuFile? _tofu;

    public ChatCryptoService(ILogger<ChatCryptoService> logger, SpheneMediator mediator,
        ApiController apiController, ChatKeyStore keyStore, string configDirectory)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _keyStore = keyStore;
        _tofuPath = Path.Combine(configDirectory, "chat-e2ee", "tofu.json");
        Mediator.Subscribe<ConnectedMessage>(this, (m) =>
        {
            // Publish/renew the device key on every connect - cheap upsert.
            _publishedThisSession = false;
            InvalidateKeyCache();
            _ = EnsureIdentityPublishedAsync();
        });
        // A peer may have rotated keys while we were offline - cached key
        // material across a reconnect can seal to dead keys, producing
        // messages the recipient can never open.
        Mediator.Subscribe<HubReconnectedMessage>(this, _ => InvalidateKeyCache());
        Mediator.Subscribe<HubClosedMessage>(this, _ => _publishedThisSession = false);
    }

    /// <summary>The active device identity - created lazily on first use.</summary>
    public ChatDeviceKeyMaterial Identity
    {
        get
        {
            lock (_identityLock)
            {
                _identity ??= _keyStore.GetOrCreateCurrent();
                return _identity;
            }
        }
    }

    public string OwnDeviceId => Identity.DeviceId;
    public byte[] OwnPublicKey => Identity.PublicKey;
    public bool OwnKeysUnprotected => _keyStore.AnyUnprotectedKeys;

    /// <summary>PEM-style shareable fingerprint of the own device key.</summary>
    public string OwnFingerprint => FingerprintText(Identity.PublicKey);

    /// <summary>Publishes the current device key. Safe to call repeatedly.</summary>
    public async Task EnsureIdentityPublishedAsync()
    {
        try
        {
            var identity = Identity;
            await _apiController.UserPublishChatDeviceKey(identity.DeviceId,
                identity.PublicKey).ConfigureAwait(false);
            _publishedThisSession = true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Chat device key publish failed");
        }
    }

    /// <summary>Active device keys for the given UIDs (own included), from the
    /// session cache or the server. Peer keys run the TOFU comparison.</summary>
    public async Task<Dictionary<string, Dictionary<string, ChatDeviceKeyDto>>> GetDeviceKeysAsync(
        IEnumerable<string> uids, bool forceRefresh = false)
    {
        var wanted = uids.Distinct(StringComparer.Ordinal).ToList();
        var missing = wanted.Where(u => forceRefresh
            || !_keyFetchedAt.TryGetValue(u, out var at)
            || DateTime.UtcNow - at > (_keyCache.TryGetValue(u, out var existing) && existing.Count == 0
                ? _keyCacheNegativeTtl : _keyCacheTtl)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                var fetched = await _apiController.UserGetChatDeviceKeys(missing)
                    .ConfigureAwait(false);
                var grouped = fetched.GroupBy(k => k.Uid, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key,
                        g => g.ToDictionary(k => k.DeviceId, StringComparer.Ordinal),
                        StringComparer.Ordinal);
                foreach (var uid in missing)
                {
                    var devices = grouped.GetValueOrDefault(uid)
                        ?? new Dictionary<string, ChatDeviceKeyDto>(StringComparer.Ordinal);
                    _keyCache[uid] = devices;
                    _keyFetchedAt[uid] = DateTime.UtcNow;
                    CheckTofu(uid, devices);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat device key fetch failed for {Uids}",
                    string.Join(",", missing));
            }
        }
        var result = new Dictionary<string, Dictionary<string, ChatDeviceKeyDto>>(StringComparer.Ordinal);
        foreach (var uid in wanted)
        {
            result[uid] = _keyCache.TryGetValue(uid, out var devices)
                ? devices
                : new Dictionary<string, ChatDeviceKeyDto>(StringComparer.Ordinal);
        }
        return result;
    }

    /// <summary>Seals <paramref name="text"/> into one copy per active device
    /// of the sealer AND of the other conversation participant - both sides
    /// must be able to read history. <paramref name="authorUid"/>/
    /// <paramref name="recipientUid"/> are the row's original fields (bound
    /// into the envelope); <paramref name="sealerUid"/> is the account
    /// producing the seal and <paramref name="otherPartyUid"/> is the other
    /// participant. For native sends sealer==author and otherParty==recipient;
    /// for migration they differ from the row fields. Returns a failure enum
    /// instead of throwing.</summary>
    public async Task<ChatSealResult> SealAsync(string authorUid, string recipientUid,
        string sealerUid, string otherPartyUid, string text, Guid messageId,
        ChatMessageKind kind, DateTime sentAtUtc)
    {
        if (!_publishedThisSession)
        {
            await EnsureIdentityPublishedAsync().ConfigureAwait(false);
        }
        // Storage keeps microsecond precision - seal the normalized value so
        // persisted and sealed timestamps stay bit-identical (the AAD also
        // quantizes, this keeps the stored data clean).
        sentAtUtc = ChatEnvelope.NormalizeTimestamp(sentAtUtc);
        var devices = await GetDeviceKeysAsync([sealerUid, otherPartyUid])
            .ConfigureAwait(false);
        var mine = devices[sealerUid];
        var theirs = devices[otherPartyUid];
        if (mine.Count == 0) return new([], ChatSealFailure.NoSelfDevices);
        if (theirs.Count == 0) return new([], ChatSealFailure.NoRecipientDevices);
        var copies = new List<ChatCipherCopyDto>(mine.Count + theirs.Count);
        try
        {
            lock (_identityLock)
            {
                _identityHandle ??= SpheneHpke.CreateKeyPair(Identity.PrivateKey,
                    Identity.PublicKey);
                foreach (var (targetUid, deviceMap) in new[]
                    { (otherPartyUid, theirs), (sealerUid, mine) })
                {
                    foreach (var (deviceId, key) in deviceMap)
                    {
                        var context = new ChatEnvelope.EnvelopeContext(messageId, kind,
                            authorUid, recipientUid, deviceId, sealerUid,
                            Identity.DeviceId, sentAtUtc);
                        copies.Add(new ChatCipherCopyDto
                        {
                            ForUid = targetUid,
                            ForDeviceId = deviceId,
                            SealerUid = sealerUid,
                            SealerDeviceId = Identity.DeviceId,
                            SealerPublicKey = Identity.PublicKey,
                            Blob = ChatEnvelope.Seal(_identityHandle, key.PublicKey,
                                context, text),
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to seal chat message");
            return new([], ChatSealFailure.NoRecipientDevices);
        }
        return new(copies, ChatSealFailure.None);
    }

    /// <summary>Tries to open the caller-addressed copies of a sealed row.
    /// True on success - false means no local device key could authenticate
    /// the copy (undecryptable display state; never render the blob).</summary>
    public bool TryOpen(ChatMessageDto dto, out string text)
    {
        text = string.Empty;
        var knownKeys = _keyStore.GetAll();
        foreach (var copy in dto.Copies)
        {
            var material = knownKeys.FirstOrDefault(k =>
                string.Equals(k.DeviceId, copy.ForDeviceId, StringComparison.Ordinal));
            if (material == null)
            {
                continue;
            }
            try
            {
                using var handle = SpheneHpke.CreateKeyPair(material.PrivateKey,
                    material.PublicKey);
                var context = new ChatEnvelope.EnvelopeContext(dto.Id, dto.Kind,
                    dto.Sender.UID, dto.Recipient.UID, copy.ForDeviceId,
                    copy.SealerUid, copy.SealerDeviceId, dto.SentAt);
                text = OpenWithLegacyFallback(copy.Blob, handle,
                    copy.SealerPublicKey, context);
                return true;
            }
            catch (CryptographicException ex)
            {
                // A copy addressed to a local device failed authentication -
                // suspicious, but other copies may target other local devices
                // and still open; keep trying before declaring undecryptable.
                Logger.LogWarning(ex, "Chat copy for device {Device} failed authentication",
                    copy.ForDeviceId);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Chat copy for device {Device} could not be opened",
                    copy.ForDeviceId);
            }
        }
        return false;
    }

    /// <summary>Opens with the canonical microsecond AAD, then the bounded
    /// raw-tick candidate window so rows sealed before the timestamp-
    /// precision fix remain readable instead of becoming placeholders.</summary>
    private static string OpenWithLegacyFallback(byte[] blob, ECDiffieHellman handle,
        byte[] sealerPublicKey, ChatEnvelope.EnvelopeContext context)
    {
        if (!ChatEnvelope.TryOpenRecovered(blob, handle, sealerPublicKey,
                context, out var text))
        {
            throw new CryptographicException("Chat copy did not authenticate.");
        }
        return text;
    }

    /// <summary>True when the peer's published key material changed since we
    /// first saw it (key rotation/reinstall) - the UI surfaces a warning
    /// until the user re-verifies or dismisses.</summary>
    public bool PeerKeyChanged(string uid) => _keyChanged.ContainsKey(uid);

    /// <summary>Dismisses the key-change warning after user acknowledgement.</summary>
    public void DismissPeerKeyChange(string uid) => _keyChanged.TryRemove(uid, out _);

    /// <summary>Pairwise safety number for manual verification - SHA-256 over
    /// both users' sorted device fingerprints. Displayed in grouped hex.</summary>
    public async Task<string?> ComputeSafetyNumberAsync(string ownUid, string otherUid)
    {
        var devices = await GetDeviceKeysAsync([ownUid, otherUid], forceRefresh: true)
            .ConfigureAwait(false);
        var mine = devices[ownUid];
        var theirs = devices[otherUid];
        if (mine.Count == 0 || theirs.Count == 0) return null;
        var (lower, higher) = string.CompareOrdinal(ownUid, otherUid) < 0
            ? (ownUid, otherUid) : (otherUid, ownUid);
        var lowKeys = devices[lower].Values.Select(k => k.PublicKey);
        var highKeys = devices[higher].Values.Select(k => k.PublicKey);
        using var sha = SHA256.Create();
        var input = new List<byte>();
        void AddPart(string s)
        {
            input.AddRange(System.Text.Encoding.UTF8.GetBytes(s));
            input.Add(0);
        }
        void AddKeys(IEnumerable<byte[]> keys)
        {
            foreach (var fp in keys.Select(SpheneHpke.Fingerprint)
                     .OrderBy(f => Convert.ToHexString(f), StringComparer.Ordinal))
            {
                input.AddRange(fp);
            }
        }
        AddPart(lower); AddPart(higher);
        AddKeys(lowKeys); AddKeys(highKeys);
        var hash = sha.ComputeHash(input.ToArray());
        var hex = Convert.ToHexString(hash);
        return string.Join(' ', hex.Chunk(8).Select(c => new string(c)).Take(4));
    }

    /// <summary>Fingerprint of the peer's most recently published cached
    /// device key; null while the peer's keys are unknown.</summary>
    public string? PeerFingerprint(string uid)
    {
        if (!_keyCache.TryGetValue(uid, out var devices) || devices.Count == 0)
        {
            return null;
        }
        var newest = devices.Values.OrderByDescending(k => k.PublishedAtUtc).First();
        return FingerprintText(newest.PublicKey);
    }

    /// <summary>Generates a fresh device identity, publishes it and revokes
    /// the previous device key. Old key files stay so previously received
    /// ciphertexts remain decryptable.</summary>
    public async Task RotateIdentityAsync()
    {
        string? previousDeviceId;
        ChatDeviceKeyMaterial fresh;
        lock (_identityLock)
        {
            previousDeviceId = _identity?.DeviceId;
            fresh = _keyStore.Rotate();
            _identity = fresh;
            _identityHandle?.Dispose();
            _identityHandle = null;
        }
        await EnsureIdentityPublishedAsync().ConfigureAwait(false);
        if (previousDeviceId != null)
        {
            try
            {
                await _apiController.UserRevokeChatDeviceKey(previousDeviceId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Old chat device key revocation failed");
            }
        }
        _keyCache.Clear();
    }

    /// <summary>TOFU: flags unexpected key material changes for a peer.
    /// New devices appearing or old devices vanishing alone are normal;
    /// changed key bytes under a known device id, or a full identity reset
    /// after keys existed, are surfaced.</summary>
    private void CheckTofu(string uid, Dictionary<string, ChatDeviceKeyDto> devices)
    {
        var own = _apiController.UID ?? string.Empty;
        if (string.Equals(uid, own, StringComparison.Ordinal)) return;
        lock (_tofuLock)
        {
            var tofu = LoadTofu();
            if (!tofu.Peers.TryGetValue(uid, out var seen))
            {
                tofu.Peers[uid] = seen = new(StringComparer.Ordinal);
            }
            var previousIds = seen.Keys.ToList();
            var changed = false;
            foreach (var (deviceId, key) in devices)
            {
                var fp = Convert.ToBase64String(SpheneHpke.Fingerprint(key.PublicKey));
                if (seen.TryGetValue(deviceId, out var knownFp)
                    && !string.Equals(knownFp, fp, StringComparison.Ordinal))
                {
                    changed = true;
                }
                seen[deviceId] = fp;
            }
            // Full identity reset: every previously seen device id is gone but
            // the peer is publishing new devices - surfaces like a key change.
            var allGone = previousIds.Count > 0 && devices.Count > 0
                && previousIds.All(id => !devices.ContainsKey(id));
            if (allGone) changed = true;
            if (seen.Count == 0)
            {
                tofu.Peers.Remove(uid);
            }
            SaveTofu(tofu);
            if (changed && _keyChanged.TryAdd(uid, 0))
            {
                Logger.LogWarning("Chat encryption keys changed for {Uid}", uid);
                Mediator.Publish(new ChatPeerKeyChangedMessage(uid));
            }
            else if (devices.Count == 0)
            {
                // All devices revoked - drop the baseline; next publish is a
                // fresh identity, not a silent change.
                _keyChanged.TryRemove(uid, out _);
            }
        }
    }

    private void InvalidateKeyCache()
    {
        _keyCache.Clear();
        _keyFetchedAt.Clear();
    }

    private TofuFile LoadTofu()
    {
        if (_tofu != null) return _tofu;
        try
        {
            if (File.Exists(_tofuPath))
            {
                _tofu = JsonSerializer.Deserialize<TofuFile>(File.ReadAllText(_tofuPath))
                    ?? new TofuFile();
                return _tofu;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Chat TOFU store unreadable - starting fresh");
        }
        _tofu = new TofuFile();
        return _tofu;
    }

    private void SaveTofu(TofuFile tofu)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_tofuPath)!);
            var tmp = _tofuPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(tofu));
            File.Move(tmp, _tofuPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Chat TOFU store write failed");
        }
    }

    private static string FingerprintText(byte[] publicKey)
    {
        var hex = Convert.ToHexString(SpheneHpke.Fingerprint(publicKey));
        return string.Join(' ', hex.Chunk(8).Select(c => new string(c)).Take(4));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        lock (_identityLock)
        {
            _identityHandle?.Dispose();
            _identityHandle = null;
        }
    }
}
