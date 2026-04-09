using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.Services;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.WebAPI;
using System.Collections.Concurrent;

namespace Sphene.PlayerData.Pairs;

public class AcknowledgmentTimeoutManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<string, AcknowledgmentTimeoutEntry> _pendingTimeouts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InvalidHashTimeoutEntry> _invalidHashTimeouts = new(StringComparer.Ordinal);
    private readonly Timer _timeoutTimer;
    private readonly Lazy<ApiController> _apiController;
    private readonly Lazy<PairManager> _pairManager;
    private const int InvalidHashTimeoutMs = 15000; // 15 seconds for invalid hash reapply
    private const int CheckIntervalMs = 5000; // Check every 5 seconds
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SpheneConfigService _configService;
    private DateTime _lastPauseLogUtc = DateTime.MinValue;
    private DateTime _lastConnectedUtc = DateTime.MinValue;
    private bool _seenAckSuccessSinceConnect = false;
    private int _ackTimeoutsSinceConnect = 0;
    private int _autoReconnectsSinceConnect = 0;
    private DateTime _lastAutoReconnectUtc = DateTime.MinValue;

    public AcknowledgmentTimeoutManager(ILogger<AcknowledgmentTimeoutManager> logger, 
        SpheneMediator mediator, Lazy<ApiController> apiController, Lazy<PairManager> pairManager,
        DalamudUtilService dalamudUtilService, SpheneConfigService configService) 
        : base(logger, mediator)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _timeoutTimer = new Timer(CheckTimeouts, null, CheckIntervalMs, CheckIntervalMs);

        Mediator.Subscribe<ConnectedMessage>(this, _ =>
        {
            _lastConnectedUtc = DateTime.UtcNow;
            _seenAckSuccessSinceConnect = false;
            _ackTimeoutsSinceConnect = 0;
            _autoReconnectsSinceConnect = 0;
        });

        Mediator.Subscribe<PairAcknowledgmentStatusChangedMessage>(this, msg =>
        {
            if (msg.LastAcknowledgmentSuccess == true)
            {
                _seenAckSuccessSinceConnect = true;
            }
        });
    }

    public void StartTimeout(string acknowledgmentId, UserData userData, string dataHash)
    {
        var key = $"{acknowledgmentId}:{userData.UID}";
        var entry = new AcknowledgmentTimeoutEntry(key, acknowledgmentId, userData, dataHash, DateTimeOffset.Now);
        _pendingTimeouts[key] = entry;
        Logger.LogDebug("Started timeout tracking for acknowledgment {ackId} for user {user}", 
            acknowledgmentId, userData.AliasOrUID);
    }

    public void CancelTimeout(string acknowledgmentId, string userUid)
    {
        var key = $"{acknowledgmentId}:{userUid}";
        if (_pendingTimeouts.TryRemove(key, out var entry))
        {
            Logger.LogDebug("Cancelled timeout tracking for acknowledgment {ackId} for user {user}", 
                acknowledgmentId, entry.UserData.AliasOrUID);
        }
    }

    public void StartInvalidHashTimeout(string userUID, string dataHash)
    {
        var entry = new InvalidHashTimeoutEntry(userUID, dataHash, DateTimeOffset.Now);
        _invalidHashTimeouts[userUID] = entry;
        Logger.LogDebug("Started invalid hash timeout tracking for user {user} with hash {hash}", 
            userUID, dataHash[..Math.Min(8, dataHash.Length)]);
    }

    public void CancelInvalidHashTimeout(string userUID)
    {
        if (_invalidHashTimeouts.TryRemove(userUID, out _))
        {
            Logger.LogDebug("Cancelled invalid hash timeout tracking for user {user}", userUID);
        }
    }

    private async void CheckTimeouts(object? state)
    {
        if (ShouldPauseTimeoutProcessing())
        {
            DeferTimeouts(TimeSpan.FromMilliseconds(CheckIntervalMs));
            return;
        }

        var now = DateTimeOffset.Now;
        
        // Check regular acknowledgment timeouts
        var expiredEntries = _pendingTimeouts.Values
            .Where(entry => now - entry.StartTime >= TimeSpan.FromSeconds(Math.Clamp(_configService.Current.AcknowledgmentTimeoutSeconds, 5, 600)))
            .ToList();

        foreach (var entry in expiredEntries)
        {
            try
            {
                await HandleExpiredAcknowledgment(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling expired acknowledgment {ackId} for user {user}", 
                    entry.AcknowledgmentId, entry.UserData.AliasOrUID);
            }
        }

        // Check invalid hash timeouts
        var expiredInvalidHashEntries = _invalidHashTimeouts.Values
            .Where(entry => now - entry.StartTime >= TimeSpan.FromMilliseconds(InvalidHashTimeoutMs))
            .ToList();

        foreach (var entry in expiredInvalidHashEntries)
        {
            try
            {
                await HandleExpiredInvalidHash(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling expired invalid hash for user {user}", entry.UserUID);
            }
        }
    }

    private async Task HandleExpiredAcknowledgment(AcknowledgmentTimeoutEntry entry)
    {
        Logger.LogDebug("Checking expired acknowledgment {ackId} for user {user} after {elapsed}ms", 
            entry.AcknowledgmentId, entry.UserData.AliasOrUID, 
            (DateTimeOffset.Now - entry.StartTime).TotalMilliseconds);

        // Get the pair to check if it still has pending acknowledgment
        var pair = _pairManager.Value.GetPairByUID(entry.UserData.UID);
        if (pair == null || !pair.HasPendingAcknowledgment || !string.Equals(pair.LastAcknowledgmentId, entry.AcknowledgmentId, StringComparison.Ordinal))
        {
            // Acknowledgment was already processed or pair no longer exists
            _pendingTimeouts.TryRemove(entry.Key, out _);
            return;
        }

        try
        {
            if (ShouldPauseTimeoutProcessing())
            {
                _pendingTimeouts[entry.Key] = entry with { StartTime = DateTimeOffset.Now };
                return;
            }

            Logger.LogWarning("Acknowledgment timed out: ackId={ackId} user={user}", entry.AcknowledgmentId, entry.UserData.AliasOrUID);
            var context = $"ctx: localInDuty={_dalamudUtilService.IsInDuty} localInCombat={_dalamudUtilService.IsInCombatOrPerforming}";
            await pair.UpdateAcknowledgmentStatus(entry.AcknowledgmentId, false, DateTimeOffset.UtcNow,
                Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout,
                $"No acknowledgment received within timeout window ({context})").ConfigureAwait(false);
            Mediator.Publish(new DebugLogEventMessage(
                LogLevel.Warning,
                "ACK",
                "Ack timeout",
                Uid: entry.UserData.UID,
                Details: $"ackId={entry.AcknowledgmentId[..Math.Min(8, entry.AcknowledgmentId.Length)]} {context}"));

            _pendingTimeouts.TryRemove(entry.Key, out _);
            await TryAutoReconnectForAckFailureAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle expired acknowledgment {ackId} for user {user}", 
                entry.AcknowledgmentId, entry.UserData.AliasOrUID);
            
            // Remove from timeout tracking on error
            _pendingTimeouts.TryRemove(entry.Key, out _);
        }
    }

    private Task HandleExpiredInvalidHash(InvalidHashTimeoutEntry entry)
    {
        if (ShouldPauseTimeoutProcessing())
        {
            _invalidHashTimeouts[entry.UserUID] = entry with { StartTime = DateTimeOffset.Now };
            return Task.CompletedTask;
        }

        Logger.LogDebug("Handling expired invalid hash for user {user} after {elapsed}ms", 
            entry.UserUID, (DateTimeOffset.Now - entry.StartTime).TotalMilliseconds);

        // Get the pair to trigger reapply
        var pair = _pairManager.Value.GetPairByUID(entry.UserUID);
        if (pair == null)
        {
            Logger.LogDebug("Pair not found for user {user} - removing invalid hash timeout", entry.UserUID);
            _invalidHashTimeouts.TryRemove(entry.UserUID, out _);
            return Task.CompletedTask;
        }

        // Check if the pair still has a yellow eye (pending acknowledgment)
        if (!pair.HasPendingAcknowledgment)
        {
            Logger.LogDebug("Pair for user {user} no longer has pending acknowledgment - removing invalid hash timeout", entry.UserUID);
            _invalidHashTimeouts.TryRemove(entry.UserUID, out _);
            return Task.CompletedTask;
        }

        try
        {
            Logger.LogInformation("Triggering automatic character data reapply for user {user} after 15 seconds of invalid hash", entry.UserUID);

            pair.ApplyLastReceivedData(forced: true);

            _invalidHashTimeouts.TryRemove(entry.UserUID, out _);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to trigger character data reapply for user {user}", entry.UserUID);
            _invalidHashTimeouts.TryRemove(entry.UserUID, out _);
        }
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timeoutTimer?.Dispose();
            _pendingTimeouts.Clear();
            _invalidHashTimeouts.Clear();
        }
        base.Dispose(disposing);
    }

    private sealed record AcknowledgmentTimeoutEntry(
        string Key,
        string AcknowledgmentId, 
        UserData UserData, 
        string DataHash, 
        DateTimeOffset StartTime);

    private sealed record InvalidHashTimeoutEntry(
        string UserUID,
        string DataHash,
        DateTimeOffset StartTime);

    private bool ShouldPauseTimeoutProcessing()
    {
        var apiController = _apiController.Value;
        if (_dalamudUtilService.IsInDuty || _dalamudUtilService.IsInCombatOrPerforming)
        {
            if (_lastPauseLogUtc.AddSeconds(30) < DateTime.UtcNow)
            {
                _lastPauseLogUtc = DateTime.UtcNow;
                Mediator.Publish(new DebugLogEventMessage(
                    LogLevel.Debug,
                    "ACK",
                    "Ack timeout processing paused",
                    Details: $"ctx: localInDuty={_dalamudUtilService.IsInDuty} localInCombat={_dalamudUtilService.IsInCombatOrPerforming}"));
            }
            return true;
        }

        return apiController.IsTransientDisconnectInProgress || !apiController.IsConnected;
    }

    private void DeferTimeouts(TimeSpan delay)
    {
        foreach (var entry in _pendingTimeouts.ToArray())
        {
            _pendingTimeouts[entry.Key] = entry.Value with { StartTime = entry.Value.StartTime.Add(delay) };
        }

        foreach (var entry in _invalidHashTimeouts.ToArray())
        {
            _invalidHashTimeouts[entry.Key] = entry.Value with { StartTime = entry.Value.StartTime.Add(delay) };
        }
    }

    private async Task TryAutoReconnectForAckFailureAsync()
    {
        var apiController = _apiController.Value;

        if (!apiController.IsConnected || apiController.IsTransientDisconnectInProgress)
        {
            return;
        }

        if (_autoReconnectsSinceConnect >= 1)
        {
            return;
        }

        if (_seenAckSuccessSinceConnect)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (_lastConnectedUtc == DateTime.MinValue || nowUtc - _lastConnectedUtc > TimeSpan.FromSeconds(90))
        {
            return;
        }

        if (_lastAutoReconnectUtc != DateTime.MinValue && nowUtc - _lastAutoReconnectUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _ackTimeoutsSinceConnect++;
        if (_ackTimeoutsSinceConnect < 2)
        {
            return;
        }

        _autoReconnectsSinceConnect++;
        _lastAutoReconnectUtc = nowUtc;

        Mediator.Publish(new DebugLogEventMessage(
            LogLevel.Warning,
            "ACK",
            "Triggering auto-reconnect due to repeated acknowledgment timeouts",
            Details: $"timeoutsSinceConnect={_ackTimeoutsSinceConnect}"));

        try
        {
            await apiController.CreateConnectionsAsync(forceCharacterDataReload: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Auto-reconnect failed");
        }
    }
}
