using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.CharaData;
using Sphene.WebAPI.SignalR.Utils;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;

namespace Sphene.WebAPI;

public partial class ApiController
{
    public async Task GroupBanUser(GroupPairDto dto, string reason)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupBanUser), dto, reason).ConfigureAwait(false);
    }

    public async Task GroupChangeGroupPermissionState(GroupPermissionDto dto)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupChangeGroupPermissionState), dto).ConfigureAwait(false);
    }

    public async Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto)
    {
        CheckConnection();
        await SetBulkPermissions(new(new(StringComparer.Ordinal),
            new(StringComparer.Ordinal) {
                { dto.Group.GID, dto.GroupPairPermissions }
            })).ConfigureAwait(false);
    }

    public async Task GroupChangeOwnership(GroupPairDto groupPair)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupChangeOwnership), groupPair).ConfigureAwait(false);
    }

    public async Task<bool> GroupChangePassword(GroupPasswordDto groupPassword)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupChangePassword), groupPassword).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetAlias(GroupAliasDto groupAlias)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetAlias), groupAlias).ConfigureAwait(false);
    }

    public async Task GroupClear(GroupDto group)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupClear), group).ConfigureAwait(false);
    }

    public async Task<GroupJoinDto> GroupCreate(GroupCreateDto? dto = null)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<GroupJoinDto>(nameof(GroupCreate), dto).ConfigureAwait(false);
    }

    public async Task<List<string>> GroupCreateTempInvite(GroupDto group, int amount)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<string>>(nameof(GroupCreateTempInvite), group, amount).ConfigureAwait(false);
    }

    public async Task GroupDelete(GroupDto group)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupDelete), group).ConfigureAwait(false);
    }

    public async Task<List<BannedGroupUserDto>> GroupGetBannedUsers(GroupDto group)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<BannedGroupUserDto>>(nameof(GroupGetBannedUsers), group).ConfigureAwait(false);
    }

    public async Task<GroupJoinInfoDto> GroupJoin(GroupPasswordDto passwordedGroup)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<GroupJoinInfoDto>(nameof(GroupJoin), passwordedGroup).ConfigureAwait(false);
    }

    public async Task<bool> GroupJoinFinalize(GroupJoinDto passwordedGroup)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupJoinFinalize), passwordedGroup).ConfigureAwait(false);
    }

    /// <summary>Effective join policy for a shell (public probe).</summary>
    public async Task<SyncshellJoinPolicy> GroupGetJoinPolicy(GroupDto group)
    {
        if (!IsConnected) return SyncshellJoinPolicy.Closed;
        return await _spheneHub!.InvokeAsync<SyncshellJoinPolicy>(nameof(GroupGetJoinPolicy), group).ConfigureAwait(false);
    }

    /// <summary>Sets the shell join policy - moderator-or-owner gated server-side.</summary>
    public async Task<bool> GroupSetJoinPolicy(GroupJoinPolicyDto dto)
    {
        if (!IsConnected) return false;
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetJoinPolicy), dto).ConfigureAwait(false);
    }

    /// <summary>Submits (or resubmits) a membership application.</summary>
    public async Task<bool> GroupApplyForMembership(GroupApplyMembershipDto dto)
    {
        if (!IsConnected) return false;
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupApplyForMembership), dto).ConfigureAwait(false);
    }

    /// <summary>Withdraws the caller's pending application.</summary>
    public async Task<bool> GroupWithdrawMembershipApplication(GroupDto group)
    {
        if (!IsConnected) return false;
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupWithdrawMembershipApplication), group).ConfigureAwait(false);
    }

    /// <summary>The caller's own application for a shell - status tracking.</summary>
    public async Task<MembershipApplicationDto?> GroupGetMyMembershipApplication(GroupDto group)
    {
        if (!IsConnected) return null;
        return await _spheneHub!.InvokeAsync<MembershipApplicationDto?>(nameof(GroupGetMyMembershipApplication), group).ConfigureAwait(false);
    }

    /// <summary>Pending + recent decided applications - ManageMembership rights required.</summary>
    public async Task<List<MembershipApplicationDto>> GroupGetMembershipApplications(GroupDto group)
    {
        if (!IsConnected) return [];
        return await _spheneHub!.InvokeAsync<List<MembershipApplicationDto>>(nameof(GroupGetMembershipApplications), group).ConfigureAwait(false);
    }

    /// <summary>Approves or rejects a pending application - ManageMembership rights required.</summary>
    public async Task<MembershipApplicationDto?> GroupDecideMembershipApplication(GroupMembershipDecisionDto dto)
    {
        if (!IsConnected) return null;
        return await _spheneHub!.InvokeAsync<MembershipApplicationDto?>(nameof(GroupDecideMembershipApplication), dto).ConfigureAwait(false);
    }

    public async Task GroupLeave(GroupDto group)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupLeave), group).ConfigureAwait(false);
    }

    public async Task GroupRemoveUser(GroupPairDto groupPair)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupRemoveUser), groupPair).ConfigureAwait(false);
    }

    public async Task GroupSetUserInfo(GroupPairUserInfoDto groupPair)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupSetUserInfo), groupPair).ConfigureAwait(false);
    }

    public async Task<int> GroupPrune(GroupDto group, int days, bool execute)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<int>(nameof(GroupPrune), group, days, execute).ConfigureAwait(false);
    }

    public async Task<List<GroupFullInfoDto>> GroupsGetAll()
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<GroupFullInfoDto>>(nameof(GroupsGetAll)).ConfigureAwait(false);
    }

    public async Task GroupUnbanUser(GroupPairDto groupPair)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupUnbanUser), groupPair).ConfigureAwait(false);
    }

    public async Task AreaBoundJoinRequest(string gid)
    {
        CheckConnection();
        
        // Create the DTO with current user and location information
        var currentUser = new UserData(UID);
        var currentLocation = await _dalamudUtil.GetMapDataAsync().ConfigureAwait(false);
        var groupData = new GroupData(gid);
        
        var joinRequestDto = new AreaBoundJoinRequestDto(groupData, currentUser, currentLocation);
        
        await _spheneHub!.InvokeAsync<bool>(nameof(GroupRequestAreaBoundJoin), joinRequestDto).ConfigureAwait(false);
    }

    public async Task<List<AreaBoundSyncshellDto>> GetAreaBoundSyncshells()
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<AreaBoundSyncshellDto>>(nameof(GetAreaBoundSyncshells)).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetAreaBinding(AreaBoundSyncshellDto areaBoundSyncshell)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetAreaBinding), areaBoundSyncshell).ConfigureAwait(false);
    }

    public async Task<bool> GroupRemoveAreaBinding(GroupDto group)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupRemoveAreaBinding), group).ConfigureAwait(false);
    }

    public async Task<List<AreaBoundSyncshellDto>> GroupGetAreaBoundSyncshells()
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<AreaBoundSyncshellDto>>(nameof(GroupGetAreaBoundSyncshells)).ConfigureAwait(false);
    }

    public async Task<bool> GroupRequestAreaBoundJoin(AreaBoundJoinRequestDto joinRequest)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupRequestAreaBoundJoin), joinRequest).ConfigureAwait(false);
    }

    public async Task GroupRespondToAreaBoundJoin(AreaBoundJoinResponseDto joinResponse)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(GroupRespondToAreaBoundJoin), joinResponse).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetAreaBoundConsent(AreaBoundJoinConsentRequestDto consentRequest)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetAreaBoundConsent), consentRequest).ConfigureAwait(false);
    }

    public async Task<bool> GroupCheckAreaBoundConsent(string syncshellGID)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupCheckAreaBoundConsent), syncshellGID).ConfigureAwait(false);
    }

    public async Task<bool> GroupResetAreaBoundConsent(string syncshellGID)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupResetAreaBoundConsent), syncshellGID).ConfigureAwait(false);
    }

    public async Task<SyncshellWelcomePageDto?> GroupGetWelcomePage(GroupDto group)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellWelcomePageDto?>(nameof(GroupGetWelcomePage), group).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetWelcomePage(SyncshellWelcomePageUpdateDto welcomePageUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetWelcomePage), welcomePageUpdate).ConfigureAwait(false);
    }

    public async Task<bool> GroupDeleteWelcomePage(GroupDto group)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDeleteWelcomePage), group).ConfigureAwait(false);
    }

    public async Task<SyncshellProfileDto?> GroupGetSyncshellProfile(GroupDto group)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellProfileDto?>(nameof(GroupGetSyncshellProfile), group).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetSyncshellProfile(SyncshellProfileUpdateDto profileUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetSyncshellProfile), profileUpdate).ConfigureAwait(false);
    }

    public async Task<SyncshellCommunityDto?> GroupGetCommunity(GroupDto group,
        CancellationToken ct = default)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellCommunityDto?>(nameof(GroupGetCommunity), group, ct).ConfigureAwait(false);
    }

    /// <summary>Profile-only community read for list/card surfaces - avoids the
    /// full <see cref="GroupGetCommunity"/> aggregate per syncshell row.</summary>
    public async Task<SyncshellProfileDto?> GroupGetCommunityProfile(GroupDto group,
        CancellationToken ct = default)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellProfileDto?>(nameof(GroupGetCommunityProfile), group, ct).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetCommunity(SyncshellCommunityUpdateDto communityUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetCommunity), communityUpdate).ConfigureAwait(false);
    }

    /// <summary>Publishes, updates or unpublishes the shell's permanent
    /// community location - ManagePage-gated, housing addresses only.</summary>
    public async Task<bool> GroupSetCommunityDestination(SyncshellCommunityDestinationUpdateDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetCommunityDestination), dto).ConfigureAwait(false);
    }

    public async Task<SyncshellEventDto?> GroupUpsertCommunityEvent(SyncshellEventUpdateDto eventUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellEventDto?>(nameof(GroupUpsertCommunityEvent), eventUpdate).ConfigureAwait(false);
    }

    public async Task<bool> GroupDeleteCommunityEvent(SyncshellCommunityItemDeleteDto delete)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDeleteCommunityEvent), delete).ConfigureAwait(false);
    }

    public async Task<SyncshellNewsPostDto?> GroupUpsertCommunityNews(SyncshellNewsPostUpdateDto newsUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellNewsPostDto?>(nameof(GroupUpsertCommunityNews), newsUpdate).ConfigureAwait(false);
    }

    public async Task<bool> GroupDeleteCommunityNews(SyncshellCommunityItemDeleteDto delete)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDeleteCommunityNews), delete).ConfigureAwait(false);
    }

    public async Task<SyncshellGalleryImageDto?> GroupUpsertCommunityGalleryImage(SyncshellGalleryImageUpdateDto imageUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellGalleryImageDto?>(nameof(GroupUpsertCommunityGalleryImage), imageUpdate).ConfigureAwait(false);
    }

    public async Task<List<SyncshellDirectoryEntryDto>> GroupGetCommunityDirectory()
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<SyncshellDirectoryEntryDto>>(nameof(GroupGetCommunityDirectory)).ConfigureAwait(false);
    }

    public async Task<SyncshellCommunityFeedDto> GroupGetCommunityFeed()
    {
        CheckConnection();
        return (await _spheneHub!.InvokeAsync<SyncshellCommunityFeedDto?>(nameof(GroupGetCommunityFeed)).ConfigureAwait(false))
            ?? new SyncshellCommunityFeedDto([]);
    }

    public async Task<SyncshellCommunityFeedPageDto> GroupGetCommunityFeedPage(FeedPageRequestDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellCommunityFeedPageDto>(nameof(GroupGetCommunityFeedPage), dto).ConfigureAwait(false);
    }

    public async Task<SyncshellCommunityFeedPageDto> UserGetProfileActivity(string targetUid, FeedPageRequestDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellCommunityFeedPageDto>(nameof(UserGetProfileActivity), targetUid, dto).ConfigureAwait(false);
    }

    public async Task<SyncshellCommunityDto?> GroupGetPublicCommunity(GroupDto group)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellCommunityDto?>(nameof(GroupGetPublicCommunity), group).ConfigureAwait(false);
    }

    public async Task<bool> GroupDeleteCommunityGalleryImage(SyncshellCommunityItemDeleteDto delete)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDeleteCommunityGalleryImage), delete).ConfigureAwait(false);
    }

    public async Task<SyncshellGalleryAlbumDto?> GroupUpsertCommunityGalleryAlbum(SyncshellGalleryAlbumUpdateDto albumUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellGalleryAlbumDto?>(nameof(GroupUpsertCommunityGalleryAlbum), albumUpdate).ConfigureAwait(false);
    }

    public async Task<bool> GroupDeleteCommunityGalleryAlbum(SyncshellCommunityItemDeleteDto delete)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDeleteCommunityGalleryAlbum), delete).ConfigureAwait(false);
    }

    public async Task<SyncshellStaffRoleDto?> GroupUpsertCommunityStaffRole(SyncshellStaffRoleUpdateDto roleUpdate)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellStaffRoleDto?>(nameof(GroupUpsertCommunityStaffRole), roleUpdate).ConfigureAwait(false);
    }

    public async Task<bool> GroupDeleteCommunityStaffRole(SyncshellCommunityItemDeleteDto delete)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDeleteCommunityStaffRole), delete).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetCommunityStaffAssignment(SyncshellStaffAssignmentUpdateDto assignment)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetCommunityStaffAssignment), assignment).ConfigureAwait(false);
    }

    /// <summary>Drops the caller's staff affiliation for a shell - sent
    /// before every explicit leave so a re-join starts clean.</summary>
    public async Task GroupRevokeOwnStaffAffiliation(GroupDto group)
    {
        CheckConnection();
        await _spheneHub!.InvokeAsync<bool>(nameof(GroupRevokeOwnStaffAffiliation), group).ConfigureAwait(false);
    }

    /// <summary>Bookmark/unbookmark a publicly viewable shell - keeps its
    /// community page reachable from the Followed surface without joining.</summary>
    public async Task<bool> GroupSetSyncshellFavorite(GroupDto group, bool favorite)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(
            nameof(GroupSetSyncshellFavorite), group, favorite).ConfigureAwait(false);
    }

    /// <summary>The caller's bookmarked shells as directory-shaped entries.</summary>
    public async Task<List<SyncshellDirectoryEntryDto>> GroupGetFavorites()
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<SyncshellDirectoryEntryDto>>(
            nameof(GroupGetFavorites)).ConfigureAwait(false);
    }

    /// <summary>Voluntary leave - revokes the caller's staff affiliation
    /// first so a later re-join starts without roles, then leaves.
    /// Area-bound auto-leave intentionally skips the revoke (staff retain
    /// administrative access outside bounds).</summary>
    public async Task GroupLeaveFully(GroupDto group)
    {
        try
        {
            await GroupRevokeOwnStaffAffiliation(group).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Staff affiliation revoke failed for {Gid} - leaving anyway", group.Group.GID);
        }
        await GroupLeave(group).ConfigureAwait(false);
    }

    public async Task<SyncshellPublishingPolicyDto> GroupGetPublishingPolicy(GroupDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellPublishingPolicyDto>(nameof(GroupGetPublishingPolicy), dto).ConfigureAwait(false);
    }

    public async Task<bool> GroupSetPublishingPolicy(SyncshellPublishingPolicyDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupSetPublishingPolicy), dto).ConfigureAwait(false);
    }

    /// <summary>Member submission path - the server resolves the effective
    /// status from the shell policy; the result may be queued for review.</summary>
    public async Task<SyncshellNewsPostDto?> GroupSubmitCommunityNews(SyncshellNewsPostUpdateDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellNewsPostDto?>(nameof(GroupSubmitCommunityNews), dto).ConfigureAwait(false);
    }

    public async Task<bool> GroupDecideCommunityNews(CommunityReviewDecisionDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDecideCommunityNews), dto).ConfigureAwait(false);
    }

    public async Task<CommunityReviewQueueDto> GroupGetCommunityReviewQueue(GroupDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<CommunityReviewQueueDto>(nameof(GroupGetCommunityReviewQueue), dto).ConfigureAwait(false);
    }

    public async Task<SyncshellGalleryImageDto?> GroupSubmitCommunityGalleryImage(SyncshellGalleryImageUpdateDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellGalleryImageDto?>(nameof(GroupSubmitCommunityGalleryImage), dto).ConfigureAwait(false);
    }

    public async Task<bool> GroupDecideCommunityGalleryImage(CommunityReviewDecisionDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDecideCommunityGalleryImage), dto).ConfigureAwait(false);
    }

    /// <summary>Server-side entity reference resolution - cards are already
    /// visibility-checked; <c>Resolved=false</c> means render as plain text.</summary>
    public async Task<List<EntityCardDto>> EntityResolveReferences(List<string> tokens)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<EntityCardDto>>(nameof(EntityResolveReferences), tokens).ConfigureAwait(false);
    }

    public async Task<CommunityMediaUploadInfoDto> GroupGetCommunityMediaUploadInfo(GroupDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<CommunityMediaUploadInfoDto>(nameof(GroupGetCommunityMediaUploadInfo), dto).ConfigureAwait(false);
    }

    /// <summary>RSVP write - the server coerces Attending → Waitlist at
    /// capacity and returns the resulting participant summary.</summary>
    public async Task<EventParticipantsDto> GroupSetEventRsvp(EventRsvpDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<EventParticipantsDto>(nameof(GroupSetEventRsvp), dto).ConfigureAwait(false);
    }

    /// <summary>Participant summary - Attending-only for members, full rows for staff.</summary>
    public async Task<EventParticipantsDto> GroupGetEventParticipants(EventRsvpDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<EventParticipantsDto>(nameof(GroupGetEventParticipants), dto).ConfigureAwait(false);
    }

    /// <summary>Staff participant override (seat, waitlist reorder, remove).</summary>
    public async Task<EventParticipantsDto> GroupUpdateEventParticipant(EventRsvpDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<EventParticipantsDto>(nameof(GroupUpdateEventParticipant), dto).ConfigureAwait(false);
    }

    /// <summary>Expanded occurrence feed - recurring series resolved server-side.</summary>
    public async Task<List<SyncshellEventOccurrenceDto>> GroupGetEventCalendar(
        SyncshellEventCalendarRequestDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<List<SyncshellEventOccurrenceDto>>(nameof(GroupGetEventCalendar), dto).ConfigureAwait(false);
    }

    /// <summary>Occurrence exception upsert/remove - staff/organizer only.</summary>
    public async Task<bool> GroupUpsertEventException(SyncshellEventExceptionDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupUpsertEventException), dto).ConfigureAwait(false);
    }

    /// <summary>Member event submission - the shell's Events publishing mode
    /// may route it to the staff review queue.</summary>
    public async Task<SyncshellEventDto?> GroupSubmitCommunityEvent(SyncshellEventUpdateDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellEventDto?>(nameof(GroupSubmitCommunityEvent), dto).ConfigureAwait(false);
    }

    /// <summary>Staff approve/reject decision for a queued event.</summary>
    public async Task<bool> GroupDecideCommunityEvent(CommunityReviewDecisionDto dto)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<bool>(nameof(GroupDecideCommunityEvent), dto).ConfigureAwait(false);
    }

    /// <summary>Toggles a gallery like; returns the resulting state + total.</summary>
    public async Task<SyncshellGalleryImageLikeDto?> GroupToggleCommunityGalleryImageLike(
        SyncshellGalleryImageLikeDto like)
    {
        CheckConnection();
        return await _spheneHub!.InvokeAsync<SyncshellGalleryImageLikeDto?>(
            nameof(GroupToggleCommunityGalleryImageLike), like).ConfigureAwait(false);
    }

    private void CheckConnection()
    {
        if (ServerState is not (ServerState.Connected or ServerState.Connecting or ServerState.Reconnecting)) throw new InvalidDataException("Not connected");
    }
}
