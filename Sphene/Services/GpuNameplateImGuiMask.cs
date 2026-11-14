using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using GameDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Sphene.Services;

/// <summary>
/// Masks the existing ImGui icon and label commands with the game's scene depth.
/// Callbacks change only pixel-stage state and restore it before any other UI draw.
/// </summary>
internal sealed unsafe class GpuNameplateImGuiMask : IDisposable
{
    public const int MaxPlateMasks = 160;
    private const uint ConstantBufferBytes = 7728;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Parameters
    {
        public Vector4 SceneInfo;
        public Vector4 IconInfo;
        public Vector4 MaskHeader;
        public fixed float MaskScreen[640];
        public fixed float MaskUv[640];
        public fixed float MaskChannel[640];
    }

    private sealed class Packet
    {
        public required GpuNameplateImGuiMask Owner;
        public ID3D11ShaderResourceView* Depth;
        public ID3D11ShaderResourceView* Bake;
        public ID3D11ShaderResourceView* Bake2;
        public ID3D11ShaderResourceView* Bake3;
        public ID3D11ShaderResourceView* Bake4;
        public ID3D11ShaderResourceView* Bake5;
        public ID3D11ShaderResourceView* Bake6;
        public ID3D11ShaderResourceView* Bake7;
        public ID3D11ShaderResourceView* Bake8;
        public ID3D11Buffer* Constants;
        public Parameters Values;
        public ID3D11PixelShader* PreviousShader;
        public ID3D11ShaderResourceView* PreviousDepth;
        public ID3D11ShaderResourceView* PreviousBake;
        public ID3D11ShaderResourceView* PreviousBake2;
        public ID3D11ShaderResourceView* PreviousBake3;
        public ID3D11ShaderResourceView* PreviousBake4;
        public ID3D11ShaderResourceView* PreviousBake5;
        public ID3D11ShaderResourceView* PreviousBake6;
        public ID3D11ShaderResourceView* PreviousBake7;
        public ID3D11ShaderResourceView* PreviousBake8;
        public ID3D11Buffer* PreviousConstants;
        public bool Bound;
    }

    private static readonly ImDrawCallback BeginCallback = BeginDraw;
    private static readonly ImDrawCallback EndCallback = EndDraw;
    private readonly ILogger _logger;
    private readonly string? _shaderCacheDir;
    private readonly GpuNameplateDepthSource _depth = new();
    private readonly nint[] _buffers = new nint[50];
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11PixelShader* _shader;
    private bool _failed;
    private bool _disposed;

    public GpuNameplateImGuiMask(ILogger logger, string? shaderCacheDir = null)
    {
        _logger = logger;
        _shaderCacheDir = shaderCacheDir;
        GpuNameplateMaskShaders.Prewarm(shaderCacheDir, logger);
    }

    public string Status { get; private set; } = "off";

