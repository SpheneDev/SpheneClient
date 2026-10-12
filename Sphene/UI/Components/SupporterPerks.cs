using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Canonical, ordered catalog of everything supporter status unlocks plus the
/// shared row renderer. Keep this the single source of truth so every promo
/// surface (settings page, hub home card, future popups) stays in sync.
/// </summary>
internal static class SupporterPerks
{
    public static IReadOnlyList<SupporterPerk> All { get; } =
    [
        new(FontAwesomeIcon.IdBadge, SpheneUI.Palette.SpheneGold,
            "Personal Nameplate Indicator",
            "Your own supporter mark next to your in-game name — visible to every Sphene user."),
        new(FontAwesomeIcon.Palette, SpheneUI.Palette.EtherealPurple,
            "Custom Symbol, Text & Colors",
            "Pick a symbol or your own label and style it with game UI colors plus an edge glow."),
        new(FontAwesomeIcon.Icons, SpheneUI.Palette.CrystalBlue,
            "Animated Sphene Icons",
            "Server-provided animated icons (PNG, GIF, WebP) that play right on your nameplate."),
        new(FontAwesomeIcon.PhotoVideo, SpheneUI.Palette.NetworkWarning,
            "Animated List Background",
            "An animated PNG or GIF shown behind your entry in your friends' lists."),
        new(FontAwesomeIcon.Award, SpheneUI.Palette.LuminousGold,
            "Profile Supporter Badge",
            "A supporter badge on your Sphene profile card that others see when viewing you."),
        new(FontAwesomeIcon.Heart, SpheneUI.Palette.NetworkDisconnected,
            "Fund Development",
            "Your donation directly funds Sphene's development, servers and new features."),
    ];

    /// <summary>
    /// Renders the perk rows: tinted icon plate + bold title + muted
    /// description. Pass <paramref name="max"/> to show only the first perks
    /// (promo teasers); a "…and more" line is appended when truncated.
    /// </summary>
    public static void DrawList(int max = int.MaxValue)
    {
        var count = Math.Min(max, All.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                SpheneUI.Layout.Space(SpheneUI.Spacing.X8);
            }
            DrawRow(All[i]);
        }
        if (count < All.Count)
        {
            SpheneUI.Layout.Space(SpheneUI.Spacing.X8);
            SpheneUI.Text.Muted($"…and {All.Count - count} more — see the Supporter page for the full list.");
        }
    }

    private static void DrawRow(SupporterPerk perk)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var rowMin = ImGui.GetCursorScreenPos();
        var plate = 30f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var descGap = 2f * scale;

        var availW = MathF.Max(60f * scale, ImGui.GetContentRegionAvail().X);
        var textX = rowMin.X + plate + 10f * scale;
        var wrapW = MathF.Max(30f * scale, rowMin.X + availW - textX);
        var descSize = ImGui.CalcTextSize(perk.Description, wrapWidth: wrapW);
        var rowH = MathF.Max(plate, lineH + descGap + descSize.Y);

        // icon plate in the perk's own accent
        var plateMin = rowMin + new Vector2(0f, (rowH - plate) * 0.5f);
        drawList.AddRectFilled(plateMin, plateMin + new Vector2(plate),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(perk.Color, 0.14f)),
            plate * 0.28f);
        var iconSize = SpheneUI.Icons.Size(perk.Icon);
        using (SpheneUI.IconFont.Push())
        {
            drawList.AddText(plateMin + (new Vector2(plate) - iconSize) * 0.5f,
                SpheneUI.ColorUtils.ToU32(perk.Color), perk.Icon.ToIconString());
        }

        ImGui.SetCursorScreenPos(new Vector2(textX, rowMin.Y + (rowH - lineH - descGap - descSize.Y) * 0.5f));
        SpheneUI.Text.Colored(perk.Title, SpheneUI.Colors.TextPrimary);
        ImGui.SetCursorScreenPos(new Vector2(textX, ImGui.GetCursorScreenPos().Y + descGap));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapW);
        using (ImRaii.PushColor(ImGuiCol.Text, SpheneUI.Colors.TextMuted))
        {
            ImGui.TextUnformatted(perk.Description);
        }
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(new Vector2(rowMin.X, rowMin.Y + rowH));
    }
}
