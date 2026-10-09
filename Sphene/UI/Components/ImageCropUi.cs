using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Interactive image-crop window: loads a picked image, lets the user drag an
/// aspect-locked selection (profile picture 1:1, profile banner 3:1), then
/// crops, resizes and PNG-encodes locally — only the cropped result is uploaded.
/// </summary>
public sealed class ImageCropUi : WindowMediatorSubscriberBase
{
    /// <summary>Which profile image is being cropped; fixes aspect + output limits.</summary>
    public enum CropKind
    {
        Avatar,
        Banner,
        RowImage,
    }

    private const string TextureKey = "imagecrop:source";
    private const float HandleHitPixels = 12f;
    private const float MinSourcePixels = 32f;

    private enum DragMode
    {
        None,
        Move,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private byte[] _imageBytes = [];
    private int _imageWidth;
    private int _imageHeight;
    private CropKind _kind;
    private float _aspect = 1f;
    private int _targetWidth;
    private int _maxBytes;
    private Vector4 _crop; // normalized x, y, w, h over the image
    private Action<byte[]>? _onConfirm;
    private byte[]? _pendingResult;
    private bool _cropFinished;
    private bool _cropFailed;
    private bool _applyBusy;
    private DragMode _drag = DragMode.None;
    private Vector2 _grabOffset;

    public ImageCropUi(ILogger<ImageCropUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Crop Image###SpheneImageCropUI", performanceCollectorService)
    {
        IsOpen = false;
        Size = new Vector2(720, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(1600, 1200),
        };
    }

    /// <summary>
    /// Opens the cropper for already-loaded PNG bytes. <paramref name="onConfirm"/>
    /// receives the cropped PNG on the UI thread. Safe to call off-thread.
    /// </summary>
    public void Open(CropKind kind, byte[] imageBytes, int imageWidth, int imageHeight,
        Action<byte[]> onConfirm)
    {
        (_aspect, _targetWidth, _maxBytes, WindowName) = kind switch
        {
            CropKind.Avatar => (1f, 256, 250 * 1024, "Crop profile picture###SpheneImageCropUI"),
            CropKind.Banner => (3f, 1200, 400 * 1024, "Crop profile banner###SpheneImageCropUI"),
            // stored strip is exactly the row-visible area (max 512×128)
            _ => (SpheneUI.Profile.RowImageAspect, 512, 400 * 1024,
                "Crop list row image###SpheneImageCropUI"),
        };
        _kind = kind;
        _imageBytes = imageBytes;
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        _onConfirm = onConfirm;
        _pendingResult = null;
        _cropFinished = false;
        _cropFailed = false;
        _applyBusy = false;
        _drag = DragMode.None;

        // largest centered rect of the target aspect
        var imageAspect = imageWidth / (float)imageHeight;
        var cw = imageAspect >= _aspect ? _aspect / imageAspect : 1f;
        var ch = imageAspect >= _aspect ? 1f : imageAspect / _aspect;
        _crop = new Vector4((1f - cw) * 0.5f, (1f - ch) * 0.5f, cw, ch);

        SpheneUI.Media.Evict(TextureKey);
        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        if (_cropFinished)
        {
            _cropFinished = false;
            var result = _pendingResult;
            _pendingResult = null;
            if (result != null)
            {
                IsOpen = false;
                _onConfirm?.Invoke(result);
                return;
            }
            _applyBusy = false;
            _cropFailed = true;
        }

        if (_cropFailed)
        {
            SpheneUI.Layout.Callout("The image could not be processed — pick a different file.",
                FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
        }

        var wrap = SpheneUI.Media.Texture(TextureKey, _imageBytes);
        if (wrap == null)
        {
            SpheneUI.Text.Muted("Loading image…");
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        var canvas = new Vector2(avail.X, MathF.Max(100f, avail.Y - footerHeight));

        var imageAspect = _imageWidth / (float)_imageHeight;
        var dispW = canvas.X;
        var dispH = dispW / imageAspect;
        if (dispH > canvas.Y)
        {
            dispH = canvas.Y;
            dispW = dispH * imageAspect;
        }
        var dispSize = new Vector2(dispW, dispH);
        var imgMin = ImGui.GetCursorScreenPos() + new Vector2((canvas.X - dispW) * 0.5f, 0f);
        var imgMax = imgMin + dispSize;

        ImGui.SetCursorScreenPos(imgMin);
        ImGui.InvisibleButton("##cropzone", dispSize);
        HandleCropInteraction(imgMin, dispSize);

        var drawList = ImGui.GetWindowDrawList();
        SpheneUI.Media.ImageInRect(drawList, wrap, imgMin, imgMax, ImageFit.Fit);
        DrawCropOverlay(drawList, imgMin, dispSize);

        ImGui.SetCursorScreenPos(imgMin + new Vector2(0f, dispH));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        SpheneUI.Text.Muted("Drag inside the selection to move it, drag the corners to resize.");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 170f * SpheneUI.Scale);
        if (SpheneUI.Controls.Button(_applyBusy ? "Applying…" : "Apply crop") && !_applyBusy)
        {
            _applyBusy = true;
            _cropFailed = false;
            var (bytes, crop, targetW, aspect, maxBytes) =
                (_imageBytes, _crop, _targetWidth, _aspect, _maxBytes);
            _ = Task.Run(() =>
                {
                    _pendingResult = RenderCrop(bytes, crop, targetW, aspect, maxBytes);
                    _cropFinished = true;
                })
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _applyBusy = false;
                        _cropFinished = false;
                        _cropFailed = true;
                        _logger.LogWarning(t.Exception, "Failed to render image crop");
                    }
                });
        }
        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Cancel"))
        {
            IsOpen = false;
        }
    }

