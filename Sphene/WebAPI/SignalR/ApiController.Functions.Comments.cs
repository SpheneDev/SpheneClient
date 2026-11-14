using Microsoft.AspNetCore.SignalR.Client;
using Sphene.API.Dto.Group;

namespace Sphene.WebAPI;

/// <summary>Phase 4 hub wrappers - comments, reactions, reports, moderation
/// queues and notification mutes. Mirrors ApiController.Functions.Groups.cs.</summary>
public partial class ApiController
{
    /// <summary>Root comments page - keyset cursor on dto.Cursor.</summary>
    public async Task<CommentPageDto> GetComments(CommentPageRequestDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<CommentPageDto>(nameof(GetComments), dto).ConfigureAwait(false);
    }

    /// <summary>Replies under one root - ascending id order.</summary>
    public async Task<CommentPageDto> GetCommentReplies(CommentPageRequestDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<CommentPageDto>(nameof(GetCommentReplies), dto).ConfigureAwait(false);
    }

    /// <summary>Create a root/reply or edit an existing comment.</summary>
    public async Task<CommunityCommentDto?> UpsertComment(CommentUpsertDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<CommunityCommentDto?>(nameof(UpsertComment), dto).ConfigureAwait(false);
    }

    /// <summary>Author soft-delete (tombstone).</summary>
    public async Task<bool> DeleteComment(CommentUpsertDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(DeleteComment), dto).ConfigureAwait(false);
    }

    /// <summary>Moderation action - hide/restore/remove/lock/unlock.</summary>
    public async Task<CommunityCommentDto?> SetCommentModeration(CommentModerationDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<CommunityCommentDto?>(nameof(SetCommentModeration), dto).ConfigureAwait(false);
    }

    /// <summary>State-based reaction write - Set=true ensures the row exists.</summary>
    public async Task<ReactionSummaryDto> SetReaction(ReactionSetDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<ReactionSummaryDto>(nameof(SetReaction), dto).ConfigureAwait(false);
    }

    /// <summary>Aggregate counts + viewer kinds for one target.</summary>
    public async Task<ReactionSummaryDto> GetReactionSummary(ReactionSetDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<ReactionSummaryDto>(nameof(GetReactionSummary), dto).ConfigureAwait(false);
    }

    /// <summary>Who-reacted listing, newest first.</summary>
    public async Task<ReactionUsersDto> GetReactionUsers(ReactionUsersRequestDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<ReactionUsersDto>(nameof(GetReactionUsers), dto).ConfigureAwait(false);
    }

    /// <summary>File a content report - server dedupes open reports.</summary>
    public async Task<bool> ReportContent(ReportCreateDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(ReportContent), dto).ConfigureAwait(false);
    }

    /// <summary>Community moderation queue - ModerateContent holders only.</summary>
    public async Task<ModerationQueueDto> GetModerationQueue(GroupDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<ModerationQueueDto>(nameof(GetModerationQueue), dto).ConfigureAwait(false);
    }

    /// <summary>Resolve/dismiss a community report.</summary>
    public async Task<bool> ResolveReport(ReportDecisionDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(ResolveReport), dto).ConfigureAwait(false);
    }

    /// <summary>Platform queue - blog reports, platform moderators only.</summary>
    public async Task<PlatformReportQueueDto> GetPlatformReportQueue()
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<PlatformReportQueueDto>(nameof(GetPlatformReportQueue)).ConfigureAwait(false);
    }

    /// <summary>Resolve/dismiss a platform-scoped blog report.</summary>
    public async Task<bool> ResolvePlatformReport(ReportDecisionDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(ResolvePlatformReport), dto).ConfigureAwait(false);
    }

    /// <summary>User-facing notification mute mask.</summary>
    public async Task<bool> UserSetNotificationMutes(long disabledKindsMask)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(UserSetNotificationMutes), disabledKindsMask).ConfigureAwait(false);
    }

    /// <summary>Reads the caller's notification mute mask.</summary>
    public async Task<long> UserGetNotificationMutes()
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<long>(nameof(UserGetNotificationMutes)).ConfigureAwait(false);
    }
}
