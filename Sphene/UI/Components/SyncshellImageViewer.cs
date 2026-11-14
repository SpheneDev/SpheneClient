using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using ImGui = Dalamud.Bindings.ImGui.ImGui;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using Sphene.UI.Syncshell;

namespace Sphene.UI.Components;

/// <summary>
/// Photo lightbox shared by the in-page community overlay and the standalone
/// popout window: dim backdrop, letterboxed image, side chevrons, filmstrip,
/// counter and caption. Navigation: chevrons, mouse wheel, left/right arrows.
/// </summary>
internal static class SyncshellImageViewer
{
    /// <summary>
    /// Draws the viewer inside the current window region.
    /// </summary>
    /// <param name="images">The navigable image list (album-filtered or full gallery).</param>
    /// <param name="index">Current position.</param>
    /// <param name="closeOnBackdrop">Overlay mode: a click on the dim closes the viewer.</param>
    /// <param name="onPopOut">When set, a popout affordance opens the standalone viewer window.</param>
    /// <param name="standalone">Popout window mode: the close button renders in
    /// the standard titlebar icon-button style instead of the lightbox circle.</param>
    /// <param name="openT">0..1 eased open progress · the lightbox scales and
    /// fades in from a slightly smaller photo instead of appearing instantly.</param>
    /// <param name="onLike">Fired when the like heart is clicked · the caller
    /// performs the toggle and patches the image list.</param>
    /// <param name="editOpen">The edit drawer below the caption is expanded  · 
    /// the photo shrinks to make room for the caller's controls.</param>
    /// <param name="onEditToggle">When set, a pen affordance joins the top
    /// chrome and flips <paramref name="editOpen"/>.</param>
    /// <param name="drawEditor">Renders the caller's edit controls into the
    /// drawer strip · receives the image, the strip origin and its width.</param>
    /// <param name="editHeight">Drawer height while <paramref name="editOpen"/>  · 
    /// callers pass a taller strip when their editor needs a second row.</param>
    /// <returns>False when the viewer asked to close this frame.</returns>
    public static bool Draw(IReadOnlyList<SyncshellGalleryImageDto> images, int index,
        string texturePrefix, string gid, bool closeOnBackdrop,
        Action<int> setIndex, Action onClose, Action? onPopOut, bool standalone = false,
        float openT = 1f, Action<int>? onLike = null,
        bool editOpen = false, Action? onEditToggle = null,
        Action<SyncshellGalleryImageDto, Vector2, float>? drawEditor = null,
        float? editHeight = null)
    {
        if (images.Count == 0)
        {
            return false;
        }
        index = Math.Clamp(index, 0, images.Count - 1);
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winMin = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        var winMax = winMin + winSize;
        var pad = 14f * scale;
        var open = true;

        // Open ease · cubic ease-out so the photo "lands" instead of popping.
        var ease = 1f - MathF.Pow(1f - Math.Clamp(openT, 0f, 1f), 3f);

        var img = images[index];
        var topH = ImGui.GetFrameHeight() + 12f * scale;
        var stripH = 52f * scale;
        var capH = ImGui.GetTextLineHeight() + 10f * scale;
        var showEditor = editOpen && drawEditor != null;
        var editH = showEditor ? (editHeight ?? 76f * scale) : 0f;
        var areaMin = winMin + new Vector2(pad, pad + topH);
        var areaMax = winMax - new Vector2(pad, pad + stripH + capH + editH);

        // Photo frame · the image eases from ~94% scale with a slight downward
        // drift into its letterboxed area, corners rounded like a photo card.
        var center = (areaMin + areaMax) * 0.5f;
        var grow = 0.94f + 0.06f * ease;
        var drift = (1f - ease) * 14f * scale;
        var imgMin = center + (areaMin - center) * grow + new Vector2(0f, drift);
        var imgMax = center + (areaMax - center) * grow + new Vector2(0f, drift);

        var wrap = SpheneUI.Media.Texture($"{texturePrefix}gallery:{gid}:{img.Id}",
            SyncshellCommunityView.ResolveBytes(img.ImageMediaHash, img.ImageBase64));
        var winRounding = ImGui.GetStyle().WindowRounding;

        // The window draw list clips to the content region · lift it to the
        // full window rect or every fill stops WindowPadding short of the
        // frame and the themed surface/blur peeks through the side slivers.
        ImGui.PushClipRect(winMin, winMax, false);
        try
        {
            // Backdrop · the photo's own colors bleed through cover-fitted
            // copies at rising zoom levels (defocus ghosting reads as blur),
            // dimmed under a themed veil. The base stays translucent so the
            // themed window blur behind still frosts through.
            dl.AddRectFilled(winMin, winMax, SpheneUI.ColorUtils.ToU32(
                new Vector4(0f, 0f, 0f, 0.76f * ease)), winRounding);
            if (wrap != null)
            {
                var ambA = 0.5f * ease;
                SpheneUI.Media.ImageInRect(dl, wrap, winMin, winMax, ImageFit.Cover,
                    tint: new Vector4(1f, 1f, 1f, ambA * 0.5f),
                    rounding: winRounding, zoom: 1.05f);
                SpheneUI.Media.ImageInRect(dl, wrap, winMin, winMax, ImageFit.Cover,
                    tint: new Vector4(1f, 1f, 1f, ambA * 0.32f),
                    rounding: winRounding, zoom: 1.14f);
                SpheneUI.Media.ImageInRect(dl, wrap, winMin, winMax, ImageFit.Cover,
                    tint: new Vector4(1f, 1f, 1f, ambA * 0.22f),
                    rounding: winRounding, zoom: 1.26f);
            }
            dl.AddRectFilled(winMin, winMax, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Surface, 0.42f * ease)), winRounding);

