using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// Per-user profile customization, synced as a small JSON blob on the profile.
/// Unknown keys are ignored on parse so newer clients can add fields
/// (page tint, layout flags, housing links, ...) without a schema change.
/// Colors serialize as "#RRGGBBAA" hex strings (human-readable order).
/// </summary>
public record SpheneProfileTheme(uint? Accent, uint? BannerTop, uint? BannerBottom,
    ProfileRowFx RowHover = ProfileRowFx.Glow, ProfileRowFx RowIdle = ProfileRowFx.None,
    bool BannerTopOver = false, bool BannerBottomOver = false,
    ProfileRowFx CardHover = ProfileRowFx.None, ProfileRowFx CardIdle = ProfileRowFx.None,
    bool CardShimmer = true)
{
    public static readonly SpheneProfileTheme Default = new(null, null, null);

    /// <summary>Parses the stored JSON theme; returns <see cref="Default"/> on any failure.</summary>
    public static SpheneProfileTheme Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var obj = JObject.Parse(json);
            return new SpheneProfileTheme(
                ParseColor(obj["accent"]?.Value<string>()),
                ParseColor(obj["bannerTop"]?.Value<string>()),
                ParseColor(obj["bannerBottom"]?.Value<string>()),
                // prefer the full flag list; older clients only ever see the
                // first flag's name in the legacy keys
                ParseEffect(obj["rowHoverFx"]?.Value<string>()
                    ?? obj["rowHover"]?.Value<string>(), ProfileRowFx.Glow),
                ParseEffect(obj["rowIdleFx"]?.Value<string>()
                    ?? obj["rowIdle"]?.Value<string>(), ProfileRowFx.None),
                obj["bannerTopOver"]?.Value<bool>() ?? false,
                obj["bannerBottomOver"]?.Value<bool>() ?? false,
                ParseEffect(obj["cardHoverFx"]?.Value<string>()
                    ?? obj["cardHover"]?.Value<string>(), ProfileRowFx.None),
                ParseEffect(obj["cardIdleFx"]?.Value<string>()
                    ?? obj["cardIdle"]?.Value<string>(), ProfileRowFx.None),
                obj["cardShimmer"]?.Value<bool>() ?? true);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public string Serialize()
    {
        var obj = new JObject();
        if (Accent.HasValue) obj["accent"] = $"#{Accent.Value:X8}";
        if (BannerTop.HasValue) obj["bannerTop"] = $"#{BannerTop.Value:X8}";
        if (BannerBottom.HasValue) obj["bannerBottom"] = $"#{BannerBottom.Value:X8}";
        var hover = RowHover & ProfileRowFx.HoverMask;
        var idle = RowIdle & ProfileRowFx.IdleMask;
        // legacy keys carry only the first flag's name so older clients render
        // a sensible single effect; the full combination rides in the new keys
        if (hover != ProfileRowFx.Glow) obj["rowHover"] = FirstFlagName(hover);
        if (hover != ProfileRowFx.Glow && !IsSingleFlag(hover)) obj["rowHoverFx"] = hover.ToString();
        if (idle != ProfileRowFx.None) obj["rowIdle"] = FirstFlagName(idle);
        if (idle != ProfileRowFx.None && !IsSingleFlag(idle)) obj["rowIdleFx"] = idle.ToString();
        var cardHover = CardHover & ProfileRowFx.HoverMask;
        var cardIdle = CardIdle & ProfileRowFx.IdleMask;
        if (cardHover != ProfileRowFx.None) obj["cardHover"] = FirstFlagName(cardHover);
        if (cardHover != ProfileRowFx.None && !IsSingleFlag(cardHover)) obj["cardHoverFx"] = cardHover.ToString();
        if (cardIdle != ProfileRowFx.None) obj["cardIdle"] = FirstFlagName(cardIdle);
        if (cardIdle != ProfileRowFx.None && !IsSingleFlag(cardIdle)) obj["cardIdleFx"] = cardIdle.ToString();
        if (!CardShimmer) obj["cardShimmer"] = false;
        if (BannerTopOver) obj["bannerTopOver"] = true;
        if (BannerBottomOver) obj["bannerBottomOver"] = true;
        return obj.Count == 0 ? string.Empty : obj.ToString(Formatting.None);
    }

    /// <summary>The themed accent color, or <paramref name="fallback"/> when unset.</summary>
    public Vector4 AccentColor(Vector4 fallback)
        => Accent.HasValue ? ToVector4(Accent.Value) : fallback;

    /// <summary>Banner gradient top color, falling back to the accent.</summary>
    public Vector4 BannerTopColor(Vector4 accentFallback)
        => (BannerTop ?? Accent).HasValue ? ToVector4((BannerTop ?? Accent)!.Value) : accentFallback;

    /// <summary>Banner gradient bottom color, falling back to the accent.</summary>
    public Vector4 BannerBottomColor(Vector4 accentFallback)
        => (BannerBottom ?? Accent).HasValue ? ToVector4((BannerBottom ?? Accent)!.Value) : accentFallback;

    /// <summary>Packs a Vector4 (0..1 RGBA) into the theme's "#RRGGBBAA" order.</summary>
    public static uint FromVector4(Vector4 color)
        => ((uint)Math.Clamp(color.X * 255f, 0f, 255f) << 24)
            | ((uint)Math.Clamp(color.Y * 255f, 0f, 255f) << 16)
            | ((uint)Math.Clamp(color.Z * 255f, 0f, 255f) << 8)
            | (uint)Math.Clamp(color.W * 255f, 0f, 255f);

    /// <summary>Unpacks a "#RRGGBBAA"-ordered uint into a 0..1 Vector4.</summary>
    public static Vector4 ToVector4(uint rgba)
        => new(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f,
            ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    private static ProfileRowFx ParseEffect(string? name, ProfileRowFx fallback)
        => Enum.TryParse<ProfileRowFx>(name, ignoreCase: true, out var fx) ? fx : fallback;

    /// <summary>Name of the first set flag in enum order ("None" when empty).</summary>
    private static string FirstFlagName(ProfileRowFx fx)
    {
        foreach (var name in Enum.GetNames<ProfileRowFx>())
        {
            if (Enum.TryParse<ProfileRowFx>(name, out var bit)
                && bit != ProfileRowFx.None
                && (fx & bit) == bit)
            {
                return name;
            }
        }
        return ProfileRowFx.None.ToString();
    }

    private static bool IsSingleFlag(ProfileRowFx fx)
        => fx != ProfileRowFx.None && ((int)fx & ((int)fx - 1)) == 0;

    private static uint? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var span = hex.AsSpan().TrimStart('#');
        if (span.Length == 6 && uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return (rgb << 8) | 0xFFu; // RRGGBB -> RRGGBBAA
        }
        return span.Length == 8
            && uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out var rgba)
            ? rgba : null;
    }
}
