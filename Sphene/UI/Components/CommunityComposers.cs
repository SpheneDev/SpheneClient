using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using Sphene.UI.Syncshell;
using Sphene.Utils;
using Sphene.WebAPI;

namespace Sphene.UI.Components;

/// <summary>
/// Save backends for the community page's inline composers. The view owns
/// the open/seed state and renders the editor in place; these calls persist
/// through the feed APIs and ask the host to refresh its cached DTO  · 
/// content is created where it is displayed, no separate studio surface.
/// </summary>
internal sealed class CommunityComposers
{
    private readonly ApiController _apiController;
    private readonly SpheneMediator _mediator;
    private readonly ILogger _logger;
    private readonly Sphene.Services.CommunityMediaClientService? _media;

    public CommunityComposers(ApiController apiController, SpheneMediator mediator, ILogger logger,
        Sphene.Services.CommunityMediaClientService? media = null)
    {
        _apiController = apiController;
        _mediator = mediator;
        _logger = logger;
        _media = media;
        MarkdownComposer.ImagePicker = PickImageBytesAsync;
        MarkdownComposer.StudioPicker = PickImageBytesAsync;
    }

    /// <summary>Host-supplied author-name deep-link - bound onto every
    /// comment ops the composers build so author names can open profiles.</summary>
    public Action<string>? OnAuthorClick { get; set; }

    /// <summary>PhotoStudio-backed picker for the shared markdown composer -
    /// resolves to the prepared image bytes or null when cancelled.</summary>
    public Task<byte[]?> PickImageBytesAsync()
        => PickImageBytesAsync(PhotoStudioTarget.CommunityPhoto);

    /// <summary>PhotoStudio-backed picker targeted at a specific image preset -
    /// the studio enforces the target's aspect ratio and output dimensions.</summary>
    public Task<byte[]?> PickImageBytesAsync(PhotoStudioTarget target)
    {
        var tcs = new TaskCompletionSource<byte[]?>();
        _mediator.Publish(new OpenPhotoStudioMessage(null, target,
            prepared => tcs.TrySetResult(prepared.Bytes)));
        // No cancel signal exists on the studio side - an unanswered pick just
        // leaves the task pending; the composer treats that as "still choosing".
        return tcs.Task;
    }

    /// <summary>Uploads the rendered image bytes to the file server when the
    /// media channel is configured - falls back to the base64 DTO payload so
    /// every client keeps working.</summary>
    private async Task<(string? base64, string? hash)> ShipImageAsync(
        byte[] bytes, string? contentType, string? base64, CancellationToken ct = default)
    {
        if (_media != null)
        {
            var hash = await _media.UploadImageAsync(bytes, contentType, ct).ConfigureAwait(false);
            if (hash != null) return (null, hash);
        }
        return (base64 ?? Convert.ToBase64String(bytes), null);
    }

