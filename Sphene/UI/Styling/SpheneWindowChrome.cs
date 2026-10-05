using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.UI.Components;
using Sphene.UI.Theme;
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

    private const int SectionCardGlowRings = 8;
    private const float SectionCardGlowRingStep = 9f;
    private const float SectionCardGlowSpeed = 0.9f;
    private const float SectionCardGlowBaseAlpha = 0.07f;
    private const int SectionCardMoteCount = 3;
    private const float SectionCardMoteSpeed = 1.4f;
    private const float SectionCardMoteAlpha = 0.10f;
    private const float SectionCardMoteRadius = 1.5f;

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

    private const float WindowBlurMaxStrength = 14f;
    private const float WindowBlurNoiseOpacity = 0f;
    private static readonly Vector4 WindowBlurTint = new(0.02f, 0.02f, 0.03f, 0.08f);

    /// <summary>
    /// Standard horizontal inner spacing for scroll areas created with noPadding.
    /// Applied automatically as a left indent; edge-bleed elements must respect the
    /// same value on the right (see <see cref="ActiveContentInset"/>).
    /// </summary>
    public const float ScrollAreaContentInset = 10f;

    /// <summary>Total height of the custom title bar including its accent line.</summary>
    public static float TitleBarHeight =>
        ImGui.GetFrameHeight() + TitleBarPaddingY * 2f * ImGuiHelpers.GlobalScale;

    private static float ActiveContentInset =>
        ScrollAreaStack.Count > 0 ? ScrollAreaStack.Peek().ContentInset : 0f;

    public sealed record TitleBarButton(FontAwesomeIcon Icon, string Tooltip, Action OnClick,
        string? StyleKey = null, SpheneBrandIcons.Brand? BrandIcon = null);

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
            var buttons = rightButtons?.ToList() ?? [];
            // Keep leading brand-icon buttons grouped on the far left, collapse after them
            var insertAt = 0;
            while (insertAt < buttons.Count && buttons[insertAt].BrandIcon.HasValue) insertAt++;
            buttons.Insert(insertAt, toggle);
            rightButtons = buttons;
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
        var accent = accentColor ?? SpheneCustomTheme.CurrentTheme.AccentCyan;

        ImGui.PushClipRect(min, windowPos + ImGui.GetWindowSize(), false);
        DrawWindowSurface(drawList, windowPos, windowPos + ImGui.GetWindowSize(), style.WindowRounding);
        DrawTitleBarBackground(drawList, min, max, windowPos.Y + ImGui.GetWindowSize().Y,
            style.WindowRounding, accent);
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
        var borderSize = MathF.Max(style.WindowBorderSize, 0f);
        if (borderSize > 0f)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.Border), windowRounding);
        }

        var inset = new Vector2(borderSize, borderSize);
        var innerRounding = MathF.Max(0f, windowRounding - borderSize);

        WindowSurfaceGradient(out var topColor, out var bottomColor);
        DrawVerticalFade(drawList, min + inset, max - inset, topColor, bottomColor,
            innerRounding, innerRounding, innerRounding, innerRounding);
    }

    private static void WindowSurfaceGradient(out Vector4 topColor, out Vector4 bottomColor)
    {
        var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        var topAlpha = MathF.Min(bg.W * 1.05f, 1f);
        topColor = new Vector4(MathF.Min(bg.X * 1.3f, 1f), MathF.Min(bg.Y * 1.3f, 1f),
            MathF.Min(bg.Z * 1.35f, 1f), topAlpha);
        bottomColor = new Vector4(bg.X * 0.8f, bg.Y * 0.8f, bg.Z * 0.85f, bg.W);
    }

    /// <summary>Evaluates the window surface gradient color at a screen-space Y.</summary>
    private static Vector4 WindowSurfaceColorAt(Vector2 parentMin, Vector2 parentMax, float y)
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
        var background = ImGui.ColorConvertFloat4ToU32(
            SpheneColors.WithAlpha(SpheneCustomTheme.CurrentTheme.PrimaryDark, TitleBarBackgroundAlpha));
        var corners = max.Y >= windowBottom - windowRounding
            ? ImDrawFlags.RoundCornersAll
            : ImDrawFlags.RoundCornersTop;
        drawList.AddRectFilled(min, max, background, windowRounding, corners);

        var accentLine = ImGui.ColorConvertFloat4ToU32(SpheneColors.WithAlpha(accent, TitleBarAccentLineAlpha));
        var cornerInset = Math.Clamp(max.Y + windowRounding - windowBottom, 0f, windowRounding);
        var lineMin = new Vector2(min.X + cornerInset, max.Y - TitleBarAccentLineHeight);
        var lineMax = new Vector2(max.X - cornerInset, max.Y);
        drawList.AddRectFilled(lineMin, lineMax, accentLine);
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
        UiSharedService.ColorText(title, SpheneCustomTheme.CurrentTheme.TextPrimary);
    }

    private static void DrawTitleBarRight(Vector2 min, Vector2 max, float buttonSize,
        UiSharedService uiShared, IReadOnlyList<TitleBarButton>? rightButtons, string? trailingText, float scale)
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
                    while (runStart - 1 >= 0 && rightButtons[runStart - 1].BrandIcon.HasValue) runStart--;
                    var runCount = i - runStart + 1;
                    var stripHeight = max.Y - min.Y - TitleBarAccentLineHeight;
                    var segWidth = stripHeight * 1.15f;
                    x -= segWidth * runCount;
                    DrawBrandButtonStrip(uiShared, rightButtons, runStart, runCount,
                        new Vector2(x, min.Y), segWidth, stripHeight);
                    i = runStart - 1;
                    x -= TitleBarButtonSpacing * scale;
                    continue;
                }
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
                i--;
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
    /// Draws a run of brand-icon title bar buttons as one joined strip spanning
    /// the full bar height minus the accent line, using the shared
    /// <see cref="SpheneBrandIcons.BrandStrip"/> renderer (seam color blend,
    /// alpha-faded edges, white glyphs).
    /// </summary>
    private static void DrawBrandButtonStrip(UiSharedService uiShared,
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
        SpheneBrandIcons.BrandStrip(uiShared, $"titlebar{start}", segments, height, segWidth * count);
    }

    /// <summary>
    /// Draws a full-width card container with padded content, a tinted background,
    /// a subtle animated shimmer and a gently pulsing accent border.
    /// </summary>
    public static void DrawAccentCard(Action content, Vector4? accentColor = null,
        bool animatedBackdrop = true, string? id = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padding = AccentCardPadding * scale;
        var rounding = AccentCardRounding * scale;
        var availWidth = ImGui.GetContentRegionAvail().X;
        var accent = accentColor ?? SpheneColors.SpheneGold;

        var drawList = ImGui.GetWindowDrawList();
        var cardMin = ImGui.GetCursorScreenPos();
        var cardRightX = MathF.Min(cardMin.X + availWidth,
            ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 1f * scale - ActiveContentInset);
        var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padding * 2f);
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(cardMin + new Vector2(padding, padding));
        using (ImRaii.Group())
        {
            DrawCardContentRegion(innerWidth, id, content);
        }

        var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padding);
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

    /// <summary>
    /// Draws a calm full-width section card: a softly tinted background, a thin
    /// accent border with a gentle pulse and an optional accent title with a
    /// fading underline. Suited for grouping settings blocks; use
    /// <see cref="DrawAccentCard"/> for prominent promo/status cards.
    /// </summary>
    /// <param name="content">Card content draw calls.</param>
    /// <param name="title">Optional accent-colored card title.</param>
    /// <param name="accentColor">Accent color for title, underline and border.</param>
    /// <param name="description">Optional grey description under the title.</param>
    /// <param name="animatedBackdrop">Draws the shimmer/glow backdrop inside the card.</param>
    public static void DrawSectionCard(Action content, string? title = null,
        Vector4? accentColor = null, string? description = null, bool animatedBackdrop = false,
        string? id = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var padding = AccentCardPadding * scale;
        var rounding = AccentCardRounding * scale;
        var availWidth = ImGui.GetContentRegionAvail().X;
        var accent = accentColor ?? SpheneCustomTheme.CurrentTheme.AccentCyan;

        var drawList = ImGui.GetWindowDrawList();
        var cardMin = ImGui.GetCursorScreenPos();
        var cardRightX = MathF.Min(cardMin.X + availWidth,
            ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - 1f * scale - ActiveContentInset);
        var innerWidth = MathF.Max(60f * scale, cardRightX - cardMin.X - padding * 2f);
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(cardMin + new Vector2(padding, padding));
        using (ImRaii.Group())
        {
            DrawCardContentRegion(innerWidth, id ?? title, () =>
            {
                if (!string.IsNullOrEmpty(title))
                {
                    UiSharedService.ColorText(title, accent);
                    if (!string.IsNullOrEmpty(description))
                    {
                        UiSharedService.ColorTextWrapped(description, ImGuiColors.DalamudGrey);
                    }
                    var lineMin = ImGui.GetCursorScreenPos() + new Vector2(0, 2f * scale);
                    var lineMax = new Vector2(lineMin.X + innerWidth, lineMin.Y + 1f * scale);
                    var lineCol = ImGui.ColorConvertFloat4ToU32(
                        new Vector4(accent.X, accent.Y, accent.Z, 0.4f));
                    drawList.AddRectFilledMultiColor(lineMin, lineMax, lineCol, 0, 0, lineCol);
                    ImGuiHelpers.ScaledDummy(0, 7);
                }
                content();
            });
        }

        var cardMax = new Vector2(cardRightX, ImGui.GetItemRectMax().Y + padding);
        drawList.ChannelsSetCurrent(0);

        var accentTint = new Vector4(accent.X * 0.10f, accent.Y * 0.09f, accent.Z * 0.05f, 0.55f);
        var secondary = SpheneCustomTheme.CurrentTheme.SecondaryDark;
        var background = Vector4.Lerp(accentTint,
            new Vector4(secondary.X, secondary.Y, secondary.Z, 0.55f), secondary.W * 0.5f);
        drawList.AddRectFilled(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(background), rounding);

        if (animatedBackdrop)
        {
            DrawSectionCardBackdrop(drawList, cardMin, cardMax, rounding, accent);
        }

        var pulse = 0.22f + 0.08f * MathF.Sin((float)ImGui.GetTime() * AccentCardBorderPulseSpeed);
        var borderColor = new Vector4(accent.X, accent.Y, accent.Z, pulse);
        drawList.AddRect(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(borderColor),
            rounding, ImDrawFlags.RoundCornersAll, AccentCardBorderThickness);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(cardMin + new Vector2(0, cardMax.Y - cardMin.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>
    /// Prepends Dalamud's blur-behind effect to the current window draw list so
    /// the scene behind the window shows through frosted. Must be called inside
    /// the window (after ImGui.Begin). Only takes effect on the main viewport;
    /// <paramref name="strengthFactor"/> is the user-facing [0,1] blur amount.
    /// </summary>
    public static void DrawWindowBlur(float strengthFactor, Vector4? tintColor = null,
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
    /// Convenience wrapper around <see cref="DrawWindowBlur"/>.
    /// </summary>
    public static void DrawThemedWindowBlur()
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        DrawWindowBlur(theme.EnableWindowBlur ? theme.WindowBlurStrength : 0f);
    }

    /// <summary>
    /// Runs <paramref name="content"/> inside a borderless, fixed-width
    /// single-column table so every widget respects the given content width:
    /// wrapped text, right-aligned SameLine rows, stretched tables and
    /// full-width items all stop at the right bound.
    /// Reusable anywhere a bounded content region is needed.
    /// </summary>
    public static void DrawCardContentRegion(float width, string? id, Action content)
    {
        var tableId = $"##card-content-{(id ?? "region")}-{(int)ImGui.GetCursorScreenPos().Y}";
        CardContentBounds.Push(new Vector2(ImGui.GetCursorScreenPos().X, ImGui.GetCursorScreenPos().X + width));
        try
        {
            using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, Vector2.Zero))
            {
                if (!ImGui.BeginTable(tableId, 1, ImGuiTableFlags.None, new Vector2(width, 0f)))
                {
                    return;
                }
            }
            ImGui.TableNextColumn();
            ImGui.PushTextWrapPos(width);
            content();
            ImGui.PopTextWrapPos();
            ImGui.EndTable();
        }
        finally
        {
            CardContentBounds.Pop();
        }
    }

    /// <summary>
    /// Screen-space right bound of the innermost card content region, or -1
    /// when not inside one. <c>GetContentRegionAvail</c> measures up to the
    /// window edge even inside card tables, so controls needing the real
    /// card bound read this instead.
    /// </summary>
    public static float CurrentCardContentRightBound
        => CardContentBounds.Count > 0 ? CardContentBounds.Peek().Y : -1f;

    /// <summary>
    /// Screen-space left bound of the innermost card content region, or -1
    /// when not inside one.
    /// </summary>
    public static float CurrentCardContentLeftBound
        => CardContentBounds.Count > 0 ? CardContentBounds.Peek().X : -1f;

    private static readonly Stack<Vector2> CardContentBounds = new();

    private sealed class ScrollAreaState
    {
        public string Id = string.Empty;
        public bool Begun;
        public bool ClipPushed;
        public bool Border;
        public bool GripHeld;
        public float GripAnim;
        public Vector2 ParentMin;
        public Vector2 ParentMax;
        public float ContentInset;
        public bool HideTopBorder;
        public float TopInset;
    }

    private static readonly Stack<ScrollAreaState> ScrollAreaStack = new();

    /// <summary>
    /// Begins a child region without a visible scrollbar. Wheel scrolling still works.
    /// Must be closed with <see cref="EndScrollArea"/>, which draws fade hints at the
    /// top/bottom edges whenever more content exists in that direction.
    /// <paramref name="noPadding"/> removes the child window padding so content can
    /// reach the edges; inner spacing must then be managed manually.
    /// <paramref name="contentInset"/> indents content inside a noPadding child;
    /// negative values use the default <see cref="ScrollAreaContentInset"/>.
    /// </summary>
    public static bool BeginScrollArea(string id, Vector2 size, bool border = false,
        ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None, bool noPadding = false,
        bool hideTopBorder = false, float topInset = 0f, float contentInset = -1f)
    {
        var state = new ScrollAreaState
        {
            Id = id,
            Border = border,
            HideTopBorder = hideTopBorder,
            TopInset = topInset,
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
            state.ContentInset = contentInset < 0f
                ? ScrollAreaContentInset * ImGuiHelpers.GlobalScale
                : contentInset;
            if (state.ContentInset > 0f)
            {
                ImGui.Indent(state.ContentInset);
            }
        }
        state.Begun = begun;
        if (begun)
        {
            if (state.TopInset > 0f && ImGui.GetScrollY() <= ScrollFadeDeadZone)
            {
                state.TopInset = 0f;
            }
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            GetScrollInsets(border, out _, out var insetY);
            if (border)
            {
                var clipX = 1f * ImGuiHelpers.GlobalScale;
                var inner = min + new Vector2(clipX, insetY + state.TopInset);
                var innerMax = max - new Vector2(clipX, insetY);
                if (innerMax.X > inner.X && innerMax.Y > inner.Y)
                {
                    ImGui.PushClipRect(inner, innerMax, true);
                    state.ClipPushed = true;
                }
            }
            else
            {
                // Horizontally content may reach the window's inner edge so
                // separators/fades can span wider than a padded child. A
                // non-intersecting rect is required: the child's own clip is
                // limited to its bounds and would cut anything wider off.
                var borderSize = MathF.Max(ImGui.GetStyle().WindowBorderSize, 0f);
                ImGui.PushClipRect(
                    new Vector2(state.ParentMin.X + borderSize, min.Y + state.TopInset),
                    new Vector2(state.ParentMax.X - borderSize, max.Y), false);
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
    /// <paramref name="edgePaddingX"/>, when non-negative, spans the fades from the
    /// parent window's inner edge inset by that amount instead of the child bounds.
    /// <paramref name="fadeSpanX"/> sets an explicit horizontal fade span
    /// (min.X, max.X), e.g. a rail reaching the window edge while its child is
    /// inset by padding; takes precedence over <paramref name="edgePaddingX"/>.
    /// <paramref name="fadeMinY"/> lets the top fade start above the child
    /// bounds, e.g. docking directly under a title bar accent line.
    /// <paramref name="bottomInset"/> adds extra spacing below the last item,
    /// e.g. to match the window padding a padded sibling child keeps.
    /// </summary>
    public static void EndScrollArea(Vector4? fadeBaseColor = null, float edgePaddingX = -1f,
        Vector2? fadeSpanX = null, float? fadeMinY = null, float bottomInset = 0f)
    {
        var state = ScrollAreaStack.Count > 0 ? ScrollAreaStack.Pop() : new ScrollAreaState();

        if (state.Begun)
        {
            if (state.ContentInset > 0f)
            {
                ImGui.Unindent(state.ContentInset);
            }
            ImGuiHelpers.ScaledDummy(0, ScrollAreaContentInset);
            if (bottomInset > 0f)
            {
                ImGui.Dummy(new Vector2(0f, bottomInset));
            }
            var scrollY = ImGui.GetScrollY();
            var scrollMaxY = ImGui.GetScrollMaxY();
            var min = ImGui.GetWindowPos();
            var max = min + ImGui.GetWindowSize();
            if (state.ClipPushed)
            {
                ImGui.PopClipRect();
            }
            ImGui.EndChild();

            // Every ImGui window (including child windows) owns a draw list, and
            // lists composite parent-first, then children in creation order.
            // Overlays drawn inside the scroll child would render under any
            // nested child content (e.g. pair list rows), so a sibling overlay
            // child created afterwards carries all chrome drawing: it composites
            // above the whole subtree while staying inside the window z-order.
            var resumeCursor = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(min);
            ImGui.BeginChild(state.Id + "##scroll-overlay", max - min, false,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings);
            if (state.Border)
            {
                DrawCornerSpillMasks(min, max, state.ParentMin, state.ParentMax);
                DrawRoundedFrame(min, max, ImGui.GetStyle().ChildRounding,
                    MathF.Max(ImGui.GetStyle().ChildBorderSize, 1f), openTop: state.HideTopBorder);
            }
            DrawScrollFades(min, max, scrollY, scrollMaxY, state.Border, state.TopInset,
                state.ParentMin, state.ParentMax, fadeBaseColor, edgePaddingX, fadeSpanX, fadeMinY);
            var styleVars = ImGui.GetStyle();
            var cornerReach = MathF.Max(styleVars.WindowBorderSize,
                    MathF.Max(styleVars.WindowPadding.X, styleVars.WindowPadding.Y))
                + 4f * ImGuiHelpers.GlobalScale;
            var ownsCorner = state.ParentMax.X - max.X <= cornerReach
                && state.ParentMax.Y - max.Y <= cornerReach;
            RedrawWindowBorder(min, max, state.ParentMin, state.ParentMax, cornerReach);
            // The grip stamp goes last so its corner triangle covers the
            // re-stroked border instead of the border cutting through the grip.
            if (!state.Border && ownsCorner)
            {
                DrawGripCorner(state.ParentMax, state);
            }
            ImGui.EndChild();
            ImGui.SetCursorScreenPos(resumeCursor);
        }
        else
        {
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// Re-strokes the parent window border when a scroll area reaches a window
    /// edge. Fades and overlays draw after ImGui's own border pass, so a fresh
    /// stroke on top keeps the border visually dominant at every edge.
    /// </summary>
    private static void RedrawWindowBorder(Vector2 min, Vector2 max,
        Vector2 parentMin, Vector2 parentMax, float tolerance)
    {
        var style = ImGui.GetStyle();
        var borderSize = MathF.Max(style.WindowBorderSize, 0f);
        if (borderSize <= 0f)
            return;
        var reach = borderSize + tolerance;
        var touchesEdge = min.X <= parentMin.X + reach
            || min.Y <= parentMin.Y + reach
            || parentMax.X - max.X <= reach
            || parentMax.Y - max.Y <= reach;
        if (!touchesEdge)
            return;
        // Window draw list keeps this in the window's z-order so overlapping
        // windows cover it; the explicit clip reaches past the child bounds.
        ImGui.PushClipRect(parentMin, parentMax, false);
        ImGui.GetWindowDrawList().AddRect(parentMin, parentMax,
            ImGui.GetColorU32(ImGuiCol.Border), style.WindowRounding,
            ImDrawFlags.None, borderSize);
        ImGui.PopClipRect();
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
        float scrollMaxY, bool border, float topInset, Vector2 parentMin, Vector2 parentMax,
        Vector4? fadeBaseColor, float edgePaddingX = -1f, Vector2? fadeSpanX = null,
        float? fadeMinY = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        // Fade strength: 1 while far from the end, shrinks to 0 as the edge
        // approaches so the band narrows and its opaque lip dissolves softly.
        var fadeRange = ScrollFadeHeight * scale;
        var upStrength = Math.Clamp(scrollY / fadeRange, 0f, 1f);
        var downStrength = Math.Clamp((scrollMaxY - scrollY) / fadeRange, 0f, 1f);
        if (upStrength <= 0f && downStrength <= 0f)
            return;

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
            return;
        var centerX = inner.X + (innerMax.X - inner.X) * 0.5f;
        var childRadius = border ? MathF.Max(0f, style.ChildRounding - edgeInset) : 0f;
        var windowRadius = MathF.Max(0f, style.WindowRounding - outerBorder);

        // A fade reaching the window's inner edge must hug the window's corner
        // arcs so it never paints over the border or outside the silhouette.
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
            var hint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, ScrollHintChevronAlpha * upStrength));
            DrawVerticalFade(drawList, inner, new Vector2(innerMax.X, inner.Y + height),
                opaque, transparentColor, topLeftRadius, topRightRadius, 0f, 0f);
            DrawScrollHintPill(drawList, centerX, inner.Y, chevronSize, hint, pointsUp: true);
        }

        if (downStrength > 0f)
        {
            var height = fadeHeight * SmoothStep(downStrength);
            var opaque = FadeLip(baseColor, downStrength);
            var hint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, ScrollHintChevronAlpha * downStrength));
            DrawVerticalFade(drawList, new Vector2(inner.X, innerMax.Y - height), innerMax,
                transparentColor, opaque, 0f, 0f, bottomLeftRadius, bottomRightRadius);
            DrawScrollHintPill(drawList, centerX, innerMax.Y, chevronSize, hint, pointsUp: false);
        }

        drawList.PopClipRect();
    }

    /// <summary>
    /// Draws a separator across the current scroll area spanning from the parent
    /// window's inner edge, inset by <paramref name="edgePadding"/> per side.
    /// Falls back to a regular separator outside of a scroll area.
    /// </summary>
    public static void ScrollAreaSeparator(float edgePadding)
    {
        if (ScrollAreaStack.Count == 0)
        {
            ImGui.Separator();
            return;
        }
        var state = ScrollAreaStack.Peek();
        var style = ImGui.GetStyle();
        var border = MathF.Max(style.WindowBorderSize, 0f);
        var spacing = MathF.Max(style.ItemSpacing.Y, 1f);
        var y = ImGui.GetCursorScreenPos().Y + spacing * 0.5f;
        var minX = state.ParentMin.X + border + edgePadding;
        var maxX = state.ParentMax.X - border - edgePadding;
        if (maxX > minX)
        {
            ImGui.GetWindowDrawList().AddLine(new Vector2(minX, y),
                new Vector2(maxX, y), ImGui.GetColorU32(ImGuiCol.Separator));
        }
        ImGui.Dummy(new Vector2(0f, spacing));
    }

    private static float SmoothStep(float t) => t * t * (3f - 2f * t);

    /// <summary>
    /// Opaque lip color for a scroll fade. Stays fully solid until the last
    /// third of the shrink range, then dissolves so the band disappears
    /// without a hard pop at the scroll boundary.
    /// </summary>
    private static Vector4 FadeLip(Vector4 baseColor, float strength)
    {
        var alpha = ScrollFadeMaxAlpha * Math.Clamp(strength / 0.35f, 0f, 1f);
        return new Vector4(baseColor.X, baseColor.Y, baseColor.Z, alpha);
    }

    /// <summary>
    /// Fills a vertical gradient band. Corners with a radius are drawn as
    /// horizontal strips whose per-side inset follows the circle so the fade
    /// hugs the rounding instead of stopping short of it or painting past it.
    /// </summary>
    private static void DrawVerticalFade(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        Vector4 topColor, Vector4 bottomColor,
        float topLeftRadius, float topRightRadius, float bottomLeftRadius, float bottomRightRadius)
    {
        var height = max.Y - min.Y;
        if (height <= 0f || max.X - min.X <= 0f)
            return;

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

    private static void FadeStrip(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        float y0, float y1, float insetLeft, float insetRight, Vector4 topColor, Vector4 bottomColor)
    {
        var height = max.Y - min.Y;
        var c0 = ImGui.ColorConvertFloat4ToU32(
            Vector4.Lerp(topColor, bottomColor, (y0 - min.Y) / height));
        var c1 = ImGui.ColorConvertFloat4ToU32(
            Vector4.Lerp(topColor, bottomColor, (y1 - min.Y) / height));
        drawList.AddRectFilledMultiColor(new Vector2(min.X + insetLeft, y0),
            new Vector2(max.X - insetRight, y1), c0, c0, c1, c1);
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

    /// <summary>
    /// Draws a sticky section header pinned inside the frame's top edge. The
    /// first label is pinned from the start; when the next section's own label
    /// scrolls up it pushes the pinned band out of the way and takes over.
    /// <paramref name="labels"/> must contain each label's current screen-space
    /// center Y plus its accent color, recorded while drawing the scrolled
    /// content.
    /// <paramref name="pinCenterY"/> optionally sets the band's resting center
    /// on screen – pass the natural center of the first label so the band
    /// overlays it exactly and simply stays put while content scrolls.
    /// </summary>
    public static void DrawPinnedSectionLabel(Vector2 frameMin, Vector2 frameMax,
        IReadOnlyList<(string Label, float CenterY, Vector4 Accent)> labels,
        Vector2 surfaceMin, Vector2 surfaceMax, Vector4? baseColor = null,
        float? pinCenterY = null)
    {
        if (labels.Count == 0)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var lineHeight = ImGui.GetTextLineHeight();
        var bandHeight = lineHeight + 8f * scale;
        var pinCenter = pinCenterY ?? (frameMin.Y + bandHeight * 0.5f);

        var current = -1;
        var next = -1;
        for (var i = 0; i < labels.Count; i++)
        {
            if (labels[i].CenterY <= pinCenter)
            {
                current = i;
            }
            else
            {
                next = i;
                break;
            }
        }
        if (current < 0)
        {
            // The first label is pinned even before it has scrolled past.
            current = 0;
            next = labels.Count > 1 ? 1 : -1;
        }

        var centerY = pinCenter;
        if (next >= 0)
        {
            centerY = MathF.Min(centerY, labels[next].CenterY - (bandHeight + lineHeight) * 0.5f);
        }

        var drawList = ImGui.GetWindowDrawList();
        var rounding = MathF.Max(ImGui.GetStyle().ChildRounding, 2f);
        var label = labels[current].Label;
        var textWidth = ImGui.CalcTextSize(label).X;
        var stripMin = new Vector2(frameMin.X + rounding * 0.5f, centerY - bandHeight * 0.5f);
        var stripMax = new Vector2(frameMax.X - rounding * 0.5f, centerY + bandHeight * 0.5f);
        var stripBase = baseColor ?? WindowSurfaceColorAt(surfaceMin, surfaceMax, centerY);
        var stripColor = baseColor.HasValue
            ? SpheneColors.WithAlpha(baseColor.Value, 1f)
            : Vector4.Lerp(stripBase, SpheneCustomTheme.CurrentTheme.HeaderBg, 0.35f);
        stripColor.W = MathF.Max(stripColor.W, 0.9f);

        // Clip to the frame so the band slides out cleanly when pushed away
        drawList.PushClipRect(new Vector2(frameMin.X, frameMin.Y), frameMax, true);
        drawList.AddRectFilled(stripMin, stripMax, ImGui.ColorConvertFloat4ToU32(stripColor),
            MathF.Min(rounding * 0.5f, bandHeight * 0.5f));
        drawList.AddText(new Vector2(
            frameMin.X + (frameMax.X - frameMin.X - textWidth) * 0.5f,
            centerY - lineHeight * 0.5f),
            ImGui.ColorConvertFloat4ToU32(labels[current].Accent), label);
        drawList.PopClipRect();
    }

    private const float GripAnimSpeed = 8f;

    /// <summary>
    /// Stamps the window's bottom-right corner with a background triangle on the
    /// window draw list so the native resize grip is fully hidden, then draws a
    /// themed grip on top with hover/active feedback. The real ImGui resize hit
    /// test stays functional underneath.
    /// </summary>
    private static void DrawGripCorner(Vector2 corner, ScrollAreaState state)
    {
        var drawList = ImGui.GetWindowDrawList();
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
        // Hover/active are derived instead of read: scopes may hide the native
        // variants (alpha 0) to suppress ImGui's border-resize ticks.
        var hover = GripHighlight(normal, 0.18f);
        var active = GripHighlight(normal, 0.34f);
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

    private static Vector4 GripHighlight(Vector4 baseColor, float lift)
    {
        return new Vector4(
            MathF.Min(baseColor.X + lift, 1f),
            MathF.Min(baseColor.Y + lift, 1f),
            MathF.Min(baseColor.Z + lift, 1f),
            baseColor.W);
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

    private static void DrawSectionCardBackdrop(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        float rounding, Vector4 accent)
    {
        var width = max.X - min.X;
        var height = max.Y - min.Y;
        if (width <= 0 || height <= 0)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var time = (float)ImGui.GetTime();
        // Clip at the card bounds minus a pixel so the glow hugs the border
        // instead of leaving a dark gap in front of it.
        drawList.PushClipRect(min + new Vector2(1f, 1f), max - new Vector2(1f, 1f), true);

        // Breathing bloom anchored at the corner arc center, faked with
        // concentric alpha rings since ImGui has no radial gradients.
        // Quadratic falloff keeps the outer edge soft and seamless.
        var pulse = 0.65f + 0.35f * MathF.Sin(time * SectionCardGlowSpeed);
        var bloomCenter = max - new Vector2(rounding, rounding);
        for (var i = SectionCardGlowRings; i >= 1; i--)
        {
            var falloff = 1f - (i - 1f) / SectionCardGlowRings;
            var alpha = SectionCardGlowBaseAlpha * falloff * falloff * pulse;
            var ringColor = new Vector4(accent.X, accent.Y, accent.Z, alpha);
            drawList.AddCircleFilled(bloomCenter, i * SectionCardGlowRingStep * scale,
                ImGui.ColorConvertFloat4ToU32(ringColor));
        }

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
                ImGui.ColorConvertFloat4ToU32(moteColor));
        }

        drawList.PopClipRect();
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
