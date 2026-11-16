using Sphene.API.Dto.User;

namespace Sphene.Services.E2ee;

public sealed record ChatSealResult(List<ChatCipherCopyDto> Copies,
    ChatSealFailure Failure)
{
    public bool Success => Failure == ChatSealFailure.None;
}
