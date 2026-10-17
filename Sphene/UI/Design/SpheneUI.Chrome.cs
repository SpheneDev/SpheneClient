using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.UI.Components;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Window chrome: custom title bars, collapse animations, window blur and
    /// the shared surface/border rendering used by cards and scroll areas.
    /// </summary>
    public static class Chrome
    {
        internal const float TitleBarPaddingX = 12f;
        internal const float TitleBarPaddingY = 7f;
        internal const float TitleBarButtonSpacing = 6f;
        internal const float TitleBarAccentLineHeight = 3f;
        internal const float TitleBarAccentLineAlpha = 0.45f;
        internal const float TitleBarBackgroundAlpha = 0.94f;
        internal const float TitleBarContentGap = 4f;

        internal const float CardBorderThickness = 1.5f;

        private const float CardBloomSpeed = 0.9f;
        private const int CardBloomRingCount = 22;
        private const float CardBloomRingStep = 2.2f;
        private const float CardBloomRingAlpha = 0.07f;
        private const int SectionCardMoteCount = 3;
        private const float SectionCardMoteSpeed = 1.4f;
        private const float SectionCardMoteAlpha = 0.10f;
        private const float SectionCardMoteRadius = 1.5f;

        internal const float ScrollFadeHeight = 22f;
        internal const float ScrollFadeMaxAlpha = 0.88f;
        internal const float ScrollFadeDeadZone = 4f;
        private const float ScrollHintChevronSize = 5f;
        private const float ScrollHintChevronAlpha = 0.95f;
        private const float ScrollHintPillPaddingX = 7f;
        private const float ScrollHintPillPaddingY = 3.5f;
        private const float ScrollHintPillAlpha = 0.8f;
        private const float ScrollHintPillBorderAlpha = 0.3f;
        private const float ScrollHintEdgeInset = 3f;
        private const float ScrollHintBounceAmplitude = 2.5f;
        private const float ScrollHintBounceSpeed = 2.2f;
        private const float ScrollFadeResizeGripScale = 1.35f;

        private const float WindowBlurMaxStrength = 14f;
        private const float WindowBlurNoiseOpacity = 0f;
        private static readonly Vector4 WindowBlurTint = new(0.02f, 0.02f, 0.03f, 0.08f);

        /// <summary>Total height of the custom title bar including its accent line.</summary>
        public static float TitleBarHeight =>
            ImGui.GetFrameHeight() + TitleBarPaddingY * 2f * SpheneUI.Scale;

        /// <summary>Title bar height in unscaled units — for <c>Window.Size</c> math.</summary>
        public static float TitleBarHeightUnscaled => TitleBarHeight / SpheneUI.Scale;

        /// <summary>A button on the right side of a <see cref="TitleBar"/>.</summary>
        /// <param name="BadgeCount">Notification count rendered as a badge dot on the button (0 = hidden).</param>
        public sealed record TitleBarButton(FontAwesomeIcon Icon, string Tooltip, Action OnClick,
            string? StyleKey = null, SpheneBrandIcons.Brand? BrandIcon = null, int BadgeCount = 0);

        private sealed class FoldState
        {
            public bool Collapsed;
            public float Amount;
            public Vector2 OpenSize;
            public WindowSizeConstraints? SavedConstraints;
            public Vector2? SavedSize;
            public ImGuiCond SavedSizeCondition;
            public Vector2? SavedPos;
            public ImGuiCond SavedPosCondition;
            public ImGuiWindowFlags SavedFlags;
            // Right edge + top of the window at toggle time — width-collapsing
            // windows shrink toward this anchor so the fold control stays
            // under the cursor.
            public Vector2 AnchorRightTop;
            // Set while the collapse animation forces the window position —
            // released once the animation settles so the strip drags again.
            public bool PositionPinned;
        }

        private const float CollapseDurationSeconds = 0.22f;
        private static readonly Vector2 FoldMaximumSize = new(10000f, 10000f);
        private static readonly Dictionary<string, FoldState> FoldStates = new(StringComparer.Ordinal);

        private static FoldState GetFold(string windowName)
        {
            if (!FoldStates.TryGetValue(windowName, out var s))
            {
                s = new FoldState();
                FoldStates[windowName] = s;
            }
            return s;
        }

        private static float EaseCollapse(float t)
            => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) * 0.5f;

        /// <summary>
        /// Call in <c>PreDraw</c> of a collapsible window before
        /// <c>base.PreDraw()</c>. Animates the window height between its open
        /// size and the title bar. <paramref name="collapsedWidthUnscaled"/>,
        /// when positive, shrinks the width alongside the height toward that
        /// collapsed size instead of keeping the open width.
        /// </summary>
        public static void UpdateCollapse(Window window, string windowName,
            float collapsedHeightUnscaled, float collapsedWidthUnscaled = 0f)
        {
            var fold = GetFold(windowName);
            var target = fold.Collapsed ? 1f : 0f;
            var animating = !MathF.Equals(fold.Amount, target);
            if (animating)
            {
                var step = ImGui.GetIO().DeltaTime / CollapseDurationSeconds;
                fold.Amount = fold.Collapsed ? MathF.Min(1f, fold.Amount + step) : MathF.Max(0f, fold.Amount - step);
            }

            var t = EaseCollapse(fold.Amount);
            if (t <= 0f)
            {
                if (fold.SavedConstraints != null)
                {
                    window.SizeConstraints = fold.SavedConstraints;
                    fold.SavedConstraints = null;
                    window.Size = fold.SavedSize;
                    window.SizeCondition = fold.SavedSizeCondition;
                    window.Position = fold.SavedPos;
                    window.PositionCondition = fold.SavedPosCondition;
                    window.Flags = fold.SavedFlags;
                    fold.PositionPinned = false;
                }
                return;
            }

            if (fold.SavedConstraints == null)
            {
                fold.SavedConstraints = window.SizeConstraints;
                fold.SavedSize = window.Size;
                fold.SavedSizeCondition = window.SizeCondition;
                fold.SavedPos = window.Position;
                fold.SavedPosCondition = window.PositionCondition;
                fold.SavedFlags = window.Flags;
            }
            window.SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = Vector2.One,
                MaximumSize = FoldMaximumSize,
            };

            if (fold.OpenSize.Y <= 0f)
            {
                return;
            }

            var scale = SpheneUI.Scale;
            var height = float.Lerp(fold.OpenSize.Y, collapsedHeightUnscaled * scale, t);
            var width = collapsedWidthUnscaled > 0f
                ? float.Lerp(fold.OpenSize.X, collapsedWidthUnscaled * scale, t)
                : window.Size?.X * scale ?? fold.OpenSize.X;
            window.Size = new Vector2(MathF.Max(width, 1f) / scale, MathF.Max(height, 1f) / scale);
            window.SizeCondition = ImGuiCond.Always;
            // While the size animates, pin the window so its right edge stays
            // on the toggle-time anchor — the fold button rides along under
            // the cursor instead of sliding away to the left. Once the
            // animation settles the position is released again so the
            // collapsed strip stays draggable.
            if (collapsedWidthUnscaled > 0f && animating && fold.AnchorRightTop != Vector2.Zero)
            {
                window.Position = new Vector2(fold.AnchorRightTop.X - width, fold.AnchorRightTop.Y);
                window.PositionCondition = ImGuiCond.Always;
                fold.PositionPinned = true;
            }
            else if (fold.PositionPinned)
            {
                window.Position = null;
                window.PositionCondition = fold.SavedPosCondition;
                fold.PositionPinned = false;
            }
            window.Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        }

        /// <summary>Fold amount 0..1 for the window (0 = fully open). Draw content with alpha fade while &lt; 1.</summary>
        public static float CollapseAmount(string windowName)
            => EaseCollapse(GetFold(windowName).Amount);

        /// <summary>True once the window is fully folded — skip drawing the content.</summary>
        public static bool IsCollapsed(string windowName)
            => CollapseAmount(windowName) >= 1f;

        /// <summary>Current fold target state — drives chevron icons on custom bars.</summary>
        internal static bool FoldCollapsed(string windowName)
            => GetFold(windowName).Collapsed;

        internal static void ToggleFold(string windowName)
        {
            var fold = GetFold(windowName);
            if (!fold.Collapsed && fold.Amount <= 0f)
            {
                fold.OpenSize = ImGui.GetWindowSize();
            }
            var pos = ImGui.GetWindowPos();
            fold.AnchorRightTop = new Vector2(pos.X + ImGui.GetWindowSize().X, pos.Y);
            fold.Collapsed = !fold.Collapsed;
        }

        /// <summary>
        /// Restores a folded window instantly — reopening a window that was
        /// closed while collapsed lands maximized, not mid-fold.
        /// </summary>
        public static void ResetFold(Window window, string windowName)
        {
            var fold = GetFold(windowName);
            fold.Collapsed = false;
            fold.Amount = 0f;
            if (fold.SavedConstraints != null)
            {
                window.SizeConstraints = fold.SavedConstraints;
                fold.SavedConstraints = null;
                window.Size = fold.SavedSize;
                window.SizeCondition = fold.SavedSizeCondition;
                window.Position = fold.SavedPos;
                window.PositionCondition = fold.SavedPosCondition;
                window.Flags = fold.SavedFlags;
                fold.PositionPinned = false;
            }
        }

        /// <summary>
        /// Draws a custom title bar at the current cursor position and moves the
        /// cursor below it. The bar area drags the window when clicked and held.
        /// Call as the first thing inside <c>DrawInternal</c> on windows that
        /// set <see cref="ImGuiWindowFlags.NoTitleBar"/>.
        /// </summary>
        public static void TitleBar(string windowName, string title,
            FontAwesomeIcon? leadingIcon = null, Vector4? accentColor = null,
            IReadOnlyList<TitleBarButton>? rightButtons = null, string? trailingText = null,
            bool collapsible = false, IDalamudTextureWrap? leadingTexture = null,
            Action<Vector2, Vector2>? middleContent = null)
        {
            if (collapsible)
            {
                var fold = GetFold(windowName);
                var toggle = new TitleBarButton(
                    fold.Collapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                    fold.Collapsed ? "Expand" : "Collapse",
                    () => ToggleFold(windowName));
                var buttons = rightButtons?.ToList() ?? [];
                // Keep leading brand-icon buttons grouped on the far left, collapse after them
                var insertAt = 0;
                while (insertAt < buttons.Count && buttons[insertAt].BrandIcon.HasValue)
                {
                    insertAt++;
                }
                buttons.Insert(insertAt, toggle);
                rightButtons = buttons;
            }

            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var style = ImGui.GetStyle();
            var frameHeight = ImGui.GetFrameHeight();
            var headerHeight = frameHeight + TitleBarPaddingY * 2f * scale;
            var windowPos = ImGui.GetWindowPos();
            var windowWidth = ImGui.GetWindowSize().X;
            var min = windowPos;
            var max = new Vector2(windowPos.X + windowWidth, windowPos.Y + headerHeight);
            var accent = accentColor ?? Theme.Current.AccentCyan;

            ImGui.PushClipRect(min, windowPos + ImGui.GetWindowSize(), false);
            DrawWindowSurface(drawList, windowPos, windowPos + ImGui.GetWindowSize(), style.WindowRounding);
            DrawTitleBarBackground(drawList, min, max, windowPos.Y + ImGui.GetWindowSize().Y,
                style.WindowRounding, accent);
            // No item covers the bar — ImGui's native window move handles
            // dragging at Begin time (moving the window mid-frame via
            // SetWindowPos leaves that frame's content behind).
            var leftEndX = DrawTitleBarLeft(min, headerHeight, title, leadingIcon, accent, leadingTexture);
            if (middleContent != null)
            {
                var midMinX = leftEndX + 10f * scale;
                var midMaxX = max.X - TitleBarPaddingX * scale
                    - MeasureTitleBarRightWidth(frameHeight, headerHeight, rightButtons, trailingText, scale)
                    - 8f * scale;
                if (midMaxX > midMinX)
                {
                    middleContent(new Vector2(midMinX, min.Y), new Vector2(midMaxX, max.Y));
                }
            }
            DrawTitleBarRight(min, max, frameHeight, rightButtons, trailingText, scale);
            ImGui.PopClipRect();

            ImGui.SetCursorScreenPos(new Vector2(
                windowPos.X + style.WindowPadding.X,
                max.Y + TitleBarContentGap * scale));
        }

        /// <summary>
        /// Draws the window surface: an opaque gradient body plus a border ring
        /// built from two rounded fills so the frame keeps uniform thickness at
        /// the corners.
        /// </summary>
        internal static void DrawWindowSurface(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float windowRounding)
        {
            var style = ImGui.GetStyle();
            var borderSize = MathF.Max(style.WindowBorderSize, 0f);
            if (borderSize > 0f)
            {
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.Border), windowRounding);
            }

            var inset = new Vector2(borderSize, borderSize);
            var innerRounding = MathF.Max(0f, windowRounding - borderSize);

            WindowSurfaceGradient(out var topColor, out var bottomColor);
            VerticalFade(drawList, min + inset, max - inset, topColor, bottomColor,
                innerRounding, innerRounding, innerRounding, innerRounding);
        }

        internal static void WindowSurfaceGradient(out Vector4 topColor, out Vector4 bottomColor)
        {
            var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            // Flat: any top/bottom difference quantizes into visible bands on
            // large dark surfaces — the depth cue comes from rails and cards
            // instead, and the nav-content backing samples the same tone.
            topColor = bg;
            bottomColor = bg;
        }

        /// <summary>Evaluates the window surface gradient color at a screen-space Y.</summary>
        internal static Vector4 WindowSurfaceColorAt(Vector2 parentMin, Vector2 parentMax, float y)
        {
            var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
            var top = parentMin.Y + borderSize;
            var bottom = parentMax.Y - borderSize;
            var t = Math.Clamp((y - top) / MathF.Max(bottom - top, 1f), 0f, 1f);
            WindowSurfaceGradient(out var topColor, out var bottomColor);
            return Vector4.Lerp(topColor, bottomColor, t);
        }

        private static void DrawTitleBarBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float windowBottom, float windowRounding, Vector4 accent)
        {
            var background = ColorUtils.ToU32(
                ColorUtils.WithAlpha(Theme.Current.PrimaryDark, TitleBarBackgroundAlpha));
            var corners = max.Y >= windowBottom - windowRounding
                ? ImDrawFlags.RoundCornersAll
                : ImDrawFlags.RoundCornersTop;
            drawList.AddRectFilled(min, max, background, windowRounding, corners);

            var accentLine = ColorUtils.ToU32(ColorUtils.WithAlpha(accent, TitleBarAccentLineAlpha));
            var cornerInset = Math.Clamp(max.Y + windowRounding - windowBottom, 0f, windowRounding);
            var lineMin = new Vector2(min.X + cornerInset, max.Y - TitleBarAccentLineHeight);
            var lineMax = new Vector2(max.X - cornerInset, max.Y);
            drawList.AddRectFilled(lineMin, lineMax, accentLine);
        }

        /// <summary>
        /// Width the right-side buttons plus trailing text will occupy —
        /// mirrors <see cref="DrawTitleBarRight"/>'s layout so middle content
        /// can size itself without overlapping the button zone.
        /// </summary>
        private static float MeasureTitleBarRightWidth(float buttonSize, float barHeight,
            IReadOnlyList<TitleBarButton>? rightButtons, string? trailingText, float scale)
        {
            var width = 0f;
            if (rightButtons != null)
            {
                var i = rightButtons.Count - 1;
                while (i >= 0)
                {
                    if (rightButtons[i].BrandIcon.HasValue)
                    {
                        var runStart = i;
                        while (runStart - 1 >= 0 && rightButtons[runStart - 1].BrandIcon.HasValue)
                        {
                            runStart--;
                        }
                        width += (barHeight - TitleBarAccentLineHeight) * 1.15f * (i - runStart + 1);
                        i = runStart - 1;
                    }
                    else
                    {
                        width += buttonSize;
                        i--;
                    }
                    width += TitleBarButtonSpacing * scale;
                }
            }
            if (!string.IsNullOrEmpty(trailingText))
            {
                width += ImGui.CalcTextSize(trailingText).X;
            }
            return width;
        }

        private static float DrawTitleBarLeft(Vector2 min, float headerHeight, string title,
            FontAwesomeIcon? leadingIcon, Vector4 accent, IDalamudTextureWrap? leadingTexture)
        {
            var scale = SpheneUI.Scale;
            var textHeight = ImGui.GetTextLineHeight();
            var textY = min.Y + (headerHeight - textHeight) * 0.5f;
            ImGui.SetCursorScreenPos(new Vector2(min.X + TitleBarPaddingX * scale, textY));

            if (leadingTexture != null)
            {
                var iconSize = headerHeight * 0.62f;
                var iconPos = ImGui.GetCursorScreenPos()
                    - new Vector2(0f, (iconSize - textHeight) * 0.5f);
                ImGui.Dummy(new Vector2(iconSize, textHeight));
                Media.ImageInRect(ImGui.GetWindowDrawList(), leadingTexture, iconPos,
                    iconPos + new Vector2(iconSize), ImageFit.Cover,
                    rounding: Metrics.SwatchRadius(iconSize));
                ImGui.SameLine(0, 8f * scale);
            }
            else if (leadingIcon.HasValue)
            {
                Icons.Icon(leadingIcon.Value, accent);
                ImGui.SameLine(0, 8f * scale);
            }
            Text.Colored(title, Theme.Current.TextPrimary);
            return ImGui.GetItemRectMax().X;
        }

        private static void DrawTitleBarRight(Vector2 min, Vector2 max, float buttonSize,
            IReadOnlyList<TitleBarButton>? rightButtons,
            string? trailingText, float scale)
        {
            var buttonY = min.Y + (max.Y - min.Y - buttonSize) * 0.5f;
            var x = max.X - TitleBarPaddingX * scale;
            var textHeight = ImGui.GetTextLineHeight();

            if (rightButtons != null)
            {
                var i = rightButtons.Count - 1;
                while (i >= 0)
                {
                    var button = rightButtons[i];
                    if (button.BrandIcon.HasValue)
                    {
                        // Consecutive brand buttons form one joined strip
                        var runStart = i;
                        while (runStart - 1 >= 0 && rightButtons[runStart - 1].BrandIcon.HasValue)
                        {
                            runStart--;
                        }
                        var runCount = i - runStart + 1;
                        var stripHeight = max.Y - min.Y - TitleBarAccentLineHeight;
                        var segWidth = stripHeight * 1.15f;
                        x -= segWidth * runCount;
                        DrawBrandButtonStrip(rightButtons, runStart, runCount,
                            new Vector2(x, min.Y), segWidth, stripHeight);
                        i = runStart - 1;
                        x -= TitleBarButtonSpacing * scale;
                        continue;
                    }
                    x -= buttonSize;
                    ImGui.SetCursorScreenPos(new Vector2(x, buttonY));
                    if (Icons.Button(button.Icon, null, null, null, buttonSize, button.StyleKey))
                    {
                        button.OnClick();
                    }
                    if (!string.IsNullOrEmpty(button.Tooltip) && ImGui.IsItemHovered())
                    {
                        SpheneUI.AttachTooltip(button.Tooltip);
                    }
                    DrawTitleBarButtonBadge(new Vector2(x, buttonY), buttonSize,
                        button.BadgeCount, scale);
                    x -= TitleBarButtonSpacing * scale;
                    i--;
                }
            }

            if (!string.IsNullOrEmpty(trailingText))
            {
                var textWidth = ImGui.CalcTextSize(trailingText).X;
                x -= textWidth;
                ImGui.SetCursorScreenPos(new Vector2(x, min.Y + (max.Y - min.Y - textHeight) * 0.5f));
                Text.Colored(trailingText, ImGuiColors.DalamudGrey3);
            }
        }

        /// <summary>
        /// Draws a small notification badge at the top-right corner of a title
        /// bar button — a filled circle with a counter (clamped to 9+).
        /// </summary>
        internal static void DrawTitleBarButtonBadge(Vector2 buttonPos, float buttonSize,
            int badgeCount, float scale)
        {
            if (badgeCount <= 0)
            {
                return;
            }

            var label = badgeCount > 9 ? "9+" : badgeCount.ToString();
            var drawList = ImGui.GetWindowDrawList();
            var badgeColor = ColorUtils.ToU32(Palette.NetworkDisconnected);
            var textColor = ColorUtils.ToU32(Palette.CrystalWhite);

            var textSize = ImGui.CalcTextSize(label);
            var radius = MathF.Max(textSize.X * 0.5f + 3f * scale, 5.5f * scale);
            var center = new Vector2(
                buttonPos.X + buttonSize - radius * 0.35f,
                buttonPos.Y + radius * 0.35f);

            drawList.AddCircleFilled(center, radius, badgeColor);
            drawList.AddCircle(center, radius,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Palette.CrystalWhite, 0.35f)),
                0, MathF.Max(1f, scale));
            drawList.AddText(center - textSize * 0.5f, textColor, label);
        }

        /// <summary>
        /// Draws a run of brand-icon title bar buttons as one joined strip
        /// spanning the full bar height minus the accent line, using the shared
        /// <see cref="SpheneBrandIcons.BrandStrip"/> renderer.
        /// </summary>
        private static void DrawBrandButtonStrip(
            IReadOnlyList<TitleBarButton> buttons, int start, int count,
            Vector2 pos, float segWidth, float height)
        {
            var segments = new SpheneBrandIcons.StripSegment[count];
            for (var k = 0; k < count; k++)
            {
                var button = buttons[start + k];
                segments[k] = new SpheneBrandIcons.StripSegment(
                    button.BrandIcon!.Value, Tooltip: button.Tooltip, OnClick: button.OnClick);
            }
            ImGui.SetCursorScreenPos(pos);
            SpheneBrandIcons.BrandStrip($"titlebar{start}", segments, height, segWidth * count);
        }

        /// <summary>
        /// Breadcrumb pill ("Parent › Current") for title bars and header
        /// rows — a dark scrim + thin border keeps it legible over imagery.
        /// The parent segment is clickable when <paramref name="onParentClick"/>
        /// is set; overlong text ellipsizes (parent drops first, then the label).
        /// </summary>
        internal static void CrumbPill(Vector2 pillMin, float maxWidth,
            string? parentLabel, string currentLabel, Action? onParentClick = null)
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var pillH = 26f * scale;
            var padX = 12f * scale;
            var sepGap = 7f * scale;
            const string sep = "›";

            var sepW = ImGui.CalcTextSize(sep).X + sepGap * 2f;
            var maxTextW = MathF.Max(0f, maxWidth - padX * 2f);
            var parentW = parentLabel != null ? ImGui.CalcTextSize(parentLabel).X + sepW : 0f;
            var currentText = Text.Ellipsize(currentLabel, MathF.Max(24f * scale, maxTextW - parentW));
            if (ImGui.CalcTextSize(currentText).X + parentW > maxTextW)
            {
                // still too wide — drop the parent before shrinking further
                parentLabel = null;
                parentW = 0f;
                currentText = Text.Ellipsize(currentLabel, maxTextW);
            }

            var pillW = padX * 2f + parentW + ImGui.CalcTextSize(currentText).X;
            var pillMax = pillMin + new Vector2(pillW, pillH);
            drawList.AddRectFilled(pillMin, pillMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(
                    new Vector4(0.02f, 0.02f, 0.04f, 1f), 0.55f)), pillH * 0.5f);
            drawList.AddRect(pillMin, pillMax,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.10f)),
                pillH * 0.5f);

            var tx = pillMin.X + padX;
            var ty = pillMin.Y + (pillH - ImGui.GetTextLineHeight()) * 0.5f;
            if (parentLabel != null)
            {
                var pw = ImGui.CalcTextSize(parentLabel).X;
                var parentClicked = false;
                var parentHovered = false;
                if (onParentClick != null)
                {
                    ImGui.SetCursorScreenPos(new Vector2(tx, pillMin.Y));
                    parentClicked = ImGui.InvisibleButton("##crumbparent", new Vector2(pw, pillH));
                    parentHovered = ImGui.IsItemHovered();
                    if (parentHovered)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    }
                }
                drawList.AddText(new Vector2(tx, ty),
                    ColorUtils.ToU32(parentHovered ? Colors.TextPrimary : Colors.TextSecondary),
                    parentLabel);
                if (parentClicked)
                {
                    onParentClick!();
                }
                tx += pw;
                drawList.AddText(new Vector2(tx + sepGap, ty),
                    ColorUtils.ToU32(Colors.TextMuted), sep);
                tx += sepW;
            }
            drawList.AddText(new Vector2(tx, ty),
                ColorUtils.ToU32(Colors.TextPrimary), currentText);
        }

        /// <summary>
        /// Prepends Dalamud's blur-behind effect to the current window draw
        /// list so the scene behind the window shows through frosted. Must be
        /// called inside the window (after ImGui.Begin). Only takes effect on
        /// the main viewport; <paramref name="strengthFactor"/> is the
        /// user-facing [0,1] blur amount.
        /// </summary>
        public static void WindowBlur(float strengthFactor, Vector4? tintColor = null,
            float noiseOpacity = WindowBlurNoiseOpacity)
        {
            if (strengthFactor <= 0f)
            {
                return;
            }
            if (ImGui.GetWindowViewport().ID != ImGui.GetMainViewport().ID)
            {
                return;
            }

            var pos = ImGui.GetWindowPos();
            ImGuiHelpers.PrependBlurBehind(
                ImGui.GetWindowDrawList(),
                pos,
                pos + ImGui.GetWindowSize(),
                Math.Clamp(strengthFactor, 0f, 1f) * WindowBlurMaxStrength,
                ImGui.GetStyle().WindowRounding,
                tintColor: tintColor ?? WindowBlurTint,
                luminosityColor: Vector4.Zero,
                noiseOpacity: noiseOpacity);
        }

        /// <summary>
        /// Applies the general window blur theme setting for the current window.
        /// </summary>
        public static void ThemedWindowBlur()
        {
            var theme = Theme.Current;
            WindowBlur(theme.EnableWindowBlur ? theme.WindowBlurStrength : 0f);
        }

        // ----- Shared draw-list chrome helpers (scroll fades, frames, backdrops) -----

        internal static float SmoothStep(float t) => t * t * (3f - 2f * t);

        /// <summary>
        /// Opaque lip color for a scroll fade. Stays fully solid until the last
        /// third of the shrink range, then dissolves so the band disappears
        /// without a hard pop at the scroll boundary.
        /// </summary>
        internal static Vector4 FadeLip(Vector4 baseColor, float strength)
        {
            var alpha = ScrollFadeMaxAlpha * Math.Clamp(strength / 0.35f, 0f, 1f);
            return new Vector4(baseColor.X, baseColor.Y, baseColor.Z, alpha);
        }

        /// <summary>
        /// Fills a vertical gradient band. Corners with a radius are drawn as
        /// horizontal strips whose per-side inset follows the circle so the
        /// fade hugs the rounding instead of stopping short of it or painting
        /// past it.
        /// </summary>
        internal static void VerticalFade(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            Vector4 topColor, Vector4 bottomColor,
            float topLeftRadius, float topRightRadius, float bottomLeftRadius, float bottomRightRadius)
        {
            var height = max.Y - min.Y;
            if (height <= 0f || max.X - min.X <= 0f)
            {
                return;
            }

            var topZone = MathF.Min(MathF.Max(topLeftRadius, topRightRadius), height * 0.5f);
            var bottomZone = MathF.Min(MathF.Max(bottomLeftRadius, bottomRightRadius), height - topZone);
            var midMaxY = max.Y - bottomZone;
            var topSegments = Math.Clamp((int)MathF.Ceiling(topZone), 4, 48);
            var bottomSegments = Math.Clamp((int)MathF.Ceiling(bottomZone), 4, 48);

            for (var i = 0; i < topSegments && topZone > 0f; i++)
            {
                var y0 = min.Y + topZone * i / topSegments;
                var y1 = min.Y + topZone * (i + 1) / topSegments;
                var d = (y0 + y1) * 0.5f - min.Y;
                FadeStrip(drawList, min, max, y0, y1,
                    CircleInsetX(topLeftRadius, d), CircleInsetX(topRightRadius, d), topColor, bottomColor);
            }
            if (midMaxY > min.Y + topZone)
            {
                FadeStrip(drawList, min, max, min.Y + topZone, midMaxY, 0f, 0f, topColor, bottomColor);
            }
            for (var i = 0; i < bottomSegments && bottomZone > 0f; i++)
            {
                var y0 = midMaxY + bottomZone * i / bottomSegments;
                var y1 = midMaxY + bottomZone * (i + 1) / bottomSegments;
                var d = max.Y - (y0 + y1) * 0.5f;
                FadeStrip(drawList, min, max, y0, y1,
                    CircleInsetX(bottomLeftRadius, d), CircleInsetX(bottomRightRadius, d), topColor, bottomColor);
            }
        }

        internal static void FadeStrip(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float y0, float y1, float insetLeft, float insetRight, Vector4 topColor, Vector4 bottomColor)
        {
            var height = max.Y - min.Y;
            var c0 = ColorUtils.ToU32(Vector4.Lerp(topColor, bottomColor, (y0 - min.Y) / height));
            var c1 = ColorUtils.ToU32(Vector4.Lerp(topColor, bottomColor, (y1 - min.Y) / height));
            drawList.AddRectFilledMultiColor(new Vector2(min.X + insetLeft, y0),
                new Vector2(max.X - insetRight, y1), c0, c0, c1, c1);
        }

        internal static float CircleInsetX(float radius, float distanceFromEdge)
        {
            if (radius <= 0f)
            {
                return 0f;
            }
            var d = Math.Clamp(distanceFromEdge, 0f, radius);
            return radius - MathF.Sqrt(2f * radius * d - d * d);
        }

        /// <summary>
        /// Paints the corner squares outside the child's rounding with the
        /// window surface color so content can never visually spill past the
        /// rounded edge. The border must be redrawn afterwards since the mask
        /// covers its arcs.
        /// </summary>
        internal static void CornerSpillMasks(Vector2 min, Vector2 max,
            Vector2 parentMin, Vector2 parentMax)
        {
            var drawList = ImGui.GetWindowDrawList();
            var style = ImGui.GetStyle();
            var rounding = MathF.Min(style.ChildRounding,
                MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);
            if (rounding <= 0.5f)
            {
                return;
            }

            drawList.PushClipRect(min, max, false);
            CornerCrescent(drawList, min, 1f, 1f, rounding, parentMin, parentMax);
            CornerCrescent(drawList, new Vector2(max.X, min.Y), -1f, 1f, rounding, parentMin, parentMax);
            CornerCrescent(drawList, max, -1f, -1f, rounding, parentMin, parentMax);
            CornerCrescent(drawList, new Vector2(min.X, max.Y), 1f, -1f, rounding, parentMin, parentMax);
            drawList.PopClipRect();
        }

        /// <summary>
        /// Fills the area inside a corner square but outside the corner circle
        /// using thin horizontal strips so spilled content is covered without
        /// touching the rounded frame. Each strip is tinted with the surface
        /// gradient at its Y.
        /// </summary>
        internal static void CornerCrescent(ImDrawListPtr drawList, Vector2 corner,
            float dirX, float dirY, float radius, Vector2 parentMin, Vector2 parentMax)
        {
            var rows = Math.Clamp((int)MathF.Ceiling(radius), 4, 48);
            for (var i = 0; i < rows; i++)
            {
                var d0 = radius * i / rows;
                var d1 = radius * (i + 1) / rows;
                var inset = CircleInsetX(radius, (d0 + d1) * 0.5f);
                var y0 = corner.Y + dirY * d0;
                var y1 = corner.Y + dirY * d1;
                var x0 = corner.X;
                var x1 = corner.X + dirX * inset;
                var color = ColorUtils.ToU32(WindowSurfaceColorAt(parentMin, parentMax, (y0 + y1) * 0.5f));
                drawList.AddRectFilled(
                    new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)),
                    new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)), color);
            }
        }

        /// <summary>
        /// Draws a child frame as filled edge strips and annulus corner sectors
        /// so every part — including the corner arcs — keeps exactly borderSize
        /// thickness. With <paramref name="openTop"/> the straight top segment
        /// is omitted while the top corner arcs are kept, making the frame read
        /// as open at the top.
        /// </summary>
        internal static void RoundedFrame(Vector2 min, Vector2 max, float rounding,
            float borderSize, bool openTop)
        {
            var drawList = ImGui.GetWindowDrawList();
            var r = MathF.Min(rounding, MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);
            var b = MathF.Max(borderSize, 1f);
            var color = ImGui.GetColorU32(ImGuiCol.Border);
            const float halfPi = MathF.PI * 0.5f;
            const int segments = 10;

            drawList.PushClipRect(min - new Vector2(2f, 2f), max + new Vector2(2f, 2f), false);
            drawList.AddRectFilled(new Vector2(min.X, min.Y + r), new Vector2(min.X + b, max.Y - r), color);
            drawList.AddRectFilled(new Vector2(max.X - b, min.Y + r), new Vector2(max.X, max.Y - r), color);
            drawList.AddRectFilled(new Vector2(min.X + r, max.Y - b), new Vector2(max.X - r, max.Y), color);
            if (!openTop)
            {
                drawList.AddRectFilled(new Vector2(min.X + r, min.Y), new Vector2(max.X - r, min.Y + b), color);
            }

            FrameCorner(drawList, new Vector2(min.X + r, min.Y + r), r, b, MathF.PI, MathF.PI + halfPi, color, segments);
            FrameCorner(drawList, new Vector2(max.X - r, min.Y + r), r, b, -halfPi, 0f, color, segments);
            FrameCorner(drawList, new Vector2(max.X - r, max.Y - r), r, b, 0f, halfPi, color, segments);
            FrameCorner(drawList, new Vector2(min.X + r, max.Y - r), r, b, halfPi, MathF.PI, color, segments);
            drawList.PopClipRect();
        }

        /// <summary>Draws one rounded frame corner as a chain of filled annulus quads.</summary>
        private static void FrameCorner(ImDrawListPtr drawList, Vector2 center, float radius,
            float borderSize, float a0, float a1, uint color, int segments)
        {
            var inner = MathF.Max(radius - borderSize, 0f);
            for (var i = 0; i < segments; i++)
            {
                var t0 = a0 + (a1 - a0) * i / segments;
                var t1 = a0 + (a1 - a0) * (i + 1) / segments;
                drawList.AddQuadFilled(
                    center + new Vector2(MathF.Cos(t0), MathF.Sin(t0)) * radius,
                    center + new Vector2(MathF.Cos(t1), MathF.Sin(t1)) * radius,
                    center + new Vector2(MathF.Cos(t1), MathF.Sin(t1)) * inner,
                    center + new Vector2(MathF.Cos(t0), MathF.Sin(t0)) * inner,
                    color);
            }
        }

        /// <summary>
        /// Re-strokes the parent window border when a scroll area reaches a
        /// window edge. Fades and overlays draw after ImGui's own border pass,
        /// so a fresh stroke on top keeps the border visually dominant at every
        /// edge.
        /// </summary>
        internal static void RedrawWindowBorder(Vector2 min, Vector2 max,
            Vector2 parentMin, Vector2 parentMax, float tolerance)
        {
            var style = ImGui.GetStyle();
            var borderSize = MathF.Max(style.WindowBorderSize, 0f);
            if (borderSize <= 0f)
            {
                return;
            }
            var reach = borderSize + tolerance;
            var touchesEdge = min.X <= parentMin.X + reach
                || min.Y <= parentMin.Y + reach
                || parentMax.X - max.X <= reach
                || parentMax.Y - max.Y <= reach;
            if (!touchesEdge)
            {
                return;
            }
            // Window draw list keeps this in the window's z-order so overlapping
            // windows cover it; the explicit clip reaches past the child bounds.
            ImGui.PushClipRect(parentMin, parentMax, false);
            ImGui.GetWindowDrawList().AddRect(parentMin, parentMax,
                ImGui.GetColorU32(ImGuiCol.Border), style.WindowRounding,
                ImDrawFlags.None, borderSize);
            ImGui.PopClipRect();
        }

        internal static void ScrollFades(Vector2 min, Vector2 max, float scrollY,
            float scrollMaxY, bool border, float topInset, Vector2 parentMin, Vector2 parentMax,
            Vector4? fadeBaseColor, float edgePaddingX = -1f, Vector2? fadeSpanX = null,
            float? fadeMinY = null)
        {
            var scale = SpheneUI.Scale;
            // A child whose scrollable range is shorter than a line of text has
            // no meaningful "more content" to hint at; the fade lip would only
            // read as a stray shadow at the edge.
            if (scrollMaxY <= ImGui.GetTextLineHeightWithSpacing())
            {
                return;
            }
            // Fade strength: 1 while far from the end, shrinks to 0 as the edge
            // approaches so the band narrows and its opaque lip dissolves softly.
            var fadeRange = ScrollFadeHeight * scale;
            var upStrength = Math.Clamp(scrollY / fadeRange, 0f, 1f);
            var downStrength = Math.Clamp((scrollMaxY - scrollY) / fadeRange, 0f, 1f);
            if (upStrength <= 0f && downStrength <= 0f)
            {
                return;
            }

            var drawList = ImGui.GetWindowDrawList();
            var style = ImGui.GetStyle();
            var fadeHeight = MathF.Min(ScrollFadeHeight * scale, (max.Y - min.Y) * 0.3f);
            var baseColor = fadeBaseColor ?? style.Colors[(int)ImGuiCol.WindowBg];
            var transparentColor = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 0f);
            var chevronSize = ScrollHintChevronSize * scale;

            var edgeInset = border ? style.ChildBorderSize + 1f * scale : 0f;
            var inner = min + new Vector2(edgeInset, edgeInset + topInset);
            var innerMax = max - new Vector2(edgeInset, edgeInset);

            var outerBorder = MathF.Max(style.WindowBorderSize, 0f);
            inner.X = MathF.Max(inner.X, parentMin.X + outerBorder);
            inner.Y = MathF.Max(inner.Y, parentMin.Y + outerBorder);
            innerMax.X = MathF.Min(innerMax.X, parentMax.X - outerBorder);
            innerMax.Y = MathF.Min(innerMax.Y, parentMax.Y - outerBorder);

            // An explicit fade span overrides the child bounds (e.g. a rail that
            // reaches the window edge while its child is inset by padding).
            if (fadeSpanX.HasValue)
            {
                inner.X = Math.Clamp(fadeSpanX.Value.X, parentMin.X, innerMax.X);
                innerMax.X = Math.Clamp(fadeSpanX.Value.Y, inner.X, parentMax.X);
            }
            // A non-negative edge padding anchors the fade to the window's inner
            // edge so it can span wider or narrower than the child itself.
            else if (edgePaddingX >= 0f)
            {
                inner.X = parentMin.X + outerBorder + edgePaddingX;
                innerMax.X = parentMax.X - outerBorder - edgePaddingX;
                if (innerMax.X <= inner.X)
                {
                    return;
                }
            }

            // An explicit top edge lets the fade reach above the child bounds,
            // e.g. docking under a title bar accent line.
            if (fadeMinY.HasValue)
            {
                inner.Y = Math.Clamp(fadeMinY.Value, parentMin.Y, innerMax.Y);
            }

            if (innerMax.X <= inner.X || innerMax.Y <= inner.Y)
            {
                return;
            }
            var centerX = inner.X + (innerMax.X - inner.X) * 0.5f;
            var childRadius = border ? MathF.Max(0f, style.ChildRounding - edgeInset) : 0f;
            var windowRadius = MathF.Max(0f, style.WindowRounding - outerBorder);

            // A fade reaching the window's inner edge must hug the window's
            // corner arcs so it never paints over the border or outside the
            // silhouette.
            var edgeTol = 1.5f * scale;
            var touchTop = inner.Y <= parentMin.Y + outerBorder + edgeTol;
            var touchBottom = innerMax.Y >= parentMax.Y - outerBorder - edgeTol;
            var touchLeft = inner.X <= parentMin.X + outerBorder + edgeTol;
            var touchRight = innerMax.X >= parentMax.X - outerBorder - edgeTol;

            var childTopRadius = border && topInset <= 0f ? childRadius : 0f;
            var childBottomRadius = border ? childRadius : 0f;
            var topLeftRadius = MathF.Max(childTopRadius, touchTop && touchLeft ? windowRadius : 0f);
            var topRightRadius = MathF.Max(childTopRadius, touchTop && touchRight ? windowRadius : 0f);
            var bottomLeftRadius = MathF.Max(childBottomRadius, touchBottom && touchLeft ? windowRadius : 0f);
            var bottomRightRadius = MathF.Max(childBottomRadius, touchBottom && touchRight ? windowRadius : 0f);

            drawList.PushClipRect(inner, innerMax, border);

            if (upStrength > 0f)
            {
                var height = fadeHeight * SmoothStep(upStrength);
                var opaque = FadeLip(baseColor, upStrength);
                var hint = ColorUtils.ToU32(new Vector4(1f, 1f, 1f, ScrollHintChevronAlpha * upStrength));
                VerticalFade(drawList, inner, new Vector2(innerMax.X, inner.Y + height),
                    opaque, transparentColor, topLeftRadius, topRightRadius, 0f, 0f);
                ScrollHintPill(drawList, centerX, inner.Y, chevronSize, hint, pointsUp: true);
            }

            if (downStrength > 0f)
            {
                var height = fadeHeight * SmoothStep(downStrength);
                var opaque = FadeLip(baseColor, downStrength);
                var hint = ColorUtils.ToU32(new Vector4(1f, 1f, 1f, ScrollHintChevronAlpha * downStrength));
                VerticalFade(drawList, new Vector2(inner.X, innerMax.Y - height), innerMax,
                    transparentColor, opaque, 0f, 0f, bottomLeftRadius, bottomRightRadius);
                ScrollHintPill(drawList, centerX, innerMax.Y, chevronSize, hint, pointsUp: false);
            }

            drawList.PopClipRect();
        }

        private const float GripAnimSpeed = 8f;

        /// <summary>
        /// Stamps the window's bottom-right corner with a background triangle
        /// on the window draw list so the native resize grip is fully hidden,
        /// then draws a themed grip on top with hover/active feedback. The real
        /// ImGui resize hit test stays functional underneath.
        /// </summary>
        internal static void GripCorner(Vector2 corner, bool gripHeld, float gripAnim,
            out bool newHeld, out float newAnim)
        {
            var drawList = ImGui.GetWindowDrawList();
            var scale = SpheneUI.Scale;
            var style = ImGui.GetStyle();
            var gripSize = ImGui.GetFontSize() * ScrollFadeResizeGripScale;
            var stampSize = gripSize + style.WindowBorderSize + 2f * scale;

            var mouse = ImGui.GetIO().MousePos;
            var inTriangle = mouse.X >= corner.X - stampSize && mouse.X <= corner.X
                && mouse.Y >= corner.Y - stampSize && mouse.Y <= corner.Y
                && (corner.X - mouse.X) + (corner.Y - mouse.Y) <= stampSize;

            newHeld = gripHeld;
            if (!newHeld && inTriangle && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                newHeld = true;
            }
            if (newHeld && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                newHeld = false;
            }

            var target = newHeld ? 2f : inTriangle ? 1f : 0f;
            var step = ImGui.GetIO().DeltaTime * GripAnimSpeed;
            newAnim = gripAnim < target
                ? MathF.Min(target, gripAnim + step)
                : MathF.Max(target, gripAnim - step);
            var t = newAnim;

            var normal = style.Colors[(int)ImGuiCol.ResizeGrip];
            // Hover/active are derived instead of read: scopes may hide the
            // native variants (alpha 0) to suppress ImGui's border-resize ticks.
            var hover = GripHighlight(normal, 0.18f);
            var active = GripHighlight(normal, 0.34f);
            var background = style.Colors[(int)ImGuiCol.WindowBg];

            drawList.PushClipRect(corner - new Vector2(stampSize, stampSize) - new Vector2(1f, 1f),
                corner + new Vector2(scale, scale), false);
            var zoneTint = 0.10f + MathF.Min(t, 1f) * 0.45f + MathF.Max(0f, t - 1f) * 0.15f;
            var zoneColor = Vector4.Lerp(background, active, zoneTint);
            zoneColor.W = 1f;
            var zoneU32 = ColorUtils.ToU32(zoneColor);
            var rounding = style.WindowRounding;
            if (rounding <= 0.01f)
            {
                drawList.AddTriangleFilled(
                    new Vector2(corner.X - stampSize, corner.Y),
                    corner,
                    new Vector2(corner.X, corner.Y - stampSize), zoneU32);
            }
            else
            {
                // The stamp follows the window's rounded silhouette: right
                // edge down to the arc, the quarter-circle corner, the bottom
                // edge — the hypotenuse closes it. Same triangle minus the
                // notch the rounding carved out.
                var arcCenter = corner - new Vector2(rounding, rounding);
                drawList.PathLineTo(new Vector2(corner.X, corner.Y - stampSize));
                drawList.PathLineTo(new Vector2(corner.X, arcCenter.Y));
                drawList.PathArcTo(arcCenter, rounding, 0f, MathF.PI * 0.5f);
                drawList.PathLineTo(new Vector2(corner.X - stampSize, corner.Y));
                drawList.PathFillConvex(zoneU32);
            }

            var gripColor = t <= 1f
                ? Vector4.Lerp(normal, hover, t)
                : Vector4.Lerp(hover, active, t - 1f);
            var color = ColorUtils.ToU32(gripColor);

            var borderSize = style.WindowBorderSize;
            var bandSize = MathF.Max(ImGui.GetFontSize() * 1.10f, 1f + borderSize * 1.5f);
            var cornerSize = MathF.Truncate(bandSize * 0.75f);
            var b = 1f * scale + borderSize;
            drawList.AddQuadFilled(
                corner + new Vector2(-b, -bandSize),
                corner + new Vector2(-bandSize, -b),
                corner + new Vector2(-cornerSize, -b),
                corner + new Vector2(-b, -cornerSize), color);

            drawList.PopClipRect();
        }

        private static Vector4 GripHighlight(Vector4 baseColor, float lift)
        {
            return new Vector4(
                MathF.Min(baseColor.X + lift, 1f),
                MathF.Min(baseColor.Y + lift, 1f),
                MathF.Min(baseColor.Z + lift, 1f),
                baseColor.W);
        }

        private static void ScrollHintPill(ImDrawListPtr drawList, float centerX, float edgeY,
            float chevronSize, uint hintColor, bool pointsUp)
        {
            var scale = SpheneUI.Scale;
            var halfWidth = chevronSize + ScrollHintPillPaddingX * scale;
            var halfHeight = chevronSize * 0.6f + ScrollHintPillPaddingY * scale;
            var inset = halfHeight + ScrollHintEdgeInset * scale;
            var bounce = MathF.Sin((float)ImGui.GetTime() * ScrollHintBounceSpeed)
                * ScrollHintBounceAmplitude * scale;
            var centerY = pointsUp ? edgeY + inset - bounce : edgeY - inset + bounce;
            var pillMin = new Vector2(centerX - halfWidth, centerY - halfHeight);
            var pillMax = new Vector2(centerX + halfWidth, centerY + halfHeight);

            var pillFill = ColorUtils.ToU32(new Vector4(0f, 0f, 0f, ScrollHintPillAlpha));
            var pillBorder = ColorUtils.ToU32(new Vector4(1f, 1f, 1f, ScrollHintPillBorderAlpha));
            drawList.AddRectFilled(pillMin, pillMax, pillFill, halfHeight);
            drawList.AddRect(pillMin, pillMax, pillBorder, halfHeight);

            var tipY = pointsUp ? centerY - chevronSize * 0.6f : centerY + chevronSize * 0.6f;
            var baseY = pointsUp ? centerY + chevronSize * 0.6f : centerY - chevronSize * 0.6f;
            drawList.AddTriangleFilled(
                new Vector2(centerX - chevronSize, baseY),
                new Vector2(centerX + chevronSize, baseY),
                new Vector2(centerX, tipY), hintColor);
        }

        /// <summary>
        /// Fills a rounded horizontal gradient — used by progress bars and
        /// transmission bars.
        /// </summary>
        internal static void RoundedHorizontalGradient(ImDrawListPtr drawList, Vector2 a, Vector2 b,
            float rounding, Vector4 startColor, Vector4 endColor)
        {
            var width = b.X - a.X;
            if (width <= 0)
            {
                return;
            }
            var slices = Math.Clamp((int)(width / 8.0f), 8, 64);
            var step = width / slices;
            for (var i = 0; i < slices; i++)
            {
                var x0 = a.X + step * i;
                var x1 = i == slices - 1 ? b.X : a.X + step * (i + 1);
                var t = (float)i / (slices - 1);
                var c = Vector4.Lerp(startColor, endColor, t);
                var flags = ImDrawFlags.None;
                var r = 0.0f;
                if (i == 0)
                {
                    flags = ImDrawFlags.RoundCornersLeft;
                    r = rounding;
                }
                else if (i == slices - 1)
                {
                    flags = ImDrawFlags.RoundCornersRight;
                    r = rounding;
                }
                drawList.AddRectFilled(new Vector2(x0, a.Y), new Vector2(x1, b.Y),
                    ColorUtils.ToU32(c), r, flags);
            }
        }

        /// <summary>
        /// Section card backdrop: a breathing bloom that follows the
        /// bottom-right corner arc, a few star glints riding it, and
        /// slow-twinkling motes near the top.
        /// </summary>
        internal static void SectionCardBackdrop(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float rounding, Vector4 accent)
        {
            var width = max.X - min.X;
            var height = max.Y - min.Y;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var scale = SpheneUI.Scale;
            var time = (float)ImGui.GetTime();
            // Clip at the card bounds minus a pixel so the glow hugs the border
            // instead of leaving a dark gap in front of it.
            drawList.PushClipRect(min + new Vector2(1f, 1f), max - new Vector2(1f, 1f), true);

            CardBloom(drawList, max, rounding, accent, time);

            // A few slow-twinkling motes scattered near the top of the card.
            for (var k = 0; k < SectionCardMoteCount; k++)
            {
                var twinkle = MathF.Max(0f, MathF.Sin(time * SectionCardMoteSpeed + k * 2.1f));
                if (twinkle <= 0.01f)
                {
                    continue;
                }
                var x = min.X + width * (0.12f + 0.7f * ((k + 1f) / (SectionCardMoteCount + 1f)));
                var y = min.Y + height * (0.16f + 0.08f * MathF.Sin(k * 1.7f));
                var moteColor = new Vector4(accent.X, accent.Y, accent.Z,
                    SectionCardMoteAlpha * twinkle * twinkle);
                drawList.AddCircleFilled(new Vector2(x, y), SectionCardMoteRadius * scale,
                    ColorUtils.ToU32(moteColor));
            }

            drawList.PopClipRect();
        }

        /// <summary>
        /// Breathing corner bloom: one radial fan centred on the
        /// bottom-right arc centre whose rim is literally the card's
        /// silhouette — along the bottom edge, around the corner arc, up
        /// the right edge, then back around on the disc's own arc. Vertex
        /// alpha follows the radial profile per point, so the glow is the
        /// exact crop of a soft disc by the rounded card: round
        /// everywhere, no clipped straight edges.
        /// </summary>
        private static void CardBloom(ImDrawListPtr drawList, Vector2 max,
            float rounding, Vector4 accent, float time)
        {
            var scale = SpheneUI.Scale;
            var pulse = 0.65f + 0.35f * MathF.Sin(time * CardBloomSpeed);
            var center = max - new Vector2(rounding, rounding);
            var reach = CardBloomRingCount * CardBloomRingStep * scale;
            var right = max.X - 1f;
            var bottom = max.Y - 1f;
            var edgeDistX = right - center.X;
            var edgeDistY = bottom - center.Y;
            if (reach <= MathF.Max(edgeDistX, edgeDistY) + 4f)
            {
                return;
            }

            var chord = MathF.Sqrt(MathF.Max(1f, reach * reach - edgeDistY * edgeDistY));
            var arcR = MathF.Max(1f, rounding - 1.5f);
            Span<Vector2> rim = stackalloc Vector2[32];
            var n = 0;
            rim[n++] = new Vector2(center.X - chord, bottom);
            rim[n++] = new Vector2(center.X, bottom);
            const int arcSegs = 5;
            for (var k = 0; k <= arcSegs; k++)
            {
                var a = MathF.PI * 0.5f * (1f - k / (float)arcSegs);
                rim[n++] = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * arcR;
            }
            rim[n++] = new Vector2(right, center.Y);
            rim[n++] = new Vector2(right, center.Y - chord);
            var aRight = MathF.Atan2(-chord, edgeDistX);
            var aBottom = MathF.Atan2(edgeDistY, -chord) - MathF.PI * 2f;
            const int rimSegs = 11;
            for (var k = 0; k <= rimSegs; k++)
            {
                var a = aRight + (aBottom - aRight) * (k / (float)rimSegs);
                rim[n++] = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * reach;
            }

            var uvWhite = ImGui.GetFontTexUvWhitePixel();
            const float mid = 0.45f;
            drawList.PrimReserve(n * 9, 1 + n * 2);
            var baseVtx = (int)drawList.VtxCurrentIdx;
            drawList.PrimWriteVtx(center, uvWhite,
                ColorUtils.ToU32(new Vector4(accent.X, accent.Y, accent.Z,
                    CardBloomRingAlpha * pulse)));
            for (var k = 0; k < n; k++)
            {
                var rel = rim[k] - center;
                var d = rel.Length();
                drawList.PrimWriteVtx(center + rel * mid, uvWhite,
                    ColorUtils.ToU32(new Vector4(accent.X, accent.Y, accent.Z,
                        BloomAlphaAt(d * mid, scale, pulse))));
                drawList.PrimWriteVtx(rim[k], uvWhite,
                    ColorUtils.ToU32(new Vector4(accent.X, accent.Y, accent.Z,
                        BloomAlphaAt(d, scale, pulse))));
            }
            for (var k = 0; k < n; k++)
            {
                var m1 = (ushort)(baseVtx + 1 + k * 2);
                var m2 = (ushort)(baseVtx + 1 + ((k + 1) % n) * 2);
                var o1 = (ushort)(m1 + 1);
                var o2 = (ushort)(m2 + 1);
                drawList.PrimWriteIdx((ushort)baseVtx);
                drawList.PrimWriteIdx(m1);
                drawList.PrimWriteIdx(m2);
                drawList.PrimWriteIdx(m1);
                drawList.PrimWriteIdx(o1);
                drawList.PrimWriteIdx(m2);
                drawList.PrimWriteIdx(m2);
                drawList.PrimWriteIdx(o1);
                drawList.PrimWriteIdx(o2);
            }
        }

        private static float BloomAlphaAt(float screenDist, float scale, float pulse)
        {
            var ringIndex = screenDist / (CardBloomRingStep * scale);
            var falloff = Math.Clamp(1f - (ringIndex - 1f) / CardBloomRingCount, 0f, 1f);
            return CardBloomRingAlpha * falloff * falloff * pulse;
        }

        /// <summary>
        /// Accent card backdrop: a soft top glow plus the shared corner
        /// bloom that follows the bottom-right arc.
        /// </summary>
        internal static void AccentCardBackdrop(ImDrawListPtr drawList, Vector2 min, Vector2 max,
            float rounding, Vector4 accent)
        {
            var width = max.X - min.X;
            var height = max.Y - min.Y;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var scale = SpheneUI.Scale;
            var time = (float)ImGui.GetTime();
            var clipInset = new Vector2(rounding * 0.4f, rounding * 0.4f);
            drawList.PushClipRect(min + clipInset, max - clipInset, true);

            var glowHeight = MathF.Min(52f * scale, height);
            var glow = ColorUtils.ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.08f));
            drawList.AddRectFilledMultiColor(min, new Vector2(max.X, min.Y + glowHeight), glow, glow, 0, 0);

            CardBloom(drawList, max, rounding, accent, time);

            drawList.PopClipRect();
        }
    }

    /// <summary>
    /// The shared content-bounds stack. Card regions and scroll areas push the
    /// screen-space (left, right) edge content must stay inside; labeled
    /// controls dock to <see cref="CurrentRight"/>.
    /// </summary>
    internal static class ContentBounds
    {
        private static readonly Stack<Vector2> _stack = new();

        /// <summary>
        /// Sentinel bound entry meaning "no active bound". Pushed while a
        /// separate window (styled popup, modal) is drawn so helpers dock
        /// against that window's own content region instead of a stale bound
        /// rendered underneath it.
        /// </summary>
        private static readonly Vector2 NoBound = new(-1f, -1f);

        public static float CurrentRight => _stack.Count > 0 ? _stack.Peek().Y : -1f;
        public static float CurrentLeft => _stack.Count > 0 ? _stack.Peek().X : -1f;
        public static void Push(Vector2 bounds) => _stack.Push(bounds);
        public static void Pop()
        {
            if (_stack.Count > 0)
            {
                _stack.Pop();
            }
        }
        public static IDisposable PushNone()
        {
            _stack.Push(NoBound);
            return new NoneScope();
        }

        /// <summary>Pushes the "no bound" sentinel; caller pops it explicitly.</summary>
        public static void PushNoneSentinel() => _stack.Push(NoBound);

        private sealed class NoneScope : IDisposable
        {
            public void Dispose() => Pop();
        }
    }
}
