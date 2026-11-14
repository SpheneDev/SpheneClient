using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Overlapping avatar row (Discord-style member preview): up to
/// <paramref name="maxShown"/> circle avatars fanned left to right with a
/// "+N" overflow chip. Avatars are drawn on the window draw list so the
/// helper works inside cards, hero stats and feed rows alike.
/// </summary>
internal static class AvatarStack
{
    /// <summary>
    /// Draws the stack at the cursor position and reserves its footprint.
    /// <paramref name="avatars"/> may contain nulls  ·  they render as tonal
    /// placeholder circles while textures resolve.
    /// </summary>
    public static void Draw(string id, IReadOnlyList<IDalamudTextureWrap?> avatars,
        float size, int maxShown = 5, Vector4? accent = null)
    {
        var scale = SpheneUI.Scale;
        var overlap = 0.32f;
        var shown = Math.Min(avatars.Count, maxShown);
        var overflow = avatars.Count - shown;
        var stride = size * (1f - overlap);
        var total = shown > 0
            ? size + stride * (shown - 1) + (overflow > 0 ? stride + size : 0f)
            : size;
        ImGui.Dummy(new Vector2(total, size));
        var min = ImGui.GetItemRectMin();
        var dl = ImGui.GetWindowDrawList();
        var tint = accent ?? SpheneUI.Colors.Accent;

        for (var i = shown - 1; i >= 0; i--)
        {
            var c = new Vector2(min.X + size * 0.5f + stride * i, min.Y + size * 0.5f);
            var r = size * 0.5f;
            // surface-colored cut-out ring separates overlapping circles
            dl.AddCircleFilled(c, r + 1.5f * scale,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface));
            var wrap = avatars[i];
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap,
                    new Vector2(c.X - r, c.Y - r), new Vector2(c.X + r, c.Y + r),
                    ImageFit.Cover, rounding: r);
            }
            else
            {
                dl.AddCircleFilled(c, r,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(tint, 0.25f)));
                using (SpheneUI.Icons.PushFont())
                {
                    var g = FontAwesomeIcon.User.ToIconString();
                    var gs = ImGui.CalcTextSize(g) * 0.55f;
                    dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 0.55f,
                        c - gs * 0.5f, SpheneUI.ColorUtils.ToU32(
                            SpheneUI.ColorUtils.WithAlpha(tint, 0.8f)), g);
                }
            }
        }

        if (overflow > 0)
        {
            var c = new Vector2(min.X + size * 0.5f + stride * shown, min.Y + size * 0.5f);
            var r = size * 0.5f;
            dl.AddCircleFilled(c, r + 1.5f * scale,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Surface));
            dl.AddCircleFilled(c, r,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(tint, 0.30f)));
            dl.AddCircle(c, r,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(tint, 0.55f)));
            var t = $"+{overflow}";
            var ts = ImGui.CalcTextSize(t) * 0.7f;
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 0.7f, c - ts * 0.5f,
                ImGui.GetColorU32(ImGuiCol.Text), t);
        }
        _ = id;
    }
}
