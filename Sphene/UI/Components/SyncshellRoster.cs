using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using ImGui = Dalamud.Bindings.ImGui.ImGui;
using Sphene.Services;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// Syncshell member-list primitives shared by the hub roster and the admin
/// management surface · presence gauge, staff chips and hover-action rows.
/// Deliberately not built from SectionCards so the roster reads like a
/// party/linkshell list instead of a settings form.
/// </summary>
internal static class SyncshellRoster
{
    /// <summary>Thin gauge showing online/total · the "souls present" bar.</summary>
    public static void PresenceGauge(int online, int total, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var barW = 120f * scale;
        var barH = 6f * scale;
        var min = ImGui.GetCursorScreenPos() + new Vector2(0f, (ImGui.GetFrameHeight() - barH) * 0.5f);
        var max = min + new Vector2(barW, barH);
        ImGui.Dummy(new Vector2(barW, ImGui.GetFrameHeight()));

        var radius = barH * 0.5f;
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBg), radius);

        var frac = total > 0 ? Math.Clamp(online / (float)total, 0f, 1f) : 0f;
        if (frac > 0f)
        {
            var fillMax = new Vector2(min.X + Math.Max(barH, barW * frac), max.Y);
            // Faint glow underlay, then the gradient fill · reads like an
            // aether gauge riding the accent ramp.
            dl.AddRectFilled(min - new Vector2(0f, 1f), fillMax + new Vector2(0f, 1f),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.22f)), radius);
            var (gDeep, gHi) = SpheneUI.Colors.AccentRamp(accent);
            SpheneUI.Chrome.RoundedHorizontalGradient(dl, min, fillMax, radius, gDeep, gHi);
        }
    }

    /// <summary>
    /// Full-width roster row on the shared friends-row renderer: avatar ring,
    /// display name, presence line, optional sub-label, role pill and the
    /// caller-supplied admin actions - always visible at the right edge, never
    /// hover-gated. The pill + action zone is reserved up front so text can
    /// never slide underneath.
    /// </summary>
    /// <summary>One icon chip in the row's badge zone - role icon in role color,
    /// the label only appears as the hover tooltip.</summary>
    internal readonly record struct RoleBadge(FontAwesomeIcon Icon, Vector4 Color, string Label);

    public static void MemberRow(string id, bool online, string label, string? subLabel,
        string? roleTag, Vector4 roleColor, Action? drawActions,
        IReadOnlyList<FontAwesomeIcon>? actionIcons = null,
        IDalamudTextureWrap? avatar = null, SpheneProfileTheme? theme = null,
        bool isNsfw = false, FontAwesomeIcon? roleIcon = null,
        IReadOnlyList<RoleBadge>? roleBadges = null, int actionSlots = 0,
        RoleBadge? staffBadge = null)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var rowH = 46f * scale * SpheneUI.Density.Factor;
        var rowW = ImGui.GetContentRegionAvail().X;
        var min = ImGui.GetCursorScreenPos();

        // Measure the actually drawn actions · Icons.Button sizes to
        // glyph + frame padding, a slot estimate clips the last icon.
        var usedActionsW = 0f;
        var actionCount = drawActions != null && actionIcons != null ? actionIcons.Count : 0;
        if (actionCount > 0)
        {
            using (SpheneUI.Icons.PushFont())
            {
                foreach (var ic in actionIcons!)
                {
                    usedActionsW += ImGui.CalcTextSize(ic.ToIconString()).X;
                }
            }
            usedActionsW += actionCount * (ImGui.GetStyle().FramePadding.X * 2f)
                + (actionCount - 1) * 4f * scale + 4f * scale;
        }

        // The action zone keeps a fixed slot count so rows with fewer
        // buttons still land their badges and the zone edge on the same
        // anchors - the divider line makes the zone a distinct column.
        var actionZoneW = 0f;
        var zoneSlots = Math.Max(actionSlots, actionCount);
        if (zoneSlots > 0)
        {
            float slotGlyphW;
            using (SpheneUI.Icons.PushFont())
            {
                slotGlyphW = ImGui.CalcTextSize(FontAwesomeIcon.UserShield.ToIconString()).X;
            }
            actionZoneW = zoneSlots * (slotGlyphW + ImGui.GetStyle().FramePadding.X * 2f)
                + (zoneSlots - 1) * 4f * scale + 16f * scale;
        }
        // Icon-badge cluster - bare glyphs only, names surface on hover.
        var badgesW = 0f;
        if (roleBadges is { Count: > 0 })
        {
            using (SpheneUI.Icons.PushFont())
            {
                foreach (var b in roleBadges)
                {
                    badgesW += ImGui.CalcTextSize(b.Icon.ToIconString()).X;
                }
            }
            badgesW += (roleBadges.Count - 1) * 6f * scale;
        }

        var tagW = badgesW > 0f || string.IsNullOrEmpty(roleTag)
            ? 0f
            : ImGui.CalcTextSize(roleTag).X + 14f * scale;
        var roleIconW = 0f;
        if (roleIcon is { } ri && tagW > 0f)
        {
            using (SpheneUI.Icons.PushFont())
            {
                roleIconW = ImGui.CalcTextSize(ri.ToIconString()).X + 5f * scale;
            }
            tagW += roleIconW;
        }
        var zoneW = badgesW > 0f ? badgesW : tagW;
        var rightReserve = actionZoneW + (zoneW > 0f ? zoneW + 8f * scale : 0f) + 6f * scale;

        // The row button must stop short of the action reserve - an
        // InvisibleButton submitted first claims the hover id and swallows
        // every click meant for the action widgets.
        ImGui.InvisibleButton($"##rosterrow-{id}",
            new Vector2(MathF.Max(1f, rowW - rightReserve), rowH));
        var max = min + new Vector2(rowW, rowH);
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        var t = SpheneUI.Fx.HoverT(ImGui.GetID($"##rosterrow-{id}"), hovered);

        // The staff marker's label surfaces when its avatar pip is hovered.
        if (staffBadge is { } sb)
        {
            var avatarMin = min + new Vector2(7f * scale, (rowH - 32f * scale) * 0.5f);
            if (ImGui.IsMouseHoveringRect(avatarMin,
                    avatarMin + new Vector2(32f * scale)))
            {
                SpheneUI.AttachTooltip(sb.Label);
            }
        }

        SpheneUI.Profile.FriendRow(dl, min, rowW, rowH,
            new SpheneUI.Profile.FriendRowContent(avatar, label,
                online ? "\u25CF online" : "\u25CB offline",
                online ? SpheneUI.Colors.Success : SpheneUI.Colors.TextMuted,
                subLabel, isNsfw, null, null,
                staffBadge is { } staffPip ? [(staffPip.Icon, staffPip.Color)] : null),
            roleColor, hovered, t, theme ?? SpheneProfileTheme.Parse(null), null, 8f * scale,
            rightInset: rightReserve, showChevron: false);

        // Action zone · faint tinted column with a hairline divider so the
        // button block reads as its own region, right-aligned contents.
        var zoneMinX = max.X - actionZoneW;
        if (actionZoneW > 0f)
        {
            dl.AddRectFilled(new Vector2(zoneMinX, min.Y), max,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.FrameBg, 0.5f)),
                8f * scale, ImDrawFlags.RoundCornersRight);
            dl.AddLine(new Vector2(zoneMinX, min.Y + 9f * scale),
                new Vector2(zoneMinX, max.Y - 9f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.8f)), 1f);
        }

        // Badge glyphs · painted after the row so they sit above the
        // backdrop, each carrying its own hover target for the name tooltip.
        if (badgesW > 0f && roleBadges != null)
        {
            var glyphH = ImGui.GetTextLineHeight();
            var bx = zoneMinX - 6f * scale - badgesW;
            for (var i = 0; i < roleBadges.Count; i++)
            {
                var badge = roleBadges[i];
                var glyph = badge.Icon.ToIconString();
                float glyphW;
                using (SpheneUI.Icons.PushFont())
                {
                    glyphW = ImGui.CalcTextSize(glyph).X;
                    dl.AddText(new Vector2(bx, min.Y + (rowH - glyphH) * 0.5f),
                        SpheneUI.ColorUtils.ToU32(badge.Color), glyph);
                }
                ImGui.SetCursorScreenPos(new Vector2(bx - 2f * scale,
                    min.Y + (rowH - glyphH) * 0.5f - 2f * scale));
                ImGui.InvisibleButton($"##badge-{id}-{i}",
                    new Vector2(glyphW + 4f * scale, glyphH + 4f * scale));
                if (ImGui.IsItemHovered())
                {
                    SpheneUI.AttachTooltip(badge.Label);
                }
                bx += glyphW + 6f * scale;
            }
        }

        // Role pill · painted after the row so it sits above the backdrop.
        if (tagW > 0f)
        {
            var pillH = ImGui.GetTextLineHeight() + 6f * scale;
            var pillMax = new Vector2(zoneMinX - 6f * scale, 0f);
            var pillMin = new Vector2(pillMax.X - tagW, min.Y + (rowH - pillH) * 0.5f);
            var pMax = pillMin + new Vector2(tagW, pillH);
            dl.AddRectFilled(pillMin, pMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(roleColor, 0.16f)), pillH * 0.5f);
            dl.AddRect(pillMin, pMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(roleColor, 0.4f)), pillH * 0.5f);
            var tagX = pillMin.X + 7f * scale;
            if (roleIcon is { } tagIcon)
            {
                var glyph = tagIcon.ToIconString();
                using (SpheneUI.Icons.PushFont())
                {
                    dl.AddText(new Vector2(tagX, pillMin.Y + (pillH - ImGui.GetTextLineHeight()) * 0.5f),
                        SpheneUI.ColorUtils.ToU32(roleColor), glyph);
                }
                tagX += roleIconW;
            }
            dl.AddText(new Vector2(tagX, pillMin.Y + 3f * scale),
                SpheneUI.ColorUtils.ToU32(roleColor), roleTag);
        }

        // Actions are part of the row chrome · always painted, never hidden
        // behind a hover gate - right-aligned inside the fixed zone.
        if (drawActions != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(max.X - usedActionsW - 8f * scale,
                min.Y + (rowH - ImGui.GetFrameHeight()) * 0.5f));
            drawActions();
        }

        // X4 gap between roster rows - the manual cursor advance bypasses
        // ItemSpacing, so siblings would otherwise paint edge to edge.
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y + SpheneUI.Spacing.X4.Scaled));
    }

    /// <summary>Staff-role ARGB → Vector4 · zero/empty falls back to the shell accent.</summary>
    internal static Vector4 StaffRoleColor(uint argb, Vector4 fallback)
    {
        if (argb == 0u)
        {
            return fallback;
        }
        return new Vector4(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f,
            (argb & 0xFF) / 255f, MathF.Max(0.25f, ((argb >> 24) & 0xFF) / 255f));
    }
}
