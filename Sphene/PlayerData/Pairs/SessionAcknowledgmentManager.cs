using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.User;
using AcknowledgmentErrorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode;
using Sphene.PlayerData.Pairs.State;
using Sphene.Services.Mediator;
using Sphene.Services;
using Sphene.Services.Events;
using Sphene.SpheneConfiguration.Models;
using Dalamud.Interface.ImGuiNotification;
using NotificationType = Sphene.SpheneConfiguration.Models.NotificationType;

namespace Sphene.PlayerData.Pairs;

// Session-based acknowledgment manager for handling multiple concurrent users.
// All pure business logic lives in SessionAcknowledgmentState.
// This class only orchestrates Dalamud-specific side effects (UI, notifications, pair updates).
public class SessionAcknowledgmentManager : DisposableMediatorSubscriberBase
{
    private readonly ILogger<SessionAcknowledgmentManager> _logger;
    private readonly Func<UserData, IPairAcknowledgment?> _getPairFunc;
    private readonly MessageService _messageService;
    private readonly AcknowledgmentBatchingService _batchingService;
    private readonly SessionAcknowledgmentState _state = new();

    private readonly string _currentSessionId;

    public SessionAcknowledgmentManager(ILogger<SessionAcknowledgmentManager> logger, SpheneMediator mediator,
        Func<UserData, IPairAcknowledgment?> getPairFunc, MessageService messageService, AcknowledgmentBatchingService batchingService) : base(logger, mediator)
    {
        _logger = logger;
        _getPairFunc = getPairFunc;
        _messageService = messageService;
        _batchingService = batchingService;
        _currentSessionId = SessionAcknowledgmentState.GenerateSessionId();

        _logger.LogInformation("SessionAcknowledgmentManager initialized with session ID: {sessionId}", _currentSessionId);
    }

    public static string? ExtractSessionId(string acknowledgmentId) => SessionAcknowledgmentState.ExtractSessionId(acknowledgmentId);
    public static bool IsValidHashVersion(string hashVersionKey) => SessionAcknowledgmentState.IsValidHashVersion(hashVersionKey);

    public SessionAcknowledgmentState.AckResultMetrics GetResultMetrics() => _state.GetMetrics();

    // Set pending acknowledgment for hash-based system - only latest per user
    public void SetPendingAcknowledgmentForHashVersion(List<UserData> recipients, string hashVersionKey)
    {
        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key: {hashVersionKey}", hashVersionKey);
            return;
        }

