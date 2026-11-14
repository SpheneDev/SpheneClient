using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Sphene.Services;

/// <summary>
/// Grabs a display-space rectangle out of the game's back buffer through the
/// swap chain's own ID3D11 objects: the region is copied into a one-off
/// staging texture and read back synchronously. Must run on the render thread
/// (a UiBuilder.Draw callback) - the copy then lands before this frame's
/// ImGui submission, so the capture is the clean scene without plugin chrome.
/// Unsupported states (missing device, unknown back buffer format) return
/// false and leave the caller to fail gracefully.
/// </summary>
public sealed unsafe class ScreenCaptureService
{
    private const int KindBgra = 0;
    private const int KindRgba = 1;
    private const int KindR10G10B10A2 = 2;
    private const int KindR16G16B16A16Float = 3;

    private readonly ILogger<ScreenCaptureService> _logger;

    public ScreenCaptureService(ILogger<ScreenCaptureService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Copies the pixels inside the absolute main-viewport rect [min, max)
    /// into a freshly allocated RGBA32 buffer. Coordinates are the same
    /// screen-space values ImGui reports (viewport.Pos + offset); they are
    /// normalized and scaled when the back buffer resolution differs from the
    /// display resolution. Returns false when the region is empty or capture
    /// is unavailable.
    /// </summary>
    public bool TryCapture(Vector2 displayMin, Vector2 displayMax,
        out byte[] rgbaPixels, out int width, out int height)
    {
        rgbaPixels = [];
        width = 0;
        height = 0;

        ID3D11Texture2D* backBuffer = null;
        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        ID3D11Texture2D* staging = null;
        try
        {
            var kernelDevice = KernelDevice.Instance();
            var swapChain = kernelDevice != null && kernelDevice->SwapChain != null
                ? (IDXGISwapChain*)kernelDevice->SwapChain->DXGISwapChain
                : null;
            if (swapChain == null)
            {
                return false;
            }

            var iid = TerraFX.Interop.Windows.IID.IID_ID3D11Texture2D;
            void* surface = null;
            if (swapChain->GetBuffer(0, &iid, &surface) < 0 || surface == null)
            {
                return false;
            }
            backBuffer = (ID3D11Texture2D*)surface;

            D3D11_TEXTURE2D_DESC desc;
            backBuffer->GetDesc(&desc);
            if (!TryFormatInfo(desc.Format, out var kind, out var bytesPerTexel))
            {
                _logger.LogDebug("[Capture] unsupported back buffer format {Format}",
                    (int)desc.Format);
                return false;
            }

            var viewport = ImGui.GetMainViewport();
            var relMin = displayMin - viewport.Pos;
            var relMax = displayMax - viewport.Pos;
            var sx = desc.Width / MathF.Max(1f, viewport.Size.X);
            var sy = desc.Height / MathF.Max(1f, viewport.Size.Y);
            var left = Math.Clamp((int)MathF.Round(relMin.X * sx), 0, (int)desc.Width);
            var top = Math.Clamp((int)MathF.Round(relMin.Y * sy), 0, (int)desc.Height);
            var right = Math.Clamp((int)MathF.Round(relMax.X * sx), 0, (int)desc.Width);
            var bottom = Math.Clamp((int)MathF.Round(relMax.Y * sy), 0, (int)desc.Height);
            var regionW = right - left;
            var regionH = bottom - top;
            if (regionW <= 0 || regionH <= 0)
            {
                return false;
            }

            backBuffer->GetDevice(&device);
            if (device == null)
            {
                return false;
            }
            device->GetImmediateContext(&context);
            if (context == null)
            {
                return false;
            }

            var stagingDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)regionW,
                Height = (uint)regionH,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                MiscFlags = 0,
            };
            if (device->CreateTexture2D(&stagingDesc, null, &staging) < 0 || staging == null)
            {
                _logger.LogWarning("[Capture] staging allocation failed");
                return false;
            }

            var box = new D3D11_BOX
            {
                left = (uint)left,
                top = (uint)top,
                front = 0,
                right = (uint)right,
                bottom = (uint)bottom,
                back = 1,
            };
            context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0,
                (ID3D11Resource*)backBuffer, 0, &box);

