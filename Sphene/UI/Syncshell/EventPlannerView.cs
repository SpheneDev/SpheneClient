using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.UI.Design;
using System.Numerics;

// The shared community contract types are nested in SyncshellCommunityView -
// short aliases keep the planner's signatures readable.
using CommunityPageState = Sphene.UI.Syncshell.SyncshellCommunityView.CommunityPageState;
using SyncshellCommunityEdit = Sphene.UI.Syncshell.SyncshellCommunityView.SyncshellCommunityEdit;
using CommunityTravelHooks = Sphene.UI.Syncshell.SyncshellCommunityView.CommunityTravelHooks;
using CommunityEventComposer = Sphene.UI.Syncshell.SyncshellCommunityView.CommunityEventComposer;

namespace Sphene.UI.Syncshell;

/// <summary>
/// The Event Planner - the dedicated management workspace for syncshell
/// events. Two modes share the rail tab: the <b>overview</b> (filters,
/// list/calendar, selected-event summary) and the <b>editor</b>, which
/// takes over the full content area with a wide main form plus a narrow
/// inspector column. Mode is derived from the composer's open flag -
/// opening a draft anywhere on the host flips the page into the editor.
/// Rendered only for viewers the host authorized (owners/staff, event
/// authors, organizers, member submitters) - every action re-checks the
/// <see cref="SyncshellCommunityEdit"/> contract.
/// </summary>
internal static class EventPlannerView
{
    /// <summary>Planner-local description composer - separate instance so
    /// preview/upload state never leaks into other surfaces.</summary>
    private static readonly UI.Components.MarkdownComposer PlannerBodyComposer
        = new("planner") { ShowPreview = false };

    /// <summary>Fixed label column inside editor sections - every labeled
    /// input starts at the same X offset so rows stay aligned.</summary>
    private static float LabelColW => 118f * SpheneUI.Scale;

    /// <summary>Per-page planner state - overview filters, selection,
    /// calendar mode and the per-event occurrence-picker cache.</summary>
    internal sealed class EventPlannerState
    {
        /// <summary>Free-text filter over title + location.</summary>
        public string Filter = string.Empty;
        /// <summary>Scope pill: 0 Upcoming · 1 Past · 2 Pending review · 3 All.</summary>
        public int Scope;
        /// <summary>Overview mode: 0 list · 1 calendar.</summary>
        public int ViewMode;
        /// <summary>Row the summary panel shows - set by list/calendar clicks.</summary>
        public long? SelectedEventId;
        /// <summary>The overview's own calendar instance - shares only the
        /// host's fetch hook, not the community tab's selection state.</summary>
        public readonly UI.Components.EventsCalendarView Calendar = new();
        /// <summary>Occurrence picker cache - (event id → loaded occurrence rows).</summary>
        public readonly Dictionary<long, List<SyncshellEventOccurrenceDto>> Occurrences = new();
    }

    /// <summary>
    /// Draws the planner workspace for <paramref name="page"/>.
    /// <paramref name="events"/> are the community DTO's event set (already
    /// visibility-filtered server-side); <paramref name="travel"/> supplies
    /// the destination pickers - optional, like on the community surface.
    /// </summary>
    public static void Draw(CommunityPageState page, SyncshellCommunityEdit edit,
        CommunityTravelHooks? travel, IReadOnlyList<SyncshellEventDto> events,
        Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var composer = page.EventComposer;
        if (composer.Open)
        {
            DrawEditorMode(page, edit, travel, events, scale);
        }
        else
        {
            DrawOverviewMode(page, edit, travel, events, accent, scale);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Mode A - Planner overview
    // ─────────────────────────────────────────────────────────────

    private static void DrawOverviewMode(CommunityPageState page,
        SyncshellCommunityEdit edit, CommunityTravelHooks? travel,
        IReadOnlyList<SyncshellEventDto> events, Vector4 accent, float scale)
    {
        var planner = page.Planner;

        // Header - workspace identity + the one creation affordance.
        using (ImRaii.Group())
        {
            SpheneUI.Text.SectionHeader("Event Planner");
            ImGui.SameLine(0f, SpheneUI.Spacing.X8);
            SpheneUI.Text.Muted("Manage events for this Syncshell");
            var canCreate = CanCreate(edit);
            var bw = SpheneUI.Controls.SegAutoWidth("+  Create event",
                null, ImGui.GetFrameHeight());
            SpheneUI.Controls.DockToRightEdge(bw);
            using (ImRaii.Disabled(!canCreate))
            {
                if (SpheneUI.Controls.PrimaryButton("+  Create event",
                        enabled: canCreate))
                {
                    page.EventComposer.OpenNew();
                }
            }
            if (!canCreate)
            {
                SpheneUI.AttachTooltip("You don't have event permissions here.");
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        // Toolbar - scope pills, search, list/calendar switch.
        var scopes = new[] { "Upcoming", "Past", "Pending", "All" };
        SpheneUI.Controls.Segmented("##ep-scope", ref planner.Scope, scopes);
        ImGui.SameLine(0f, SpheneUI.Spacing.X12);
        SpheneUI.Controls.InputTextWithHint("##ep-search", "Search events...",
            ref planner.Filter, 96, preferredWidth: 200f * scale);
        var modes = new[] { "List", "Calendar" };
        var mw = SpheneUI.Controls.SegmentedWidth(modes);
        if (SpheneUI.Controls.SameLineIfFits(mw + SpheneUI.Spacing.X12))
        {
            SpheneUI.Controls.DockToRightEdge(mw);
        }
        SpheneUI.Controls.Segmented("##ep-viewmode", ref planner.ViewMode, modes,
            width: mw);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        // Content - managed rows (or the calendar) + the selected-event
        // summary side by side; stacked below ~680px.
        var availX = SpheneUI.Controls.BoundedAvailX();
        var wide = availX >= 680f * scale;
        var listW = wide ? MathF.Min(availX * 0.60f, availX - 300f * scale) : availX;
        if (planner.ViewMode == 1)
        {
            planner.Calendar.Fetch = edit.LoadCalendar;
            using var _ = ImRaii.Child("##ep-cal", new Vector2(listW, 0f), false);
            planner.Calendar.Draw(accent,
                onOpen: id => planner.SelectedEventId = id,
                onEditOccurrence: occ =>
                {
                    var ev = events.FirstOrDefault(e => e.Id == occ.EventId);
                    if (ev == null || edit.CanEditEvent?.Invoke(ev) != true) return;
                    planner.SelectedEventId = ev.Id;
                    page.EventComposer.OpenOccurrence(ev, occ);
                });
        }
        else
        {
            DrawOverviewList(page, edit, events, accent, listW, scale);
        }
        if (wide)
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.CardGap);
            using var _ = ImRaii.Child("##ep-summary", Vector2.Zero, false);
            DrawSelectedSummary(page, edit, travel, events);
        }
        else
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawSelectedSummary(page, edit, travel, events);
        }
    }

    /// <summary>Filtered, scope-sorted managed rows - click selects for the
    /// summary panel; editing happens through the panel's Edit button so a
    /// single click never throws the user into a form.</summary>
    private static void DrawOverviewList(CommunityPageState page,
        SyncshellCommunityEdit edit, IReadOnlyList<SyncshellEventDto> events,
        Vector4 accent, float listW, float scale)
    {
        var rows = Filtered(page.Planner, events);
        var canCreate = CanCreate(edit);
        using var _ = ImRaii.Child("##ep-list", new Vector2(listW, 0f), false);
        if (rows.Count == 0)
        {
            var (title, hint) = EmptyCopy(page.Planner);
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.CalendarTimes, title, hint,
                actionLabel: canCreate ? "Create event" : null,
                onAction: canCreate ? () => page.EventComposer.OpenNew() : null,
                actionIcon: FontAwesomeIcon.CalendarPlus, accent: accent);
            return;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var ev = rows[i];
            ImGui.PushID($"ep-ev-{ev.Id}-{i}");
            if (PlannerEventRow(ev, page.Planner.SelectedEventId == ev.Id,
                    accent, scale))
            {
                page.Planner.SelectedEventId = ev.Id;
            }
            ImGui.PopID();
            if (i < rows.Count - 1)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
        }
    }