    private void HandleCropInteraction(Vector2 imgMin, Vector2 dispSize)
    {
        var mouse = ImGui.GetIO().MousePos;
        var m = (mouse - imgMin) / dispSize;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(HitTest(m, dispSize) switch
            {
                DragMode.Move => ImGuiMouseCursor.Hand,
                DragMode.TopLeft or DragMode.BottomRight => ImGuiMouseCursor.ResizeNwse,
                DragMode.TopRight or DragMode.BottomLeft => ImGuiMouseCursor.ResizeNesw,
                _ => ImGuiMouseCursor.Arrow,
            });
        }
        if (ImGui.IsItemActivated())
        {
            _drag = HitTest(m, dispSize);
            if (_drag == DragMode.Move)
            {
                _grabOffset = m - new Vector2(_crop.X, _crop.Y);
            }
            else if (_drag == DragMode.None)
            {
                // click outside: recenter the selection on the click point
                _crop.X = Math.Clamp(m.X - _crop.Z * 0.5f, 0f, 1f - _crop.Z);
                _crop.Y = Math.Clamp(m.Y - _crop.W * 0.5f, 0f, 1f - _crop.W);
            }
        }
        if (ImGui.IsItemDeactivated())
        {
            _drag = DragMode.None;
        }
        if (!ImGui.IsItemActive() || !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            return;
        }

        var minW = MathF.Max(20f / dispSize.X, MinSourcePixels / _imageWidth);
        if (_drag == DragMode.Move)
        {
            _crop.X = Math.Clamp(m.X - _grabOffset.X, 0f, 1f - _crop.Z);
            _crop.Y = Math.Clamp(m.Y - _grabOffset.Y, 0f, 1f - _crop.W);
            return;
        }
        if (_drag == DragMode.None)
        {
            return;
        }

        // aspect-locked corner resize anchored on the opposite corner
        var left = _drag is DragMode.TopLeft or DragMode.BottomLeft;
        var top = _drag is DragMode.TopLeft or DragMode.TopRight;
        var anchorX = left ? _crop.X + _crop.Z : _crop.X;
        var anchorY = top ? _crop.Y + _crop.W : _crop.Y;

        // normalized units: w maps to image width, h to image height
        var widthForHeight = _aspect * _imageHeight / _imageWidth;
        var wByX = MathF.Abs(m.X - anchorX);
        var wByY = MathF.Abs(m.Y - anchorY) * widthForHeight;
        var maxX = left ? anchorX : 1f - anchorX;
        var maxY = top ? anchorY : 1f - anchorY;
        var maxW = MathF.Min(maxX, maxY * widthForHeight);
        var newW = Math.Clamp(MathF.Max(wByX, wByY), MathF.Min(minW, maxW), maxW);
        var newH = newW / widthForHeight;

