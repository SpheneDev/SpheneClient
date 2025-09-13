using Sphene.API.Data;
using Sphene.API.Dto;
using Sphene.API.Dto.User;
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

    // UserUpdateAckOther method removed - AckOther is controlled by other player's AckYou

    public async Task UserSendCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        Logger.LogInformation("UserSendCharacterDataAcknowledgment called - AckId: {acknowledgmentId}, User: {user}, Success: {success}, Connected: {connected}", 
            acknowledgmentDto.AcknowledgmentId, acknowledgmentDto.User.AliasOrUID, acknowledgmentDto.Success, IsConnected);
        
        if (!IsConnected) 
        {
            Logger.LogWarning("Cannot send acknowledgment - not connected to server. AckId: {acknowledgmentId}", acknowledgmentDto.AcknowledgmentId);
            return;
        }
        
        try
        {
            await _spheneHub!.SendAsync(nameof(UserSendCharacterDataAcknowledgment), acknowledgmentDto).ConfigureAwait(false);
            Logger.LogInformation("Successfully sent acknowledgment to server - AckId: {acknowledgmentId}", acknowledgmentDto.AcknowledgmentId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send acknowledgment to server - AckId: {acknowledgmentId}", acknowledgmentDto.AcknowledgmentId);
        }
    }

    private async Task PushCharacterDataInternal(CharacterData character, List<UserData> visibleCharacters, string? acknowledgmentId = null)
    {
        Logger.LogInformation("Pushing character data for {hash} to {charas} with acknowledgment ID {ackId}", character.DataHash.Value, string.Join(", ", visibleCharacters.Select(c => c.AliasOrUID)), acknowledgmentId);
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

        await UserPushData(new(visibleCharacters, character, censusDto) { AcknowledgmentId = acknowledgmentId }).ConfigureAwait(false);
    }
}
#pragma warning restore MA0040