            // Chrome gradients · they ground the counter, close button,
            // caption and filmstrip against bright photos without hard strips.
            var chromeTop = new Vector4(0f, 0f, 0f, 0.5f * ease);
            var chromeNone = chromeTop with { W = 0f };
            SpheneUI.Media.FillRoundedGradient(dl, winMin, new Vector2(winMax.X, areaMin.Y),
                chromeTop, chromeTop, chromeNone, chromeNone,
                winRounding, ImDrawFlags.RoundCornersTop);
            SpheneUI.Media.FillRoundedGradient(dl, new Vector2(winMin.X, areaMax.Y), winMax,
                chromeNone, chromeNone, chromeTop, chromeTop,
                winRounding, ImDrawFlags.RoundCornersBottom);

            // The popout owns a real window · it gets the hairline frame the
            // borderless chrome skips (theme WindowBorderSize is 0). The
            // in-page overlay floats above content instead and stays frameless.
            if (standalone)
            {
                dl.AddRect(winMin, winMax, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.Colors.Border with { W = 0.55f * ease }),
                    winRounding, ImDrawFlags.None, MathF.Max(1f, scale));
            }
        }
        finally
        {
            // A leaked clip rect clips every later draw to the stale viewer
            // rect - the page renders "invisible" and clicks die under the
            // ghost window until ImGui resets on a window move.
            ImGui.PopClipRect();
        }

        // The photo draws FIRST · every chrome element after it stays visible
        // no matter how the image letterboxes (a wide shot previously covered
        // the like heart and the nav chevrons drawn before it). A soft shadow
        // lifts it off the ambient backdrop.
        if (wrap != null)
        {
            SpheneUI.Fx.SoftShadow(dl, imgMin, imgMax, 10f * scale, 1.4f * ease);
            SpheneUI.Media.ImageInRect(dl, wrap, imgMin, imgMax, ImageFit.Fit,
                tint: new Vector4(1f, 1f, 1f, ease), rounding: 10f * scale);
        }

        // ImGui hit order is first-submitted-wins: the controls sitting on the
        // photo claim their clicks before the swallow zone and the backdrop.
        var chevR = 18f * scale;
        var midY = (areaMin.Y + areaMax.Y) * 0.5f;
        CircleButton(dl, new Vector2(areaMin.X + chevR + 4f * scale, midY), chevR,
            FontAwesomeIcon.ChevronLeft, "##viewer-prev", "Previous photo",
            images.Count > 1, () => setIndex((index - 1 + images.Count) % images.Count));
        CircleButton(dl, new Vector2(areaMax.X - chevR - 4f * scale, midY), chevR,
            FontAwesomeIcon.ChevronRight, "##viewer-next", "Next photo",
            images.Count > 1, () => setIndex((index + 1) % images.Count));

        // Top-right affordances: close, then popout left of it. In the
        // standalone window the close renders as the standard titlebar
        // icon button · the circle style belongs to the lightbox overlay.
        var topY = winMin.Y + pad + 14f * scale;
        var xr = 14f * scale;
        var right = winMax.X - pad - xr;
        if (standalone)
        {
            var btnSize = ImGui.GetFrameHeight();
            ImGui.SetCursorScreenPos(new Vector2(winMax.X - pad - btnSize, topY - btnSize * 0.5f));
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Times, width: btnSize))
            {
                onClose();
            }
            SpheneUI.AttachTooltip("Close");
            right = winMax.X - pad - btnSize;
        }
        else
        {
            CircleButton(dl, new Vector2(right, topY), xr, FontAwesomeIcon.Times,
                "##viewer-close", "Close", true, onClose);
            right -= xr * 2f + 8f * scale;
            if (onPopOut != null)
            {
                CircleButton(dl, new Vector2(right, topY), xr, FontAwesomeIcon.ExternalLinkAlt,
                    "##viewer-popout", "Open in own window", true, onPopOut);
                right -= xr * 2f + 8f * scale;
            }
            if (onEditToggle != null)
            {
                CircleButton(dl, new Vector2(right, topY), xr, FontAwesomeIcon.Pen,
                    "##viewer-edit", "Edit photo", true, onEditToggle, active: editOpen);
            }
        }

        // Swallow zone over the photo · clicks on the image itself do nothing.
        ImGui.SetCursorScreenPos(areaMin);
        ImGui.InvisibleButton("##viewer-img", areaMax - areaMin);

        // Keyboard + wheel navigation · gated on this window having focus and
        // no text field holding input (arrows move the caret while editing).
        var typing = ImGui.GetIO().WantTextInput;
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            if (!typing && ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) setIndex((index - 1 + images.Count) % images.Count);
            if (!typing && ImGui.IsKeyPressed(ImGuiKey.RightArrow)) setIndex((index + 1) % images.Count);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) open = false;
        }
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel > 0f) setIndex((index - 1 + images.Count) % images.Count);
            if (wheel < 0f) setIndex((index + 1) % images.Count);
        }

        // Counter, top-left.
        dl.PushClipRect(winMin + new Vector2(pad, pad), new Vector2(right - xr, winMin.Y + pad + topH), true);
        dl.AddText(new Vector2(winMin.X + pad, topY - ImGui.GetTextLineHeight() * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text), $"{index + 1} / {images.Count}");
        dl.PopClipRect();

        // Bottom bar · its own section between photo and filmstrip: the
        // caption sits centered, the like heart plus count dock right. Nothing
        // overlays the photo itself, so every aspect ratio reads the same.
        var capY = areaMax.Y + 6f * scale;
        var likeReserve = 0f;
        if (onLike != null)
        {
            var likeR = 11f * scale;
            var countText = img.LikeCount > 0 ? img.LikeCount.ToString() : string.Empty;
            var countW = countText.Length > 0 ? ImGui.CalcTextSize(countText).X : 0f;
            var likePos = new Vector2(winMax.X - pad - likeR, capY + capH * 0.5f);
            var countPos = new Vector2(
                likePos.X - likeR - 6f * scale - countW,
                likePos.Y - ImGui.GetTextLineHeight() * 0.5f);
            likeReserve = winMax.X - MathF.Min(likePos.X - likeR, countPos.X);
            LikeButton(dl, likePos, likeR, img.LikedByMe, ease, () => onLike(index));
            if (countText.Length > 0)
            {
                dl.AddText(countPos,
                    SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.9f * ease)), countText);
            }
        }
        if (!showEditor && !string.IsNullOrWhiteSpace(img.Caption))
        {
            var capW = ImGui.CalcTextSize(img.Caption).X;
            var cx = winMin.X + (winSize.X - capW) * 0.5f;
            dl.PushClipRect(new Vector2(winMin.X + pad, capY),
                new Vector2(winMax.X - pad - likeReserve, capY + capH), true);
            dl.AddText(new Vector2(MathF.Max(winMin.X + pad, cx), capY),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextSecondary), img.Caption);
            dl.PopClipRect();
        }

        // Edit drawer · a surface card between caption and filmstrip where the
        // caller renders its controls; the photo area already shrank for it.
        if (showEditor && drawEditor != null)
        {
            var editMin = new Vector2(winMin.X + pad, capY + capH);
            var editMax = new Vector2(winMax.X - pad, editMin.Y + editH - 6f * scale);
            dl.AddRectFilled(editMin, editMax, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Surface, 0.94f * ease)), 8f * scale);
            dl.AddRect(editMin, editMax, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.5f * ease)), 8f * scale);
            drawEditor(img, editMin + new Vector2(10f * scale, 8f * scale),
                editMax.X - editMin.X - 20f * scale);
        }

        // Filmstrip · window of thumbs centred on the current image.
        var stripY = capY + capH + editH;
        var thumb = 44f * scale;
        var gap = 6f * scale;
        var fit = Math.Max(1, (int)((winSize.X - pad * 2f + gap) / (thumb + gap)));
        var half = fit / 2;
        var lo = Math.Clamp(index - half, 0, Math.Max(0, images.Count - fit));
        var hi = Math.Min(images.Count, lo + fit);
        var stripW = (hi - lo) * (thumb + gap) - gap;
        var tx = winMin.X + (winSize.X - stripW) * 0.5f;
        for (var i = lo; i < hi; i++)
        {
            var tMin = new Vector2(tx + (i - lo) * (thumb + gap), stripY);
            var tMax = tMin + new Vector2(thumb, thumb);
            var cur = i == index;
            ImGui.SetCursorScreenPos(tMin);
            if (ImGui.InvisibleButton($"##viewer-thumb{i}", new Vector2(thumb, thumb)))
            {
                setIndex(i);
            }
            var tWrap = SpheneUI.Media.Texture($"{texturePrefix}gallery:{gid}:{images[i].Id}",
                SyncshellCommunityView.ResolveBytes(images[i].ImageMediaHash, images[i].ImageBase64));
            if (tWrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, tWrap, tMin, tMax, ImageFit.Cover,
                    tint: cur ? Vector4.One : new Vector4(1f, 1f, 1f, 0.55f), rounding: 6f * scale);
            }
            dl.AddRect(tMin, tMax, SpheneUI.ColorUtils.ToU32(cur
                ? SpheneUI.Palette.CrystalBlue
                : SpheneUI.Colors.Border with { W = 0.3f }), 6f * scale);
        }

        // Backdrop close zone · submitted last so it only owns empty space.
        if (closeOnBackdrop)
        {
            ImGui.SetCursorScreenPos(winMin);
            if (ImGui.InvisibleButton("##viewer-bg", winSize))
            {
                open = false;
            }
        }

        return open;
    }

    private static void CircleButton(ImDrawListPtr dl, Vector2 center, float radius,
        FontAwesomeIcon icon, string id, string tooltip, bool enabled, Action onClick,
        bool active = false)
    {
        ImGui.SetCursorScreenPos(center - new Vector2(radius, radius));
        if (ImGui.InvisibleButton(id, new Vector2(radius * 2f, radius * 2f)) && enabled)
        {
            onClick();
        }
        var hovered = enabled && ImGui.IsItemHovered();
        dl.AddCircleFilled(center, radius, SpheneUI.ColorUtils.ToU32(
            active ? SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Accent, hovered ? 0.85f : 0.6f)
                : new Vector4(0f, 0f, 0f, hovered ? 0.75f : enabled ? 0.5f : 0.25f)), 48);
        if (hovered || active)
        {
            dl.AddCircle(center, radius,
                SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.5f)), 48);
        }
        if (hovered)
        {
            SpheneUI.AttachTooltip(tooltip);
        }
        using (SpheneUI.Icons.PushFont())
        {
            var g = icon.ToIconString();
            var gs = ImGui.CalcTextSize(g);
            dl.AddText(center - gs * 0.5f, SpheneUI.ColorUtils.ToU32(
                enabled ? Vector4.One : new Vector4(1f, 1f, 1f, 0.35f)), g);
        }
    }

    /// <summary>Like heart · fills with the danger/rose tone when liked, pulses on hover.</summary>
    private static void LikeButton(ImDrawListPtr dl, Vector2 center, float radius,
        bool liked, float alpha, Action onClick)
    {
        ImGui.SetCursorScreenPos(center - new Vector2(radius, radius));
        var clicked = ImGui.InvisibleButton("##viewer-like", new Vector2(radius * 2f, radius * 2f));
        var hovered = ImGui.IsItemHovered();
        var t = SpheneUI.Fx.HoverT(ImGui.GetID("##viewer-like"), hovered);
        var heartCol = liked
            ? SpheneUI.Colors.Danger
            : new Vector4(1f, 1f, 1f, 0.85f);
        dl.AddCircleFilled(center, radius, SpheneUI.ColorUtils.ToU32(
            new Vector4(0f, 0f, 0f, (0.5f + 0.25f * t) * alpha)), 48);
        if (liked || hovered)
        {
            dl.AddCircle(center, radius, SpheneUI.ColorUtils.ToU32(
                liked ? heartCol : new Vector4(1f, 1f, 1f, 0.5f * alpha)), 48);
        }
        if (hovered)
        {
            SpheneUI.AttachTooltip(liked ? "Remove your like" : "Like this photo");
        }
        using (SpheneUI.Icons.PushFont())
        {
            var g = FontAwesomeIcon.Heart.ToIconString();
            var gs = ImGui.CalcTextSize(g);
            dl.AddText(center - gs * 0.5f,
                SpheneUI.ColorUtils.ToU32(heartCol with { W = heartCol.W * alpha }), g);
        }
        if (clicked)
        {
            onClick();
        }
    }
}
