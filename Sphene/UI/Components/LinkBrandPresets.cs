using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// Registry of link-chip brand presets · the link DTO's <c>Icon</c> field
/// stores the preset key; "custom"/null falls back to a neutral globe.
/// Extend by appending rows to <see cref="All"/>.
/// </summary>
internal static class LinkBrandPresets
{
    public static readonly LinkPreset Custom = new("custom", "Custom link",
        FontAwesomeIcon.Globe, null, "https://…");

    public static readonly IReadOnlyList<LinkPreset> All = new List<LinkPreset>
    {
        Custom,
        new("discord", "Discord", null, null,
            "https://discord.gg/…", SpheneBrandIcons.Brand.Discord),
        new("x", "X", null, null,
            "https://x.com/…", SpheneBrandIcons.Brand.X),
        new("kofi", "Ko-fi", null, null,
            "https://ko-fi.com/…", SpheneBrandIcons.Brand.Kofi),
        new("youtube", "YouTube", null, null,
            "https://youtube.com/…", SpheneBrandIcons.Brand.YouTube),
        new("twitch", "Twitch", null, null,
            "https://twitch.tv/…", SpheneBrandIcons.Brand.Twitch),
        new("instagram", "Instagram", null, null,
            "https://instagram.com/…", SpheneBrandIcons.Brand.Instagram),
        new("bluesky", "Bluesky", null, null,
            "https://bsky.app/…", SpheneBrandIcons.Brand.Bluesky),
        new("tiktok", "TikTok", null, null,
            "https://tiktok.com/@…", SpheneBrandIcons.Brand.TikTok),
        new("patreon", "Patreon", null, null,
            "https://patreon.com/…", SpheneBrandIcons.Brand.Patreon),
        new("github", "GitHub", null, null,
            "https://github.com/…", SpheneBrandIcons.Brand.GitHub),
        new("carrd", "Carrd", null, null,
            "https://….carrd.co", SpheneBrandIcons.Brand.Carrd),
        new("lodestone", "Lodestone", FontAwesomeIcon.Scroll,
            SpheneUI.Palette.SpheneGold, "https://eu.finalfantasyxiv.com/lodestone/…"),
        new("venue", "Venue", FontAwesomeIcon.MapMarkedAlt,
            null, "https://…"),
        new("shop", "Shop", FontAwesomeIcon.Store,
            null, "https://…"),
        new("email", "E-mail", FontAwesomeIcon.Envelope,
            null, "mailto:…"),
    };

    public static LinkPreset? Find(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        foreach (var p in All)
        {
            if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>Draws the preset's mark centered in <paramref name="rect"/>  · 
    /// brand texture when available, the glyph letter for marks without a
    /// FontAwesome icon, else the FA glyph tinted in the preset color.</summary>
    public static void DrawMark(LinkPreset? preset, ImDrawListPtr dl,
        Vector2 rectMin, Vector2 rectMax, Vector4 fallbackColor)
    {
        var center = (rectMin + rectMax) * 0.5f;
        var color = preset?.Color ?? fallbackColor;

        if (preset?.Brand is { } brand)
        {
            var side = MathF.Min(rectMax.X - rectMin.X, rectMax.Y - rectMin.Y) * 0.9f;
            SpheneBrandIcons.DrawIcon(dl, brand,
                center - new Vector2(side * 0.5f),
                center + new Vector2(side * 0.5f), SpheneBrandIcons.GetBrandColor(brand));
            return;
        }

        var u32 = SpheneUI.ColorUtils.ToU32(color);
        if (preset?.Glyph is { } glyph)
        {
            var size = ImGui.CalcTextSize(glyph);
            dl.AddText(center - size * 0.5f, u32, glyph);
            return;
        }

        var icon = preset?.Icon ?? FontAwesomeIcon.Globe;
        using (SpheneUI.Icons.PushFont())
        {
            var size = ImGui.CalcTextSize(icon.ToIconString());
            dl.AddText(center - size * 0.5f, u32, icon.ToIconString());
        }
    }
}
