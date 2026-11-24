using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.Services;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Compact profile preview for flyouts and hover cards: a mini hero (banner
/// strip + overlapping avatar), name, status line, a clipped description
/// snippet and up to two link chips  ·  plus an optional "Open profile"
/// action. Reuses the same visual language as <c>Profile.Hero</c> so a
/// person looks identical wherever they pop up.
/// </summary>
internal static class PersonCard
{
    /// <summary>
    /// Draws the card into the current layout flow (flyouts already size to
    /// content). <paramref name="profile"/> may be the loading placeholder  · 
    /// the card then shows a skeleton-lean state instead of empty text.
    /// </summary>
    public static void Draw(string id, string displayName, SpheneProfileData? profile,
        IDalamudTextureWrap? avatar, IDalamudTextureWrap? banner, Vector4 accent,
        float width = 240f, Action? openProfile = null)
    {
        var scale = SpheneUI.Scale;
        var density = SpheneUI.Density.Factor;
        var bannerH = 52f * scale * density;
        var avatarSize = 40f * scale * density;
        var pad = 10f * scale;

        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, bannerH));
        var bannerMax = new Vector2(min.X + width, min.Y + bannerH);

        // banner: the owner's art, else a soft accent wash - matches Profile.Banner
        dl.AddRectFilled(min, bannerMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.16f)),
            8f * scale, ImDrawFlags.RoundCornersTop);
        if (banner != null)
        {
            SpheneUI.Media.ImageInRect(dl, banner, min, bannerMax, ImageFit.Cover,
                rounding: 8f * scale, corners: ImDrawFlags.RoundCornersTop);
        }
        // fade the banner's bottom edge into the surface below
        dl.AddRectFilledMultiColor(
            new Vector2(min.X, bannerMax.Y - bannerH * 0.45f), bannerMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Surface, 0f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Surface, 0f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface));

        // avatar overlapping the banner edge
        var avatarMin = new Vector2(min.X + pad, bannerMax.Y - avatarSize * 0.55f);
        var avatarMax = avatarMin + new Vector2(avatarSize);
        dl.AddCircleFilled(avatarMin + new Vector2(avatarSize * 0.5f),
            avatarSize * 0.5f + 2f * scale,
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface));
        if (avatar != null)
        {
            SpheneUI.Media.ImageInRect(dl, avatar, avatarMin, avatarMax,
                ImageFit.Cover, rounding: avatarSize * 0.5f);
        }

        var textX = avatarMax.X + pad;
        var lineH = ImGui.GetTextLineHeight();
        var nameY = bannerMax.Y - avatarSize * 0.55f + 2f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textX, nameY));
        SpheneUI.Text.Colored(SpheneUI.Text.Ellipsize(displayName,
            min.X + width - textX - pad), SpheneUI.Colors.TextPrimary);

        var status = SpheneUI.Profile.StatusLineText(profile, out var statusColor);
        ImGui.SetCursorScreenPos(new Vector2(textX, nameY + lineH));
        if (!string.IsNullOrWhiteSpace(status))
        {
            SpheneUI.Text.Colored(SpheneUI.Text.Ellipsize(status,
                min.X + width - textX - pad), statusColor);
        }

        var bodyY = nameY + lineH * 2f + 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(min.X + pad, bodyY));

        var description = profile?.Description;
        var links = profile?.Links;
        var loading = profile == null || string.IsNullOrEmpty(profile.Description)
            && string.IsNullOrEmpty(profile.Base64ProfilePicture);

        if (loading)
        {
            SpheneUI.Text.Muted("Loading profile...");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(description))
            {
                var snippet = description!.Length > 110
                    ? description[..110].TrimEnd() + "..." : description;
                SpheneUI.Text.MutedWrapped(snippet, min.X + width - pad);
            }
            if (links is { Count: > 0 })
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var shown = Math.Min(links.Count, 2);
                for (var i = 0; i < shown; i++)
                {
                    var l = links[i];
                    var usedW = LinkChips.Chip(
                        string.IsNullOrWhiteSpace(l.Label) ? l.Url : l.Label,
                        l.Url, l.Icon, accent, i);
                    if (i + 1 < shown)
                    {
                        ImGui.SameLine(0, SpheneUI.Spacing.X4.Scaled);
                    }
                    _ = usedW;
                }
                if (links.Count > shown)
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Muted($"+{links.Count - shown}");
                }
            }
        }

        if (openProfile != null)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            if (SpheneUI.Controls.SegmentButton("Open profile",
                FontAwesomeIcon.ArrowRight, accent: accent))
            {
                openProfile();
            }
        }
        _ = id;
    }
}