    /// <summary>Scope + text filtering for the overview list.</summary>
    private static List<SyncshellEventDto> Filtered(EventPlannerState planner,
        IReadOnlyList<SyncshellEventDto> events)
    {
        var now = DateTime.UtcNow;
        var needle = planner.Filter;
        var rows = events.Where(e =>
        {
            if (!string.IsNullOrEmpty(needle)
                && !e.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !(e.Location?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return false;
            }
            var past = (e.EndUtc ?? e.StartUtc) < now.AddHours(-4);
            var pending = e.PublishStatus != (int)ContentPublishStatus.Published;
            return planner.Scope switch
            {
                1 => past,
                2 => pending,
                3 => true,
                _ => !past,
            };
        });
        return planner.Scope is 1
            ? rows.OrderByDescending(e => e.StartUtc).ToList()
            : rows.OrderBy(e => e.StartUtc).ToList();
    }

    private static (string Title, string Hint) EmptyCopy(EventPlannerState planner)
        => planner.Scope switch
        {
            1 => ("No past events", "Nothing here yet - past events land in this list."),
            2 => ("Nothing pending review", "Drafts and submitted events awaiting staff review appear here."),
            3 => ("No events", "This Syncshell hasn't hosted an event yet."),
            _ => ("No upcoming events", "Plan the next meetup - it shows up here and on the community page."),
        };

    /// <summary>A managed-event row - calendar plate, title, time ·
    /// location meta and a right cluster with the lifecycle pill plus
    /// attendance. Selected rows get an accent edge + border.</summary>
    private static bool PlannerEventRow(SyncshellEventDto ev, bool selected,
        Vector4 accent, float scale)
    {
        var w = SpheneUI.Controls.BoundedAvailX();
        var plate = 40f * scale;
        var pad = 8f * scale;
        var h = 52f * scale;

        var clicked = ImGui.InvisibleButton("##row", new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        var dl = ImGui.GetWindowDrawList();
        var radius = SpheneUI.Metrics.ButtonRadius;
        var life = EventLifecycle(ev, DateTime.UtcNow);
        var bg = selected
            ? SpheneUI.ColorUtils.WithAlpha(accent, 0.12f)
            : SpheneUI.Layout.NavRailBandColor();
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(bg), radius);
        SpheneUI.Fx.HoverLift(dl, min, max,
            SpheneUI.Fx.HoverLiftT(ImGui.GetID("##lift"), min, max), radius, accent);
        if (selected)
        {
            dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(accent, 0.75f)),
                radius, ImDrawFlags.RoundCornersAll, MathF.Max(1f, 1.25f * scale));
        }

