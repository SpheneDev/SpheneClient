using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.WebAPI;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sphene.Services.E2ee;

/// <summary>
/// Orchestrates the account-bound E2EE key vault: restores device keys and
/// the TOFU baseline from the server-stored encrypted blob, merges them
/// into the local keyring and writes the union back through the server's
/// compare-and-swap revision. All decisions go through
/// <see cref="ChatKeyVaultCore.Plan"/> - this class only owns IO, the
/// last-written-revision marker and the blocked-state reporting.
/// An unreadable remote is never overwritten: only when the local marker
/// proves this install wrote that exact revision and the keyring still
/// covers everything in it may it be rewritten under the current secret.
/// OAuth2 logins have no stable client-side secret and skip the vault.
/// </summary>
public sealed class ChatKeyVaultService : DisposableMediatorSubscriberBase,
    IHostedService
{
    /// <summary>Local proof of what this install last stored server-side -
    /// the only safe basis for rewriting a blob we can no longer open.</summary>
    private sealed class VaultStateFile
    {
        [JsonPropertyName("lastWrittenRevision")] public long LastWrittenRevision { get; set; }
        [JsonPropertyName("deviceIds")] public List<string> DeviceIds { get; set; } = [];
    }

    private const int MaxSyncAttempts = 3;

    private readonly ApiController _apiController;
    private readonly ChatKeyStore _keyStore;
    private readonly ChatCryptoService _crypto;
    private readonly ServerConfigurationManager _serverManager;
    private readonly string _statePath;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly System.Threading.Lock _stateIoLock = new();
    private long _notifiedBlockedRevision = -1;

    public ChatKeyVaultService(ILogger<ChatKeyVaultService> logger,
        SpheneMediator mediator, ApiController apiController,
        ChatKeyStore keyStore, ChatCryptoService crypto,
        ServerConfigurationManager serverManager, string configDirectory)
        : base(logger, mediator)
    {
        _apiController = apiController;
        _keyStore = keyStore;
        _crypto = crypto;
        _serverManager = serverManager;
        _statePath = Path.Combine(configDirectory, "chat-e2ee",
            "vault-state.json");
        Mediator.Subscribe<ConnectedMessage>(this, (m) =>
        {
            _ = SyncAsync();
        });
        // Auto-reconnects skip ConnectedMessage but are still worth a sync
        // pass - another device of the same account may have extended the
        // keyring in the meantime.
        Mediator.Subscribe<HubReconnectedMessage>(this, (m) =>
        {
            _ = SyncAsync();
        });
    }

    /// <summary>Restore-then-merge-then-backup pass for one connect. Bounded
    /// retries ride the CAS loop; every outcome preserves the server blob
    /// and the local keyring unless a write provably loses nothing.</summary>
    private async Task SyncAsync()
    {
        if (!await _syncLock.WaitAsync(0).ConfigureAwait(false)) return;
        byte[]? vaultKey = null;
        try
        {
            if (!_apiController.IsConnected
                || _serverManager.CurrentServer?.UseOAuth2 == true)
            {
                return;
            }
            var secret = _serverManager.GetSecretKey(out _);
            var uid = _apiController.UID;
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(uid))
            {
                return;
            }
            vaultKey = ChatKeyVaultCore.DeriveVaultKey(secret, uid);
            var aad = ChatKeyVaultCore.BuildAad(uid);
            var legacyAad = ChatKeyVaultCore.BuildLegacyAad(uid);
            for (var attempt = 1; attempt <= MaxSyncAttempts; attempt++)
            {
                var stored = await _apiController.UserGetChatKeyBackup()
                    .ConfigureAwait(false);
                var remote = ToRemote(stored, vaultKey, aad, legacyAad);
                var localDevices = _keyStore.GetAll();
                var localTofu = _crypto.ExportTofu();
                var marker = ReadMarker();
                var plan = ChatKeyVaultCore.Plan(remote, localDevices,
                    localTofu, marker?.LastWrittenRevision ?? -1,
                    marker?.DeviceIds ?? []);
                if (remote?.Payload != null)
                {
                    // Restore and merge before backup - devices and trust
                    // state flow into local storage regardless of whether
                    // this pass ends up writing.
                    ApplyRestore(remote.Payload);
                }
                switch (plan.Action)
                {
                    case ChatKeyVaultCore.VaultAction.Skip:
                        WriteMarker(remote!.Revision, remote.Payload!);
                        return;
                    case ChatKeyVaultCore.VaultAction.Blocked:
                        ReportBlocked(remote!.Revision, plan.Reason);
                        return;
                    default:
                        var blob = ChatKeyVaultCore.Wrap(vaultKey, aad,
                            ChatKeyVaultCore.SerializePayload(plan.Payload!));
                        var result = await _apiController.UserPutChatKeyBackup(
                            blob, plan.ExpectedRevision).ConfigureAwait(false);
                        if (result.Success)
                        {
                            WriteMarker(result.Revision, plan.Payload!);
                            return;
                        }
                        // CAS conflict - the next attempt re-reads, merges
                        // against the winning writer and retries.
                        continue;
                }
            }
            Logger.LogDebug("Chat key vault sync deferred - persistent write conflicts");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Chat key vault sync failed");
        }
        finally
        {
            if (vaultKey != null)
            {
                CryptographicOperations.ZeroMemory(vaultKey);
            }
            _syncLock.Release();
        }
    }

    /// <summary>Maps the stored row to a remote state: unwrapped payload,
    /// unsupported version or unreadable ciphertext.</summary>
    private static ChatKeyVaultCore.VaultRemote? ToRemote(
        Sphene.API.Dto.User.ChatKeyBackupDto? stored, byte[] vaultKey,
        byte[] aad, byte[] legacyAad)
    {
        if (stored == null) return null;
        var unwrap = ChatKeyVaultCore.TryUnwrap(vaultKey, aad, legacyAad,
            stored.Blob, out var payload, out var legacyFormat);
        return unwrap switch
        {
            ChatKeyVaultCore.VaultUnwrap.Ok => new ChatKeyVaultCore.VaultRemote
            {
                Revision = stored.Revision,
                Payload = payload,
                LegacyFormat = legacyFormat,
            },
            ChatKeyVaultCore.VaultUnwrap.UnsupportedVersion
                => new ChatKeyVaultCore.VaultRemote
                {
                    Revision = stored.Revision,
                    Unsupported = true,
                },
            _ => new ChatKeyVaultCore.VaultRemote { Revision = stored.Revision },
        };
    }

    /// <summary>Imports device keys and the TOFU baseline contained in a
    /// successfully unwrapped payload. Imported keys are decrypt-only and
    /// never become the active identity.</summary>
    private void ApplyRestore(ChatKeyVaultCore.VaultPayload payload)
    {
        var imported = 0;
        foreach (var device in payload.Devices)
        {
            if (ChatKeyVaultCore.TryMaterialize(device, out var material)
                && _keyStore.ImportDevice(material))
            {
                imported++;
            }
        }
        if (!string.IsNullOrEmpty(payload.Tofu))
        {
            _crypto.ImportTofu(payload.Tofu);
        }
        if (imported > 0)
        {
            Logger.LogInformation(
                "Restored {Count} chat device keys from backup", imported);
            Mediator.Publish(new ChatKeysRestoredMessage());
        }
    }

    /// <summary>The remote exists but cannot be opened and cannot be proven
    /// locally-authored - the server ciphertext and all local keys are
    /// preserved. Recoverable: syncing under the wrapping secret unlocks it
    /// again.</summary>
    private void ReportBlocked(long revision, string? reason)
    {
        Logger.LogWarning(
            "Chat key backup at revision {Revision} is not readable ({Reason}) - preserved unchanged",
            revision, reason ?? "unknown");
        Mediator.Publish(new ChatKeyVaultBlockedMessage(revision));
        if (_notifiedBlockedRevision != revision)
        {
            _notifiedBlockedRevision = revision;
            Mediator.Publish(new NotificationMessage("Chat key backup locked",
                "The stored chat key backup could not be opened with the current secret key. It was left untouched - reconnect with the secret key it was created with to restore your device keys.",
                SpheneConfiguration.Models.NotificationType.Warning));
        }
    }

    private VaultStateFile? ReadMarker()
    {
        lock (_stateIoLock)
        {
            try
            {
                if (!File.Exists(_statePath)) return null;
                return JsonSerializer.Deserialize<VaultStateFile>(
                    File.ReadAllText(_statePath));
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat vault state marker unreadable");
                return null;
            }
        }
    }

    /// <summary>Records the device set provably present on the server at
    /// <paramref name="revision"/> - the marker is the coverage proof that
    /// later permits rewriting an unopenable blob.</summary>
    private void WriteMarker(long revision,
        ChatKeyVaultCore.VaultPayload written)
    {
        var deviceIds = new List<string>(written.Devices.Count);
        foreach (var d in written.Devices)
        {
            deviceIds.Add(d.DeviceId);
        }
        lock (_stateIoLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                var state = new VaultStateFile
                {
                    LastWrittenRevision = revision,
                    DeviceIds = deviceIds,
                };
                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state));
                File.Move(tmp, _statePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Chat vault state marker write failed");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _syncLock.Dispose();
    }
}
