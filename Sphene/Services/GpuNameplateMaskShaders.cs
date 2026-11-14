using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;

namespace Sphene.Services;

internal static unsafe class GpuNameplateMaskShaders
{
    private const string ImGuiPixelSource = """
        Texture2D<float4> sourceImage : register(t0);
        Texture2D<float> sceneDepth : register(t1);
        Texture2D<float4> plateBake : register(t2);
        Texture2D<float4> plateBake2 : register(t3);
        Texture2D<float4> plateBake3 : register(t4);
        Texture2D<float4> plateBake4 : register(t5);
        Texture2D<float4> plateBake5 : register(t6);
        Texture2D<float4> plateBake6 : register(t7);
        Texture2D<float4> plateBake7 : register(t8);
        Texture2D<float4> plateBake8 : register(t9);
        SamplerState sourceSampler : register(s0);
        cbuffer MaskParameters : register(b0)
        {
            float4 sceneInfo;
            float4 iconInfo;
            float4 maskHeader;
            float4 maskScreen[160];
            float4 maskUv[160];
            float4 maskChannel[160];
        };
        static const float2 edgeTaps[9] = {
            float2(0.0, 0.0), float2(1.0, 0.0), float2(-1.0, 0.0),
            float2(0.0, 1.0), float2(0.0, -1.0), float2(1.0, 1.0),
            float2(1.0, -1.0), float2(-1.0, 1.0), float2(-1.0, -1.0) };
        struct Input
        {
            float4 position : SV_POSITION;
            float4 color : COLOR0;
            float2 uv : TEXCOORD0;
        };
        float4 main(Input input) : SV_TARGET
        {
            float4 color = input.color * sourceImage.Sample(sourceSampler, input.uv);
            int2 depthPixel = clamp(int2(input.position.xy * sceneInfo.xy),
                int2(0, 0), int2(sceneInfo.zw) - 1);
            float scene = sceneDepth.Load(int3(depthPixel, 0));
            if (scene > 0 && iconInfo.x + iconInfo.y < scene)
                color.a = 0;
            // [loop] keeps the mask array a real loop - letting the compiler
            // unroll 160 iterations made wine's d3dcompiler take ~60s
            [loop]
            for (uint m = 0; m < min((uint)maskHeader.x, 160u); m++)
            {
                float4 s = maskScreen[m];
                float4 u = maskUv[m];
                float ex = 0.0;
                float ey = 0.0;
                float refA = 1.0;
                float edgeSide = 0.0;
                float rimPx = 0.0;
                if (u.z >= 0.0 && maskChannel[m].w > 0.0)
                {
                    if (maskChannel[m].x >= 8.0)
                    {
                        // HUD masks don't dilate - .w packs three values:
                        // rim offset in screen px (*32), the bitmask of
                        // sides the band ramps on (*2, 1|2|4|8 l/r/t/b),
                        // and the region's own max alpha (fraction)
                        rimPx = floor(maskChannel[m].w * (1.0 / 32.0));
                        float rem = maskChannel[m].w - rimPx * 32.0;
                        edgeSide = floor(rem * 0.5);
                        refA = rem - edgeSide * 2.0;
                    }
                    else
                    {
                        ex = floor(maskChannel[m].w * (1.0 / 512.0)) * 0.5;
                        ey = maskChannel[m].w - floor(maskChannel[m].w
                            * (1.0 / 512.0)) * 512.0;
                        ey *= 0.5;
                    }
                }
                if (input.position.x >= s.x - ex && input.position.x < s.z + ex
                    && input.position.y >= s.y - ey && input.position.y < s.w + ey)
                {
                    if (u.z < 0.0)
                    {
                        float d = min(input.position.x - s.x, s.z - input.position.x);
                        d = min(d, min(input.position.y - s.y, s.w - input.position.y));
                        color.a *= 1.0 - saturate(d / 3.0) * maskChannel[m].z;
                        continue;
                    }
                    float2 span = max(s.zw - s.xy, float2(1.0, 1.0));
                    float2 uv = u.xy + (input.position.xy - s.xy) / span
                        * (u.zw - u.xy);
                    // game font atlases pack four glyphs per texel into
                    // r/g/b/a - each mask carries the channel its glyph uses;
                    // .y selects the secondary atlas (fallback font pages),
                    // .z the cut strength (translucent text cuts less)
                    uint channel = (uint)maskChannel[m].x;
                    // channel >= 8 marks HUD window chrome: same linear ramp,
                    // but each pixel is also gated by coverage/refA - the
                    // region's own max alpha. Window parts carry a drawn
                    // drop-shadow/AA rim below ~80% of their densest texel;
                    // without the gate that rim fades our overlay past the
                    // hard frame edge. Uniform translucent fills sit at
                    // ~100% of their max, so their cut stays unchanged
                    float hard = channel >= 8u ? 1.0 : 0.0;
                    // channel 4..7 marks linear-alpha masks (target cursor):
                    // cut strength follows the texture's real alpha gradient,
                    // so soft halo edges fade instead of hard-clipping
                    float soft = (channel >= 4u && channel < 8u) ? 1.0 : 0.0;
                    channel = channel % 4u;
                    // the dilated outline the game draws past each glyph box -
                    // taps clamp into the glyph's atlas rect so pixels in the
                    // bleed ring still sample its edge coverage
                    float2 edgeUv = (u.zw - u.xy) / span * float2(ex, ey);
                    float coverage = 0.0;
                    // t2..t9 hold the bound mask textures - dominant atlases
                    // take slots 0/1, cursor/coverage/bypass textures pin 2..7
                    float sel = maskChannel[m].y;
                    for (int t = 0; t < 9; t++)
                    {
                        float2 uv0 = clamp(uv + edgeTaps[t] * edgeUv, u.xy, u.zw);
                        float4 ink;
                        if (sel > 6.5) ink = plateBake8.Sample(sourceSampler, uv0);
                        else if (sel > 5.5) ink = plateBake7.Sample(sourceSampler, uv0);
                        else if (sel > 4.5) ink = plateBake6.Sample(sourceSampler, uv0);
                        else if (sel > 3.5) ink = plateBake5.Sample(sourceSampler, uv0);
                        else if (sel > 2.5) ink = plateBake4.Sample(sourceSampler, uv0);
                        else if (sel > 1.5) ink = plateBake3.Sample(sourceSampler, uv0);
                        else if (sel > 0.5) ink = plateBake2.Sample(sourceSampler, uv0);
                        else ink = plateBake.Sample(sourceSampler, uv0);
                        coverage = max(coverage, ink[channel]);
                    }
                    // cut the icon wherever real ink is visible - a narrow
                    // feather below ~25% coverage keeps the antialiased edge
                    // soft without hollowing out translucent letter fill.
                    // Linear masks (HUD chrome, glow overlays, cursor) floor
                    // faint coverage out: pixels under ~20% alpha are only
                    // tints the game repaints anyway, so cutting there adds
                    // a long halo fade past the real window edge.
                    // edge bands ramp on their outer side: past the
                    // silhouette (rimPx from that edge) the frame is window
                    // interior - cut floored at the plateau so dips in the
                    // texture cannot let the overlay reappear. Between the
                    // quad edge and the silhouette lies the drawn drop
                    // shadow - it barely occludes, so the gate stays
                    float rel = coverage / max(refA, 0.01);
                    // inside = past every ramping side's silhouette - a two-
                    // sided strip floors its middle band, a one-sided column
                    // everything inward, a four-sided whole window never
                    // gets marked so it keeps proportional alpha
                    float dL = input.position.x - s.x;
                    float dR = s.z - input.position.x;
                    float dT = input.position.y - s.y;
                    float dB = s.w - input.position.y;
                    uint sides = (uint)edgeSide;
                    bool inside = edgeSide > 0.5
                        && ((sides & 1u) == 0u || dL >= rimPx)
                        && ((sides & 2u) == 0u || dR >= rimPx)
                        && ((sides & 4u) == 0u || dT >= rimPx)
                        && ((sides & 8u) == 0u || dB >= rimPx);
                    float cut = hard > 0.5
                        ? (inside
                            // the frame band composites several parts to
                            // ~90% - flooring at the region's own plateau
                            // (.74) left a visible ghost over the frame
                            ? saturate((max(coverage, 0.90) - 0.20) / 0.80)
                            : saturate((coverage - 0.20) / 0.80)
                                * smoothstep(0.80, 0.92, rel))
                        : soft > 0.5 ? saturate((coverage - 0.20) / 0.80)
                        : smoothstep(0.06, 0.25, coverage);
                    color.a *= 1.0 - cut * maskChannel[m].z;
                }
            }
            return color;
        }
        """;

