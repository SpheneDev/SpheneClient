using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System.Numerics;

namespace Sphene.UI.Design;

public static partial class SpheneUI
{
    public static partial class Controls
    {
        /// <summary>
        /// Calendar date picker - a compact combo button opening a
        /// Monday-first month grid. State lives in the caller's
        /// year/month/day ints so draft objects survive frames untouched.
        /// </summary>
        public static bool DatePicker(string id, ref int year, ref int month, ref int day,
            float preferredWidth = -1f)
        {
            var changed = false;
            var valid = year >= 2000 && month is >= 1 and <= 12
                && day >= 1 && day <= DateTime.DaysInMonth(year, month);
            var label = valid
                ? Text.FormatDate(new DateTime(year, month, day, 0, 0, 0,
                    DateTimeKind.Unspecified))
                : "Pick a date";
            var width = preferredWidth > 0f ? preferredWidth
                : ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + 16f * SpheneUI.Scale;
            if (BeginComboBox(id, label, width))
            {
                var navKey = ImGui.GetID($"{id}nav");
                if (ImGui.IsWindowAppearing() || !_dateNav.TryGetValue(navKey, out var nav))
                {
                    nav = valid ? (year, month) : (DateTime.Now.Year, DateTime.Now.Month);
                }
                if (Icons.Button(FontAwesomeIcon.ChevronLeft))
                {
                    nav = nav.month == 1 ? (nav.year - 1, 12) : (nav.year, nav.month - 1);
                }
                ImGui.SameLine(0f, Spacing.Inline);
                Text.SectionHeader(new DateTime(nav.year, nav.month, 1, 0, 0, 0,
                    DateTimeKind.Unspecified).ToString("MMMM yyyy"));
                ImGui.SameLine(0f, Spacing.Inline);
                if (Icons.Button(FontAwesomeIcon.ChevronRight))
                {
                    nav = nav.month == 12 ? (nav.year + 1, 1) : (nav.year, nav.month + 1);
                }
                _dateNav[navKey] = nav;

                var daysInMonth = DateTime.DaysInMonth(nav.year, nav.month);
                var firstDow = ((int)new DateTime(nav.year, nav.month, 1, 0, 0, 0,
                    DateTimeKind.Unspecified).DayOfWeek + 6) % 7;
                var today = DateTime.Now.Date;
                var cell = new Vector2(30f * SpheneUI.Scale, 0f);
                Layout.VerticalSpace(Spacing.X4);
                if (ImGui.BeginTable($"{id}grid", 7, ImGuiTableFlags.SizingFixedSame))
                {
                    foreach (var wd in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
                    {
                        ImGui.TableNextColumn();
                        Text.Muted(wd);
                    }
                    for (var pad = 0; pad < firstDow; pad++)
                    {
                        ImGui.TableNextColumn();
                    }
                    for (var d = 1; d <= daysInMonth; d++)
                    {
                        ImGui.TableNextColumn();
                        var isSel = valid && nav.year == year && nav.month == month && d == day;
                        var isToday = nav.year == today.Year && nav.month == today.Month
                            && d == today.Day;
                        ImGui.PushID($"{id}d{d}");
                        var col = isSel ? Colors.Accent
                            : isToday ? Colors.TextPrimary : Colors.TextSecondary;
                        Text.Colored(d.ToString("00"), col);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        }
                        if (ImGui.IsItemClicked())
                        {
                            year = nav.year;
                            month = nav.month;
                            day = d;
                            changed = true;
                            ImGui.CloseCurrentPopup();
                        }
                        if (isToday) AttachTooltip("Today");
                        _ = cell;
                        ImGui.PopID();
                    }
                    ImGui.EndTable();
                }
                if (Icons.Button(FontAwesomeIcon.ChevronUp)) nav.year++;
                ImGui.SameLine(0f, Spacing.Inline);
                Text.Muted($"Year {nav.year}");
                ImGui.SameLine(0f, Spacing.Inline);
                if (Icons.Button(FontAwesomeIcon.ChevronDown)) nav.year--;
                _dateNav[navKey] = nav;
                EndComboBox();
            }
            return changed;
        }

        private static readonly Dictionary<uint, (int year, int month)> _dateNav = new();