    /// <summary>Starts depth masking this plate's subsequent ImGui draw commands.
    /// Baked nameplate text of plates in front is passed as screen/uv rect pairs -
    /// only their glyph pixels mask the icon, so it shines through letter gaps.
    /// <paramref name="maskAlpha"/> scales each quad's cut strength - translucent
    /// text (companion owner names) only removes its own ink share.</summary>
    public nint Begin(ImDrawListPtr drawList, int plateIndex, Vector3 world,
        nint bakeView, nint bake2View, nint bake3View, nint bake4View,
        nint bake5View, nint bake6View, nint bake7View, nint bake8View,
        Vector4[] maskScreen, Vector4[] maskUv,
        float[] maskChannel, float[] maskAlpha, float[] maskTexSel,
        float[] maskEdge, int maskCount)
    {
        if (_disposed || _failed || plateIndex is < 0 or >= 50 || !EnsureShader())
            return 0;
        var removedReason = _device->GetDeviceRemovedReason();
        if (removedReason < 0)
        {
            Fail($"deviceRemoved:0x{removedReason:X8}");
            return 0;
        }
        if (!_depth.TryGet(_device, _context, out var depthView, out var depthWidth, out var depthHeight))
        {
            Status = "noSceneDepth";
            return 0;
        }
        var gameDevice = GameDevice.Instance();
        var control = Control.Instance();
        if (gameDevice == null || gameDevice->Width < 1 || gameDevice->Height < 1 || control == null)
        {
            Status = "noCamera";
            return 0;
        }
        var matrix = *(Matrix4x4*)&control->ViewProjectionMatrix;
        var projected = Vector4.Transform(new Vector4(world, 1f), matrix);
        var z = projected.Z / projected.W;
        if (projected.W <= 0 || !float.IsFinite(z) || z is < 0f or > 1f)
        {
            Status = "badProjection";
            return 0;
        }
        var buffer = (ID3D11Buffer*)_buffers[plateIndex];
        if (buffer == null)
        {
            var desc = new D3D11_BUFFER_DESC(ConstantBufferBytes, (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER);
            if ((int)_device->CreateBuffer(&desc, null, &buffer) < 0 || buffer == null)
            {
                Status = "createConstants";
                return 0;
            }
            _buffers[plateIndex] = (nint)buffer;
        }
        depthView->AddRef();
        var bake = (ID3D11ShaderResourceView*)bakeView;
        if (bake != null)
            bake->AddRef();
        var bake2 = (ID3D11ShaderResourceView*)bake2View;
        if (bake2 != null)
            bake2->AddRef();
        var bake3 = (ID3D11ShaderResourceView*)bake3View;
        if (bake3 != null)
            bake3->AddRef();
        var bake4 = (ID3D11ShaderResourceView*)bake4View;
        if (bake4 != null)
            bake4->AddRef();
        var bake5 = (ID3D11ShaderResourceView*)bake5View;
        if (bake5 != null)
            bake5->AddRef();
        var bake6 = (ID3D11ShaderResourceView*)bake6View;
        if (bake6 != null)
            bake6->AddRef();
        var bake7 = (ID3D11ShaderResourceView*)bake7View;
        if (bake7 != null)
            bake7->AddRef();
        var bake8 = (ID3D11ShaderResourceView*)bake8View;
        if (bake8 != null)
            bake8->AddRef();
        var count = Math.Min(maskCount, MaxPlateMasks);
        var packet = new Packet
        {
            Owner = this,
            Depth = depthView,
            Bake = bake,
            Bake2 = bake2,
            Bake3 = bake3,
            Bake4 = bake4,
            Bake5 = bake5,
            Bake6 = bake6,
            Bake7 = bake7,
            Bake8 = bake8,
            Constants = buffer,
            Values = new Parameters
            {
                SceneInfo = new Vector4(depthWidth / (float)gameDevice->Width,
                    depthHeight / (float)gameDevice->Height, depthWidth, depthHeight),
                IconInfo = new Vector4(z, 0.00005f, 0, 0),
                MaskHeader = new Vector4(count, 0, 0, 0)
            }
        };
        for (var i = 0; i < count; i++)
        {
            var s = maskScreen[i];
            var u = maskUv[i];
            packet.Values.MaskScreen[i * 4 + 0] = s.X;
            packet.Values.MaskScreen[i * 4 + 1] = s.Y;
            packet.Values.MaskScreen[i * 4 + 2] = s.Z;
            packet.Values.MaskScreen[i * 4 + 3] = s.W;
            packet.Values.MaskUv[i * 4 + 0] = u.X;
            packet.Values.MaskUv[i * 4 + 1] = u.Y;
            packet.Values.MaskUv[i * 4 + 2] = u.Z;
            packet.Values.MaskUv[i * 4 + 3] = u.W;
            var ch = i < maskChannel.Length ? maskChannel[i] : 3f;
            packet.Values.MaskChannel[i * 4 + 0] = ch;
            packet.Values.MaskChannel[i * 4 + 1] = i < maskTexSel.Length ? maskTexSel[i] : 0f;
            packet.Values.MaskChannel[i * 4 + 2] = i < maskAlpha.Length ? maskAlpha[i] : 1f;
            packet.Values.MaskChannel[i * 4 + 3] = i < maskEdge.Length ? maskEdge[i] : 0f;
        }
        var handle = GCHandle.Alloc(packet);
        try
        {
            var token = GCHandle.ToIntPtr(handle);
            drawList.AddCallback(BeginCallback, (void*)token);
            return token;
        }
        catch (Exception ex)
        {
            handle.Free();
            depthView->Release();
            if (bake != null)
                bake->Release();
            if (bake2 != null)
                bake2->Release();
            if (bake3 != null)
                bake3->Release();
            if (bake4 != null)
                bake4->Release();
            if (bake5 != null)
                bake5->Release();
            if (bake6 != null)
                bake6->Release();
            if (bake7 != null)
                bake7->Release();
            if (bake8 != null)
                bake8->Release();
            _logger.LogWarning(ex, "[Nameplate] GPU mask callback could not be queued");
            Status = "queueFailed";
            return 0;
        }
    }

    /// <summary>Restores ordinary ImGui rendering after the icon and label.</summary>
    public static void End(ImDrawListPtr drawList, nint token)
    {
        if (token != 0)
            drawList.AddCallback(EndCallback, (void*)token);
    }

    private static void BeginDraw(ImDrawList* list, ImDrawCmd* command)
    {
        var packet = (Packet)GCHandle.FromIntPtr((nint)command->UserCallbackData).Target!;
        var owner = packet.Owner;
        if (owner._disposed)
            return;
        try
        {
            var context = owner._context;
            var shaderCount = 0u;
            ID3D11PixelShader* oldShader = null;
            var oldViews = stackalloc ID3D11ShaderResourceView*[9];
            ID3D11Buffer* oldConstants = null;
            context->PSGetShader(&oldShader, null, &shaderCount);
            context->PSGetShaderResources(1, 9, oldViews);
            context->PSGetConstantBuffers(0, 1, &oldConstants);
            packet.PreviousShader = oldShader;
            packet.PreviousDepth = oldViews[0];
            packet.PreviousBake = oldViews[1];
            packet.PreviousBake2 = oldViews[2];
            packet.PreviousBake3 = oldViews[3];
            packet.PreviousBake4 = oldViews[4];
            packet.PreviousBake5 = oldViews[5];
            packet.PreviousBake6 = oldViews[6];
            packet.PreviousBake7 = oldViews[7];
            packet.PreviousBake8 = oldViews[8];
            packet.PreviousConstants = oldConstants;
            packet.Bound = true;
            var values = packet.Values;
            context->UpdateSubresource((ID3D11Resource*)packet.Constants, 0, null, &values, 0, 0);
            context->PSSetShader(owner._shader, null, 0);
            var views = stackalloc ID3D11ShaderResourceView*[9];
            views[0] = packet.Depth;
            views[1] = packet.Bake;
            views[2] = packet.Bake2;
            views[3] = packet.Bake3;
            views[4] = packet.Bake4;
            views[5] = packet.Bake5;
            views[6] = packet.Bake6;
            views[7] = packet.Bake7;
            views[8] = packet.Bake8;
            var constants = packet.Constants;
            context->PSSetShaderResources(1, 9, views);
            context->PSSetConstantBuffers(0, 1, &constants);
            owner.Status = "gpu";
        }
        catch (Exception ex)
        {
            owner.Fail($"callback:{ex.Message}");
        }
    }

    private static void EndDraw(ImDrawList* list, ImDrawCmd* command)
    {
        var handle = GCHandle.FromIntPtr((nint)command->UserCallbackData);
        var packet = (Packet)handle.Target!;
        try
        {
            var context = packet.Owner._context;
            if (packet.Bound && context != null)
            {
                context->PSSetShader(packet.PreviousShader, null, 0);
                var views = stackalloc ID3D11ShaderResourceView*[9];
                views[0] = packet.PreviousDepth;
                views[1] = packet.PreviousBake;
                views[2] = packet.PreviousBake2;
                views[3] = packet.PreviousBake3;
                views[4] = packet.PreviousBake4;
                views[5] = packet.PreviousBake5;
                views[6] = packet.PreviousBake6;
                views[7] = packet.PreviousBake7;
                views[8] = packet.PreviousBake8;
                var constants = packet.PreviousConstants;
                context->PSSetShaderResources(1, 9, views);
                context->PSSetConstantBuffers(0, 1, &constants);
            }
        }
        catch (Exception ex)
        {
            packet.Owner.Fail($"restore:{ex.Message}");
        }
        finally
        {
            if (packet.PreviousShader != null) packet.PreviousShader->Release();
            if (packet.PreviousDepth != null) packet.PreviousDepth->Release();
            if (packet.PreviousBake != null) packet.PreviousBake->Release();
            if (packet.PreviousBake2 != null) packet.PreviousBake2->Release();
            if (packet.PreviousBake3 != null) packet.PreviousBake3->Release();
            if (packet.PreviousBake4 != null) packet.PreviousBake4->Release();
            if (packet.PreviousBake5 != null) packet.PreviousBake5->Release();
            if (packet.PreviousBake6 != null) packet.PreviousBake6->Release();
            if (packet.PreviousBake7 != null) packet.PreviousBake7->Release();
            if (packet.PreviousBake8 != null) packet.PreviousBake8->Release();
            if (packet.PreviousConstants != null) packet.PreviousConstants->Release();
            packet.Depth->Release();
            if (packet.Bake != null) packet.Bake->Release();
            if (packet.Bake2 != null) packet.Bake2->Release();
            if (packet.Bake3 != null) packet.Bake3->Release();
            if (packet.Bake4 != null) packet.Bake4->Release();
            if (packet.Bake5 != null) packet.Bake5->Release();
            if (packet.Bake6 != null) packet.Bake6->Release();
            if (packet.Bake7 != null) packet.Bake7->Release();
            if (packet.Bake8 != null) packet.Bake8->Release();
            handle.Free();
        }
    }

    private bool EnsureShader()
    {
        if (_shader != null)
            return true;
        var kernel = GameDevice.Instance();
        var kernelContext = kernel != null ? (ID3D11DeviceContext*)kernel->D3D11DeviceContext : null;
        if (kernelContext == null)
        {
            Status = "noContext";
            return false;
        }
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
        GpuNameplateMaskShaders.Prewarm(_shaderCacheDir);
        switch (GpuNameplateMaskShaders.Poll(out var bytecode, out var error))
        {
            case GpuNameplateMaskShaders.CompilePoll.Pending:
                Status = "compiling";
                return false;
            case GpuNameplateMaskShaders.CompilePoll.Failed:
                return Fail($"pixelShader:{error}");
        }
        if (bytecode == null || bytecode.Length == 0)
            return Fail("emptyBytecode");
        ID3D11PixelShader* shader = null;
        int created;
        fixed (byte* bytecodePtr = bytecode)
        {
            created = (int)_device->CreatePixelShader(bytecodePtr,
                (nuint)bytecode.Length, null, &shader);
        }
        if (created < 0 || shader == null)
        {
            if (GpuNameplateMaskShaders.LastFromCache)
            {
                // cached blob rejected - drop it; next frame recompiles once
                GpuNameplateMaskShaders.InvalidateCache();
                Status = "compiling";
                return false;
            }
            return Fail("createShader");
        }
        _shader = shader;
        _logger.LogDebug("[Nameplate] pixel shader ready ({cache}queue {queue}ms, compile {compile}ms)",
            GpuNameplateMaskShaders.LastCacheKind != null
                ? $"cached({GpuNameplateMaskShaders.LastCacheKind} {GpuNameplateMaskShaders.LastCachePath}), "
                : string.Empty,
            GpuNameplateMaskShaders.LastQueueMs, GpuNameplateMaskShaders.LastCompileMs);
        return true;
    }

    private bool Fail(string reason)
    {
        if (!_failed)
            _logger.LogWarning("[Nameplate] GPU depth mask unavailable: {reason}", reason);
        _failed = true;
        Status = reason;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _depth.Dispose();
        for (var i = 0; i < _buffers.Length; i++)
        {
            var buffer = (ID3D11Buffer*)_buffers[i];
            if (buffer != null)
            {
                buffer->Release();
                _buffers[i] = 0;
            }
        }
        if (_shader != null) { _shader->Release(); _shader = null; }
        if (_device != null) { _device->Release(); _device = null; }
        if (_context != null) { _context->Release(); _context = null; }
    }
}
