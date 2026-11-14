using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Sphene.Services;

/// <summary>
/// Reads back a small screen-space region of the game's scene depth buffer via
/// the game's own ID3D11DeviceContext. CopySubresourceRegion is issued into a
/// ping-pong pair of staging textures; the Map() always targets the texture
/// filled during the previous frame, so the read never stalls the GPU
/// (one frame of depth latency). Any unsupported state (missing device,
/// MSAA depth buffer, unknown format, staging allocation failure) disables the
/// sampler and callers fall back to the CPU raycast path.
/// </summary>
internal sealed unsafe class SceneDepthSampler : IDisposable
{
    private const int StagingBucket = 64;
    private const int MaxStaleFrames = 10;

    private const int KindFloat32 = 0;
    private const int KindUnorm24 = 1;
    private const int KindUnorm16 = 2;

    private readonly ILogger _logger;
    private readonly int[] _pendingW = new int[2];
    private readonly int[] _pendingH = new int[2];
    private readonly int[] _pendingX = new int[2];
    private readonly int[] _pendingY = new int[2];
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Texture2D* _src;
    private ID3D11Texture2D* _staging0;
    private ID3D11Texture2D* _staging1;
    private DXGI_FORMAT _srcFormat = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN;
    private uint _stagingW;
    private uint _stagingH;
    private int _parity;
    private byte[] _depth = [];
    private int _depthStride;
    private int _depthW;
    private int _depthH;
    private int _depthOriginX;
    private int _depthOriginY;
    private uint _bufW;
    private uint _bufH;
    private int _formatKind;
    private int _bytesPerTexel;
    private Vector2 _displaySize;
    private int _staleFrames;
    private bool _initFailed;
    private bool _pendingValid;
    private bool _disposed;
    private bool _loggedReady;
    private bool _loggedFailure;