    private const uint CompileFlags = 1024; // D3DCOMPILE_PREFER_FLOW_CONTROL

    public static bool TryCompile(out ID3DBlob* result, out string? error)
        => Compile(ImGuiPixelSource, "ps_5_0\0"u8, out result, out error);

    public enum CompilePoll
    {
        Pending,
        Ready,
        Failed,
    }

    private static Task<(byte[]? Bytecode, string? Error, long QueueMs, long CompileMs, string? CacheKind)>? _warmTask;
    private static string? _cacheDir;
    private static ILogger? _logger;
    private static bool _skipCaches;

    /// <summary>Queue wait measured for the last completed compile - the
    /// thread-pool starvation the startup diagnostic exposed.</summary>
    public static long LastQueueMs { get; private set; }
    /// <summary>Wall-clock time D3DCompile itself took.</summary>
    public static long LastCompileMs { get; private set; }
    /// <summary>True when the bytecode came from the disk cache, not a fresh
    /// compile - wine's d3dcompiler takes ~60s, so the blob is stored in the
    /// plugin config dir keyed by a hash of the shader source.</summary>
    public static bool LastFromCache { get; private set; }
    /// <summary>Where the last bytecode came from: "disk", "embedded", or
    /// null for a fresh compile.</summary>
    public static string? LastCacheKind { get; private set; }
    /// <summary>Disk path or resource name that produced the last bytecode -
    /// logged so users can verify which cache tier served the shader.</summary>
    public static string? LastCachePath { get; private set; }
    private static string? _cachePath;

