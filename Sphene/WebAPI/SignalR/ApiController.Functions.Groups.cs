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
        var currentLocation = await _dalamudUtil.GetMapDataAsync();
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

    private void CheckConnection()
    {
        Logger.LogDebug("CheckConnection called - Current ServerState: {0}", ServerState);
        if (ServerState is not (ServerState.Connected or ServerState.Connecting or ServerState.Reconnecting)) 
        {
            Logger.LogWarning("Connection check failed - ServerState is {0}, expected Connected/Connecting/Reconnecting", ServerState);
            throw new InvalidDataException($"Not connected - ServerState: {ServerState}");
        }
        Logger.LogDebug("Connection check passed - ServerState: {0}", ServerState);
    }
}
