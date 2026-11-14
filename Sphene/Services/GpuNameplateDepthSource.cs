using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;

namespace Sphene.Services;

/// <summary>Holds a typed shader view of the game's current scene-depth texture.</summary>
internal sealed unsafe class GpuNameplateDepthSource : IDisposable
{
    private ID3D11ShaderResourceView* _view;
    private ID3D11Texture2D* _copy;
    private nint _textureIdentity;
    private bool _disposed;

    /// <summary>Copies the game's depth into a private texture and returns a view
    /// on the copy, so the shader never samples a resource the game may still
    /// have bound as depth-stencil - that hazard can kill the device.</summary>
    public bool TryGet(ID3D11Device* device, ID3D11DeviceContext* context,
        out ID3D11ShaderResourceView* view, out uint width, out uint height)
    {
        view = null;
        width = 0;
        height = 0;
        if (_disposed || device == null || context == null)
            return false;
        var target = RenderTargetManager.Instance();
        var depth = target != null ? target->DepthStencil : null;
        var texture = depth != null ? (ID3D11Texture2D*)depth->D3D11Texture2D : null;
        if (texture == null)
        {
            ReleaseView();
            return false;
        }

        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        if (desc.SampleDesc.Count != 1 || desc.Width == 0 || desc.Height == 0)
            return false;
        var format = desc.Format switch
        {
            DXGI_FORMAT.DXGI_FORMAT_R24G8_TYPELESS or
                DXGI_FORMAT.DXGI_FORMAT_D24_UNORM_S8_UINT or
                DXGI_FORMAT.DXGI_FORMAT_R24_UNORM_X8_TYPELESS
                => DXGI_FORMAT.DXGI_FORMAT_R24_UNORM_X8_TYPELESS,
            DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS or
                DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT or
                DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT
                => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
            _ => DXGI_FORMAT.DXGI_FORMAT_UNKNOWN
        };
        if (format == DXGI_FORMAT.DXGI_FORMAT_UNKNOWN)
            return false;
        if (_view == null || _textureIdentity != (nint)texture)
        {
            ReleaseView();
            var copyDesc = desc;
            copyDesc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
            copyDesc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
            copyDesc.CPUAccessFlags = 0;
            copyDesc.MiscFlags = 0;
            ID3D11Texture2D* copy = null;
            if ((int)device->CreateTexture2D(&copyDesc, null, &copy) < 0 || copy == null)
                return false;
            var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = format,
                ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D
            };
            srvDesc.Texture2D.MostDetailedMip = 0;
            srvDesc.Texture2D.MipLevels = 1;
            ID3D11ShaderResourceView* newView = null;
            if ((int)device->CreateShaderResourceView((ID3D11Resource*)copy, &srvDesc, &newView) < 0
                || newView == null)
            {
                copy->Release();
                return false;
            }
            _copy = copy;
            _view = newView;
            _textureIdentity = (nint)texture;
        }
        context->CopyResource((ID3D11Resource*)_copy, (ID3D11Resource*)texture);
        width = Math.Min(depth->ActualWidth > 0 ? depth->ActualWidth : desc.Width, desc.Width);
        height = Math.Min(depth->ActualHeight > 0 ? depth->ActualHeight : desc.Height, desc.Height);
        view = _view;
        return width > 0 && height > 0;
    }

    private void ReleaseView()
    {
        if (_view != null)
        {
            _view->Release();
            _view = null;
        }
        if (_copy != null)
        {
            _copy->Release();
            _copy = null;
        }
        _textureIdentity = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseView();
    }
}
