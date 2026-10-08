using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Image primitives of the design system: async texture cache (never
    /// blocks the UI thread), fit/cover/stretch placement, rounded corners,
    /// tinting, borders, avatars, placeholders and image backgrounds.
    /// Replaces all raw <c>ImGui.Image</c>/<c>AddImage</c> usage and the
    /// duplicated aspect-fit logic scattered across windows.
    /// </summary>
    public static class Media
    {
        private sealed class TextureEntry
        {
            public Task<IDalamudTextureWrap>? Task;
            public bool Failed;
        }

        private static readonly ConcurrentDictionary<string, TextureEntry> _textures = new(StringComparer.Ordinal);
        private static ITextureProvider? _provider;

        /// <summary>Wires the texture provider. Called once from UiSharedService init.</summary>
        internal static void SetTextureProvider(ITextureProvider provider) => _provider = provider;

        /// <summary>
        /// Cache-owned texture for <paramref name="data"/>, keyed by
        /// <paramref name="key"/>. Returns null while the texture is still
        /// decoding — render <see cref="Placeholder"/> meanwhile. The cache
        /// owns the wrap: callers must not dispose it. Call
        /// <see cref="Evict"/> when the underlying bytes change.
        /// </summary>
        public static IDalamudTextureWrap? Texture(string key, byte[] data)
            => Texture(key, () => data);

        /// <summary>
        /// Cache-owned texture resolved lazily: <paramref name="dataSource"/>
        /// only runs on a cache miss, so callers can pass the byte-producing
        /// code inline without per-frame cost.
        /// </summary>
        public static IDalamudTextureWrap? Texture(string key, Func<byte[]?> dataSource)
        {
            if (_textures.TryGetValue(key, out var entry))
            {
                return TryResolve(entry, key);
            }

            byte[]? data;
            try
            {
                data = dataSource();
            }
            catch (Exception ex)
            {
                LogError(ex, $"Media: failed to read image data for {key}");
                return null;
            }
            if (data == null || data.Length == 0 || _provider == null)
            {
                return null;
            }

            entry = new TextureEntry();
            _textures[key] = entry;
            try
            {
                entry.Task = _provider.CreateFromImageAsync(data);
            }
            catch (Exception ex)
            {
                entry.Failed = true;
                LogError(ex, $"Media: failed to start texture creation for {key}");
                return null;
            }
            return TryResolve(entry, key);
        }

        private static IDalamudTextureWrap? TryResolve(TextureEntry entry, string key)
        {
            var task = entry.Task;
            if (task == null || entry.Failed)
            {
                return null;
            }
            if (task.IsCompletedSuccessfully)
            {
                return task.Result;
            }
            if (task.IsFaulted)
            {
                entry.Failed = true;
                LogError(task.Exception ?? new Exception("texture decode failed"),
                    $"Media: texture decode failed for {key}");
            }
            return null;
        }

        /// <summary>Evicts a cached texture (disposing the wrap) so it reloads.</summary>
        public static void Evict(string key)
        {
            if (_textures.TryRemove(key, out var entry) && entry.Task is { IsCompletedSuccessfully: true } t)
            {
                t.Result.Dispose();
            }
        }

        /// <summary>
        /// Computes the destination rect and UV range for
        /// <paramref name="imageSize"/> inside the box defined by
        /// <paramref name="boxMin"/>/<paramref name="boxMax"/>.
        /// </summary>
        public static void FitRect(Vector2 boxMin, Vector2 boxMax, Vector2 imageSize, ImageFit fit,
            out Vector2 rectMin, out Vector2 rectMax, out Vector2 uv0, out Vector2 uv1)
        {
            uv0 = Vector2.Zero;
            uv1 = Vector2.One;
            var boxSize = boxMax - boxMin;
            if (imageSize.X <= 0f || imageSize.Y <= 0f || boxSize.X <= 0f || boxSize.Y <= 0f)
            {
                rectMin = boxMin;
                rectMax = boxMax;
                return;
            }

            if (fit == ImageFit.Stretch)
            {
                rectMin = boxMin;
                rectMax = boxMax;
                return;
            }

            var imageAspect = imageSize.X / imageSize.Y;
            var boxAspect = boxSize.X / boxSize.Y;

            if (fit == ImageFit.Fit)
            {
                // Letterbox: shrink the rect so the full image stays visible.
                var drawn = imageAspect > boxAspect
                    ? new Vector2(boxSize.X, boxSize.X / imageAspect)
                    : new Vector2(boxSize.Y * imageAspect, boxSize.Y);
                var offset = (boxSize - drawn) * 0.5f;
                rectMin = boxMin + offset;
                rectMax = rectMin + drawn;
                return;
            }

            // Cover: keep the box rect and crop the UVs on the overflowing axis.
            rectMin = boxMin;
            rectMax = boxMax;
            if (imageAspect > boxAspect)
            {
                var visible = boxAspect / imageAspect;
                var crop = (1f - visible) * 0.5f;
                uv0 = new Vector2(crop, 0f);
                uv1 = new Vector2(1f - crop, 1f);
            }
            else
            {
                var visible = imageAspect / boxAspect;
                var crop = (1f - visible) * 0.5f;
                uv0 = new Vector2(0f, crop);
                uv1 = new Vector2(1f, 1f - crop);
            }
        }

        /// <summary>
        /// Draws <paramref name="wrap"/> into the rect [min,max] with the given
        /// fit mode, optional tint, corner rounding and border.
        /// </summary>
        public static void ImageInRect(ImDrawListPtr drawList, IDalamudTextureWrap wrap,
            Vector2 min, Vector2 max, ImageFit fit = ImageFit.Fit, Vector4? tint = null,
            float rounding = 0f, bool border = false)
        {
            FitRect(min, max, new Vector2(wrap.Width, wrap.Height), fit,
                out var rectMin, out var rectMax, out var uv0, out var uv1);
            var tintU32 = tint.HasValue
                ? ColorUtils.ToU32(tint.Value)
                : ImGui.GetColorU32(Vector4.One);
            if (rounding > 0.5f)
            {
                drawList.AddImageRounded(wrap.Handle, rectMin, rectMax, uv0, uv1,
                    tintU32, rounding);
            }
            else
            {
                drawList.AddImage(wrap.Handle, rectMin, rectMax, uv0, uv1, tintU32);
            }
            if (border)
            {
                drawList.AddRect(rectMin, rectMax, ColorUtils.ToU32(Colors.Border with { W = 0.5f }),
                    rounding, ImDrawFlags.RoundCornersAll, MathF.Max(1f, SpheneUI.Scale));
            }
        }

        /// <summary>
        /// Inline image at the cursor, reserving <paramref name="size"/> in the
        /// layout. Renders <see cref="Placeholder"/> while
        /// <paramref name="wrap"/> is null (e.g. texture still decoding).
        /// </summary>
        public static void Image(IDalamudTextureWrap? wrap, Vector2 size, ImageFit fit = ImageFit.Fit,
            Vector4? tint = null, Space? rounding = null, bool border = false)
        {
            var pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(size);
            var max = pos + size;
            var r = rounding?.Scaled ?? Metrics.SwatchRadius(MathF.Min(size.X, size.Y));
            if (wrap == null)
            {
                PlaceholderInRect(ImGui.GetWindowDrawList(), pos, max, r, border);
                return;
            }
            ImageInRect(ImGui.GetWindowDrawList(), wrap, pos, max, fit, tint, r, border);
            Lint.CheckBounds("Media.Image");
        }

        /// <summary>
        /// Atlas/sprite image with explicit UVs (e.g. animated icon frames).
        /// </summary>
        public static void Sprite(ImTextureID handle, Vector2 size, Vector2 uv0, Vector2 uv1,
            Vector4? tint = null)
        {
            var pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(size);
            var tintU32 = tint.HasValue ? ColorUtils.ToU32(tint.Value) : uint.MaxValue;
            ImGui.GetWindowDrawList().AddImage(handle, pos, pos + size, uv0, uv1, tintU32);
            Lint.CheckBounds("Media.Sprite");
        }

        /// <summary>
        /// Square avatar image: cover-cropped, rounded, bordered, with a
        /// centered placeholder glyph while the texture is unavailable.
        /// </summary>
        public static void Avatar(IDalamudTextureWrap? wrap, float size, Vector4? tint = null,
            FontAwesomeIcon placeholderIcon = FontAwesomeIcon.User)
        {
            var pos = ImGui.GetCursorScreenPos();
            var box = new Vector2(size, size);
            ImGui.Dummy(box);
            var max = pos + box;
            var rounding = Metrics.SwatchRadius(size);
            var drawList = ImGui.GetWindowDrawList();
            if (wrap == null)
            {
                PlaceholderInRect(drawList, pos, max, rounding, border: true, icon: placeholderIcon);
                return;
            }
            ImageInRect(drawList, wrap, pos, max, ImageFit.Cover, tint, rounding, border: true);
            Lint.CheckBounds("Media.Avatar");
        }

        /// <summary>
        /// Themed placeholder block for a missing or still-loading image:
        /// muted fill, subtle border, centered image glyph.
        /// </summary>
        public static void Placeholder(Vector2 size, FontAwesomeIcon icon = FontAwesomeIcon.Image)
        {
            var pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(size);
            PlaceholderInRect(ImGui.GetWindowDrawList(), pos, pos + size,
                Metrics.SwatchRadius(MathF.Min(size.X, size.Y)), border: true, icon: icon);
        }

        /// <summary>Placeholder rendering into an explicit rect.</summary>
        public static void PlaceholderInRect(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float rounding = 0f, bool border = true, FontAwesomeIcon icon = FontAwesomeIcon.Image)
        {
            var fill = Colors.FrameBg with { W = 0.6f };
            drawList.AddRectFilled(min, max, ColorUtils.ToU32(fill), rounding);
            var glyph = icon.ToIconString();
            using (Icons.PushFont())
            {
                var glyphSize = ImGui.CalcTextSize(glyph);
                var center = min + (max - min) * 0.5f;
                drawList.AddText(center - glyphSize * 0.5f,
                    ColorUtils.ToU32(Colors.TextMuted with { W = 0.55f }), glyph);
            }
            if (border)
            {
                drawList.AddRect(min, max, ColorUtils.ToU32(Colors.Border with { W = 0.3f }),
                    rounding, ImDrawFlags.RoundCornersAll, MathF.Max(1f, SpheneUI.Scale));
            }
        }

        /// <summary>
        /// Image background behind <paramref name="content"/>: reserves
        /// <paramref name="size"/> (or the remaining content width with
        /// <paramref name="height"/>), paints the image on the backdrop channel
        /// with optional <paramref name="scrim"/> darkening for readability,
        /// then renders the content on top with <paramref name="padding"/>.
        /// </summary>
        public static void ImageBackground(IDalamudTextureWrap? wrap, Action content,
            float height, ImageFit fit = ImageFit.Cover, Vector4? tint = null,
            Vector4? scrim = null, Space? rounding = null, Space? padding = null)
        {
            var scale = SpheneUI.Scale;
            var pos = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var size = new Vector2(width, height);
            var max = pos + size;
            var r = rounding?.Scaled ?? 8f * scale;
            var pad = padding?.Scaled ?? Spacing.CardPadding.Scaled;

            var drawList = ImGui.GetWindowDrawList();
            drawList.ChannelsSplit(2);
            drawList.ChannelsSetCurrent(1);

            ImGui.SetCursorScreenPos(pos + new Vector2(pad, pad));
            using (ImRaii.Group())
            {
                content();
            }

            drawList.ChannelsSetCurrent(0);
            if (wrap != null)
            {
                if (r > 0.5f)
                {
                    drawList.PushClipRect(pos, max, true);
                }
                ImageInRect(drawList, wrap, pos, max, fit, tint);
                var scrimColor = scrim ?? new Vector4(0f, 0f, 0f, 0.35f);
                drawList.AddRectFilled(pos, max, ColorUtils.ToU32(scrimColor), r);
                if (r > 0.5f)
                {
                    drawList.PopClipRect();
                }
                drawList.AddRect(pos, max, ColorUtils.ToU32(Colors.Border with { W = 0.4f }),
                    r, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
            }
            else
            {
                PlaceholderInRect(drawList, pos, max, r, border: true);
            }
            drawList.ChannelsMerge();

            ImGui.SetCursorScreenPos(pos + new Vector2(0f, size.Y));
        }
    }
}
