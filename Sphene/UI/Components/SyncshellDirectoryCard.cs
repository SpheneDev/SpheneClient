using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using Sphene.UI.Syncshell;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Card for the public syncshell community directory: banner strip with an
/// avatar overlap, name, tagline and member count · the "venue flyer" shape
/// shared by the hub discover section and future directory surfaces.
/// </summary>
internal static class SyncshellDirectoryCard
{
    /// <summary>Draws the card. Returns true when the body was clicked.</summary>
    public static bool Draw(string id, SyncshellDirectoryEntryDto entry, string texturePrefix)
    {
        var scale = SpheneUI.Scale;
        var accent = SyncshellCommunityView.ParseAccent(entry.AccentColor) ?? SpheneUI.Palette.CrystalBlue;
        var cardW = ImGui.GetContentRegionAvail().X;
        var density = SpheneUI.Density.Factor;
        var bannerH = 56f * scale * density;
        var avatarSize = 34f * scale * density;
        var padX = SpheneUI.Spacing.X8.Scaled;
        var padY = SpheneUI.Spacing.X4.Scaled;
        var lineH = ImGui.GetTextLineHeight();
        var bodyH = avatarSize * 0.5f + padY + lineH
            + (string.IsNullOrWhiteSpace(entry.Tagline) ? 0f : lineH + 2f * scale) + padY;
        var cardH = bannerH + bodyH;

        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##dircard", new Vector2(cardW, cardH));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();
        SpheneUI.Lint.CheckBounds(id);

        var dl = ImGui.GetWindowDrawList();
        var radius = SpheneUI.Metrics.ButtonRadius;
        var fill = hovered
            ? SpheneUI.ColorUtils.WithAlpha(accent, 0.09f)
            : SpheneUI.Colors.Surface;
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), radius);

        // Banner strip (top corners follow the card's rounding)
        var bannerMax = new Vector2(max.X, min.Y + bannerH);
        if (entry.BannerMediaHash != null || !string.IsNullOrEmpty(entry.BannerImageBase64))
        {
            var wrap = SpheneUI.Media.Texture(
                $"{texturePrefix}dir-banner:{entry.GroupGID}:{SyncshellCommunityView.MediaVersion(entry.BannerMediaHash, entry.BannerImageBase64)}",
                SyncshellCommunityView.ResolveBytes(entry.BannerMediaHash, entry.BannerImageBase64));
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, min, bannerMax, ImageFit.Cover,
                    rounding: radius, corners: ImDrawFlags.RoundCornersTop);
            }
        }
        else
        {
            var wash = SpheneUI.ColorUtils.WithAlpha(accent, hovered ? 0.22f : 0.16f);
            dl.AddRectFilled(min, bannerMax, SpheneUI.ColorUtils.ToU32(wash),
                radius, ImDrawFlags.RoundCornersTop);
            using (SpheneUI.Icons.PushFont())
            {
                var glyph = FontAwesomeIcon.Users.ToIconString();
                var gsize = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(max.X - padX - gsize.X, min.Y + (bannerH - gsize.Y) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.5f)), glyph);
            }
        }

        // Bottom fade keeps text readable over bright banners.
        dl.AddRectFilledMultiColor(
            new Vector2(min.X, bannerMax.Y - bannerH * 0.55f), bannerMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Surface, 0f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Surface, 0f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface));

        // Accent seam under the banner.
        dl.AddRectFilled(bannerMax - new Vector2(0f, 1.5f * scale),
            new Vector2(max.X, bannerMax.Y), SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(accent, hovered ? 0.85f : 0.55f)));

        // Avatar overlapping the banner edge.
        var avatarMin = new Vector2(min.X + padX, bannerMax.Y - avatarSize * 0.5f);
        var avatarMax = avatarMin + new Vector2(avatarSize, avatarSize);
        if (entry.AvatarMediaHash != null || !string.IsNullOrEmpty(entry.AvatarImageBase64))
        {
            var wrap = SpheneUI.Media.Texture(
                $"{texturePrefix}dir-avatar:{entry.GroupGID}:{SyncshellCommunityView.MediaVersion(entry.AvatarMediaHash, entry.AvatarImageBase64)}",
                SyncshellCommunityView.ResolveBytes(entry.AvatarMediaHash, entry.AvatarImageBase64));
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, avatarMin, avatarMax, ImageFit.Cover,
                    rounding: 6f * scale, border: true);
            }
        }
        else
        {
            dl.AddRectFilled(avatarMin, avatarMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.30f)),
                6f * scale);
            dl.AddRect(avatarMin, avatarMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.7f)),
                6f * scale);
            using (SpheneUI.Icons.PushFont())
            {
                var glyph = FontAwesomeIcon.Users.ToIconString();
                var gsize = ImGui.CalcTextSize(glyph);
                dl.AddText(avatarMin + (new Vector2(avatarSize, avatarSize) - gsize) * 0.5f,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.ContrastTextColor(accent)), glyph);
            }
        }

        var textX = avatarMax.X + SpheneUI.Spacing.X8.Scaled;
        var titleY = bannerMax.Y + padY * 0.25f;
        var name = string.IsNullOrWhiteSpace(entry.GroupAlias) ? entry.GroupGID : entry.GroupAlias!;
        dl.AddText(new Vector2(textX, titleY), ImGui.GetColorU32(ImGuiCol.Text), name);
        var memberText = $"{entry.MemberCount} members";
        var memberSize = ImGui.CalcTextSize(memberText);
        dl.AddText(new Vector2(max.X - padX - memberSize.X, titleY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), memberText);

        if (!string.IsNullOrWhiteSpace(entry.Tagline))
        {
            ImGui.PushClipRect(new Vector2(textX, titleY + lineH + 2f * scale),
                new Vector2(max.X - padX, max.Y - padY * 0.5f), true);
            dl.AddText(new Vector2(textX, titleY + lineH + 2f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), entry.Tagline);
            ImGui.PopClipRect();
        }

        return clicked;
    }
}