            D3D11_MAPPED_SUBRESOURCE mapped;
            if (context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ,
                    0, &mapped) < 0 || mapped.pData == null)
            {
                return false;
            }
            try
            {
                rgbaPixels = ReadPixels(mapped, regionW, regionH, kind, bytesPerTexel);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
            width = regionW;
            height = regionH;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Capture] screen region capture failed");
            return false;
        }
        finally
        {
            if (staging != null) staging->Release();
            if (context != null) context->Release();
            if (device != null) device->Release();
            if (backBuffer != null) backBuffer->Release();
        }
    }

    private static byte[] ReadPixels(D3D11_MAPPED_SUBRESOURCE mapped, int w, int h,
        int kind, int bytesPerTexel)
    {
        var result = new byte[w * h * 4];
        var rowBuffer = new byte[w * bytesPerTexel];
        for (var y = 0; y < h; y++)
        {
            Marshal.Copy(nint.Add((nint)mapped.pData, y * (int)mapped.RowPitch),
                rowBuffer, 0, rowBuffer.Length);
            var dst = y * w * 4;
            switch (kind)
            {
                case KindBgra:
                    for (var x = 0; x < w; x++)
                    {
                        var s = x * 4;
                        result[dst + s + 0] = rowBuffer[s + 2];
                        result[dst + s + 1] = rowBuffer[s + 1];
                        result[dst + s + 2] = rowBuffer[s + 0];
                        result[dst + s + 3] = 255;
                    }
                    break;
                case KindRgba:
                    for (var x = 0; x < w; x++)
                    {
                        var s = x * 4;
                        result[dst + s + 0] = rowBuffer[s + 0];
                        result[dst + s + 1] = rowBuffer[s + 1];
                        result[dst + s + 2] = rowBuffer[s + 2];
                        result[dst + s + 3] = 255;
                    }
                    break;
                case KindR10G10B10A2:
                    for (var x = 0; x < w; x++)
                    {
                        var packed = BitConverter.ToUInt32(rowBuffer, x * 4);
                        result[dst + x * 4 + 0] = (byte)((packed & 0x3FFu) * 255u / 1023u);
                        result[dst + x * 4 + 1] = (byte)(((packed >> 10) & 0x3FFu) * 255u / 1023u);
                        result[dst + x * 4 + 2] = (byte)(((packed >> 20) & 0x3FFu) * 255u / 1023u);
                        result[dst + x * 4 + 3] = 255;
                    }
                    break;
                default: // R16G16B16A16_FLOAT
                    for (var x = 0; x < w; x++)
                    {
                        var s = x * 8;
                        result[dst + x * 4 + 0] = HalfToByte(BitConverter.ToUInt16(rowBuffer, s + 0));
                        result[dst + x * 4 + 1] = HalfToByte(BitConverter.ToUInt16(rowBuffer, s + 2));
                        result[dst + x * 4 + 2] = HalfToByte(BitConverter.ToUInt16(rowBuffer, s + 4));
                        result[dst + x * 4 + 3] = 255;
                    }
                    break;
            }
        }
        return result;
    }

    private static byte HalfToByte(ushort bits)
        => (byte)Math.Clamp((int)(255f * (float)BitConverter.UInt16BitsToHalf(bits)), 0, 255);

    private static bool TryFormatInfo(DXGI_FORMAT format, out int kind, out int bytesPerTexel)
    {
        switch (format)
        {
            case DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
                kind = KindBgra;
                bytesPerTexel = 4;
                return true;
            case DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                kind = KindRgba;
                bytesPerTexel = 4;
                return true;
            case DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM:
                kind = KindR10G10B10A2;
                bytesPerTexel = 4;
                return true;
            case DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT:
                kind = KindR16G16B16A16Float;
                bytesPerTexel = 8;
                return true;
            default:
                kind = KindBgra;
                bytesPerTexel = 4;
                return false;
        }
    }
}
