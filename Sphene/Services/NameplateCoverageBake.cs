using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;
using System.Runtime.CompilerServices;
using TerraFX.Interop.DirectX;
using GameDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Sphene.Services;

/// <summary>
/// CPU-side re-render of the reconstructed glyph masks into one shared R8
/// coverage texture. Every emitted textured mask contributes its real source
/// bitmap (game font atlas, gfd icons, ImGui atlas - read back once and
/// cached), so the shader samples a single baked image per plate instead of
/// per-glyph quads. What the bake cannot fix is pen placement - it only makes
/// coverage generation exact.
/// </summary>
internal sealed unsafe class NameplateCoverageBake : IDisposable
{
    public const int SlotWidth = 512;
    public const int SlotHeight = 72;
    private const int SlotCols = 8;
    public const int MaxSlots = 64;
    private const int TexWidth = SlotWidth * SlotCols;
    private const int TexHeight = SlotHeight * (MaxSlots / SlotCols);

    private readonly ILogger _logger;
    private readonly byte[] _pixels = new byte[TexWidth * TexHeight];
    private readonly byte[] _scratch = new byte[SlotWidth * SlotHeight];
    private readonly Dictionary<ulong, Page?> _pages = new();
    private readonly Dictionary<ulong, int> _pageAttempts = new();
    private readonly Dictionary<ulong, long> _pageRetryAt = new();
    private readonly Dictionary<ulong, (long First, long Last)> _pending = new();
    private readonly Dictionary<ulong, long> _settleAt = new();
    private readonly Dictionary<ulong, long> _pagePinAt = new();
    private readonly HashSet<ulong> _diskMisses = new();
    private readonly bool[] _dirty = new bool[MaxSlots];
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Texture2D* _texture;
    private ID3D11ShaderResourceView* _srv;
    private bool _failed;
    private string? _failReason;
    private bool _disposed;

    public NameplateCoverageBake(ILogger logger)
    {
        _logger = logger;
    }

    internal sealed class Page
    {
        public required byte[] Data;
        public required int Width;
        public required int Height;
        public required int Bpp;
        /// <summary>Byte offset inside a pixel for glyph channel 0-3 - the
        /// game atlases pack four glyph planes into BGRA.</summary>
        public required int[] ChannelOffset;
        /// <summary>False until a second live readback replays identical
        /// pixels - a texture captured mid-upload can hold partial alpha,
        /// and a page cached that early would silently drop masks in the
        /// regions still streaming. Unproven pages cover conservatively.</summary>
        public bool Settled;
    }

    private readonly HashSet<nint> _dumped = new();

    /// <summary>Where PNG dumps go - set by the caller (plugin config dir in
    /// dev builds); temp fallback when unset.</summary>
    public string? DumpDirectory { get; set; }

    private string DumpDir()
        => DumpDirectory ?? Path.Combine(Path.GetTempPath(), "sphene-nameplate-icons");

    /// <summary>Where alpha pages persist between sessions - set to a folder
    /// inside the plugin config directory by the caller.</summary>
    public string? CacheDirectory { get; set; }

    private string CacheDir()
        => CacheDirectory ?? Path.Combine(Path.GetTempPath(), "sphene-nameplate-pages");

    /// <summary>Writes a resolved mask texture to a PNG in temp - lets us
    /// eyeball which icon each sampled region really contains.</summary>
    public void DumpPage(nint srv, uint contentKey = 0)
    {
        if (!_dumped.Add(srv))
            return;
        var page = GetPage(srv, contentKey);
        if (page == null)
            return;
        try
        {
            var dir = DumpDir();
            Directory.CreateDirectory(dir);
            var rgba = new byte[page.Width * page.Height * 4];
            for (var i = 0; i < page.Width * page.Height; i++)
            {
                var src = i * page.Bpp;
                var dst = i * 4;
                rgba[dst] = page.ChannelOffset[0] >= 0 ? page.Data[src + page.ChannelOffset[0]] : (byte)255;
                rgba[dst + 1] = page.ChannelOffset[1] >= 0 ? page.Data[src + page.ChannelOffset[1]] : (byte)255;
                rgba[dst + 2] = page.ChannelOffset[2] >= 0 ? page.Data[src + page.ChannelOffset[2]] : (byte)255;
                rgba[dst + 3] = page.ChannelOffset[3] >= 0 ? page.Data[src + page.ChannelOffset[3]] : (byte)255;
            }
            var path = Path.Combine(dir, $"ic-{srv:X}-{page.Width}x{page.Height}.png");
            using var img = Image.LoadPixelData<Rgba32>(rgba, page.Width, page.Height);
            img.SaveAsPng(path);
            _logger.LogDebug("[Nameplate] icon texture dump {path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] icon texture dump failed");
        }
    }

    private readonly HashSet<string> _dumpedRegions = new(StringComparer.Ordinal);

