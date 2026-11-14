using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.Interop.Ipc;

namespace Sphene.UI.Syncshell;

internal static partial class SyncshellCommunityView
{
    /// <summary>Residential districts offered by the address editor - matches
    /// <see cref="EventDestinationValidator"/>'s territory map exactly.</summary>
    private static readonly (string Name, uint TerritoryId)[] DestinationDistricts =
    [
        ("Mist", 339u),
        ("The Lavender Beds", 340u),
        ("The Goblet", 341u),
        ("Shirogane", 641u),
        ("Empyreum", 979u),
    ];

    /// <summary>Persistent inline event composer state · buffers survive across frames.</summary>
    internal sealed class CommunityEventComposer
    {
        public bool Open;
        public long EditId;
        public string Title = string.Empty;
        /// <summary>Structured schedule - the pickers write these; the save
        /// path converts through <see cref="BuildStartUtc"/>/<see cref="BuildEndUtc"/>.</summary>
        public int StartYear, StartMonth, StartDay, StartHour, StartMinute;
        /// <summary>False = open-ended event (no end time).</summary>
        public bool HasEnd;
        public int EndYear, EndMonth, EndDay, EndHour, EndMinute;
        public string Location = string.Empty;
        public string Url = string.Empty;
        public string Description = string.Empty;
        /// <summary>Empty = unlimited capacity; parses to MaxAttendees.</summary>
        public string MaxAttendees = string.Empty;
        /// <summary><see cref="EventVisibility"/> int · Members by default.</summary>
        public int Visibility;
        /// <summary><see cref="EventLifecycleStatus"/> int · Scheduled; staff can cancel.</summary>
        public int EventStatus;
        /// <summary>Which instant a DST-ambiguous local time maps to -
        /// 0 = earlier occurrence, 1 = later. Only meaningful while
        /// <see cref="AmbiguousOffsets"/> reports a value.</summary>
        public int AmbiguousChoice;

        /// <summary><see cref="ContentPublishStatus"/> intent - staff pick, resolved server-side.</summary>
        public int StatusIntent = (int)ContentPublishStatus.Published;
        /// <summary>TimeZoneInfo.Id - always populated; OpenNew seeds the
        /// system zone, OpenEdit keeps the event's stored zone.</summary>
        public string TimezoneId = string.Empty;
        /// <summary>Comma-separated minutes-before-start reminder offsets.</summary>
        public string Reminders = string.Empty;
        /// <summary>Recurrence preset index - 0 none, 1 daily, 2 weekly, 3 biweekly, 4 monthly.</summary>
        public int Recurrence;
        /// <summary>Ordered organizer UIDs (first = creator) - filled by the
        /// member picker, never typed by hand.</summary>
        public List<string> OrganizerUids = [];
        /// <summary>When set the save targets one occurrence of a recurring series.</summary>
        public DateTime? OccurrenceStartUtc;
        /// <summary>Picked cover image - shipped through the media pipeline on save.</summary>
        public byte[]? CoverBytes;
        public string? CoverContentType;
        /// <summary>Picked 16:9 preview image (800×450) for list/card contexts -
        /// shipped through the media pipeline on save; distinct from the 3:1
        /// detail banner in <see cref="CoverBytes"/>.</summary>
        public byte[]? PreviewBytes;
        public string? PreviewContentType;
        /// <summary>Persisted media references seeded on <see cref="OpenEdit"/> -
        /// hash (or legacy base64) of the stored banner/preview so the editor
        /// shows the real state after reopening. Null = no stored image.</summary>
        public string? ExistingCoverKey;
        public string? ExistingPreviewKey;
        /// <summary>Explicit user removal - a save writes empty media refs so
        /// the server clears the stored images (a null pick means "keep").</summary>
        public bool CoverCleared;
        public bool PreviewCleared;
        /// <summary>Save-as-draft affordance on member submissions.</summary>
        public bool SaveAsDraft;

