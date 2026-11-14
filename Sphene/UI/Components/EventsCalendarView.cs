using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Month-grid calendar for community events - occurrences are expanded
/// server-side (recurring series + exceptions resolved), the component owns
/// month navigation and lazy per-window fetching. The fetch delegate is
/// injected by the host so the component stays transport-agnostic.
/// </summary>
internal sealed class EventsCalendarView
{
    private int _year = DateTime.UtcNow.Year;
    private int _month = DateTime.UtcNow.Month;
    private int _selectedDay;
    private List<SyncshellEventOccurrenceDto>? _occurrences;
    private bool _loading;
    private int _loadedYear;
    private int _loadedMonth;

    /// <summary>Server fetch: [fromUtc, toUtc) → expanded occurrences.</summary>
    public Func<DateTime, DateTime, Task<List<SyncshellEventOccurrenceDto>?>>? Fetch;

    /// <summary>Invalidates the loaded window - next draw refetches.</summary>
    public void Invalidate()
    {
        _occurrences = null;
        _loading = false;
    }

    /// <summary>Draws the month grid plus the selected day's occurrence list.</summary>
    /// <param name="accent">Shell accent for day dots + selection.</param>
    /// <param name="onOpen">Called with the occurrence's event id on click.</param>
    /// <param name="onException">Staff hook on recurring occurrences - the host
    /// writes an exception row (cancel/restore) for the picked date.</param>
    /// <param name="onEditOccurrence">Staff hook on recurring occurrences -
    /// opens the per-date composer (title/time/destination overrides).</param>
    public void Draw(Vector4 accent, Action<long>? onOpen = null,
        Action<SyncshellEventOccurrenceDto>? onException = null,
        Action<SyncshellEventOccurrenceDto>? onEditOccurrence = null)
    {
        var scale = SpheneUI.Scale;
        var today = DateTime.UtcNow.Date;

        // Month navigation.
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronLeft))
        {
            (_year, _month) = _month == 1 ? (_year - 1, 12) : (_year, _month - 1);
            Invalidate();
        }
        SpheneUI.AttachTooltip("Previous month");
        ImGui.SameLine();
        var label = new DateTime(_year, _month, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        SpheneUI.Text.SectionHeader(label);
        ImGui.SameLine();
        if (SpheneUI.Icons.Button(FontAwesomeIcon.ChevronRight))
        {
            (_year, _month) = _month == 12 ? (_year + 1, 1) : (_year, _month + 1);
            Invalidate();
        }
        SpheneUI.AttachTooltip("Next month");
        ImGui.SameLine();
        var nowW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.CalendarDay, "Today");
        SpheneUI.Controls.DockToRightEdge(nowW);
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.CalendarDay, "Today"))
        {
            _year = today.Year;
            _month = today.Month;
            _selectedDay = today.Day;
            Invalidate();
        }

        EnsureLoaded();

        if (_loading)
        {
            SpheneUI.Text.Muted("Loading calendar...");
            return;
        }

        var byDay = new Dictionary<int, List<SyncshellEventOccurrenceDto>>();
        if (_occurrences != null)
        {
            foreach (var occ in _occurrences)
            {
                var local = occ.OccurrenceStartUtc.ToLocalTime().Date;
                if (local.Year != _year || local.Month != _month) continue;
                if (!byDay.TryGetValue(local.Day, out var list))
                {
                    byDay[local.Day] = list = [];
                }
                list.Add(occ);
            }
        }

        // Day grid · 7 columns, weekday header, accent dots for event days.
        var daysInMonth = DateTime.DaysInMonth(_year, _month);
        var firstDow = ((int)new DateTime(_year, _month, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .DayOfWeek + 6) % 7; // Monday-first
        using var table = SpheneUI.Tables.Begin("##events-calendar", 7,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoBordersInBody);
        if (!table.Success) return;

        foreach (var wd in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
        {
            SpheneUI.Tables.NextColumn();
            SpheneUI.Text.Muted(wd);
        }

        var cell = 0;
        for (var pad = 0; pad < firstDow; pad++, cell++)
        {
            SpheneUI.Tables.NextColumn();
        }
        var dl = ImGui.GetWindowDrawList();
        for (var day = 1; day <= daysInMonth; day++, cell++)
        {
            SpheneUI.Tables.NextColumn();
            var isToday = _year == today.Year && _month == today.Month && day == today.Day;
            var isSelected = day == _selectedDay;
            var hasEvents = byDay.ContainsKey(day);

            ImGui.PushID(day);
            var clicked = ImGui.InvisibleButton($"##day", new Vector2(-1f, 26f * scale));
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var hovered = ImGui.IsItemHovered();
            if (isSelected)
            {
                dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(accent, 0.25f)), 6f * scale);
            }
            else if (hovered)
            {
                dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(accent, 0.12f)), 6f * scale);
            }
            var text = day.ToString();
            var size = ImGui.CalcTextSize(text);
            var color = isToday ? accent
                : hasEvents ? SpheneUI.Colors.TextPrimary
                : SpheneUI.Colors.TextMuted;
            dl.AddText(new Vector2(min.X + (max.X - min.X - size.X) * 0.5f, min.Y + 4f * scale),
                SpheneUI.ColorUtils.ToU32(color), text);
            if (hasEvents)
            {
                var dotY = max.Y - 5f * scale;
                var cx = min.X + (max.X - min.X) * 0.5f;
                dl.AddCircleFilled(new Vector2(cx, dotY), 2f * scale,
                    SpheneUI.ColorUtils.ToU32(accent));
            }
            if (clicked)
            {
                _selectedDay = day;
            }
            ImGui.PopID();
        }

        // Selected day's occurrence list.
        if (_selectedDay >= 1 && _selectedDay <= daysInMonth
            && byDay.TryGetValue(_selectedDay, out var dayOccs))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Text.SectionHeader(
                new DateTime(_year, _month, _selectedDay, 0, 0, 0, DateTimeKind.Local)
                    .ToString("dddd, MMM d", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var occ in dayOccs.OrderBy(o => o.OccurrenceStartUtc))
            {
                var start = occ.OccurrenceStartUtc.ToLocalTime();
                var title = occ.IsCancelled
                    ? $"{occ.Title} (cancelled)"
                    : occ.Title;
                var meta = occ.OccurrenceEndUtc.HasValue
                    ? $"{start:HH:mm} - {occ.OccurrenceEndUtc.Value.ToLocalTime():HH:mm}"
                    : $"{start:HH:mm}";
                if (!string.IsNullOrWhiteSpace(occ.Location))
                {
                    meta += $" · {occ.Location}";
                }
                if (occ.IsException && !occ.IsCancelled)
                {
                    meta += " · moved";
                }
                var captured = occ;
                SpheneUI.Rows.Nav(title, meta, () => onOpen?.Invoke(captured.EventId),
                    occ.IsCancelled ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.CalendarDay);

                if ((onException != null || onEditOccurrence != null) && occ.SeriesIndex >= 0)
                {
                    ImGui.SameLine();
                    ImGui.PushID($"ex-{occ.EventId}-{occ.OccurrenceStartUtc.Ticks}");
                    if (onEditOccurrence != null && !occ.IsCancelled)
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Pen))
                        {
                            onEditOccurrence(occ);
                        }
                        SpheneUI.AttachTooltip("Edit this date");
                    }
                    if (onException != null)
                    {
                        if (SpheneUI.Icons.Button(occ.IsCancelled
                                ? FontAwesomeIcon.Undo : FontAwesomeIcon.Ban))
                        {
                            onException(occ);
                        }
                        SpheneUI.AttachTooltip(occ.IsCancelled
                            ? "Restore this date" : "Cancel this date");
                    }
                    ImGui.PopID();
                }
            }
        }
        else if (_selectedDay >= 1 && _selectedDay <= daysInMonth)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Text.Muted("No events on this day.");
        }
    }

    /// <summary>Fetches the visible month's occurrences once per window change.</summary>
    private void EnsureLoaded()
    {
        if (Fetch == null || _loading || (_occurrences != null
            && _loadedYear == _year && _loadedMonth == _month))
        {
            return;
        }
        _loading = true;
        _loadedYear = _year;
        _loadedMonth = _month;
        // ±1 month padding covers local-time edges at the window borders.
        var from = new DateTime(_year, _month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var to = new DateTime(_year, _month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(2);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Fetch(from, to).ConfigureAwait(false);
                _occurrences = result ?? [];
            }
            catch
            {
                _occurrences = [];
            }
            finally
            {
                _loading = false;
            }
        });
    }
}
