using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sphene.Services.E2ee;

/// <summary>
/// Typed chat content carried INSIDE the sealed UTF-8 plaintext of the
/// E2EE envelope. Wire format is a compact JSON object with a strict
/// versioned marker so ordinary user text (including JSON-looking text)
/// never collides. Adding a new <c>"t"</c> value is additive; older
/// clients surface the raw text, which stays human-readable.
/// </summary>
public static class ChatContentCodec
{
    private const int SchemeVersion = 1;
    private const string TypeGif = "gif";
    private const int MaxProviderIdLength = 64;
    private const int MaxDimension = 512;

    private static readonly Regex ProviderIdPattern
        = new("^[A-Za-z0-9_-]{3,64}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /// <summary>Decoded GIF content reference (provider id + source dimensions).</summary>
    public sealed record GifContent(string Provider, string Id, int Width, int Height);

    /// <summary>Encodes a GIF reference as the sealed plaintext body.</summary>
    public static string Gif(string provider, string id, int width, int height)
    {
        if (!IsValidProviderId(provider, id) || !IsValidDimension(width) || !IsValidDimension(height))
        {
            throw new ArgumentException("invalid gif content", nameof(id));
        }
        return $"{{\"sc\":{SchemeVersion},\"t\":\"{TypeGif}\",\"p\":\"{provider}\"," +
               $"\"i\":\"{id}\",\"w\":{width},\"h\":{height}}}";
    }

    /// <summary>
    /// Strictly parses a GIF payload. Only returns true when the whole
    /// decrypted body is exactly our typed object - unknown types,
    /// trailing text, unknown providers and hostile ids all reject.
    /// </summary>
    public static bool TryParseGif(string? text, out GifContent? gif)
    {
        gif = null;
        if (string.IsNullOrEmpty(text) || text[0] != '{' || text.Length > 256)
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!root.TryGetProperty("sc", out var sc) || sc.ValueKind != JsonValueKind.Number
                || sc.GetInt32() != SchemeVersion)
            {
                return false;
            }
            if (!root.TryGetProperty("t", out var t)
                || !string.Equals(t.GetString(), TypeGif, StringComparison.Ordinal))
            {
                return false;
            }
            var provider = root.TryGetProperty("p", out var p) ? p.GetString() : null;
            var id = root.TryGetProperty("i", out var i) ? i.GetString() : null;
            var w = root.TryGetProperty("w", out var wEl) && wEl.ValueKind == JsonValueKind.Number
                ? wEl.GetInt32() : 0;
            var h = root.TryGetProperty("h", out var hEl) && hEl.ValueKind == JsonValueKind.Number
                ? hEl.GetInt32() : 0;
            if (!IsValidProviderId(provider, id) || !IsValidDimension(w) || !IsValidDimension(h))
            {
                return false;
            }
            gif = new GifContent(provider!, id!, w, h);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Provider ids this build can persist/parse. "klipy" is the
    /// active provider; "giphy" stays accepted so messages sealed by the
    /// earlier dev build still decode (they render as a bounded placeholder
    /// since no GIPHY backend ships).</summary>
    public static bool IsValidProviderId(string? provider, string? id)
        => (string.Equals(provider, "klipy", StringComparison.Ordinal)
                || string.Equals(provider, "giphy", StringComparison.Ordinal))
            && id != null
            && id.Length <= MaxProviderIdLength
            && ProviderIdPattern.IsMatch(id);

    private static bool IsValidDimension(int v) => v is > 0 and <= MaxDimension;
}
