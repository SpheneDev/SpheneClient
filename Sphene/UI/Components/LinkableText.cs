using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Clickable-name primitives  ·  the "everything is a link" rule of the
/// community redesign. A name rendered through <see cref="UserLink"/> or
/// <see cref="SyncshellLink"/> always reads as interactive: tinted text,
/// underline on hover, hand cursor. Navigation stays with the caller (they
/// own mediator/context); the helpers return the click.
/// </summary>
internal static class LinkableText
{
    /// <summary>
    /// Draws <paramref name="displayName"/> as a profile link. Returns true
    /// on left-click  ·  open the profile page or
    /// <see cref="PersonCard"/> flyout there. Right-click reports through
    /// <paramref name="rightClicked"/> for context menus.
    /// </summary>
    public static bool UserLink(string id, string displayName,
        Vector4? accent = null, bool rightClicked = false)
        => Draw(id, displayName, accent ?? SpheneUI.Palette.CrystalBlue, rightClicked);

    /// <summary>
    /// Draws <paramref name="displayName"/> as a community link. Returns
    /// true on click  ·  navigate to the shell's community page.
    /// </summary>
    public static bool SyncshellLink(string id, string displayName,
        Vector4? accent = null)
        => Draw(id, displayName, accent ?? SpheneUI.Palette.EtherealPurple, false);

    private static bool Draw(string id, string text, Vector4 tint, bool captureRight)
    {
        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##linktext", ImGui.CalcTextSize(text));
        var hovered = ImGui.IsItemHovered();
        var right = captureRight && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.PopID();

        var dl = ImGui.GetWindowDrawList();
        var hoverT = SpheneUI.Fx.HoverT(ImGui.GetID($"lt{id}"), hovered, 12f);
        var color = SpheneUI.ColorUtils.LerpColor(SpheneUI.Colors.TextPrimary, tint, 0.25f + 0.55f * hoverT);
        dl.AddText(min, SpheneUI.ColorUtils.ToU32(color), text);
        if (hoverT > 0.01f)
        {
            var y = max.Y + 1f;
            dl.AddLine(new Vector2(min.X, y), new Vector2(max.X, y),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(tint, 0.75f * hoverT)),
                MathF.Max(1f, SpheneUI.Scale));
        }
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        _ = right;
        return clicked;
    }
}