        // Date badge - a flat tint fill + hairline in the lifecycle color,
        // so the state survives selection without a saturated tile.
        var local = ev.StartUtc.ToLocalTime();
        var plateMin = new Vector2(min.X + pad, min.Y + (h - plate) * 0.5f);
        dl.AddRectFilled(plateMin, plateMin + new Vector2(plate, plate),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(life.Plate, 0.10f)),
            6f * scale);
        dl.AddRect(plateMin, plateMin + new Vector2(plate, plate),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(life.Plate, 0.5f)),
            6f * scale, ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
        var pcx = plateMin.X + plate * 0.5f;
        var day = local.ToString("dd");
        using (SpheneUI.HeadingFont.Push())
        {
            var ds = ImGui.CalcTextSize(day);
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
                new Vector2(pcx - ds.X * 0.5f, plateMin.Y + plate * 0.30f - ds.Y * 0.5f),
                ImGui.GetColorU32(ImGuiCol.Text), day);
        }
        var month = local.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture)
            .ToUpperInvariant();
        var ms = ImGui.CalcTextSize(month);
        dl.AddText(new Vector2(pcx - ms.X * 0.5f, plateMin.Y + plate * 0.68f - ms.Y * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(life.Plate, 0.85f)), month);

        // Right cluster - lifecycle pill over the attendance line.
        var attend = ev.AttendingCount > 0 ? $"{ev.AttendingCount} going" : null;
        var rightClusterW = MathF.Max(
            ImGui.CalcTextSize(life.Text).X + 12f * scale,
            attend != null ? ImGui.CalcTextSize(attend).X : 0f);
        var lineH = ImGui.GetTextLineHeight();
        var lw = ImGui.CalcTextSize(life.Text).X + 12f * scale;
        var lh = lineH + 4f * scale;
        var lmin = new Vector2(max.X - pad - lw, min.Y + (h - lineH * 2 - 4f * scale) * 0.5f);
        dl.AddRectFilled(lmin, lmin + new Vector2(lw, lh),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(life.Color, 0.2f)),
            lh * 0.5f);
        dl.AddText(lmin + new Vector2(6f * scale, 2f * scale),
            SpheneUI.ColorUtils.ToU32(life.Color), life.Text);
        if (attend != null)
        {
            var aw = ImGui.CalcTextSize(attend).X;
            dl.AddText(new Vector2(max.X - pad - aw, min.Y + h * 0.5f + 4f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), attend);
        }

        var textX = plateMin.X + plate + pad;
        var textRight = max.X - pad - (rightClusterW > 0f ? rightClusterW + pad * 0.5f : 0f);
        var meta = $"{local:HH:mm}"
            + (ev.EndUtc.HasValue ? $" - {ev.EndUtc.Value.ToLocalTime():HH:mm}" : string.Empty)
            + (!string.IsNullOrWhiteSpace(ev.Location) ? $" · {ev.Location}" : string.Empty);
        dl.PushClipRect(new Vector2(textX, min.Y), new Vector2(textRight, max.Y), true);
        var title = SpheneUI.Text.Ellipsize(ev.Title, MathF.Max(40f, textRight - textX));
        var titleCol = life.Dimmed
            ? SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                  SpheneUI.Colors.TextPrimary, 0.72f))
            : ImGui.GetColorU32(ImGuiCol.Text);
        dl.AddText(new Vector2(textX, min.Y + (h - lineH * 2 - 4f * scale) * 0.5f),
            titleCol, title);
        dl.AddText(new Vector2(textX, min.Y + h * 0.5f + 4f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
            SpheneUI.Text.Ellipsize(meta, MathF.Max(40f, textRight - textX)));
        dl.PopClipRect();

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        return clicked;
    }

    /// <summary>Lifecycle → row styling: pill text+color, date-plate tint and
    /// dimmed title for finished states. Selection is drawn independently
    /// (outline+fill) and never touches these slots. The effective end
    /// mirrors the list/calendar rule (<c>EndUtc ?? StartUtc + 4h</c>) so an
    /// end-less event is "Live" for four hours, not indefinitely.</summary>
    internal sealed record RowLifecycle(string Text, Vector4 Color, Vector4 Plate,
        bool Dimmed);

    internal static RowLifecycle EventLifecycle(SyncshellEventDto ev, DateTime nowUtc)
        => PlannerEventLifecycle.Classify(ev, nowUtc) switch
        {
            PlannerEventState.Cancelled => new("Cancelled", SpheneUI.Colors.Danger,
                SpheneUI.Colors.TextMuted, true),
            PlannerEventState.Draft => new("Draft", SpheneUI.Colors.TextMuted,
                SpheneUI.Colors.TextMuted, false),
            PlannerEventState.InReview => new("In review", SpheneUI.Colors.Warning,
                SpheneUI.Colors.Warning, false),
            PlannerEventState.Ended => new("Ended", SpheneUI.Colors.TextMuted,
                SpheneUI.Colors.TextMuted, true),
            PlannerEventState.Live => new("Live", SpheneUI.Colors.Success,
                SpheneUI.Colors.Success, false),
            _ => new("Upcoming", SpheneUI.Palette.CrystalBlue,
                SpheneUI.Palette.CrystalBlue, false),
        };

    /// <summary>Selected-event summary - the overview's right pane: real
    /// event metadata plus View/Edit actions.</summary>
    private static void DrawSelectedSummary(CommunityPageState page,
        SyncshellCommunityEdit edit, CommunityTravelHooks? travel,
        IReadOnlyList<SyncshellEventDto> events)
    {
        var ev = page.Planner.SelectedEventId is { } id
            ? events.FirstOrDefault(e => e.Id == id) : null;
        if (ev == null)
        {
            page.Planner.SelectedEventId = null;
            SpheneUI.Layout.HeadedSurface("Selected event", () =>
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.HandPointer,
                    "Pick an event", "Select an event on the left to see its details here.");
            }, FontAwesomeIcon.InfoCircle);
            return;
        }

        SpheneUI.Layout.HeadedSurface("Selected event", () =>
        {
            SpheneUI.Text.Big(SpheneUI.Text.Ellipsize(ev.Title,
                SpheneUI.Controls.BoundedAvailX()));
            var life = EventLifecycle(ev, DateTime.UtcNow);
            SpheneUI.Text.Colored(life.Text, life.Color);
            SummaryRow("When", WhenText(ev));
            var where = !string.IsNullOrWhiteSpace(ev.Location) ? ev.Location!
                : ev.Destination is { } d
                    ? SyncshellCommunityView.DestinationTitle(d, travel)
                    : null;
            if (where != null)
            {
                SummaryRow("Where", where);
            }
            SummaryRow("Visibility",
                ev.Visibility == (int)EventVisibility.Public ? "Public" : "Members only");
            if (ev.OrganizerUsers is { Count: > 0 } organizers)
            {
                SummaryRow("Organizers",
                    string.Join(", ", organizers.Select(o =>
                        SyncshellCommunityView.HumanName(o) ?? "Unknown user")));
            }
            var attendance = ev.MaxAttendees is { } cap
                ? $"{ev.AttendingCount} / {cap} going"
                : $"{ev.AttendingCount} going";
            SummaryRow("Attendance", attendance
                + (ev.WaitlistCount > 0 ? $" · {ev.WaitlistCount} waitlisted" : string.Empty));
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            var canEdit = edit.CanEditEvent?.Invoke(ev) == true
                && edit.OnEditEvent != null;
            using (ImRaii.Group())
            {
                using (ImRaii.Disabled(edit.OnViewEvent == null))
                {
                    if (SpheneUI.Controls.SegmentButton("View",
                            FontAwesomeIcon.Eye))
                    {
                        edit.OnViewEvent?.Invoke(ev);
                    }
                }
                ImGui.SameLine(0f, SpheneUI.Spacing.X8);
                using (ImRaii.Disabled(!canEdit))
                {
                    if (SpheneUI.Controls.SegmentButton("Edit",
                            FontAwesomeIcon.Pen))
                    {
                        page.EventComposer.OpenEdit(ev);
                    }
                }
            }
        }, FontAwesomeIcon.InfoCircle);
    }

    private static string WhenText(SyncshellEventDto ev)
    {
        var start = SpheneUI.Text.FormatDateTime(ev.StartUtc.ToLocalTime());
        var end = ev.EndUtc.HasValue
            ? $" - {ev.EndUtc.Value.ToLocalTime():HH:mm}" : string.Empty;
        var zone = ZoneName(ev.TimezoneId);
        return start + end + (zone != null ? $" · {zone}" : string.Empty);
    }

    private static string? ZoneName(string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId)) return null;
        try
        {
            return SpheneUI.Controls.ZoneFriendly(
                TimeZoneInfo.FindSystemTimeZoneById(tzId));
        }
        catch (TimeZoneNotFoundException) { return tzId; }
        catch (InvalidTimeZoneException) { return tzId; }
    }

    /// <summary>One label→value line inside the summary cards - labels in
    /// the muted column tone, values wrap.</summary>
    private static void SummaryRow(string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        SpheneUI.Text.Muted(label);
        SpheneUI.Text.Wrapped(value);
    }

    private static bool CanCreate(SyncshellCommunityEdit edit)
        => (edit.CanManageEvents || edit.CanSubmit) && edit.OnSaveEvent != null;

    // ─────────────────────────────────────────────────────────────
    // Mode B - Dedicated event editor
    // ─────────────────────────────────────────────────────────────

    private static void DrawEditorMode(CommunityPageState page,
        SyncshellCommunityEdit edit, CommunityTravelHooks? travel,
        IReadOnlyList<SyncshellEventDto> events, float scale)
    {
        var composer = page.EventComposer;
        var occurrenceEdit = composer.OccurrenceStartUtc.HasValue;

        // Breadcrumb header - Back restores the overview; the draft is
        // discarded on the way out (same semantics as the old close).
        using (ImRaii.Group())
        {
            if (SpheneUI.Controls.SegmentButton("Event Planner",
                    FontAwesomeIcon.ChevronLeft))
            {
                composer.Close();
                return;
            }
            ImGui.SameLine(0f, SpheneUI.Spacing.X12);
            SpheneUI.Text.SectionHeader(occurrenceEdit
                ? $"Edit {SpheneUI.Text.FormatShortDate(composer.OccurrenceStartUtc!.Value.ToLocalTime())} occurrence"
                : composer.EditId == 0 ? "New event" : "Edit event");
            if (edit.OnPreviewEvent != null)
            {
                var pw = SpheneUI.Controls.SegAutoWidth("Live Preview",
                    FontAwesomeIcon.Eye, ImGui.GetFrameHeight());
                SpheneUI.Controls.DockToRightEdge(pw);
                using (ImRaii.Disabled(composer.ScheduleProblem() != null))
                {
                    if (SpheneUI.Controls.SegmentButton("Live Preview",
                            FontAwesomeIcon.Eye))
                    {
                        edit.OnPreviewEvent(composer);
                    }
                }
                SpheneUI.AttachTooltip("Attendee-facing preview - nothing is saved.");
            }
        }
        if (occurrenceEdit)
        {
            SpheneUI.Text.MutedWrapped(
                "Only this date of the series changes - the rest keeps the series defaults.");
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        // Columns: main form (own scroll, Save footer pinned below it) and
        // the inspector; the inspector moves below the form when the
        // available width can't host both.
        var availX = SpheneUI.Controls.BoundedAvailX();
        var availY = ImGui.GetContentRegionAvail().Y;
        var gap = SpheneUI.Spacing.CardGap.Scaled;
        var insW = MathF.Max(240f * scale, availX * 0.33f);
        var footerH = ImGui.GetFrameHeight() + SpheneUI.Spacing.X12.Scaled;
        var formW = availX - insW - gap;
        var wide = formW >= 420f * scale;

        if (wide)
        {
            var colX = ImGui.GetCursorScreenPos().X;
            var colY = ImGui.GetCursorScreenPos().Y;
            var formH = MathF.Max(160f * scale, availY - footerH);
            var insH = MathF.Max(160f * scale, availY);

            // Each column pushes its own bound: a raw BeginChild registers
            // none, so dock helpers inside would measure the full page
            // bound - that is how the footer Save used to land at the page
            // edge and the group rect pushed the inspector off-screen.
            SpheneUI.ContentBounds.Push(new Vector2(colX, colX + formW));
            using (var _ = ImRaii.Child("##ep-form",
                   new Vector2(formW, formH), false))
            {
                DrawEditorForm(page, edit, events, scale);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            DrawEditorFooter(edit, events, composer);
            SpheneUI.ContentBounds.Pop();

            var insX = colX + formW + gap;
            ImGui.SetCursorScreenPos(new Vector2(insX, colY));
            SpheneUI.ContentBounds.Push(new Vector2(insX, insX + insW));
            using (var _ = ImRaii.Child("##ep-ins",
                   new Vector2(insW, insH), false))
            {
                DrawInspector(edit, travel, composer);
            }
            SpheneUI.ContentBounds.Pop();

            // Land below both columns so following content starts at the
            // left edge again instead of tucking under the inspector.
            var bottom = colY + MathF.Max(insH,
                formH + SpheneUI.Spacing.X4.Scaled + footerH);
            ImGui.SetCursorScreenPos(new Vector2(colX, bottom));
        }
        else
        {
            using (var _ = ImRaii.Child("##ep-form",
                   new Vector2(availX, MathF.Max(160f * scale, availY - footerH)), false))
            {
                DrawEditorForm(page, edit, events, scale);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            DrawEditorFooter(edit, events, composer);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawInspector(edit, travel, composer);
        }
    }

    /// <summary>Section cards of the main column - details, schedule,
    /// location, people, publishing. Every input is labeled and anchored to
    /// the shared <see cref="LabelCol"/> column inside its card.</summary>
    private static void DrawEditorForm(CommunityPageState page,
        SyncshellCommunityEdit edit,
        IReadOnlyList<SyncshellEventDto> events, float scale)
    {
        var composer = page.EventComposer;
        var occurrenceEdit = composer.OccurrenceStartUtc.HasValue;

        SpheneUI.Layout.HeadedSurface("Event details", () =>
        {
            LabelCol("Title");
            SpheneUI.Controls.InputTextWithHint("##ep-title", "Name the event...",
                ref composer.Title, 200,
                preferredWidth: SpheneUI.Controls.BoundedAvailX());
            LabelCol("Link");
            SpheneUI.Controls.InputTextWithHint("##ep-url", "https://... (optional)",
                ref composer.Url, 500,
                preferredWidth: SpheneUI.Controls.BoundedAvailX());
            SpheneUI.Text.Muted("Description");
            PlannerBodyComposer.MentionProvider =
                () => edit.MentionCandidates?.Invoke() ?? [];
            PlannerBodyComposer.Draw("##ep-desc", ref composer.Description, 4000,
                96f * scale);
            if (edit.OnPreviewEvent != null)
            {
                SpheneUI.Text.Muted("See how your event will appear to attendees -");
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Eye, "Live Preview"))
                {
                    edit.OnPreviewEvent(composer);
                }
            }
            if (UI.Components.MarkdownComposer.StudioPicker != null
                || UI.Components.MarkdownComposer.ImagePicker != null)
            {
                void PickInto(Services.Mediator.PhotoStudioTarget target, string label,
                    string tip, byte[]? picked, string? existing, bool cleared,
                    Action<byte[]?> apply, Action<bool> setCleared)
                {
                    var hasImage = picked != null || (existing != null && !cleared);
                    var state = picked != null ? "picked" : hasImage ? "set" : "...";
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Image, $"{label} {state}"))
                    {
                        var pending = UI.Components.MarkdownComposer.StudioPicker?.Invoke(target)
                            ?? UI.Components.MarkdownComposer.ImagePicker!();
                        _ = Task.Run(async () =>
                        {
                            var bytes = await pending.ConfigureAwait(false);
                            apply(bytes);
                            if (bytes != null) setCleared(false);
                        });
                    }
                    SpheneUI.AttachTooltip(tip);
                    if (hasImage)
                    {
                        ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Clear"))
                        {
                            apply(null);
                            setCleared(true);
                        }
                        SpheneUI.AttachTooltip($"Remove the stored {label.ToLowerInvariant()}");
                    }
                }
                PickInto(Services.Mediator.PhotoStudioTarget.EventBanner, "Event banner",
                    "Optional banner for the event header - cropped to 1200×400 (3:1)",
                    composer.CoverBytes, composer.ExistingCoverKey, composer.CoverCleared,
                    b => { composer.CoverBytes = b; composer.CoverContentType = b != null ? "image/png" : null; },
                    v => composer.CoverCleared = v);
                ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                PickInto(Services.Mediator.PhotoStudioTarget.EventPreview, "Preview image",
                    "Optional image for event lists and cards - cropped to 800×450 (16:9)",
                    composer.PreviewBytes, composer.ExistingPreviewKey, composer.PreviewCleared,
                    b => { composer.PreviewBytes = b; composer.PreviewContentType = b != null ? "image/png" : null; },
                    v => composer.PreviewCleared = v);
            }
        }, FontAwesomeIcon.Edit);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        DrawScheduleSection(page, composer, edit, events, scale);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.HeadedSurface("Location", () =>
        {
            LabelCol("Venue");
            SpheneUI.Controls.InputTextWithHint("##ep-loc",
                "Where attendees meet - e.g. The Quicksand, Ul'dah",
                ref composer.Location, 200,
                preferredWidth: SpheneUI.Controls.BoundedAvailX());
            if (edit.Travel != null)
            {
                SpheneUI.Layout.SettingsGroup("Travel destination",
                    FontAwesomeIcon.MapMarkedAlt);
                SyncshellCommunityView.DrawEventDestinationEditor(composer, edit,
                    occurrenceEdit, scale, showHeader: false);
            }
        }, FontAwesomeIcon.MapMarkerAlt);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        DrawPeopleSection(composer, edit);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        DrawPublishingSection(composer, edit, events, occurrenceEdit);
    }

    /// <summary>Label left of a control row - aligns every field to a
    /// shared column inside the card. Returns nothing; the caller keeps
    /// drawing controls on the same line.</summary>
    private static void LabelCol(string label)
    {
        var bound = SpheneUI.Layout.ContentLeftBound;
        var left = bound > 0f
            ? bound - ImGui.GetWindowPos().X
            : ImGui.GetWindowContentRegionMin().X;
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Muted(label);
        ImGui.SameLine(0f, 0f);
        ImGui.SetCursorPosX(left + LabelColW);
    }

    /// <summary>Schedule card - labeled structured pickers, zone-aware
    /// ambiguity choice, recurrence and reminders.</summary>
    private static void DrawScheduleSection(CommunityPageState page,
        CommunityEventComposer composer,
        SyncshellCommunityEdit edit, IReadOnlyList<SyncshellEventDto> events,
        float scale)
    {
        SpheneUI.Layout.HeadedSurface("Schedule", () =>
        {
            var forOffset = new DateTime(
                composer.StartYear < 2000 ? DateTime.Now.Year : composer.StartYear,
                Math.Clamp(composer.StartMonth, 1, 12), Math.Clamp(composer.StartDay, 1, 28),
                composer.StartHour, composer.StartMinute, 0, DateTimeKind.Unspecified);

            LabelCol("Starts");
            SpheneUI.Controls.DatePicker("##ep-sd", ref composer.StartYear,
                ref composer.StartMonth, ref composer.StartDay,
                preferredWidth: 150f * scale);
            SpheneUI.Controls.SameLineIfFits(110f * scale);
            SpheneUI.Controls.TimePicker("##ep-st", ref composer.StartHour,
                ref composer.StartMinute, preferredWidth: 110f * scale);

            LabelCol("Ends");
            if (composer.HasEnd)
            {
                SpheneUI.Controls.DatePicker("##ep-ed", ref composer.EndYear,
                    ref composer.EndMonth, ref composer.EndDay,
                    preferredWidth: 150f * scale);
                SpheneUI.Controls.SameLineIfFits(110f * scale);
                SpheneUI.Controls.TimePicker("##ep-et", ref composer.EndHour,
                    ref composer.EndMinute, preferredWidth: 110f * scale);
                SpheneUI.Controls.SameLineIfFits(ImGui.GetFrameHeight());
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
                {
                    composer.HasEnd = false;
                }
                SpheneUI.AttachTooltip("Remove the end time");
            }
            else
            {
                var hasEnd = false;
                if (SpheneUI.Controls.Checkbox("Add an end time", ref hasEnd)
                    && hasEnd)
                {
                    composer.HasEnd = true;
                    var e = new DateTime(
                        Math.Max(composer.StartYear, 2000),
                        Math.Clamp(composer.StartMonth, 1, 12),
                        Math.Clamp(composer.StartDay, 1, 28),
                        composer.StartHour, composer.StartMinute, 0,
                        DateTimeKind.Unspecified).AddHours(1);
                    (composer.EndYear, composer.EndMonth, composer.EndDay) =
                        (e.Year, e.Month, e.Day);
                    (composer.EndHour, composer.EndMinute) = (e.Hour, e.Minute);
                }
            }

            LabelCol("Time zone");
            SpheneUI.Controls.TimezonePicker("##ep-tz", ref composer.TimezoneId,
                forOffset,
                preferredWidth: MathF.Min(440f * scale,
                    SpheneUI.Controls.BoundedAvailX()));

            if (composer.AmbiguousOffsets() is { } amb)
            {
                LabelCol("DST overlap");
                using (ImRaii.Group())
                {
                    if (SpheneUI.Controls.Selectable(
                            $"Earlier - {amb.EarlierUtc:HH:mm} UTC",
                            composer.AmbiguousChoice == 0))
                    {
                        composer.AmbiguousChoice = 0;
                    }
                    if (SpheneUI.Controls.Selectable(
                            $"Later - {amb.LaterUtc:HH:mm} UTC",
                            composer.AmbiguousChoice == 1))
                    {
                        composer.AmbiguousChoice = 1;
                    }
                    SpheneUI.Text.MutedWrapped(
                        "That local time occurs twice on the daylight-saving switch - pick which instant it means.");
                }
            }

            if (!composer.OccurrenceStartUtc.HasValue)
            {
                var recurrence = new[]
                {
                    "Does not repeat", "Daily", "Weekly", "Every 2 weeks", "Monthly",
                };
                LabelCol("Repeats");
                SpheneUI.Controls.Combo("##ep-rec", recurrence, s => s ?? string.Empty,
                    onSelected: p => composer.Recurrence = p is string s
                        ? Array.IndexOf(recurrence, s) : 0,
                    initialSelectedItem: recurrence[Math.Clamp(composer.Recurrence, 0,
                        recurrence.Length - 1)],
                    preferredWidth: 180f * scale);
                SpheneUI.AttachTooltip(
                    "Recurrence - ends are expanded server-side; single dates can " +
                    "be moved or cancelled per occurrence.");
                if (composer.Recurrence != 0)
                {
                    DrawOccurrencePicker(composer, edit, events, page.Planner);
                }
            }

            LabelCol("Reminders");
            SpheneUI.Controls.InputTextWithHint("##ep-rem",
                "Minutes before start - e.g. 15, 60", ref composer.Reminders,
                60, preferredWidth: 200f * scale);

            if (composer.ScheduleProblem() is { } problem)
            {
                SpheneUI.Text.Colored(problem, SpheneUI.Colors.Danger);
            }
        }, FontAwesomeIcon.Clock,
            description: "Local wall-clock in the selected zone - saved as UTC.");
    }

    /// <summary>"Edit a specific date" - resolves the series' occurrences via
    /// the host's calendar hook and lets the planner retarget the editor to
    /// one exception date.</summary>
    private static void DrawOccurrencePicker(CommunityEventComposer composer,
        SyncshellCommunityEdit edit, IReadOnlyList<SyncshellEventDto> events,
        EventPlannerState planner)
    {
        var ev = composer.EditId != 0
            ? events.FirstOrDefault(e => e.Id == composer.EditId) : null;
        if (ev == null || edit.LoadCalendar == null) return;

        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.CalendarDay, "Edit a specific date..."))
        {
            ImGui.OpenPopup("##ep-occpick");
        }
        if (SpheneUI.Popups.BeginStyled("##ep-occpick"))
        {
            if (!planner.Occurrences.TryGetValue(ev.Id, out var occs))
            {
                occs = null;
                var from = DateTime.UtcNow.AddDays(-30);
                var to = DateTime.UtcNow.AddDays(180);
                var load = edit.LoadCalendar;
                _ = Task.Run(async () =>
                {
                    planner.Occurrences[ev.Id] =
                        await load(from, to).ConfigureAwait(false) ?? [];
                });
            }
            var forEvent = occs?.Where(o => o.EventId == ev.Id)
                .OrderBy(o => o.OccurrenceStartUtc).Take(24).ToList();
            if (forEvent == null)
            {
                SpheneUI.Text.Muted("Loading dates...");
            }
            else if (forEvent.Count == 0)
            {
                SpheneUI.Text.Muted("No upcoming dates in the next 6 months.");
            }
            else
            {
                foreach (var occ in forEvent)
                {
                    var label = SpheneUI.Text.FormatDateTime(occ.OccurrenceStartUtc.ToLocalTime())
                        + (occ.IsCancelled ? "  (cancelled)" : string.Empty)
                        + (occ.IsException ? "  (custom)" : string.Empty);
                    if (SpheneUI.Controls.Selectable(label,
                            composer.OccurrenceStartUtc == occ.OccurrenceStartUtc))
                    {
                        composer.OpenOccurrence(ev, occ);
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            SpheneUI.Popups.EndStyled();
        }
    }

    /// <summary>People card - creator identity, organizer picker and the
    /// labeled capacity field. Members resolve by display name; the draft
    /// stores UIDs only.</summary>
    private static void DrawPeopleSection(CommunityEventComposer composer,
        SyncshellCommunityEdit edit)
    {
        SpheneUI.Layout.HeadedSurface("People", () =>
        {
            // Creator identity - first organizer of an existing event, or
            // the editor themselves for a new one.
            if (composer.EditId == 0)
            {
                SummaryRow("Creator", "You - saved as the event's creator.");
            }
            else if (composer.OrganizerUids.Count > 0)
            {
                SummaryRow("Creator", OrganizerLabel(composer.OrganizerUids[0], edit, null));
            }

            LabelCol("Max attendees");
            SpheneUI.Controls.InputTextWithHint("##ep-cap",
                "Empty = unlimited", ref composer.MaxAttendees, 8,
                preferredWidth: 120f * SpheneUI.Scale);
            if (!string.IsNullOrWhiteSpace(composer.MaxAttendees)
                && (!int.TryParse(composer.MaxAttendees, out var cap) || cap <= 0))
            {
                SpheneUI.Text.Colored("Capacity must be a positive number.",
                    SpheneUI.Colors.Danger);
            }
            if (!edit.CanManageEvents) return;

            var options = edit.MemberOptions?.Invoke();
            SpheneUI.Layout.SettingsGroup("Organizers", FontAwesomeIcon.UserShield);
            var removeIndex = -1;
            for (var i = 0; i < composer.OrganizerUids.Count; i++)
            {
                var uid = composer.OrganizerUids[i];
                var label = OrganizerLabel(uid, edit, options);
                ImGui.PushID($"ep-org-{i}");
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Times))
                {
                    removeIndex = i;
                }
                SpheneUI.AttachTooltip("Remove organizer");
                ImGui.SameLine(0f, SpheneUI.Spacing.X8);
                SpheneUI.Text.Colored($"{i + 1}. {label}", SpheneUI.Colors.TextSecondary);
                ImGui.PopID();
            }
            if (removeIndex >= 0)
            {
                composer.OrganizerUids.RemoveAt(removeIndex);
            }
            if (options != null)
            {
                var available = options
                    .Where(o => !composer.OrganizerUids.Contains(o.Uid, StringComparer.Ordinal))
                    .ToArray();
                SpheneUI.Controls.Combo("Add organizer##ep-orgadd", available,
                    o => o is { } v ? v.Label : "Pick a member...",
                    onSelected: o =>
                    {
                        if (o is { } picked)
                        {
                            composer.OrganizerUids.Add(picked.Uid);
                        }
                    },
                    initialSelectedItem: (Uid: string.Empty, Label: "Pick a member..."));
            }
            else
            {
                SpheneUI.Text.MutedWrapped(
                    "Member identities aren't available on this surface - " +
                    "organizers stay as saved.");
            }
        }, FontAwesomeIcon.Users);
    }

    private static string OrganizerLabel(string uid, SyncshellCommunityEdit edit,
        IReadOnlyList<(string Uid, string Label)>? options)
        => options?.FirstOrDefault(o =>
                string.Equals(o.Uid, uid, StringComparison.Ordinal)).Label
            ?? edit.MemberLabel?.Invoke(uid) ?? "Unknown user";

    /// <summary>Publishing card - visibility + moderation state, then a
    /// visually separated destructive zone for cancel/delete. Save lives in
    /// the pinned footer.</summary>
    private static void DrawPublishingSection(CommunityEventComposer composer,
        SyncshellCommunityEdit edit, IReadOnlyList<SyncshellEventDto> events,
        bool occurrenceEdit)
    {
        SpheneUI.Layout.HeadedSurface("Publishing", () =>
        {
            if (!occurrenceEdit)
            {
                var vis = new[] { "Members only", "Public" };
                using (ImRaii.Disabled(!edit.CanManageEvents))
                {
                    LabelCol("Visibility");
                    SpheneUI.Controls.Combo("##ep-vis", vis, s => s ?? string.Empty,
                        onSelected: p => composer.Visibility = p is string s
                            ? Array.IndexOf(vis, s) : 0,
                        initialSelectedItem: vis[Math.Clamp(composer.Visibility, 0,
                            vis.Length - 1)],
                        preferredWidth: 200f * SpheneUI.Scale);
                }
                SpheneUI.Text.MutedWrapped(edit.CanManageEvents
                    ? "Public events also render on the community's public page."
                    : "Visibility is staff-managed - your event goes to the review queue.");
            }

            var existing = composer.EditId != 0
                ? events.FirstOrDefault(e => e.Id == composer.EditId) : null;
            if (existing == null || edit.CanEditEvent?.Invoke(existing) != true) return;

            SpheneUI.Layout.SettingsGroup("Danger zone", FontAwesomeIcon.ExclamationTriangle);
            using (ImRaii.Group())
            {
                if (edit.OnCancelEvent != null
                    && existing.EventStatus != (int)EventLifecycleStatus.Cancelled)
                {
                    if (SpheneUI.Controls.SegmentButton("Cancel event",
                            FontAwesomeIcon.Ban, accent: SpheneUI.Colors.Danger))
                    {
                        edit.OnCancelEvent(existing);
                    }
                    SpheneUI.AttachTooltip("Cancel event - participants are notified");
                }
                if (edit.OnDeleteEvent != null && edit.CanManageEvents)
                {
                    if (edit.OnCancelEvent != null
                        && existing.EventStatus != (int)EventLifecycleStatus.Cancelled)
                    {
                        ImGui.SameLine(0f, SpheneUI.Spacing.X8);
                    }
                    if (SpheneUI.Controls.SegmentButton("Delete event",
                            FontAwesomeIcon.Trash, accent: SpheneUI.Colors.Danger))
                    {
                        edit.OnDeleteEvent(existing);
                    }
                }
            }
        }, FontAwesomeIcon.GlobeAmericas);
    }

    /// <summary>Pinned action row under the scrolling form - Discard on the
    /// left, the primary Save right; member drafts sit between them.</summary>
    private static void DrawEditorFooter(
        SyncshellCommunityEdit edit, IReadOnlyList<SyncshellEventDto> events,
        CommunityEventComposer composer)
    {
        var occurrenceEdit = composer.OccurrenceStartUtc.HasValue;
        var existing = composer.EditId != 0
            ? events.FirstOrDefault(e => e.Id == composer.EditId) : null;
        var valid = !string.IsNullOrWhiteSpace(composer.Title)
            && composer.ScheduleProblem() == null
            && !composer.DestinationInvalid()
            && (string.IsNullOrWhiteSpace(composer.MaxAttendees)
                || (int.TryParse(composer.MaxAttendees, out var cap2) && cap2 > 0))
            && (composer.EditId == 0
                || (existing != null && edit.CanEditEvent?.Invoke(existing) == true));

        if (SpheneUI.Controls.SegmentButton("Discard",
                FontAwesomeIcon.ChevronLeft))
        {
            composer.Close();
        }
        SpheneUI.AttachTooltip("Back to the planner - unsaved changes are dropped.");
        var saveLabel = occurrenceEdit ? "Save this date"
            : composer.EditId == 0 ? "Create event" : "Save event";
        var midPad = SpheneUI.Spacing.X8;
        if (!edit.CanManageEvents && composer.EditId == 0 && !occurrenceEdit)
        {
            var draftW = SpheneUI.Controls.SegAutoWidth("Save draft",
                FontAwesomeIcon.Save, ImGui.GetFrameHeight());
            SpheneUI.Controls.DockToRightEdge(draftW + midPad
                + SpheneUI.Controls.SegAutoWidth(saveLabel,
                    FontAwesomeIcon.CalendarPlus, ImGui.GetFrameHeight()));
            if (SpheneUI.Controls.SegmentButton("Save draft", FontAwesomeIcon.Save))
            {
                composer.SaveAsDraft = true;
                edit.OnSaveEvent?.Invoke(composer);
            }
            ImGui.SameLine(0f, midPad);
        }
        else
        {
            SpheneUI.Controls.DockToRightEdge(SpheneUI.Controls.SegAutoWidth(
                saveLabel, FontAwesomeIcon.CalendarPlus, ImGui.GetFrameHeight()));
        }
        using (ImRaii.Disabled(!valid))
        {
            if (SpheneUI.Controls.PrimaryButton(saveLabel,
                    FontAwesomeIcon.CalendarPlus, enabled: valid))
            {
                edit.OnSaveEvent?.Invoke(composer);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Right-side inspector
    // ─────────────────────────────────────────────────────────────

    /// <summary>The inspector column - live event summary, per-section
    /// validation status and the preview affordance. Everything reads real
    /// composer state; nothing is fabricated.</summary>
    private static void DrawInspector(
        SyncshellCommunityEdit edit, CommunityTravelHooks? travel,
        CommunityEventComposer composer)
    {
        SpheneUI.Layout.HeadedSurface("Event summary", () =>
        {
            SummaryRow("Title",
                string.IsNullOrWhiteSpace(composer.Title) ? "Untitled event" : composer.Title.Trim());
            var tz = composer.ResolvedTimeZone();
            var startDateOk = composer.StartYear >= 2000
                && composer.StartMonth is >= 1 and <= 12
                && composer.StartDay >= 1
                && composer.StartDay <= DateTime.DaysInMonth(composer.StartYear,
                    composer.StartMonth);
            if (startDateOk)
            {
                var when = SpheneUI.Text.FormatDateTime(composer.BuildStartUtc().ToLocalTime())
                    + (composer.HasEnd && composer.EndYear >= 2000
                        && composer.EndMonth is >= 1 and <= 12
                        && composer.EndDay >= 1
                        && composer.EndDay <= DateTime.DaysInMonth(composer.EndYear,
                            composer.EndMonth)
                        && composer.BuildEndUtc() is { } end
                            ? $" - {end.ToLocalTime():HH:mm}" : string.Empty)
                    + $" · {SpheneUI.Controls.ZoneFriendly(tz)}";
                SummaryRow("When", when);
            }
            var where = !string.IsNullOrWhiteSpace(composer.Location)
                ? composer.Location.Trim()
                : composer.BuildDestination() is { } dest
                    ? SyncshellCommunityView.DestinationTitle(dest, travel)
                    : null;
            if (where != null)
            {
                SummaryRow("Where", where);
            }
            SummaryRow("Visibility",
                composer.Visibility == (int)EventVisibility.Public ? "Public" : "Members only");
            if (composer.OrganizerUids.Count > 0)
            {
                SummaryRow("Organizers", string.Join(", ",
                    composer.OrganizerUids.Select(u => OrganizerLabel(u, edit, null))));
            }
            SummaryRow("Capacity",
                string.IsNullOrWhiteSpace(composer.MaxAttendees)
                    ? "Unlimited" : composer.MaxAttendees.Trim());
            if (composer.Recurrence != 0)
            {
                SummaryRow("Repeats", composer.Recurrence switch
                {
                    1 => "Daily", 2 => "Weekly", 3 => "Every 2 weeks", 4 => "Monthly",
                    _ => string.Empty,
                });
            }
        }, FontAwesomeIcon.Eye);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.HeadedSurface("Checks", () =>
        {
            CheckRow("Event details",
                !string.IsNullOrWhiteSpace(composer.Title), "needs a title");
            var scheduleProblem = composer.ScheduleProblem();
            CheckRow("Schedule", scheduleProblem == null, scheduleProblem);
            CheckRow("Location", !composer.DestinationInvalid(),
                "destination is incomplete");
            var capOk = string.IsNullOrWhiteSpace(composer.MaxAttendees)
                || (int.TryParse(composer.MaxAttendees, out var cap) && cap > 0);
            CheckRow("People", capOk, "capacity must be a positive number");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            if (edit.OnPreviewEvent != null)
            {
                using (ImRaii.Disabled(scheduleProblem != null))
                {
                    if (SpheneUI.Controls.PrimaryButton("Live Preview",
                            FontAwesomeIcon.Eye, enabled: scheduleProblem == null))
                    {
                        edit.OnPreviewEvent(composer);
                    }
                }
                SpheneUI.Text.MutedWrapped(
                    "Attendee-facing rendering of the unsaved draft - nothing is written.");
            }
        }, FontAwesomeIcon.ClipboardCheck);
    }

    /// <summary>One validation line - check or warning icon, section name,
    /// and the specific problem so the user knows where to look.</summary>
    private static void CheckRow(string section, bool ok, string? problem)
    {
        if (ok)
        {
            SpheneUI.Icons.Icon(FontAwesomeIcon.Check, SpheneUI.Palette.Success);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8);
            SpheneUI.Text.Colored(section, SpheneUI.Colors.TextSecondary);
        }
        else
        {
            SpheneUI.Icons.Icon(FontAwesomeIcon.ExclamationTriangle,
                SpheneUI.Colors.Warning);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8);
            SpheneUI.Text.ColoredWrapped(
                problem != null ? $"{section} - {problem}" : section,
                SpheneUI.Colors.Warning);
        }
    }
}
