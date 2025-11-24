using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.Services.Mediator;
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
    private const int TimeoutIntervalMs = 5000; // 5 seconds
    private const int InvalidHashTimeoutMs = 15000; // 15 seconds for invalid hash reapply
    private const int CheckIntervalMs = 5000; // Check every 5 seconds

    public AcknowledgmentTimeoutManager(ILogger<AcknowledgmentTimeoutManager> logger, 
        SpheneMediator mediator, Lazy<ApiController> apiController, Lazy<PairManager> pairManager) 
        : base(logger, mediator)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        _timeoutTimer = new Timer(CheckTimeouts, null, CheckIntervalMs, CheckIntervalMs);
    }

    public void StartTimeout(string acknowledgmentId, UserData userData, string dataHash)
    {
        var entry = new AcknowledgmentTimeoutEntry(acknowledgmentId, userData, dataHash, DateTimeOffset.Now);
        _pendingTimeouts[acknowledgmentId] = entry;
        Logger.LogDebug("Started timeout tracking for acknowledgment {ackId} for user {user}", 
            acknowledgmentId, userData.AliasOrUID);
    }

    public void CancelTimeout(string acknowledgmentId)
    {
        if (_pendingTimeouts.TryRemove(acknowledgmentId, out var entry))
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
        var now = DateTimeOffset.Now;
        
        // Check regular acknowledgment timeouts
        var expiredEntries = _pendingTimeouts.Values
            .Where(entry => now - entry.StartTime >= TimeSpan.FromMilliseconds(TimeoutIntervalMs))
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
            _pendingTimeouts.TryRemove(entry.AcknowledgmentId, out _);
            return;
        }

        try
        {
            // Validate the current hash with the server
            var currentUserUID = _apiController.Value.UID;
            if (string.IsNullOrEmpty(currentUserUID))
            {
                Logger.LogWarning("Cannot validate hash - current user UID is null");
                _pendingTimeouts.TryRemove(entry.AcknowledgmentId, out _);
                return;
            }

            var response = await _apiController.Value.ValidateCharaDataHash(currentUserUID, entry.DataHash).ConfigureAwait(false);
            if (response != null && response.IsValid)
            {
                Logger.LogInformation("Hash validation successful for expired acknowledgment {ackId} - auto-completing acknowledgment for user {user}", 
                    entry.AcknowledgmentId, entry.UserData.AliasOrUID);

                // Hash is still valid, automatically complete the acknowledgment
                await pair.UpdateAcknowledgmentStatus(entry.DataHash, true, DateTimeOffset.Now).ConfigureAwait(false);
                
                // Remove from timeout tracking
                _pendingTimeouts.TryRemove(entry.AcknowledgmentId, out _);
            }
            else
            {
                Logger.LogDebug("Hash validation failed for expired acknowledgment {ackId} - keeping pending for user {user}", 
                    entry.AcknowledgmentId, entry.UserData.AliasOrUID);
                
                // Hash is no longer valid, start invalid hash timeout for automatic reapply
                StartInvalidHashTimeout(entry.UserData.UID, entry.DataHash);
                
                // Remove from timeout tracking but keep acknowledgment pending
                _pendingTimeouts.TryRemove(entry.AcknowledgmentId, out _);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to validate hash for expired acknowledgment {ackId} for user {user}", 
                entry.AcknowledgmentId, entry.UserData.AliasOrUID);
            
            // Remove from timeout tracking on error
            _pendingTimeouts.TryRemove(entry.AcknowledgmentId, out _);
        }
    }

    private Task HandleExpiredInvalidHash(InvalidHashTimeoutEntry entry)
    {
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
        string AcknowledgmentId, 
        UserData UserData, 
        string DataHash, 
        DateTimeOffset StartTime);

    private sealed record InvalidHashTimeoutEntry(
        string UserUID,
        string DataHash,
        DateTimeOffset StartTime);
}
