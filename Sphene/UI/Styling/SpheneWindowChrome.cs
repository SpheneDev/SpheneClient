using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.UI.Components;
using System.Numerics;

namespace Sphene.UI.Styling;

/// <summary>
/// Shared window chrome helpers: custom title bars for windows using
/// <see cref="ImGuiWindowFlags.NoTitleBar"/> and reusable accent card containers.
/// </summary>
public static class SpheneWindowChrome
{
    private const float TitleBarPaddingX = 12f;
    private const float TitleBarPaddingY = 7f;
    private const float TitleBarButtonSpacing = 6f;
    private const float TitleBarAccentLineHeight = 3f;
    private const float TitleBarAccentLineAlpha = 0.45f;
    private const float TitleBarBackgroundAlpha = 0.94f;
    private const float TitleBarContentGap = 4f;

    private const float AccentCardPadding = 14f;
    private const float AccentCardRounding = 8f;
    private const float AccentCardBorderPulseSpeed = 1.3f;
    private const float AccentCardShimmerPixelsPerSecond = 55f;
    private const float AccentCardBorderThickness = 1.5f;

    private const float ScrollFadeHeight = 22f;
    private const float ScrollFadeMaxAlpha = 0.88f;
    private const float ScrollFadeDeadZone = 4f;
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

    /// <summary>
    /// Standard horizontal inner spacing for scroll areas created with noPadding.
    /// Applied automatically as a left indent; edge-bleed elements must respect the
    /// same value on the right (see <see cref="ActiveContentInset"/>).
    /// </summary>
    public const float ScrollAreaContentInset = 10f;

    private static float ActiveContentInset =>
        ScrollAreaStack.Count > 0 ? ScrollAreaStack.Peek().ContentInset : 0f;

    public sealed record TitleBarButton(FontAwesomeIcon Icon, string Tooltip, Action OnClick, string? StyleKey = null);

    private sealed class FoldState
    {
        public bool Collapsed;
        public float Amount;
        public Vector2 OpenSize;
        public WindowSizeConstraints? SavedConstraints;
        public Vector2? SavedSize;
        public ImGuiCond SavedSizeCondition;
        public ImGuiWindowFlags SavedFlags;
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
    /// Call in <c>PreDraw</c> of a collapsible window before <c>base.PreDraw()</c>.
    /// Animates the window height between its open size and the title bar.
    /// </summary>
    public static void UpdateCollapse(Window window, string windowName, float collapsedHeightUnscaled)
    {
        var fold = GetFold(windowName);
        var target = fold.Collapsed ? 1f : 0f;
        if (!MathF.Equals(fold.Amount, target))
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
                window.Flags = fold.SavedFlags;
            }
            return;
        }

