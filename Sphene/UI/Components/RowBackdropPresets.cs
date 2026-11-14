using Dalamud.Bindings.ImGui;
using Sphene.UI.Design;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Advanced;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Curated row backdrops shipped with Sphene - Discord-style defaults so
/// supporters get polished list rows without uploading their own files.
/// Generated procedurally (diagonal tri-tone gradient + light hotspot), cached
/// per preset and uploaded through the normal RowImageBase64 pipeline, so
/// viewers on any client version render the same image.
/// </summary>
public static class RowBackdropPresets
{
    public readonly record struct Preset(string Id, string Label,
        Vector4 From, Vector4 Via, Vector4 To);

    public static readonly Preset[] All =
    [
        new("aurora", "Aurora",   new(0.05f, 0.45f, 0.55f, 1f), new(0.25f, 0.20f, 0.60f, 1f), new(0.02f, 0.05f, 0.20f, 1f)),
        new("sunset", "Sunset",   new(0.95f, 0.45f, 0.20f, 1f), new(0.75f, 0.20f, 0.45f, 1f), new(0.15f, 0.05f, 0.25f, 1f)),
        new("ocean",  "Deep Sea", new(0.05f, 0.25f, 0.55f, 1f), new(0.05f, 0.55f, 0.75f, 1f), new(0.01f, 0.05f, 0.15f, 1f)),
        new("sakura", "Sakura",   new(0.90f, 0.55f, 0.75f, 1f), new(0.55f, 0.30f, 0.60f, 1f), new(0.10f, 0.05f, 0.20f, 1f)),
        new("ember",  "Ember",    new(0.95f, 0.60f, 0.15f, 1f), new(0.65f, 0.15f, 0.15f, 1f), new(0.10f, 0.03f, 0.05f, 1f)),
        new("crystal","Crystal",  new(0.55f, 0.75f, 0.95f, 1f), new(0.50f, 0.40f, 0.80f, 1f), new(0.05f, 0.08f, 0.20f, 1f)),
    ];

    // server cap for row images is 512x128/400KiB · render exactly at the
    // cap, the gradient math is resolution-agnostic anyway.
    private const int Width = 512;
    private const int Height = 128;
    private static readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    public static byte[] Bytes(Preset preset)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(preset.Id, out var bytes))
            {
                bytes = Render(preset);
                _cache[preset.Id] = bytes;
            }
            return bytes;
        }
    }

    public static string Base64(Preset preset) => Convert.ToBase64String(Bytes(preset));

    /// <summary>Draw-list gradient approximation of a preset for picker swatches.</summary>
    public static void DrawSwatch(Vector2 min, Vector2 size, Preset preset, float radius)
    {
        var drawList = ImGui.GetWindowDrawList();
        var max = min + size;
        SpheneUI.Media.FillRoundedGradient(drawList, min, max,
            preset.From, preset.Via, preset.To, preset.Via, radius);
        drawList.AddRect(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.55f)),
            radius, ImDrawFlags.RoundCornersAll, MathF.Max(1f, SpheneUI.Scale));
    }

    /// <summary>Renders any preset (built-in or ad-hoc) to PNG - uncached.</summary>
    public static byte[] Render(Preset preset)
    {
        using var image = new Image<Rgba32>(Width, Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < Width; x++)
                {
                    var t = Math.Clamp(x / (float)Width * 0.8f + y / (float)Height * 0.2f, 0f, 1f);
                    var c = t < 0.5f
                        ? Lerp(preset.From, preset.Via, t * 2f)
                        : Lerp(preset.Via, preset.To, t * 2f - 1f);
                    var dx = x / (float)Width - 0.25f;
                    var dy = y / (float)Height - 0.30f;
                    var glow = MathF.Max(0f, 1f - (dx * dx + dy * dy) * 4f) * 0.18f;
                    c += new Vector4(glow, glow * 0.9f, glow * 0.6f, 0f);
                    row[x] = new Rgba32(c.X, c.Y, c.Z, 1f);
                }
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a + (b - a) * t;
}
