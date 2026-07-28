using Sphene.Services;

namespace Sphene.PlayerData.Pairs;

/// <summary>
/// Minimal interface for acknowledgment operations on a pair.
/// Enables testability of <see cref="SessionAcknowledgmentManager"/> without concrete <see cref="Pair"/> dependencies.
/// </summary>
public interface IPairAcknowledgment
{
    string? LastAcknowledgmentId { get; }

    void ClearPendingAcknowledgmentForce(MessageService? messageService = null);

    System.Threading.Tasks.Task UpdateAcknowledgmentStatus(
        string? acknowledgmentId,
        bool success,
        DateTimeOffset timestamp,
        Sphene.API.Dto.User.AcknowledgmentErrorCode errorCode = Sphene.API.Dto.User.AcknowledgmentErrorCode.None,
        string? errorMessage = null);

    void SetOutgoingAcknowledgmentContext(string? dataHash, string? sessionId);
}