        foreach (var recipient in recipients)
        {
            var result = _state.SetPending(recipient.UID, hashVersionKey, DateTimeOffset.UtcNow);

            if (result.WasReplaced && !string.IsNullOrEmpty(result.PreviousAcknowledgmentId))
            {
                try
                {
                    var pair = _getPairFunc(recipient);
                    if (pair != null && string.Equals(pair.LastAcknowledgmentId, result.PreviousAcknowledgmentId, StringComparison.Ordinal))
                    {
                        pair.ClearPendingAcknowledgmentForce(_messageService);
                        _logger.LogDebug("Cleared previous pending ack {oldAck} on pair for user {user}", result.PreviousAcknowledgmentId, recipient.AliasOrUID);
                    }

                    _messageService.CleanTaggedMessages($"ack_{result.PreviousAcknowledgmentId}");
                    Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                        AcknowledgmentId: result.PreviousAcknowledgmentId,
                        User: recipient
                    ));
                    Mediator.Publish(new RefreshUiMessage());
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed clearing previous pending ack {oldAck} for user {user}", result.PreviousAcknowledgmentId, recipient.AliasOrUID);
                }
            }

            _logger.LogDebug(result.WasReplaced
                    ? "Replaced acknowledgment {oldHashVersion} with {newHashVersion} for user {user}"
                    : "Added pending acknowledgment {hashVersionKey} for user {user}",
                result.PreviousAcknowledgmentId, hashVersionKey, recipient.UID);

            _messageService.AddTaggedMessage(
                $"ack_{hashVersionKey}",
                $"Waiting for acknowledgment from {recipient.AliasOrUID}",
                NotificationType.Info,
                "Acknowledgment Pending",
                TimeSpan.FromSeconds(5)
            );

            Mediator.Publish(new AcknowledgmentPendingMessage(
                new AcknowledgmentEventDto(
                    hashVersionKey,
                    recipient,
                    AcknowledgmentStatus.Pending,
                    DateTime.UtcNow)
            ));

            Mediator.Publish(new RefreshUiMessage());
        }
    }
    


    
    
    
    // Process received acknowledgment for hash-based system
    public bool ProcessReceivedAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        var hashVersionKey = acknowledgmentDto.DataHash;
        var acknowledgingUser = acknowledgmentDto.User;

        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key: {hashVersionKey}", hashVersionKey);
            return false;
        }

        var result = _state.ProcessReceivedAcknowledgment(
            acknowledgingUser.UID,
            hashVersionKey,
            acknowledgmentDto.Success,
            acknowledgmentDto.ErrorCode,
            DateTimeOffset.UtcNow);

        if (!result.WasProcessed)
        {
            _logger.LogDebug("Acknowledgment {hashVersionKey} from {user} is not the latest, not found, and not in history", hashVersionKey, acknowledgingUser.AliasOrUID);
            return false;
        }

        if (result.WasHistory && !result.WasLatest)
        {
            _logger.LogDebug("Acknowledgment {hashVersionKey} from {user} matched history (out-of-order)", hashVersionKey, acknowledgingUser.AliasOrUID);
        }

        var pair = _getPairFunc(acknowledgingUser);
        if (pair != null)
        {
            pair.UpdateAcknowledgmentStatus(hashVersionKey, acknowledgmentDto.Success, DateTimeOffset.UtcNow,
                    acknowledgmentDto.ErrorCode, acknowledgmentDto.ErrorMessage)
                .GetAwaiter().GetResult();
            pair.SetOutgoingAcknowledgmentContext(hashVersionKey, acknowledgmentDto.SessionId);
            _logger.LogDebug("Updated pair acknowledgment status for user {user} - HashVersion: {hashVersionKey} success={success} errorCode={errorCode}",
                acknowledgingUser.AliasOrUID, hashVersionKey, acknowledgmentDto.Success, acknowledgmentDto.ErrorCode);
            Mediator.Publish(new DebugLogEventMessage(
                acknowledgmentDto.Success ? LogLevel.Information : LogLevel.Warning,
                "ACK",
                acknowledgmentDto.Success ? "Ack received" : "Ack received (fail)",
                Uid: acknowledgingUser.UID,
                Details: $"hash={hashVersionKey[..Math.Min(8, hashVersionKey.Length)]} code={acknowledgmentDto.ErrorCode} msg={acknowledgmentDto.ErrorMessage ?? "-"} session={acknowledgmentDto.SessionId ?? "-"}"));

            _messageService.AddTaggedMessage(
                $"ack_result_{hashVersionKey}_{acknowledgingUser.UID}",
                acknowledgmentDto.Success
                    ? $"Acknowledgment received from {acknowledgingUser.AliasOrUID}"
                    : $"Acknowledgment failed from {acknowledgingUser.AliasOrUID}",
                acknowledgmentDto.Success ? NotificationType.Success : NotificationType.Warning,
                acknowledgmentDto.Success ? "Acknowledgment Received" : "Acknowledgment Failed",
                TimeSpan.FromSeconds(3)
            );

            Mediator.Publish(new AcknowledgmentReceivedMessage(
                new AcknowledgmentEventDto(
                    hashVersionKey,
                    acknowledgingUser,
                    AcknowledgmentStatus.Received,
                    DateTime.UtcNow)
            ));
        }
        else
        {
            _logger.LogWarning("Could not find pair for user {user} to update acknowledgment status", acknowledgingUser.AliasOrUID);
        }

        _logger.LogDebug("Processed acknowledgment from {user} for HashVersion {hashVersionKey}",
            acknowledgingUser.AliasOrUID, hashVersionKey);

        _messageService.CleanTaggedMessages($"ack_{hashVersionKey}");

        _messageService.AddTaggedMessage(
            $"ack_complete_{hashVersionKey}",
            acknowledgmentDto.Success ? "Acknowledgment received successfully" : "Acknowledgment failed",
            acknowledgmentDto.Success ? NotificationType.Success : NotificationType.Warning,
            acknowledgmentDto.Success ? "Acknowledgment Complete" : "Acknowledgment Failed",
            TimeSpan.FromSeconds(4)
        );

        Mediator.Publish(new AcknowledgmentBatchCompletedMessage(
            hashVersionKey,
            new List<UserData> { acknowledgingUser },
            DateTime.UtcNow
        ));

        Mediator.Publish(new AcknowledgmentUiRefreshMessage(
            AcknowledgmentId: hashVersionKey,
            User: acknowledgingUser
        ));

        var totalPending = _state.GetPendingCount();
        Mediator.Publish(new AcknowledgmentMetricsUpdatedMessage(
            totalPending,
            1,
            0,
            DateTime.UtcNow
        ));
        var stats = _batchingService.GetStatistics();
        Logger.LogDebug("Batch stats - Pending: {pending}, Users: {users}", stats.PendingBatches, stats.TotalPendingUsers);

        Mediator.Publish(new RefreshUiMessage());
        return true;
    }
    
    // Get all pending acknowledgments
    public Dictionary<string, string> GetPendingAcknowledgments() => _state.GetPendingAcknowledgments();

    // Get total pending acknowledgment count
    public int GetPendingAcknowledgmentCount() => _state.GetPendingCount();

    // Remove a pending acknowledgment for a user and clear pair state
    public bool RemovePendingAcknowledgment(UserData user, string? acknowledgmentId = null)
    {
        try
        {
            if (string.IsNullOrEmpty(user.UID))
            {
                Logger.LogWarning("RemovePendingAcknowledgment called with empty user UID");
                return false;
            }

            var result = _state.RemovePending(user.UID, acknowledgmentId);

            if (!result.WasRemoved)
            {
                Logger.LogDebug("No pending acknowledgment found for user {user}", user.AliasOrUID);
                return false;
            }

            // Clear pair pending state and related notifications
            var pair = _getPairFunc(user);
            if (pair != null)
            {
                pair.ClearPendingAcknowledgmentForce(_messageService);
                Logger.LogDebug("Cleared pending acknowledgment for user {user}", user.AliasOrUID);
            }

            if (!string.IsNullOrEmpty(result.RemovedAcknowledgmentId))
            {
                _messageService.CleanTaggedMessages($"ack_{result.RemovedAcknowledgmentId}");

                Mediator.Publish(new AcknowledgmentUiRefreshMessage(
                    AcknowledgmentId: result.RemovedAcknowledgmentId,
                    User: user
                ));
            }

            Mediator.Publish(new RefreshUiMessage());
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to remove pending acknowledgment for user {user}", user.AliasOrUID);
            return false;
        }
    }

    // Clean up old pending acknowledgments based on age
    public async Task CleanupOldPendingAcknowledgments(TimeSpan maxAge)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = _state.CleanupOldPending(maxAge, now);

        foreach (var entry in removed)
        {
            var userData = new UserData(entry.UserUid);
            var pair = _getPairFunc(userData);
            if (pair != null && string.Equals(pair.LastAcknowledgmentId, entry.AckId, StringComparison.Ordinal))
            {
                pair.UpdateAcknowledgmentStatus(entry.AckId, false, now,
                        Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout,
                        "Acknowledgment timed out")
                    .GetAwaiter().GetResult();
            }

            _messageService.CleanTaggedMessages($"ack_{entry.AckId}");
            _messageService.AddTaggedMessage(
                $"ack_timeout_{entry.AckId}",
                $"Acknowledgment {entry.AckId} timed out and was removed",
                NotificationType.Warning,
                "Acknowledgment Timeout",
                TimeSpan.FromSeconds(5)
            );

            Mediator.Publish(new AcknowledgmentTimeoutMessage(
                entry.AckId,
                userData,
                DateTime.UtcNow
            ));
        }

        if (removed.Count > 0)
        {
            _logger.LogInformation("Cleaned up {count} old pending acknowledgments", removed.Count);

            var totalPending = _state.GetPendingCount();
            Mediator.Publish(new AcknowledgmentMetricsUpdatedMessage(
                totalPending,
                0,
                removed.Count,
                DateTime.UtcNow
            ));

            Mediator.Publish(new AcknowledgmentUiRefreshMessage(RefreshAll: true));
            Mediator.Publish(new RefreshUiMessage());
        }
    }

    // Check if there are any pending acknowledgments
    public bool HasPendingAcknowledgments() => _state.HasPendingAcknowledgments();

    // Clean up old sessions (simplified for single acknowledgment per user model)
    public async Task CleanupOldSessions(TimeSpan maxAge)
    {
        await CleanupOldPendingAcknowledgments(maxAge).ConfigureAwait(false);
    }

    // Get acknowledgment status for UI display
    public List<string> GetAcknowledgmentStatuses()
    {
        var pending = _state.GetPendingAcknowledgments();
        return pending.Select(kvp => $"User: {kvp.Key}, HashVersion: {kvp.Value}").ToList();
    }

    public string CurrentSessionId => _currentSessionId;

    // Process timeout acknowledgment - mark as failed and update pair status
    public void ProcessTimeoutAcknowledgment(string hashVersionKey)
    {
        if (string.IsNullOrEmpty(hashVersionKey))
        {
            _logger.LogWarning("Invalid hash version key for timeout: {hashVersionKey}", hashVersionKey);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var timedOut = _state.ProcessTimeout(hashVersionKey, now);

        foreach (var entry in timedOut)
        {
            var userData = new UserData(entry.UserUid, null);
            var pair = _getPairFunc(userData);
            if (pair != null)
            {
                pair.UpdateAcknowledgmentStatus(hashVersionKey, false, now,
                        Sphene.API.Dto.User.AcknowledgmentErrorCode.NotArrivedTimeout,
                        "Acknowledgment timed out")
                    .GetAwaiter().GetResult();
                _logger.LogWarning("Updated pair acknowledgment status for timeout - User: {user}, HashVersion: {hashVersionKey}", entry.UserUid, hashVersionKey);

                _messageService.AddTaggedMessage(
                    $"ack_timeout_{hashVersionKey}_{entry.UserUid}",
                    $"Acknowledgment timed out for user {entry.UserUid}",
                    NotificationType.Warning,
                    "Acknowledgment Timeout",
                    TimeSpan.FromSeconds(5)
                );

                Mediator.Publish(new AcknowledgmentTimeoutMessage(
                    hashVersionKey,
                    userData,
                    DateTime.UtcNow
                ));
            }
        }

        Mediator.Publish(new RefreshUiMessage());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _state.Clear();
            _logger.LogInformation("SessionAcknowledgmentManager disposed for session: {sessionId}", _currentSessionId);
        }

        base.Dispose(disposing);
    }
}