        /// <summary>
        /// Time selector - hour (00-23) and minute (5-minute steps plus the
        /// exact current minute so editing never silently shifts it).
        /// </summary>
        public static bool TimePicker(string id, ref int hour, ref int minute,
            float preferredWidth = -1f)
        {
            var changed = false;
            var w = preferredWidth > 0f ? preferredWidth * 0.5f : 56f * SpheneUI.Scale;
            if (BeginComboBox($"{id}h", hour.ToString("00"), w))
            {
                for (var h = 0; h < 24; h++)
                {
                    if (Selectable(h.ToString("00"), h == hour))
                    {
                        hour = h;
                        changed = true;
                    }
                }
                EndComboBox();
            }
            ImGui.SameLine(0f, Spacing.X2);
            Text.Colored(":", Colors.TextMuted);
            ImGui.SameLine(0f, Spacing.X2);
            if (BeginComboBox($"{id}m", minute.ToString("00"), w))
            {
                for (var m = 0; m < 60; m += 5)
                {
                    if (Selectable(m.ToString("00"), m == minute))
                    {
                        minute = m;
                        changed = true;
                    }
                }
                if (minute % 5 != 0
                    && Selectable(minute.ToString("00"), true))
                {
                    changed = true;
                }
                EndComboBox();
            }
            return changed;
        }

        /// <summary>
        /// Searchable time zone selector over the system's recognized zones.
        /// Option labels carry the DST-correct UTC offset computed against
        /// <paramref name="forLocalDate"/> (the event's date) - never a fixed
        /// offset. The button preview reads "Id (UTC±hh:mm)".
        /// </summary>
        public static bool TimezonePicker(string id, ref string tzId, DateTime forLocalDate,
            float preferredWidth = -1f)
        {
            var changed = false;
            var current = TryZone(tzId) ?? TimeZoneInfo.Local;
            var preview = $"{ZoneFriendly(current)}  ({OffsetLabel(current, forLocalDate)})";
            var width = preferredWidth > 0f ? preferredWidth
                : MathF.Min(ImGui.CalcTextSize(preview).X + ImGui.GetFrameHeight() + 16f * SpheneUI.Scale,
                    360f * SpheneUI.Scale);
            if (BeginComboBox(id, preview, width))
            {
                var filterKey = ImGui.GetID($"{id}f");
                var filter = _tzFilter.TryGetValue(filterKey, out var f) ? f : string.Empty;
                if (ImGui.IsWindowAppearing())
                {
                    ImGui.SetKeyboardFocusHere();
                }
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.InputTextWithHint($"##{id}filter", "Search time zones…", ref filter, 96))
                {
                    _tzFilter[filterKey] = filter;
                }
                Layout.VerticalSpace(Spacing.X4);
                var shown = 0;
                foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
                {
                    if (!string.IsNullOrEmpty(filter)
                        && !tz.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !tz.StandardName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !tz.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (shown++ >= 120) break;
                    var label = $"{ZoneFriendly(tz)} - {tz.Id} ({OffsetLabel(tz, forLocalDate)})";
                    if (Selectable(label, string.Equals(tz.Id, current.Id, StringComparison.Ordinal)))
                    {
                        tzId = tz.Id;
                        changed = true;
                        _tzFilter.Remove(filterKey);
                        ImGui.CloseCurrentPopup();
                    }
                }
                if (shown == 0)
                {
                    Text.Muted("No matching time zone.");
                }
                EndComboBox();
            }
            AttachTooltip("Recurring series keep this anchor across daylight-saving changes.");
            return changed;
        }

        private static readonly Dictionary<uint, string> _tzFilter = new();

        private static TimeZoneInfo? TryZone(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
            catch (TimeZoneNotFoundException) { return null; }
            catch (InvalidTimeZoneException) { return null; }
        }

        /// <summary>Human-facing zone name: the localized display name when
        /// it carries city/region text, the standard name otherwise, the raw
        /// id only as last resort.</summary>
        public static string ZoneFriendly(TimeZoneInfo tz)
        {
            var display = tz.DisplayName;
            if (!string.IsNullOrWhiteSpace(display))
            {
                // "(UTC+01:00) Amsterdam, Berlin, …" - strip the offset prefix,
                // the live offset is appended separately.
                var close = display.IndexOf(')');
                if (display.StartsWith("(UTC", StringComparison.Ordinal) && close > 0
                    && close + 1 < display.Length)
                {
                    return display[(close + 1)..].Trim();
                }
                return display;
            }
            return !string.IsNullOrWhiteSpace(tz.StandardName) ? tz.StandardName : tz.Id;
        }

        private static string OffsetLabel(TimeZoneInfo tz, DateTime forLocalDate)
        {
            var probe = DateTime.SpecifyKind(forLocalDate, DateTimeKind.Unspecified);
            var off = tz.GetUtcOffset(probe);
            var sign = off < TimeSpan.Zero ? '-' : '+';
            var abs = off.Duration();
            return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
        }
    }
}
