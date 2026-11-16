using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;

namespace Sphene.Services;

/// <summary>Display-ready chat row: the wire DTO plus the locally resolved
/// text (or its absence). <see cref="ChatMessageKind"/> on the DTO keeps the
/// honest provenance - native seal vs. migrated legacy plaintext.</summary>
public sealed record ChatEntry(ChatMessageDto Dto, string? Text, ChatEntryState State)
{
    public bool IsSealed => State == ChatEntryState.Sealed;
    public bool WasMigrated => Dto.Kind == ChatMessageKind.SealedMigrated;
    public bool IsPlaintext => State == ChatEntryState.Plaintext;
}
