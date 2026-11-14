using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.UI.Design;

namespace Sphene.UI.Syncshell;

/// <summary>
/// The pickable staff-role symbol set — carried over from VenuePlus, whose
/// role icons were stored as <see cref="FontAwesomeIcon"/> enum names. Roles
/// persist that same string in <c>SyncshellStaffRoleDto.Icon</c>, so a Venue
/// "Barkeeper · Cocktail" port keeps working and everything syncs through the
/// existing role update call.
/// </summary>
internal static class StaffRoleIcons
{
    /// <summary>Icon shown for the shell owner — owners have no role row.</summary>
    public const FontAwesomeIcon Owner = FontAwesomeIcon.Crown;
    /// <summary>Fallback for staff members without a role and for roles whose
    /// stored key no longer resolves.</summary>
    public const FontAwesomeIcon StaffFallback = FontAwesomeIcon.UserShield;

    /// <summary>VenuePlus' curated picker categories ("All" is implicit).</summary>
    public static readonly (string Name, string[] Icons)[] Categories =
    [
        ("People", ["User", "Users", "UserFriends", "UserShield", "UserTie", "HandsHelping", "Handshake"]),
        ("Authority", ["Shield", "Crown", "Star", "Gem", "Medal", "Award", "Trophy"]),
        ("Music", ["Music", "Guitar", "Drum", "DrumSteelpan", "Microphone", "Headphones",
            "CompactDisc", "RecordVinyl", "TheaterMasks", "Mask"]),
        ("Drinks", ["GlassCheers", "Beer", "WineBottle", "GlassWhiskey", "Cocktail",
            "GlassMartini", "GlassMartiniAlt", "Coffee", "MugHot"]),
        ("Food", ["Utensils", "PizzaSlice", "Hamburger", "Hotdog", "IceCream", "CandyCane",
            "CakeCandles", "Cheese", "AppleAlt", "Lemon", "Carrot", "Fish"]),
        ("Mood", ["Heart", "GrinStars", "Smile", "Laugh", "KissWinkHeart"]),
        ("Others", ["Fire", "Snowflake", "Sun", "Moon", "Bolt", "Leaf", "Feather", "Magic"]),
    ];

    /// <summary>Every catalog key that resolves against this binding's enum.</summary>
    public static readonly IReadOnlyList<string> All = BuildAll();

    /// <summary>Stored key → icon; invalid/empty keys fall back like VenuePlus did.</summary>
    public static FontAwesomeIcon Parse(string? key, FontAwesomeIcon fallback = StaffFallback)
        => Enum.TryParse<FontAwesomeIcon>(key, ignoreCase: true, out var icon) ? icon : fallback;

    /// <summary>
    /// Icon-browser flyout content — category dropdown, search, "No icon" row
    /// and the icon grid. Returns true on a pick (<paramref name="picked"/> is
    /// the enum key, or null when "No icon" was chosen); the caller then saves
    /// and closes the flyout.
    /// </summary>
    public static bool DrawPicker(ref string filter, ref string category, out string? picked)
    {
        picked = null;
        var scale = SpheneUI.Scale;
        var width = 232f * scale;

        if (SpheneUI.Controls.BeginComboBox("##roleiconcat", category, 110f * scale))
        {
            foreach (var option in CategoryNames())
            {
                if (SpheneUI.Controls.Selectable(option,
                        string.Equals(option, category, StringComparison.Ordinal)))
                {
                    category = option;
                }
            }
            SpheneUI.Controls.EndComboBox();
        }
        ImGui.SameLine(0f, 6f * scale);
        _ = SpheneUI.Controls.InputTextWithHint("##roleiconfilter", "Search icons…", ref filter, 48);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        if (SpheneUI.Controls.Selectable("No icon", false))
        {
            return true;
        }
        SpheneUI.AttachTooltip("Remove the role's icon");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var cell = ImGui.GetFrameHeight();
        var cols = Math.Max(4, (int)(width / (cell + ImGui.GetStyle().ItemSpacing.X)));
        var shown = 0;
        foreach (var key in All)
        {
            if (!string.IsNullOrWhiteSpace(filter)
                && key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            using (ImRaii.PushId($"roleiconpick{shown}"))
            {
                var icon = Parse(key);
                if (SpheneUI.Icons.Button(icon))
                {
                    picked = key;
                    return true;
                }
            }
            SpheneUI.AttachTooltip(key);
            shown++;
            if (shown % cols != 0)
            {
                ImGui.SameLine(0f, 4f * scale);
            }
        }
        return false;
    }

    private static IEnumerable<string> CategoryNames()
    {
        yield return "All";
        foreach (var (name, _) in Categories)
        {
            yield return name;
        }
    }

    private static List<string> BuildAll()
    {
        var names = Enum.GetNames<FontAwesomeIcon>();
        var valid = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>(64);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, icons) in Categories)
        {
            foreach (var icon in icons)
            {
                if (valid.TryGetValue(icon, out var real) && added.Add(real))
                {
                    list.Add(real);
                }
            }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