    public SceneDepthSampler(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>True while a mapped depth frame is available and fresh enough
    /// to be trusted for occlusion tests.</summary>
    public bool IsActive => _depth.Length > 0 && _staleFrames <= MaxStaleFrames;

    /// <summary>Short status token for the debug overlay (e.g. "msaa4").</summary>
    public string? Status { get; private set; }

    /// <summary>Diagnostic: sampled depth buffer width.</summary>
    public uint BufW => _bufW;

    /// <summary>Diagnostic: sampled depth buffer height.</summary>
    public uint BufH => _bufH;

    /// <summary>Diagnostic: source depth format.</summary>
    public int Format => (int)_srcFormat;

    /// <summary>
    /// Per-frame entry point on the render/draw thread. Resolves the scene
    /// depth texture, maps the staging copy issued last frame into managed
    /// memory and issues this frame's copy for the given screen-space region.
    /// </summary>
    public void BeginFrame(float x0, float y0, float x1, float y1)
    {
        if (_disposed || _initFailed)
        {
            _depth = [];
            return;
        }
        _displaySize = ImGui.GetIO().DisplaySize;
        if (!EnsureDevice() || !ResolveDepthSource())
        {
            _staleFrames++;
            return;
        }
        if (_displaySize.X < 1f || _displaySize.Y < 1f)
        {
            _staleFrames++;
            return;
        }

        var sx = _bufW / _displaySize.X;
        var sy = _bufH / _displaySize.Y;
        var rx = Math.Clamp((int)MathF.Floor(x0 * sx), 0, (int)_bufW - 1);
        var ry = Math.Clamp((int)MathF.Floor(y0 * sy), 0, (int)_bufH - 1);
        var rw = Math.Clamp((int)MathF.Ceiling(x1 * sx) - rx, 0, (int)_bufW - rx);
        var rh = Math.Clamp((int)MathF.Ceiling(y1 * sy) - ry, 0, (int)_bufH - ry);
        if (rw <= 0 || rh <= 0)
        {
            _staleFrames++;
            return;
        }
        if (!EnsureStaging((uint)rw, (uint)rh))
            return;

        ReadPrevious();
        IssueCopy(rx, ry, rw, rh);
        _parity ^= 1;
        if (!_loggedReady && IsActive)
        {
            _loggedReady = true;
            _logger.LogInformation("[Nameplate] Depth occlusion active: {w}x{h} fmt={fmt}", _bufW, _bufH, (int)_srcFormat);
        }
    }

    /// <summary>Returns the normalized scene depth (0..1, convention as stored
    /// in the buffer) at a display-space pixel, or false when the position lies
    /// outside the last readback region.</summary>
    public bool TrySampleDepth(float screenX, float screenY, out float depth)
    {
        depth = 0f;
        if (!IsActive)
            return false;
        var bx = (int)(screenX / _displaySize.X * _bufW) - _depthOriginX;
        var by = (int)(screenY / _displaySize.Y * _bufH) - _depthOriginY;
        if ((uint)bx >= (uint)_depthW || (uint)by >= (uint)_depthH)
            return false;
        depth = DecodeTexel(by * _depthStride + bx * _bytesPerTexel);
        return true;
    }

    /// <summary>Checks whether the captured region contains depth other than the
    /// far-plane clear value. An entirely cleared region cannot occlude icons.</summary>
    public bool HasSceneDepth(bool standardZ)
    {
        if (!IsActive)
            return false;
        for (var y = 0; y < _depthH; y += 8)
        {
            for (var x = 0; x < _depthW; x += 8)
            {
                var value = DecodeTexel(y * _depthStride + x * _bytesPerTexel);
                if (float.IsFinite(value) && (standardZ ? value < 0.99999f : value > 0.00001f))
                    return true;
            }
        }
        return false;
    }

    private float DecodeTexel(int offset)
    {
        return _formatKind switch
        {
            KindFloat32 => BitConverter.ToSingle(_depth, offset),
            KindUnorm24 => (BitConverter.ToUInt32(_depth, offset) & 0x00FFFFFF) * (1f / 16777215f),
            KindUnorm16 => BitConverter.ToUInt16(_depth, offset) * (1f / 65535f),
            _ => 0f
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseStaging();
        if (_device != null)
        {
            _device->Release();
            _device = null;
        }
        _context = null;
        _src = null;
        _depth = [];
    }

    private bool EnsureDevice()
    {
        if (_context != null && _device != null)
            return true;
        var kernelDevice = KernelDevice.Instance();
        var ctx = kernelDevice != null ? (ID3D11DeviceContext*)kernelDevice->D3D11DeviceContext : null;
        if (ctx == null)
        {
            Status = "noContext";
            return false;
        }
        ID3D11Device* dev = null;
        ctx->GetDevice(&dev);
        if (dev == null)
        {
            Status = "noDevice";
            return false;
        }
        _context = ctx;
        _device = dev;
        return true;
    }

    private bool ResolveDepthSource()
    {
        var rtm = RenderTargetManager.Instance();
        var tex = rtm != null ? rtm->DepthStencil : null;
        var d3dTex = tex != null ? (ID3D11Texture2D*)tex->D3D11Texture2D : null;
        if (d3dTex == null)
        {
            Status = "noDepthTex";
            return false;
        }
        D3D11_TEXTURE2D_DESC desc;
        d3dTex->GetDesc(&desc);
        if (desc.SampleDesc.Count > 1)
        {
            Status = $"msaa{desc.SampleDesc.Count}";
            return false;
        }
        if (!TryFormatInfo(desc.Format, out _formatKind, out _bytesPerTexel))
        {
            Status = $"fmt{(int)desc.Format}";
            return false;
        }
        _srcFormat = desc.Format;
        _bufW = desc.Width;
        _bufH = desc.Height;
        _src = d3dTex;
        return true;
    }

    private bool EnsureStaging(uint w, uint h)
    {
        var bw = Align(w, StagingBucket);
        var bh = Align(h, StagingBucket);
        if (bw <= _stagingW && bh <= _stagingH && _staging0 != null)
            return true;
        ReleaseStaging();
        _pendingValid = false;
        _stagingW = bw;
        _stagingH = bh;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = bw,
            Height = bh,
            MipLevels = 1,
            ArraySize = 1,
            Format = _srcFormat,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };
        ID3D11Texture2D* t0 = null;
        ID3D11Texture2D* t1 = null;
        var hr0 = _device->CreateTexture2D(&desc, null, &t0);
        var hr1 = _device->CreateTexture2D(&desc, null, &t1);
        if ((int)hr0 < 0 || (int)hr1 < 0 || t0 == null || t1 == null)
        {
            if (t0 != null) t0->Release();
            if (t1 != null) t1->Release();
            _initFailed = true;
            Status = "stagingFail";
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _logger.LogWarning("[Nameplate] Depth staging allocation failed (hr0={h0:X8} hr1={h1:X8} fmt={fmt}) - depth occlusion disabled",
                    (int)hr0, (int)hr1, (int)_srcFormat);
            }
            return false;
        }
        _staging0 = t0;
        _staging1 = t1;
        return true;
    }

