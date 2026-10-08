using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services.Mediator;
using Sphene.Utils;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// FontAwesome icon primitives: glyph text, icon buttons and
    /// icon+label buttons — including the per-key style override system
    /// (<see cref="ThemeConfiguration.ButtonStyles"/>). All glyphs render
    /// through the shared icon font handle.
    /// </summary>
    public static class Icons
    {
        /// <summary>Measured size of an icon glyph.</summary>
        public static Vector2 Size(FontAwesomeIcon icon)
        {
            using var font = PushIconFont();
            return ImGui.CalcTextSize(icon.ToIconString());
        }

        /// <summary>Standard button size for an icon glyph.</summary>
        public static Vector2 ButtonSize(FontAwesomeIcon icon)
        {
            using var font = PushIconFont();
            return ImGuiHelpers.GetButtonSize(icon.ToIconString());
        }

        /// <summary>Natural width of an <see cref="TextButton"/> for the given content.</summary>
        public static float TextButtonWidth(FontAwesomeIcon icon, string text)
        {
            Vector2 iconSize;
            using (PushIconFont())
            {
                iconSize = ImGui.CalcTextSize(icon.ToIconString());
            }
            var textSize = ImGui.CalcTextSize(text);
            var gap = 3f * SpheneUI.Scale;
            return iconSize.X + textSize.X + ImGui.GetStyle().FramePadding.X * 2f + gap;
        }

        /// <summary>Icon glyph as text, in the themed text color.</summary>
        public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
            => Icon(icon, color.HasValue ? ColorUtils.ToU32(color.Value) : ImGui.GetColorU32(ImGuiCol.Text));

        /// <summary>Icon glyph as text in an explicit packed color.</summary>
        public static void Icon(FontAwesomeIcon icon, uint color)
        {
            if (IconFont is { } font)
            {
                Text.Font(font, icon.ToIconString(), color);
            }
            else
            {
                using var pushed = ImRaii.PushColor(ImGuiCol.Text, color);
                ImGui.TextUnformatted(icon.ToIconString());
            }
        }

        /// <summary>Checkmark/cross icon colored by the boolean state.</summary>
        public static void BoolIcon(bool value, bool inline = true)
        {
            using var green = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, value);
            using var red = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, !value);
            if (inline)
            {
                ImGui.SameLine();
            }
            Icon(value ? FontAwesomeIcon.Check : FontAwesomeIcon.Times);
        }

        /// <summary>Pushes the shared icon font. Shared across design modules.</summary>
        internal static IDisposable PushFont() => PushIconFont();

        private static IDisposable PushIconFont()
            => IconFont?.Push() ?? new EmptyScope();

        private sealed class EmptyScope : IDisposable
        {
            public void Dispose() { }
        }

        /// <summary>
        /// Icon-only button rendered as an empty ImGui button plus a centered
        /// glyph on the draw list. Honors <paramref name="styleKey"/> overrides
        /// (size deltas, colors, icon offset) from the theme configuration.
        /// </summary>
        public static bool Button(FontAwesomeIcon icon, float? height = null, float? iconScale = null,
            float? iconPixelSize = null, float? width = null, string? styleKey = null)
        {
            var glyph = icon.ToIconString();

            ImGui.PushID(glyph);
            Vector2 glyphSize;
            using (PushIconFont())
            {
                var scale = iconPixelSize.HasValue
                    ? iconPixelSize.Value / ImGui.GetFontSize()
                    : (iconScale ?? 1f);
                if (iconPixelSize.HasValue)
                {
                    scale = MathF.Max(1f, MathF.Round(scale));
                }
                if (MathF.Abs(scale - 1f) > 0.0001f)
                {
                    ImGui.SetWindowFontScale(scale);
                }
                glyphSize = ImGui.CalcTextSize(glyph);
                if (MathF.Abs(scale - 1f) > 0.0001f)
                {
                    ImGui.SetWindowFontScale(1f);
                }
            }
            var drawList = ImGui.GetWindowDrawList();
            var cursorPos = ImGui.GetCursorScreenPos();
            var buttonWidth = width ?? (glyphSize.X + ImGui.GetStyle().FramePadding.X * 2f);
            var buttonHeight = height ?? ImGui.GetFrameHeight();

            var buttonPos = cursorPos;
            bool result;
            var theme = Theme.Current;
            ButtonStyleOverride? overrideStyle = null;
            if (!string.IsNullOrEmpty(styleKey))
            {
                theme.ButtonStyles.TryGetValue(styleKey, out overrideStyle);
            }
            if (overrideStyle != null)
            {
                buttonWidth += overrideStyle.WidthDelta;
                buttonHeight += overrideStyle.HeightDelta;
            }
            result = DrawButtonFrame(buttonWidth, buttonHeight, overrideStyle, theme);

            var glyphPos = new Vector2(buttonPos.X + (buttonWidth / 2f) - (glyphSize.X / 2f),
                buttonPos.Y + buttonHeight / 2f - (glyphSize.Y / 2f));
            if (overrideStyle != null)
            {
                glyphPos += overrideStyle.IconOffset;
            }
            using (PushIconFont())
            {
                var scale = iconPixelSize.HasValue
                    ? iconPixelSize.Value / ImGui.GetFontSize()
                    : (iconScale ?? 1f);
                if (iconPixelSize.HasValue)
                {
                    scale = MathF.Max(1f, MathF.Round(scale));
                }
                if (MathF.Abs(scale - 1f) > 0.0001f)
                {
                    ImGui.SetWindowFontScale(scale);
                }
                var iconColorU32 = overrideStyle?.Icon.HasValue == true
                    ? ImGui.GetColorU32(overrideStyle.Icon.Value)
                    : ImGui.GetColorU32(ImGuiCol.Text);
                drawList.AddText(glyphPos, iconColorU32, glyph);
                if (MathF.Abs(scale - 1f) > 0.0001f)
                {
                    ImGui.SetWindowFontScale(1f);
                }
            }
            ImGui.PopID();

            if (result && !string.IsNullOrEmpty(styleKey) && ButtonStyleManagerUI.IsPickerEnabled)
            {
                Mediator?.Publish(new ThemeNavigateToButtonSettingsMessage(styleKey));
                return false;
            }

            return result;
        }

        /// <summary>Icon + label button in the standard theme colors.</summary>
        public static bool TextButton(FontAwesomeIcon icon, string text, float? width = null,
            bool isInPopup = false, string? styleKey = null)
        {
            return TextButtonInternal(icon, text,
                isInPopup ? ColorHelpers.RgbaUintToVector4(ImGui.GetColorU32(ImGuiCol.PopupBg)) : null,
                width <= 0 ? null : width,
                null, null, styleKey);
        }

        /// <summary>Icon + label button tinted with the theme's action-button colors.</summary>
        public static bool ActionButton(FontAwesomeIcon icon, string text, float? width = null,
            string? styleKey = null)
        {
            var theme = Theme.Current;
            return TextButtonInternal(icon, text, theme.CompactActionButton, width <= 0 ? null : width,
                theme.CompactActionButtonHovered, theme.CompactActionButtonActive, styleKey);
        }

        private static bool TextButtonInternal(FontAwesomeIcon icon, string text,
            Vector4? defaultColor = null, float? width = null,
            Vector4? hoveredColor = null, Vector4? activeColor = null, string? styleKey = null)
        {
            var pushedColors = 0;
            var pushedVars = 0;
            if (defaultColor.HasValue)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, defaultColor.Value);
                pushedColors++;
            }
            if (hoveredColor.HasValue)
            {
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoveredColor.Value);
                pushedColors++;
            }
            if (activeColor.HasValue)
            {
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, activeColor.Value);
                pushedColors++;
            }
            ButtonStyleOverride? overrideStyle = null;
            if (!string.IsNullOrEmpty(styleKey))
            {
                var theme = Theme.Current;
                if (theme.ButtonStyles.TryGetValue(styleKey, out var found))
                {
                    overrideStyle = found;
                    if (found.Button.HasValue) { ImGui.PushStyleColor(ImGuiCol.Button, found.Button.Value); pushedColors++; }
                    if (found.ButtonHovered.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonHovered, found.ButtonHovered.Value); pushedColors++; }
                    if (found.ButtonActive.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonActive, found.ButtonActive.Value); pushedColors++; }
                    if (found.Text.HasValue) { ImGui.PushStyleColor(ImGuiCol.Text, found.Text.Value); pushedColors++; }
                    ImGui.PushStyleColor(ImGuiCol.Border, found.Border ?? theme.Border); pushedColors++;
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, found.BorderSize ?? 0f); pushedVars++;
                }
            }

            ImGui.PushID(text);
            Vector2 glyphSize;
            using (PushIconFont())
            {
                glyphSize = ImGui.CalcTextSize(icon.ToIconString());
            }
            var textSize = ImGui.CalcTextSize(text);
            var drawList = ImGui.GetWindowDrawList();
            var cursorPos = ImGui.GetCursorScreenPos();
            var gap = 3f * SpheneUI.Scale;
            var buttonWidth = width ?? glyphSize.X + textSize.X + ImGui.GetStyle().FramePadding.X * 2f + gap;
            var buttonHeight = ImGui.GetFrameHeight();
            if (overrideStyle != null)
            {
                buttonWidth += overrideStyle.WidthDelta;
                buttonHeight += overrideStyle.HeightDelta;
            }
            var result = ImGui.Button(string.Empty, new Vector2(buttonWidth, buttonHeight));
            var glyphPos = new Vector2(cursorPos.X + ImGui.GetStyle().FramePadding.X,
                cursorPos.Y + ImGui.GetStyle().FramePadding.Y);
            using (PushIconFont())
            {
                if (overrideStyle != null)
                {
                    glyphPos += overrideStyle.IconOffset;
                }
                var iconColorU32 = overrideStyle?.Icon.HasValue == true
                    ? ImGui.GetColorU32(overrideStyle.Icon.Value)
                    : ImGui.GetColorU32(ImGuiCol.Text);
                drawList.AddText(glyphPos, iconColorU32, icon.ToIconString());
            }
            var labelPos = new Vector2(glyphPos.X + glyphSize.X + gap,
                cursorPos.Y + ImGui.GetStyle().FramePadding.Y);
            var labelColorU32 = overrideStyle?.Text.HasValue == true
                ? ImGui.GetColorU32(overrideStyle.Text.Value)
                : ImGui.GetColorU32(ImGuiCol.Text);
            drawList.AddText(labelPos, labelColorU32, text);
            ImGui.PopID();
            if (pushedVars > 0)
            {
                ImGui.PopStyleVar(pushedVars);
            }
            if (pushedColors > 0)
            {
                ImGui.PopStyleColor(pushedColors);
            }

            return result;
        }

        /// <summary>Renders the clickable button frame with style-override colors pushed.</summary>
        private static bool DrawButtonFrame(float buttonWidth, float buttonHeight,
            ButtonStyleOverride? overrideStyle, ThemeConfiguration theme)
        {
            var pushedColors = 0;
            var pushedVars = 0;
            if (overrideStyle != null)
            {
                if (overrideStyle.Button.HasValue) { ImGui.PushStyleColor(ImGuiCol.Button, overrideStyle.Button.Value); pushedColors++; }
                if (overrideStyle.ButtonHovered.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonHovered, overrideStyle.ButtonHovered.Value); pushedColors++; }
                if (overrideStyle.ButtonActive.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonActive, overrideStyle.ButtonActive.Value); pushedColors++; }
                if (overrideStyle.Text.HasValue) { ImGui.PushStyleColor(ImGuiCol.Text, overrideStyle.Text.Value); pushedColors++; }
            }
            ImGui.PushStyleColor(ImGuiCol.Border, overrideStyle?.Border ?? theme.Border); pushedColors++;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, overrideStyle?.BorderSize ?? 0f); pushedVars++;
            var result = ImGui.Button(string.Empty, new Vector2(buttonWidth, buttonHeight));
            if (pushedVars > 0)
            {
                ImGui.PopStyleVar(pushedVars);
            }
            if (pushedColors > 0)
            {
                ImGui.PopStyleColor(pushedColors);
            }
            return result;
        }
    }
}
