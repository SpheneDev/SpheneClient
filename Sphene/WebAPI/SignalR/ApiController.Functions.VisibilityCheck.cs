using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR.Client;
using Sphene.API.Data;
using Sphene.Services;
using Sphene.Services.Mediator.Messages;
using System.Threading.Tasks;

namespace Sphene.WebAPI;

public partial class ApiController
{
    // Send visibility check request to another player
    public async Task<bool> SendVisibilityCheckRequestAsync(string targetPlayerIdentifier, string requestId)
    {
        if (_spheneHub?.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            Logger.LogWarning("Cannot send visibility check request - not connected to server");
            return false;
        }

        try
        {
            Logger.LogDebug("Sending visibility check request to {player} with requestId {requestId}", 
                targetPlayerIdentifier, requestId);
            
            await _spheneHub.SendAsync("SendVisibilityCheckRequest", targetPlayerIdentifier, requestId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send visibility check request to {player}", targetPlayerIdentifier);
            return false;
        }
    }

    // Send visibility check response back to requesting player
    public async Task<bool> SendVisibilityCheckResponseAsync(string targetPlayerIdentifier, string requestId, bool canSeePlayer)
    {
        if (_spheneHub?.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            Logger.LogWarning("Cannot send visibility check response - not connected to server");
            return false;
        }

        try
        {
            Logger.LogDebug("Sending visibility check response to {player} with requestId {requestId}: {canSee}", 
                targetPlayerIdentifier, requestId, canSeePlayer);
            
            await _spheneHub.SendAsync("SendVisibilityCheckResponse", targetPlayerIdentifier, requestId, canSeePlayer).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send visibility check response to {player}", targetPlayerIdentifier);
            return false;
        }
    }

    // Handle incoming visibility check request from server
    public async Task Client_ReceiveVisibilityCheckRequest(string fromPlayerIdentifier, string requestId)
    {
        Logger.LogDebug("Received visibility check request from {player} with requestId {requestId}", 
            fromPlayerIdentifier, requestId);
        
        try
        {
            // Handle the request using mediator message
            Mediator.Publish(new VisibilityCheckRequestMessage(fromPlayerIdentifier, requestId));
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling visibility check request from {player}", fromPlayerIdentifier);
        }
    }

    // Handle incoming visibility check response from server
    public async Task Client_ReceiveVisibilityCheckResponse(string requestId, bool canSeeUs)
    {
        Logger.LogDebug("Received visibility check response for requestId {requestId}: {canSee}", 
            requestId, canSeeUs);
        
        try
        {
            // Handle the response using mediator message
            Mediator.Publish(new VisibilityCheckResponseMessage(requestId, canSeeUs));
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling visibility check response for requestId {requestId}", requestId);
        }
    }
}