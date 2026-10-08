using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// Styled popup helpers — ImGui popups wrapped in the design-system popup
    /// theme so rounding, border and padding apply to the popup frame itself
    /// (a scope pushed after Begin would only affect its content).
    /// </summary>
    public static class Popups
    {
        /// <summary>Flags shared by styled popup windows (fixed size, no scroll chrome).</summary>
        public static readonly ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        private static readonly Stack<IDisposable> _styledPopupThemeStack = new();

        /// <summary>
        /// <see cref="ImGui.BeginPopup"/> wrapped in the design-system popup
        /// theme: the configured rounding, border and padding are pushed before
        /// the popup window is created, so the popup frame itself is styled.
        /// Pair with <see cref="EndStyled"/>.
        /// </summary>
        public static bool BeginStyled(string id)
        {
            var themeScope = Theme.ContextMenuScope();
            if (!ImGui.BeginPopup(id))
            {
                themeScope.Dispose();
                return false;
            }
            _styledPopupThemeStack.Push(themeScope);
            ContentBounds.PushNoneSentinel();
            return true;
        }

        /// <summary>Ends a popup opened via <see cref="BeginStyled"/>.</summary>
        public static void EndStyled()
        {
            ImGui.EndPopup();
            ContentBounds.Pop();
            if (_styledPopupThemeStack.Count > 0)
            {
                _styledPopupThemeStack.Pop().Dispose();
            }
        }

        /// <summary>
        /// <see cref="ImGui.BeginPopupModal"/> wrapped in the design-system
        /// popup theme so the modal frame is styled as well. Pair with
        /// <see cref="EndStyled"/> (calls <c>ImGui.EndPopup</c>).
        /// </summary>
        public static bool BeginModalStyled(string label, ref bool open,
            ImGuiWindowFlags flags = default)
        {
            var themeScope = Theme.ContextMenuScope();
            if (!ImGui.BeginPopupModal(label, ref open,
                    flags == default ? WindowFlags : flags))
            {
                themeScope.Dispose();
                return false;
            }
            _styledPopupThemeStack.Push(themeScope);
            ContentBounds.PushNoneSentinel();
            return true;
        }

        /// <summary>
        /// <see cref="BeginModalStyled(string, ref bool, ImGuiWindowFlags)"/>
        /// for modals without a close button (e.g. progress dialogs that must
        /// not be dismissed).
        /// </summary>
        public static bool BeginModalStyled(string label,
            ImGuiWindowFlags flags = default)
        {
            var themeScope = Theme.ContextMenuScope();
            if (!ImGui.BeginPopupModal(label, flags == default ? WindowFlags : flags))
            {
                themeScope.Dispose();
                return false;
            }
            _styledPopupThemeStack.Push(themeScope);
            ContentBounds.PushNoneSentinel();
            return true;
        }

        /// <summary>
        /// <see cref="ImGui.BeginPopupContextItem"/> wrapped in the
        /// design-system popup theme. Pair with <see cref="EndStyled"/>.
        /// </summary>
        public static bool BeginContextStyled(string? id = null,
            ImGuiPopupFlags flags = ImGuiPopupFlags.MouseButtonRight)
        {
            var themeScope = Theme.ContextMenuScope();
            if (!ImGui.BeginPopupContextItem(id, flags))
            {
                themeScope.Dispose();
                return false;
            }
            _styledPopupThemeStack.Push(themeScope);
            ContentBounds.PushNoneSentinel();
            return true;
        }
    }
}
