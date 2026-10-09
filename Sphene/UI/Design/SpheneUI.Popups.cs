using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

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

        private sealed class DropdownState
        {
            public bool Open;
            public float T;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public float WinMinY;
            public float ContentH;
        }

        private static readonly Dictionary<string, DropdownState> _dropdowns = new(StringComparer.Ordinal);
        private static string? _activeDropdown;
        private static int _dropdownDepth;

        /// <summary>True while executing inside a <see cref="BeginDropdown"/> scope.</summary>
        public static bool InsideDropdown => _dropdownDepth > 0;

        /// <summary>
        /// Toggles the anchored dropdown <paramref name="id"/> and captures the
        /// anchor rect from the last submitted item — call this from the
        /// trigger button's click handler (its rect is still current then).
        /// </summary>
        public static void ToggleDropdown(string id)
        {
            if (!_dropdowns.TryGetValue(id, out var st))
            {
                st = new DropdownState();
                _dropdowns[id] = st;
            }
            st.Open = !st.Open;
            st.AnchorMin = ImGui.GetItemRectMin();
            st.AnchorMax = ImGui.GetItemRectMax();
        }

        /// <summary>
        /// Anchored dropdown that visually grows out of its trigger button:
        /// a sliver under the button eases down and left to
        /// <paramref name="contentSize"/> — right-aligned with the button's
        /// right edge, so the menu expands leftward and downward. While open
        /// the trigger is repainted as a flat-bottomed tab in the menu's
        /// surface color (its icon and label in accent), so button and menu
        /// read as one connected shape; clicking the button again, clicking
        /// outside, Escape or <see cref="CloseDropdown"/> reverses the
        /// animation. The window is focus-pinned while open so it stays in
        /// front of the host window. Returns true while visible — render
        /// content after it and always pair with <see cref="EndDropdown"/>.
        /// </summary>
        public static bool BeginDropdown(string id, Vector2 contentSize,
            FontAwesomeIcon icon = FontAwesomeIcon.None, string? label = null)
        {
            if (!_dropdowns.TryGetValue(id, out var st))
            {
                return false;
            }
            var dt = ImGui.GetIO().DeltaTime;
            st.T = Math.Clamp(st.T + (st.Open ? dt : -dt) * 7f, 0f, 1f);
            var eased = st.T * st.T * (3f - 2f * st.T);
            if (eased <= 0.004f && !st.Open)
            {
                _dropdowns.Remove(id);
                _flyouts.Clear();
                return false;
            }

            var btnSize = st.AnchorMax - st.AnchorMin;
            // height adapts to last frame's measured content so the menu never
            // reserves dead space below the cards nor clips them
            var targetH = st.ContentH > 0f ? MathF.Min(contentSize.Y, st.ContentH) : contentSize.Y;
            var size = Vector2.Lerp(new Vector2(btnSize.X, 3f * Scale),
                new Vector2(contentSize.X, targetH), eased);
            var pos = new Vector2(st.AnchorMax.X - size.X, st.AnchorMax.Y - 1f);
            var disp = ImGui.GetIO().DisplaySize;
            if (pos.X < 4f) pos.X = 4f;
            if (pos.Y + size.Y > disp.Y - 4f)
            {
                pos.Y = Math.Max(4f, st.AnchorMin.Y - size.Y + 1f);
            }
            ImGui.SetNextWindowPos(pos);
            ImGui.SetNextWindowSize(size);
            // focus-pin keeps the menu above the host window — but must yield
            // while an inner popup/flyout (combo, color picker) is open, else
            // the pin would pull the menu in front of its own popup every frame
            if (st.Open && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId)
                && !AnyFlyoutOpen)
            {
                ImGui.SetNextWindowFocus();
            }
            // fully opaque surface — a translucent popup bg lets the host
            // window shine through and shows as a displaced strip of content
            ImGui.PushStyleColor(ImGuiCol.WindowBg,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Surface, 1f)));
            // the window's own border stays invisible — the merged
            // button∪menu outline is drawn manually in DrawDropdownTab
            ImGui.PushStyleColor(ImGuiCol.Border, 0u);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * Scale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * Scale);
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(eased * 1.35f, 0f, 1f));
            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.Popup;
            var visible = ImGui.Begin($"##dropdown_{id}", flags);
            var wMin = ImGui.GetWindowPos();
            var wMax = wMin + ImGui.GetWindowSize();
            st.WinMinY = wMin.Y;
            _activeDropdown = id;
            var drawList = ImGui.GetWindowDrawList();
            DrawDropdownTab(drawList, st, wMin, wMax, eased, icon, label);
            var io = ImGui.GetIO();
            var m = io.MousePos;
            var inWindow = m.X >= wMin.X && m.X <= wMax.X && m.Y >= wMin.Y && m.Y <= wMax.Y;
            var anyPopup = ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId)
                || AnyFlyoutOpen;
            if (st.Open && !inWindow && !anyPopup
                && (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                    || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            {
                st.Open = false;
            }
            if (st.Open && ImGui.IsKeyPressed(ImGuiKey.Escape)
                && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            {
                st.Open = false;
            }
            if (!visible)
            {
                _activeDropdown = null;
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
                return false;
            }
            _dropdownDepth++;
            ContentBounds.PushNoneSentinel();
            return true;
        }

        /// <summary>
        /// Repaints the trigger button as a flat-bottomed tab that shares the
        /// menu surface: the fill extends into the window's top edge (or
        /// bottom edge when flipped) so both borders merge into one outline.
        /// </summary>
        private static void DrawDropdownTab(ImDrawListPtr drawList, DropdownState st,
            Vector2 wMin, Vector2 wMax, float eased, FontAwesomeIcon icon, string? label)
        {
            if (eased <= 0.05f)
            {
                return;
            }
            var btnSize = st.AnchorMax - st.AnchorMin;
            var frameR = ImGui.GetStyle().FrameRounding;
            var surface = ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Surface, 1f));
            var border = ColorUtils.ToU32(Colors.Border);
            var below = wMin.Y >= st.AnchorMin.Y;
            drawList.PushClipRectFullScreen();
            // the tab fill extends a hair past the button rect so it covers
            // the trigger's own border stroke — otherwise the button outline
            // stays visible inside the merged surface
            var cover = 1f * Scale;
            var winR = 10f * Scale;
            if (below)
            {
                drawList.AddRectFilled(st.AnchorMin - new Vector2(cover),
                    new Vector2(st.AnchorMax.X + cover, wMin.Y + 1f), surface,
                    frameR + cover, ImDrawFlags.RoundCornersTop);
                // fill the cove left by the window's rounded top-right corner
                drawList.AddRectFilled(new Vector2(wMax.X - winR - cover, wMin.Y - cover),
                    new Vector2(wMax.X + cover, wMin.Y + winR + cover), surface);
            }
            else
            {
                drawList.AddRectFilled(new Vector2(st.AnchorMin.X - cover, wMax.Y - 1f),
                    st.AnchorMax + new Vector2(cover), surface,
                    frameR + cover, ImDrawFlags.RoundCornersBottom);
                drawList.AddRectFilled(new Vector2(wMax.X - winR - cover, wMax.Y - winR - cover),
                    new Vector2(wMax.X + cover, wMax.Y + cover), surface);
            }
            // one continuous outline around button ∪ window — the border
            // climbs around the button's rounded top instead of cutting a
            // straight line under it
            AddDropdownOutline(drawList, st, wMin, wMax, frameR, 10f * Scale, below, border);
            var accent = ColorUtils.ToU32(Colors.Accent);
            var lineH = ImGui.GetTextLineHeight();
            var iconStr = icon != FontAwesomeIcon.None ? icon.ToIconString() : null;
            var iconSize = Vector2.Zero;
            if (iconStr != null)
            {
                ImGui.PushFont(UiBuilder.IconFont);
                iconSize = ImGui.CalcTextSize(iconStr);
                ImGui.PopFont();
            }
            var labelW = label != null ? ImGui.CalcTextSize(label).X : 0f;
            var gap = iconStr != null && label != null ? 5f * Scale : 0f;
            var tx = st.AnchorMin.X + (btnSize.X - iconSize.X - gap - labelW) * 0.5f;
            var ty = st.AnchorMin.Y + (btnSize.Y - lineH) * 0.5f;
            if (iconStr != null)
            {
                ImGui.PushFont(UiBuilder.IconFont);
                drawList.AddText(new Vector2(tx,
                    ty + (lineH - iconSize.Y) * 0.5f), accent, iconStr);
                ImGui.PopFont();
                tx += iconSize.X + gap;
            }
            if (label != null)
            {
                drawList.AddText(new Vector2(tx, ty), accent, label);
            }
            drawList.PopClipRect();
        }

        /// <summary>
        /// Strokes one continuous outline around the union of the trigger
        /// button and the dropdown window: the path leaves the window's top
        /// edge at the button's side, runs up around the button's rounded
        /// top corners and comes back down on the other side — so the border
        /// reads as if the menu and its button were cast as one shape.
        /// </summary>
        private static void AddDropdownOutline(ImDrawListPtr drawList, DropdownState st,
            Vector2 wMin, Vector2 wMax, float btnR, float winR, bool below, uint color)
        {
            const float pi = MathF.PI;
            var bMin = st.AnchorMin;
            var bMax = st.AnchorMax;
            if (below)
            {
                // clockwise, starting at the button's top-left arc
                Arc(drawList, new Vector2(bMin.X + btnR, bMin.Y + btnR), btnR, pi, pi * 1.5f);
                drawList.PathLineTo(new Vector2(bMax.X - btnR, bMin.Y));
                Arc(drawList, new Vector2(bMax.X - btnR, bMin.Y + btnR), btnR, pi * 1.5f, pi * 2f);
                drawList.PathLineTo(new Vector2(bMax.X, wMin.Y));
                drawList.PathLineTo(new Vector2(bMax.X, wMax.Y - winR));
                Arc(drawList, new Vector2(wMax.X - winR, wMax.Y - winR), winR, 0f, pi * 0.5f);
                drawList.PathLineTo(new Vector2(wMin.X + winR, wMax.Y));
                Arc(drawList, new Vector2(wMin.X + winR, wMax.Y - winR), winR, pi * 0.5f, pi);
                drawList.PathLineTo(new Vector2(wMin.X, wMin.Y + winR));
                Arc(drawList, new Vector2(wMin.X + winR, wMin.Y + winR), winR, pi, pi * 1.5f);
                drawList.PathLineTo(new Vector2(bMin.X, wMin.Y));
                drawList.PathLineTo(new Vector2(bMin.X, bMin.Y + btnR));
            }
            else
            {
                // flipped: window above, button hangs below with rounded bottom
                drawList.PathLineTo(new Vector2(bMax.X, wMax.Y));
                drawList.PathLineTo(new Vector2(bMax.X, bMax.Y - btnR));
                Arc(drawList, new Vector2(bMax.X - btnR, bMax.Y - btnR), btnR, 0f, pi * 0.5f);
                drawList.PathLineTo(new Vector2(bMin.X + btnR, bMax.Y));
                Arc(drawList, new Vector2(bMin.X + btnR, bMax.Y - btnR), btnR, pi * 0.5f, pi);
                drawList.PathLineTo(new Vector2(bMin.X, wMax.Y));
                drawList.PathLineTo(new Vector2(wMin.X + winR, wMax.Y));
                Arc(drawList, new Vector2(wMin.X + winR, wMax.Y - winR), winR, pi * 0.5f, pi);
                drawList.PathLineTo(new Vector2(wMin.X, wMin.Y + winR));
                Arc(drawList, new Vector2(wMin.X + winR, wMin.Y + winR), winR, pi, pi * 1.5f);
                drawList.PathLineTo(new Vector2(wMax.X - winR, wMin.Y));
                Arc(drawList, new Vector2(wMax.X - winR, wMin.Y + winR), winR, pi * 1.5f, pi * 2f);
                drawList.PathLineTo(new Vector2(wMax.X, wMax.Y));
            }
            drawList.PathStroke(color, ImDrawFlags.Closed, MathF.Max(1f, 1.25f * Scale));
        }

        /// <summary>Appends an arc segment to the current path (angles: 0 = +X, π/2 = +Y).</summary>
        private static void Arc(ImDrawListPtr drawList, Vector2 center, float radius,
            float aMin, float aMax)
        {
            var seg = Math.Max(3, (int)MathF.Ceiling((aMax - aMin) * radius / 6f));
            drawList.PathArcTo(center, radius, aMin, aMax, seg);
        }

        /// <summary>Closes the dropdown <paramref name="id"/> (plays the collapse animation).</summary>
        public static void CloseDropdown(string id)
        {
            if (_dropdowns.TryGetValue(id, out var st))
            {
                st.Open = false;
            }
        }

        /// <summary>Drops all dropdown and flyout state — call on page/window resets like <see cref="Fx.Reset"/>.</summary>
        public static void ResetDropdowns()
        {
            _dropdowns.Clear();
            _flyouts.Clear();
        }

        /// <summary>Ends a dropdown opened via <see cref="BeginDropdown"/>.</summary>
        public static void EndDropdown()
        {
            if (_activeDropdown != null
                && _dropdowns.TryGetValue(_activeDropdown, out var st))
            {
                // measured content height incl. bottom window padding — the
                // next BeginDropdown shrinks the menu to fit instead of
                // reserving the maximum height
                st.ContentH = Math.Max(0f, ImGui.GetCursorScreenPos().Y - st.WinMinY
                    + ImGui.GetStyle().WindowPadding.Y);
            }
            _activeDropdown = null;
            if (_dropdownDepth > 0)
            {
                _dropdownDepth--;
            }
            ContentBounds.Pop();
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        private sealed class FlyoutState
        {
            public bool Open;
            public float T;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public bool JustOpened;
            public int LastFrame = -1;
        }

        private static readonly Dictionary<string, FlyoutState> _flyouts = new(StringComparer.Ordinal);

        // entries age out when their host stopped rendering them — otherwise a
        // flyout orphaned by a closed dropdown would hold the gates forever
        private static bool AnyFlyoutOpen
        {
            get
            {
                var frame = ImGui.GetFrameCount();
                foreach (var st in _flyouts.Values)
                {
                    if (st.Open && st.LastFrame >= frame - 1)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Toggles the anchored flyout <paramref name="id"/> and captures the
        /// anchor rect from the last submitted item — call this from the
        /// trigger's click handler (its rect is still current then). Flyouts
        /// are plain styled windows pinned above their dropdown parent; they
        /// bypass <see cref="ImGui.OpenPopup"/>/<see cref="ImGui.BeginPopup"/>,
        /// which lose their first open frame against the dropdown's own
        /// <see cref="ImGuiWindowFlags.Popup"/> layer and focus pinning.
        /// </summary>
        public static void ToggleFlyout(string id)
        {
            if (!_flyouts.TryGetValue(id, out var st))
            {
                st = new FlyoutState();
                _flyouts[id] = st;
            }
            st.Open = !st.Open;
            st.AnchorMin = ImGui.GetItemRectMin();
            st.AnchorMax = ImGui.GetItemRectMax();
            st.JustOpened = st.Open;
            foreach (var kv in _flyouts)
            {
                if (!ReferenceEquals(kv.Value, st))
                {
                    kv.Value.Open = false;
                }
            }
        }

        /// <summary>
        /// Anchored flyout panel that fades in at the right edge of its
        /// trigger (below it, flipping above near the viewport bottom), sized
        /// to its content via <see cref="ImGuiWindowFlags.AlwaysAutoResize"/>.
        /// It is focus-pinned so it renders above the host dropdown, and
        /// closes on outside click or Escape. Returns true while visible —
        /// render content after it and always pair with
        /// <see cref="EndFlyout"/>.
        /// </summary>
        public static bool BeginFlyout(string id)
        {
            if (!_flyouts.TryGetValue(id, out var st))
            {
                return false;
            }
            st.LastFrame = ImGui.GetFrameCount();
            st.T = Math.Clamp(st.T + (st.Open
                ? ImGui.GetIO().DeltaTime : -ImGui.GetIO().DeltaTime) * 12f, 0f, 1f);
            var eased = st.T * st.T * (3f - 2f * st.T);
            if (eased <= 0.004f && !st.Open)
            {
                _flyouts.Remove(id);
                return false;
            }
            var disp = ImGui.GetIO().DisplaySize;
            var gap = 4f * Scale;
            var flip = st.AnchorMax.Y + gap + 260f * Scale > disp.Y - 4f;
            ImGui.SetNextWindowPos(
                flip ? new Vector2(st.AnchorMax.X, st.AnchorMin.Y - gap)
                    : new Vector2(st.AnchorMax.X, st.AnchorMax.Y + gap),
                ImGuiCond.None,
                flip ? new Vector2(1f, 1f) : new Vector2(1f, 0f));
            if (st.Open)
            {
                ImGui.SetNextWindowFocus();
            }
            ImGui.PushStyleColor(ImGuiCol.WindowBg,
                ColorUtils.ToU32(ColorUtils.WithAlpha(Colors.Surface, 1f)));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f) * Scale);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * Scale);
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(eased * 1.35f, 0f, 1f));
            // deliberately NOT ImGuiWindowFlags.Popup: Begin() reads the live
            // popup stack for Popup-flagged windows — a window never opened
            // through OpenPopup gets a garbage PopupId, which makes focus and
            // close handling erratic (opens only after several clicks). The
            // focus pin + manual outside-close below do the job instead.
            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.AlwaysAutoResize;
            var visible = ImGui.Begin($"##flyout_{id}", flags);
            var wMin = ImGui.GetWindowPos();
            var wMax = wMin + ImGui.GetWindowSize();
            var m = ImGui.GetIO().MousePos;
            var inWindow = m.X >= wMin.X && m.X <= wMax.X && m.Y >= wMin.Y && m.Y <= wMax.Y;
            if (st.Open && !st.JustOpened && !inWindow
                && (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                    || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            {
                st.Open = false;
            }
            if (st.Open && ImGui.IsKeyPressed(ImGuiKey.Escape)
                && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            {
                st.Open = false;
            }
            st.JustOpened = false;
            if (!visible)
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(1);
                return false;
            }
            ContentBounds.PushNoneSentinel();
            return true;
        }

        /// <summary>Ends a flyout opened via <see cref="BeginFlyout"/>.</summary>
        public static void EndFlyout()
        {
            ContentBounds.Pop();
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(1);
        }
    }
}
