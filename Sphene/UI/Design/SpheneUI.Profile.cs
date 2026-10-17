using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services;
using System.Numerics;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Community-style profile rendering shared between the hub's own profile
    /// page, the in-hub friend view and the standalone/popout profile windows.
    /// Applies a user's <see cref="SpheneProfileTheme"/> (banner image or
    /// custom gradient, accent color) with graceful fallbacks.
    /// </summary>
    public static class Profile
    {
        /// <summary>A clickable action button rendered in the hero's top-right corner.</summary>
        public sealed record HeroAction(FontAwesomeIcon Icon, string Label, string? Tooltip, Action OnClick);

        /// <summary>
        /// Edit-mode state for the hero of the user's own profile: enables
        /// click overlays on banner + avatar and swaps the status/description
        /// display for inline inputs writing into <see cref="StatusBuffer"/> /
        /// <see cref="DescriptionBuffer"/>. Commits fire on Enter or focus loss.
        /// </summary>
        public sealed class HeroEdit
        {
            public bool Enabled { get; set; }
            public Action? OnAvatarClicked { get; set; }
            public Action? OnBannerClicked { get; set; }
            public string StatusBuffer { get; set; } = string.Empty;
            public Action<string>? OnStatusCommitted { get; set; }
            public string DescriptionBuffer { get; set; } = string.Empty;
            public Action<string>? OnDescriptionCommitted { get; set; }
            /// <summary>Full comma-joined tag string after an add/remove — the hero never parses it itself.</summary>
            public Action<string>? OnTagsCommitted { get; set; }
            /// <summary>Fired when the NSFW chip is toggled in edit mode.</summary>
            public Action<bool>? OnNsfwToggled { get; set; }
            /// <summary>Buffer for the inline "+ add tag" input; seeded empty by the host.</summary>
            public string NewTagBuffer { get; set; } = string.Empty;
            /// <summary>Whether the inline add-tag input is currently open.</summary>
            public bool AddingTag { get; set; }
            /// <summary>Set when the add-tag input opens so the hero can focus it once.</summary>
            public bool TagInputFocusPending { get; set; }

            /// <summary>Own-profile: click the big name to edit the display name in place.</summary>
            public string DisplayNameBuffer { get; set; } = string.Empty;
            /// <summary>Commit callback for the display name (empty string = cleared).</summary>
            public Action<string>? OnDisplayNameCommitted { get; set; }
            /// <summary>Whether the name line is currently swapped for its input.</summary>
            public bool EditingDisplayName { get; set; }
            /// <summary>Set when the name input opens so the hero can focus it once.</summary>
            public bool NameFocusPending { get; set; }
            /// <summary>Input cap for the display name buffer.</summary>
            public int DisplayNameMaxLength { get; set; } = 64;

            /// <summary>Own-profile: click the "@" meta line to rename the handle in place.</summary>
            public string HandleBuffer { get; set; } = string.Empty;
            /// <summary>Commit callback for the handle rename; null disables handle editing.</summary>
            public Action<string>? OnHandleCommitted { get; set; }
            /// <summary>Whether the meta line is currently swapped for its input.</summary>
            public bool EditingHandle { get; set; }
            /// <summary>Set when the handle input opens so the hero can focus it once.</summary>
            public bool HandleFocusPending { get; set; }
            /// <summary>Cooldown gate — a locked handle shows the hint instead of opening the input.</summary>
            public bool HandleLocked { get; set; }
            /// <summary>Muted hint under the handle (rules / next rename date).</summary>
            public string? HandleHint { get; set; }
            /// <summary>Input cap for the handle buffer.</summary>
            public int HandleMaxLength { get; set; } = 15;

            /// <summary>Small feedback line under the identity block (rename result, cooldown notice).</summary>
            public string? Feedback { get; set; }
            /// <summary>Color for <see cref="Feedback"/>.</summary>
            public Vector4 FeedbackColor { get; set; } = Colors.TextMuted;

            /// <summary>Server-side display name — commits that match it are ignored.</summary>
            public string? DisplayNameCurrent { get; set; }
            /// <summary>Server-side handle without the "@" — commits that match it are ignored.</summary>
            public string? HandleCurrent { get; set; }
            /// <summary>Pending display-name value shown in the confirmation modal.</summary>
            public string? PendingDisplayName { get; set; }
            /// <summary>Pending handle value shown in the confirmation modal.</summary>
            public string? PendingHandle { get; set; }
            /// <summary>Muted detail line in the display-name confirmation modal.</summary>
            public string? DisplayNameConfirmDetail { get; set; }
            /// <summary>Muted detail line in the handle confirmation modal (e.g. the cooldown rule).</summary>
            public string? HandleConfirmDetail { get; set; }
            /// <summary>Confirmation modal guarding the display-name commit.</summary>
            public Popups.ConfirmDialog DisplayNameConfirm { get; } = new();
            /// <summary>Confirmation modal guarding the handle rename.</summary>
            public Popups.ConfirmDialog HandleConfirm { get; } = new();
        }

        /// <summary>Content for <see cref="Identity"/>: the own-identity header strip.</summary>
        public readonly record struct IdentityContent(
            IDalamudTextureWrap? Avatar,
            string Name,
            Vector4 NameColor,
            string StatusLine,
            Vector4 StatusColor,
            Vector4 PresenceColor,
            string PresenceLabel,
            string? Handle,
            Vector4 Accent);

        /// <summary>
        /// Own-name chain for identity surfaces: the display name when set,
        /// else the bare alias. Never the game character name — that is not
        /// the Sphene-facing identity — and never the raw UID either: the
        /// handle is the identity. The "@" belongs to the handle meta line,
        /// not the name slot.
        /// </summary>
        public static string OwnName(string? displayName, string? alias)
            => !string.IsNullOrWhiteSpace(displayName) ? displayName
             : !string.IsNullOrWhiteSpace(alias) ? alias
             : "--";

        /// <summary>
        /// Status line for the own-identity strip: the status message when
        /// set, else a real description, else the set-a-status hint.
        /// </summary>
        public static string StatusLineText(SpheneProfileData? profile, out Vector4 color)
        {
            if (profile != null && !string.IsNullOrWhiteSpace(profile.StatusMessage))
            {
                color = Colors.TextSecondary;
                return profile.StatusMessage;
            }
            if (profile != null && !string.IsNullOrWhiteSpace(profile.Description)
                && !string.Equals(profile.Description, SpheneProfileManager.NoDescriptionPlaceholder,
                    StringComparison.Ordinal))
            {
                color = Colors.TextMuted;
                return profile.Description;
            }
            color = Colors.TextMuted;
            return "Set a status in your profile";
        }

        private static readonly Vector4 IdentityScrimColor = new(0.02f, 0.02f, 0.04f, 1f);
        private const float IdentityScrimAlpha = 0.55f;

        /// <summary>
        /// Dark scrim behind <see cref="Identity"/> — the same dim the hub
        /// header card applies. Deliberately translucent: a caller's banner
        /// or row backdrop underneath still reads through.
        /// </summary>
        public static void IdentityScrim(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float rounding = 0f, ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
            => drawList.AddRectFilled(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(IdentityScrimColor, IdentityScrimAlpha)),
                rounding, corners);

        /// <summary>
        /// Self-contained own-identity card — <see cref="IdentityScrim"/>,
        /// hover sheen, hairline and the <see cref="Identity"/> strip as one
        /// darker container floating on the caller's backdrop. The caller
        /// owns click handling and hover detection (bar variants must not
        /// swallow drag presses).
        /// </summary>
        public static void IdentityCard(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float padX, IdentityContent content, bool hovered, float rounding,
            ImDrawFlags corners, string tooltipHint)
        {
            IdentityScrim(drawList, min, max, rounding, corners);
            if (hovered)
            {
                drawList.AddRectFilled(min, max,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.06f)),
                    rounding, corners);
            }
            drawList.AddRect(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.10f)),
                rounding, corners);
            Identity(drawList, min, max.Y - min.Y, max.X - min.X, padX, content, hovered,
                tooltipHint);
        }

        /// <summary>
        /// Own-identity strip shared by the hub header card and the CompactUI
        /// profile bar: accent-haloed avatar with presence dot, name and
        /// status line. Draws no background — the caller's backdrop (row
        /// image, <see cref="IdentityScrim"/>) stays visible behind it.
        /// </summary>
        public static void Identity(ImDrawListPtr drawList, Vector2 min, float height, float width,
            float padX, IdentityContent content, bool hovered, string tooltipHint)
        {
            var scale = Scale;
            var avatarSize = MathF.Min(34f * scale, height - 10f * scale);
            var avatarPos = new Vector2(min.X + padX, min.Y + (height - avatarSize) * 0.5f);

            drawList.AddCircleFilled(avatarPos + new Vector2(avatarSize * 0.5f),
                avatarSize * 0.5f + 2f * scale,
                ColorUtils.ToU32(ColorUtils.WithAlpha(content.Accent, 0.30f)), 0);
            ImGui.SetCursorScreenPos(avatarPos);
            Media.Avatar(content.Avatar, avatarSize);
            Media.PresenceDot(drawList, avatarPos, avatarSize, content.PresenceColor);

            var textX = avatarPos.X + avatarSize + 9f * scale;
            var textMaxW = MathF.Max(30f * scale, min.X + width - textX - 8f * scale);
            var lineH = ImGui.GetTextLineHeight();
            var lineGap = 2f * scale;
            var nameY = min.Y + (height - lineH * 2f - lineGap) * 0.5f;

            var statusText = Text.Ellipsize(content.StatusLine, textMaxW);
            drawList.AddText(new Vector2(textX, nameY),
                ColorUtils.ToU32(content.NameColor), Text.Ellipsize(content.Name, textMaxW));
            drawList.AddText(new Vector2(textX, nameY + lineH + lineGap),
                ColorUtils.ToU32(content.StatusColor), statusText);

            if (hovered)
            {
                var tip = string.IsNullOrEmpty(content.Handle)
                    ? content.PresenceLabel
                    : $"{content.PresenceLabel} — @{content.Handle}";
                if (!string.Equals(statusText, content.StatusLine, StringComparison.Ordinal))
                {
                    tip = $"{content.StatusLine}\n{tip}";
                }
                AttachTooltip(tip + tooltipHint);
            }
        }

        /// <summary>
        /// Draws the profile banner strip: the user's banner image (cover),
        /// optionally blended with the owner's banner top/bottom colors via
        /// the theme's <c>BannerTopOver</c>/<c>BannerBottomOver</c> flags, a
        /// theme-colored gradient when no image exists, and the default accent
        /// gradient as last resort.
        /// </summary>
        public static void Banner(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            IDalamudTextureWrap? bannerTexture, SpheneProfileTheme theme,
            Vector4 fallbackA, Vector4 fallbackB, IDalamudTextureWrap? watermark = null,
            float? rounding = null, ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            var scale = Scale;
            var r = rounding ?? 0f;
            var darkTop = new Vector4(0f, 0f, 0f, 0.05f);
            var darkBottom = new Vector4(0f, 0f, 0f, 0.35f);
            if (bannerTexture != null)
            {
                if (r > 0.5f)
                {
                    Media.FitRect(min, max, new Vector2(bannerTexture.Width, bannerTexture.Height),
                        ImageFit.Cover, out var rMin, out var rMax, out var uv0, out var uv1);
                    drawList.AddImageRounded(bannerTexture.Handle, rMin, rMax, uv0, uv1,
                        ImGui.GetColorU32(Vector4.One), r, corners);
                }
                else
                {
                    Media.ImageInRect(drawList, bannerTexture, min, max, ImageFit.Cover);
                }
                // owner's banner colors blended over the image, per half
                var overTop = theme.BannerTopOver && theme.BannerTop.HasValue
                    ? ColorUtils.WithAlpha(theme.BannerTopColor(fallbackA), 0.55f)
                    : Vector4.Zero;
                var overBottom = theme.BannerBottomOver && theme.BannerBottom.HasValue
                    ? ColorUtils.WithAlpha(theme.BannerBottomColor(fallbackB), 0.55f)
                    : Vector4.Zero;
                if (overTop.W > 0f || overBottom.W > 0f)
                {
                    FillGradient(drawList, min, max,
                        overTop, overTop, overBottom, overBottom, r, corners);
                }
                // darken toward the bottom so overlaid content stays readable
                FillGradient(drawList, min, max,
                    darkTop, darkTop, darkBottom, darkBottom, r, corners);
            }
            else
            {
                var top = ColorUtils.WithAlpha(theme.BannerTopColor(fallbackA), 0.45f);
                var bottom = ColorUtils.WithAlpha(theme.BannerBottomColor(fallbackB), 0.45f);
                var fadeTop = ColorUtils.WithAlpha(theme.BannerTopColor(fallbackA), 0.14f);
                var fadeBottom = ColorUtils.WithAlpha(theme.BannerBottomColor(fallbackB), 0.14f);
                FillGradient(drawList, min, max, top, bottom, fadeBottom, fadeTop, r, corners);
            }
            if (watermark != null)
            {
                var size = (max.Y - min.Y) * 1.9f;
                Media.ImageInRect(drawList, watermark,
                    new Vector2(max.X - size - 4f * scale, max.Y - size * 0.62f),
                    new Vector2(max.X - 4f * scale, max.Y + size * 0.38f),
                    ImageFit.Cover, new Vector4(1f, 1f, 1f, 0.14f));
            }
        }

        /// <summary>
        /// Four-corner gradient fill honoring <paramref name="rounding"/>:
        /// uses <see cref="Media.FillRoundedGradient"/>'s vertex-colored fan
        /// when rounded (the 2×2 texture + <c>AddImageRounded</c> path visibly
        /// offset the blend), otherwise <c>AddRectFilledMultiColor</c>.
        /// </summary>
        private static void FillGradient(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 topLeft, Vector4 topRight, Vector4 bottomRight, Vector4 bottomLeft,
            float rounding, ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            if (rounding > 0.5f)
            {
                Media.FillRoundedGradient(drawList, min, max,
                    topLeft, topRight, bottomRight, bottomLeft, rounding, corners);
                return;
            }
            drawList.AddRectFilledMultiColor(min, max,
                ColorUtils.ToU32(topLeft), ColorUtils.ToU32(topRight),
                ColorUtils.ToU32(bottomRight), ColorUtils.ToU32(bottomLeft));
        }

        /// <summary>Banner strip height in unscaled pixels, shared across hero renderers.</summary>
        public const float HeroBannerHeight = 64f;

        /// <summary>Avatar size in unscaled pixels, shared across hero renderers.</summary>
        public const float HeroAvatarSize = 56f;

        /// <summary>Fraction of the banner height that dissolves into the
        /// card body via a fade of the card's own accent tint.</summary>
        private const float HeroBannerBodyFade = 0.45f;

        /// <summary>
        /// The profile hero card: banner, avatar half-overlapping the banner
        /// edge, name + meta + status + tags + optional stats row and actions.
        /// </summary>
        /// <param name="displayName">Primary name shown in the big font.</param>
        /// <param name="metaLine">Secondary line under the name — "@handle" renders white, anything else muted. In edit mode a "@" line becomes the handle-rename entry point.</param>
        /// <param name="profile">Profile data for status/tags; may be a placeholder.</param>
        /// <param name="avatar">Avatar texture (caller caches per user).</param>
        /// <param name="banner">Optional banner texture — falls back to theme gradient.</param>
        /// <param name="theme">The user's profile theme.</param>
        /// <param name="fallbackAccentA">Gradient/ring fallback color A.</param>
        /// <param name="fallbackAccentB">Gradient fallback color B.</param>
        /// <param name="isNsfw">Shows the NSFW badge next to the name.</param>
        /// <param name="watermark">Optional watermark image in the banner.</param>
        /// <param name="actions">Buttons at the banner's bottom-right edge.</param>
        /// <param name="drawStats">Optional full-width content under the identity block (e.g. stat tiles).</param>
        /// <param name="edit">Own-profile edit mode: click overlays on banner/avatar + inline status/description inputs.</param>
        public static void Hero(string displayName, string metaLine,
            SpheneProfileData profile, IDalamudTextureWrap? avatar, IDalamudTextureWrap? banner,
            SpheneProfileTheme theme, Vector4 fallbackAccentA, Vector4 fallbackAccentB,
            bool isNsfw = false, IDalamudTextureWrap? watermark = null,
            IReadOnlyList<HeroAction>? actions = null, Action<float>? drawStats = null,
            HeroEdit? edit = null)
        {
            var scale = Scale;
            var bannerHeight = HeroBannerHeight * scale;
            var avatarSize = HeroAvatarSize * scale;
            var accent = theme.AccentColor(fallbackAccentA);
            var bleedHeight = bannerHeight + Spacing.CardPadding.Scaled;
            var topRounding = Layout.AccentCardRounding * scale;

            Layout.AccentCard(() =>
            {
                var cardMin = ImGui.GetCursorScreenPos();
                var cardWidth = ImGui.GetContentRegionAvail().X;
                var drawList = ImGui.GetWindowDrawList();

                // Approximate banner rect in content coordinates — only used
                // for the edit click zone, the chrome layer draws the exact one.
                var padding = Spacing.CardPadding.Scaled;
                var bannerMin = cardMin - new Vector2(padding);
                var bannerMax = new Vector2(cardMin.X + cardWidth + padding,
                    cardMin.Y + bannerHeight);

                ImGui.Dummy(new Vector2(cardWidth, bannerHeight));

                var editing = edit?.Enabled == true;

                var avatarPos = cardMin + new Vector2(10f * scale, bannerHeight - avatarSize * 0.45f);
                ImGui.SetCursorScreenPos(avatarPos);
                Media.Avatar(avatar, avatarSize);
                drawList.AddRect(avatarPos, avatarPos + new Vector2(avatarSize),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Palette.CrystalWhite, 0.55f)),
                    avatarSize * 0.5f, ImDrawFlags.RoundCornersAll,
                    MathF.Max(1.25f, 1.25f * scale));
                if (editing && edit!.OnAvatarClicked != null)
                {
                    ImGui.SetCursorScreenPos(avatarPos);
                    if (ImGui.InvisibleButton($"##hero-avatar-edit-{displayName}",
                            new Vector2(avatarSize)))
                    {
                        edit.OnAvatarClicked();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        drawList.AddRectFilled(avatarPos, avatarPos + new Vector2(avatarSize),
                            ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.45f)),
                            avatarSize * 0.5f);
                        using (Icons.PushFont())
                        {
                            var cam = FontAwesomeIcon.Camera.ToIconString();
                            var camSize = ImGui.CalcTextSize(cam);
                            drawList.AddText(
                                avatarPos + (new Vector2(avatarSize) - camSize) * 0.5f,
                                ColorUtils.ToU32(Vector4.One), cam);
                        }
                    }
                }

                var textMin = new Vector2(avatarPos.X + avatarSize + 10f * scale,
                    cardMin.Y + bannerHeight + 4f * scale);
                var textMaxX = cardMin.X + cardWidth - 10f * scale;
                var lineH = ImGui.GetTextLineHeight();
                var hasStatus = !string.IsNullOrWhiteSpace(profile.StatusMessage);
                var hasTags = !string.IsNullOrWhiteSpace(profile.Tags);
                var hasDesc = !string.IsNullOrWhiteSpace(profile.Description)
                    && !string.Equals(profile.Description,
                        SpheneProfileManager.NoDescriptionPlaceholder, StringComparison.Ordinal);
                var textHeight = avatarSize * 0.55f + lineH * 1.2f + 6f * scale
                    + (metaLine.Length > 0 ? lineH + 2f * scale : 0f)
                    + ((editing || hasStatus) ? lineH + 6f * scale : 0f)
                    + ((editing || hasTags) ? lineH + 12f * scale : 0f)
                    + (editing ? lineH * 3f + 14f * scale : (hasDesc ? lineH * 2f : 0f))
                    + (!string.IsNullOrEmpty(edit?.Feedback) ? lineH * 1.5f : 0f);

                ImGui.SetCursorScreenPos(textMin);
                using (var textChild = ImRaii.Child("##profile-hero-text",
                           new Vector2(MathF.Max(120f, textMaxX - textMin.X), textHeight),
                           false, ImGuiWindowFlags.NoScrollbar))
                {
                    if (textChild)
                    {
                        var inputWidth = MathF.Max(140f, textMaxX - textMin.X);
                        DrawHeroName(displayName, metaLine, accent, editing ? edit : null,
                            inputWidth, scale);
                        var onNsfw = edit?.OnNsfwToggled;
                        var nsfwClickable = editing && onNsfw != null;
                        if (isNsfw || nsfwClickable)
                        {
                            DrawNsfwChip(isNsfw, nsfwClickable, scale,
                                () => onNsfw?.Invoke(!isNsfw));
                        }
                        if (editing)
                        {
                            var statusBuf = edit!.StatusBuffer;
                            var statusCommit = Controls.InputTextWithHint("##hero-status",
                                    "Set a status message…", ref statusBuf, 100,
                                    ImGuiInputTextFlags.EnterReturnsTrue,
                                    preferredWidth: inputWidth)
                                || ImGui.IsItemDeactivatedAfterEdit();
                            edit.StatusBuffer = statusBuf;
                            if (statusCommit)
                            {
                                edit.OnStatusCommitted?.Invoke(statusBuf.Trim());
                            }
                        }
                        else if (hasStatus)
                        {
                            Text.ColoredWrapped(profile.StatusMessage, Colors.TextSecondary);
                        }
                        if (editing || hasTags)
                        {
                            DrawHeroTags(profile.Tags, editing ? edit : null, accent, scale);
                        }
                        if (editing)
                        {
                            var descBuf = edit!.DescriptionBuffer;
                            var descCommit = Controls.InputTextMultiline("##hero-desc",
                                    ref descBuf, 2000,
                                    new Vector2(inputWidth, lineH * 3f + 8f * scale))
                                || ImGui.IsItemDeactivatedAfterEdit();
                            edit.DescriptionBuffer = descBuf;
                            if (descCommit)
                            {
                                edit.OnDescriptionCommitted?.Invoke(descBuf.Trim());
                            }
                        }
                        else if (hasDesc)
                        {
                            var preview = profile.Description;
                            if (preview.Length > 140) preview = preview[..140] + "…";
                            Text.MutedWrapped(preview);
                        }
                    }
                }

                // Action buttons along the banner's bottom-right edge. Their
                // bounding box is remembered so the banner edit zone can
                // exclude it geometrically — HoveredId arbitration alone still
                // lets the banner overlay bleed through under the buttons.
                var hasActionsZone = false;
                var actionsZoneMin = Vector2.Zero;
                var actionsZoneMax = Vector2.Zero;
                if (actions != null && actions.Count > 0)
                {
                    var x = textMaxX;
                    var actionTop = cardMin.Y + bannerHeight - ImGui.GetFrameHeight() - 6f * scale;
                    foreach (var action in actions)
                    {
                        var w = Icons.TextButtonWidth(action.Icon, action.Label) + 4f * scale;
                        x -= w;
                        ImGui.SetCursorScreenPos(new Vector2(x, actionTop));
                        if (Icons.TextButton(action.Icon, action.Label))
                        {
                            action.OnClick();
                        }
                        if (action.Tooltip != null)
                        {
                            AttachTooltip(action.Tooltip);
                        }
                        x -= ImGui.GetStyle().ItemSpacing.X;
                    }
                    actionsZoneMin = new Vector2(
                        x + ImGui.GetStyle().ItemSpacing.X - 2f * scale, actionTop - 2f * scale);
                    actionsZoneMax = new Vector2(textMaxX + 2f * scale,
                        actionTop + ImGui.GetFrameHeight() + 2f * scale);
                    hasActionsZone = true;
                }

                // Banner click zone comes last so action buttons and the avatar
                // keep hover/click priority where they overlap it.
                if (editing && edit!.OnBannerClicked != null)
                {
                    var bannerSize = bannerMax - bannerMin;
                    ImGui.SetCursorScreenPos(bannerMin);
                    var clicked = ImGui.InvisibleButton($"##hero-banner-edit-{displayName}",
                            bannerSize);
                    var overInteractables =
                        (edit!.OnAvatarClicked != null && ImGui.IsMouseHoveringRect(
                            avatarPos, avatarPos + new Vector2(avatarSize)))
                        || (hasActionsZone && ImGui.IsMouseHoveringRect(
                            actionsZoneMin, actionsZoneMax));
                    if (clicked && !overInteractables)
                    {
                        edit.OnBannerClicked();
                    }
                    if (ImGui.IsItemHovered() && !overInteractables)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        drawList.AddRectFilled(bannerMin, bannerMax,
                            ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.30f)),
                            topRounding, ImDrawFlags.RoundCornersTop);
                        const string changeLabel = "Click to change banner";
                        var labelSize = ImGui.CalcTextSize(changeLabel);
                        var chipMin = bannerMin
                            + (bannerSize - labelSize) * 0.5f
                            - new Vector2(8f * scale, 0f);
                        drawList.AddRectFilled(chipMin, chipMin + labelSize + new Vector2(16f * scale, 0f),
                            ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.60f)), 6f * scale);
                        drawList.AddText(chipMin + new Vector2(8f * scale, 0f),
                            ColorUtils.ToU32(Vector4.One), changeLabel);
                    }
                }

                var contentBottom = MathF.Max(avatarPos.Y + avatarSize + 6f * scale,
                    textMin.Y + textHeight + 4f * scale);
                if (drawStats != null)
                {
                    ImGui.SetCursorScreenPos(new Vector2(cardMin.X + 6f * scale,
                        contentBottom));
                    drawStats(cardWidth - 12f * scale);
                    contentBottom = ImGui.GetCursorScreenPos().Y;
                }
                ImGui.SetCursorScreenPos(new Vector2(cardMin.X, contentBottom));
                ImGui.Dummy(Vector2.Zero);
            }, accent, animatedBackdrop: false,
            id: $"profile-hero-{displayName}",
            fxHover: theme.CardHover, fxIdle: theme.CardIdle,
            topBleed: (dl, cMin, cMax, cRounding) =>
            {
                // Full-bleed banner: the rect comes from AccentCard's own outer
                // bounds, so the image sits exactly on the card's top/side
                // edges and its bottom dissolves into the card tint.
                var bleedMax = new Vector2(cMax.X, cMin.Y + bleedHeight);
                dl.PushClipRect(cMin, bleedMax, true);
                Banner(dl, cMin, bleedMax, banner, theme,
                    fallbackAccentA, fallbackAccentB, watermark,
                    rounding: topRounding, corners: ImDrawFlags.RoundCornersTop);
                dl.PopClipRect();
                var fadeH = bleedHeight * HeroBannerBodyFade;
                var fadeClear = ColorUtils.ToU32(ColorUtils.WithAlpha(
                    Layout.AccentCardTint(accent), 0f));
                var fadeSolid = ColorUtils.ToU32(Layout.AccentCardTint(accent));
                dl.AddRectFilledMultiColor(new Vector2(cMin.X, bleedMax.Y - fadeH),
                    bleedMax, fadeClear, fadeClear, fadeSolid, fadeSolid);
            });
        }

        /// <summary>
        /// Loading twin of <see cref="Hero"/>: same silhouette (banner bleed,
        /// half-overlapping avatar, name/meta/status bars) drawn as pulsing
        /// blocks with a traveling sheen. Hosts call this while the profile
        /// fetch is in flight — when the real hero swaps in, geometry lines
        /// up so the transition does not jump.
        /// </summary>
        /// <param name="accent">Tint for the banner placeholder and card ring.</param>
        /// <param name="statsRows">Skeleton bars appended under the identity block, mirroring a caller's stats/meta lines.</param>
        public static void HeroSkeleton(Vector4 accent, int statsRows = 0)
        {
            var scale = Scale;
            var bannerHeight = HeroBannerHeight * scale;
            var avatarSize = HeroAvatarSize * scale;
            var bleedHeight = bannerHeight + Spacing.CardPadding.Scaled;
            var topRounding = Layout.AccentCardRounding * scale;
            var phase = (float)ImGui.GetTime();

            Layout.AccentCard(() =>
            {
                var cardMin = ImGui.GetCursorScreenPos();
                var cardWidth = ImGui.GetContentRegionAvail().X;
                var drawList = ImGui.GetWindowDrawList();
                var lineH = ImGui.GetTextLineHeight();
                var barR = lineH * 0.30f;

                ImGui.Dummy(new Vector2(cardWidth, bannerHeight));

                var avatarPos = cardMin + new Vector2(10f * scale, bannerHeight - avatarSize * 0.45f);
                ImGui.SetCursorScreenPos(avatarPos);
                ImGui.Dummy(new Vector2(avatarSize));
                SkeletonBlock(drawList, avatarPos, avatarPos + new Vector2(avatarSize),
                    avatarSize * 0.5f, phase);

                var textX = avatarPos.X + avatarSize + 10f * scale;
                var textW = MathF.Max(60f * scale, cardMin.X + cardWidth - 10f * scale - textX);
                var nameY = cardMin.Y + bannerHeight + 4f * scale;
                SkeletonBlock(drawList, new Vector2(textX, nameY),
                    new Vector2(textX + textW * 0.55f, nameY + lineH * 1.15f), barR, phase + 0.12f);
                var metaY = nameY + lineH * 1.15f + 3f * scale;
                SkeletonBlock(drawList, new Vector2(textX, metaY),
                    new Vector2(textX + textW * 0.32f, metaY + lineH), barR, phase + 0.24f);
                var statusY = metaY + lineH + 7f * scale;
                SkeletonBlock(drawList, new Vector2(textX, statusY),
                    new Vector2(textX + textW * 0.85f, statusY + lineH), barR, phase + 0.36f);

                var contentBottom = MathF.Max(avatarPos.Y + avatarSize + 6f * scale,
                    statusY + lineH + 4f * scale);
                for (var row = 0; row < statsRows; row++)
                {
                    var rowW = cardWidth - 12f * scale;
                    var rowMin = new Vector2(cardMin.X + 6f * scale, contentBottom);
                    SkeletonBlock(drawList, rowMin,
                        new Vector2(rowMin.X + rowW * (row == 0 ? 0.7f : 0.5f),
                            rowMin.Y + lineH), barR, phase + 0.5f + row * 0.15f);
                    contentBottom += lineH + 6f * scale;
                }

                ImGui.SetCursorScreenPos(new Vector2(cardMin.X, contentBottom));
                ImGui.Dummy(Vector2.Zero);
            }, accent, animatedBackdrop: false, id: "profile-hero-skeleton",
            topBleed: (dl, cMin, cMax, _) =>
            {
                var bleedMax = new Vector2(cMax.X, cMin.Y + bleedHeight);
                dl.PushClipRect(cMin, bleedMax, true);
                Banner(dl, cMin, bleedMax, null, SpheneProfileTheme.Default,
                    accent, accent, rounding: topRounding, corners: ImDrawFlags.RoundCornersTop);
                SkeletonSheen(dl, cMin, bleedMax, phase);
                dl.PopClipRect();
                var fadeH = bleedHeight * HeroBannerBodyFade;
                var fadeClear = ColorUtils.ToU32(ColorUtils.WithAlpha(
                    Layout.AccentCardTint(accent), 0f));
                var fadeSolid = ColorUtils.ToU32(Layout.AccentCardTint(accent));
                dl.AddRectFilledMultiColor(new Vector2(cMin.X, bleedMax.Y - fadeH),
                    bleedMax, fadeClear, fadeClear, fadeSolid, fadeSolid);
            });
        }

        /// <summary>
        /// One skeleton placeholder block: pulsing fill, a traveling diagonal
        /// sheen clipped to the block, and a faint outline. <paramref name="phase"/>
        /// offsets the sheen so stacked blocks do not flash in lockstep.
        /// </summary>
        private static void SkeletonBlock(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float rounding, float phase)
        {
            var pulse = 0.055f + 0.03f * (0.5f + 0.5f * MathF.Sin(phase * 2.4f));
            drawList.AddRectFilled(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, pulse)), rounding);
            SkeletonSheen(drawList, min, max, phase);
            drawList.AddRect(min, max,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.06f)), rounding);
        }

        /// <summary>Diagonal light sweep moving left to right inside the rect.</summary>
        private static void SkeletonSheen(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float phase)
        {
            var w = max.X - min.X;
            var sweepW = MathF.Max(20f * Scale, w * 0.4f);
            var span = w + sweepW;
            var x = min.X - sweepW + (phase * 70f * Scale % span);
            var sheen = ColorUtils.WithAlpha(Vector4.One, 0.09f);
            var clear = ColorUtils.WithAlpha(Vector4.One, 0f);
            drawList.PushClipRect(min, max, true);
            drawList.AddRectFilledMultiColor(
                new Vector2(x - sweepW * 0.35f, min.Y), new Vector2(x + sweepW, max.Y),
                ColorUtils.ToU32(clear), ColorUtils.ToU32(sheen),
                ColorUtils.ToU32(clear), ColorUtils.ToU32(clear));
            drawList.PopClipRect();
        }

        /// <summary>Measured size of the NSFW chip — grows by a check glyph when <paramref name="active"/>.</summary>
        public static Vector2 NsfwChipSize(bool active, float scale)
        {
            var size = ImGui.CalcTextSize("NSFW") + new Vector2(10f, 3f) * scale;
            if (active)
            {
                size.X += Icons.Size(FontAwesomeIcon.Check).X + 3f * scale;
            }
            return size;
        }

        /// <summary>
        /// Draws the NSFW chip at an explicit position: red when
        /// <paramref name="active"/> (with a check mark so the enabled state is
        /// unambiguous), muted outline otherwise. <paramref name="hoverT"/>
        /// feeds the same eased hover emphasis as <see cref="Fx.HoverT"/> rows.
        /// </summary>
        public static void NsfwChipAt(ImDrawListPtr drawList, Vector2 pos, bool active,
            float scale, float hoverT = 0f)
        {
            var chipSize = NsfwChipSize(active, scale);
            var fill = active
                ? ColorUtils.WithAlpha(Colors.Danger, 0.40f + 0.15f * hoverT)
                : ColorUtils.WithAlpha(Colors.TextMuted, 0.16f + 0.14f * hoverT);
            var border = active ? Colors.Danger : Colors.TextMuted;
            drawList.AddRectFilled(pos, pos + chipSize, ColorUtils.ToU32(fill), chipSize.Y * 0.5f);
            drawList.AddRect(pos, pos + chipSize,
                ColorUtils.ToU32(ColorUtils.WithAlpha(border, active ? 0.8f : 0.4f)),
                chipSize.Y * 0.5f);
            drawList.AddText(pos + new Vector2(5f * scale, 1.5f * scale),
                ColorUtils.ToU32(active ? Colors.Danger : Colors.TextMuted), "NSFW");
            if (active)
            {
                var checkSize = Icons.Size(FontAwesomeIcon.Check);
                using (Icons.PushFont())
                {
                    drawList.AddText(
                        new Vector2(pos.X + chipSize.X - checkSize.X - 4f * scale,
                            pos.Y + (chipSize.Y - checkSize.Y) * 0.5f),
                        ColorUtils.ToU32(Colors.Danger), FontAwesomeIcon.Check.ToIconString());
                }
            }
        }

        /// <summary>NSFW badge chip next to the profile name; a clickable toggle when editable.</summary>
        private static void DrawNsfwChip(bool isNsfw, bool clickable, float scale, Action onToggle)
        {
            ImGui.SameLine(0f, 8f * scale);
            var drawList = ImGui.GetWindowDrawList();
            var chipSize = NsfwChipSize(isNsfw, scale);
            var pos = ImGui.GetCursorScreenPos();
            var hovered = false;
            var clicked = false;
            uint id = 0;
            if (clickable)
            {
                id = ImGui.GetID("##hero-nsfw");
                clicked = ImGui.InvisibleButton("##hero-nsfw", chipSize);
                hovered = ImGui.IsItemHovered();
            }
            else
            {
                ImGui.Dummy(chipSize);
            }
            var t = Fx.HoverT(id, hovered, 10f);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (ImGui.IsItemHovered())
            {
                AttachTooltip(clickable
                    ? (isNsfw ? "Unmark your profile as NSFW" : "Mark your profile as NSFW")
                    : "This profile is marked NSFW");
            }
            NsfwChipAt(drawList, pos, isNsfw, scale, clickable ? t : 0f);
            if (clicked)
            {
                onToggle();
            }
        }

        /// <summary>
        /// Non-edit name line: the big font when it fits the column, the
        /// display font (~26px) when it does not, ellipsized with a tooltip
        /// as the last resort — long names never bleed past the card edge.
        /// </summary>
        private static void DrawFittedHeroName(string displayName, Vector4 accent, float availW)
        {
            var fitsBig = false;
            if (UidFont is { } bigFont)
            {
                using (bigFont.Push())
                {
                    fitsBig = ImGui.CalcTextSize(displayName).X <= availW;
                }
            }
            else
            {
                fitsBig = ImGui.CalcTextSize(displayName).X <= availW;
            }
            if (fitsBig)
            {
                Text.Big(displayName, accent);
                return;
            }

            string shown;
            if (SpheneUI.DisplayFont is { } displayFont)
            {
                using (displayFont.Push())
                {
                    shown = Text.Ellipsize(displayName, availW);
                }
                Text.Display(shown, accent);
            }
            else
            {
                shown = Text.Ellipsize(displayName, availW);
                Text.Colored(shown, accent);
            }
            if (shown.Length != displayName.Length)
            {
                AttachTooltip(displayName);
            }
        }

        /// <summary>
        /// Name line plus the visible meta/handle line under it. In edit mode
        /// (own profile) both become click-to-edit inputs committing through
        /// <see cref="HeroEdit.OnDisplayNameCommitted"/> /
        /// <see cref="HeroEdit.OnHandleCommitted"/>; a locked handle shows the
        /// hint instead of opening the input.
        /// </summary>
        private static void DrawHeroName(string displayName, string metaLine,
            Vector4 accent, HeroEdit? edit, float inputWidth, float scale)
        {
            var editing = edit?.Enabled == true;
            if (editing && edit!.OnDisplayNameCommitted != null && edit.EditingDisplayName)
            {
                if (edit.NameFocusPending)
                {
                    ImGui.SetKeyboardFocusHere();
                    edit.NameFocusPending = false;
                }
                // bare in-place edit: the input inherits the big name font and
                // drops the frame chrome, so the line keeps its size — an
                // accent underline marks it as the active edit
                var committed = DrawInPlaceInput("##hero-displayname",
                    "Display name — Enter applies, empty removes it", edit.DisplayNameBuffer,
                    edit.DisplayNameMaxLength, ImGuiInputTextFlags.EnterReturnsTrue,
                    inputWidth, accent, scale, out var nameBuf, out var nameCancelled, font: UidFont);
                edit.DisplayNameBuffer = nameBuf;
                if (nameCancelled)
                {
                    edit.EditingDisplayName = false;
                    edit.DisplayNameBuffer = edit.DisplayNameCurrent ?? string.Empty;
                }
                else if (committed)
                {
                    edit.EditingDisplayName = false;
                    var trimmed = nameBuf.Trim();
                    if (!string.Equals(trimmed, edit.DisplayNameCurrent ?? string.Empty,
                            StringComparison.Ordinal))
                    {
                        edit.PendingDisplayName = trimmed;
                        edit.DisplayNameConfirm.Open(
                            () => edit.OnDisplayNameCommitted?.Invoke(trimmed));
                    }
                }
            }
            else
            {
                DrawFittedHeroName(displayName, accent, ImGui.GetContentRegionAvail().X);
                if (editing && edit!.OnDisplayNameCommitted != null)
                {
                    var nameHeight = ImGui.GetItemRectSize().Y;
                    var hovered = ImGui.IsItemHovered();
                    var clicked = ImGui.IsItemClicked();
                    var pen = DrawEditPen(nameHeight, accent, scale);
                    hovered |= pen.Hovered;
                    clicked |= pen.Clicked;
                    if (hovered)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
                    }
                    if (clicked)
                    {
                        edit.EditingDisplayName = true;
                        edit.NameFocusPending = true;
                    }
                }
            }

            var canEditHandle = editing && edit!.OnHandleCommitted != null
                && metaLine.StartsWith('@');
            if (canEditHandle && edit!.EditingHandle)
            {
                Text.Colored("@", Palette.CrystalWhite);
                ImGui.SameLine(0f, 1f * scale);
                // focus request must sit directly before the input — the "@"
                // prefix item would otherwise consume it and the field never
                // becomes active, leaving the edit stuck open
                if (edit.HandleFocusPending)
                {
                    ImGui.SetKeyboardFocusHere();
                    edit.HandleFocusPending = false;
                }
                var committed = DrawInPlaceInput("##hero-handle", null,
                    edit.HandleBuffer, edit.HandleMaxLength,
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsNoBlank,
                    MathF.Max(120f, 160f * scale), accent, scale, out var handleBuf,
                    out var handleCancelled);
                edit.HandleBuffer = handleBuf;
                if (handleCancelled)
                {
                    edit.EditingHandle = false;
                    edit.HandleBuffer = edit.HandleCurrent ?? string.Empty;
                }
                else if (committed)
                {
                    edit.EditingHandle = false;
                    var trimmed = handleBuf.Trim();
                    if (trimmed.Length > 0 && !string.Equals(trimmed,
                            edit.HandleCurrent ?? string.Empty, StringComparison.Ordinal))
                    {
                        edit.PendingHandle = trimmed;
                        edit.HandleConfirm.Open(
                            () => edit.OnHandleCommitted?.Invoke(trimmed));
                    }
                }
                if (!string.IsNullOrEmpty(edit.HandleHint))
                {
                    Text.Muted(edit.HandleHint);
                }
            }
            else if (metaLine.Length > 0)
            {
                var handleLine = metaLine.StartsWith('@');
                Text.Colored(metaLine, handleLine ? Palette.CrystalWhite : Colors.TextMuted);
                if (canEditHandle)
                {
                    var lineHeight = ImGui.GetItemRectSize().Y;
                    var hovered = ImGui.IsItemHovered();
                    var clicked = ImGui.IsItemClicked();
                    var pen = DrawEditPen(lineHeight, accent, scale);
                    hovered |= pen.Hovered;
                    clicked |= pen.Clicked;
                    if (hovered)
                    {
                        ImGui.SetMouseCursor(edit!.HandleLocked
                            ? ImGuiMouseCursor.NotAllowed : ImGuiMouseCursor.TextInput);
                    }
                    if (clicked)
                    {
                        if (edit!.HandleLocked)
                        {
                            edit.Feedback = edit.HandleHint;
                            edit.FeedbackColor = Colors.Warning;
                        }
                        else
                        {
                            edit.EditingHandle = true;
                            edit.HandleFocusPending = true;
                        }
                    }
                    AttachTooltip(edit!.HandleHint ?? "Click to rename your handle");
                }
                else if (handleLine)
                {
                    AttachTooltip("Handle — used for pair requests and profile lookup");
                }
            }

            if (editing && canEditHandle && !edit!.EditingHandle && !edit.HandleLocked
                && !string.IsNullOrEmpty(edit.HandleHint))
            {
                Text.Muted(edit.HandleHint);
            }
            if (!string.IsNullOrEmpty(edit?.Feedback))
            {
                Text.ColoredWrapped(edit!.Feedback!, edit.FeedbackColor);
            }
            if (edit != null)
            {
                edit.DisplayNameConfirm.Draw("hero-name", "Change display name?",
                    string.IsNullOrWhiteSpace(edit.PendingDisplayName)
                        ? "Remove your display name — you will be shown under your handle."
                        : $"Your display name will change to \"{edit.PendingDisplayName}\".",
                    confirmLabel: "Apply", icon: FontAwesomeIcon.Pen,
                    confirmIcon: FontAwesomeIcon.Check,
                    detail: edit.DisplayNameConfirmDetail);
                edit.HandleConfirm.Draw("hero-handle", "Rename handle?",
                    $"Your handle will change to \"@{edit.PendingHandle}\" — others pair and find you by it.",
                    confirmLabel: "Rename", icon: FontAwesomeIcon.At,
                    confirmIcon: FontAwesomeIcon.At,
                    detail: edit.HandleConfirmDetail);
            }
        }

        /// <summary>
        /// Small pen glyph right of the last item marking the line as
        /// click-to-edit — vertically centered against the line's height,
        /// accent on hover, muted otherwise. Returns the pen's own hover/click
        /// so callers can merge it with the text item's input state.
        /// </summary>
        private static (bool Hovered, bool Clicked) DrawEditPen(float lineHeight,
            Vector4 accent, float scale)
        {
            ImGui.SameLine(0f, 5f * scale);
            var pos = ImGui.GetCursorScreenPos();
            using var font = Icons.PushFont();
            var glyph = FontAwesomeIcon.Pen.ToIconString();
            var size = ImGui.CalcTextSize(glyph);
            pos += new Vector2(0f, MathF.Max(0f, (lineHeight - size.Y) * 0.5f));
            ImGui.SetCursorScreenPos(pos);
            ImGui.Dummy(size);
            var hovered = ImGui.IsItemHovered();
            var clicked = ImGui.IsItemClicked();
            ImGui.GetWindowDrawList().AddText(pos,
                ColorUtils.ToU32(hovered ? accent : Colors.TextMuted), glyph);
            return (hovered, clicked);
        }

        /// <summary>
        /// InputText that looks like the text it replaces: transparent frame,
        /// zero padding and an accent caret-line — so click-to-edit keeps the
        /// exact visual size of the line instead of swapping in a boxed field.
        /// Returns true on Enter or a click-away that kept edits (caller routes
        /// through the confirm dialog); <paramref name="cancelled"/> is set on
        /// Escape or a click-away without changes — the caller then discards
        /// the edit instead of saving it.
        /// </summary>
        private static bool DrawInPlaceInput(string id, string? hint, string value,
            int maxLength, ImGuiInputTextFlags flags, float width, Vector4 accent,
            float scale, out string outValue, out bool cancelled, IFontHandle? font = null)
        {
            var buf = value;
            bool committed;
            using (font?.Push())
            using (ImRaii.PushColor(ImGuiCol.FrameBg, 0u)
                       .Push(ImGuiCol.FrameBgHovered, 0u)
                       .Push(ImGuiCol.FrameBgActive, 0u)
                       .Push(ImGuiCol.Border, 0u))
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero))
            {
                ImGui.SetNextItemWidth(width);
                committed = hint != null
                    ? ImGui.InputTextWithHint(id, hint, ref buf, maxLength, flags)
                    : ImGui.InputText(id, ref buf, maxLength, flags);
                cancelled = false;
                if (!committed && ImGui.IsItemDeactivated())
                {
                    // deactivated with edits = click-away save; without = Esc or plain click-away
                    if (ImGui.IsItemDeactivatedAfterEdit()) committed = true;
                    else cancelled = true;
                }
                // a field that never gained focus (e.g. a focus request eaten by
                // a sibling item) cannot deactivate — still let a click elsewhere
                // drop the edit instead of leaving it stuck open
                if (!committed && !cancelled
                    && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                    && !ImGui.IsItemHovered() && !ImGui.IsItemActive())
                {
                    cancelled = true;
                }
            }
            var itemMin = ImGui.GetItemRectMin();
            var itemMax = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(itemMin.X, itemMax.Y), new Vector2(itemMax.X, itemMax.Y),
                ColorUtils.ToU32(accent), MathF.Max(1f, scale));
            outValue = buf;
            return committed;
        }

        /// <summary>
        /// Tag chips row with inline editing when <paramref name="edit"/> is set
        /// and enabled: chips get a hoverable "×" remove affordance and a "+"
        /// chip opens an inline input. Commits call <see cref="HeroEdit.OnTagsCommitted"/>
        /// with the full comma-joined list.
        /// </summary>
        private static void DrawHeroTags(string? tags, HeroEdit? edit, Vector4 accent, float scale)
        {
            if (edit == null || !edit.Enabled)
            {
                Controls.TagChips(tags);
                return;
            }
            var drawList = ImGui.GetWindowDrawList();
            var avail = ImGui.GetContentRegionAvail().X;
            var padX = 7f * scale;
            var padY = 2f * scale;
            var chipH = ImGui.GetTextLineHeight() + padY * 2f;
            const string removeGlyph = "×";
            var removeW = ImGui.CalcTextSize(removeGlyph).X + 4f * scale;
            var startX = ImGui.GetCursorScreenPos().X;
            var startY = ImGui.GetCursorScreenPos().Y;
            var xMax = startX + avail;
            var x = startX;
            var y = startY;

            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(tags))
            {
                list.AddRange(tags.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            string? removed = null;
            var shown = Math.Min(list.Count, 10);
            for (var i = 0; i < shown; i++)
            {
                var tag = list[i];
                var chipW = ImGui.CalcTextSize(tag).X + padX * 2f + removeW;
                if (x + chipW > xMax)
                {
                    x = startX;
                    y += chipH + 4f * scale;
                }
                var chipMin = new Vector2(x, y);
                var chipSize = new Vector2(chipW, chipH);
                ImGui.SetCursorScreenPos(chipMin);
                var clicked = ImGui.InvisibleButton($"##hero-tag-{i}", chipSize);
                var hovered = ImGui.IsItemHovered();
                var overRemove = hovered
                    && ImGui.GetIO().MousePos.X >= chipMin.X + chipW - removeW;
                if (hovered)
                {
                    if (overRemove)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    }
                    AttachTooltip(overRemove ? $"Remove \"{tag}\"" : tag);
                }
                drawList.AddRectFilled(chipMin, chipMin + chipSize,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, hovered ? 0.22f : 0.14f)),
                    chipH * 0.5f);
                drawList.AddRect(chipMin, chipMin + chipSize,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, hovered ? 0.5f : 0.35f)),
                    chipH * 0.5f);
                drawList.AddText(chipMin + new Vector2(padX, padY),
                    ColorUtils.ToU32(Colors.TextPrimary), tag);
                if (hovered)
                {
                    drawList.AddText(chipMin + new Vector2(chipW - removeW + 2f * scale, padY),
                        ColorUtils.ToU32(Colors.Danger), removeGlyph);
                }
                if (clicked && overRemove)
                {
                    removed = tag;
                }
                x = chipMin.X + chipW + 6f * scale;
            }

            if (!edit.AddingTag)
            {
                var plusW = chipH;
                if (x + plusW > xMax)
                {
                    x = startX;
                    y += chipH + 4f * scale;
                }
                var plusMin = new Vector2(x, y);
                ImGui.SetCursorScreenPos(plusMin);
                var plusClicked = ImGui.InvisibleButton("##hero-tag-add", new Vector2(plusW, chipH));
                var plusHovered = ImGui.IsItemHovered();
                if (plusHovered)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    AttachTooltip("Add a tag");
                }
                drawList.AddRect(plusMin, plusMin + new Vector2(plusW, chipH),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, plusHovered ? 0.6f : 0.35f)),
                    chipH * 0.5f, ImDrawFlags.None, MathF.Max(1f, scale));
                var plusSize = ImGui.CalcTextSize("+");
                drawList.AddText(plusMin + (new Vector2(plusW, chipH) - plusSize) * 0.5f,
                    ColorUtils.ToU32(plusHovered ? accent : Colors.TextSecondary), "+");
                if (plusClicked)
                {
                    edit.AddingTag = true;
                    edit.NewTagBuffer = string.Empty;
                    edit.TagInputFocusPending = true;
                }
            }
            else
            {
                var inputW = MathF.Min(160f * scale, xMax - x);
                if (inputW < 60f * scale)
                {
                    x = startX;
                    y += chipH + 4f * scale;
                    inputW = MathF.Min(160f * scale, xMax - x);
                }
                ImGui.SetCursorScreenPos(new Vector2(x, y));
                ImGui.SetNextItemWidth(inputW);
                if (edit.TagInputFocusPending)
                {
                    ImGui.SetKeyboardFocusHere();
                    edit.TagInputFocusPending = false;
                }
                var buf = edit.NewTagBuffer;
                var commit = ImGui.InputTextWithHint("##hero-newtag", "New tag…", ref buf, 30,
                        ImGuiInputTextFlags.EnterReturnsTrue)
                    || ImGui.IsItemDeactivatedAfterEdit();
                edit.NewTagBuffer = buf;
                if (commit)
                {
                    var trimmed = buf.Trim();
                    edit.AddingTag = false;
                    edit.NewTagBuffer = string.Empty;
                    if (trimmed.Length > 0
                        && !list.Exists(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(trimmed);
                        edit.OnTagsCommitted?.Invoke(string.Join(", ", list));
                    }
                }
            }

            if (removed != null)
            {
                list.RemoveAll(t => string.Equals(t, removed, StringComparison.OrdinalIgnoreCase));
                edit.OnTagsCommitted?.Invoke(string.Join(", ", list));
            }

            ImGui.SetCursorScreenPos(new Vector2(startX, startY));
            ImGui.Dummy(new Vector2(avail, y + chipH - startY + 2f * scale));
        }

        /// <summary>
        /// Toggleable theme color in one settings row: a
        /// <see cref="Controls.ColorSwatch"/> leading on the LEFT, then label
        /// + slide toggle. The swatch is always rendered (dimmed while
        /// disabled) so every toggle's label column and switch track stay
        /// vertically aligned with the rows around it — optional controls
        /// must never push the switch. Design-system rule for all future
        /// settings rows that combine a toggle with extra controls.
        /// </summary>
        public static void ThemeColorRow(string label, ref bool enabled, ref Vector4 color)
        {
            var scale = Scale;
            var swatch = Metrics.SwatchSize;
            var gap = 8f * scale;
            var rowPos = ImGui.GetCursorScreenPos();
            var frameH = ImGui.GetFrameHeight();
            var pickerId = $"##themepick_{label}";

            ImGui.SetCursorScreenPos(rowPos + new Vector2(0f, (frameH - swatch) * 0.5f));
            var swatchCol = enabled ? color : ColorUtils.WithAlpha(color, 0.40f);
            var swatchClicked = Controls.ColorSwatch($"##themesw_{label}", swatchCol);
            if (enabled && swatchClicked)
            {
                // inside a dropdown the native OpenPopup/BeginPopup pair loses
                // its first frame against the menu's own Popup layer — the
                // flyout is a plain pinned window that opens on the first click
                if (Popups.InsideDropdown)
                {
                    Popups.ToggleFlyout(pickerId);
                }
                else
                {
                    ImGui.OpenPopup(pickerId);
                }
            }
            SpheneUI.AttachTooltip(enabled
                ? "Click to pick a color."
                : "Enable the toggle to pick a color.");
            if (enabled)
            {
                var pickerOpen = Popups.InsideDropdown
                    ? Popups.BeginFlyout(pickerId)
                    : Popups.BeginStyled(pickerId);
                if (pickerOpen)
                {
                    ImGui.ColorPicker4($"##themecp_{label}", ref color,
                        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoLabel);
                    if (Popups.InsideDropdown)
                    {
                        Popups.EndFlyout();
                    }
                    else
                    {
                        Popups.EndStyled();
                    }
                }
            }

            ImGui.SetCursorScreenPos(rowPos + new Vector2(swatch + gap, 0f));
            Controls.Toggle(label, ref enabled, trailingWidth: 0f);
            // restore the cursor below the row — swatch and toggle were both
            // drawn inside the same frame-high band
            ImGui.SetCursorScreenPos(rowPos + new Vector2(0f,
                frameH + ImGui.GetStyle().ItemSpacing.Y));
        }

        /// <summary>Aspect of the stored row-image strip — exactly the displayed
        /// area (max 512×128), so the crop selection never wastes pixels.</summary>
        public const float RowImageAspect = 4f;

        /// <summary>Fraction of the row-image strip where the right-edge fade starts.</summary>
        public const float RowImageFadeStart = 0.55f;

        /// <summary>
        /// Draws a supporter's animated row image into <paramref name="min"/>..
        /// <paramref name="max"/>: cover-fitted, dimmed for readability, and
        /// fading to TRUE transparency toward the right edge — the image's own
        /// vertex alpha dissolves so the row background shows through instead
        /// of blending into a solid color. Tessellated as a textured perimeter
        /// fan so corner rounding, atlas UVs and per-vertex alpha are all
        /// baked into the mesh.
        /// </summary>
        public static void RowImageStrip(ImDrawListPtr drawList,
            Media.AnimatedImage anim, Vector2 min, Vector2 max, float radius,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            var size = max - min;
            if (size.X <= 0f || size.Y <= 0f) return;

            var idx = anim.FrameIndexAt(ImGui.GetTime());
            Media.FitRect(min, max, new Vector2(anim.FrameWidth, anim.FrameHeight),
                ImageFit.Cover, out var rMin, out var rMax, out var uv0, out var uv1);
            var frameW = 1f / anim.FrameCount;
            var au0 = new Vector2((idx + uv0.X) * frameW, uv0.Y);
            var au1 = new Vector2((idx + uv1.X) * frameW, uv1.Y);

            var r = MathF.Min(radius, MathF.Min(size.X, size.Y) * 0.5f);
            var fadeStart = rMin.X + (rMax.X - rMin.X) * RowImageFadeStart;
            var invFade = 1f / MathF.Max(1f, rMax.X - fadeStart);
            var invW = 1f / (rMax.X - rMin.X);
            var invH = 1f / (rMax.Y - rMin.Y);
            const int ringCount = 32;
            Span<Vector2> ring = stackalloc Vector2[ringCount];
            var n = 0;
            // the strip's left corners follow the caller's silhouette; its
            // right edge is interior (fades out), so those stay rounded
            n = AppendStripArc(ring, n, new Vector2(rMin.X, rMin.Y), new Vector2(r, r),
                MathF.PI, r, corners.HasFlag(ImDrawFlags.RoundCornersTopLeft));
            n = AppendStripArc(ring, n, new Vector2(rMax.X, rMin.Y), new Vector2(-r, r),
                -MathF.PI * 0.5f, r, true);
            n = AppendStripArc(ring, n, new Vector2(rMax.X, rMax.Y), new Vector2(-r, -r),
                0f, r, true);
            AppendStripArc(ring, n, new Vector2(rMin.X, rMax.Y), new Vector2(r, -r),
                MathF.PI * 0.5f, r, corners.HasFlag(ImDrawFlags.RoundCornersBottomLeft));

            var center = (rMin + rMax) * 0.5f;
            const float baseAlpha = 0.55f;
            const float dim = 0.70f; // readability scrim baked into the vertex color
            Vector2 Uv(Vector2 p) => new(
                au0.X + Math.Clamp((p.X - rMin.X) * invW, 0f, 1f) * (au1.X - au0.X),
                au0.Y + Math.Clamp((p.Y - rMin.Y) * invH, 0f, 1f) * (au1.Y - au0.Y));
            uint Shade(Vector2 p)
            {
                var a = p.X <= fadeStart
                    ? 1f
                    : 1f - Math.Clamp((p.X - fadeStart) * invFade, 0f, 1f);
                return ColorUtils.ToU32(new Vector4(dim, dim, dim, baseAlpha * a));
            }

            drawList.PushTextureID(anim.Atlas.Handle);
            try
            {
                drawList.PrimReserve(ringCount * 3, 1 + ringCount);
                var baseVtx = (int)drawList.VtxCurrentIdx;
                drawList.PrimWriteVtx(center, Uv(center), Shade(center));
                for (var k = 0; k < ringCount; k++)
                {
                    var p = ring[k];
                    drawList.PrimWriteVtx(p, Uv(p), Shade(p));
                }
                for (var k = 0; k < ringCount; k++)
                {
                    var v1 = (ushort)(baseVtx + 1 + k);
                    var v2 = (ushort)(baseVtx + 1 + ((k + 1) % ringCount));
                    drawList.PrimWriteIdx((ushort)baseVtx);
                    drawList.PrimWriteIdx(v1);
                    drawList.PrimWriteIdx(v2);
                }
            }
            finally
            {
                drawList.PopTextureID();
            }
        }

        private static int AppendStripArc(Span<Vector2> ring, int i, Vector2 corner,
            Vector2 centerOffset, float startAngle, float r, bool rounded)
        {
            const int segs = 8;
            for (var s = 0; s < segs; s++)
            {
                if (!rounded)
                {
                    ring[i++] = corner;
                    continue;
                }
                var a = startAngle + MathF.PI * 0.5f * (s / (float)segs);
                ring[i++] = corner + centerOffset
                    + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            }
            return i;
        }

        /// <summary>
        /// The complete list-row backdrop exactly as viewers see it — the
        /// owner's animated row image (true alpha fade) under their combinable
        /// idle + hover effect layers. Shared by the friends list and the
        /// settings preview so the two can never drift apart.
        /// </summary>
        public static void RowBackdrop(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 accent, bool hovered, float hoverT, SpheneProfileTheme theme,
            Media.AnimatedImage? rowImage, float radius,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
            => RowBackdrop(drawList, min, max, accent, hovered, hoverT,
                theme.RowHover, theme.RowIdle, rowImage, radius, corners);

        /// <summary>
        /// Flag-composed variant of the shared row backdrop — same render
        /// layers and draw order as the friends list, driven by explicit
        /// effect flags instead of a theme.
        /// </summary>
        public static void RowBackdrop(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 accent, bool hovered, float hoverT,
            ProfileRowFx hover, ProfileRowFx idle,
            Media.AnimatedImage? rowImage, float radius,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            if (rowImage != null)
            {
                var stripW = MathF.Min((max.Y - min.Y) * RowImageAspect, max.X - min.X);
                RowImageStrip(drawList, rowImage, min,
                    min + new Vector2(stripW, max.Y - min.Y), radius, corners);
            }
            RowFx(drawList, min, max, accent, hovered, hoverT, hover, idle, radius, corners);
        }

        /// <summary>The data a friends-list row displays — avatar, name,
        /// presence line, optional status message and NSFW flag.</summary>
        public readonly record struct FriendRowContent(
            IDalamudTextureWrap? Avatar, string Name, string Presence,
            Vector4 PresenceColor, string? StatusMessage, bool IsNsfw);

        /// <summary>
        /// The complete friends-list row exactly as viewers see it — shared
        /// render path for the hub's friends list AND the style preview so
        /// the two can never drift apart: row backdrop (animated image under
        /// the owner's effect layers), avatar with accent ring, name, presence
        /// + status message, NSFW chip and the hover chevron. Callers own the
        /// row's hit-test and pass <paramref name="hovered"/>/
        /// <paramref name="hoverT"/> in; drawing restores the cursor below
        /// the row.
        /// </summary>
        public static void FriendRow(ImDrawListPtr drawList, Vector2 pos, float width,
            float height, FriendRowContent content, Vector4 accent, bool hovered,
            float hoverT, SpheneProfileTheme theme, Media.AnimatedImage? rowImage,
            float radius)
        {
            var scale = Scale;
            var rowMax = pos + new Vector2(width, height);
            RowBackdrop(drawList, pos, rowMax, accent, hovered, hoverT, theme,
                rowImage, radius);

            var avatarSize = 32f * scale;
            var avatarPos = pos + new Vector2(7f * scale, (height - avatarSize) * 0.5f);
            ImGui.SetCursorScreenPos(avatarPos);
            Media.Avatar(content.Avatar, avatarSize);
            drawList.AddRect(avatarPos, avatarPos + new Vector2(avatarSize),
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.55f)),
                avatarSize * 0.5f, ImDrawFlags.RoundCornersAll,
                MathF.Max(1f, 1.25f * scale));

            var lineH = ImGui.GetTextLineHeight();
            var textX = avatarPos.X + avatarSize + 9f * scale;

            // right edge: NSFW chip (when tagged) + chevron sliding in on hover
            var chevron = "›";
            var chevW = ImGui.CalcTextSize(chevron).X;
            var rightX = pos.X + width - 10f * scale;
            var chipW = 0f;
            if (content.IsNsfw)
            {
                var chipSize = NsfwChipSize(true, scale);
                chipW = chipSize.X + 8f * scale;
                NsfwChipAt(drawList,
                    new Vector2(rightX - chevW - 6f * scale - chipSize.X,
                        pos.Y + (height - chipSize.Y) * 0.5f),
                    true, scale);
            }
            drawList.AddText(
                new Vector2(rightX - chevW - (1f - hoverT) * 5f * scale,
                    pos.Y + (height - lineH) * 0.5f),
                ColorUtils.ToU32(
                    ColorUtils.WithAlpha(Colors.TextMuted, 0.45f + 0.55f * hoverT)),
                chevron);

            var textMaxX = rightX - chevW - chipW - 8f * scale;
            drawList.AddText(new Vector2(textX, pos.Y + 6f * scale),
                ColorUtils.ToU32(Colors.TextPrimary),
                Text.Ellipsize(content.Name, textMaxX - textX));

            var secondY = pos.Y + 6f * scale + lineH + 1f * scale;
            drawList.AddText(new Vector2(textX, secondY),
                ColorUtils.ToU32(content.PresenceColor), content.Presence);
            if (!string.IsNullOrWhiteSpace(content.StatusMessage))
            {
                var presenceW = ImGui.CalcTextSize(content.Presence).X;
                drawList.AddText(new Vector2(textX + presenceW + 6f * scale, secondY),
                    ColorUtils.ToU32(Colors.TextSecondary),
                    Text.Ellipsize($"· {content.StatusMessage.Trim()}",
                        textMaxX - textX - presenceW - 6f * scale));
            }

            ImGui.SetCursorScreenPos(pos + new Vector2(0f, height));
        }

        /// <summary>
        /// Renders the row owner's chosen list effects behind row content:
        /// the theme's idle layer (always visible, even unhovered) plus the
        /// hover layer driven by the eased <paramref name="hoverT"/>. Pass the
        /// *row owner's* theme — never the viewer's — and draw any animated
        /// row image beneath this call.
        /// </summary>
        public static void RowFx(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 accent,
            bool hovered, float hoverT, SpheneProfileTheme theme, float radius,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
            => RowFx(drawList, min, max, accent, hovered, hoverT,
                theme.RowHover, theme.RowIdle, radius, corners);

        /// <summary>
        /// Flag-composed variant of <see cref="RowFx(ImDrawListPtr, Vector2, Vector2, Vector4, bool, float, SpheneProfileTheme, float)"/>:
        /// every set bit in <paramref name="hover"/>/<paramref name="idle"/>
        /// draws its own layer, so owners can stack effects. Draw order is
        /// deterministic — base wash, idle layers, then hover layers on top.
        /// </summary>
        public static void RowFx(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 accent,
            bool hovered, float hoverT, ProfileRowFx hover, ProfileRowFx idle, float radius,
            ImDrawFlags corners = ImDrawFlags.RoundCornersAll)
        {
            var scale = Scale;
            var time = (float)ImGui.GetTime();
            idle &= ProfileRowFx.IdleMask;
            hover &= ProfileRowFx.HoverMask;
            if (hover != ProfileRowFx.None && hoverT > 0.001f)
            {
                drawList.AddRectFilled(min, max,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.02f + 0.04f * hoverT)),
                    radius, corners);
            }

            if (idle.HasFlag(ProfileRowFx.Tint))
            {
                // AddRectFilledMultiColor can't round — FillGradient emits a
                // vertex-colored fan so the tint respects the corner mask
                FillGradient(drawList, min, max,
                    ColorUtils.WithAlpha(accent, 0.07f),
                    ColorUtils.WithAlpha(accent, 0.02f),
                    ColorUtils.WithAlpha(accent, 0.02f),
                    ColorUtils.WithAlpha(accent, 0.07f), radius, corners);
            }
            if (idle.HasFlag(ProfileRowFx.Wave))
            {
                Fx.Sheen(drawList, min, max, (float)((time * 0.16) % 1.35),
                    90f * scale, ColorUtils.WithAlpha(accent, 0.05f), radius, corners);
            }
            if (idle.HasFlag(ProfileRowFx.Sparkle))
            {
                FxSparkle(drawList, min, max, accent, time, scale);
            }
            if (idle.HasFlag(ProfileRowFx.Pulse))
            {
                var breathe = 0.5f + 0.5f * MathF.Sin(time * 1.8f);
                drawList.AddRect(min, max,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.06f + 0.10f * breathe)),
                    radius, corners);
            }

            if (hover.HasFlag(ProfileRowFx.Lift) && hoverT > 0.001f)
            {
                var off = 2.5f * scale * hoverT;
                drawList.AddRectFilled(min + new Vector2(2f * scale, off + 1.5f * scale),
                    max + new Vector2(-2f * scale, off + 1.5f * scale),
                    ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.22f * hoverT)), radius, corners);
                drawList.AddLine(new Vector2(min.X + radius, min.Y + 0.5f),
                    new Vector2(max.X - radius, min.Y + 0.5f),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.10f * hoverT)), 1f);
            }
            if (hover.HasFlag(ProfileRowFx.Glow))
            {
                Fx.HoverRow(drawList, min, max, accent, hoverT, radius, corners);
            }
            if (hover.HasFlag(ProfileRowFx.Sweep) && hoverT > 0.001f)
            {
                FxBorderSweep(drawList, min, max, accent, hoverT, time, scale, radius, corners);
            }
            if (hover.HasFlag(ProfileRowFx.Sheen))
            {
                drawList.AddRect(min, max,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.05f + 0.20f * hoverT)),
                    radius, corners);
                Fx.Sheen(drawList, min, max,
                    hovered ? (float)((time * 0.9) % 1.5) : -1f,
                    42f * scale, ColorUtils.WithAlpha(accent, 0.09f), radius, corners);
            }
            if (hover.HasFlag(ProfileRowFx.Pulse))
            {
                var breathe = 0.5f + 0.5f * MathF.Sin(time * 4.5f);
                drawList.AddRect(min, max,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, (0.08f + 0.30f * breathe) * hoverT)),
                    radius, corners);
            }
        }

        /// <summary>Twinkling accent sparks at fixed relative spots — deterministic, no state.</summary>
        private static void FxSparkle(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 accent, float time, float scale)
        {
            for (var i = 0; i < 4; i++)
            {
                var px = min.X + (max.X - min.X) * (0.16f + i * 0.22f);
                var py = min.Y + (max.Y - min.Y) * (i % 2 == 0 ? 0.30f : 0.66f);
                var tw = 0.5f + 0.5f * MathF.Sin(time * (2.2f + i * 0.7f) + i * 2.1f);
                var alpha = 0.04f + 0.26f * tw;
                var p = new Vector2(px, py);
                var r = 1.4f * scale;
                drawList.AddCircleFilled(p, r,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, alpha)), 6);
                if (tw > 0.72f)
                {
                    var ray = r * 2.2f * (tw - 0.72f) / 0.28f;
                    var rayCol = ColorUtils.ToU32(ColorUtils.WithAlpha(accent, alpha * 0.9f));
                    drawList.AddLine(p - new Vector2(ray, 0f), p + new Vector2(ray, 0f), rayCol, 1f);
                    drawList.AddLine(p - new Vector2(0f, ray), p + new Vector2(0f, ray), rayCol, 1f);
                }
            }
        }

        /// <summary>Accent comet travelling the row's border — a fading dot trail.</summary>
        private static void FxBorderSweep(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 accent, float t, float time, float scale, float radius, ImDrawFlags corners)
        {
            var w = max.X - min.X;
            var h = max.Y - min.Y;
            var r = MathF.Min(radius, MathF.Min(w, h) * 0.5f);
            float rTL = corners.HasFlag(ImDrawFlags.RoundCornersTopLeft) ? r : 0f;
            float rTR = corners.HasFlag(ImDrawFlags.RoundCornersTopRight) ? r : 0f;
            float rBR = corners.HasFlag(ImDrawFlags.RoundCornersBottomRight) ? r : 0f;
            float rBL = corners.HasFlag(ImDrawFlags.RoundCornersBottomLeft) ? r : 0f;
            var arc = MathF.PI * 0.5f;
            var per = (w - rTL - rTR) + rTR * arc + (h - rTR - rBR) + rBR * arc
                + (w - rBR - rBL) + rBL * arc + (h - rBL - rTL) + rTL * arc;
            var head = (float)((time * 0.45) % 1f) * per;
            const int dots = 14;
            var segLen = per * 0.20f;
            for (var i = 0; i < dots; i++)
            {
                var k = 1f - (float)i / dots;
                var p = RowPerimeterPoint(min, max, w, h, per,
                    head - segLen * i / dots, rTL, rTR, rBR, rBL);
                drawList.AddCircleFilled(p, (0.9f + 1.5f * k) * scale,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.45f * k * t)), 8);
            }
        }

        /// <summary>Walks the rounded-rect perimeter clockwise from the top-left
        /// corner at distance <paramref name="d"/>; each corner radius may be 0
        /// (sharp) so the path matches the silhouette exactly.</summary>
        private static Vector2 RowPerimeterPoint(Vector2 min, Vector2 max, float w, float h,
            float per, float d, float rTL, float rTR, float rBR, float rBL)
        {
            d = ((d % per) + per) % per;
            var arc = MathF.PI * 0.5f;

            var seg = w - rTL - rTR;
            if (d < seg) return new Vector2(min.X + rTL + d, min.Y);
            d -= seg;
            seg = rTR * arc;
            if (d < seg) return ArcPoint(new Vector2(max.X - rTR, min.Y + rTR),
                rTR, -MathF.PI * 0.5f + d / seg * arc);
            d -= seg;
            seg = h - rTR - rBR;
            if (d < seg) return new Vector2(max.X, min.Y + rTR + d);
            d -= seg;
            seg = rBR * arc;
            if (d < seg) return ArcPoint(new Vector2(max.X - rBR, max.Y - rBR),
                rBR, d / seg * arc);
            d -= seg;
            seg = w - rBR - rBL;
            if (d < seg) return new Vector2(max.X - rBR - d, max.Y);
            d -= seg;
            seg = rBL * arc;
            if (d < seg) return ArcPoint(new Vector2(min.X + rBL, max.Y - rBL),
                rBL, MathF.PI * 0.5f + d / seg * arc);
            d -= seg;
            seg = h - rBL - rTL;
            if (d < seg) return new Vector2(min.X, max.Y - rBL - d);
            d -= seg;
            seg = rTL * arc;
            if (d < seg) return ArcPoint(new Vector2(min.X + rTL, min.Y + rTL),
                rTL, MathF.PI + d / seg * arc);
            return new Vector2(min.X + rTL, min.Y + rTL);
        }

        private static Vector2 ArcPoint(Vector2 center, float r, float angle)
            => center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;

        private static readonly (ProfileRowFx Bit, string Name)[] FxOrder =
        [
            (ProfileRowFx.Glow, "Glow"), (ProfileRowFx.Sheen, "Sheen"),
            (ProfileRowFx.Pulse, "Pulse"), (ProfileRowFx.Sweep, "Sweep"),
            (ProfileRowFx.Lift, "Lift"),
            (ProfileRowFx.Tint, "Tint"), (ProfileRowFx.Sparkle, "Sparkle"),
            (ProfileRowFx.Wave, "Wave"),
        ];

        /// <summary>
        /// Chip group for picking combinable <see cref="ProfileRowFx"/> bits:
        /// one rounded toggle pill per effect contained in
        /// <paramref name="allowed"/>, wrapping at the content bound. Active
        /// chips fill and outline in <paramref name="accent"/>.
        /// </summary>
        public static void FxChips(string groupLabel, ref ProfileRowFx value,
            ProfileRowFx allowed, Vector4 accent)
        {
            var scale = Scale;
            var drawList = ImGui.GetWindowDrawList();
            Text.Colored(groupLabel, Colors.TextSecondary);
            var chipH = ImGui.GetTextLineHeight() + 8f * scale;
            var padX = 10f * scale;
            var gap = 6f * scale;
            var cursor = ImGui.GetCursorScreenPos();
            var rightBound = Layout.ContentRightBound;
            if (rightBound <= 0f)
            {
                rightBound = cursor.X + ImGui.GetContentRegionAvail().X;
            }
            var startX = cursor.X;
            var x = startX;
            var lineY = cursor.Y;
            foreach (var (bit, name) in FxOrder)
            {
                if (!allowed.HasFlag(bit))
                {
                    continue;
                }
                var textW = ImGui.CalcTextSize(name).X;
                var chipW = textW + padX * 2f;
                if (x + chipW > rightBound && x > startX)
                {
                    x = startX;
                    lineY += chipH + gap;
                }
                var chipMin = new Vector2(x, lineY);
                var chipMax = chipMin + new Vector2(chipW, chipH);
                ImGui.SetCursorScreenPos(chipMin);
                var on = value.HasFlag(bit);
                var pressed = ImGui.InvisibleButton($"{groupLabel}##fxchip_{name}",
                    new Vector2(chipW, chipH));
                if (pressed)
                {
                    value = on ? value & ~bit : value | bit;
                }
                var hovered = ImGui.IsItemHovered();
                var fillA = on ? 0.16f : hovered ? 0.07f : 0.03f;
                var borderA = on ? 0.60f : hovered ? 0.35f : 0.18f;
                var chipR = chipH * 0.5f;
                drawList.AddRectFilled(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, fillA)), chipR);
                drawList.AddRect(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(accent, borderA)), chipR);
                drawList.AddText(chipMin + new Vector2(padX,
                    (chipH - ImGui.GetTextLineHeight()) * 0.5f),
                    ColorUtils.ToU32(on ? Colors.TextPrimary : Colors.TextSecondary), name);
                x += chipW + gap;
            }
            ImGui.SetCursorScreenPos(new Vector2(startX, lineY + chipH));
            ImGui.Dummy(new Vector2(0f, gap));
        }

        /// <summary>
        /// Dashboard-style stat tile: rounded tile with an accent-tinted icon
        /// plate, big value and muted label. Clickable when
        /// <paramref name="onClick"/> is provided.
        /// </summary>
        public static void StatTile(string label, FontAwesomeIcon icon, int value,
            Vector4 accent, string? tooltip = null, Action? onClick = null)
            => StatTile(label, icon, value.ToString(), accent, tooltip, onClick);

        /// <inheritdoc cref="StatTile(string, FontAwesomeIcon, int, Vector4, string?, Action?)"/>
        public static void StatTile(string label, FontAwesomeIcon icon, string value,
            Vector4 accent, string? tooltip = null, Action? onClick = null)
        {
            var scale = Scale;
            var drawList = ImGui.GetWindowDrawList();
            var pad = 10f * scale;
            var height = 58f * scale;
            var width = MathF.Max(40f * scale, ImGui.GetContentRegionAvail().X - 4f * scale);
            var tileMin = ImGui.GetCursorScreenPos() + new Vector2(2f * scale, 0f);
            var tileSize = new Vector2(width, height);
            var tileMax = tileMin + tileSize;

            ImGui.SetCursorScreenPos(tileMin);
            var tileId = ImGui.GetID($"##sphene-stat-{label}");
            var clicked = ImGui.InvisibleButton($"##sphene-stat-{label}", tileSize);
            var hovered = ImGui.IsItemHovered();
            var t = Fx.HoverT(tileId, hovered && onClick != null);

            var radius = 10f * scale;
            drawList.AddRectFilled(tileMin, tileMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One,
                    0.035f + 0.035f * t)), radius);
            drawList.AddRect(tileMin, tileMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent,
                    0.10f + 0.15f * t)), radius);

            var plate = 34f * scale;
            var plateMin = tileMin + new Vector2(pad, (height - plate) * 0.5f);
            drawList.AddRectFilled(plateMin, plateMin + new Vector2(plate),
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.16f)), plate * 0.3f);
            var iconSize = Icons.Size(icon);
            using (Icons.PushFont())
            {
                drawList.AddText(plateMin + (new Vector2(plate) - iconSize) * 0.5f,
                    ColorUtils.ToU32(accent), icon.ToIconString());
            }

            var lineH = ImGui.GetTextLineHeight();
            var valueText = value;
            Vector2 valueSize;
            using (UidFont.Push())
            {
                valueSize = ImGui.CalcTextSize(valueText);
            }
            var textX = plateMin.X + plate + pad;
            var textY = tileMin.Y + (height - valueSize.Y - lineH) * 0.5f;
            ImGui.SetCursorScreenPos(new Vector2(textX, textY));
            Text.Big(valueText, accent);
            ImGui.SetCursorScreenPos(new Vector2(textX, textY + valueSize.Y));
            Text.Muted(label);

            if (hovered)
            {
                if (onClick != null) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (tooltip != null) AttachTooltip(tooltip);
            }
            if (clicked)
            {
                onClick?.Invoke();
            }
            ImGui.SetCursorScreenPos(tileMin + new Vector2(0f, height + 4f * scale));
        }
    }
}
