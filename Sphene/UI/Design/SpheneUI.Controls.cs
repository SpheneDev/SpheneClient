using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    /// <summary>
    /// All value controls of the design system · labeled rows (label left in
    /// the muted column tone, control docked to the shared right bound), slide
    /// toggles, sliders, drags, inputs, combos, color edits, buttons, swatches,
    /// chips, selectables, progress bars and status dots. Raw ImGui widgets
    /// stay private to this module; callers always go through these members.
    /// </summary>
    public static partial class Controls
    {
        /// <summary>Standard compact width for numeric/small value controls.</summary>
        public static float CompactWidth => 140f * SpheneUI.Scale;

        /// <summary>
        /// Content width remaining up to the real bound (card edge inside card
        /// regions, window content edge otherwise). <c>GetContentRegionAvail</c>
        /// measures past card bounds inside card tables · use this instead.
        /// </summary>
        public static float BoundedAvailX()
        {
            var bound = Layout.ContentRightBound;
            if (bound > 0f)
            {
                return MathF.Max(0f, bound - ImGui.GetCursorScreenPos().X);
            }
            return ImGui.GetContentRegionAvail().X;
        }

        /// <summary>
        /// Remaining row width up to the shared control edge: the bound minus
        /// the trailing icon column that labeled controls and toggles reserve,
        /// minus optional extra trailing content drawn via SameLine.
        /// </summary>
        public static float FillRowWidth(float extraTrailing = 0f)
            => MathF.Max(0f, BoundedAvailX() - Metrics.TrailingIconReserve - extraTrailing);

        /// <summary>
        /// Moves the cursor so a hidden-label control of <paramref name="width"/>
        /// ends flush at the shared control edge (bound minus the trailing icon
        /// column and <paramref name="extraTrailing"/> for items that follow on
        /// the same row).
        /// </summary>
        public static void DockToRightEdge(float width, float extraTrailing = 0f)
        {
            var reserve = Metrics.TrailingIconReserve + extraTrailing;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX()
                + MathF.Max(0f, BoundedAvailX() - reserve - width));
        }

        /// <summary>
        /// Moves the cursor so an item/group of <paramref name="width"/> ends
        /// flush at the card-aligned edge: inside cards the pushed content
        /// bound (the card's inner content edge), at page level the shared
        /// card edge (<see cref="Layout.CardAlignedAvailX"/>). The tighter of
        /// the two wins · the scroll area pushes its own wider bound, so the
        /// min is required on both sides. Unlike <see cref="DockToRightEdge"/>
        /// no trailing icon column is reserved, so docked clusters close on
        /// the same edge help icons use.
        /// </summary>
        public static void DockToCardEdge(float width)
        {
            var cursor = ImGui.GetCursorScreenPos();
            var rightEdge = cursor.X + Layout.CardAlignedAvailX();
            var bound = Layout.ContentRightBound;
            if (bound > 0f)
            {
                rightEdge = MathF.Min(rightEdge, bound);
            }
            ImGui.SetCursorScreenPos(new Vector2(
                MathF.Max(cursor.X, rightEdge - width), cursor.Y));
        }

        /// <summary>
        /// SameLine only when the next item of the given width still fits
        /// inside the real content bound; otherwise stays on the new line so
        /// the item is never clipped at the card edge.
        /// </summary>
        public static bool SameLineIfFits(float nextItemWidth)
        {
            var cursor = ImGui.GetCursorScreenPos();
            var bound = Layout.ContentRightBound;
            var rightBound = bound > 0f ? bound : cursor.X + ImGui.GetContentRegionAvail().X;
            // SameLine continues at the previous item's right edge, not at the
            // line start · measuring from the cursor would always claim a full
            // line of room and let items render past the bound.
            var itemStartX = MathF.Max(cursor.X, ImGui.GetItemRectMax().X);
            if (itemStartX + ImGui.GetStyle().ItemSpacing.X + nextItemWidth > rightBound)
            {
                return false;
            }
            ImGui.SameLine();
            return true;
        }

        /// <summary>
        /// Settings-style field row: a label aligned to the control's frame
        /// text, then a fill-width control that stops
        /// <paramref name="trailingWidth"/> short of the bound so SameLine
        /// followers (buttons, icons) share the row. Use for
        /// label + input + action-button rows instead of hand-computing
        /// <c>GetContentRegionAvail</c> · that measures to the window edge
        /// and overshoots the card bound.
        /// </summary>
        public static void FieldRow(string label, float trailingWidth, Action drawControl)
        {
            ImGui.AlignTextToFramePadding();
            Text.Plain(label);
            ImGui.SameLine();
            var gap = trailingWidth > 0f ? ImGui.GetStyle().ItemSpacing.X : 0f;
            var minWidth = Metrics.ControlHeight * 4f;
            ImGui.SetNextItemWidth(MathF.Max(minWidth,
                BoundedAvailX() - trailingWidth - gap));
            drawControl();
        }

        /// <summary>
        /// Prepares a settings row for a labeled value control: a visible label
        /// renders in the muted column tone and the control docks to the real
        /// content bound (card edge inside card regions, the window content
        /// edge otherwise). Standard-tier controls always dock to the bound;
        /// only controls with an explicit <paramref name="preferredWidth"/>
        /// narrower than ~45% of the row sit right after their label.
        /// Hidden labels ("##id") pass through unchanged; mid-line calls only
        /// clamp the control width. Returns the id to pass to the ImGui widget.
        /// </summary>
        internal static string PrepareLabeledControl(string label, float trailingWidth = 0f,
            float preferredWidth = -1f, bool dockRight = false)
            => PrepareLabeledControl(label, trailingWidth, preferredWidth, dockRight, out _);

        /// <summary>
        /// Same as the other overload but also reports the width applied via
        /// SetNextItemWidth · composite controls (input + attached button)
        /// need it to subdivide their frame.
        /// </summary>
        internal static string PrepareLabeledControl(string label, float trailingWidth,
            float preferredWidth, bool dockRight, out float controlWidth)
        {
            Lint.CheckThemeScope(label);
            controlWidth = 0f;
            var labelSplit = label.IndexOf("##", StringComparison.Ordinal);
            var text = labelSplit < 0 ? label : label[..labelSplit];
            if (text.Length == 0)
            {
                var hiddenPos = ImGui.GetCursorScreenPos();
                var hiddenBound = Layout.ContentRightBound;
                if (hiddenBound > 0f)
                {
                    // The help column reserve also applies to hidden-label
                    // controls so they dock flush with labeled rows.
                    hiddenBound -= MathF.Max(trailingWidth, Metrics.TrailingIconReserve);
                }
                if (preferredWidth <= 0f && dockRight && hiddenBound > 0f)
                {
                    var autoWidth = MathF.Max(60f * SpheneUI.Scale,
                        MathF.Min(ImGui.CalcItemWidth(), (hiddenBound - hiddenPos.X) * 0.5f));
                    if (hiddenBound - autoWidth > hiddenPos.X)
                    {
                        ImGui.SetCursorScreenPos(new Vector2(hiddenBound - autoWidth, hiddenPos.Y));
                    }
                    ImGui.SetNextItemWidth(autoWidth);
                    controlWidth = autoWidth;
                }
                else if (preferredWidth > 0f)
                {
                    var width = preferredWidth;
                    if (hiddenBound > 0f)
                    {
                        width = MathF.Min(width, MathF.Max(0f, hiddenBound - hiddenPos.X));
                        if (dockRight && hiddenBound - width > hiddenPos.X)
                        {
                            ImGui.SetCursorScreenPos(new Vector2(hiddenBound - width, hiddenPos.Y));
                        }
                    }
                    ImGui.SetNextItemWidth(width);
                    controlWidth = width;
                }
                return label;
            }

            var scale = SpheneUI.Scale;
            var cursor = ImGui.GetCursorScreenPos();
            var leftBound = Layout.ContentLeftBound;
            if (leftBound <= 0f)
            {
                leftBound = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X;
            }

            // Line start also covers indented sub-rows: when the previous item
            // sits on a line above the cursor nothing was drawn on this row yet.
            var isLineStart = cursor.X <= leftBound + 1f
                || ImGui.GetItemRectMin().Y < cursor.Y - 1f;
            // Inside a RowGroup a labeled control at line start owns a row:
            // the ambient group centers it on the shared row height and
            // hairline-separates it from the previous row. Pending rows
            // auto-complete so no tail hook is needed here.
            var grouped = isLineStart && GroupState.RowStart();
            if (grouped)
            {
                cursor = ImGui.GetCursorScreenPos();
            }

            var rightBound = grouped ? GroupState.CurrentRightBound : Layout.ContentRightBound;
            if (rightBound <= 0f)
            {
                rightBound = cursor.X + ImGui.GetContentRegionAvail().X;
            }
            // Reserve the same help-icon column the toggles use so every value
            // control ends flush with the switch edge instead of overshooting it.
            rightBound -= MathF.Max(trailingWidth, Metrics.TrailingIconReserve);
            var avail = MathF.Max(0f, rightBound - cursor.X);
            var labelWidth = ImGui.CalcTextSize(text).X;
            // Standard tier never exceeds half the row so labels stay readable
            // and controls do not sprawl across the card.
            var desiredWidth = preferredWidth > 0f
                ? preferredWidth
                : MathF.Min(ImGui.CalcItemWidth(), avail * 0.5f);
            controlWidth = MathF.Max(60f * scale,
                MathF.Min(desiredWidth, avail - labelWidth - ImGui.GetStyle().ItemInnerSpacing.X));

            if (isLineStart)
            {
                ImGui.AlignTextToFramePadding();
                var lineY = ImGui.GetCursorScreenPos().Y;
                Text.Colored(text, ImGuiColors.DalamudGrey);
                // Standard-tier controls always dock to the right bound. Only
                // compact controls with an explicit preferred width sit right
                // after their label so no dead space opens up between them.
                var controlX = dockRight || preferredWidth <= 0f || controlWidth >= avail * 0.45f
                    ? rightBound - controlWidth
                    : cursor.X + labelWidth + ImGui.GetStyle().ItemInnerSpacing.X * 2f;
                ImGui.SetCursorScreenPos(new Vector2(controlX, lineY));
                ImGui.SetNextItemWidth(controlWidth);
                // Keep the full label inside the widget id ("##Text##id"): the
                // leading "##" hides it from the widget while the visible text
                // still distinguishes ids. Returning only the "##id" suffix
                // would collapse every "Label##" + blockId control onto one id.
                return "##" + label;
            }

            // Mid-line: keep the original label so its trailing text still
            // renders next to the control inside the reserved space.
            ImGui.SetNextItemWidth(controlWidth);
            return label;
        }

        /// <summary>Content-fit width for a combo: longest item text plus frame and arrow.</summary>
        internal static float ComboFitWidth(IEnumerable<string> itemTexts)
        {
            var fitContent = 0f;
            foreach (var itemText in itemTexts)
            {
                fitContent = MathF.Max(fitContent, ImGui.CalcTextSize(itemText).X);
            }
            var minWidth = CompactWidth;
            var maxWidth = MathF.Max(minWidth, ImGui.CalcItemWidth());
            return Math.Clamp(
                fitContent + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetFrameHeight()
                    + ImGui.GetStyle().ItemInnerSpacing.X,
                minWidth, maxWidth);
        }

        private sealed class MultiPopScope(int styleVars, int styleColors) : IDisposable
        {
            public void Dispose()
            {
                if (styleVars > 0)
                {
                    ImGui.PopStyleVar(styleVars);
                }
                if (styleColors > 0)
                {
                    ImGui.PopStyleColor(styleColors);
                }
            }
        }

        /// <summary>
        /// Pushes the value-control styling shared by the row wrappers: rounded
        /// frames, a knob-shaped accent grab, a subtle outline and accent-tinted
        /// hover/active states, plus rounded combo popups.
        /// </summary>
        public static IDisposable PushValueControlStyle()
        {
            var frameHeight = ImGui.GetFrameHeight();
            var accent = Colors.Accent;
            var frameBg = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.FrameBg));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Metrics.ControlRadius);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, Metrics.ControlRadius);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, frameHeight * Metrics.GrabMinSizeFactor);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Metrics.ControlBorderSize);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Metrics.PopupRounding);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, Metrics.PopupBorderSize);
            var border = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Border));
            ImGui.PushStyleColor(ImGuiCol.Border, border with { W = MathF.Max(border.W, 0.35f) });
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, accent with { W = 0.9f });
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, accent);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, accent);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Lerp(frameBg, accent, 0.12f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Lerp(frameBg, accent, 0.2f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, accent with { W = 0.25f });
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, accent with { W = 0.4f });
            return new MultiPopScope(6, 8);
        }

        /// <summary>
        /// Pushes the shared push-button styling: the header-button corner
        /// radius and frame border so every button · menus, pages, settings  · 
        /// matches the title-bar look. Colors stay ambient (the active
        /// theme's Button* slots), matching the header buttons.
        /// </summary>
        public static IDisposable PushButtonStyle()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Metrics.ButtonRadius);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Metrics.ButtonBorderSize);
            return new MultiPopScope(2, 0);
        }

        private static readonly ConcurrentDictionary<uint, float> _toggleAnimStates = new();
        private const float ToggleAnimDuration = 0.15f;

        /// <summary>Advances the slide-toggle animation (0 = off, 1 = on) for the given item id.</summary>
        public static float ToggleAnimValue(uint animKey, bool isOn)
        {
            var target = isOn ? 1f : 0f;
            var t = _toggleAnimStates.GetOrAdd(animKey, target);
            if (MathF.Abs(t - target) > 0.001f)
            {
                t = Math.Clamp(t + (target > t ? 1f : -1f) * ImGui.GetIO().DeltaTime / ToggleAnimDuration, 0f, 1f);
                _toggleAnimStates[animKey] = t;
            }
            return t;
        }

        /// <summary>Draws the slide-toggle track and knob into the given rect.</summary>
        public static void SwitchVisual(ImDrawListPtr drawList, Vector2 trackMin, Vector2 trackMax,
            float animT, float alpha = 1f)
        {
            var trackHeight = trackMax.Y - trackMin.Y;
            var trackWidth = trackMax.X - trackMin.X;
            var offTrack = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.FrameBg));
            offTrack.W *= alpha;
            drawList.AddRectFilled(trackMin, trackMax, ColorUtils.ToU32(offTrack),
                trackHeight * 0.5f, ImDrawFlags.RoundCornersAll);
            if (animT > 0.001f)
            {
                // On-state rides the accent gradient ramp, faded in by the
                // slide animation instead of cross-blending a flat color.
                var (gDeep, gHi) = Colors.AccentRamp(Colors.Accent);
                gDeep.W *= animT * alpha;
                gHi.W *= animT * alpha;
                Chrome.RoundedHorizontalGradient(drawList, trackMin, trackMax,
                    trackHeight * 0.5f, gDeep, gHi);
            }
            var knobRadius = trackHeight * 0.5f - 2f * SpheneUI.Scale;
            var knobX = trackMin.X + trackHeight * 0.5f + animT * (trackWidth - trackHeight);
            drawList.AddCircleFilled(new Vector2(knobX, trackMin.Y + trackHeight * 0.5f), knobRadius,
                ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.95f * alpha)));
        }

        /// <summary>
        /// Drop-in replacement for <c>ImGui.Checkbox</c> on settings rows:
        /// label left, animated slide toggle docked to the row's right bound.
        /// The whole row is clickable and tinted while hovered. A trailing icon
        /// column is reserved so a following <see cref="HelpText"/> marker still
        /// fits on the same line. Returns true when the value changed.
        /// </summary>
        /// <param name="icon">Leading icon drawn inside the row, in the label color.</param>
        /// <param name="labelColor">Optional override for the label/icon text color.</param>
        public static bool Toggle(string label, ref bool value, bool indented = false,
            float trailingWidth = 0f, string? menuPopupId = null,
            FontAwesomeIcon? icon = null, Vector4? labelColor = null)
        {
            var grouped = GroupState.RowStart();
            var idSplit = label.IndexOf("##", StringComparison.Ordinal);
            var text = idSplit < 0 ? label : label[..idSplit];
            var scale = SpheneUI.Scale;
            var cursor = ImGui.GetCursorScreenPos();
            var rightBound = grouped ? GroupState.CurrentRightBound : Layout.ContentRightBound;
            if (rightBound <= 0f)
            {
                rightBound = cursor.X + ImGui.GetContentRegionAvail().X;
            }
            var helpReserve = MathF.Max(Metrics.TrailingIconReserve, trailingWidth);
            var frameHeight = ImGui.GetFrameHeight();
            var rowEndX = rightBound - helpReserve;

            var trackHeight = MathF.Round(frameHeight * 0.7f);
            var trackWidth = trackHeight * 1.8f;
            var trackMin = new Vector2(rowEndX - trackWidth, cursor.Y + (frameHeight - trackHeight) * 0.5f);
            var trackMax = trackMin + new Vector2(trackWidth, trackHeight);

            var menuSize = menuPopupId != null ? frameHeight : 0f;
            var innerGap = ImGui.GetStyle().ItemInnerSpacing.X;
            var menuMin = new Vector2(trackMin.X - innerGap - menuSize, cursor.Y);
            var buttonEndX = menuPopupId != null ? menuMin.X - innerGap : rowEndX;
            var buttonWidth = MathF.Max(buttonEndX - cursor.X, frameHeight);

            var toggled = ImGui.InvisibleButton(label, new Vector2(buttonWidth, frameHeight));
            if (menuPopupId != null)
            {
                ImGui.SetItemAllowOverlap();
            }
            if (toggled)
            {
                value = !value;
            }
            var hovered = ImGui.IsItemHovered();
            var alpha = ImGui.GetStyle().Alpha;

            var animKey = ImGui.GetID(label);
            var t = ToggleAnimValue(animKey, value);

            var drawList = ImGui.GetWindowDrawList();
            if (hovered)
            {
                var tint = Colors.Accent with { W = 0.07f * alpha };
                drawList.AddRectFilled(
                    new Vector2(cursor.X - 2f * scale, cursor.Y),
                    new Vector2(rightBound, cursor.Y + frameHeight),
                    ColorUtils.ToU32(tint), 4f * scale, ImDrawFlags.RoundCornersAll);
            }

            if (menuPopupId != null)
            {
                ImGui.SetCursorScreenPos(menuMin);
                var menuClicked = ImGui.InvisibleButton(label + "##menu", new Vector2(menuSize, frameHeight));
                var menuHovered = ImGui.IsItemHovered();
                if (menuHovered)
                {
                    var menuTint = Colors.Accent with { W = 0.16f * alpha };
                    drawList.AddRectFilled(menuMin, menuMin + new Vector2(menuSize, frameHeight),
                        ColorUtils.ToU32(menuTint), 4f * scale, ImDrawFlags.RoundCornersAll);
                }
                var dotRadius = MathF.Max(1f, 1.5f * scale);
                var dotSpacing = menuSize * 0.22f;
                var dotX = menuMin.X + menuSize * 0.5f;
                var dotY = menuMin.Y + frameHeight * 0.5f;
                var dotColor = ImGui.GetColorU32(menuHovered ? ImGuiCol.Text : ImGuiCol.TextDisabled);
                for (var i = -1; i <= 1; i++)
                {
                    drawList.AddCircleFilled(new Vector2(dotX, dotY + i * dotSpacing), dotRadius, dotColor);
                }
                if (menuClicked)
                {
                    ImGui.OpenPopup(menuPopupId);
                }
                SpheneUI.AttachTooltip("More options");
            }

            SwitchVisual(drawList, trackMin, trackMax, t, alpha);

            if (text.Length > 0 || icon.HasValue)
            {
                ImGui.SetCursorScreenPos(cursor);
                ImGui.AlignTextToFramePadding();
                var textColor = labelColor
                    ?? (indented ? ImGuiColors.DalamudGrey3 : ImGuiColors.DalamudGrey);
                if (icon.HasValue)
                {
                    Icons.Icon(icon.Value, textColor);
                    ImGui.SameLine();
                }
                if (text.Length > 0)
                {
                    Text.Colored(indented ? "  " + text : text, textColor);
                }
            }

            Lint.CheckBounds(label);
            return toggled;
        }

        /// <summary>
        /// Bare slide switch without the label row · the same animated track
        /// <see cref="Toggle"/> docks to the bound, sized to the control frame.
        /// Use inside composite rows (trailing icon columns, table cells,
        /// custom list rows) that place the switch themselves; for the
        /// standard label-left/switch-right settings row use
        /// <see cref="Toggle"/>.
        /// </summary>
        /// <returns>True when the switch was clicked.</returns>
        public static bool Switch(string id, ref bool value)
        {
            var height = ImGui.GetFrameHeight();
            var width = height * 1.8f;
            var position = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
            if (clicked)
            {
                value = !value;
            }
            // Same proportions as Toggle: the track is drawn at 70% of the
            // frame height, centered inside the full-height hit box.
            var trackHeight = MathF.Round(height * 0.7f);
            var trackMin = new Vector2(position.X + (width - trackHeight * 1.8f) * 0.5f,
                position.Y + (height - trackHeight) * 0.5f);
            var animT = ToggleAnimValue(ImGui.GetID(id), value);
            SwitchVisual(ImGui.GetWindowDrawList(), trackMin,
                trackMin + new Vector2(trackHeight * 1.8f, trackHeight), animT, ImGui.GetStyle().Alpha);
            Lint.CheckBounds(id);
            return clicked;
        }

        /// <summary>SliderInt with the settings-row convention (label left, control sized to the bound).</summary>
        public static bool SliderInt(string label, ref int value, int min, int max, string? format = null,
            float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.SliderInt(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, min, max, format ?? "%d");
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>SliderFloat with the settings-row convention (label left, control sized to the bound).</summary>
        public static bool SliderFloat(string label, ref float value, float min, float max, string? format = null,
            float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.SliderFloat(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, min, max, format ?? "%.3f");
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>SliderFloat2 with the settings-row convention (label left, control sized to the bound).</summary>
        public static bool SliderFloat2(string label, ref Vector2 value, float min, float max, string? format = null,
            float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.SliderFloat2(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, min, max, format ?? "%.3f");
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>DragFloat with the settings-row convention (label left, compact numeric tier).</summary>
        public static bool DragFloat(string label, ref float value, float speed, float min, float max,
            string? format = null, float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.DragFloat(PrepareLabeledControl(label, trailingWidth,
                preferredWidth > 0f ? preferredWidth : CompactWidth), ref value, speed, min, max, format ?? "%.3f");
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>DragFloat2 with the settings-row convention (label left, compact numeric tier).</summary>
        public static bool DragFloat2(string label, ref Vector2 value, float speed, float min, float max,
            string? format = null, float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.DragFloat2(PrepareLabeledControl(label, trailingWidth,
                preferredWidth > 0f ? preferredWidth : CompactWidth), ref value, speed, min, max, format ?? "%.3f");
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>InputInt with the settings-row convention (label left, compact numeric tier).</summary>
        public static bool InputInt(string label, ref int value, int step = 1, int stepFast = 100,
            float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.InputInt(PrepareLabeledControl(label, trailingWidth,
                preferredWidth > 0f ? preferredWidth : CompactWidth), ref value, step, stepFast);
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>InputText with the settings-row convention (label left, control sized to the bound).</summary>
        public static bool InputText(string label, ref string value, int maxLength,
            ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, float trailingWidth = 0f,
            float preferredWidth = -1f, bool dockRight = false)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.InputText(PrepareLabeledControl(label, trailingWidth, preferredWidth, dockRight), ref value, maxLength, flags);
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>InputTextWithHint with the settings-row convention (label left, control sized to the bound).</summary>
        public static bool InputTextWithHint(string label, string hint, ref string value, int maxLength,
            ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.InputTextWithHint(PrepareLabeledControl(label, trailingWidth, preferredWidth), hint, ref value, maxLength, flags);
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>
        /// Callback-capable <see cref="InputTextWithHint"/> · same contract as
        /// the multiline overload: with <see cref="ImGuiInputTextFlags.CallbackAlways"/>
        /// the callback fires each frame the widget is active, which is how
        /// editors splice text at the live caret (UTF-8 byte offsets).
        /// </summary>
        public static bool InputTextWithHint<TContext>(string label, string hint, ref string value,
            int maxLength, ImGuiInputTextFlags flags,
            ImGui.ImGuiInputTextCallbackRefContextDelegate<TContext> callback,
            ref TContext context, float trailingWidth = 0f, float preferredWidth = -1f,
            bool emojiOverlay = false)
        {
            using var style = PushValueControlStyle();
            // With emoji present the font can only render fallback glyphs -
            // hide the native text and let Emoji.InputOverlay draw the whole
            // visible line (text runs, atlas sprites, mirrored caret) instead.
            var queue = emojiOverlay && context is Helpers.Emoji.EmojiEditQueue q ? q : null;
            var codec = queue?.Codec;
            // The native edit buffer holds 16-bit ImWchar - astral emoji die
            // in the TextW→TextA round-trip. The buffer view carries one BMP
            // placeholder per emoji; the model string keeps real sequences.
            var view = codec?.Encode(value) ?? value;
            var hideText = queue != null && Emoji.ContainsEmoji(value);
            if (hideText)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
            }
            var changed = ImGui.InputTextWithHint(PrepareLabeledControl(label, trailingWidth, preferredWidth),
                hint, ref view, maxLength, flags, callback, ref context);
            if (codec != null)
            {
                // sync the model only - changed keeps the widget's own
                // semantics (Enter for EnterReturnsTrue callers, else edit)
                var model = codec.Expand(view);
                if (!string.Equals(model, value, StringComparison.Ordinal))
                {
                    value = model;
                }
            }
            if (hideText)
            {
                ImGui.PopStyleColor();
                Emoji.InputOverlay(view, queue!);
            }
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>InputTextMultiline with the settings-row convention (label left, control sized to the bound).</summary>
        public static bool InputTextMultiline(string label, ref string value, int maxLength, Vector2 size,
            ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.InputTextMultiline(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, maxLength, size, flags);
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>
        /// Callback-capable <see cref="InputTextMultiline"/> · the context is
        /// handed to <paramref name="callback"/> every time ImGui invokes it
        /// (with <see cref="ImGuiInputTextFlags.CallbackAlways"/> this is each
        /// frame while the widget is active). Callback positions are UTF-8
        /// byte offsets into the buffer - editors use them to splice markup
        /// at the caret or around the selection via
        /// <c>InsertChars</c>/<c>DeleteChars</c> on the callback data.
        /// </summary>
        public static bool InputTextMultiline<TContext>(string label, ref string value,
            int maxLength, Vector2 size, ImGuiInputTextFlags flags,
            ImGui.ImGuiInputTextCallbackRefContextDelegate<TContext> callback,
            ref TContext context,
            float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            // same 16-bit ImWchar boundary as the single-line overload:
            // emoji placeholders ride the buffer, the model keeps real text.
            var codec = context is Helpers.Emoji.EmojiEditQueue q ? q.Codec : null;
            var view = codec?.Encode(value) ?? value;
            var changed = ImGui.InputTextMultiline(PrepareLabeledControl(label, trailingWidth, preferredWidth),
                ref view, maxLength, size, flags, callback, ref context);
            if (codec != null)
            {
                var model = codec.Expand(view);
                if (!string.Equals(model, value, StringComparison.Ordinal))
                {
                    value = model;
                }
            }
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>
        /// Private-chat composer field: a borderless multiline input meant
        /// to sit inside a caller-drawn composite frame. ImGui's multiline
        /// only breaks on hard newlines, so the buffer gets real '\n'
        /// characters injected at the measured wrap points and they are
        /// verified/stripped back out when the buffer returns - wrapping,
        /// scrolling and caret/selection behavior all stay native.
        /// Emoji-containing models hide the native text and get the
        /// per-line overlay. Wrap width mirrors the widget's inner child:
        /// <paramref name="size"/>.X minus window padding (and the
        /// scrollbar once the content scrolls).
        /// </summary>
        public static bool InputComposerMultiline(string label, ref string value,
            int maxLength, Vector2 size,
            ImGui.ImGuiInputTextCallbackRefContextDelegate<Helpers.Emoji.EmojiEditQueue> callback,
            ref Helpers.Emoji.EmojiEditQueue context)
        {
            using var style = PushValueControlStyle();
            var codec = context.Codec;
            var view = codec.Encode(value);
            // the multiline's inner child insets text by FramePadding -
            // wrap against that width or the injected breaks disagree with
            // the widget's own layout
            var wrapInnerWidth = MathF.Max(40f,
                size.X - ImGui.GetStyle().FramePadding.X * 2f);
            var (buf, soft) = Helpers.Emoji.ChatComposerWrap.Wrap(view, wrapInnerWidth,
                s => ImGui.CalcTextSize(s).X);
            if (Helpers.Emoji.ChatComposerWrap.Lines(buf) > 3)
            {
                // the field caps at 3 visible lines - past that the native
                // scrollbar steals inner width, so wrap against the
                // reduced width or text slides under it
                (buf, soft) = Helpers.Emoji.ChatComposerWrap.Wrap(view,
                    MathF.Max(40f, wrapInnerWidth - ImGui.GetStyle().ScrollbarSize),
                    s => ImGui.CalcTextSize(s).X);
            }
            if (!soft.SequenceEqual(context.WrapSoft) && context.CaretCharOffset >= 0)
            {
                context.WrapCaretTarget = Helpers.Emoji.ChatComposerWrap.RemapCaret(
                    context.CaretCharOffset, context.WrapSoft, soft);
            }
            context.WrapSoft = soft;
            var submitted = buf;
            var hideText = Emoji.ContainsEmoji(value);
            // the composite owns the frame - the field's own visuals stay off
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
            if (hideText)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
            }
            var changed = ImGui.InputTextMultiline(label, ref buf,
                maxLength + soft.Count, size,
                ImGuiInputTextFlags.CallbackAlways, callback, ref context);
            if (hideText)
            {
                ImGui.PopStyleColor();
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);
            var restored = Helpers.Emoji.ChatComposerWrap.Unwrap(submitted, buf, soft);
            var model = codec.Expand(restored);
            if (!string.Equals(model, value, StringComparison.Ordinal))
            {
                value = model;
            }
            if (hideText)
            {
                Emoji.InputOverlayLines(buf, context);
            }
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>
        /// One action cap fused to the right edge of an input · an icon glyph,
        /// or <paramref name="Label"/> for labeled caps (e.g. "Browse").
        /// </summary>
        public readonly record struct InputButtonSpec(FontAwesomeIcon Icon = FontAwesomeIcon.None,
            string? Tooltip = null, bool Enabled = true, string? Label = null);

        /// <summary>
        /// Text input with action buttons fused into its right edge: one
        /// rounded frame encloses the field and every cap (input-group look).
        /// The composite keeps the control width a plain <see cref="InputText"/>
        /// would occupy, so the buttons live inside the field's footprint.
        /// Returns whether the text changed and which cap was pressed (-1 = none).
        /// </summary>
        public static (bool Changed, int PressedButton) InputTextWithButtons(
            string label, ref string value, int maxLength,
            ImGuiInputTextFlags flags = ImGuiInputTextFlags.None,
            float trailingWidth = 0f, float preferredWidth = -1f,
            params InputButtonSpec[] buttons)
        {
            var scale = SpheneUI.Scale;
            var id = PrepareLabeledControl(label, trailingWidth, preferredWidth,
                dockRight: true, out var controlWidth);
            var height = ImGui.GetFrameHeight();
            var framePadX = ImGui.GetStyle().FramePadding.X;

            var capWidths = new float[buttons.Length];
            var capsWidth = 0f;
            for (var i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                capWidths[i] = b.Label is { Length: > 0 } t
                    ? ImGui.CalcTextSize(t).X + framePadX * 2f
                    : height;
                capsWidth += capWidths[i];
            }
            if (controlWidth <= 0f)
            {
                controlWidth = MathF.Min(ImGui.CalcItemWidth(), BoundedAvailX());
            }
            var fieldWidth = MathF.Max(60f * scale, controlWidth - capsWidth);

            var pos = ImGui.GetCursorScreenPos();
            var radius = Metrics.ControlRadius;
            var drawList = ImGui.GetWindowDrawList();
            var size = new Vector2(controlWidth, height);

            drawList.AddRectFilled(pos, pos + size, ColorUtils.ToU32(Colors.FrameBg), radius);

            // The field renders with a stripped frame so the composite frame
            // stays the only visible border.
            ImGui.SetNextItemWidth(fieldWidth);
            bool fieldChanged;
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero)
                .Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
                .Push(ImGuiCol.FrameBgActive, Vector4.Zero))
            {
                fieldChanged = ImGui.InputText(id, ref value, maxLength, flags);
            }
            var fieldActive = ImGui.IsItemActive();

            var pressedButton = -1;
            var anyCapHovered = false;
            var cursorX = pos.X + fieldWidth;
            for (var i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                var capPos = new Vector2(cursorX, pos.Y);
                var capSize = new Vector2(capWidths[i], height);
                var isLast = i == buttons.Length - 1;
                cursorX += capWidths[i];

                bool capPressed;
                ImGui.SetCursorScreenPos(capPos);
                if (b.Enabled)
                {
                    capPressed = ImGui.InvisibleButton($"##cap{i}{id}", capSize);
                }
                else
                {
                    ImGui.Dummy(capSize);
                    capPressed = false;
                }
                var capHovered = ImGui.IsItemHovered();
                if (capPressed)
                {
                    pressedButton = i;
                }
                if (b.Tooltip != null)
                {
                    SpheneUI.AttachTooltip(b.Tooltip);
                }
                anyCapHovered |= capHovered && b.Enabled;

                var capBg = !b.Enabled
                    ? Vector4.Zero
                    : capHovered
                        ? Colors.FrameBgHover
                        : new Vector4(1f, 1f, 1f, 0.05f);
                if (capBg.W > 0f)
                {
                    drawList.AddRectFilled(capPos + new Vector2(0f, 1f),
                        capPos + capSize - new Vector2(0f, 1f),
                        ColorUtils.ToU32(capBg), isLast ? radius : 0f,
                        isLast ? ImDrawFlags.RoundCornersRight : ImDrawFlags.None);
                }
                drawList.AddLine(new Vector2(capPos.X, pos.Y + 3f * scale),
                    new Vector2(capPos.X, pos.Y + height - 3f * scale),
                    ColorUtils.ToU32(Colors.Border with { W = 0.3f }),
                    MathF.Max(1f, scale));

                var glyphColor = !b.Enabled
                    ? Colors.TextMuted with { W = 0.45f }
                    : capHovered ? Colors.Accent : Colors.TextSecondary;
                var glyphColorU32 = ColorUtils.ToU32(glyphColor);
                if (b.Label is { Length: > 0 } capText)
                {
                    var gs = ImGui.CalcTextSize(capText);
                    drawList.AddText(capPos + (capSize - gs) * 0.5f, glyphColorU32, capText);
                }
                else
                {
                    var glyph = b.Icon.ToIconString();
                    using (PushIconFont())
                    {
                        var gs = ImGui.CalcTextSize(glyph);
                        drawList.AddText(capPos + (capSize - gs) * 0.5f, glyphColorU32, glyph);
                    }
                }
            }

            var borderColor = fieldActive || anyCapHovered
                ? Colors.Accent with { W = 0.75f }
                : Colors.Border with { W = 0.28f };
            drawList.AddRect(pos, pos + size, ColorUtils.ToU32(borderColor),
                radius, ImDrawFlags.None, MathF.Max(1f, scale));

            Lint.CheckBounds(label);
            return (fieldChanged, pressedButton);
        }

        private static IDisposable PushIconFont()
            => IconFont?.Push() ?? new EmptyScope();

        private sealed class EmptyScope : IDisposable
        {
            public void Dispose() { }
        }

        /// <summary>Combo (index) with the settings-row convention.</summary>
        public static bool LabeledCombo(string label, ref int currentItem, string[] items, int itemsCount,
            float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.Combo(PrepareLabeledControl(label, trailingWidth,
                preferredWidth > 0f ? preferredWidth : ComboFitWidth(items.Take(itemsCount)),
                dockRight: preferredWidth <= 0f), ref currentItem, items, itemsCount);
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>Combo (\0-separated items) with the settings-row convention.</summary>
        public static bool LabeledCombo(string label, ref int currentItem, string itemsSeparatedByZeros,
            int popupMaxHeightInItems = -1, float trailingWidth = 0f, float preferredWidth = -1f)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.Combo(PrepareLabeledControl(label, trailingWidth,
                preferredWidth > 0f ? preferredWidth : ComboFitWidth(itemsSeparatedByZeros.Split('\0', StringSplitOptions.RemoveEmptyEntries)),
                dockRight: preferredWidth <= 0f), ref currentItem, itemsSeparatedByZeros,
                popupMaxHeightInItems);
            Lint.CheckBounds(label);
            return changed;
        }

        private static readonly Dictionary<string, object?> _selectedComboItems = new(StringComparer.Ordinal);

        /// <summary>
        /// Typed combo with the settings-row convention: remembers the selected
        /// item per <paramref name="comboName"/> and reports it back.
        /// </summary>
        public static T? Combo<T>(string comboName, IEnumerable<T> comboItems, Func<T?, string> toName,
            Action<T?>? onSelected = null, T? initialSelectedItem = default, float trailingWidth = 0f,
            float preferredWidth = -1f)
        {
            if (!comboItems.Any())
            {
                return default;
            }

            if (!_selectedComboItems.TryGetValue(comboName, out var selectedItem) && selectedItem == null)
            {
                selectedItem = initialSelectedItem;
                _selectedComboItems[comboName] = selectedItem;
            }

            var autoDock = preferredWidth <= 0f;
            if (preferredWidth <= 0f)
            {
                preferredWidth = ComboFitWidth(comboItems.Select(toName));
            }
            var comboId = PrepareLabeledControl(comboName, trailingWidth, preferredWidth,
                dockRight: autoDock);

            using var style = PushValueControlStyle();
            if (ImGui.BeginCombo(comboId, selectedItem == null ? "Unset Value" : toName((T?)selectedItem)))
            {
                foreach (var item in comboItems)
                {
                    var isSelected = EqualityComparer<T>.Default.Equals(item, (T?)selectedItem);
                    if (ImGui.Selectable(toName(item), isSelected))
                    {
                        _selectedComboItems[comboName] = item!;
                        onSelected?.Invoke(item!);
                    }
                }

                ImGui.EndCombo();
            }

            Lint.CheckBounds(comboName);
            return (T?)_selectedComboItems[comboName];
        }

        private static readonly Stack<IDisposable> _comboStyleStack = new();

        /// <summary>
        /// <see cref="ImGui.BeginCombo"/> wrapped in the value-control style, so
        /// pickers whose items don't fit <see cref="Combo{T}"/> (icons,
        /// mixed types, custom rows) still get the themed frame and popup
        /// chrome. Call <see cref="EndComboBox"/> only when this returns true;
        /// populate with <see cref="Selectable"/> items.
        /// </summary>
        /// <param name="width">Item width in pixels; ≤ 0 keeps ImGui's default
        /// width. Pass <see cref="FillRowWidth"/> results for fill rows.</param>
        /// <param name="flags">Combo flags, e.g. <see cref="ImGuiComboFlags.NoPreview"/>
        /// for arrow-only dropdown buttons.</param>
        public static bool BeginComboBox(string id, string previewLabel, float width = -1f,
            ImGuiComboFlags flags = ImGuiComboFlags.None)
        {
            if (width > 0f)
            {
                ImGui.SetNextItemWidth(width);
            }
            var style = PushValueControlStyle();
            if (!ImGui.BeginCombo(id, previewLabel, flags))
            {
                style.Dispose();
                return false;
            }
            _comboStyleStack.Push(style);
            Lint.CheckBounds(id);
            return true;
        }

        /// <summary>Ends a combo opened via <see cref="BeginComboBox"/>.</summary>
        public static void EndComboBox()
        {
            ImGui.EndCombo();
            if (_comboStyleStack.Count > 0)
            {
                _comboStyleStack.Pop().Dispose();
            }
        }

        /// <summary>
        /// Renders a comma-separated tag list as rounded accent chips, wrapping
        /// to the next line when they exceed the available width. Empty input
        /// renders nothing. Capped at <paramref name="maxChips"/> chips.
        /// </summary>
        public static void TagChips(string? tags, Vector4? accent = null, int maxChips = 10)
        {
            if (string.IsNullOrWhiteSpace(tags))
            {
                return;
            }
            var scale = SpheneUI.Scale;
            var chipAccent = accent ?? SpheneTheme.Current.AccentCyan;
            var drawList = ImGui.GetWindowDrawList();
            var avail = ImGui.GetContentRegionAvail().X;
            var padX = 7f * scale;
            var padY = 2f * scale;
            var chipH = ImGui.GetTextLineHeight() + padY * 2f;
            var startX = ImGui.GetCursorScreenPos().X;
            var startY = ImGui.GetCursorScreenPos().Y;
            var xMax = startX + avail;
            var x = startX;
            var y = startY;
            var shown = 0;
            foreach (var raw in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (shown >= maxChips)
                {
                    break;
                }
                var chipW = ImGui.CalcTextSize(raw).X + padX * 2f;
                if (x + chipW > xMax)
                {
                    x = startX;
                    y += chipH + 4f * scale;
                }
                var chipMin = new Vector2(x, y);
                var chipMax = chipMin + new Vector2(chipW, chipH);
                drawList.AddRectFilled(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(chipAccent, 0.14f)), chipH * 0.5f);
                drawList.AddRect(chipMin, chipMax,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(chipAccent, 0.35f)), chipH * 0.5f);
                drawList.AddText(chipMin + new Vector2(padX, padY),
                    ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), raw);
                x = chipMax.X + 6f * scale;
                shown++;
            }
            ImGui.SetCursorScreenPos(new Vector2(startX, startY));
            ImGui.Dummy(new Vector2(avail, y + chipH - startY));
        }

        /// <summary>
        /// Draws a ? help marker in the reserved right column of the current
        /// row (same line as the previous item when it shares the line,
        /// right-aligned on the next line otherwise) so all help icons share
        /// one flush column. Never climbs back onto a finished line.
        /// </summary>
        public static void HelpText(string helpText)
        {
            var iconW = Icons.Size(FontAwesomeIcon.QuestionCircle).X;
            var cursor = ImGui.GetCursorScreenPos();
            var bound = Layout.ContentRightBound;
            if (bound <= 0f)
            {
                bound = cursor.X + ImGui.GetContentRegionAvail().X;
            }
            var targetX = bound - iconW;
            var lastMin = ImGui.GetItemRectMin();
            var lastMax = ImGui.GetItemRectMax();
            // Join the previous line only when the last item is real content  · 
            // zero-width spacers (VerticalSpace, ScaledDummy, trailing chip
            // gaps) must not pull the marker back up next to titles or rows
            // that already ended.
            var joinable = lastMax.X - lastMin.X > 2f;
            if (joinable && targetX > lastMax.X + 1f)
            {
                // Pin to the reserved right column so every help icon sits
                // flush behind the row's control or switch.
                ImGui.SameLine(0f, targetX - lastMax.X);
            }
            else
            {
                // Spacer above or no room left: dock to the reserved column
                // on its own line instead of climbing back or overlapping.
                ImGui.SetCursorScreenPos(new Vector2(
                    MathF.Max(cursor.X, targetX), cursor.Y));
                if (GroupState.InGroup)
                {
                    Lint.RecordDetachedHelp(helpText);
                }
            }
            // The glyph is painted through the draw list, centered inside the
            // row's frame box · IconText would render at the line's text base
            // offset, which differs per row type. A Dummy registers the frame
            // box as the item so hover and the tooltip cover exactly the glyph.
            var markerPos = ImGui.GetCursorScreenPos();
            var frameH = Metrics.ControlHeight;
            var glyph = FontAwesomeIcon.QuestionCircle.ToIconString();
            using (PushIconFont())
            {
                var glyphSize = ImGui.CalcTextSize(glyph);
                ImGui.GetWindowDrawList().AddText(new Vector2(markerPos.X,
                    markerPos.Y + (frameH - glyphSize.Y) * 0.5f),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled), glyph);
            }
            ImGui.Dummy(new Vector2(iconW, frameH));
            SpheneUI.AttachTooltip(helpText);
        }

        /// <summary>
        /// Color picker in the settings-row convention: muted label left, a
        /// right-docked <see cref="ColorSwatch"/> that opens a
        /// <see cref="Popups.BeginStyled"/> popup hosting ImGui's full
        /// ColorPicker4.
        /// </summary>
        public static bool ColorEdit(string label, ref Vector4 color, float trailingWidth = 0f)
        {
            var swatchSize = Metrics.SwatchSize;
            var id = PrepareLabeledControl(label, trailingWidth, swatchSize, dockRight: true);
            var pickerPopupId = id + "_picker";
            if (ColorSwatch(id, color))
            {
                ImGui.OpenPopup(pickerPopupId);
            }
            SpheneUI.AttachTooltip("Click to pick a color.");
            var changed = false;
            if (Popups.BeginStyled(pickerPopupId))
            {
                changed = ImGui.ColorPicker4("##picker" + id, ref color,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoLabel);
                Popups.EndStyled();
            }
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>
        /// ImGui's ColorPicker4 (alpha bar, no label) meant to be hosted inside
        /// a caller-managed <see cref="Popups.BeginStyled"/> popup or styled
        /// child · the raw widget behind <see cref="ColorEdit"/> for custom
        /// picker popups that need more than a swatch row.
        /// </summary>
        public static bool ColorPicker(string id, ref Vector4 color)
        {
            var changed = ImGui.ColorPicker4(id, ref color,
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoLabel);
            Lint.CheckBounds(id);
            return changed;
        }

        /// <summary>
        /// Label-less swatch that opens a <see cref="Popups.BeginStyled"/>
        /// ColorPicker popup on click · the toolbar counterpart of
        /// <see cref="ColorEdit"/> for inline/icon rows. Use
        /// <paramref name="selected"/> to mark the swatch as the active state.
        /// </summary>
        /// <returns>True while the picker reports a change.</returns>
        public static bool ColorPickerSwatch(string id, ref Vector4 color, bool selected = false)
        {
            var pickerPopupId = id + "_picker";
            if (ColorSwatch(id, color, selected: selected))
            {
                ImGui.OpenPopup(pickerPopupId);
            }
            var changed = false;
            if (Popups.BeginStyled(pickerPopupId))
            {
                changed = ColorPicker("##picker" + id, ref color);
                Popups.EndStyled();
            }
            Lint.CheckBounds(id);
            return changed;
        }

        /// <summary>Gap between the icon zone's seam and the label.</summary>
        private const float SegTextPadX = 10f;

        /// <summary>Trailing padding after the label.</summary>
        private const float SegTrailPadX = 12f;

        /// <summary>Idle pill fill (white alpha) · deliberately brighter than
        /// the surrounding band so push buttons read as clickable.</summary>
        private const float SegPillIdle = 0.075f;
        private const float SegPillHover = 0.13f;
        private const float SegPillHeld = 0.17f;
        private const float SegPillActive = 0.18f;

        /// <summary>Icon zone fill · always a step brighter than the pill.</summary>
        private const float SegZoneIdle = 0.13f;
        private const float SegZoneHover = 0.27f;
        private const float SegZoneHeld = 0.38f;
        private const float SegZoneActive = 0.92f;

        /// <summary>Hairline seam between zone and label.</summary>
        private const float SegSeamIdle = 0.13f;
        private const float SegSeamActive = 0.55f;

        /// <summary>Opaque theme base drawn under the tint layers so busy
        /// backdrops (banner art, images) cannot bleed through · the frosted
        /// look without paying for a per-rect blur pass.</summary>
        private const float SegScrimAlpha = 0.66f;

        /// <summary>How far the scrim is pulled toward near-black · keeps
        /// button bodies clearly darker than the band they sit on.</summary>
        private const float SegScrimDarken = 0.5f;

        /// <summary>Hairline around the pill · idle keeps it quiet, states
        /// brighten it for definition without an outlined-box look.</summary>
        private const float SegBorderIdle = 0.30f;
        private const float SegBorderHover = 0.50f;
        private const float SegBorderHeld = 0.58f;
        private const float SegBorderActive = 0.55f;

        /// <summary>Max corner radius relative to button height · keeps
        /// short buttons reading as buttons instead of full pills.</summary>
        private const float SegRadiusFactor = 0.25f;

        /// <summary>
        /// Auto-fit width for the segmented button: square zone (or square
        /// button for icon-only) plus label padding, matching
        /// <see cref="SegCore"/>'s draw math.
        /// </summary>
        internal static float SegAutoWidth(string? label, FontAwesomeIcon? icon, float height)
        {
            var scale = SpheneUI.Scale;
            var display = StripLabelId(label);
            if (string.IsNullOrEmpty(display))
            {
                return icon.HasValue ? height : SegTextPadX * 2f * scale;
            }
            var textW = ImGui.CalcTextSize(display).X;
            return height + SegTextPadX * scale + textW + SegTrailPadX * scale;
        }

        /// <summary>Drops the ImGui "##id" suffix so only the visible label
        /// part is measured and drawn.</summary>
        private static string? StripLabelId(string? label)
        {
            if (label == null)
            {
                return null;
            }
            var idx = label.IndexOf("##", StringComparison.Ordinal);
            return idx >= 0 ? label[..idx] : label;
        }

        /// <summary>
        /// Shared renderer for the segmented button family · the same visual
        /// language as <c>Layout.NavRailSegmentItem</c>: a tonal pill with a
        /// square icon zone on the left separated by a hairline seam.
        /// Modes: icon + label → segmented button, icon only → square zone
        /// button, label only → zone with a subdued default chevron. Idle is
        /// a quiet gray,
        /// hover lifts toward the accent, <paramref name="active"/> is an
        /// optional pressed/toggled look (content buttons usually stay off).
        /// </summary>
        internal static bool SegCore(string id, string? label, FontAwesomeIcon? icon,
            Vector2? size, Vector4 accent, bool active = false, bool enabled = true,
            float iconScale = 1f,
            Action<ImDrawListPtr, Vector2, Vector2>? zoneContent = null,
            bool solidFill = false, Vector4? labelColor = null)
        {
            var scale = SpheneUI.Scale;
            var display = StripLabelId(label);
            var hasLabel = !string.IsNullOrEmpty(display);
            // Every labeled button carries a zone · the semantic icon when
            // given, otherwise a subdued default chevron marking it as an
            // actionable button. zoneContent swaps the glyph for caller-drawn
            // content (e.g. a brand texture glyph).
            var glyph = icon?.ToIconString()
                ?? (hasLabel ? FontAwesomeIcon.ChevronRight.ToIconString() : null);
            var hasZone = glyph != null || zoneContent != null;
            var isDefaultGlyph = !icon.HasValue && hasLabel;
            var h = size?.Y is > 0f ? size.Value.Y : ImGui.GetFrameHeight();
            var w = size?.X is > 0f or < 0f ? size.Value.X : SegAutoWidth(display, icon, h);
            // Fixed FrameRounding turns short buttons into pills · cap it
            // relative to the height so proportions stay button-like at
            // any control size.
            var radius = MathF.Min(Metrics.ButtonRadius, h * SegRadiusFactor);

            ImGui.PushID(id);
            bool clicked;
            using (ImRaii.Disabled(!enabled))
            {
                clicked = ImGui.InvisibleButton("##segbtn", new Vector2(w, h));
            }
            var hovered = enabled && ImGui.IsItemHovered();
            var held = enabled && ImGui.IsItemActive();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.PopID();

            var drawList = ImGui.GetWindowDrawList();
            var dim = enabled ? 1f : 0.45f;
            var scrim = ColorUtils.LerpColor(Colors.FrameBg,
                new Vector4(0.02f, 0.02f, 0.035f, 1f), SegScrimDarken);
            scrim.W = MathF.Max(scrim.W, SegScrimAlpha) * dim;
            drawList.AddRectFilled(min, max, ColorUtils.ToU32(scrim), radius);
            // solidFill: the whole pill carries the accent as a two-stop
            // gradient ramp plus a top sheen and a soft ambient glow (the
            // primary-action look) · hover/held only brighten the ramp
            // instead of tinting a neutral fill.
            if (solidFill)
            {
                var (gDeep, gHi) = Colors.AccentRamp(accent);
                var fillAlpha = (held ? 1f : hovered ? 0.97f : 0.90f) * dim;
                gDeep.W = fillAlpha;
                gHi.W = fillAlpha;
                Fx.AmbientGlow(drawList, min, max, radius, accent,
                    (hovered ? 1.4f : 0.8f) * dim);
                Chrome.RoundedHorizontalGradient(drawList, min, max, radius, gDeep, gHi);
                var sheen = ColorUtils.ToU32(ColorUtils.WithAlpha(gHi, 0.30f * fillAlpha));
                // Fade in from the left cap tangent - a hard edge inside the
                // corner arc would nick the pill silhouette.
                var sheenIn = MathF.Min(10f * scale, (w - radius * 1.2f) * 0.35f);
                drawList.AddRectFilledMultiColor(
                    new Vector2(min.X + radius * 0.6f, min.Y + 1f * scale),
                    new Vector2(min.X + radius * 0.6f + sheenIn, min.Y + h * 0.34f),
                    0u, sheen, 0u, 0u);
                drawList.AddRectFilledMultiColor(
                    new Vector2(min.X + radius * 0.6f + sheenIn, min.Y + 1f * scale),
                    new Vector2(max.X - radius * 0.6f, min.Y + h * 0.34f),
                    sheen, sheen, 0u, 0u);
            }
            else
            {
                var pillFill = active ? ColorUtils.WithAlpha(accent, SegPillActive)
                    : held ? ColorUtils.WithAlpha(accent, SegPillHeld)
                    : hovered ? ColorUtils.WithAlpha(accent, SegPillHover)
                    : ColorUtils.WithAlpha(Vector4.One, SegPillIdle);
                pillFill.W *= dim;
                drawList.AddRectFilled(min, max, ColorUtils.ToU32(pillFill), radius);
            }

            // Press feedback: held buttons dip their contents by a pixel and
            // pick up a faint top inner shadow · reads as a physical push
            // rather than a flat tint swap.
            var pressShift = held ? scale : 0f;
            if (held)
            {
                drawList.AddRectFilled(min, new Vector2(max.X, min.Y + 1.5f * scale),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.Zero, 0.18f)),
                    radius, ImDrawFlags.RoundCornersTop);
            }

            var zoneW = hasZone && hasLabel ? h : 0f;
            if (hasZone)
            {
                var zoneMax = hasLabel ? new Vector2(min.X + zoneW, max.Y) : max;
                var zoneFill = solidFill
                    ? ColorUtils.WithAlpha(ColorUtils.LerpColor(accent, Vector4.Zero,
                        hovered ? 0.10f : 0.20f), 0.95f)
                    : active ? ColorUtils.WithAlpha(accent, SegZoneActive)
                    : held ? ColorUtils.WithAlpha(accent, SegZoneHeld)
                    : hovered ? ColorUtils.WithAlpha(accent, SegZoneHover)
                    : ColorUtils.WithAlpha(Vector4.One, SegZoneIdle);
                zoneFill.W *= dim;
                drawList.AddRectFilled(min, zoneMax, ColorUtils.ToU32(zoneFill),
                    radius, hasLabel ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersAll);
                if (hasLabel)
                {
                    var seamBase = active ? ColorUtils.LerpColor(accent, Vector4.One, 0.5f) : Vector4.One;
                    var seamCol = ImGui.ColorConvertFloat4ToU32(
                        ColorUtils.WithAlpha(seamBase, (active ? SegSeamActive : SegSeamIdle) * dim));
                    var seamInset = h * 0.22f;
                    drawList.AddLine(new Vector2(zoneMax.X - 0.5f, min.Y + seamInset),
                        new Vector2(zoneMax.X - 0.5f, max.Y - seamInset), seamCol);
                }
                if (zoneContent != null)
                {
                    zoneContent(drawList, min, zoneMax);
                }
                else
                {
                    // solidFill pills are fully accent · zone glyph and label
                    // both need contrast text to stay readable on the fill.
                    var glyphColor = solidFill || active ? ColorUtils.ContrastTextColor(accent)
                        : hovered ? ColorUtils.LerpColor(accent, Vector4.One, 0.3f)
                        : ColorUtils.LerpColor(accent, Vector4.One, 0.1f);
                    // The default chevron stays quieter than a semantic icon so
                    // real iconography keeps standing out.
                    glyphColor.W *= dim * (isDefaultGlyph ? 0.55f : 1f);
                    using (PushIconFont())
                    {
                        if (MathF.Abs(iconScale - 1f) > 0.0001f)
                        {
                            ImGui.SetWindowFontScale(iconScale);
                        }
                        var glyphSize = ImGui.CalcTextSize(glyph!);
                        var zoneCenter = hasLabel
                            ? new Vector2(min.X + zoneW * 0.5f, (min.Y + max.Y) * 0.5f)
                            : (min + max) * 0.5f;
                        drawList.AddText(zoneCenter - glyphSize * 0.5f
                                + new Vector2(0f, pressShift),
                            ColorUtils.ToU32(glyphColor), glyph);
                        if (MathF.Abs(iconScale - 1f) > 0.0001f)
                        {
                            ImGui.SetWindowFontScale(1f);
                        }
                    }
                }
            }

            var borderAlpha = (active ? SegBorderActive
                : held ? SegBorderHeld
                : hovered ? SegBorderHover
                : SegBorderIdle) * dim;
            var borderCol = solidFill
                ? ColorUtils.WithAlpha(ColorUtils.LerpColor(accent, Vector4.One, 0.25f), borderAlpha)
                : ColorUtils.WithAlpha(Colors.Border, borderAlpha);
            drawList.AddRect(min, max, ColorUtils.ToU32(borderCol), radius);

            if (hasLabel)
            {
                var labelSize = ImGui.CalcTextSize(display!);
                var textPadX = SegTextPadX * scale;
                var textX = min.X + zoneW + textPadX;
                var textBase = labelColor
                    ?? (solidFill ? ColorUtils.ContrastTextColor(accent)
                    : active ? ColorUtils.LerpColor(accent, Vector4.One, 0.25f)
                    : ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Text)));
                textBase.W *= dim;
                ImGui.PushClipRect(new Vector2(textX, min.Y),
                    new Vector2(max.X - textPadX * 0.5f, max.Y), true);
                drawList.AddText(new Vector2(textX,
                        min.Y + (max.Y - min.Y - labelSize.Y) * 0.5f + pressShift),
                    ColorUtils.ToU32(textBase), display!);
                ImGui.PopClipRect();
            }
            Lint.CheckBounds(id);
            return clicked && enabled;
        }

        /// <summary>
        /// Segmented push button in the nav-menu language: optional square
        /// icon zone + seam + label on a tonal pill. The single helper for
        /// action buttons across the app · use it instead of raw ImGui
        /// buttons. <paramref name="active"/> is an optional toggled state;
        /// <paramref name="accent"/> overrides the theme accent per button.
        /// </summary>
        public static bool SegmentButton(string label, FontAwesomeIcon? icon = null,
            Vector2? size = null, Vector4? accent = null, bool active = false, bool enabled = true)
            => SegCore(label, label, icon, size, accent ?? Colors.Accent, active, enabled);

        /// <summary>
        /// Primary call-to-action in the same segmented pill language · the
        /// whole pill carries the accent and the label renders in contrast
        /// text, so it reads as the one action that matters on a page
        /// (capture, confirm, apply). Everything else should stay a
        /// <see cref="SegmentButton"/>, <see cref="Button"/> or icon button.
        /// Width matches <see cref="Icons.TextButtonWidth"/>.
        /// </summary>
        public static bool PrimaryButton(string label, FontAwesomeIcon? icon = null,
            Vector2? size = null, Vector4? accent = null, bool enabled = true)
            => SegCore(label, label, icon, size, accent ?? Colors.Accent,
                enabled: enabled, solidFill: true);

        /// <summary>Natural width of a <see cref="Segmented"/> bar for the given items.</summary>
        public static float SegmentedWidth(string[] items)
        {
            var scale = SpheneUI.Scale;
            var pad = 10f * scale;
            var maxText = 0f;
            foreach (var item in items)
            {
                maxText = MathF.Max(maxText, ImGui.CalcTextSize(item).X);
            }
            return (maxText + pad * 2f) * items.Length + 2f * scale;
        }

        /// <summary>
        /// Connected segment bar · the modern replacement for a dropdown when
        /// only a handful of options exist: one tonal track, N equal segments,
        /// the active one filled with the accent, dividers between them.
        /// </summary>
        public static bool Segmented(string id, ref int index, string[] items,
            float? width = null)
        {
            var scale = SpheneUI.Scale;
            var frameH = ImGui.GetFrameHeight();
            var radius = Metrics.ControlRadius;
            var total = width ?? SegmentedWidth(items);
            var segW = total / items.Length;
            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var changed = false;

            var theme = SpheneTheme.Current;
            drawList.AddRectFilled(min, min + new Vector2(total, frameH),
                ColorUtils.ToU32(theme.FrameBg), radius);

            for (var i = 0; i < items.Length; i++)
            {
                var segMin = new Vector2(min.X + i * segW, min.Y);
                var segMax = segMin + new Vector2(segW, frameH);
                ImGui.SetCursorScreenPos(segMin);
                if (ImGui.InvisibleButton($"{items[i]}##{id}-{i}", new Vector2(segW, frameH)))
                {
                    index = i;
                    changed = true;
                }
                var hovered = ImGui.IsItemHovered();
                var active = i == index;
                if (active || hovered)
                {
                    var flags = ImDrawFlags.None;
                    if (i == 0)
                    {
                        flags = ImDrawFlags.RoundCornersLeft;
                    }
                    else if (i == items.Length - 1)
                    {
                        flags = ImDrawFlags.RoundCornersRight;
                    }
                    if (active)
                    {
                        // Selected segment rides the accent ramp so the bar
                        // matches the nav/primary gradient language.
                        var (gDeep, gHi) = Colors.AccentRamp(Colors.Accent);
                        gDeep.W = 0.34f * ImGui.GetStyle().Alpha;
                        gHi.W = 0.34f * ImGui.GetStyle().Alpha;
                        Chrome.RoundedHorizontalGradient(drawList, segMin, segMax,
                            radius, gDeep, gHi, flags);
                    }
                    else
                    {
                        var fill = ColorUtils.WithAlpha(Colors.Accent,
                            0.10f * ImGui.GetStyle().Alpha);
                        drawList.AddRectFilled(segMin, segMax, ColorUtils.ToU32(fill), radius, flags);
                    }
                }
                if (i > 0)
                {
                    var x = MathF.Round(segMin.X) + 0.5f;
                    drawList.AddLine(new Vector2(x, segMin.Y + 3f * scale),
                        new Vector2(x, segMax.Y - 3f * scale),
                        ColorUtils.ToU32(ColorUtils.WithAlpha(Vector4.One, 0.09f)));
                }
                var text = items[i];
                var textW = ImGui.CalcTextSize(text).X;
                drawList.AddText(new Vector2(segMin.X + (segW - textW) * 0.5f,
                        segMin.Y + (frameH - ImGui.GetTextLineHeight()) * 0.5f),
                    ImGui.GetColorU32(active || hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled),
                    text);
            }
            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new Vector2(total, frameH));
            Lint.CheckBounds(id);
            return changed;
        }

        /// <summary>
        /// Design-system push button: segmented pill in the shared button
        /// language. <paramref name="danger"/> tints the zone red for
        /// destructive actions. Use instead of raw ImGui.Button.
        /// </summary>
        public static bool Button(string label, Vector2 size = default, bool danger = false)
            => SegCore(label, label, null, size == default ? null : size,
                danger ? Colors.Danger : Colors.Accent);

        /// <summary>
        /// Compact text button for dense rows and inline actions · same
        /// segmented fill language as <see cref="Button"/>.
        /// </summary>
        public static bool SmallButton(string label)
            => SegCore(label, label, null, null, Colors.Accent);

        /// <summary>Rendered width of a <see cref="SmallButton"/> · zone plus
        /// label padding, for callers that dock the button to a bound.</summary>
        public static float SmallButtonWidth(string label)
            => SegAutoWidth(label, null, ImGui.GetFrameHeight());

        /// <summary>
        /// Prominent call-to-action button: custom accent color, centered
        /// FontAwesome icon plus display-font label across the given rect.
        /// Use for primary page actions (setup flows, confirmations) where a
        /// standard <see cref="Button"/> is too subtle.
        /// </summary>
        public static bool HeroButton(string id, string label, FontAwesomeIcon? icon = null,
            Vector2 size = default, Vector4? color = null, bool enabled = true, float? textScale = null)
        {
            var scale = SpheneUI.Scale;
            var buttonSize = size == default
                ? new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight())
                : size * scale;
            var baseCol = color ?? Palette.CrystalBlue;

            var drawList = ImGui.GetWindowDrawList();
            var start = ImGui.GetCursorScreenPos();
            var end = start + buttonSize;

            using var disabledScope = ImRaii.Disabled(!enabled);
            var clicked = ImGui.InvisibleButton($"##hero-btn-{id}", buttonSize);
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();

            var hovT = Fx.HoverT(ImGui.GetID($"##hero-btn-{id}"), hovered && enabled);
            var lift = enabled ? 1f + 0.10f * hovT - (active ? 0.10f : 0f) : 0f;
            var fill = enabled ? baseCol * lift : Colors.FrameBg;
            drawList.AddRectFilled(start, end, ImGui.ColorConvertFloat4ToU32(fill),
                Metrics.ButtonRadius);

            if (enabled)
            {
                // soft top sheen · inset by the radius so its square corners
                // never reach the rounded edge
                var sheenMin = new Vector2(start.X + Metrics.ButtonRadius, start.Y + scale);
                var sheenMax = new Vector2(end.X - Metrics.ButtonRadius, start.Y + buttonSize.Y * 0.55f);
                var sheenCol = ImGui.ColorConvertFloat4ToU32(
                    ColorUtils.WithAlpha(Colors.TextPrimary, 0.10f + 0.06f * hovT));
                drawList.AddRectFilledMultiColor(sheenMin, sheenMax, sheenCol, sheenCol, 0u, 0u);
            }

            drawList.AddRect(start, end, ImGui.ColorConvertFloat4ToU32(
                enabled ? ColorUtils.WithAlpha(Colors.TextPrimary, 0.12f + 0.12f * hovT) : Colors.Border),
                Metrics.ButtonRadius);

            var textCol = enabled ? ColorUtils.ContrastTextColor(baseCol) : Colors.TextMuted;
            var spacing = icon.HasValue ? 3f * scale : 0f;
            var iconSize = Vector2.Zero;
            if (icon.HasValue)
            {
                using (SpheneUI.IconFont.Push())
                    iconSize = ImGui.CalcTextSize(icon.Value.ToIconString());
            }
            var drawScale = textScale ?? 1f;
            Vector2 textSize;
            using (SpheneUI.UidFont.Push())
            {
                if (textScale.HasValue) ImGui.SetWindowFontScale(textScale.Value);
                textSize = ImGui.CalcTextSize(label);
                if (textScale.HasValue) ImGui.SetWindowFontScale(1f);
            }

            // shrink the label instead of clipping it when the button is too narrow
            var textAvail = buttonSize.X - iconSize.X - spacing - 12f * scale;
            if (textSize.X > textAvail && textSize.X > 1f)
            {
                var fit = MathF.Max(0.55f, textAvail / textSize.X);
                drawScale *= fit;
                textSize *= fit;
            }

            var totalWidth = iconSize.X + spacing + textSize.X;
            var contentX = start.X + (buttonSize.X - totalWidth) * 0.5f;
            var iconY = start.Y + (buttonSize.Y - iconSize.Y) * 0.5f;
            var textY = start.Y + (buttonSize.Y - textSize.Y) * 0.5f;
            var textU32 = ImGui.ColorConvertFloat4ToU32(textCol);
            if (icon.HasValue)
            {
                using (SpheneUI.IconFont.Push())
                    drawList.AddText(new Vector2(contentX, iconY), textU32, icon.Value.ToIconString());
            }
            using (SpheneUI.UidFont.Push())
            {
                if (drawScale < 0.999f) ImGui.SetWindowFontScale(drawScale);
                drawList.AddText(new Vector2(contentX + iconSize.X + spacing, textY), textU32, label);
                if (drawScale < 0.999f) ImGui.SetWindowFontScale(1f);
            }

            Lint.CheckBounds(label);
            return clicked && enabled;
        }

        /// <summary>
        /// Compact checkbox for dense rows and table cells · keeps the classic
        /// box footprint where a row-spanning <see cref="Toggle"/> would not fit.
        /// </summary>
        public static bool Checkbox(string label, ref bool value)
        {
            using var style = PushValueControlStyle();
            var changed = ImGui.Checkbox(label, ref value);
            Lint.CheckBounds(label);
            return changed;
        }

        /// <summary>
        /// Square icon-only button in the swatch language · frame fill, border,
        /// centered FontAwesome glyph in the segmented zone language. Use for
        /// compact actions next to value controls; pair with
        /// <see cref="SpheneUI.AttachTooltip"/>.
        /// </summary>
        public static bool IconButton(string id, FontAwesomeIcon icon,
            bool enabled = true, float? size = null)
        {
            var side = size ?? Metrics.ControlHeight;
            return SegCore(id, null, icon, new Vector2(side, side),
                Colors.Accent, enabled: enabled);
        }

        /// <summary>
        /// Confirm/cancel pair split across the bounded content width · the
        /// modal dialog pattern. Returns which button was clicked (0/1/2).
        /// </summary>
        public static int DialogButtons(string id, string confirmLabel, string cancelLabel,
            bool confirmDanger = false)
        {
            var spacing = Metrics.Gap;
            var buttonWidth = (BoundedAvailX() - spacing) * 0.5f;
            var size = new Vector2(buttonWidth, 0f);
            var clicked = 0;
            if (PrimaryButton(confirmLabel + "##" + id, size: size,
                    accent: confirmDanger ? Colors.Danger : null))
            {
                clicked = 1;
            }
            ImGui.SameLine();
            if (Button(cancelLabel + "##" + id, size))
            {
                clicked = 2;
            }
            return clicked;
        }

        /// <summary>
        /// Compact pill toggle for inline groups (several toggles sharing one
        /// row via SameLine): accent fill + accent text while on, frame fill +
        /// muted text while off.
        /// </summary>
        public static bool ToggleChip(string label, ref bool value)
        {
            var scale = SpheneUI.Scale;
            var accent = Colors.Accent;
            var idSplit = label.IndexOf("##", StringComparison.Ordinal);
            var text = idSplit < 0 ? label : label[..idSplit];
            var height = ImGui.GetFrameHeight();
            var width = ImGui.CalcTextSize(text).X + height * 0.9f;
            var pos = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(label, new Vector2(width, height));
            var hovered = ImGui.IsItemHovered();
            if (clicked)
            {
                value = !value;
            }
            var frameBg = Colors.FrameBg;
            var fill = value
                ? accent with { W = 0.85f }
                : hovered
                    ? Vector4.Lerp(frameBg, accent, 0.15f)
                    : frameBg;
            var border = value
                ? accent with { W = Metrics.SwatchBorderAlphaSelected }
                : hovered
                    ? accent with { W = Metrics.SwatchBorderAlphaHover }
                    : new Vector4(1f, 1f, 1f, Metrics.SwatchBorderAlpha);
            var max = pos + new Vector2(width, height);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(pos, max, ColorUtils.ToU32(fill), height * 0.5f);
            drawList.AddRect(pos, max, ColorUtils.ToU32(border), height * 0.5f,
                ImDrawFlags.None, 1f * scale);
            var textSize = ImGui.CalcTextSize(text);
            var textColor = value ? new Vector4(1f, 1f, 1f, 1f) : Colors.TextSecondary;
            drawList.AddText(new Vector2(pos.X + (width - textSize.X) * 0.5f,
                pos.Y + (height - textSize.Y) * 0.5f), ColorUtils.ToU32(textColor), text);
            Lint.CheckBounds(label);
            return clicked;
        }

        /// <summary>
        /// Selectable list row in the value-control language · use instead of
        /// raw ImGui.Selectable inside settings content.
        /// </summary>
        public static bool Selectable(string label, bool selected,
            ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, Vector2 size = default)
        {
            using var style = PushValueControlStyle();
            var clicked = ImGui.Selectable(label, selected, flags, size);
            Lint.CheckBounds(label);
            return clicked;
        }

        /// <summary>
        /// Collapsible sub-section header: chevron + label, accent text while
        /// hovered, hover row tint, open state persisted in the window storage.
        /// Replaces ImGui.CollapsingHeader · returns whether the section is
        /// open; draw the content inside the if-block.
        /// </summary>
        public static bool CollapsibleSection(string label, bool defaultOpen = false)
        {
            var storage = ImGui.GetStateStorage();
            var storageId = ImGui.GetID("collapsible##" + label);
            var open = storage.GetInt(storageId, defaultOpen ? 1 : 0) != 0;

            var scale = SpheneUI.Scale;
            var cursor = ImGui.GetCursorScreenPos();
            var height = ImGui.GetFrameHeight();
            var width = BoundedAvailX();
            var pressed = ImGui.InvisibleButton("##sectionToggle" + label, new Vector2(width, height));
            var hovered = ImGui.IsItemHovered();
            if (pressed)
            {
                open = !open;
                storage.SetInt(storageId, open ? 1 : 0);
            }

            var drawList = ImGui.GetWindowDrawList();
            if (hovered)
            {
                var tint = Colors.Accent with { W = 0.08f * ImGui.GetStyle().Alpha };
                drawList.AddRectFilled(cursor, cursor + new Vector2(width, height),
                    ColorUtils.ToU32(tint), Metrics.SwatchRadius(height));
            }

            ImGui.SetCursorScreenPos(cursor + new Vector2(2f * scale,
                (height - ImGui.GetTextLineHeight()) * 0.5f));
            Icons.Icon(open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight,
                hovered ? Colors.Accent : Colors.TextMuted);
            ImGui.SameLine(0f, Metrics.Gap * 0.5f);
            Text.Colored(label, open ? Colors.TextPrimary : Colors.TextSecondary);

            Lint.CheckBounds(label);
            return open;
        }

        /// <summary>
        /// Rounded color swatch button: filled with the color, subtle border
        /// that brightens on hover and turns accent when
        /// <paramref name="selected"/>. Never opens a picker · the caller owns
        /// the click.
        /// </summary>
        public static bool ColorSwatch(string id, Vector4 color, float size = -1f, bool selected = false)
        {
            var scale = SpheneUI.Scale;
            if (size < 0f)
            {
                size = Metrics.SwatchSize;
            }
            var pos = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
            var hovered = ImGui.IsItemHovered();
            var fill = hovered
                ? new Vector4(
                    MathF.Min(1f, color.X * Metrics.SwatchHoverBrighten + Metrics.SwatchHoverBrightenAdd),
                    MathF.Min(1f, color.Y * Metrics.SwatchHoverBrighten + Metrics.SwatchHoverBrightenAdd),
                    MathF.Min(1f, color.Z * Metrics.SwatchHoverBrighten + Metrics.SwatchHoverBrightenAdd), color.W)
                : color;
            var rounding = Metrics.SwatchRadius(size);
            var accent = Colors.Accent;
            var border = selected
                ? new Vector4(accent.X, accent.Y, accent.Z, Metrics.SwatchBorderAlphaSelected)
                : hovered
                    ? new Vector4(accent.X, accent.Y, accent.Z, Metrics.SwatchBorderAlphaHover)
                    : new Vector4(1f, 1f, 1f, Metrics.SwatchBorderAlpha);
            var max = pos + new Vector2(size, size);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(pos, max, ColorUtils.ToU32(fill), rounding);
            drawList.AddRect(pos, max, ColorUtils.ToU32(border), rounding,
                ImDrawFlags.None, selected ? Metrics.SwatchSelectedThickness * scale : 1f * scale);
            Lint.CheckBounds(id);
            return clicked;
        }

        /// <summary>
        /// Same swatch button as <see cref="ColorSwatch"/> but renders a short
        /// glyph (symbol, letter) centered instead of a color fill · used for
        /// palette pickers whose entries are characters rather than colors.
        /// </summary>
        public static bool GlyphSwatch(string id, string glyph, float size = -1f, bool selected = false)
        {
            var scale = SpheneUI.Scale;
            if (size < 0f)
            {
                size = Metrics.SwatchSize;
            }
            var pos = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
            var hovered = ImGui.IsItemHovered();
            var style = ImGui.GetStyle();
            var fill = hovered
                ? style.Colors[(int)ImGuiCol.FrameBgHovered]
                : style.Colors[(int)ImGuiCol.FrameBg];
            var accent = Colors.Accent;
            var border = selected
                ? new Vector4(accent.X, accent.Y, accent.Z, Metrics.SwatchBorderAlphaSelected)
                : hovered
                    ? new Vector4(accent.X, accent.Y, accent.Z, Metrics.SwatchBorderAlphaHover)
                    : new Vector4(1f, 1f, 1f, Metrics.SwatchBorderAlpha);
            var max = pos + new Vector2(size, size);
            var rounding = Metrics.SwatchRadius(size);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(pos, max, ColorUtils.ToU32(fill), rounding);
            drawList.AddRect(pos, max, ColorUtils.ToU32(border), rounding,
                ImDrawFlags.None, selected ? Metrics.SwatchSelectedThickness * scale : 1f * scale);
            if (glyph.Length > 0)
            {
                var textSize = ImGui.CalcTextSize(glyph);
                drawList.AddText(new Vector2(pos.X + (size - textSize.X) * 0.5f,
                    pos.Y + (size - textSize.Y) * 0.5f), ImGui.GetColorU32(ImGuiCol.Text), glyph);
            }
            Lint.CheckBounds(id);
            return clicked;
        }

        /// <summary>
        /// Same swatch button as <see cref="GlyphSwatch"/> but renders a thick
        /// colored frame instead of a glyph · the interior keeps the neutral
        /// control background. Used for pickers where the color acts as an
        /// outline or edge rather than the glyph fill.
        /// </summary>
        public static bool FrameSwatch(string id, Vector4 color, float size = -1f, bool selected = false)
        {
            var scale = SpheneUI.Scale;
            if (size < 0f)
            {
                size = Metrics.SwatchSize;
            }
            var pos = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
            var hovered = ImGui.IsItemHovered();
            var style = ImGui.GetStyle();
            var fill = hovered
                ? style.Colors[(int)ImGuiCol.FrameBgHovered]
                : style.Colors[(int)ImGuiCol.FrameBg];
            var ring = hovered
                ? new Vector4(
                    MathF.Min(1f, color.X * Metrics.SwatchHoverBrighten + Metrics.SwatchHoverBrightenAdd),
                    MathF.Min(1f, color.Y * Metrics.SwatchHoverBrighten + Metrics.SwatchHoverBrightenAdd),
                    MathF.Min(1f, color.Z * Metrics.SwatchHoverBrighten + Metrics.SwatchHoverBrightenAdd), color.W)
                : color;
            var rounding = Metrics.SwatchRadius(size);
            var max = pos + new Vector2(size, size);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(pos, max, ColorUtils.ToU32(fill), rounding);
            drawList.AddRect(pos, max, ColorUtils.ToU32(ring), rounding,
                ImDrawFlags.None, MathF.Max(2f, 2.5f * scale));
            if (selected)
            {
                var accent = Colors.Accent;
                var outer = new Vector2(1.5f * scale, 1.5f * scale);
                drawList.AddRect(pos - outer, max + outer,
                    ColorUtils.ToU32(accent with { W = Metrics.SwatchBorderAlphaSelected }),
                    rounding, ImDrawFlags.None, 1f * scale);
            }
            Lint.CheckBounds(id);
            return clicked;
        }

        /// <summary>
        /// Swatch button rendered as a transparency checkerboard · the
        /// "no color / none" entry in palette pickers.
        /// </summary>
        public static bool TransparentSwatch(string id, float size = -1f, bool selected = false)
        {
            var scale = SpheneUI.Scale;
            if (size < 0f)
            {
                size = Metrics.SwatchSize;
            }
            var pos = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
            var hovered = ImGui.IsItemHovered();
            var rounding = Metrics.SwatchRadius(size);
            var max = pos + new Vector2(size, size);
            var drawList = ImGui.GetWindowDrawList();
            var light = new Vector4(0.72f, 0.72f, 0.72f, 1f);
            var dark = new Vector4(0.38f, 0.38f, 0.38f, 1f);
            var cell = size / 4f;
            drawList.PushClipRect(pos, max, true);
            drawList.AddRectFilled(pos, max, ColorUtils.ToU32(light), rounding);
            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    if (((row + col) & 1) == 0)
                    {
                        var cellMin = pos + new Vector2(col * cell, row * cell);
                        drawList.AddRectFilled(cellMin, cellMin + new Vector2(cell, cell),
                            ColorUtils.ToU32(dark));
                    }
                }
            }
            drawList.PopClipRect();
            var accent = Colors.Accent;
            var border = selected
                ? new Vector4(accent.X, accent.Y, accent.Z, Metrics.SwatchBorderAlphaSelected)
                : hovered
                    ? new Vector4(accent.X, accent.Y, accent.Z, Metrics.SwatchBorderAlphaHover)
                    : new Vector4(1f, 1f, 1f, Metrics.SwatchBorderAlpha);
            drawList.AddRect(pos, max, ColorUtils.ToU32(border), rounding,
                ImDrawFlags.None, selected ? Metrics.SwatchSelectedThickness * scale : 1f * scale);
            Lint.CheckBounds(id);
            return clicked;
        }

        /// <summary>
        /// Status dot + label in the semantic status colors · the shared
        /// connected/warning/error indicator.
        /// </summary>
        public static void StatusDot(string label, bool isActive, bool hasWarning = false,
            bool hasError = false)
        {
            var theme = Theme.Current;
            var statusColor = hasError
                ? theme.CompactServerStatusError
                : hasWarning
                    ? theme.CompactServerStatusWarning
                    : (isActive ? theme.CompactServerStatusConnected : theme.CompactTextSecondary);
            var drawList = ImGui.GetWindowDrawList();

            ImGui.AlignTextToFramePadding();
            var alignedPos = ImGui.GetCursorScreenPos();

            var frameHeight = ImGui.GetFrameHeight();
            var scale = SpheneUI.Scale;
            var dotRadius = 6f * scale;
            var circleCenter = new Vector2(alignedPos.X + 8f * scale, alignedPos.Y + frameHeight * 0.5f);

            drawList.AddCircleFilled(circleCenter, dotRadius, ColorUtils.ToU32(statusColor));
            drawList.AddCircle(circleCenter, dotRadius, ColorUtils.ToU32(theme.TextPrimary), 12, 1.5f);

            if (isActive && !hasError)
            {
                var glowColor = new Vector4(statusColor.X, statusColor.Y, statusColor.Z, 0.4f);
                drawList.AddCircle(circleCenter, 8f * scale, ColorUtils.ToU32(glowColor), 12, 2.0f);
            }

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20f * scale);
            Text.Colored(label, theme.TextPrimary);
        }

        /// <summary>
        /// Theme-aware progress bar with the crystalline styling (rounded fill,
        /// optional gradient, centered overlay text).
        /// </summary>
        public static void ProgressBar(string label, float progress, string? overlay = null,
            Vector4? color = null)
        {
            ImGui.AlignTextToFramePadding();
            Text.Colored(label, Theme.Current.CompactText);
            var theme = Theme.Current;
            var barSize = new Vector2(theme.CompactProgressBarWidth, theme.CompactProgressBarHeight);
            ProgressBar(progress, barSize, overlay, color);
        }

        /// <summary>
        /// Theme-aware progress bar with the crystalline styling (rounded fill,
        /// optional gradient, centered overlay text).
        /// </summary>
        public static void ProgressBar(float progress, Vector2? size = null, string? overlay = null,
            Vector4? color = null)
        {
            var theme = Theme.Current;
            var barSize = size ?? new Vector2(theme.CompactProgressBarWidth, theme.CompactProgressBarHeight);
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var endPos = new Vector2(pos.X + barSize.X, pos.Y + barSize.Y);
            var borderRadius = theme.ProgressBarRounding;

            drawList.AddRectFilled(pos, endPos, ColorUtils.ToU32(theme.CompactProgressBarBackground), borderRadius);

            if (progress > 0)
            {
                var progressEnd = new Vector2(pos.X + barSize.X * progress, pos.Y + barSize.Y);
                if (theme.ProgressBarUseGradient)
                {
                    Chrome.RoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius,
                        theme.ProgressBarGradientStart, theme.ProgressBarGradientEnd);
                }
                else
                {
                    var progressColorStart = color ?? theme.CompactProgressBarForeground;
                    drawList.AddRectFilled(pos, progressEnd, ColorUtils.ToU32(progressColorStart), borderRadius);
                }
            }

            drawList.AddRect(pos, endPos, ColorUtils.ToU32(theme.CompactProgressBarBorder),
                borderRadius, ImDrawFlags.RoundCornersAll, theme.FrameBorderSize);

            if (!string.IsNullOrEmpty(overlay))
            {
                var textSize = ImGui.CalcTextSize(overlay);
                var textPos = new Vector2(
                    pos.X + (barSize.X - textSize.X) * 0.5f,
                    pos.Y + (barSize.Y - textSize.Y) * 0.5f);
                drawList.AddText(textPos, ColorUtils.ToU32(theme.TextPrimary), overlay);
            }

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + barSize.Y + ImGui.GetStyle().ItemSpacing.Y);
        }

        /// <summary>
        /// Indeterminate variant of <see cref="ProgressBar"/> - a rounded gradient
        /// segment ping-pongs across the track for work of unknown length.
        /// </summary>
        public static void ProgressBarIndeterminate(Vector2? size = null, string? overlay = null)
        {
            var theme = Theme.Current;
            var barSize = size ?? new Vector2(theme.CompactProgressBarWidth, theme.CompactProgressBarHeight);
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var endPos = pos + barSize;
            var borderRadius = theme.ProgressBarRounding;

            drawList.AddRectFilled(pos, endPos, ColorUtils.ToU32(theme.CompactProgressBarBackground), borderRadius);

            // ping-pong travel - the segment never leaves the track, so its
            // rounded caps are always visible and match the bar's rounding
            var segW = barSize.X * 0.30f;
            var t = (float)(ImGui.GetTime() % 2.2) / 2.2f;
            var s = t < 0.5f ? t * 2f : 2f - t * 2f;
            var ease = s * s * (3f - 2f * s);
            var segMin = new Vector2(pos.X + (barSize.X - segW) * ease, pos.Y);
            var segMax = segMin + new Vector2(segW, barSize.Y);

            if (theme.ProgressBarUseGradient)
            {
                Chrome.RoundedHorizontalGradient(drawList, segMin, segMax, borderRadius,
                    theme.ProgressBarGradientStart, theme.ProgressBarGradientEnd);
            }
            else
            {
                drawList.AddRectFilled(segMin, segMax, ColorUtils.ToU32(theme.CompactProgressBarForeground), borderRadius);
            }

            drawList.AddRect(pos, endPos, ColorUtils.ToU32(theme.CompactProgressBarBorder),
                borderRadius, ImDrawFlags.RoundCornersAll, theme.FrameBorderSize);

            if (!string.IsNullOrEmpty(overlay))
            {
                var textSize = ImGui.CalcTextSize(overlay);
                var textPos = new Vector2(
                    pos.X + (barSize.X - textSize.X) * 0.5f,
                    pos.Y + (barSize.Y - textSize.Y) * 0.5f);
                drawList.AddText(textPos, ColorUtils.ToU32(theme.TextPrimary), overlay);
            }

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + barSize.Y + ImGui.GetStyle().ItemSpacing.Y);
        }

        /// <summary>Transmission progress bar with upload/download styling from the theme.</summary>
        public static void TransmissionBar(string label, float progress, string? overlay = null,
            bool? isUpload = null)
        {
            ImGui.AlignTextToFramePadding();
            Text.Colored(label, Theme.Current.CompactText);
            var theme = Theme.Current;
            var width = theme.AutoTransmissionBarWidth
                ? Math.Max(50.0f, ImGui.GetContentRegionAvail().X)
                : theme.CompactTransmissionBarWidth;
            var height = theme.SeparateTransmissionBarStyles && isUpload.HasValue
                ? (isUpload.Value ? theme.UploadTransmissionBarHeight : theme.DownloadTransmissionBarHeight)
                : theme.CompactTransmissionBarHeight;
            var barSize = new Vector2(width, height);
            TransmissionBar(progress, barSize, overlay, isUpload);
        }

        /// <summary>Transmission progress bar with upload/download styling from the theme.</summary>
        public static void TransmissionBar(float progress, Vector2 size, string? overlay = null,
            bool? isUpload = null)
        {
            var theme = Theme.Current;
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var endPos = new Vector2(pos.X + size.X, pos.Y + size.Y);
            var borderRadius = theme.SeparateTransmissionBarStyles && isUpload.HasValue
                ? (isUpload.Value ? theme.UploadTransmissionBarRounding : theme.DownloadTransmissionBarRounding)
                : theme.TransmissionBarRounding;

            var bg = theme.SeparateTransmissionBarStyles && isUpload.HasValue
                ? (isUpload.Value ? theme.UploadTransmissionBarBackground : theme.DownloadTransmissionBarBackground)
                : theme.CompactTransmissionBarBackground;
            drawList.AddRectFilled(pos, endPos, ColorUtils.ToU32(bg), borderRadius);

            if (progress > 0)
            {
                var progressEnd = new Vector2(pos.X + size.X * progress, pos.Y + size.Y);
                if (theme.SeparateTransmissionBarStyles && isUpload.HasValue)
                {
                    if (isUpload.Value)
                    {
                        if (theme.TransmissionUseGradient)
                        {
                            Chrome.RoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius,
                                theme.UploadTransmissionGradientStart, theme.UploadTransmissionGradientEnd);
                        }
                        else
                        {
                            drawList.AddRectFilled(pos, progressEnd,
                                ColorUtils.ToU32(theme.UploadTransmissionBarForeground), borderRadius);
                        }
                    }
                    else
                    {
                        if (theme.TransmissionUseGradient)
                        {
                            Chrome.RoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius,
                                theme.DownloadTransmissionGradientStart, theme.DownloadTransmissionGradientEnd);
                        }
                        else
                        {
                            drawList.AddRectFilled(pos, progressEnd,
                                ColorUtils.ToU32(theme.DownloadTransmissionBarForeground), borderRadius);
                        }
                    }
                }
                else
                {
                    if (theme.TransmissionUseGradient)
                    {
                        Chrome.RoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius,
                            theme.TransmissionGradientStart, theme.TransmissionGradientEnd);
                    }
                    else
                    {
                        drawList.AddRectFilled(pos, progressEnd,
                            ColorUtils.ToU32(theme.CompactTransmissionBarForeground), borderRadius);
                    }
                }
            }

            var border = theme.SeparateTransmissionBarStyles && isUpload.HasValue
                ? (isUpload.Value ? theme.UploadTransmissionBarBorder : theme.DownloadTransmissionBarBorder)
                : theme.CompactTransmissionBarBorder;
            drawList.AddRect(pos, endPos, ColorUtils.ToU32(border), borderRadius,
                ImDrawFlags.RoundCornersAll, theme.CompactFrameBorderSize);

            if (!string.IsNullOrEmpty(overlay))
            {
                var textSize = ImGui.CalcTextSize(overlay);
                var textPos = new Vector2(
                    pos.X + (size.X - textSize.X) * 0.5f,
                    pos.Y + (size.Y - textSize.Y) * 0.5f);
                drawList.AddText(textPos, ColorUtils.ToU32(theme.TextPrimary), overlay);
            }

            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + size.Y + ImGui.GetStyle().ItemSpacing.Y);
        }

        /// <summary>One entry of a <see cref="TabBar"/> · icon, label, dim state, tooltip.</summary>
        public readonly record struct TabItem(FontAwesomeIcon? Icon, string Label,
            bool Dimmed = false, string? Tooltip = null);

        // Eased marker position in tab-index units, per tab-bar instance.
        private static readonly ConcurrentDictionary<uint, float> _tabTracePos = new();

        /// <summary>
        /// Horizontal tab strip with the aether-trace indicator: a tapered
        /// accent line on the baseline plus a diamond node at the tab's
        /// center. The marker eases in tab-index space · never in screen
        /// space · so it cannot trail behind when the window is dragged.
        /// Content (icon + label) is measured and centered per tab.
        /// Returns the clicked index or -1; the caller owns the selection.
        /// </summary>
        public static int TabBar(string id, IReadOnlyList<TabItem> tabs, int selected,
            Vector4 accent)
        {
            var scale = Scale;
            var drawList = ImGui.GetWindowDrawList();
            var rowH = ImGui.GetFrameHeight() + 8f * scale;
            var barMin = ImGui.GetCursorScreenPos();
            var rowRight = barMin.X + ImGui.GetContentRegionAvail().X;
            var clicked = -1;
            var hovered = -1;
            var rects = new (float Center, float ContentHalf)[tabs.Count];

            using (ImRaii.PushId(id))
            {
                for (var i = 0; i < tabs.Count; i++)
                {
                    var tab = tabs[i];
                    var icon = tab.Icon?.ToIconString();
                    var labelW = ImGui.CalcTextSize(tab.Label).X;
                    float iconW;
                    using (Icons.PushFont())
                    {
                        iconW = icon != null ? ImGui.CalcTextSize(icon).X : 0f;
                    }
                    var gap = icon != null ? 5f * scale : 0f;
                    var contentW = iconW + gap + labelW;
                    var tabW = contentW + 20f * scale;

                    var tabMin = ImGui.GetCursorScreenPos();
                    if (ImGui.InvisibleButton($"##{i}", new Vector2(tabW, rowH)))
                    {
                        clicked = i;
                    }
                    var isHovered = ImGui.IsItemHovered();
                    if (isHovered)
                    {
                        hovered = i;
                        if (tab.Tooltip != null)
                        {
                            AttachTooltip(tab.Tooltip);
                        }
                    }

                    var active = i == selected;
                    var col = active ? accent
                        : tab.Dimmed ? ColorUtils.WithAlpha(Colors.TextMuted, 0.45f)
                        : isHovered ? Colors.TextPrimary
                        : Colors.TextMuted;
                    var textY = tabMin.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
                    var x = tabMin.X + (tabW - contentW) * 0.5f;
                    if (icon != null)
                    {
                        using (Icons.PushFont())
                        {
                            drawList.AddText(new Vector2(x, textY), ColorUtils.ToU32(col), icon);
                        }
                        x += iconW + gap;
                    }
                    drawList.AddText(new Vector2(x, textY), ColorUtils.ToU32(col), tab.Label);

                    rects[i] = (tabMin.X + tabW * 0.5f, contentW * 0.5f);
                    ImGui.SameLine(0f, 2f * scale);
                }
                ImGui.NewLine();
            }

            var baseY = barMin.Y + rowH;
            drawList.AddLine(new Vector2(barMin.X, baseY + 1f),
                new Vector2(rowRight, baseY + 1f),
                ColorUtils.ToU32(Colors.Border with { W = 0.25f }));

            if (hovered >= 0 && hovered != selected)
            {
                var (hcx, hch) = rects[hovered];
                TraceLine(drawList, hcx, baseY, hch + 6f * scale,
                    ColorUtils.WithAlpha(accent, 0.30f), scale);
            }

            if (selected >= 0 && selected < tabs.Count)
            {
                var animKey = ImGui.GetID(id);
                if (!_tabTracePos.TryGetValue(animKey, out var anim) || anim < -0.5f)
                {
                    anim = selected;
                }
                var delta = selected - anim;
                anim += delta * MathF.Min(1f, ImGui.GetIO().DeltaTime * 14f);
                if (MathF.Abs(delta) < 0.03f)
                {
                    anim = selected;
                }
                _tabTracePos[animKey] = anim;

                var lo = Math.Clamp((int)MathF.Floor(anim), 0, tabs.Count - 1);
                var hi = Math.Min(lo + 1, tabs.Count - 1);
                var frac = anim - lo;
                var cx = rects[lo].Center + (rects[hi].Center - rects[lo].Center) * frac;
                var ch = rects[lo].ContentHalf
                    + (rects[hi].ContentHalf - rects[lo].ContentHalf) * frac;

                TraceLine(drawList, cx, baseY, ch + 8f * scale,
                    Colors.AccentRamp(accent).Hi, scale);
                TraceNode(drawList, cx, baseY + 1f,
                    MathF.Abs(delta) > 0.02f, accent, scale);
            }

            return clicked;
        }

        /// <summary>Tapered glow line · transparent ends fading into a solid core.</summary>
        private static void TraceLine(ImDrawListPtr drawList, float centerX, float baseY,
            float halfWidth, Vector4 color, float scale)
        {
            var h = MathF.Max(1.25f, 1.5f * scale);
            var y0 = baseY + 1f - h * 0.5f;
            var y1 = baseY + 1f + h * 0.5f;
            var col = ColorUtils.ToU32(color);
            drawList.AddRectFilledMultiColor(
                new Vector2(centerX - halfWidth, y0), new Vector2(centerX, y1),
                0u, col, col, 0u);
            drawList.AddRectFilledMultiColor(
                new Vector2(centerX, y0), new Vector2(centerX + halfWidth, y1),
                col, 0u, 0u, col);
        }

        /// <summary>
        /// The diamond node at the trace center · outer accent diamond, bright
        /// core, soft halo. Swells slightly while the marker is in transit.
        /// </summary>
        /// <summary>Joined two-half call-to-action - both halves read as one
        /// pill with a center divider (same family as the Ko-fi|perks split).
        /// Returns which half was activated.</summary>
        public static (bool left, bool right) SplitButton(string id,
            FontAwesomeIcon leftIcon, string leftLabel, Vector4 leftTint,
            FontAwesomeIcon rightIcon, string rightLabel, Vector4 rightTint,
            float width, float height, bool enabled = true)
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, height);
            var radius = MathF.Min(Metrics.ButtonRadius, height * 0.3f);
            var half = new Vector2(width * 0.5f, height);

            ImGui.PushID(id);
            using (ImRaii.Disabled(!enabled))
            {
                ImGui.SetCursorScreenPos(min);
                var leftHit = ImGui.InvisibleButton("##left", half);
                var leftHov = ImGui.IsItemHovered();
                ImGui.SetCursorScreenPos(new Vector2(min.X + half.X, min.Y));
                var rightHit = ImGui.InvisibleButton("##right", half);
                var rightHov = ImGui.IsItemHovered();
                ImGui.PopID();
                ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));

                var t = (float)(ImGui.GetTime() % 2.0);
                var pulse = enabled ? 0.5f + 0.5f * MathF.Sin(t * MathF.PI * 2f) : 0f;
                var midX = min.X + half.X;

                var leftCol = ColorUtils.LerpColor(leftTint, Vector4.One,
                    (leftHov ? 0.30f : 0f) + pulse * 0.10f);
                var rightCol = ColorUtils.LerpColor(rightTint, Vector4.One,
                    (rightHov ? 0.30f : 0f) + pulse * 0.10f);
                drawList.AddRectFilled(min, new Vector2(midX, max.Y),
                    ColorUtils.ToU32(ColorUtils.WithAlpha(leftCol, 0.95f)),
                    radius, ImDrawFlags.RoundCornersLeft);
                drawList.AddRectFilled(new Vector2(midX, min.Y), max,
                    ColorUtils.ToU32(ColorUtils.WithAlpha(rightCol, 0.95f)),
                    radius, ImDrawFlags.RoundCornersRight);
                drawList.AddRect(min, max, ColorUtils.ToU32(ColorUtils.WithAlpha(
                    ColorUtils.LerpColor(rightTint, Vector4.One, pulse * 0.5f),
                    0.55f + 0.45f * pulse)), radius, ImDrawFlags.RoundCornersAll,
                    MathF.Max(1.5f, 1.5f * scale));
                drawList.AddLine(new Vector2(midX, min.Y + 7f * scale),
                    new Vector2(midX, max.Y - 7f * scale),
                    ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 0.30f)));

                var iconS = height * 0.45f;
                void Half(Vector2 halfMin, FontAwesomeIcon glyph, string label,
                    Vector4 content)
                {
                    var contentU32 = ColorUtils.ToU32(content);
                    var labelW = ImGui.CalcTextSize(label).X;
                    var groupW = iconS + 6f * scale + labelW;
                    var x = halfMin.X + (half.X - groupW) * 0.5f;
                    var iconMin = new Vector2(x, halfMin.Y + (height - iconS) * 0.5f);
                    using (SpheneUI.IconFont.Push())
                    {
                        drawList.AddText(iconMin, contentU32, glyph.ToIconString());
                    }
                    drawList.AddText(new Vector2(x + iconS + 6f * scale,
                        halfMin.Y + (height - ImGui.GetTextLineHeight()) * 0.5f),
                        contentU32, label);
                }

                var dark = new Vector4(0.10f, 0.10f, 0.14f, 1f);
                var light = new Vector4(1f, 1f, 1f, 0.96f);
                static float Luma(Vector4 c) => c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;
                Half(min, leftIcon, leftLabel, Luma(leftTint) > 0.55f ? dark : light);
                Half(new Vector2(midX, min.Y), rightIcon, rightLabel,
                    Luma(rightTint) > 0.55f ? dark : light);
                return (leftHit && enabled, rightHit && enabled);
            }
        }

        private static void TraceNode(ImDrawListPtr drawList, float centerX, float centerY,
            bool traveling, Vector4 accent, float scale)
        {
            var r = 3.5f * scale * (traveling ? 1.3f : 1f);
            var c = new Vector2(centerX, centerY);
            drawList.AddCircleFilled(c, r * 2.7f,
                ColorUtils.ToU32(ColorUtils.WithAlpha(accent, 0.14f)));
            var col = ColorUtils.ToU32(accent);
            drawList.AddQuadFilled(
                new Vector2(c.X, c.Y - r), new Vector2(c.X + r, c.Y),
                new Vector2(c.X, c.Y + r), new Vector2(c.X - r, c.Y), col);
            var core = ColorUtils.LerpColor(accent, new Vector4(1f, 1f, 1f, 1f), 0.55f);
            var coreR = r * 0.5f;
            drawList.AddQuadFilled(
                new Vector2(c.X, c.Y - coreR), new Vector2(c.X + coreR, c.Y),
                new Vector2(c.X, c.Y + coreR), new Vector2(c.X - coreR, c.Y),
                ColorUtils.ToU32(core));
        }
    }
}