        // Structured travel destination · the machine-readable target next to
        // the free-text Location label. 0 none · 1 syncshell property ·
        // 2 custom housing address · 3 public location.
        public int DestKind;
        /// <summary>Occurrence edits only: 0 inherit the series · 1 override ·
        /// 2 explicitly no destination on this date.</summary>
        public int DestScope;
        /// <summary>Selected bound-property candidate index · -1 = pick required.</summary>
        public int DestPropertyIndex = -1;
        /// <summary>Deduped properties resolved from the shell's bound areas -
        /// loaded on demand via <see cref="SyncshellCommunityEdit.Travel"/>.</summary>
        public List<EventDestinationDto>? PropertyCandidates;
        public bool PropertyCandidatesLoading;
        public bool PropertyCandidatesLoaded;
        public int DestWorldId;
        /// <summary>Index into <see cref="DestinationDistricts"/>.</summary>
        public int DestDistrict;
        public int DestWard = 1;
        public int DestPlot = 1;
        public bool DestApartment;
        public int DestRoom = 1;
        public bool DestApartmentSubdivision;
        /// <summary>Public-location target territory row id (0 = pick).</summary>
        public int DestTerritoryId;
        public string DestLabel = string.Empty;

        /// <summary>Builds the destination to persist - null on DestKind 0 and
        /// on occurrence "inherit"; the <see cref="EventDestinationKind.None"/>
        /// sentinel on occurrence "clear". Result is pre-validated via
        /// <see cref="EventDestinationValidator.Sanitize"/> - null on a
        /// configured kind means "incomplete/invalid".</summary>
        public EventDestinationDto? BuildDestination()
        {
            if (OccurrenceStartUtc.HasValue)
            {
                if (DestScope == 0) return null;
                if (DestScope == 2)
                {
                    return new EventDestinationDto { Kind = (int)EventDestinationKind.None };
                }
            }
            EventDestinationDto? dto = DestKind switch
            {
                1 => PropertyCandidates is { } c
                    && DestPropertyIndex >= 0 && DestPropertyIndex < c.Count
                        ? c[DestPropertyIndex] with { }
                        : null,
                2 => new EventDestinationDto
                {
                    Kind = (int)EventDestinationKind.HousingAddress,
                    WorldId = (uint)Math.Max(0, DestWorldId),
                    DistrictTerritoryId = DestinationDistricts[Math.Clamp(DestDistrict, 0,
                        DestinationDistricts.Length - 1)].TerritoryId,
                    Ward = DestWard,
                    IsApartment = DestApartment,
                    Plot = DestPlot,
                    Room = DestRoom,
                    ApartmentSubdivision = DestApartmentSubdivision,
                },
                3 => new EventDestinationDto
                {
                    Kind = (int)EventDestinationKind.PublicLocation,
                    TerritoryId = (uint)Math.Max(0, DestTerritoryId),
                },
                _ => null,
            };
            if (dto != null && !string.IsNullOrWhiteSpace(DestLabel))
            {
                dto.DisplayLabel = DestLabel.Trim();
            }
            return EventDestinationValidator.Sanitize(dto);
        }

        /// <summary>Seeds only the destination fields - the community
        /// location settings editor borrows this class as a field bag.</summary>
        public void SeedDestinationFields(EventDestinationDto? d) => SeedDestination(d);

        /// <summary>Seeds the destination fields from a stored DTO - a
        /// housing snapshot always opens as "Custom housing address" so the
        /// saved snapshot keeps meaning exactly what it was, independent of
        /// today's bound areas.</summary>
        private void SeedDestination(EventDestinationDto? d)
        {
            DestScope = 0;
            DestPropertyIndex = -1;
            PropertyCandidates = null;
            PropertyCandidatesLoaded = PropertyCandidatesLoading = false;
            DestWorldId = (int)(d?.WorldId ?? 0);
            DestPlot = d is { Plot: > 0 } ? d.Plot : 1;
            DestWard = d is { Ward: > 0 } ? d.Ward : 1;
            DestRoom = d is { Room: > 0 } ? d.Room : 1;
            DestApartment = d?.IsApartment == true;
            DestApartmentSubdivision = d?.ApartmentSubdivision == true;
            DestTerritoryId = (int)(d?.TerritoryId ?? 0);
            DestLabel = d?.DisplayLabel ?? string.Empty;
            DestDistrict = 0;
            if (d != null && (EventDestinationKind)d.Kind == EventDestinationKind.HousingAddress)
            {
                for (var i = 0; i < DestinationDistricts.Length; i++)
                {
                    if (DestinationDistricts[i].TerritoryId == d.DistrictTerritoryId)
                    {
                        DestDistrict = i;
                        break;
                    }
                }
            }
            DestKind = d == null ? 0
                : (EventDestinationKind)d.Kind == EventDestinationKind.HousingAddress ? 2
                : (EventDestinationKind)d.Kind == EventDestinationKind.PublicLocation ? 3
                : 0;
        }

