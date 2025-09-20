using Sphene.API.Data;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.User;
using Sphene.Services.CharaData.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Sphene.WebAPI;
public partial class ApiController
{
    public async Task<CharaDataFullDto?> CharaDataCreate()
    {
        if (!IsConnected) return null;

        try
        {
            Logger.LogDebug("Creating new Character Data");
            return await _spheneHub!.InvokeAsync<CharaDataFullDto?>(nameof(CharaDataCreate)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to create new character data");
            return null;
        }
    }

    public async Task<CharaDataFullDto?> CharaDataUpdate(CharaDataUpdateDto updateDto)
    {
        if (!IsConnected) return null;

        try
        {
            Logger.LogDebug("Updating chara data for {id}", updateDto.Id);
            return await _spheneHub!.InvokeAsync<CharaDataFullDto?>(nameof(CharaDataUpdate), updateDto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update chara data for {id}", updateDto.Id);
            return null;
        }
    }

    public async Task<bool> CharaDataDelete(string id)
    {
        if (!IsConnected) return false;

        try
        {
            Logger.LogDebug("Deleting chara data for {id}", id);
            return await _spheneHub!.InvokeAsync<bool>(nameof(CharaDataDelete), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete chara data for {id}", id);
            return false;
        }
    }

    public async Task<CharaDataMetaInfoDto?> CharaDataGetMetainfo(string id)
    {
        if (!IsConnected) return null;

        try
        {
            Logger.LogDebug("Getting metainfo for chara data {id}", id);
            return await _spheneHub!.InvokeAsync<CharaDataMetaInfoDto?>(nameof(CharaDataGetMetainfo), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get meta info for chara data {id}", id);
            return null;
        }
    }
    
    public async Task<CharacterDataHashValidationResponse?> ValidateCharaDataHash(string userUid, string dataHash)
    {
        if (!IsConnected) 
        {
            Logger.LogWarning("Cannot validate hash - not connected to server");
            return new CharacterDataHashValidationResponse
            {
                IsValid = true,
                CurrentHash = dataHash
            };
        }

        try
        {
            var request = new CharacterDataHashValidationRequest
            {
                UserUID = userUid,
                DataHash = dataHash
            };
            
            Logger.LogDebug("Validating chara data hash for user {userUid}", userUid);
            Logger.LogDebug("About to call ValidateCharaDataHash hub method with request: UserUID={UserUID}, DataHash={DataHash}", request.UserUID, request.DataHash);
            var response = await _spheneHub!.InvokeAsync<CharacterDataHashValidationResponse>("ValidateCharaDataHash", request).ConfigureAwait(false);
            Logger.LogDebug("ValidateCharaDataHash hub method call completed");
            
            if (response == null)
            {
                Logger.LogWarning("Received null response from server for hash validation");
                return new CharacterDataHashValidationResponse
                {
                    IsValid = true,
                    CurrentHash = dataHash
                };
            }
            
            Logger.LogDebug("Hash validation response: IsValid={IsValid}, CurrentHash={CurrentHash}", 
                response.IsValid, response.CurrentHash);
            return response;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to validate chara data hash for user {userUid} - Exception Type: {exceptionType}, Message: {message} - returning fallback response", 
                userUid, ex.GetType().Name, ex.Message);
            return new CharacterDataHashValidationResponse
            {
                IsValid = true,
                CurrentHash = dataHash
            };
        }
    }

    public async Task<CharaDataFullDto?> CharaDataAttemptRestore(string id)
    {
        if (!IsConnected) return null;

        try
        {
            Logger.LogDebug("Attempting to restore chara data {id}", id);
            return await _spheneHub!.InvokeAsync<CharaDataFullDto?>(nameof(CharaDataAttemptRestore), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to restore chara data for {id}", id);
            return null;
        }
    }

    public async Task<List<CharaDataFullDto>> CharaDataGetOwn()
    {
        if (!IsConnected) return [];

        try
        {
            Logger.LogDebug("Getting all own chara data");
            return await _spheneHub!.InvokeAsync<List<CharaDataFullDto>>(nameof(CharaDataGetOwn)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get own chara data");
            return [];
        }
    }

    public async Task<List<CharaDataMetaInfoDto>> CharaDataGetShared()
    {
        if (!IsConnected) return [];

        try
        {
            Logger.LogDebug("Getting all own chara data");
            return await _spheneHub!.InvokeAsync<List<CharaDataMetaInfoDto>>(nameof(CharaDataGetShared)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get shared chara data");
            return [];
        }
    }

    public async Task<CharaDataDownloadDto?> CharaDataDownload(string id)
    {
        if (!IsConnected) return null;

        try
        {
            Logger.LogDebug("Getting download chara data for {id}", id);
            return await _spheneHub!.InvokeAsync<CharaDataDownloadDto>(nameof(CharaDataDownload), id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get download chara data for {id}", id);
            return null;
        }
    }

    public async Task<string> GposeLobbyCreate()
    {
        if (!IsConnected) return string.Empty;

        try
        {
            Logger.LogDebug("Creating GPose Lobby");
            return await _spheneHub!.InvokeAsync<string>(nameof(GposeLobbyCreate)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to create GPose lobby");
            return string.Empty;
        }
    }

    public async Task<bool> GposeLobbyLeave()
    {
        if (!IsConnected) return true;

        try
        {
            Logger.LogDebug("Leaving current GPose Lobby");
            return await _spheneHub!.InvokeAsync<bool>(nameof(GposeLobbyLeave)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to leave GPose lobby");
            return false;
        }
    }

    public async Task<List<UserData>> GposeLobbyJoin(string lobbyId)
    {
        if (!IsConnected) return [];

        try
        {
            Logger.LogDebug("Joining GPose Lobby {id}", lobbyId);
            return await _spheneHub!.InvokeAsync<List<UserData>>(nameof(GposeLobbyJoin), lobbyId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to join GPose lobby {id}", lobbyId);
            return [];
        }
    }

    public async Task GposeLobbyPushCharacterData(CharaDataDownloadDto charaDownloadDto)
    {
        if (!IsConnected) return;

        try
        {
            // No hash validation here - we'll rely on the VisibleUserDataDistributor to handle this
            
            Logger.LogDebug("Sending Chara Data to GPose Lobby");
            await _spheneHub!.InvokeAsync(nameof(GposeLobbyPushCharacterData), charaDownloadDto).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send Chara Data to GPose lobby");
        }
    }

    public async Task GposeLobbyPushPoseData(PoseData poseData)
    {
        if (!IsConnected) return;

        try
        {
            Logger.LogDebug("Sending Pose Data to GPose Lobby");
            await _spheneHub!.InvokeAsync(nameof(GposeLobbyPushPoseData), poseData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send Pose Data to GPose lobby");
        }
    }

    public async Task GposeLobbyPushWorldData(WorldData worldData)
    {
        if (!IsConnected) return;

        try
        {
            await _spheneHub!.InvokeAsync(nameof(GposeLobbyPushWorldData), worldData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send World Data to GPose lobby");
        }
    }
}