    /// <summary>Starts shader compilation on a dedicated background thread -
    /// D3DCompile is free-threaded and its first call can take seconds (dll
    /// load + optimizer), which would freeze the render thread if done inside
    /// a draw callback. A dedicated thread, not Task.Run: at plugin load the
    /// .NET thread pool is saturated with startup work (font atlas, SignalR),
    /// and this compile already starved ~57s in the queue once.</summary>
    public static void Prewarm(string? cacheDir, ILogger? logger = null)
    {
        if (_warmTask != null)
            return;
        _logger ??= logger;
        _cacheDir = cacheDir;
        var queuedUtc = DateTime.UtcNow;
        var tcs = new TaskCompletionSource<(byte[]?, string?, long, long, string?)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _warmTask = tcs.Task;
        var thread = new Thread(() =>
        {
            try
            {
                var started = DateTime.UtcNow;
                var queueMs = (long)(started - queuedUtc).TotalMilliseconds;
                var skip = _skipCaches;
                _skipCaches = false;
                byte[]? cached = null;
                string? kind = null;
                if (!skip)
                {
                    cached = TryLoadCache();
                    if (cached != null) kind = "disk";
                    else
                    {
                        cached = TryLoadEmbedded();
                        if (cached != null) kind = "embedded";
                    }
                }
                if (cached != null)
                {
                    tcs.TrySetResult((cached, (string?)null, queueMs, (long)0, kind));
                    return;
                }
                var ok = TryCompile(out var blob, out var error);
                byte[]? bytes = null;
                if (ok && blob != null)
                {
                    bytes = new ReadOnlySpan<byte>(blob->GetBufferPointer(),
                        (int)blob->GetBufferSize()).ToArray();
                    blob->Release();
                    TryStoreCache(bytes);
                }
                var compileMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                tcs.TrySetResult((bytes, error, queueMs, compileMs, (string?)null));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "SpheneMaskShaderCompile" };
        thread.Start();
    }

    /// <summary>Non-blocking fetch of the prewarmed bytecode - taken exactly
    /// once; the returned bytes only need pinning for CreatePixelShader.</summary>
    public static CompilePoll Poll(out byte[]? bytecode, out string? error)
    {
        bytecode = null;
        error = null;
        var task = _warmTask;
        if (task == null)
        {
            error = "notStarted";
            return CompilePoll.Failed;
        }
        if (!task.IsCompleted)
            return CompilePoll.Pending;
        _warmTask = null;
        if (task.IsFaulted)
        {
            error = task.Exception?.GetBaseException().Message;
            return CompilePoll.Failed;
        }
        if (task.Result.Bytecode == null)
        {
            error = task.Result.Error;
            return CompilePoll.Failed;
        }
        LastQueueMs = task.Result.QueueMs;
        LastCompileMs = task.Result.CompileMs;
        LastCacheKind = task.Result.CacheKind;
        LastFromCache = task.Result.CacheKind != null;
        LastCachePath = task.Result.CacheKind != null ? _cachePath : null;
        bytecode = task.Result.Bytecode;
        return CompilePoll.Ready;
    }

    /// <summary>Drops the disk cache after a cached blob was rejected by
    /// CreatePixelShader - the next Prewarm recompiles once instead of the
    /// mask failing permanently.</summary>
    public static void InvalidateCache()
    {
        _warmTask = null;
        _skipCaches = true; // embedded blobs can't be deleted - force one live compile
        try
        {
            var file = CacheFile();
            if (file != null && File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Nameplate] shader bytecode cache delete failed");
        }
    }

    private static string? CacheFile()
    {
        var dir = _cacheDir;
        if (string.IsNullOrEmpty(dir))
            return null;
        return Path.Combine(dir, $"npsh-{SourceHash():x8}.dxbc");
    }

    private static uint SourceHash()
    {
        // everything that changes the produced blob, not only the source -
        // flags/entry/target edits must invalidate the cached bytecode too
        var bytes = Encoding.UTF8.GetBytes($"{ImGuiPixelSource}|main|ps_5_0|{CompileFlags}");
        var h = 2166136261u;
        foreach (var b in bytes)
            h = (h ^ b) * 16777619u;
        return h;
    }

    private static byte[]? TryLoadCache()
    {
        try
        {
            var file = CacheFile();
            if (file == null || !File.Exists(file))
                return null;
            var bytes = File.ReadAllBytes(file);
            if (IsDxbc(bytes))
            {
                _cachePath = file;
                return bytes;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Nameplate] shader bytecode cache read failed");
            return null;
        }
    }

    /// <summary>Bytecode shipped inside the assembly - the plugin package can
    /// carry `Resources/Shaders/npsh-&lt;hash&gt;.dxbc` so users never compile.
    /// The hash in the name makes stale shipped blobs a clean miss.</summary>
    private static byte[]? TryLoadEmbedded()
    {
        try
        {
            var suffix = $"npsh-{SourceHash():x8}.dxbc";
            var names = typeof(GpuNameplateMaskShaders).Assembly.GetManifestResourceNames();
            var match = Array.Find(names, n => n.EndsWith(suffix, StringComparison.Ordinal));
            if (match == null)
                return null;
            using var stream = typeof(GpuNameplateMaskShaders).Assembly.GetManifestResourceStream(match);
            if (stream == null)
                return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (IsDxbc(bytes))
            {
                _cachePath = match;
                return bytes;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Nameplate] embedded shader bytecode read failed");
            return null;
        }
    }

    private static bool IsDxbc(byte[] bytes)
        => bytes.Length > 4 && bytes[0] == (byte)'D' && bytes[1] == (byte)'X'
            && bytes[2] == (byte)'B' && bytes[3] == (byte)'C';

    private static void TryStoreCache(byte[] bytes)
    {
        try
        {
            var dir = _cacheDir;
            var file = CacheFile();
            if (dir == null || file == null)
                return;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(file, bytes);
            foreach (var stale in Directory.EnumerateFiles(dir, "npsh-*.dxbc"))
            {
                if (!string.Equals(stale, file, StringComparison.OrdinalIgnoreCase))
                    File.Delete(stale);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Nameplate] shader bytecode cache write failed");
        }
    }

    private static bool Compile(string shader, ReadOnlySpan<byte> target, out ID3DBlob* result, out string? error)
    {
        result = null;
        error = null;
        var source = Encoding.UTF8.GetBytes(shader);
        var entry = "main\0"u8;
        ID3DBlob* errors = null;
        ID3DBlob* compiled = null;
        try
        {
            fixed (byte* sourcePtr = source)
            fixed (byte* entryPtr = entry)
            fixed (byte* targetPtr = target)
            {
                var hr = D3DCompile(sourcePtr, (nuint)source.Length, null, null, null,
                    entryPtr, targetPtr, CompileFlags, 0, &compiled, &errors);
                if (hr >= 0 && compiled != null)
                {
                    result = compiled;
                    return true;
                }
                error = errors != null
                    ? Marshal.PtrToStringUTF8((nint)errors->GetBufferPointer(), (int)errors->GetBufferSize())
                    : $"D3DCompile failed: 0x{hr:X8}";
                if (compiled != null)
                    compiled->Release();
                return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (errors != null)
                errors->Release();
        }
    }

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* source, nuint sourceLength, byte* fileName,
        void* defines, void* include, byte* entryPoint, byte* target, uint flags1, uint flags2,
        ID3DBlob** code, ID3DBlob** errors);
}
