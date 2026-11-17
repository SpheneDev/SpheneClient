using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.Services.E2ee;

namespace Sphene.Services;

/// <summary>Display-ready chat row: the wire DTO plus the locally resolved
/// text (or its absence). <see cref="ChatMessageKind"/> on the DTO keeps the
/// honest provenance - native seal vs. migrated legacy plaintext.</summary>
public sealed record ChatEntry(ChatMessageDto Dto, string? Text, ChatEntryState State)
{
    private bool _gifParsed;
    private ChatContentCodec.GifContent? _gif;

    public bool IsSealed => State == ChatEntryState.Sealed;
    public bool WasMigrated => Dto.Kind == ChatMessageKind.SealedMigrated;
    public bool IsPlaintext => State == ChatEntryState.Plaintext;

    /// <summary>Typed GIF payload when the resolved body is a sealed
    /// <see cref="ChatContentCodec"/> message; null for ordinary text.</summary>
    public ChatContentCodec.GifContent? Gif
    {
        get
        {
            if (!_gifParsed)
            {
                _gifParsed = true;
                ChatContentCodec.TryParseGif(Text, out _gif);
            }
            return _gif;
        }
    }

    /// <summary>Text to show wherever a compact preview of the row is needed.</summary>
    public string PreviewText => Gif != null ? "[GIF]" : Text ?? string.Empty;
}