    /// <summary>Writes the exact normalized-uv region a mask quad samples to a
    /// PNG - for gfd icons those are the precise pixels the cutout taps,
    /// including any soft alpha edge or shine baked into the sheet. Returns
    /// true only when a file was actually written - callers can tie a
    /// once-per-dump log line to it.</summary>
    public bool DumpRegion(nint srv, Vector4 uv, string name, uint contentKey = 0)
    {
        if (!_dumpedRegions.Add(name))
            return false;
        var page = GetPage(srv, contentKey);
        if (page == null)
            return false;
        try
        {
            var dir = DumpDir();
            Directory.CreateDirectory(dir);
            var ix0 = Math.Clamp((int)MathF.Floor(MathF.Min(uv.X, uv.Z) * page.Width), 0, page.Width - 1);
            var ix1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(uv.X, uv.Z) * page.Width), ix0 + 1, page.Width);
            var iy0 = Math.Clamp((int)MathF.Floor(MathF.Min(uv.Y, uv.W) * page.Height), 0, page.Height - 1);
            var iy1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(uv.Y, uv.W) * page.Height), iy0 + 1, page.Height);
            var w = ix1 - ix0;
            var h = iy1 - iy0;
            var rgba = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var src = ((iy0 + y) * page.Width + ix0 + x) * page.Bpp;
                    var dst = (y * w + x) * 4;
                    rgba[dst] = page.ChannelOffset[0] >= 0 ? page.Data[src + page.ChannelOffset[0]] : (byte)255;
                    rgba[dst + 1] = page.ChannelOffset[1] >= 0 ? page.Data[src + page.ChannelOffset[1]] : (byte)255;
                    rgba[dst + 2] = page.ChannelOffset[2] >= 0 ? page.Data[src + page.ChannelOffset[2]] : (byte)255;
                    rgba[dst + 3] = page.ChannelOffset[3] >= 0 ? page.Data[src + page.ChannelOffset[3]] : (byte)255;
                }
            }
            var path = Path.Combine(dir, $"{name}-{w}x{h}.png");
            using var img = Image.LoadPixelData<Rgba32>(rgba, w, h);
            img.SaveAsPng(path);
            _logger.LogDebug("[Nameplate] icon region dump {path}", path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] icon region dump failed");
            return false;
        }
    }

    /// <summary>Alpha below this counts as empty - placeholder icons render
    /// content at a few alpha steps that never reaches the screen.</summary>
    public const byte CoverageThreshold = 16;

    /// <summary>True when the sampled texel range holds visible alpha. Nodes
    /// that stay visible but draw (near-)transparent content (status icon
    /// placeholders) would otherwise emit a pointless mask and can even steal
    /// one of the two bound texture slots, degrading real masks to rects.</summary>
    public static bool HasCoverage(Page page, float x0, float y0, float x1, float y1)
        => AlphaStats(page, x0, y0, x1, y1, out var max, out _) && max >= CoverageThreshold;

    /// <summary>Max alpha and covered-texel count over the sampled range -
    /// distinguishes a real icon from placeholder noise without logging
    /// the mask's verdict alone.</summary>
    public static bool AlphaStats(Page page, float x0, float y0, float x1, float y1,
        out byte max, out int covered)
        => AlphaStats(page, x0, y0, x1, y1, out max, out covered, out _);

    /// <summary>Same scan plus an alpha sum - the mean decides whether a
    /// region is a translucent sheen overlay or real opaque window chrome.
    /// </summary>
    public static bool AlphaStats(Page page, float x0, float y0, float x1, float y1,
        out byte max, out int covered, out uint alphaSum)
    {
        max = 0;
        covered = 0;
        alphaSum = 0;
        var ix0 = Math.Clamp((int)MathF.Floor(MathF.Min(x0, x1)), 0, page.Width - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(x0, x1)), 0, page.Width);
        var iy0 = Math.Clamp((int)MathF.Floor(MathF.Min(y0, y1)), 0, page.Height - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(y0, y1)), 0, page.Height);
        var off = page.ChannelOffset[3];
        if (off < 0)
        {
            // X8 formats carry no alpha plane - every texel counts as solid
            max = 255;
            covered = (ix1 - ix0) * (iy1 - iy0);
            alphaSum = (uint)(covered * 255);
            return true;
        }
        for (var y = iy0; y < iy1; y++)
        {
            var row = y * page.Width * page.Bpp + off;
            for (var x = ix0; x < ix1; x++)
            {
                var a = page.Data[row + x * page.Bpp];
                if (a > max)
                    max = a;
                alphaSum += a;
                if (a >= CoverageThreshold)
                    covered++;
            }
        }
        return true;
    }

    /// <summary>Bitmask of the region's sides that ramp in from (near-)zero
    /// - 1 left, 2 right, 4 top, 8 bottom, or 0 for none/all-four. A window
    /// edge band ramps on its outer side, a standalone edge strip on both;
    /// a whole-window part ramps on all four and must keep proportional
    /// cutting. rimTexels gets the widest silhouette offset - how far in
    /// from the ramping edge the alpha reaches its plateau.</summary>
    public static int OuterEdgeSide(Page page, float x0, float y0, float x1, float y1,
        int aMax, out int rimTexels)
    {
        rimTexels = 0;
        var off = page.ChannelOffset[3];
        if (off < 0 || aMax <= 0)
            return 0;
        var ix0 = Math.Clamp((int)MathF.Floor(MathF.Min(x0, x1)), 0, page.Width - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(x0, x1)), 0, page.Width);
        var iy0 = Math.Clamp((int)MathF.Floor(MathF.Min(y0, y1)), 0, page.Height - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(y0, y1)), 0, page.Height);
        var w = ix1 - ix0;
        var h = iy1 - iy0;
        if (w < 4 || h < 4)
            return 0;
        var half = aMax / 2;
        var left = 0;
        var right = 0;
        for (var y = iy0; y < iy1; y++)
        {
            var row = y * page.Width * page.Bpp + off;
            left += page.Data[row + ix0 * page.Bpp]
                + page.Data[row + (ix0 + 1) * page.Bpp];
            right += page.Data[row + (ix1 - 1) * page.Bpp]
                + page.Data[row + (ix1 - 2) * page.Bpp];
        }
        var top = 0;
        var bottom = 0;
        for (var x = ix0; x < ix1; x++)
        {
            top += page.Data[(iy0 * page.Width + x) * page.Bpp + off]
                + page.Data[((iy0 + 1) * page.Width + x) * page.Bpp + off];
            bottom += page.Data[((iy1 - 1) * page.Width + x) * page.Bpp + off]
                + page.Data[((iy1 - 2) * page.Width + x) * page.Bpp + off];
        }
        var sideMask = 0;
        if (left < half * 2 * h) sideMask |= 1;
        if (right < half * 2 * h) sideMask |= 2;
        if (top < half * 2 * w) sideMask |= 4;
        if (bottom < half * 2 * w) sideMask |= 8;
        // 1-3 ramping sides mark a real edge band (frame columns ramp on
        // the outer edge, standalone strips on both); four sides means a
        // whole-window part - flooring that would opaque the body
        if (sideMask == 0 || sideMask == 15)
            return 0;
        // silhouette offset per ramping side: first column/row whose mean
        // reaches ~80% of the region max - the visible frame starts there,
        // and the shared rim takes the widest side so every band's floor
        // lands past its own ramp
        var rimMax = (int)(aMax * 4L / 5);
        if ((sideMask & 1) != 0)
            rimTexels = Math.Max(rimTexels, RimWalk(page, ix0, ix1, iy0, iy1, off, 1, rimMax));
        if ((sideMask & 2) != 0)
            rimTexels = Math.Max(rimTexels, RimWalk(page, ix0, ix1, iy0, iy1, off, 2, rimMax));
        if ((sideMask & 4) != 0)
            rimTexels = Math.Max(rimTexels, RimWalk(page, ix0, ix1, iy0, iy1, off, 4, rimMax));
        if ((sideMask & 8) != 0)
            rimTexels = Math.Max(rimTexels, RimWalk(page, ix0, ix1, iy0, iy1, off, 8, rimMax));
        return sideMask;
    }

    private static int RimWalk(Page page, int ix0, int ix1, int iy0, int iy1,
        int off, int side, int rimMax)
    {
        var w = ix1 - ix0;
        var h = iy1 - iy0;
        var span = side <= 2 ? w : h;
        for (var t = 0; t < span; t++)
        {
            var sum = 0;
            if (side <= 2)
            {
                var x = side == 1 ? ix0 + t : ix1 - 1 - t;
                for (var y = iy0; y < iy1; y++)
                    sum += page.Data[(y * page.Width + x) * page.Bpp + off];
                if (sum >= rimMax * h)
                    return t;
            }
            else
            {
                var y = side == 4 ? iy0 + t : iy1 - 1 - t;
                for (var x = ix0; x < ix1; x++)
                    sum += page.Data[(y * page.Width + x) * page.Bpp + off];
                if (sum >= rimMax * w)
                    return t;
            }
        }
        return span;
    }

    private const int RegionStatsMax = 512;
    private readonly Dictionary<StatsKey, RegionStats> _regionStats = new();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly struct StatsKey : IEquatable<StatsKey>
    {
        private readonly int _pageId;
        private readonly int _x0;
        private readonly int _y0;
        private readonly int _x1;
        private readonly int _y1;

        public StatsKey(Page page, float x0, float y0, float x1, float y1)
        {
            // object identity ties every entry to this page instance - a
            // confirm read that swaps the pixels (mid-upload snapshot, stale
            // disk data) orphans the old stats without needing invalidation
            _pageId = RuntimeHelpers.GetHashCode(page);
            _x0 = BitConverter.SingleToInt32Bits(x0);
            _y0 = BitConverter.SingleToInt32Bits(y0);
            _x1 = BitConverter.SingleToInt32Bits(x1);
            _y1 = BitConverter.SingleToInt32Bits(y1);
        }

        public bool Equals(StatsKey other)
            => _pageId == other._pageId && _x0 == other._x0 && _y0 == other._y0
                && _x1 == other._x1 && _y1 == other._y1;

        public override bool Equals(object? obj) => obj is StatsKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(_pageId, _x0, _y0, _x1, _y1);
    }

    /// <summary>Region scan results memoized per (page, texel range) - masks
    /// re-ask the same question every frame while the page pixels stay fixed,
    /// so the O(texels) walk runs once per entry instead of per frame. Edge
    /// data computes lazily since empty regions never reach it.</summary>
    public sealed class RegionStats
    {
        public byte AMax;
        public int Covered;
        public uint AlphaSum;
        internal int EdgeSide = -1;
        internal int RimTexels;
    }

    /// <summary>Cached <see cref="AlphaStats"/> - identical math, O(1) on
    /// repeat queries. Entries self-invalidate with the page instance.</summary>
    public RegionStats RegionStatsFor(Page page, float x0, float y0, float x1, float y1)
    {
        var key = new StatsKey(page, x0, y0, x1, y1);
        if (!_regionStats.TryGetValue(key, out var st))
        {
            if (_regionStats.Count >= RegionStatsMax)
                _regionStats.Clear();
            st = new RegionStats();
            AlphaStats(page, x0, y0, x1, y1, out st.AMax, out st.Covered, out st.AlphaSum);
            _regionStats[key] = st;
        }
        return st;
    }

    /// <summary>Cached <see cref="OuterEdgeSide"/> - reuses the region's
    /// stored alpha max and computes the rim walk once per (page, region).</summary>
    public int RegionEdgeSide(Page page, float x0, float y0, float x1, float y1,
        out int rimTexels)
    {
        var st = RegionStatsFor(page, x0, y0, x1, y1);
        if (st.EdgeSide < 0)
            st.EdgeSide = OuterEdgeSide(page, x0, y0, x1, y1, st.AMax, out st.RimTexels);
        rimTexels = st.RimTexels;
        return st.EdgeSide;
    }

    public nint Srv => (nint)_srv;

    public string Status => _disposed ? "disposed"
        : _failed ? _failReason ?? "failed"
        : _srv != null ? "ok" : "init";

    public static Vector4 SlotUv(int slot, int usedW, int usedH)
    {
        var sx = (slot % SlotCols) * SlotWidth;
        var sy = (slot / SlotCols) * SlotHeight;
        return new Vector4(sx / (float)TexWidth, sy / (float)TexHeight,
            (sx + usedW) / (float)TexWidth, (sy + usedH) / (float)TexHeight);
    }

    public bool EnsureDevice()
    {
        if (_failed || _disposed)
            return false;
        if (_srv != null)
            return true;
        var kernel = GameDevice.Instance();
        var kernelContext = kernel != null ? (ID3D11DeviceContext*)kernel->D3D11DeviceContext : null;
        if (kernelContext == null)
            return Fail("noContext");
        if (_device == null)
        {
            ID3D11Device* device = null;
            kernelContext->GetDevice(&device);
            _device = device;
            if (_device == null)
                return Fail("noDevice");
        }
        if (_context == null)
        {
            ID3D11DeviceContext* immediate = null;
            _device->GetImmediateContext(&immediate);
            if (immediate == null)
                return Fail("noImmediateContext");
            _context = immediate;
        }
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = TexWidth,
            Height = TexHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };
        fixed (ID3D11Texture2D** tex = &_texture)
        {
            if ((int)_device->CreateTexture2D(&desc, null, tex) < 0 || _texture == null)
                return Fail("createTexture");
        }
        fixed (ID3D11ShaderResourceView** srv = &_srv)
        {
            if ((int)_device->CreateShaderResourceView((ID3D11Resource*)_texture, null, srv) < 0 || _srv == null)
                return Fail("createSrv");
        }
        return true;
    }

    /// <summary>Separates content-keyed entries (texture path hashes) from
    /// raw SRV keys - SRVs are userspace pointers well below this bit.</summary>
    private const ulong ContentKeyBit = 0x8000_0000_0000_0000UL;
    private const int ReadRetryMax = 8;
    private const int ReadRetryDelayMs = 400;
    // a pinned-unreadable key re-arms after this cooldown - a texture that
    // lost its early upload race gets a fresh attempt series instead of
    // dropping its masks for the rest of the session
    private const long PinRearmMs = 15000;
    // a resource whose kernel texture never materializes stops being treated
    // as "still loading" - the cover it held would otherwise become a phantom.
    // The window is per episode: a gap in queries (the window closed, the
    // node stopped being captured) re-arms it for the next open. Sixty
    // seconds bounds pathological phantoms while outlasting any real stream
    private const long PendingTtlMs = 60000;
    private const long PendingEpisodeGapMs = 1000;
    private const int PendingMapMax = 128;
    // a page re-reads once after this delay - identical pixels prove the
    // upload finished; shorter would race slow streams, longer just keeps
    // the conservative cover on a little more
    private const long SettleConfirmMs = 300;

    /// <summary>Resolves a mask texture to its CPU-side pixels, reading the
    /// game texture back through a staging copy on first use. Keyed by the
    /// uld texture's path hash when the caller supplies one - a window's
    /// texture gets a fresh SRV every time the addon reloads it, while the
    /// pixels stay identical, so a successful read survives reopen cycles.
    /// Failed and still-empty readbacks are NOT cached: the game streams the
    /// upload in after the SRV exists, so the early attempts race it and
    /// would otherwise poison the entry for the whole texture lifetime.
    /// They retry on a spaced budget, then pin as permanently unreadable.</summary>
    public Page? GetPage(nint srv, uint contentKey = 0)
    {
        var key = contentKey != 0 ? ContentKeyBit | contentKey : (ulong)srv;
        if (_pages.TryGetValue(key, out var cached))
        {
            // a page only graduates once a second live readback replays its
            // pixels - an early read can hold a half-uploaded texture whose
            // still-empty regions would quietly drop their masks
            if (cached != null && !cached.Settled)
                ConfirmSettled(key, srv, cached);
            if (cached != null)
                return cached;
            // pinned-unreadable - hold the pin only for the cooldown, then
            // drop it and let the retry path re-attempt the read
            if (_pagePinAt.TryGetValue(key, out var pinAt)
                && Environment.TickCount64 - pinAt < PinRearmMs)
                return null;
            _pages.Remove(key);
            _pagePinAt.Remove(key);
        }
        if (contentKey != 0 && !_diskMisses.Contains(key))
        {
            if (TryLoadCached(key, out var disk))
            {
                _pages[key] = disk;
                _settleAt[key] = Environment.TickCount64 + SettleConfirmMs;
                _pageAttempts.Remove(key);
                _pageRetryAt.Remove(key);
                _pending.Remove(key);
                return disk;
            }
            _diskMisses.Add(key);
        }
        if (_pageRetryAt.TryGetValue(key, out var retryAt)
            && Environment.TickCount64 < retryAt)
            return null;
        // a real read attempt proves the kernel exists - restart the pending
        // window so the readback-retry phase is not timed against the span
        // the key already spent waiting for its SRV
        _pending[key] = (Environment.TickCount64, Environment.TickCount64);
        var page = ReadPage(srv);
        if (page != null && !HasAnyAlpha(page))
            page = null;
        if (page == null)
        {
            var tries = _pageAttempts.TryGetValue(key, out var n) ? n + 1 : 1;
            _pageAttempts[key] = tries;
            if (tries >= ReadRetryMax)
            {
                _pages[key] = null;
                _pagePinAt[key] = Environment.TickCount64;
                _pageAttempts.Remove(key);
                _pageRetryAt.Remove(key);
                _pending.Remove(key);
                _settleAt.Remove(key);
            }
            else
            {
                _pageRetryAt[key] = Environment.TickCount64 + ReadRetryDelayMs;
            }
            return null;
        }
        _pages[key] = page;
        _pageAttempts.Remove(key);
        _pageRetryAt.Remove(key);
        _pagePinAt.Remove(key);
        _pending.Remove(key);
        // fresh pages persist only after the confirm read proves them -
        // otherwise a mid-upload snapshot would survive on disk
        _settleAt[key] = Environment.TickCount64 + SettleConfirmMs;
        return page;
    }

    /// <summary>The second live readback that proves a page's pixels are
    /// final. Identical results settle the page and persist it for content
    /// keys (rewriting also refreshes the file's timestamp for the prune
    /// cap); a mismatch means the first read raced the upload - the live
    /// page replaces the entry and re-arms the settle gate.</summary>
    private void ConfirmSettled(ulong key, nint srv, Page cached)
    {
        if (!_settleAt.TryGetValue(key, out var at)
            || Environment.TickCount64 < at)
            return;
        var real = ReadPage(srv);
        if (real == null || !HasAnyAlpha(real))
        {
            _settleAt[key] = Environment.TickCount64 + ReadRetryDelayMs;
            return;
        }
        if (SamePixels(real, cached))
        {
            cached.Settled = true;
            _settleAt.Remove(key);
            if ((key & ContentKeyBit) != 0)
                SaveCached(key, cached);
            return;
        }
        _pages[key] = real;
        _settleAt[key] = Environment.TickCount64 + SettleConfirmMs;
        _logger.LogDebug(
            "[Nameplate] coverage page {key:x16} refreshed - content changed",
            key);
    }

    private static bool SamePixels(Page a, Page b)
        => a.Width == b.Width && a.Height == b.Height && a.Bpp == b.Bpp
            && a.ChannelOffset.AsSpan().SequenceEqual(b.ChannelOffset)
            && a.Data.AsSpan().SequenceEqual(b.Data);

    // one file per content key: magic, w, h, bpp, four channel offsets, then
    // the raw pixel bytes - keyed by content hash, so the file survives SRV
    // reincarnation exactly like the in-memory entry does
    private const int PageMagic = 0x4E504356; // "NPCV"
    private const int PageHeaderInts = 9;
    private const int PageFileMaxBytes = 64 * 1024 * 1024;
    private const int PageCacheMaxFiles = 256;

    private string PagePath(ulong key)
        => Path.Combine(CacheDir(), $"p{key:x16}.pg");

    private bool TryLoadCached(ulong key, out Page page)
    {
        page = null!;
        try
        {
            var path = PagePath(key);
            if (!File.Exists(path))
                return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < PageHeaderInts * 4 || bytes.Length > PageFileMaxBytes)
                return false;
            var span = bytes.AsSpan();
            if (BitConverter.ToInt32(span) != PageMagic)
                return false;
            var w = BitConverter.ToInt32(span[4..]);
            var h = BitConverter.ToInt32(span[8..]);
            var bpp = BitConverter.ToInt32(span[12..]);
            if (w <= 0 || h <= 0 || bpp <= 0 || w > 16384 || h > 16384
                || (long)w * h * bpp != bytes.Length - PageHeaderInts * 4)
                return false;
            var offs = new int[4];
            for (var i = 0; i < 4; i++)
                offs[i] = BitConverter.ToInt32(span[(16 + i * 4)..]);
            var dataLen = BitConverter.ToInt32(span[32..]);
            if (dataLen != w * h * bpp)
                return false;
            page = new Page
            {
                Data = span[(PageHeaderInts * 4)..].ToArray(),
                Width = w,
                Height = h,
                Bpp = bpp,
                ChannelOffset = offs,
            };
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] coverage cache read failed");
            return false;
        }
    }

    private void SaveCached(ulong key, Page page)
    {
        try
        {
            var dir = CacheDir();
            Directory.CreateDirectory(dir);
            using var fs = new FileStream(PagePath(key), FileMode.Create,
                FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(PageMagic);
            bw.Write(page.Width);
            bw.Write(page.Height);
            bw.Write(page.Bpp);
            for (var i = 0; i < 4; i++)
                bw.Write(page.ChannelOffset[i]);
            bw.Write(page.Data.Length);
            bw.Write(page.Data);
            PruneCacheDir(dir);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] coverage cache write failed");
        }
    }

    /// <summary>Caps the page folder by dropping the oldest files - a page
    /// that stopped being asked for ages out with the files it rode in
    /// on.</summary>
    private static void PruneCacheDir(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("p*.pg");
        if (files.Length <= PageCacheMaxFiles)
            return;
        Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (var i = 0; i < files.Length - PageCacheMaxFiles; i++)
        {
            try { files[i].Delete(); }
            catch (Exception)
            {
                // a file that refuses to delete just stays - the cap is a
                // bound on growth, not a hard limit
            }
        }
    }

    /// <summary>True when a content key already owns a page - a pending node
    /// whose pixels are cached still deserves its solid cover until its own
    /// SRV wires up, so callers gate on this OR pending.</summary>
    public bool HasPage(uint contentKey)
        => contentKey != 0
            && _pages.TryGetValue(ContentKeyBit | contentKey, out var p)
            && p != null;

    /// <summary>True while a key has no settled entry - readback still racing
    /// the game's upload or retrying, or the resource's kernel texture has
    /// not materialized yet. Bounded by <see cref="PendingTtlMs"/> per query
    /// episode: a key that stays unresolved stops reporting pending so a
    /// texture that never lands cannot hold a phantom cover, while a gap in
    /// queries (window closed) re-arms the window for the next open.
    /// Permanently failed reads pin a null entry and report not-pending.</summary>
    public bool PagePending(nint srv, uint contentKey = 0)
    {
        var key = contentKey != 0 ? ContentKeyBit | contentKey : (ulong)srv;
        if (_pages.ContainsKey(key))
        {
            _pending.Remove(key);
            return false;
        }
        var now = Environment.TickCount64;
        if (!_pending.TryGetValue(key, out var ep)
            || now - ep.Last > PendingEpisodeGapMs)
        {
            if (_pending.Count >= PendingMapMax)
                _pending.Clear();
            _pending[key] = (now, now);
            return true;
        }
        _pending[key] = (ep.First, now);
        return now - ep.First < PendingTtlMs;
    }

    /// <summary>A texture whose upload has not landed yet reads back as all
    /// zero alpha - indistinguishable from a genuinely empty sheet, but both
    /// cases mean "no mask data", so treating it as pending only defers the
    /// inevitable until real pixels arrive.</summary>
    private static bool HasAnyAlpha(Page page)
        => page.ChannelOffset[3] < 0
            || (AlphaStats(page, 0, 0, page.Width, page.Height, out var max, out _)
                && max > 0);

    private Page? ReadPage(nint srvPtr)
    {
        if (_context == null || srvPtr == 0)
            return null;
        ID3D11Resource* resource = null;
        ID3D11Texture2D* source = null;
        ID3D11Texture2D* staging = null;
        try
        {
            var view = (ID3D11ShaderResourceView*)srvPtr;
            view->GetResource(&resource);
            if (resource == null)
                return null;
            // the SRV may wrap something that is not a 2D texture - probing
            // via QueryInterface keeps a foreign resource from being read
            // through the wrong vtable
            var texIid = TerraFX.Interop.Windows.IID.IID_ID3D11Texture2D;
            if ((int)resource->QueryInterface(&texIid, (void**)&source) < 0
                || source == null)
                return null;
            var desc = new D3D11_TEXTURE2D_DESC();
            source->GetDesc(&desc);
            var fmt = (int)desc.Format;
            var isBc = fmt is >= 71 and <= 78 and not 73 and not 76;
            int[] channelOffset;
            switch (fmt)
            {
                case 87: // B8G8R8A8_UNORM - channel c lives in byte [2,1,0,3][c]
                case 91: // B8G8R8A8_UNORM_SRGB - same layout, gamma-corrected
                    channelOffset = [2, 1, 0, 3];
                    break;
                case 88: // B8G8R8X8_UNORM - no alpha plane, renders opaque
                case 93: // B8G8R8X8_UNORM_SRGB
                    channelOffset = [2, 1, 0, -1];
                    break;
                case 28: // R8G8B8A8_UNORM
                case 29: // R8G8B8A8_UNORM_SRGB
                    channelOffset = [0, 1, 2, 3];
                    break;
                case 65: // A8_UNORM - single plane, only channel 3 carries ink
                    channelOffset = [-1, -1, -1, 0];
                    break;
                case >= 71 and <= 78 when isBc: // BC1/BC2/BC3 - decoded to RGBA below
                    channelOffset = [0, 1, 2, 3];
                    break;
                default:
                    _logger.LogDebug("[Nameplate] coverage readback skipped format {fmt}", fmt);
                    return null;
            }
            var bpp = fmt == 65 ? 1 : 4;
            var stagingDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                MiscFlags = 0,
            };
            if ((int)_device->CreateTexture2D(&stagingDesc, null, &staging) < 0 || staging == null)
                return null;
            _context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0,
                resource, 0, null);
            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            if ((int)_context->Map((ID3D11Resource*)staging, 0,
                    D3D11_MAP.D3D11_MAP_READ, 0, &mapped) < 0)
                return null;
            var w = (int)desc.Width;
            var h = (int)desc.Height;
            byte[] data;
            if (isBc)
            {
                data = DecodeBc((byte*)mapped.pData, (int)mapped.RowPitch, w, h, fmt);
                _context->Unmap((ID3D11Resource*)staging, 0);
            }
            else
            {
                data = new byte[w * h * bpp];
                for (var y = 0; y < h; y++)
                {
                    var srcRow = (byte*)mapped.pData + y * mapped.RowPitch;
                    fixed (byte* dst = data)
                        Buffer.MemoryCopy(srcRow, dst + y * w * bpp, w * bpp, w * bpp);
                }
                _context->Unmap((ID3D11Resource*)staging, 0);
            }
            return new Page { Data = data, Width = w, Height = h, Bpp = bpp, ChannelOffset = channelOffset };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] coverage readback failed");
            return null;
        }
        finally
        {
            if (staging != null)
                staging->Release();
            if (source != null)
                source->Release();
            if (resource != null)
                resource->Release();
        }
    }

    /// <summary>Decodes a BC1/BC2/BC3 staging buffer to straight RGBA. UI
    /// atlases ship compressed - the target arrow lives in one - so masks need
    /// real texels, not a rejected format.</summary>
    private static unsafe byte[] DecodeBc(byte* src, int rowPitch, int w, int h, int fmt)
    {
        var data = new byte[w * h * 4];
        var blockBytes = fmt is 71 or 72 ? 8 : 16;
        for (var by = 0; by < (h + 3) / 4; by++)
        {
            for (var bx = 0; bx < (w + 3) / 4; bx++)
            {
                DecodeBcBlock(src + by * rowPitch + bx * blockBytes, fmt, data, w, h, bx, by);
            }
        }
        return data;
    }

    private static unsafe void DecodeBcBlock(byte* block, int fmt, byte[] data,
        int w, int h, int bx, int by)
    {
        Span<byte> alpha = stackalloc byte[16];
        var color = block;
        if (fmt is 77 or 78) // BC3: a0,a1 + 48 bits of 3-bit indices
        {
            var a0 = block[0];
            var a1 = block[1];
            ulong bits = 0;
            for (var i = 0; i < 6; i++)
                bits |= (ulong)block[2 + i] << (8 * i);
            Span<byte> pal = stackalloc byte[8];
            pal[0] = a0;
            pal[1] = a1;
            if (a0 > a1)
                for (var i = 0; i < 6; i++)
                    pal[2 + i] = (byte)((7 - 1 - i) * a0 / 7 + (i + 1) * a1 / 7);
            else
            {
                for (var i = 0; i < 4; i++)
                    pal[2 + i] = (byte)((5 - 1 - i) * a0 / 5 + (i + 1) * a1 / 5);
                pal[6] = 0;
                pal[7] = 255;
            }
            for (var i = 0; i < 16; i++)
                alpha[i] = pal[(int)((bits >> (3 * i)) & 7UL)];
            color = block + 8;
        }
        else if (fmt is 74 or 75) // BC2: 16× 4-bit explicit alpha
        {
            for (var i = 0; i < 8; i++)
            {
                alpha[2 * i] = (byte)((block[i] & 0xF) * 17);
                alpha[2 * i + 1] = (byte)((block[i] >> 4) * 17);
            }
            color = block + 8;
        }
        else
        {
            alpha.Fill(255);
        }
        var c0 = (ushort)(color[0] | (color[1] << 8));
        var c1 = (ushort)(color[2] | (color[3] << 8));
        Span<byte> r = stackalloc byte[4];
        Span<byte> g = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        Unpack565(c0, out r[0], out g[0], out b[0]);
        Unpack565(c1, out r[1], out g[1], out b[1]);
        if (c0 > c1 || fmt is not (71 or 72))
        {
            r[2] = (byte)((2 * r[0] + r[1] + 1) / 3);
            g[2] = (byte)((2 * g[0] + g[1] + 1) / 3);
            b[2] = (byte)((2 * b[0] + b[1] + 1) / 3);
            r[3] = (byte)((r[0] + 2 * r[1] + 1) / 3);
            g[3] = (byte)((g[0] + 2 * g[1] + 1) / 3);
            b[3] = (byte)((b[0] + 2 * b[1] + 1) / 3);
        }
        else
        {
            r[2] = (byte)((r[0] + r[1] + 1) / 2);
            g[2] = (byte)((g[0] + g[1] + 1) / 2);
            b[2] = (byte)((b[0] + b[1] + 1) / 2);
        }
        var bits32 = (uint)(color[4] | (color[5] << 8) | (color[6] << 16) | (color[7] << 24));
        for (var py = 0; py < 4; py++)
        {
            for (var px = 0; px < 4; px++)
            {
                var x = bx * 4 + px;
                var y = by * 4 + py;
                if (x >= w || y >= h)
                    continue;
                var idx = (int)((bits32 >> (2 * (py * 4 + px))) & 3);
                var o = (y * w + x) * 4;
                data[o] = r[idx];
                data[o + 1] = g[idx];
                data[o + 2] = b[idx];
                data[o + 3] = fmt is 71 or 72 && c0 <= c1 && idx == 3 ? (byte)0 : alpha[py * 4 + px];
            }
        }
    }

    private static void Unpack565(ushort c, out byte r, out byte g, out byte b)
    {
        var r5 = (c >> 11) & 0x1F;
        var g6 = (c >> 5) & 0x3F;
        var b5 = c & 0x1F;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }
    public void BeginSlot(int slot)
    {
        var sx = (slot % SlotCols) * SlotWidth;
        var sy = (slot / SlotCols) * SlotHeight;
        for (var y = 0; y < SlotHeight; y++)
            Array.Clear(_pixels, (sy + y) * TexWidth + sx, SlotWidth);
        _dirty[slot] = true;
    }

    /// <summary>Bilinear resample of one channel plane into the slot - the dst
    /// rect is already in coverage pixels, so pen sub-texel placement folds
    /// into the resample exactly like the rasterizer's bilinear sampling.
    /// <paramref name="alpha"/> bakes the mask's per-element cut strength in.</summary>
    public void Blit(int slot, Page page, int channel, float alpha,
        float sx0, float sy0, float sx1, float sy1,
        float dx0, float dy0, float dx1, float dy1)
    {
        var off = channel >= 0 && channel < 4 ? page.ChannelOffset[channel] : -1;
        if (off < 0)
            return;
        var baseX = (slot % SlotCols) * SlotWidth;
        var baseY = (slot / SlotCols) * SlotHeight;
        var dw = dx1 - dx0;
        var dh = dy1 - dy0;
        if (dw <= 0f || dh <= 0f)
            return;
        var iy1 = (int)MathF.Ceiling(dy1);
        var ix1 = (int)MathF.Ceiling(dx1);
        var sw = sx1 - sx0;
        var sh = sy1 - sy0;
        for (var y = (int)dy0; y < iy1; y++)
        {
            if (y < 0 || y >= SlotHeight)
                continue;
            var fy = sy0 + (y + 0.5f - dy0) / dh * sh - 0.5f;
            var y0 = (int)MathF.Floor(fy);
            var wy = fy - y0;
            var ya = Math.Clamp(y0, 0, page.Height - 1);
            var yb = Math.Clamp(y0 + 1, 0, page.Height - 1);
            var rowDst = (baseY + y) * TexWidth + baseX;
            var rowA = ya * page.Width * page.Bpp + off;
            var rowB = yb * page.Width * page.Bpp + off;
            for (var x = (int)dx0; x < ix1; x++)
            {
                if (x < 0 || x >= SlotWidth)
                    continue;
                var fx = sx0 + (x + 0.5f - dx0) / dw * sw - 0.5f;
                var x0 = (int)MathF.Floor(fx);
                var wx = fx - x0;
                var xa = Math.Clamp(x0, 0, page.Width - 1);
                var xb = Math.Clamp(x0 + 1, 0, page.Width - 1);
                var v00 = page.Data[rowA + xa * page.Bpp];
                var v10 = page.Data[rowA + xb * page.Bpp];
                var v01 = page.Data[rowB + xa * page.Bpp];
                var v11 = page.Data[rowB + xb * page.Bpp];
                var v = (byte)((v00 * (1f - wx) * (1f - wy) + v10 * wx * (1f - wy)
                    + v01 * (1f - wx) * wy + v11 * wx * wy) * alpha + 0.5f);
                var idx = rowDst + x;
                if (v > _pixels[idx])
                    _pixels[idx] = v;
            }
        }
    }

    /// <summary>Applies the outline bleed the game draws past each glyph box -
    /// a 3x3 max at the edge radius, matching the shader's tap pattern.</summary>
    public void Dilate(int slot, int radiusX, int radiusY)
    {
        if (radiusX <= 0 && radiusY <= 0)
            return;
        var sx = (slot % SlotCols) * SlotWidth;
        var sy = (slot / SlotCols) * SlotHeight;
        var stepX = Math.Max(radiusX, 1);
        var stepY = Math.Max(radiusY, 1);
        for (var y = 0; y < SlotHeight; y++)
        {
            for (var x = 0; x < SlotWidth; x++)
            {
                var max = _pixels[(sy + y) * TexWidth + sx + x];
                for (var dy = -radiusY; dy <= radiusY; dy += stepY)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= SlotHeight)
                        continue;
                    for (var dx = -radiusX; dx <= radiusX; dx += stepX)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= SlotWidth)
                            continue;
                        var s = _pixels[(sy + ny) * TexWidth + sx + nx];
                        if (s > max)
                            max = s;
                    }
                }
                _scratch[y * SlotWidth + x] = max;
            }
        }
        for (var y = 0; y < SlotHeight; y++)
        {
            Array.Copy(_scratch, y * SlotWidth, _pixels,
                (sy + y) * TexWidth + sx, SlotWidth);
        }
    }

    /// <summary>Uploads every slot that was touched since the last flush.</summary>
    public void Flush()
    {
        if (_context == null || _texture == null)
            return;
        for (var slot = 0; slot < MaxSlots; slot++)
        {
            if (!_dirty[slot])
                continue;
            _dirty[slot] = false;
            var box = new D3D11_BOX
            {
                left = (uint)((slot % SlotCols) * SlotWidth),
                top = (uint)((slot / SlotCols) * SlotHeight),
                front = 0,
                right = (uint)((slot % SlotCols) * SlotWidth + SlotWidth),
                bottom = (uint)((slot / SlotCols) * SlotHeight + SlotHeight),
                back = 1,
            };
            fixed (byte* src = _pixels)
            {
                _context->UpdateSubresource((ID3D11Resource*)_texture, 0, &box,
                    src + box.top * TexWidth + box.left, TexWidth, 0);
            }
        }
    }

    private bool Fail(string reason)
    {
        if (!_failed)
            _logger.LogDebug("[Nameplate] coverage bake unavailable: {reason}", reason);
        _failed = true;
        _failReason = reason;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_srv != null) { _srv->Release(); _srv = null; }
        if (_texture != null) { _texture->Release(); _texture = null; }
        if (_device != null) { _device->Release(); _device = null; }
        if (_context != null) { _context->Release(); _context = null; }
    }
}
