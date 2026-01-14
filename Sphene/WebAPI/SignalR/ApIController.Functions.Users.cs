using Sphene.API.Data;
using Sphene.API.Dto;
using Sphene.API.Dto.User;
using Sphene.API.Dto.Visibility;
using Sphene.API.Dto.CharaData;
using Sphene.Services.Mediator;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Sphene.WebAPI;

#pragma warning disable MA0040
public partial class ApiController
{
    public async Task PushCharacterData(CharacterData data, List<UserData> visibleCharacters, string? acknowledgmentId = null)
    {
        if (!IsConnected) return;

        try
        {
            await PushCharacterDataInternal(data, [.. visibleCharacters], acknowledgmentId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Upload operation was cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during upload of files");
        }
    }

    public async Task UserReportVisibility(UserVisibilityReportDto dto)
    {
        if (!IsConnected)
        {
            Logger.LogDebug("Suppressing visibility report for {target} - not connected", dto.Target.AliasOrUID);
            return;
        }
        try
        {
            await _spheneHub!.InvokeAsync(nameof(UserReportVisibility), dto).ConfigureAwait(false);
            Logger.LogDebug("Sent visibility report to server for reporter={reporter}, target={target}, proximity={visible}", dto.Reporter.UID, dto.Target.AliasOrUID, dto.IsVisible);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to report visibility to server for {target}", dto.Target.AliasOrUID);
        }
    }

    public async Task UserAddPair(UserDto user)
    {
        if (!IsConnected) return;
        await _spheneHub!.SendAsync(nameof(UserAddPair), user).ConfigureAwait(false);
    }

    public async Task UserDelete()
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(UserDelete)).ConfigureAwait(false);
        await CreateConnectionsAsync().ConfigureAwait(false);
    }

    public async Task<List<OnlineUserIdentDto>> UserGetOnlinePairs(CensusDataDto? censusDataDto)
    {
        return await _spheneHub!.InvokeAsync<List<OnlineUserIdentDto>>(nameof(UserGetOnlinePairs), censusDataDto).ConfigureAwait(false);
    }

    public async Task<List<UserFullPairDto>> UserGetPairedClients()
    {
        return await _spheneHub!.InvokeAsync<List<UserFullPairDto>>(nameof(UserGetPairedClients)).ConfigureAwait(false);
    }

    public async Task<UserProfileDto> UserGetProfile(UserDto dto)
    {
        if (!IsConnected) return new UserProfileDto(dto.User, Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null);
        return await _spheneHub!.InvokeAsync<UserProfileDto>(nameof(UserGetProfile), dto).ConfigureAwait(false);
    }

    public async Task UserPushData(UserCharaDataMessageDto dto)
    {
        try
        {
            await _spheneHub!.InvokeAsync(nameof(UserPushData), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to Push character data");
        }
    }

    public async Task SetBulkPermissions(BulkPermissionsDto dto)
    {
        CheckConnection();

        try
        {
            await _spheneHub!.InvokeAsync(nameof(SetBulkPermissions), dto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to set permissions");
        }
    }

    public async Task UserRemovePair(UserDto userDto)
    {
        if (!IsConnected) return;
        await _spheneHub!.SendAsync(nameof(UserRemovePair), userDto).ConfigureAwait(false);
    }

    public async Task UserSetPairPermissions(UserPermissionsDto userPermissions)
    {
        await SetBulkPermissions(new(new(StringComparer.Ordinal)
        {
                { userPermissions.User.UID, userPermissions.Permissions }
            }, new(StringComparer.Ordinal))).ConfigureAwait(false);
    }

    public async Task UserSetProfile(UserProfileDto userDescription)
    {
        if (!IsConnected) return;
        await _spheneHub!.InvokeAsync(nameof(UserSetProfile), userDescription).ConfigureAwait(false);
    }

    public async Task UserUpdateDefaultPermissions(DefaultPermissionsDto defaultPermissionsDto)
    {
        CheckConnection();
        await _spheneHub!.InvokeAsync(nameof(UserUpdateDefaultPermissions), defaultPermissionsDto).ConfigureAwait(false);
    }

    public async Task UserUpdateAckYou(bool ackYou)
    {
        CheckConnection();
        await _spheneHub!.InvokeAsync(nameof(UserUpdateAckYou), ackYou).ConfigureAwait(false);
    }

    public async Task UserUpdatePenumbraReceivePreference(bool allowMods)
    {
        CheckConnection();
        await _spheneHub!.InvokeAsync(nameof(UserUpdatePenumbraReceivePreference), allowMods).ConfigureAwait(false);
    }

    public async Task UserUpdateGposeState(bool isInGpose)
    {
        if (!IsConnected) return;

        try
        {
            await _spheneHub!.InvokeAsync(nameof(UserUpdateGposeState), isInGpose).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to update GPose state to {isInGpose}", isInGpose);
        }
    }

    // UserUpdateAckOther method removed - AckOther is controlled by other player's AckYou

    public async Task UserSendCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        Logger.LogDebug("UserSendCharacterDataAcknowledgment called - Hash: {hash}, User: {user}, Success: {success}, Connected: {connected}", 
            acknowledgmentDto.DataHash[..Math.Min(8, acknowledgmentDto.DataHash.Length)], acknowledgmentDto.User.AliasOrUID, acknowledgmentDto.Success, IsConnected);
        
        if (!IsConnected) 
        {
            Logger.LogWarning("Cannot send acknowledgment - not connected to server. Hash: {hash}", 
                acknowledgmentDto.DataHash[..Math.Min(8, acknowledgmentDto.DataHash.Length)]);
            return;
        }
        
        try
        {
            await _spheneHub!.SendAsync(nameof(UserSendCharacterDataAcknowledgment), acknowledgmentDto).ConfigureAwait(false);
            Logger.LogDebug("Successfully sent acknowledgment to server - Hash: {hash}", 
                acknowledgmentDto.DataHash[..Math.Min(8, acknowledgmentDto.DataHash.Length)]);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send acknowledgment to server - Hash: {hash}", 
                acknowledgmentDto.DataHash[..Math.Min(8, acknowledgmentDto.DataHash.Length)]);
        }
    }

    public async Task<List<UserHousingPropertyDto>> UserGetHousingProperties()
    {
        if (!IsConnected) return new List<UserHousingPropertyDto>();
        
        try
        {
            return await _spheneHub!.InvokeAsync<List<UserHousingPropertyDto>>(nameof(UserGetHousingProperties)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get housing properties from server");
            return new List<UserHousingPropertyDto>();
        }
    }

    public async Task<UserHousingPropertyDto?> UserSetHousingProperty(UserHousingPropertyUpdateDto dto)
    {
        Logger.LogDebug("[CLIENT DEBUG] UserSetHousingProperty called with: {location}, IsConnected: {connected}", dto.Location, IsConnected);
        
        if (!IsConnected) 
        {
            Logger.LogWarning("[CLIENT DEBUG] UserSetHousingProperty: Not connected to server");
            return null;
        }
        
        try
        {
            Logger.LogDebug("[CLIENT DEBUG] UserSetHousingProperty: Invoking hub method");
            var result = await _spheneHub!.InvokeAsync<UserHousingPropertyDto?>(nameof(UserSetHousingProperty), dto).ConfigureAwait(false);
            Logger.LogDebug("[CLIENT DEBUG] UserSetHousingProperty: Hub method returned: {result}", result != null ? "SUCCESS" : "NULL");
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to set housing property: {location}", dto.Location);
            return null;
        }
    }

    public async Task<bool> UserDeleteHousingProperty(LocationInfo location)
    {
        if (!IsConnected) return false;
        
        try
        {
            return await _spheneHub!.InvokeAsync<bool>(nameof(UserDeleteHousingProperty), location).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete housing property: {location}", location);
            return false;
        }
    }

    private async Task PushCharacterDataInternal(CharacterData character, List<UserData> visibleCharacters, string? acknowledgmentId = null)
    {
        Logger.LogDebug("Pushing character data for {hash} to {charas} with acknowledgment ID {ackId}", character.DataHash.Value, string.Join(", ", visibleCharacters.Select(c => c.AliasOrUID)), acknowledgmentId);
        StringBuilder sb = new();
        foreach (var kvp in character.FileReplacements.ToList())
        {
            sb.AppendLine($"FileReplacements for {kvp.Key}: {kvp.Value.Count}");
        }
        foreach (var item in character.GlamourerData)
        {
            sb.AppendLine($"GlamourerData for {item.Key}: {!string.IsNullOrEmpty(item.Value)}");
        }
        Logger.LogDebug("Chara data contained: {nl} {data}", Environment.NewLine, sb.ToString());

        CensusDataDto? censusDto = null;
        if (_serverManager.SendCensusData && _lastCensus != null)
        {
            var world = await _dalamudUtil.GetWorldIdAsync().ConfigureAwait(false);
            censusDto = new((ushort)world, _lastCensus.RaceId, _lastCensus.TribeId, _lastCensus.Gender);
            Logger.LogDebug("Attaching Census Data: {data}", censusDto);
        }

        await UserPushData(new(visibleCharacters, character, censusDto)).ConfigureAwait(false);
    }

    public async Task UserAckFileTransfer(FileTransferAckMessage msg)
    {
        if (!IsConnected) return;
        try
        {
            await _spheneHub!.InvokeAsync("UserAckFileTransfer", msg.Hash, msg.SenderUID).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send file transfer acknowledgment");
        }
    }
}
#pragma warning restore MA0040
