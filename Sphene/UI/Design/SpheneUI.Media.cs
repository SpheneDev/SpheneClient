using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

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
            public long LastUsedTicks;
        }

        private static readonly ConcurrentDictionary<string, TextureEntry> _textures = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> _pendingEvict = new(StringComparer.Ordinal);
        private const long EvictDelayMs = 100;
        /// <summary>Soft cap on cached textures · beyond it the least-recently
        /// used entries ride the same deferred eviction as explicit
        /// <see cref="Evict"/> calls, so GPU memory stays bounded over a
        /// session without churning textures still on screen.</summary>
        private const int MaxCachedTextures = 256;
        private static ITextureProvider? _provider;

        /// <summary>Wires the texture provider. Called once from UiSharedService init.</summary>
        internal static void SetTextureProvider(ITextureProvider provider) => _provider = provider;

        /// <summary>
        /// Shared in-game UI icon (job icons, action icons, ...) resolved via
        /// <see cref="ITextureProvider.GetFromGameIcon"/> · Dalamud owns the
        /// cache, so this is safe to call every frame. Returns null until the
        /// wrap resolves; render a placeholder or leave the slot empty.
        /// </summary>
        public static IDalamudTextureWrap? GameIcon(uint iconId, bool hiRes = false)
        {
            if (_provider == null)
            {
                return null;
            }
            try
            {
                return _provider.TryGetFromGameIcon(new GameIconLookup(iconId, hiRes: hiRes), out var tex)
                    && tex.TryGetWrap(out var wrap, out _)
                    ? wrap
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Cache-owned texture for <paramref name="data"/>, keyed by
        /// <paramref name="key"/>. Returns null while the texture is still
        /// decoding · render <see cref="Placeholder"/> meanwhile. The cache
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
            FlushEvictions();
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

            entry = new TextureEntry { LastUsedTicks = Environment.TickCount64 };
            _textures[key] = entry;
            TrimCapacity();
            try
            {
                entry.Task = IsWebP(data)
                    ? TranscodeToPngAsync(data)
                    : _provider.CreateFromImageAsync(data);
            }
            catch (Exception ex)
            {
                entry.Failed = true;
                LogError(ex, $"Media: failed to start texture creation for {key}");
                return null;
            }
            return TryResolve(entry, key);
        }

        /// <summary>RIFF/WEBP magic check - the provider's stb decoder can't
        /// read WebP, but community uploads encode exactly that.</summary>
        private static bool IsWebP(byte[] data)
            => data.Length > 12
                && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
                && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';

        /// <summary>ImageSharp decodes WebP - re-encode to PNG once and let
        /// the provider decode that. Runs inside the cache-owned task so the
        /// transcode cost is paid once per key, never per frame.</summary>
        private static async Task<IDalamudTextureWrap> TranscodeToPngAsync(byte[] data)
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(data);
            using var stream = new MemoryStream();
            await image.SaveAsPngAsync(stream).ConfigureAwait(false);
            return await _provider!.CreateFromImageAsync(stream.ToArray()).ConfigureAwait(false);
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
                entry.LastUsedTicks = Environment.TickCount64;
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

        /// <summary>Drops the oldest entries past <see cref="MaxCachedTextures"/>
        /// via the deferred-eviction path - queued wraps are disposed by
        /// <see cref="FlushEvictions"/> after the delay, never mid-draw.</summary>
        private static void TrimCapacity()
        {
            var excess = _textures.Count - MaxCachedTextures;
            if (excess <= 0)
            {
                return;
            }
            foreach (var kv in _textures.OrderBy(kv => kv.Value.LastUsedTicks).Take(excess))
            {
                _pendingEvict[kv.Key] = Environment.TickCount64;
            }
        }

        /// <summary>
        /// Evicts a cached texture so it reloads. Disposal is deferred by a
        /// short delay: a wrap already recorded into the current frame's draw
        /// lists must stay alive until the renderer consumed it - disposing
        /// it mid-frame leaves a dangling D3D texture and crashes the game.
        /// Timestamp-based (not <c>ImGui.GetFrameCount</c>) because callers may
        /// evict off the UI thread, where touching the ImGui context crashes.
        /// </summary>
        public static void Evict(string key)
        {
            _pendingEvict[key] = Environment.TickCount64;
        }

        /// <summary>Disposes wraps whose eviction delay elapsed · called
        /// lazily from the texture entry points on the UI thread.</summary>
        private static void FlushEvictions()
        {
            if (_pendingEvict.IsEmpty)
            {
                return;
            }
            var now = Environment.TickCount64;
            foreach (var kv in _pendingEvict)
            {
                if (now - kv.Value < EvictDelayMs || !_pendingEvict.TryRemove(kv.Key, out _))
                {
                    continue;
                }
                if (_textures.TryRemove(kv.Key, out var entry)
                    && entry.Task is { IsCompletedSuccessfully: true } t)
                {
                    t.Result.Dispose();
                }
                if (_animated.TryRemove(kv.Key, out var anim)
                    && anim is { IsCompletedSuccessfully: true } at)
                {
                    at.Result?.Dispose();
                }
            }
        }

        /// <summary>
        /// Animated image stored as a horizontal frame atlas in a single
        /// texture wrap. <see cref="FrameIndexAt"/> picks the frame for a
        /// timestamp; use with <see cref="AnimatedInRect"/> which maps the
        /// atlas UVs automatically.
        /// </summary>
        public sealed class AnimatedImage : IDisposable
        {
            public required IDalamudTextureWrap Atlas { get; init; }
            public required int FrameCount { get; init; }
            public required int FrameWidth { get; init; }
            public required int FrameHeight { get; init; }
            /// <summary>Atlas grid layout - frames tile column-major inside a
            /// texture that must stay under the GPU's max-dimension bound.</summary>
            public required int AtlasColumns { get; init; }
            public required int AtlasRows { get; init; }
            public required IReadOnlyList<float> Durations { get; init; }
            public float TotalDuration { get; init; }

            /// <summary>Whether more than one frame exists (false = static image).</summary>
            public bool IsAnimated => FrameCount > 1;

            /// <summary>Frame index for <paramref name="time"/> seconds, looping.</summary>
            public int FrameIndexAt(double time)
            {
                if (!IsAnimated || TotalDuration <= 0f)
                {
                    return 0;
                }
                var t = time % TotalDuration;
                var acc = 0f;
                for (var i = 0; i < Durations.Count; i++)
                {
                    acc += Durations[i];
                    if (t < acc)
                    {
                        return i;
                    }
                }
                return 0;
            }

            public void Dispose() => Atlas.Dispose();
        }

        private static readonly ConcurrentDictionary<string, Task<AnimatedImage?>> _animated
            = new(StringComparer.Ordinal);
        /// <summary>Animated decode (ImageSharp + atlas composite + PNG
        /// encode + texture upload) saturates a whole core per job - a full
        /// picker grid decodes in waves instead of hitching the render
        /// thread's neighbors.</summary>
        private static readonly SemaphoreSlim _decodeSlots = new(2, 2);
        private const int MaxAnimatedFrames = 48;
        private const int MaxAnimatedDimension = 512;
        /// <summary>D3D11 hard texture bound - a horizontal frame strip of
        /// 48 × ~400px frames overflows it and the upload throws, so the
        /// atlas tiles frames into a grid that stays inside both axes.</summary>
        private const int MaxAtlasDimension = 16384;
        private const int GradientCornerSegments = 8;

        /// <summary>
        /// Fills the rounded rect [min,max] with a four-corner bilinear
        /// gradient by emitting a vertex-colored triangle fan (plus a 1px
        /// alpha fringe for smooth edges). Unlike the 2×2 texture +
        /// <c>AddImageRounded</c> path this bakes the colors straight into the
        /// vertices, so the blend always spans the rect exactly and cannot be
        /// offset by texture sampling/UV shading quirks.
        /// </summary>
        public static void FillRoundedGradient(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 topLeft, Vector4 topRight, Vector4 bottomRight, Vector4 bottomLeft,
            float rounding, ImDrawFlags flags = ImDrawFlags.RoundCornersAll)
        {
            var size = max - min;
            if (size.X <= 0f || size.Y <= 0f)
            {
                return;
            }
            var r = MathF.Min(rounding, MathF.Min(size.X, size.Y) * 0.5f);
            var uvWhite = ImGui.GetFontTexUvWhitePixel();

            const int ringCount = GradientCornerSegments * 4;
            Span<Vector2> ring = stackalloc Vector2[ringCount];
            var n = 0;
            n = AppendGradientArc(ring, n, min, new Vector2(r, r), MathF.PI, r,
                flags.HasFlag(ImDrawFlags.RoundCornersTopLeft));
            n = AppendGradientArc(ring, n, new Vector2(max.X, min.Y), new Vector2(-r, r),
                -MathF.PI * 0.5f, r, flags.HasFlag(ImDrawFlags.RoundCornersTopRight));
            n = AppendGradientArc(ring, n, max, new Vector2(-r, -r), 0f, r,
                flags.HasFlag(ImDrawFlags.RoundCornersBottomRight));
            AppendGradientArc(ring, n, new Vector2(min.X, max.Y), new Vector2(r, -r),
                MathF.PI * 0.5f, r, flags.HasFlag(ImDrawFlags.RoundCornersBottomLeft));

            var center = (min + max) * 0.5f;
            var invW = 1f / size.X;
            var invH = 1f / size.Y;
            uint Shade(Vector2 p)
            {
                var tx = Math.Clamp((p.X - min.X) * invW, 0f, 1f);
                var ty = Math.Clamp((p.Y - min.Y) * invH, 0f, 1f);
                return ColorUtils.ToU32(Vector4.Lerp(
                    Vector4.Lerp(topLeft, topRight, tx),
                    Vector4.Lerp(bottomLeft, bottomRight, tx), ty));
            }

            drawList.PrimReserve(ringCount * 9, 1 + ringCount * 2);
            var baseVtx = (int)drawList.VtxCurrentIdx;
            drawList.PrimWriteVtx(center, uvWhite, Shade(center));
            for (var k = 0; k < ringCount; k++)
            {
                var p = ring[k];
                var dir = p - center;
                var len = dir.Length();
                var outer = len > 0.001f ? p + dir * (1f / len) : p;
                var col = Shade(p);
                drawList.PrimWriteVtx(p, uvWhite, col);
                drawList.PrimWriteVtx(outer, uvWhite, col & 0x00FFFFFFu);
            }
            for (var k = 0; k < ringCount; k++)
            {
                var inner1 = (ushort)(baseVtx + 1 + k * 2);
                var inner2 = (ushort)(baseVtx + 1 + ((k + 1) % ringCount) * 2);
                var outer1 = (ushort)(inner1 + 1);
                var outer2 = (ushort)(inner2 + 1);
                drawList.PrimWriteIdx((ushort)baseVtx);
                drawList.PrimWriteIdx(inner1);
                drawList.PrimWriteIdx(inner2);
                drawList.PrimWriteIdx(inner1);
                drawList.PrimWriteIdx(outer1);
                drawList.PrimWriteIdx(inner2);
                drawList.PrimWriteIdx(inner2);
                drawList.PrimWriteIdx(outer1);
                drawList.PrimWriteIdx(outer2);
            }
        }

        private static int AppendGradientArc(Span<Vector2> ring, int i, Vector2 corner,
            Vector2 centerOffset, float startAngle, float r, bool rounded)
        {
            for (var s = 0; s < GradientCornerSegments; s++)
            {
                if (!rounded)
                {
                    ring[i++] = corner;
                    continue;
                }
                var a = startAngle + MathF.PI * 0.5f * (s / (float)GradientCornerSegments);
                ring[i++] = corner + centerOffset
                    + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            }
            return i;
        }

        /// <summary>
        /// Cache-owned animated texture for <paramref name="data"/> (GIF/WebP,
        /// static images decode to a single frame). Frames are composited into
        /// a horizontal atlas off the UI thread; returns null while decoding.
        /// The cache owns the atlas · callers must not dispose it.
        /// </summary>
        public static AnimatedImage? Animated(string key, byte[] data)
        {
            FlushEvictions();
            if (_animated.TryGetValue(key, out var task))
            {
                return task is { IsCompletedSuccessfully: true } ? task.Result : null;
            }
            if (_provider == null || data.Length == 0)
            {
                return null;
            }
            var load = Task.Run(() => DecodeAnimatedAsync(data));
            _animated[key] = load;
            return null;
        }

        /// <summary>
        /// Draws the current animation frame into the rect [min,max] with the
        /// given fit mode, tint and corner rounding. <paramref name="time"/>
        /// defaults to <see cref="ImGui.GetTime"/>.
        /// </summary>
        public static void AnimatedInRect(ImDrawListPtr drawList, AnimatedImage anim,
            Vector2 min, Vector2 max, ImageFit fit = ImageFit.Cover, Vector4? tint = null,
            float rounding = 0f, double? time = null,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            var idx = anim.FrameIndexAt(time ?? ImGui.GetTime());
            FitRect(min, max, new Vector2(anim.FrameWidth, anim.FrameHeight), fit,
                out var rectMin, out var rectMax, out var uv0, out var uv1);
            var col = idx % anim.AtlasColumns;
            var row = idx / anim.AtlasColumns;
            var cellU = 1f / anim.AtlasColumns;
            var cellV = 1f / anim.AtlasRows;
            var au0 = new Vector2((col + uv0.X) * cellU, (row + uv0.Y) * cellV);
            var au1 = new Vector2((col + uv1.X) * cellU, (row + uv1.Y) * cellV);
            var tintU32 = tint.HasValue ? ColorUtils.ToU32(tint.Value) : ImGui.GetColorU32(Vector4.One);
            if (rounding > 0.5f)
            {
                drawList.AddImageRounded(anim.Atlas.Handle, rectMin, rectMax, au0, au1,
                    tintU32, rounding, corners);
            }
            else
            {
                drawList.AddImage(anim.Atlas.Handle, rectMin, rectMax, au0, au1, tintU32);
            }
        }

        /// <summary>Composites all frames (GIF disposal applied by the decoder) into one atlas.</summary>
        private static async Task<AnimatedImage?> DecodeAnimatedAsync(byte[] data)
        {
            await _decodeSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                // animated WebP (KLIPY stickers) declares a canvas background
                // color in its ANIM chunk - often opaque white. The default
                // handling paints every frame onto it, so deltas render as a
                // moving white box; Ignore starts each canvas transparent.
                using var image = IsWebp(data)
                    ? await SixLabors.ImageSharp.Formats.Webp.WebpDecoder.Instance
                        .DecodeAsync<Rgba32>(
                            new SixLabors.ImageSharp.Formats.Webp.WebpDecoderOptions
                            {
                                BackgroundColorHandling = SixLabors.ImageSharp.Formats
                                    .Webp.BackgroundColorHandling.Ignore,
                            },
                            new MemoryStream(data, writable: false))
                        .ConfigureAwait(false)
                    : SixLabors.ImageSharp.Image.Load<Rgba32>(data);
                var frameWidth = image.Width;
                var frameHeight = image.Height;
                if (frameWidth <= 0 || frameHeight <= 0
                    || frameWidth > MaxAnimatedDimension || frameHeight > MaxAnimatedDimension)
                {
                    return null;
                }

                var frameCount = Math.Clamp(image.Frames.Count, 1, MaxAnimatedFrames);
                var durations = new float[frameCount];
                // a horizontal strip overflows the 16384px texture bound for
                // wide media x many frames - tile into a grid instead
                var columns = Math.Min(frameCount,
                    Math.Max(1, MaxAtlasDimension / frameWidth));
                var rows = (frameCount + columns - 1) / columns;
                using var atlas = new Image<Rgba32>(columns * frameWidth,
                    rows * frameHeight);
                var framePixels = new Rgba32[frameWidth * frameHeight];
                for (var i = 0; i < frameCount; i++)
                {
                    var frame = image.Frames[i];
                    durations[i] = FrameDelaySeconds(frame);
                    frame.CopyPixelDataTo(framePixels);
                    var xOffset = (i % columns) * frameWidth;
                    var yOffset = (i / columns) * frameHeight;
                    atlas.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < frameHeight; y++)
                        {
                            framePixels.AsSpan(y * frameWidth, frameWidth)
                                .CopyTo(accessor.GetRowSpan(yOffset + y)
                                    .Slice(xOffset, frameWidth));
                        }
                    });
                }

                using var stream = new MemoryStream();
                await atlas.SaveAsPngAsync(stream).ConfigureAwait(false);
                var wrap = await _provider!.CreateFromImageAsync(stream.ToArray()).ConfigureAwait(false);
                var total = 0f;
                foreach (var d in durations) total += d;
                return new AnimatedImage
                {
                    Atlas = wrap,
                    FrameCount = frameCount,
                    FrameWidth = frameWidth,
                    FrameHeight = frameHeight,
                    AtlasColumns = columns,
                    AtlasRows = rows,
                    Durations = durations,
                    TotalDuration = total,
                };
            }
            catch (Exception ex)
            {
                LogError(ex, "Media: animated image decode failed");
                return null;
            }
            finally
            {
                _decodeSlots.Release();
            }
        }

        /// <summary>RIFF+WEBP magic check - decides whether the
        /// background-ignoring WebP decoder path applies.</summary>
        private static bool IsWebp(byte[] d)
            => d.Length >= 12
               && d[0] == (byte)'R' && d[1] == (byte)'I'
               && d[2] == (byte)'F' && d[3] == (byte)'F'
               && d[8] == (byte)'W' && d[9] == (byte)'E'
               && d[10] == (byte)'B' && d[11] == (byte)'P';

        private static float FrameDelaySeconds(ImageFrame<Rgba32> frame)
        {
            var gif = frame.Metadata.GetGifMetadata();
            if (gif != null && gif.FrameDelay > 0)
            {
                return MathF.Max(0.02f, gif.FrameDelay / 100f);
            }
            var webp = frame.Metadata.GetWebpMetadata();
            if (webp != null && webp.FrameDelay > 0)
            {
                return MathF.Max(0.02f, webp.FrameDelay / 1000f);
            }
            return 0.1f;
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
        /// <paramref name="corners"/> selects which corners
        /// <paramref name="rounding"/> applies to. <paramref name="zoom"/>
        /// scales the sampled texture region toward its center · the output
        /// rect and rounding stay fixed, so hover zooms never touch layout.
        /// </summary>
        public static void ImageInRect(ImDrawListPtr drawList, IDalamudTextureWrap wrap,
            Vector2 min, Vector2 max, ImageFit fit = ImageFit.Fit, Vector4? tint = null,
            float rounding = 0f, bool border = false,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll, float zoom = 1f)
        {
            FitRect(min, max, new Vector2(wrap.Width, wrap.Height), fit,
                out var rectMin, out var rectMax, out var uv0, out var uv1);
            if (zoom > 1.0001f)
            {
                var k = 1f - 1f / zoom;
                var mid = (uv0 + uv1) * 0.5f;
                uv0 = Vector2.Lerp(uv0, mid, k);
                uv1 = Vector2.Lerp(uv1, mid, k);
            }
            var tintU32 = tint.HasValue
                ? ColorUtils.ToU32(tint.Value)
                : ImGui.GetColorU32(Vector4.One);
            if (rounding > 0.5f)
            {
                drawList.AddImageRounded(wrap.Handle, rectMin, rectMax, uv0, uv1,
                    tintU32, rounding, corners);
            }
            else
            {
                drawList.AddImage(wrap.Handle, rectMin, rectMax, uv0, uv1, tintU32);
            }
            if (border)
            {
                drawList.AddRect(rectMin, rectMax, ColorUtils.ToU32(Colors.Border with { W = 0.5f }),
                    rounding, corners, MathF.Max(1f, SpheneUI.Scale));
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
        /// Atlas/sprite image with explicit UVs drawn into a caller-owned rect  · 
        /// the drawList counterpart of <see cref="Sprite"/> for manual layouts
        /// (previews, composed rows) that position content themselves.
        /// </summary>
        public static void SpriteInRect(ImDrawListPtr drawList, ImTextureID handle,
            Vector2 min, Vector2 max, Vector2 uv0, Vector2 uv1, Vector4? tint = null)
        {
            var tintU32 = tint.HasValue ? ColorUtils.ToU32(tint.Value) : uint.MaxValue;
            drawList.AddImage(handle, min, max, uv0, uv1, tintU32);
        }

        /// <summary>
        /// Rounded-corner variant of <see cref="ImageInRect"/> for tile
        /// cells where a logo must conform to the cell shape instead of
        /// bleeding past the rounding.
        /// </summary>
        public static void ImageRoundedInRect(ImDrawListPtr drawList, IDalamudTextureWrap wrap,
            Vector2 min, Vector2 max, float rounding, Vector4? tint = null)
        {
            var tintU32 = tint.HasValue ? ColorUtils.ToU32(tint.Value) : uint.MaxValue;
            drawList.AddImageRounded(wrap.Handle, min, max, Vector2.Zero, Vector2.One,
                tintU32, rounding);
        }

        /// <summary>
        /// Texture drawn as a quad rotated <paramref name="radians"/> around
        /// the rect's center - the icon-tilt for surfaces that grow out of a
        /// brand mark (e.g. the icon menu). Angle 0 renders a regular rect.
        /// </summary>
        public static void ImageQuadInRect(ImDrawListPtr drawList, IDalamudTextureWrap wrap,
            Vector2 min, Vector2 max, float radians, Vector4? tint = null)
        {
            var c = (min + max) * 0.5f;
            var cos = MathF.Cos(radians);
            var sin = MathF.Sin(radians);
            var hw = (max.X - min.X) * 0.5f;
            var hh = (max.Y - min.Y) * 0.5f;
            Vector2 Rot(float x, float y)
                => new(c.X + x * cos - y * sin, c.Y + x * sin + y * cos);
            var tintU32 = tint.HasValue ? ColorUtils.ToU32(tint.Value) : uint.MaxValue;
            drawList.AddImageQuad(wrap.Handle,
                Rot(-hw, -hh), Rot(hw, -hh), Rot(hw, hh), Rot(-hw, hh),
                Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, tintU32);
        }

        /// <summary>
        /// Circular avatar image: cover-cropped, bordered, with a centered
        /// placeholder glyph while the texture is unavailable. Avatars are
        /// always circles so decorations (presence dots, rings) can rely on
        /// fixed edge positions.
        /// </summary>
        public static void Avatar(IDalamudTextureWrap? wrap, float size, Vector4? tint = null,
            FontAwesomeIcon placeholderIcon = FontAwesomeIcon.User)
        {
            var pos = ImGui.GetCursorScreenPos();
            var box = new Vector2(size, size);
            ImGui.Dummy(box);
            var max = pos + box;
            var rounding = size * 0.5f;
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
        /// Discord-style presence badge on a circular avatar's lower-right
        /// edge: dark backdrop ring plus a colored core so it reads over any
        /// avatar art. Fixed 45° position · only valid for circle avatars.
        /// </summary>
        public static void PresenceDot(ImDrawListPtr drawList, Vector2 avatarPos,
            float avatarSize, Vector4 color)
        {
            var dotR = MathF.Max(4.5f * Scale, avatarSize * 0.14f);
            var center = avatarPos + new Vector2(avatarSize * 0.8536f);
            drawList.AddCircleFilled(center, dotR + 2f * Scale,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.Zero, 0.75f)));
            drawList.AddCircleFilled(center, dotR, ColorUtils.ToU32(color));
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
            if (Layout.InChannelSplit)
            {
                Layout.ChannelSplitFallback(nameof(ImageBackground), "imagebg", content);
                return;
            }

            var scale = SpheneUI.Scale;
            var pos = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var size = new Vector2(width, height);
            var max = pos + size;
            var r = rounding?.Scaled ?? 8f * scale;
            var pad = padding?.Scaled ?? Spacing.CardPadding.Scaled;

            var drawList = ImGui.GetWindowDrawList();
            using var splitScope = new Layout.ChannelSplitGuard(drawList);
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

            ImGui.SetCursorScreenPos(pos + new Vector2(0f, size.Y));
        }
    }
}