    /// <summary>Persists the inline post composer · staff write via upsert,
    /// member submission goes through the policy-gated submit endpoint.</summary>
    public void SaveNews(GroupData group, SyncshellCommunityView.CommunityPostComposer composer,
        bool draft, Func<Task> refresh, bool staff = true)
    {
        var intent = draft ? (int)ContentPublishStatus.Draft : (int)ContentPublishStatus.Published;
        var dto = new SyncshellNewsPostUpdateDto(composer.EditId, group,
            composer.Title.Trim(), composer.Body.Trim(), composer.Pinned, null, draft,
            StatusIntent: intent);
        composer.Close();
        _ = Task.Run(async () =>
        {
            try
            {
                var saved = staff
                    ? await _apiController.GroupUpsertCommunityNews(dto).ConfigureAwait(false)
                    : await _apiController.GroupSubmitCommunityNews(dto).ConfigureAwait(false);
                if (saved != null)
                {
                    await refresh().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save news post for {GID}", group.GID);
            }
        });
    }

    /// <summary>Persists the inline event composer · staff write via upsert,
    /// member submissions route through the policy-gated submit endpoint.
    /// <paramref name="staff"/> decides which; the server re-resolves the
    /// effective publish state either way.</summary>
    public void SaveEvent(GroupData group, SyncshellCommunityView.CommunityEventComposer composer,
        Func<Task> refresh, bool staff = true)
    {
        var saveAsDraft = composer.SaveAsDraft;
        var coverBytes = composer.CoverBytes;
        var coverType = composer.CoverContentType;
        var previewBytes = composer.PreviewBytes;
        var previewType = composer.PreviewContentType;
        var dto = new SyncshellEventUpdateDto(composer.EditId, group, composer.Title.Trim(),
            NullIfEmpty(composer.Description), composer.BuildStartUtc(),
            composer.BuildEndUtc(), NullIfEmpty(composer.Location), NullIfEmpty(composer.Url),
            null,
            MaxAttendees: ParseCap(composer.MaxAttendees),
            Visibility: composer.Visibility,
            EventStatus: composer.EventStatus,
            StatusIntent: saveAsDraft
                ? (int)ContentPublishStatus.Draft
                : composer.StatusIntent,
            TimezoneId: NullIfEmpty(composer.TimezoneId),
            RecurrenceRule: RecurrenceRule(composer.Recurrence),
            Reminders: ParseReminders(composer.Reminders),
            OrganizerUids: staff && composer.OrganizerUids.Count > 0
                ? [.. composer.OrganizerUids] : null,
            OccurrenceStartUtc: composer.OccurrenceStartUtc,
            Destination: composer.BuildDestination());
        composer.Close();
        _ = Task.Run(async () =>
        {
            try
            {
                if (coverBytes != null)
                {
                    var (base64, hash) = await ShipImageAsync(coverBytes, coverType, null)
                        .ConfigureAwait(false);
                    dto = dto with { ImageBase64 = base64, ImageMediaHash = hash };
                }
                else if (composer.CoverCleared)
                {
                    dto = dto with { ImageBase64 = null, ImageMediaHash = "" };
                }
                if (previewBytes != null)
                {
                    var (_, previewHash) = await ShipImageAsync(previewBytes, previewType, null)
                        .ConfigureAwait(false);
                    dto = dto with { PreviewMediaHash = previewHash };
                }
                else if (composer.PreviewCleared)
                {
                    dto = dto with { PreviewMediaHash = "" };
                }
                var saved = staff
                    ? await _apiController.GroupUpsertCommunityEvent(dto).ConfigureAwait(false)
                    : await _apiController.GroupSubmitCommunityEvent(dto).ConfigureAwait(false);
                if (saved != null)
                {
                    await refresh().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save event for {GID}", group.GID);
            }
        });
    }

    /// <summary>
    /// Wires the event-participation ops onto a community edit contract -
    /// RSVP/withdraw/participants/calendar for every member, staff overrides
    /// (seat/move/remove, cancel, exceptions) only when
    /// <paramref name="canManageEvents"/> holds.
    /// </summary>
    public void WireEventOps(SyncshellCommunityView.SyncshellCommunityEdit edit,
        GroupData group, bool canManageEvents, Func<Task> refresh)
    {
        edit.OnRsvp = (ev, status) => _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupSetEventRsvp(
                    new EventRsvpDto(group, ev.Id, status)).ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to RSVP for event {EventId}", ev.Id);
            }
        });
        edit.OnWithdrawRsvp = ev => _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupSetEventRsvp(
                    new EventRsvpDto(group, ev.Id, 0, Remove: true)).ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to withdraw RSVP for event {EventId}", ev.Id);
            }
        });
        edit.LoadParticipants = ev => Task.Run(async () =>
        {
            try
            {
                return (EventParticipantsDto?)await _apiController.GroupGetEventParticipants(
                    new EventRsvpDto(group, ev.Id, 0)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load participants for event {EventId}", ev.Id);
                return null;
            }
        });
        edit.LoadCalendar = (fromUtc, toUtc) => Task.Run(async () =>
        {
            try
            {
                return (List<SyncshellEventOccurrenceDto>?)await _apiController
                    .GroupGetEventCalendar(
                        new SyncshellEventCalendarRequestDto(group, fromUtc, toUtc))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load event calendar for {GID}", group.GID);
                return null;
            }
        });
        if (!canManageEvents)
        {
            return;
        }
        edit.OnUpdateParticipant = (ev, uid, status, remove) => _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupUpdateEventParticipant(
                    new EventRsvpDto(group, ev.Id, status, TargetUid: uid, Remove: remove))
                    .ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update participant {Uid} on event {EventId}",
                    uid, ev.Id);
            }
        });
        edit.OnCancelEvent = ev => _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupUpsertCommunityEvent(
                    new SyncshellEventUpdateDto(ev.Id, group, ev.Title, ev.Description,
                        ev.StartUtc, ev.EndUtc, ev.Location, ev.Url, null,
                        ev.ImageMediaHash,
                        EventStatus: (int)Sphene.API.Data.Enum.EventLifecycleStatus.Cancelled,
                        // Cancelling must not wipe the stored destination.
                        Destination: ev.Destination))
                    .ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel event {EventId}", ev.Id);
            }
        });
        edit.OnEventException = dto => _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupUpsertEventException(dto).ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write event exception for {EventId}", dto.EventId);
            }
        });
    }

    /// <summary>
    /// Builds the Lifestream travel/address hook set for one shell - used by
    /// the destination editor (via <see cref="WireTravelOps"/>) and passed
    /// to <c>DrawContent</c> directly so read-only viewers keep their travel
    /// affordances without any editing rights.
    /// </summary>
    public SyncshellCommunityView.CommunityTravelHooks BuildTravelHooks(
        GroupData group, Sphene.Services.LifestreamTravelService travel,
        Sphene.Services.DalamudUtilService dalamudUtil,
        Sphene.Interop.Ipc.IpcCallerLifestream lifestream)
    {
        return new SyncshellCommunityView.CommunityTravelHooks(
            GetState: d => travel.GetStateAsync(d),
            RequestTravel: d => travel.RequestTravelAsync(d),
            LoadBoundLocations: async () =>
            {
                var shells = await _apiController.GroupGetAreaBoundSyncshells()
                    .ConfigureAwait(false);
                var match = shells.FirstOrDefault(s => string.Equals(s.Group.GID,
                    group.GID, StringComparison.Ordinal));
                return match?.BoundAreas.Select(a => a.Location).ToList();
            },
            CurrentLocation: () => dalamudUtil.IsOnFrameworkThread
                ? dalamudUtil.GetMapData() : null,
            WorldName: id => dalamudUtil.WorldData.Value.TryGetValue(id, out var n)
                ? n : null,
            TerritoryName: id => dalamudUtil.TerritoryData.Value.TryGetValue(id, out var n)
                ? n : null,
            WorldChoices: () => dalamudUtil.WorldData.Value
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(w => w.Value, StringComparer.Ordinal)
                .ToList(),
            TerritoryChoices: () => dalamudUtil.TerritoryData.Value
                .Select(kv => (kv.Key, kv.Value))
                .OrderBy(t => t.Value, StringComparer.Ordinal)
                .ToList(),
            ShowOnMap: d => ShowDestinationOnMapAsync(d, lifestream, dalamudUtil));
    }

    /// <summary>
    /// Wires the Lifestream travel hooks onto a community edit contract plus
    /// the permanent community-location save op (page settings surface).
    /// Everything stays optional; a missing service only removes its
    /// affordance.
    /// </summary>
    public void WireTravelOps(SyncshellCommunityView.SyncshellCommunityEdit edit,
        GroupData group, Sphene.Services.LifestreamTravelService travel,
        Sphene.Services.DalamudUtilService dalamudUtil,
        Sphene.Interop.Ipc.IpcCallerLifestream lifestream,
        Func<Task> refresh)
    {
        edit.Travel = BuildTravelHooks(group, travel, dalamudUtil, lifestream);
        edit.OnSetCommunityLocation = (dest, publish) => _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupSetCommunityDestination(
                    new SyncshellCommunityDestinationUpdateDto(group, dest, publish))
                    .ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save community destination for {GID}", group.GID);
            }
        });
    }

    /// <summary>
    /// Opens the in-game map at a destination - plot-entrance marker for house
    /// plots via Lifestream's <c>GetPlotEntrance</c>, the plain district/area
    /// map when no reliable position exists (apartments, public locations,
    /// Lifestream absent).
    /// </summary>
    private async Task<bool> ShowDestinationOnMapAsync(EventDestinationDto d,
        Sphene.Interop.Ipc.IpcCallerLifestream lifestream,
        Sphene.Services.DalamudUtilService dalamudUtil)
    {
        var housing = (EventDestinationKind)d.Kind == EventDestinationKind.HousingAddress;
        var territory = housing ? d.DistrictTerritoryId : d.TerritoryId;
        if (territory == 0) return false;
        return await dalamudUtil.RunOnFrameworkThread(() =>
        {
            var map = dalamudUtil.MapData.Value.Values
                .FirstOrDefault(m => m.Map.TerritoryType.RowId == territory).Map;
            if (map.RowId == 0) return false;
            if (housing && !d.IsApartment && lifestream.APIAvailable)
            {
                try
                {
                    // Lifestream's plot numbers are 0-based at this endpoint.
                    if (lifestream.GetPlotEntrance(territory, d.Plot - 1) is { } pos)
                    {
                        dalamudUtil.SetMarkerAndOpenMap(pos, map);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Lifestream plot-entrance lookup failed");
                }
            }
            dalamudUtil.OpenMap(map);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Wires the comment-thread ops onto a community edit contract - the
    /// shared <see cref="CommentThreadView"/> calls these for loads, writes,
    /// moderation, reactions and reports. <paramref name="refresh"/> re-pulls
    /// the community DTO so comment counts update.
    /// </summary>
    public void WireCommentOps(SyncshellCommunityView.SyncshellCommunityEdit edit,
        GroupData group, Func<Task> refresh, Func<string, string?>? displayName = null,
        Func<string, SpheneProfileData?>? getProfile = null)
    {
        edit.Comments = (kind, targetId) =>
        {
            var ops = BuildCommentOps(kind, targetId, group, refresh, displayName, getProfile);
            ops.MentionCandidates = edit.MentionCandidates;
            return ops;
        };
    }

    /// <summary>Read-only comment ops for the inert event preview - the real
    /// loads render the actual thread while every write, reaction,
    /// moderation and report hook is stripped, so the shared
    /// <see cref="CommentThreadView"/> suppresses those affordances on its
    /// own. Drafts without a server id (target 0) return empty pages without
    /// a round-trip.</summary>
    public CommentThreadView.CommentOps BuildReadOnlyCommentOps(CommentTargetKind kind,
        long targetId, GroupData group, Func<string, string?>? displayName = null,
        Func<string, SpheneProfileData?>? getProfile = null)
    {
        var ops = BuildCommentOps(kind, targetId, group, () => Task.CompletedTask,
            displayName, getProfile);
        ops.Save = null;
        ops.Delete = null;
        ops.Moderate = null;
        ops.SetReaction = null;
        ops.Report = null;
        if (targetId == 0)
        {
            ops.LoadRoots = _ => Task.FromResult<CommentPageDto?>(null);
            ops.LoadReplies = (_, _) => Task.FromResult<CommentPageDto?>(null);
            ops.ReadReactions = null;
        }
        return ops;
    }

    /// <summary>Builds per-target comment ops - the thread view binds these
    /// when a post's comments expand.</summary>
    private CommentThreadView.CommentOps BuildCommentOps(CommentTargetKind kind,
        long targetId, GroupData group, Func<Task> refresh,
        Func<string, string?>? displayName = null,
        Func<string, SpheneProfileData?>? getProfile = null)
    {
        var ops = new CommentThreadView.CommentOps
        {
            LoadRoots = cursor => Task.Run(async () =>
            {
                try
                {
                    return (CommentPageDto?)await _apiController.GetComments(
                        new CommentPageRequestDto(kind, targetId,
                            GroupGID: group.GID, Cursor: cursor)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load comments for {Kind} {Id}", kind, targetId);
                    return null;
                }
            }),
            LoadReplies = (rootId, cursor) => Task.Run(async () =>
            {
                try
                {
                    return (CommentPageDto?)await _apiController.GetCommentReplies(
                        new CommentPageRequestDto(kind, targetId,
                            GroupGID: group.GID, RootId: rootId, Cursor: cursor))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load replies for root {RootId}", rootId);
                    return null;
                }
            }),
            Save = dto => Task.Run(async () =>
            {
                try
                {
                    var saved = await _apiController.UpsertComment(dto).ConfigureAwait(false);
                    // edits patch the thread in place - only a new comment
                    // changes the target's denormalized counter
                    if (saved != null && dto.CommentId == null)
                    {
                        await refresh().ConfigureAwait(false);
                    }
                    return saved;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save comment on {Kind} {Id}", kind, targetId);
                    return null;
                }
            }),
            Delete = commentId => Task.Run(async () =>
            {
                try
                {
                    var ok = await _apiController.DeleteComment(new CommentUpsertDto(
                        kind, targetId, null, group.GID, string.Empty, null, commentId))
                        .ConfigureAwait(false);
                    if (ok) await refresh().ConfigureAwait(false);
                    return ok;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete comment {Id}", commentId);
                    return false;
                }
            }),
            Moderate = dto => Task.Run(async () =>
            {
                try
                {
                    var result = await _apiController.SetCommentModeration(dto).ConfigureAwait(false);
                    await refresh().ConfigureAwait(false);
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to moderate comment {Id}", dto.CommentId);
                    return null;
                }
            }),
            SetReaction = async dto =>
            {
                try
                {
                    await _apiController.SetReaction(dto with { Group = group }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to set reaction on {Kind} {Id}", dto.TargetKind, dto.TargetId);
                }
            },
            ReadReactions = (rk, id) => _apiController.Reactions.Read(rk, id, null, group),
            Report = dto => Task.Run(async () =>
            {
                try
                {
                    return await _apiController.ReportContent(dto).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to report comment {Id}", dto.TargetId);
                    return false;
                }
            }),
            DisplayName = displayName,
            Profile = getProfile,
            OnAuthorClick = OnAuthorClick,
        };
        return ops;
    }

    /// <summary>
    /// Blog-target comment ops - the profile journal surface binds these per
    /// post. <paramref name="ownerUid"/> is the profile owner's uid; no group.
    /// </summary>
    public CommentThreadView.CommentOps BuildBlogCommentOps(string ownerUid, long postId,
        Func<Task> refresh, Func<string, string?>? displayName = null,
        Func<string, SpheneProfileData?>? getProfile = null)
    {
        // Blog routing carries the owner uid on every request instead.
        var wrapped = new CommentThreadView.CommentOps
        {
            LoadRoots = cursor => Task.Run(async () =>
            {
                try
                {
                    return (CommentPageDto?)await _apiController.GetComments(
                        new CommentPageRequestDto(CommentTargetKind.Blog, postId,
                            OwnerUid: ownerUid, Cursor: cursor)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load comments for blog {Id}", postId);
                    return null;
                }
            }),
            LoadReplies = (rootId, cursor) => Task.Run(async () =>
            {
                try
                {
                    return (CommentPageDto?)await _apiController.GetCommentReplies(
                        new CommentPageRequestDto(CommentTargetKind.Blog, postId,
                            OwnerUid: ownerUid, RootId: rootId, Cursor: cursor))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load replies for blog {Id}", postId);
                    return null;
                }
            }),
            Save = dto => Task.Run(async () =>
            {
                try
                {
                    var saved = await _apiController.UpsertComment(dto with
                        { OwnerUid = ownerUid }).ConfigureAwait(false);
                    // edits patch the thread in place - only a new comment
                    // changes the target's denormalized counter
                    if (saved != null && dto.CommentId == null)
                    {
                        await refresh().ConfigureAwait(false);
                    }
                    return saved;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save comment on blog {Id}", postId);
                    return null;
                }
            }),
            Delete = commentId => Task.Run(async () =>
            {
                try
                {
                    var ok = await _apiController.DeleteComment(new CommentUpsertDto(
                        CommentTargetKind.Blog, postId, ownerUid, null, string.Empty,
                        null, commentId)).ConfigureAwait(false);
                    if (ok) await refresh().ConfigureAwait(false);
                    return ok;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete comment {Id}", commentId);
                    return false;
                }
            }),
            Moderate = dto => Task.Run(async () =>
            {
                try
                {
                    var result = await _apiController.SetCommentModeration(dto with
                        { OwnerUid = ownerUid }).ConfigureAwait(false);
                    await refresh().ConfigureAwait(false);
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to moderate comment {Id}", dto.CommentId);
                    return null;
                }
            }),
            SetReaction = async dto =>
            {
                try
                {
                    await _apiController.SetReaction(dto with
                        { OwnerUid = ownerUid }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to set reaction on blog {Id}", postId);
                }
            },
            ReadReactions = (rk, id) => _apiController.Reactions.Read(rk, id, ownerUid, null),
            Report = dto => Task.Run(async () =>
            {
                try
                {
                    return await _apiController.ReportContent(dto with
                        { OwnerUid = ownerUid }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to report comment {Id}", dto.TargetId);
                    return false;
                }
            }),
            DisplayName = displayName,
            Profile = getProfile,
            OnAuthorClick = OnAuthorClick,
        };
        return wrapped;
    }

    private static int? ParseCap(string text)
        => int.TryParse(text.Trim(), out var cap) && cap > 0 ? cap : null;

    private static string? RecurrenceRule(int preset) => preset switch
    {
        1 => "FREQ=DAILY",
        2 => "FREQ=WEEKLY",
        3 => "FREQ=WEEKLY;INTERVAL=2",
        4 => "FREQ=MONTHLY",
        _ => null,
    };

    private static List<int>? ParseReminders(string text)
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

    /// <summary>Deletes a post through the feed API, then refreshes the host.</summary>
    public void DeleteNews(GroupData group, SyncshellNewsPostDto post, Func<Task> refresh)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupDeleteCommunityNews(
                    new SyncshellCommunityItemDeleteDto(group, post.Id)).ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete news post {PostId}", post.Id);
            }
        });
    }

    /// <summary>Deletes an event through the feed API, then refreshes the host.</summary>
    public void DeleteEvent(GroupData group, SyncshellEventDto ev, Func<Task> refresh)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.GroupDeleteCommunityEvent(
                    new SyncshellCommunityItemDeleteDto(group, ev.Id)).ConfigureAwait(false);
                await refresh().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete event {EventId}", ev.Id);
            }
        });
    }

    /// <summary>
    /// Opens the photo studio's file browser at the gallery affordance · the
    /// picked image goes through crop/adjust and only the rendered result uploads.
    /// </summary>
    public void PickPhoto(GroupData group, int sortOrder, Func<Task> refresh, bool staff = true)
    {
        _mediator.Publish(new OpenPhotoStudioMessage(null, PhotoStudioTarget.CommunityPhoto,
            prepared =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (base64, hash) = await ShipImageAsync(prepared.Bytes,
                            prepared.ContentType, prepared.Base64).ConfigureAwait(false);
                        var dto = new SyncshellGalleryImageUpdateDto(0, group, base64,
                            prepared.FileName, prepared.ContentType, prepared.Bytes.Length,
                            null, sortOrder, null, ImageMediaHash: hash);
                        var saved = staff
                            ? await _apiController.GroupUpsertCommunityGalleryImage(dto).ConfigureAwait(false)
                            : await _apiController.GroupSubmitCommunityGalleryImage(dto).ConfigureAwait(false);
                        if (saved != null)
                        {
                            await refresh().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to upload gallery image for {GID}", group.GID);
                    }
                });
            }));
    }

    /// <summary>Opens the studio's file browser at the banner/avatar affordance  · 
    /// the rendered result patches the profile image. When <paramref name="stage"/>
    /// is set the patched DTO is handed to it instead of saving - the host keeps
    /// it as a pending change until its apply flow commits.</summary>
    public void PickProfileImage(SyncshellCommunityDto community, GroupData group,
        string texturePrefix, bool banner, Func<Task> refresh,
        Action<SyncshellCommunityDto>? stage = null)
    {
        _mediator.Publish(new OpenPhotoStudioMessage(null,
            banner ? PhotoStudioTarget.CommunityBanner : PhotoStudioTarget.CommunityAvatar,
            prepared =>
            {
                if (stage != null)
                {
                    _ = StagePickedImageAsync(prepared, community, banner, stage);
                    return;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (base64, hash) = await ShipImageAsync(prepared.Bytes,
                            prepared.ContentType, prepared.Base64).ConfigureAwait(false);
                        var profile = community.Profile;
                        var patched = community with
                        {
                            Profile = banner
                                ? profile with
                                {
                                    BannerImageBase64 = base64,
                                    BannerMediaHash = hash,
                                    BannerFileName = prepared.FileName,
                                    BannerContentType = prepared.ContentType,
                                    BannerSize = prepared.Bytes.LongLength
                                }
                                : profile with
                                {
                                    AvatarImageBase64 = base64,
                                    AvatarMediaHash = hash,
                                    AvatarFileName = prepared.FileName,
                                    AvatarContentType = prepared.ContentType,
                                    AvatarSize = prepared.Bytes.LongLength
                                }
                        };
                        var ok = await _apiController.GroupSetCommunity(
                            ToUpdateDto(patched, group)).ConfigureAwait(false);
                        if (ok)
                        {
                            await refresh().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to upload community image for {GID}", group.GID);
                    }
                });
            }));
    }

    /// <summary>Image-picker path for staged edits · ships the rendered bytes
    /// (media upload or base64 fallback) and hands the patched aggregate to the
    /// host's draft instead of persisting it.</summary>
    private async Task StagePickedImageAsync(ImageUploadProcessor.Prepared prepared,
        SyncshellCommunityDto community, bool banner, Action<SyncshellCommunityDto> stage)
    {
        try
        {
            var (base64, hash) = await ShipImageAsync(prepared.Bytes,
                prepared.ContentType, prepared.Base64).ConfigureAwait(false);
            var profile = community.Profile;
            var patched = community with
            {
                Profile = banner
                    ? profile with
                    {
                        BannerImageBase64 = base64,
                        BannerMediaHash = hash,
                        BannerFileName = prepared.FileName,
                        BannerContentType = prepared.ContentType,
                        BannerSize = prepared.Bytes.LongLength
                    }
                    : profile with
                    {
                        AvatarImageBase64 = base64,
                        AvatarMediaHash = hash,
                        AvatarFileName = prepared.FileName,
                        AvatarContentType = prepared.ContentType,
                        AvatarSize = prepared.Bytes.LongLength
                    }
            };
            stage(patched);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stage community image");
        }
    }

    /// <summary>
    /// Builds the page's structural ops bundle · every affordance maps onto an
    /// API call followed by the host's refresh. The community getter is invoked
    /// at call time so patches always apply to the freshest aggregate.
    /// </summary>
    public SyncshellCommunityView.CommunityManageOps CreateOps(GroupData group,
        Func<SyncshellCommunityDto?> community, Func<Task> refresh,
        Func<IReadOnlyList<SyncshellCommunityView.CommunityStaffMember>>? getStaffMembers = null,
        Action<Func<SyncshellCommunityDto, SyncshellCommunityDto>>? mutate = null)
        => new()
        {
            Patch = patch => _ = community() is { } c0
                ? PatchCommunity(c0, group, patch, refresh) : Task.CompletedTask,
            CreateStaffRole = name => _ = Task.Run(async () =>
            {
                try
                {
                    var c = community();
                    var saved = await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(0, group, name, 0xFF85C8FF, null,
                            SyncshellStaffPermission.None,
                            (c?.StaffRoles?.Max(r => (int?)r.SortOrder) ?? 0) + 1))
                        .ConfigureAwait(false);
                    if (saved != null) await refresh().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to create staff role"); }
            }),
            SaveStaffRole = role => _ = Task.Run(async () =>
            {
                try
                {
                    var saved = await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(role.Id, group, role.Name, role.ColorArgb,
                            role.Icon, role.Permissions, role.SortOrder)).ConfigureAwait(false);
                    if (saved != null) await refresh().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save staff role {RoleId}", role.Id); }
            }),
            DeleteStaffRole = role => _ = Task.Run(async () =>
            {
                try
                {
                    await _apiController.GroupDeleteCommunityStaffRole(
                        new SyncshellCommunityItemDeleteDto(group, role.Id)).ConfigureAwait(false);
                    await refresh().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to delete staff role {RoleId}", role.Id); }
            }),
            MoveStaffRole = (role, dir) => _ = Task.Run(async () =>
            {
                try
                {
                    var ordered = (community()?.StaffRoles ?? []).OrderBy(r => r.SortOrder).ToList();
                    var idx = ordered.FindIndex(r => r.Id == role.Id);
                    var swap = idx + dir;
                    if (idx < 0 || swap < 0 || swap >= ordered.Count) return;
                    var other = ordered[swap];
                    // Sequential · each upsert re-maps the role list.
                    await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(role.Id, group, role.Name, role.ColorArgb,
                            role.Icon, role.Permissions, swap)).ConfigureAwait(false);
                    await _apiController.GroupUpsertCommunityStaffRole(
                        new SyncshellStaffRoleUpdateDto(other.Id, group, other.Name, other.ColorArgb,
                            other.Icon, other.Permissions, idx)).ConfigureAwait(false);
                    await refresh().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to reorder staff role {RoleId}", role.Id); }
            }),
            SetStaffAssignment = (uid, roleIds) => _ = Task.Run(async () =>
            {
                try
                {
                    await _apiController.GroupSetCommunityStaffAssignment(
                        new SyncshellStaffAssignmentUpdateDto(group, uid, null,
                            roleIds.ToList())).ConfigureAwait(false);
                    await refresh().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to set staff assignment for {Uid}", uid); }
            }),
            CreateAlbum = name => Task.Run(async () =>
            {
                try
                {
                    var saved = await _apiController.GroupUpsertCommunityGalleryAlbum(
                        new SyncshellGalleryAlbumUpdateDto(0, group, name, null, null, null,
                            community()?.Albums?.Count ?? 0)).ConfigureAwait(false);
                    if (saved == null) return false;
                    mutate?.Invoke(c => c with { Albums = [.. c.Albums ?? [], saved] });
                    await refresh().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to create gallery album"); return false; }
            }),
            SaveAlbum = album => Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug(
                        "Saving album {AlbumId} name={Name} audience={Audience} event={LinkedEventId} cover={CoverImageId}",
                        album.Id, album.Name, album.Audience, album.LinkedEventId, album.CoverImageId);
                    var saved = await _apiController.GroupUpsertCommunityGalleryAlbum(
                        new SyncshellGalleryAlbumUpdateDto(album.Id, group, album.Name,
                            album.Description, album.LinkedEventId, album.CoverImageId,
                            album.SortOrder, (int)album.Audience, album.AllowedUids)).ConfigureAwait(false);
                    if (saved == null)
                    {
                        _logger.LogWarning("Album {AlbumId} save rejected by server", album.Id);
                        return false;
                    }
                    _logger.LogDebug("Album {AlbumId} saved as {Name}", saved.Id, saved.Name);
                    mutate?.Invoke(c => c with
                        { Albums = c.Albums?.Select(a => a.Id == saved.Id ? saved : a).ToList() });
                    await refresh().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save gallery album {AlbumId}", album.Id); return false; }
            }),
            DeleteAlbum = album => Task.Run(async () =>
            {
                try
                {
                    var ok = await _apiController.GroupDeleteCommunityGalleryAlbum(
                        new SyncshellCommunityItemDeleteDto(group, album.Id)).ConfigureAwait(false);
                    if (!ok) return false;
                    mutate?.Invoke(c => c with
                        { Albums = c.Albums?.Where(a => a.Id != album.Id).ToList() });
                    await refresh().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to delete gallery album {AlbumId}", album.Id); return false; }
            }),
            AssignImageAlbum = (img, albumId) => _ = Task.Run(async () =>
            {
                try
                {
                    var saved = await _apiController.GroupUpsertCommunityGalleryImage(
                        new SyncshellGalleryImageUpdateDto(img.Id, group, null, null, null, null,
                            img.Caption, img.SortOrder, albumId)).ConfigureAwait(false);
                    if (saved != null) await refresh().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to assign image {ImageId} to album", img.Id); }
            }),
            SaveImage = img => Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug(
                        "Saving gallery image {ImageId} caption={Caption} album={AlbumId} audience={Audience}",
                        img.Id, img.Caption, img.AlbumId, img.Audience);
                    var saved = await _apiController.GroupUpsertCommunityGalleryImage(
                        new SyncshellGalleryImageUpdateDto(img.Id, group, null, null, null, null,
                            img.Caption, img.SortOrder, img.AlbumId,
                            (int)img.Audience, img.AllowedUids)).ConfigureAwait(false);
                    if (saved == null)
                    {
                        _logger.LogWarning("Gallery image {ImageId} save rejected by server", img.Id);
                        return false;
                    }
                    _logger.LogDebug("Gallery image {ImageId} saved", saved.Id);
                    mutate?.Invoke(c => c with
                        { Gallery = c.Gallery?.Select(g => g.Id == saved.Id ? saved : g).ToList() });
                    await refresh().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to save gallery image {ImageId}", img.Id); return false; }
            }),
            DeleteImage = img => _ = Task.Run(async () =>
            {
                try
                {
                    await _apiController.GroupDeleteCommunityGalleryImage(
                        new SyncshellCommunityItemDeleteDto(group, img.Id)).ConfigureAwait(false);
                    await refresh().ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to delete gallery image {ImageId}", img.Id); }
            }),
            GetStaffMembers = getStaffMembers,
        };

    /// <summary>Saves a patched community aggregate through <c>GroupSetCommunity</c>, then refreshes.</summary>
    public Task PatchCommunity(SyncshellCommunityDto community, GroupData group,
        Func<SyncshellCommunityDto, SyncshellCommunityDto> patch, Func<Task> refresh)
    {
        return Task.Run(async () =>
        {
            try
            {
                var ok = await _apiController.GroupSetCommunity(
                    ToUpdateDto(patch(community), group)).ConfigureAwait(false);
                if (ok)
                {
                    await refresh().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save syncshell community for {GID}", group.GID);
            }
        });
    }

    /// <summary>Maps a read model back onto the static-parts update contract.</summary>
    public static SyncshellCommunityUpdateDto ToUpdateDto(SyncshellCommunityDto community, GroupData group)
    {
        var p = community.Profile;
        return new SyncshellCommunityUpdateDto(group, p.Tagline, p.Description,
            p.BannerImageBase64, p.BannerFileName, p.BannerContentType, p.BannerSize,
            p.AvatarImageBase64, p.AvatarFileName, p.AvatarContentType, p.AvatarSize,
            p.AccentColor, p.Sections, community.RulesText, community.Links,
            community.VisibleSections, community.IsListedPublicly, p.IsEnabled,
            p.ShowOnJoin, p.ShowOnAreaBoundJoin, community.OverviewWidgets,
            p.BannerMediaHash, p.AvatarMediaHash,
            ClearBanner: p.BannerMediaHash == null && string.IsNullOrEmpty(p.BannerImageBase64),
            ClearAvatar: p.AvatarMediaHash == null && string.IsNullOrEmpty(p.AvatarImageBase64));
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