    private void IssueCopy(int x, int y, int w, int h)
    {
        var dst = _parity == 0 ? _staging0 : _staging1;
        var box = new D3D11_BOX
        {
            left = (uint)x,
            top = (uint)y,
            front = 0,
            right = (uint)(x + w),
            bottom = (uint)(y + h),
            back = 1
        };
        _context->CopySubresourceRegion((ID3D11Resource*)dst, 0, 0, 0, 0, (ID3D11Resource*)_src, 0, &box);
        var p = _parity;
        _pendingX[p] = x;
        _pendingY[p] = y;
        _pendingW[p] = w;
        _pendingH[p] = h;
        _pendingValid = true;
    }

    private void ReadPrevious()
    {
        var prev = _parity ^ 1;
        if (!_pendingValid)
            return;
        var staging = prev == 0 ? _staging0 : _staging1;
        var w = _pendingW[prev];
        var h = _pendingH[prev];
        D3D11_MAPPED_SUBRESOURCE mapped;
        var hr = _context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ,
            (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if ((int)hr == DXGI.DXGI_ERROR_WAS_STILL_DRAWING)
        {
            _staleFrames++;
            Status = "busy";
            return;
        }
        if ((int)hr < 0 || mapped.pData == null)
        {
            _staleFrames++;
            Status = $"map{(int)hr:X8}";
            return;
        }
        var stride = w * _bytesPerTexel;
        var needed = h * stride;
        if (_depth.Length != needed)
            _depth = new byte[needed];
        var src = (byte*)mapped.pData;
        for (var y = 0; y < h; y++)
            Marshal.Copy((nint)(src + (nuint)y * mapped.RowPitch), _depth, y * stride, stride);
        _context->Unmap((ID3D11Resource*)staging, 0);
        _depthStride = stride;
        _depthW = w;
        _depthH = h;
        _depthOriginX = _pendingX[prev];
        _depthOriginY = _pendingY[prev];
        _staleFrames = 0;
        Status = null;
    }

    private void ReleaseStaging()
    {
        if (_staging0 != null)
        {
            _staging0->Release();
            _staging0 = null;
        }
        if (_staging1 != null)
        {
            _staging1->Release();
            _staging1 = null;
        }
        _stagingW = 0;
        _stagingH = 0;
    }

    private static uint Align(uint v, uint bucket) => (v + bucket - 1) / bucket * bucket;

    private static bool TryFormatInfo(DXGI_FORMAT format, out int kind, out int bytesPerTexel)
    {
        kind = KindFloat32;
        bytesPerTexel = 4;
        switch (format)
        {
            case DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT:
            case DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS:
            case DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT:
                return true;
            case DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS:
            case DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
            case DXGI_FORMAT.DXGI_FORMAT_X32_TYPELESS_G8X24_UINT:
                bytesPerTexel = 8;
                return true;
            case DXGI_FORMAT.DXGI_FORMAT_D24_UNORM_S8_UINT:
            case DXGI_FORMAT.DXGI_FORMAT_R24G8_TYPELESS:
            case DXGI_FORMAT.DXGI_FORMAT_R24_UNORM_X8_TYPELESS:
            case DXGI_FORMAT.DXGI_FORMAT_X24_TYPELESS_G8_UINT:
                kind = KindUnorm24;
                return true;
            case DXGI_FORMAT.DXGI_FORMAT_D16_UNORM:
            case DXGI_FORMAT.DXGI_FORMAT_R16_UNORM:
            case DXGI_FORMAT.DXGI_FORMAT_R16_TYPELESS:
                kind = KindUnorm16;
                bytesPerTexel = 2;
                return true;
            default:
                return false;
        }
    }
}