        /// <summary>True while the configured destination can't be persisted -
        /// a configured kind that fails validation, or a property pick with no
        /// candidate selected.</summary>
        public bool DestinationInvalid()
        {
            if (OccurrenceStartUtc.HasValue && DestScope != 1) return false;
            if (DestKind == 0) return false;
            if (DestKind == 1)
            {
                return PropertyCandidates is not { Count: > 0 }
                    || DestPropertyIndex < 0
                    || DestPropertyIndex >= PropertyCandidates.Count;
            }
            return BuildDestination() == null;
        }

        /// <summary>The zone the pickers' wall-clock values are interpreted
        /// in - falls back to the system zone on an unknown/empty id.</summary>
        public TimeZoneInfo ResolvedTimeZone()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(TimezoneId))
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(TimezoneId.Trim());
                }
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.Local;
            }
            catch (InvalidTimeZoneException)
            {
                return TimeZoneInfo.Local;
            }
            return TimeZoneInfo.Local;
        }

        /// <summary>Picker wall-clock → UTC. DST-ambiguous local times map to
        /// the occurrence the user picked via <see cref="AmbiguousChoice"/>
        /// (earlier/later); nonexistent times must be caught by
        /// <see cref="ScheduleProblem"/> before saving.</summary>
        public DateTime BuildStartUtc()
            => ToUtc(StartYear, StartMonth, StartDay, StartHour, StartMinute);

        /// <summary>End wall-clock → UTC · null when <see cref="HasEnd"/> is off.</summary>
        public DateTime? BuildEndUtc()
            => !HasEnd ? null : ToUtc(EndYear, EndMonth, EndDay, EndHour, EndMinute);

        /// <summary>UTC instants an ambiguous local start can mean -
        /// earlier occurrence (larger offset) and later occurrence.
        /// Null when the picked start is not ambiguous.</summary>
        public (DateTime EarlierUtc, DateTime LaterUtc)? AmbiguousOffsets()
        {
            if (!ValidDate(StartYear, StartMonth, StartDay)) return null;
            var tz = ResolvedTimeZone();
            var local = new DateTime(StartYear, StartMonth, StartDay, StartHour, StartMinute, 0,
                DateTimeKind.Unspecified);
            if (!tz.IsAmbiguousTime(local)) return null;
            var offsets = tz.GetAmbiguousTimeOffsets(local);
            var earlier = new DateTimeOffset(local, offsets.Max()).UtcDateTime;
            var later = new DateTimeOffset(local, offsets.Min()).UtcDateTime;
            return (earlier, later);
        }

        private DateTime ToUtc(int y, int mo, int d, int h, int mi)
        {
            var tz = ResolvedTimeZone();
            var local = new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified);
            if (tz.IsAmbiguousTime(local))
            {
                var offsets = tz.GetAmbiguousTimeOffsets(local);
                var offset = AmbiguousChoice == 1 ? offsets.Min() : offsets.Max();
                return new DateTimeOffset(local, offset).UtcDateTime;
            }
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }

        /// <summary>True when a zone id was entered/stored but doesn't
        /// resolve on this machine - saving must not silently re-anchor the
        /// event to the editor's system zone.</summary>
        public bool TimezoneInvalid()
        {
            if (string.IsNullOrWhiteSpace(TimezoneId)) return false;
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(TimezoneId.Trim());
                return false;
            }
            catch (TimeZoneNotFoundException) { return true; }
            catch (InvalidTimeZoneException) { return true; }
        }

        /// <summary>Blocking schedule problem, or null when the date/time
        /// selection is saveable - surfaced both inline and in the inspector.</summary>
        public string? ScheduleProblem()
        {
            if (StartYear < 2000 || !ValidDate(StartYear, StartMonth, StartDay))
            {
                return "Pick a start date.";
            }
            if (TimezoneInvalid())
            {
                return $"\"{TimezoneId.Trim()}\" isn't a known time zone - pick a valid zone.";
            }
            var tz = ResolvedTimeZone();
            var start = new DateTime(StartYear, StartMonth, StartDay, StartHour, StartMinute, 0,
                DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(start))
            {
                return "That start time doesn't exist in the selected zone (daylight-saving gap) - pick a different hour.";
            }
            if (tz.IsAmbiguousTime(start))
            {
                // Not blocking - ConvertTimeToUtc picks the standard mapping -
                // but the user should know which occurrence it lands on.
                return null;
            }
            if (HasEnd)
            {
                if (!ValidDate(EndYear, EndMonth, EndDay))
                {
                    return "Pick an end date or disable the end time.";
                }
                var end = new DateTime(EndYear, EndMonth, EndDay, EndHour, EndMinute, 0,
                    DateTimeKind.Unspecified);
                if (tz.IsInvalidTime(end))
                {
                    return "That end time doesn't exist in the selected zone (daylight-saving gap).";
                }
                if (end <= start)
                {
                    return "The end must be later than the start.";
                }
            }
            return null;
        }

        /// <summary>True while the entered start lands on a DST-ambiguous
        /// local time - informational, not blocking.</summary>
        public bool ScheduleAmbiguous()
        {
            if (!ValidDate(StartYear, StartMonth, StartDay)) return false;
            var start = new DateTime(StartYear, StartMonth, StartDay, StartHour, StartMinute, 0,
                DateTimeKind.Unspecified);
            return ResolvedTimeZone().IsAmbiguousTime(start);
        }

        private static bool ValidDate(int y, int m, int d)
            => y is >= 2000 and <= 9999 && m is >= 1 and <= 12
                && d >= 1 && d <= DateTime.DaysInMonth(y, m);

        /// <summary>Projects the draft into the attendee-facing DTO the
        /// preview renders - same normalization as the save path (UTC
        /// conversion through the selected zone, sanitized destination).
        /// <paramref name="resolveUser"/> maps organizer UIDs to identity
        /// projections so the preview shows names like the real card.</summary>
        public SyncshellEventDto BuildPreviewDto(Func<string, UserData?>? resolveUser = null)
        {
            var tz = ResolvedTimeZone();
            return new SyncshellEventDto(EditId, string.Empty,
                string.IsNullOrWhiteSpace(Title) ? "Untitled event" : Title.Trim(),
                NullIfEmpty(Description), BuildStartUtc(), BuildEndUtc(),
                NullIfEmpty(Location), NullIfEmpty(Url),
                CoverBytes != null ? Convert.ToBase64String(CoverBytes) : null,
                DateTime.UtcNow, DateTime.UtcNow, null,
                OrganizerUids.Count > 0 ? [.. OrganizerUids] : null, null,
                MaxAttendees: int.TryParse(MaxAttendees.Trim(), out var cap) && cap > 0 ? cap : null,
                Visibility: Visibility, EventStatus: EventStatus, PublishStatus: StatusIntent,
                TimezoneId: string.IsNullOrWhiteSpace(TimezoneId) ? tz.Id : TimezoneId.Trim(),
                RecurrenceRule: RecurrenceRuleForPreview(),
                Reminders: ParseRemindersLocal(Reminders),
                Destination: BuildDestination(),
                OrganizerUsers: OrganizerUids
                    .Select(u => resolveUser?.Invoke(u))
                    .Where(u => u != null).Cast<UserData>().ToList(),
                PreviewBase64: PreviewBytes != null
                    ? Convert.ToBase64String(PreviewBytes) : null);
        }

        private string? RecurrenceRuleForPreview() => Recurrence switch
        {
            1 => "FREQ=DAILY", 2 => "FREQ=WEEKLY", 3 => "FREQ=WEEKLY;INTERVAL=2",
            4 => "FREQ=MONTHLY", _ => null,
        };

        private static List<int>? ParseRemindersLocal(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var list = new List<int>();
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var m) && m > 0 && m <= 40320)
                {
                    list.Add(m);
                }
            }
            return list.Count > 0 ? list.Distinct().ToList() : null;
        }

        private static string? NullIfEmpty(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private void SeedSchedule(DateTime? startUtc, DateTime? endUtc, string? tzId)
        {
            TimezoneId = string.IsNullOrWhiteSpace(tzId) ? TimeZoneInfo.Local.Id : tzId!;
            var tz = ResolvedTimeZone();
            var start = TimeZoneInfo.ConvertTimeFromUtc(startUtc ?? DateTime.UtcNow.AddHours(1), tz);
            (StartYear, StartMonth, StartDay) = (start.Year, start.Month, start.Day);
            (StartHour, StartMinute) = (start.Hour, start.Minute);
            HasEnd = endUtc.HasValue;
            var end = TimeZoneInfo.ConvertTimeFromUtc(endUtc ?? startUtc ?? start, tz);
            (EndYear, EndMonth, EndDay) = (end.Year, end.Month, end.Day);
            (EndHour, EndMinute) = (end.Hour, end.Minute);
        }

        public void OpenNew()
        {
            Open = true;
            EditId = 0;
            Title = Location = Url = Description = string.Empty;
            // Sensible default: next full hour, no end time, the viewer's zone.
            SeedSchedule(DateTime.UtcNow.AddHours(1).AddMinutes(-DateTime.UtcNow.Minute), null, null);
            MaxAttendees = string.Empty;
            Visibility = (int)EventVisibility.Members;
            EventStatus = (int)EventLifecycleStatus.Scheduled;
            StatusIntent = (int)ContentPublishStatus.Published;
            Reminders = "60";
            Recurrence = 0;
            OrganizerUids = [];
            AmbiguousChoice = 0;
            OccurrenceStartUtc = null;
            CoverBytes = null;
            CoverContentType = null;
            PreviewBytes = null;
            PreviewContentType = null;
            ExistingCoverKey = null;
            ExistingPreviewKey = null;
            CoverCleared = false;
            PreviewCleared = false;
            SaveAsDraft = false;
            SeedDestination(null);
        }

        public void OpenEdit(SyncshellEventDto ev)
        {
            Open = true;
            EditId = ev.Id;
            Title = ev.Title;
            // Keep the event's stored zone - never silently re-anchor it to
            // the editor user's system zone.
            SeedSchedule(ev.StartUtc, ev.EndUtc, ev.TimezoneId);
            Location = ev.Location ?? string.Empty;
            Url = ev.Url ?? string.Empty;
            Description = ev.Description ?? string.Empty;
            MaxAttendees = ev.MaxAttendees?.ToString() ?? string.Empty;
            Visibility = ev.Visibility;
            EventStatus = ev.EventStatus;
            StatusIntent = ev.PublishStatus;
            Reminders = ev.Reminders != null ? string.Join(", ", ev.Reminders) : string.Empty;
            Recurrence = RecurrencePreset(ev.RecurrenceRule);
            OrganizerUids = ev.OrganizerUids != null ? [.. ev.OrganizerUids] : [];
            AmbiguousChoice = 0;
            OccurrenceStartUtc = null;
            CoverBytes = null;
            CoverContentType = null;
            PreviewBytes = null;
            PreviewContentType = null;
            ExistingCoverKey = ev.ImageMediaHash ?? ev.ImageBase64;
            ExistingPreviewKey = ev.PreviewMediaHash;
            CoverCleared = false;
            PreviewCleared = false;
            SaveAsDraft = false;
            SeedDestination(ev.Destination);
        }

        /// <summary>Opens the composer targeted at one series occurrence -
        /// the save writes a per-date exception instead of the series. The
        /// destination scope seeds from the occurrence's raw override:
        /// absent = inherit, Kind.None = cleared, value = override.</summary>
        public void OpenOccurrence(SyncshellEventDto ev, SyncshellEventOccurrenceDto occ)
        {
            OpenEdit(ev);
            OccurrenceStartUtc = occ.OccurrenceStartUtc;
            var ov = occ.DestinationOverride;
            if (ov == null)
            {
                DestScope = 0;
            }
            else if ((EventDestinationKind)ov.Kind == EventDestinationKind.None)
            {
                DestScope = 2;
                SeedDestination(null);
            }
            else
            {
                DestScope = 1;
                SeedDestination(ov);
            }
        }

        /// <summary>Maps a stored RRULE onto the preset combo index.</summary>
        private static int RecurrencePreset(string? rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return 0;
            var r = rule.ToUpperInvariant();
            if (r.Contains("FREQ=DAILY")) return 1;
            if (r.Contains("INTERVAL=2")) return 3;
            if (r.Contains("FREQ=WEEKLY")) return 2;
            if (r.Contains("FREQ=MONTHLY")) return 4;
            return 0;
        }

        public void Close() => Open = false;
    }

}