        _crop.X = left ? anchorX - newW : anchorX;
        _crop.Y = top ? anchorY - newH : anchorY;
        _crop.Z = newW;
        _crop.W = newH;
    }

    private DragMode HitTest(Vector2 m, Vector2 dispSize)
    {
        var handle = new Vector2(HandleHitPixels / dispSize.X, HandleHitPixels / dispSize.Y);
        var corners = new (DragMode Mode, Vector2 Pos)[]
        {
            (DragMode.TopLeft, new Vector2(_crop.X, _crop.Y)),
            (DragMode.TopRight, new Vector2(_crop.X + _crop.Z, _crop.Y)),
            (DragMode.BottomLeft, new Vector2(_crop.X, _crop.Y + _crop.W)),
            (DragMode.BottomRight, new Vector2(_crop.X + _crop.Z, _crop.Y + _crop.W)),
        };
        foreach (var (mode, pos) in corners)
        {
            if (MathF.Abs(m.X - pos.X) <= handle.X && MathF.Abs(m.Y - pos.Y) <= handle.Y)
            {
                return mode;
            }
        }
        var inside = m.X >= _crop.X && m.X <= _crop.X + _crop.Z
            && m.Y >= _crop.Y && m.Y <= _crop.Y + _crop.W;
        return inside ? DragMode.Move : DragMode.None;
    }

    private void DrawCropOverlay(ImDrawListPtr drawList, Vector2 imgMin, Vector2 dispSize)
    {
        var cropMin = imgMin + new Vector2(_crop.X * dispSize.X, _crop.Y * dispSize.Y);
        var cropMax = imgMin + new Vector2((_crop.X + _crop.Z) * dispSize.X,
            (_crop.Y + _crop.W) * dispSize.Y);
        var imgMax = imgMin + dispSize;
        var dim = SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.55f));
        var scale = SpheneUI.Scale;

        // dim everything outside the selection
        drawList.AddRectFilled(imgMin, new Vector2(imgMax.X, cropMin.Y), dim);
        drawList.AddRectFilled(new Vector2(imgMin.X, cropMax.Y), imgMax, dim);
        drawList.AddRectFilled(new Vector2(imgMin.X, cropMin.Y), new Vector2(cropMin.X, cropMax.Y), dim);
        drawList.AddRectFilled(new Vector2(cropMax.X, cropMin.Y), new Vector2(imgMax.X, cropMax.Y), dim);

        // rule-of-thirds guides
        var guide = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.18f));
        for (var i = 1; i <= 2; i++)
        {
            var tx = cropMin.X + (cropMax.X - cropMin.X) * i / 3f;
            var ty = cropMin.Y + (cropMax.Y - cropMin.Y) * i / 3f;
            drawList.AddLine(new Vector2(tx, cropMin.Y), new Vector2(tx, cropMax.Y), guide);
            drawList.AddLine(new Vector2(cropMin.X, ty), new Vector2(cropMax.X, ty), guide);
        }

        drawList.AddRect(cropMin, cropMax,
            SpheneUI.ColorUtils.ToU32(SpheneTheme.Current.AccentCyan), 0f,
            ImDrawFlags.None, MathF.Max(1.5f, 1.5f * scale));

        // row strips fade into the row background — mark the fading tail
        // inside the selection so the picked area reads as displayed
        if (_kind == CropKind.RowImage)
        {
            var fadeFrom = cropMin.X
                + (cropMax.X - cropMin.X) * SpheneUI.Profile.RowImageFadeStart;
            var transparent = SpheneUI.ColorUtils.ToU32(Vector4.Zero);
            var shade = SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.45f));
            drawList.PushClipRect(cropMin, cropMax, true);
            drawList.AddRectFilledMultiColor(new Vector2(fadeFrom, cropMin.Y),
                cropMax, transparent, shade, shade, transparent);
            var dashX = fadeFrom;
            for (var y = cropMin.Y; y < cropMax.Y - 4f * scale; y += 8f * scale)
            {
                drawList.AddLine(new Vector2(dashX, y),
                    new Vector2(dashX, MathF.Min(y + 4f * scale, cropMax.Y)),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                        SpheneTheme.Current.AccentCyan, 0.55f)), 1f);
            }
            drawList.PopClipRect();
        }

        // corner handles
        var handleSize = 5f * scale;
        var handleColor = SpheneUI.ColorUtils.ToU32(Vector4.One);
        foreach (var corner in new[]
        {
            cropMin, new Vector2(cropMax.X, cropMin.Y),
            new Vector2(cropMin.X, cropMax.Y), cropMax,
        })
        {
            drawList.AddRectFilled(corner - new Vector2(handleSize), corner + new Vector2(handleSize),
                handleColor, 2f * scale);
        }
    }

    /// <summary>
    /// Crops in source pixels, downscales to the target, retries smaller when
    /// over the byte limit. Multi-frame sources keep their animation — crop
    /// and resize apply to every frame and the result encodes as GIF.
    /// </summary>
    private static byte[]? RenderCrop(byte[] imageBytes, Vector4 crop, int targetWidth,
        float aspect, int maxBytes)
    {
        try
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            var animated = image.Frames.Count > 1;
            var px = Math.Clamp((int)MathF.Round(crop.X * image.Width), 0, image.Width - 1);
            var py = Math.Clamp((int)MathF.Round(crop.Y * image.Height), 0, image.Height - 1);
            var pw = Math.Clamp((int)MathF.Round(crop.Z * image.Width), 1, image.Width - px);
            var ph = Math.Clamp((int)MathF.Round(crop.W * image.Height), 1, image.Height - py);
            image.Mutate(x => x.Crop(new Rectangle(px, py, pw, ph)));

            var outW = Math.Min(targetWidth, pw);
            var outH = Math.Max(1, (int)MathF.Round(outW / aspect));
            while (true)
            {
                using var output = image.Clone(x => x.Resize(outW, outH));
                using var ms = new MemoryStream();
                if (animated)
                {
                    output.SaveAsGif(ms);
                }
                else
                {
                    output.SaveAsPng(ms);
                }
                var bytes = ms.ToArray();
                if (bytes.Length <= maxBytes || outW <= 96)
                {
                    return bytes;
                }
                outW /= 2;
                outH /= 2;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