        if (fold.SavedConstraints == null)
        {
            fold.SavedConstraints = window.SizeConstraints;
            fold.SavedSize = window.Size;
            fold.SavedSizeCondition = window.SizeCondition;
            fold.SavedFlags = window.Flags;
        }
        window.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Vector2.One,
            MaximumSize = FoldMaximumSize,
        };

        if (fold.OpenSize.Y <= 0f)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var height = float.Lerp(fold.OpenSize.Y, collapsedHeightUnscaled * scale, t);
        var width = window.Size?.X * scale ?? fold.OpenSize.X;
        window.Size = new Vector2(MathF.Max(width, 1f) / scale, MathF.Max(height, 1f) / scale);
        window.SizeCondition = ImGuiCond.Always;
        window.Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    /// <summary>Fold amount 0..1 for the window (0 = fully open). Draw content with alpha fade while &lt; 1.</summary>
    public static float GetCollapseAmount(string windowName)
        => EaseCollapse(GetFold(windowName).Amount);

    /// <summary>True once the window is fully folded – skip drawing the content.</summary>
    public static bool IsCollapsed(string windowName)
        => GetCollapseAmount(windowName) >= 1f;

    private static void ToggleFold(string windowName)
    {
        var fold = GetFold(windowName);
        if (!fold.Collapsed && fold.Amount <= 0f)
        {
            fold.OpenSize = ImGui.GetWindowSize();
        }
        fold.Collapsed = !fold.Collapsed;
    }

    /// <summary>
    /// Draws a custom title bar at the current cursor position and moves the cursor
    /// below it. The bar area drags the window when clicked and held.
    /// Call as the first thing inside <c>DrawInternal</c> on windows that set
    /// <see cref="ImGuiWindowFlags.NoTitleBar"/>.
    /// </summary>
    public static void DrawTitleBar(string windowName, string title, UiSharedService uiShared,
        FontAwesomeIcon? leadingIcon = null, Vector4? accentColor = null,
        IReadOnlyList<TitleBarButton>? rightButtons = null, string? trailingText = null,
        bool collapsible = false)
    {
        if (collapsible)
        {
            var fold = GetFold(windowName);
            var toggle = new TitleBarButton(
                fold.Collapsed ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                fold.Collapsed ? "Expand" : "Collapse",
                () => ToggleFold(windowName));
            rightButtons = rightButtons == null ? [toggle] : [toggle, .. rightButtons];
        }

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var frameHeight = ImGui.GetFrameHeight();
        var headerHeight = frameHeight + TitleBarPaddingY * 2f * scale;
        var windowPos = ImGui.GetWindowPos();
        var windowWidth = ImGui.GetWindowSize().X;
        var min = windowPos;
        var max = new Vector2(windowPos.X + windowWidth, windowPos.Y + headerHeight);
        var accent = accentColor ?? SpheneColors.CrystalBlue;

        ImGui.PushClipRect(min, windowPos + ImGui.GetWindowSize(), false);
        DrawWindowSurface(drawList, windowPos, windowPos + ImGui.GetWindowSize(), style.WindowRounding);
        DrawTitleBarBackground(drawList, min, max, style.WindowRounding, accent);
        DrawTitleBarDragRegion(windowName, min, windowWidth, headerHeight);
        DrawTitleBarLeft(min, headerHeight, title, uiShared, leadingIcon, accent);
        DrawTitleBarRight(min, max, frameHeight, uiShared, rightButtons, trailingText, scale);
        ImGui.PopClipRect();

        ImGui.SetCursorScreenPos(new Vector2(
            windowPos.X + style.WindowPadding.X,
            max.Y + TitleBarContentGap * scale));
    }

    /// <summary>
    /// Draws the window surface: an opaque gradient body plus a border ring built
    /// from two rounded fills so the frame keeps uniform thickness at the corners.
    /// </summary>
    private static void DrawWindowSurface(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        float windowRounding)
    {
        var style = ImGui.GetStyle();
        var borderSize = MathF.Max(style.WindowBorderSize, 1f);
        var borderColor = ImGui.GetColorU32(ImGuiCol.Border);

        drawList.AddRectFilled(min, max, borderColor, windowRounding);

        var inset = new Vector2(borderSize, borderSize);
        var innerMin = min + inset;
        var innerMax = max - inset;
        var innerRounding = MathF.Max(0f, windowRounding - borderSize);

        WindowSurfaceGradient(out var topColor, out var bottomColor);
        DrawVerticalFade(drawList, innerMin, innerMax, topColor, bottomColor,
            innerRounding, roundTop: true, roundBottom: true);
    }

    private static void WindowSurfaceGradient(out Vector4 topColor, out Vector4 bottomColor)
    {
        var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        topColor = new Vector4(MathF.Min(bg.X * 1.3f, 1f), MathF.Min(bg.Y * 1.3f, 1f),
            MathF.Min(bg.Z * 1.35f, 1f), 1f);
        bottomColor = new Vector4(bg.X * 0.8f, bg.Y * 0.8f, bg.Z * 0.85f, 1f);
    }

    /// <summary>Evaluates the window surface gradient color at a screen-space Y.</summary>
    private static Vector4 WindowSurfaceColorAt(Vector2 parentMin, Vector2 parentMax, float y)
    {
        var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 1f);
        var top = parentMin.Y + borderSize;
        var bottom = parentMax.Y - borderSize;
        var t = Math.Clamp((y - top) / MathF.Max(bottom - top, 1f), 0f, 1f);
        WindowSurfaceGradient(out var topColor, out var bottomColor);
        return Vector4.Lerp(topColor, bottomColor, t);
    }

    private static void DrawTitleBarBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        float windowRounding, Vector4 accent)
    {
        var background = ImGui.ColorConvertFloat4ToU32(
            SpheneColors.WithAlpha(SpheneColors.BackgroundMid, TitleBarBackgroundAlpha));
        drawList.AddRectFilled(min, max, background, windowRounding, ImDrawFlags.RoundCornersTop);

        var accentLine = ImGui.ColorConvertFloat4ToU32(SpheneColors.WithAlpha(accent, TitleBarAccentLineAlpha));
        var lineMin = new Vector2(min.X, max.Y - TitleBarAccentLineHeight);
        drawList.AddRectFilled(lineMin, max, accentLine);
    }

    private static void DrawTitleBarDragRegion(string windowName, Vector2 min, float width, float height)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##{windowName}-titleDrag", new Vector2(width, height));
        ImGui.SetItemAllowOverlap();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }
    }

    private static void DrawTitleBarLeft(Vector2 min, float headerHeight, string title,
        UiSharedService uiShared, FontAwesomeIcon? leadingIcon, Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var textHeight = ImGui.GetTextLineHeight();
        var textY = min.Y + (headerHeight - textHeight) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(min.X + TitleBarPaddingX * scale, textY));

        if (leadingIcon.HasValue)
        {
            uiShared.IconText(leadingIcon.Value, accent);
            ImGui.SameLine(0, 8f * scale);
        }
        UiSharedService.ColorText(title, SpheneColors.TextPrimary);
    }

    private static void DrawTitleBarRight(Vector2 min, Vector2 max, float buttonSize,
        UiSharedService uiShared, IReadOnlyList<TitleBarButton>? rightButtons, string? trailingText, float scale)
    {
        var buttonY = min.Y + (max.Y - min.Y - buttonSize) * 0.5f;
        var x = max.X - TitleBarPaddingX * scale;
        var textHeight = ImGui.GetTextLineHeight();

        if (rightButtons != null)
        {
            for (var i = rightButtons.Count - 1; i >= 0; i--)
            {
                var button = rightButtons[i];
                x -= buttonSize;
                ImGui.SetCursorScreenPos(new Vector2(x, buttonY));
                if (uiShared.IconButton(button.Icon, null, null, null, buttonSize, button.StyleKey))
                {
                    button.OnClick();
                }
                if (!string.IsNullOrEmpty(button.Tooltip) && ImGui.IsItemHovered())
                {
                    UiSharedService.AttachToolTip(button.Tooltip);
                }
                x -= TitleBarButtonSpacing * scale;
            }
        }

        if (!string.IsNullOrEmpty(trailingText))
        {
            var textWidth = ImGui.CalcTextSize(trailingText).X;
            x -= textWidth;
            ImGui.SetCursorScreenPos(new Vector2(x, min.Y + (max.Y - min.Y - textHeight) * 0.5f));
            UiSharedService.ColorText(trailingText, ImGuiColors.DalamudGrey3);
        }
    }

    /// <summary>
    /// Draws a full-width card container with padded content, a tinted background,
    /// a subtle animated shimmer and a gently pulsing accent border.
    /// </summary>
    public static void DrawAccentCard(Action content, Vector4? accentColor = null, bool animatedBackdrop = true)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padding = AccentCardPadding * scale;
        var rounding = AccentCardRounding * scale;
        var availWidth = ImGui.GetContentRegionAvail().X;
        var accent = accentColor ?? SpheneColors.SpheneGold;

        var drawList = ImGui.GetWindowDrawList();
        var cardMin = ImGui.GetCursorScreenPos();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(cardMin + new Vector2(padding, padding));
        using (ImRaii.Group())
        {
            content();
        }

        var cardMax = new Vector2(
            MathF.Min(cardMin.X + availWidth,
                ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 1f * scale - ActiveContentInset),
            ImGui.GetItemRectMax().Y + padding);
        drawList.ChannelsSetCurrent(0);

        var background = new Vector4(accent.X * 0.16f, accent.Y * 0.14f, accent.Z * 0.07f, 0.82f);
        drawList.AddRectFilled(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(background), rounding);

        if (animatedBackdrop)
        {
            DrawAccentCardBackdrop(drawList, cardMin, cardMax, rounding, accent);
        }

        var pulse = 0.30f + 0.16f * MathF.Sin((float)ImGui.GetTime() * AccentCardBorderPulseSpeed);
        var borderColor = new Vector4(accent.X, accent.Y, accent.Z, pulse);
        drawList.AddRect(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(borderColor),
            rounding, ImDrawFlags.RoundCornersAll, AccentCardBorderThickness);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    private sealed class ScrollAreaState
    {
        public bool Begun;
        public bool ClipPushed;
        public bool Border;
        public bool GripHeld;
        public float GripAnim;
        public Vector2 ParentMin;
        public Vector2 ParentMax;
        public float ContentInset;
        public bool HideTopBorder;
    }

    private static readonly Stack<ScrollAreaState> ScrollAreaStack = new();

    /// <summary>
    /// Begins a child region without a visible scrollbar. Wheel scrolling still works.
    /// Must be closed with <see cref="EndScrollArea"/>, which draws fade hints at the
    /// top/bottom edges whenever more content exists in that direction.
    /// <paramref name="noPadding"/> removes the child window padding so content can
    /// reach the edges; inner spacing must then be managed manually.
    /// </summary>
    public static bool BeginScrollArea(string id, Vector2 size, bool border = false,
        ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None, bool noPadding = false,
        bool hideTopBorder = false)
    {
        var state = new ScrollAreaState
        {
            Border = border,
            HideTopBorder = hideTopBorder,
            ParentMin = ImGui.GetWindowPos(),
            ParentMax = ImGui.GetWindowPos() + ImGui.GetWindowSize(),
        };
        if (noPadding)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        }
        var begun = ImGui.BeginChild(id, size, false, windowFlags | ImGuiWindowFlags.NoScrollbar);
        if (noPadding)
        {
            ImGui.PopStyleVar();
            state.ContentInset = ScrollAreaContentInset * ImGuiHelpers.GlobalScale;
            ImGui.Indent(state.ContentInset);
        }
        state.Begun = begun;
        if (begun)
        {
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            GetScrollInsets(border, out _, out var insetY);
            if (border)
            {
                var clipX = 1f * ImGuiHelpers.GlobalScale;
                var inner = min + new Vector2(clipX, insetY);
                var innerMax = max - new Vector2(clipX, insetY);
                if (innerMax.X > inner.X && innerMax.Y > inner.Y)
                {
                    ImGui.PushClipRect(inner, innerMax, true);
                    state.ClipPushed = true;
                }
            }
            else
            {
                ImGui.PushClipRect(min, max, false);
                state.ClipPushed = true;
            }
        }
        ScrollAreaStack.Push(state);
        return begun;
    }

    /// <summary>
    /// Ends a child opened by <see cref="BeginScrollArea"/> and overlays the scroll fades.
    /// <paramref name="fadeBaseColor"/> should match the background behind the child;
    /// defaults to the window background color.
    /// </summary>
    public static void EndScrollArea(Vector4? fadeBaseColor = null)
    {
        var state = ScrollAreaStack.Count > 0 ? ScrollAreaStack.Pop() : new ScrollAreaState();

        if (state.Begun)
        {
            if (state.ContentInset > 0f)
            {
                ImGui.Unindent(state.ContentInset);
            }
            ImGuiHelpers.ScaledDummy(0, ScrollAreaContentInset);
            var scrollY = ImGui.GetScrollY();
            var scrollMaxY = ImGui.GetScrollMaxY();
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            if (state.ClipPushed)
            {
                ImGui.PopClipRect();
            }
            if (state.Border)
            {
                DrawCornerSpillMasks(min, max, state.ParentMin, state.ParentMax);
                DrawRoundedFrame(min, max, ImGui.GetStyle().ChildRounding,
                    MathF.Max(ImGui.GetStyle().ChildBorderSize, 1f), openTop: state.HideTopBorder);
            }
            DrawScrollFades(min, max, scrollY, scrollMaxY, state.Border, fadeBaseColor);
            var cornerTolerance = 4f * ImGuiHelpers.GlobalScale;
            var ownsCorner = state.ParentMax.X - max.X <= cornerTolerance
                && state.ParentMax.Y - max.Y <= cornerTolerance;
            if (!state.Border && ownsCorner)
            {
                DrawGripCorner(state.ParentMax, state);
            }
        }
        ImGui.EndChild();
    }

    private static void GetScrollInsets(bool border, out float insetX, out float insetY)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        if (border)
        {
            insetX = MathF.Max(style.ChildRounding, 3f * scale);
            insetY = MathF.Max(style.ChildBorderSize + 1f * scale, 2f * scale);
        }
        else
        {
            insetX = 0f;
            insetY = 0f;
        }
    }

    private static void DrawScrollFades(Vector2 min, Vector2 max, float scrollY,
        float scrollMaxY, bool border, Vector4? fadeBaseColor)
    {
        var canScrollUp = scrollY > ScrollFadeDeadZone;
        var canScrollDown = scrollMaxY - scrollY > ScrollFadeDeadZone;
        if (!canScrollUp && !canScrollDown)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var fadeHeight = MathF.Min(ScrollFadeHeight * scale, (max.Y - min.Y) * 0.3f);
        var baseColor = fadeBaseColor ?? style.Colors[(int)ImGuiCol.WindowBg];
        var opaqueColor = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, ScrollFadeMaxAlpha);
        var transparentColor = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 0f);
        var hintColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, ScrollHintChevronAlpha));
        var chevronSize = ScrollHintChevronSize * scale;

        var edgeInset = border ? style.ChildBorderSize + 1f * scale : 0f;
        var inner = min + new Vector2(edgeInset, edgeInset);
        var innerMax = max - new Vector2(edgeInset, edgeInset);
        if (innerMax.X <= inner.X || innerMax.Y <= inner.Y)
            return;
        var centerX = inner.X + (innerMax.X - inner.X) * 0.5f;
        var cornerRadius = border ? MathF.Max(0f, style.ChildRounding - edgeInset) : 0f;

        var clipMin = border ? inner : min;
        var clipMax = border ? innerMax : max;
        drawList.PushClipRect(clipMin, clipMax, border);

        if (canScrollUp)
        {
            DrawVerticalFade(drawList, inner, new Vector2(innerMax.X, inner.Y + fadeHeight),
                opaqueColor, transparentColor, cornerRadius, roundTop: border, roundBottom: false);
            DrawScrollHintPill(drawList, centerX, inner.Y, chevronSize, hintColor, pointsUp: true);
        }

        if (canScrollDown)
        {
            DrawVerticalFade(drawList, new Vector2(inner.X, innerMax.Y - fadeHeight), innerMax,
                transparentColor, opaqueColor, cornerRadius, roundTop: false, roundBottom: border);
            DrawScrollHintPill(drawList, centerX, innerMax.Y, chevronSize, hintColor, pointsUp: false);
        }

        drawList.PopClipRect();
    }

    /// <summary>
    /// Fills a vertical gradient band. When a corner zone is rounded, the band is
    /// drawn as horizontal strips whose inset follows the circle so the fade hugs
    /// the child rounding instead of stopping short of it.
    /// </summary>
    private static void DrawVerticalFade(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        Vector4 topColor, Vector4 bottomColor, float cornerRadius, bool roundTop, bool roundBottom)
    {
        var height = max.Y - min.Y;
        if (height <= 0f || max.X - min.X <= 0f)
            return;

        var topZone = roundTop ? MathF.Min(cornerRadius, height * 0.5f) : 0f;
        var bottomZone = roundBottom ? MathF.Min(cornerRadius, height - topZone) : 0f;
        var midMaxY = max.Y - bottomZone;
        var topSegments = Math.Clamp((int)MathF.Ceiling(topZone), 4, 48);
        var bottomSegments = Math.Clamp((int)MathF.Ceiling(bottomZone), 4, 48);

        for (var i = 0; i < topSegments && topZone > 0f; i++)
        {
            var y0 = min.Y + topZone * i / topSegments;
            var y1 = min.Y + topZone * (i + 1) / topSegments;
            FadeStrip(drawList, min, max, y0, y1,
                CircleInsetX(cornerRadius, (y0 + y1) * 0.5f - min.Y), topColor, bottomColor);
        }
        if (midMaxY > min.Y + topZone)
        {
            FadeStrip(drawList, min, max, min.Y + topZone, midMaxY, 0f, topColor, bottomColor);
        }
        for (var i = 0; i < bottomSegments && bottomZone > 0f; i++)
        {
            var y0 = midMaxY + bottomZone * i / bottomSegments;
            var y1 = midMaxY + bottomZone * (i + 1) / bottomSegments;
            FadeStrip(drawList, min, max, y0, y1,
                CircleInsetX(cornerRadius, max.Y - (y0 + y1) * 0.5f), topColor, bottomColor);
        }
    }

    private static void FadeStrip(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        float y0, float y1, float inset, Vector4 topColor, Vector4 bottomColor)
    {
        var height = max.Y - min.Y;
        var c0 = ImGui.ColorConvertFloat4ToU32(
            Vector4.Lerp(topColor, bottomColor, (y0 - min.Y) / height));
        var c1 = ImGui.ColorConvertFloat4ToU32(
            Vector4.Lerp(topColor, bottomColor, (y1 - min.Y) / height));
        drawList.AddRectFilledMultiColor(new Vector2(min.X + inset, y0),
            new Vector2(max.X - inset, y1), c0, c0, c1, c1);
    }

    private static float CircleInsetX(float radius, float distanceFromEdge)
    {
        if (radius <= 0f)
            return 0f;
        var d = Math.Clamp(distanceFromEdge, 0f, radius);
        return radius - MathF.Sqrt(2f * radius * d - d * d);
    }

    /// <summary>
    /// Paints the corner squares outside the child's rounding with the window
    /// surface color so content can never visually spill past the rounded edge.
    /// The border must be redrawn afterwards since the mask covers its arcs.
    /// </summary>
    private static void DrawCornerSpillMasks(Vector2 min, Vector2 max,
        Vector2 parentMin, Vector2 parentMax)
    {
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var rounding = MathF.Min(style.ChildRounding,
            MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);
        if (rounding <= 0.5f)
            return;

        drawList.PushClipRect(min, max, false);
        CornerCrescent(drawList, min, 1f, 1f, rounding, parentMin, parentMax);
        CornerCrescent(drawList, new Vector2(max.X, min.Y), -1f, 1f, rounding, parentMin, parentMax);
        CornerCrescent(drawList, max, -1f, -1f, rounding, parentMin, parentMax);
        CornerCrescent(drawList, new Vector2(min.X, max.Y), 1f, -1f, rounding, parentMin, parentMax);
        drawList.PopClipRect();
    }

    /// <summary>
    /// Fills the area inside a corner square but outside the corner circle using
    /// thin horizontal strips so spilled content is covered without touching the
    /// rounded frame. Each strip is tinted with the surface gradient at its Y.
    /// </summary>
    private static void CornerCrescent(ImDrawListPtr drawList, Vector2 corner,
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
            var color = ImGui.ColorConvertFloat4ToU32(
                WindowSurfaceColorAt(parentMin, parentMax, (y0 + y1) * 0.5f));
            drawList.AddRectFilled(
                new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)),
                new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)), color);
        }
    }

    /// <summary>
    /// Draws a child frame as filled edge strips and annulus corner sectors so
    /// every part — including the corner arcs — keeps exactly borderSize thickness.
    /// With <paramref name="openTop"/> the straight top segment is omitted while
    /// the top corner arcs are kept, making the frame read as open at the top.
    /// </summary>
    private static void DrawRoundedFrame(Vector2 min, Vector2 max, float rounding,
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

    private const float GripAnimSpeed = 8f;

    /// <summary>
    /// Stamps the window's bottom-right corner with a background triangle on the
    /// foreground draw list so the native resize grip is fully hidden, then draws a
    /// themed grip on top with hover/active feedback. The real ImGui resize hit test
    /// stays functional underneath.
    /// </summary>
    private static void DrawGripCorner(Vector2 corner, ScrollAreaState state)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        var gripSize = ImGui.GetFontSize() * ScrollFadeResizeGripScale;
        var stampSize = gripSize + style.WindowBorderSize + 2f * scale;

        var mouse = ImGui.GetIO().MousePos;
        var inTriangle = mouse.X >= corner.X - stampSize && mouse.X <= corner.X
            && mouse.Y >= corner.Y - stampSize && mouse.Y <= corner.Y
            && (corner.X - mouse.X) + (corner.Y - mouse.Y) <= stampSize;

        if (!state.GripHeld && inTriangle && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            state.GripHeld = true;
        }
        if (state.GripHeld && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            state.GripHeld = false;
        }

        var target = state.GripHeld ? 2f : inTriangle ? 1f : 0f;
        var step = ImGui.GetIO().DeltaTime * GripAnimSpeed;
        state.GripAnim = state.GripAnim < target
            ? MathF.Min(target, state.GripAnim + step)
            : MathF.Max(target, state.GripAnim - step);
        var t = state.GripAnim;

        var normal = style.Colors[(int)ImGuiCol.ResizeGrip];
        var hover = style.Colors[(int)ImGuiCol.ResizeGripHovered];
        var active = style.Colors[(int)ImGuiCol.ResizeGripActive];
        var background = style.Colors[(int)ImGuiCol.WindowBg];

        drawList.PushClipRect(corner - new Vector2(stampSize, stampSize) - new Vector2(1f, 1f),
            corner + new Vector2(scale, scale), false);
        var zoneTint = 0.10f + MathF.Min(t, 1f) * 0.45f + MathF.Max(0f, t - 1f) * 0.15f;
        var zoneColor = Vector4.Lerp(background, active, zoneTint);
        zoneColor.W = 1f;
        drawList.AddTriangleFilled(
            new Vector2(corner.X - stampSize, corner.Y),
            corner,
            new Vector2(corner.X, corner.Y - stampSize),
            ImGui.ColorConvertFloat4ToU32(zoneColor));

        var gripColor = t <= 1f
            ? Vector4.Lerp(normal, hover, t)
            : Vector4.Lerp(hover, active, t - 1f);
        var color = ImGui.ColorConvertFloat4ToU32(gripColor);

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

    private static void DrawScrollHintPill(ImDrawListPtr drawList, float centerX, float edgeY,
        float chevronSize, uint hintColor, bool pointsUp)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var halfWidth = chevronSize + ScrollHintPillPaddingX * scale;
        var halfHeight = chevronSize * 0.6f + ScrollHintPillPaddingY * scale;
        var inset = halfHeight + ScrollHintEdgeInset * scale;
        var bounce = MathF.Sin((float)ImGui.GetTime() * ScrollHintBounceSpeed)
            * ScrollHintBounceAmplitude * scale;
        var centerY = pointsUp ? edgeY + inset - bounce : edgeY - inset + bounce;
        var pillMin = new Vector2(centerX - halfWidth, centerY - halfHeight);
        var pillMax = new Vector2(centerX + halfWidth, centerY + halfHeight);

        var pillFill = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, ScrollHintPillAlpha));
        var pillBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, ScrollHintPillBorderAlpha));
        drawList.AddRectFilled(pillMin, pillMax, pillFill, halfHeight);
        drawList.AddRect(pillMin, pillMax, pillBorder, halfHeight);

        var tipY = pointsUp ? centerY - chevronSize * 0.6f : centerY + chevronSize * 0.6f;
        var baseY = pointsUp ? centerY + chevronSize * 0.6f : centerY - chevronSize * 0.6f;
        drawList.AddTriangleFilled(
            new Vector2(centerX - chevronSize, baseY),
            new Vector2(centerX + chevronSize, baseY),
            new Vector2(centerX, tipY), hintColor);
    }

    private static void DrawAccentCardBackdrop(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        float rounding, Vector4 accent)
    {
        var width = max.X - min.X;
        var height = max.Y - min.Y;
        if (width <= 0 || height <= 0)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var time = (float)ImGui.GetTime();
        var clipInset = new Vector2(rounding * 0.4f, rounding * 0.4f);
        drawList.PushClipRect(min + clipInset, max - clipInset, true);

        var glowHeight = MathF.Min(52f * scale, height);
        var glow = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.08f));
        drawList.AddRectFilledMultiColor(min, new Vector2(max.X, min.Y + glowHeight), glow, glow, 0, 0);

        var bandWidth = MathF.Min(90f * scale, width * 0.4f);
        var travel = width + bandWidth * 2f;
        var pos = time * AccentCardShimmerPixelsPerSecond * scale % travel;
        var bandStart = min.X - bandWidth + pos;
        var bandMid = bandStart + bandWidth * 0.5f;
        var shimmer = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.82f, 0.45f, 0.05f));
        drawList.AddRectFilledMultiColor(new Vector2(bandStart, min.Y), new Vector2(bandMid, max.Y), 0, shimmer, shimmer, 0);
        drawList.AddRectFilledMultiColor(new Vector2(bandMid, min.Y), new Vector2(bandStart + bandWidth, max.Y), shimmer, 0, 0, shimmer);

        drawList.PopClipRect();
    }
}
