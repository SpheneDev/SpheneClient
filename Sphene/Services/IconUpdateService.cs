using Sphene.API.Data;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.UI.Components;
using Microsoft.Extensions.Logging;

namespace Sphene.Services;

// Service for publishing selective icon updates to optimize UI performance
public class IconUpdateService : IMediatorSubscriber
{
    private readonly SpheneMediator _mediator;
    private readonly ILogger<IconUpdateService> _logger;
    
    public SpheneMediator Mediator => _mediator;
    
    public IconUpdateService(SpheneMediator mediator, ILogger<IconUpdateService> logger)
    {
        _mediator = mediator;
        _logger = logger;
        
        // Subscribe to icon update events to trigger UI refresh
        _mediator.Subscribe<UserPairIconUpdateMessage>(this, OnIconUpdate);
    }
    
    private void OnIconUpdate(UserPairIconUpdateMessage message)
    {
        _logger.LogDebug("Icon update received for user {UserUID}, type: {UpdateType}", 
            message.User.UID, message.UpdateType);
        
        // Publish RefreshUiMessage to trigger icon-only refresh
        _mediator.Publish(new RefreshUiMessage());
    }
    
    // Update connection status icons for a specific user
    public void UpdateConnectionStatus(UserData user, bool isVisible, bool isOnline, bool isPaused)
    {
        var data = new ConnectionStatusData(isVisible, isOnline, isPaused);
        _mediator.Publish(new UserPairIconUpdateMessage(user, IconUpdateType.ConnectionStatus, data));
        _logger.LogDebug("Published connection status icon update for user {user}", user.AliasOrUID);
    }
    
    // Update acknowledgment status icons for a specific user
    public void UpdateAcknowledgmentStatus(UserData user, bool hasPending, bool? lastSuccess, DateTimeOffset? lastTime)
    {
        var data = new AcknowledgmentStatusData(hasPending, lastSuccess, lastTime);
        _mediator.Publish(new UserPairIconUpdateMessage(user, IconUpdateType.AcknowledgmentStatus, data));
        _logger.LogDebug("Published acknowledgment status icon update for user {user}", user.AliasOrUID);
    }
    
    // Update permission status icons for a specific user
    public void UpdatePermissionStatus(UserData user, bool isPaused, bool canReload)
    {
        var data = new PermissionStatusData(isPaused, canReload);
        _mediator.Publish(new UserPairIconUpdateMessage(user, IconUpdateType.PermissionStatus, data));
        _logger.LogDebug("Published permission status icon update for user {user}", user.AliasOrUID);
    }
    
    // Update individual permission icons for a specific user
    public void UpdateIndividualPermission(UserData user, string permissionType, bool isEnabled)
    {
        var data = new IndividualPermissionData(permissionType, isEnabled);
        _mediator.Publish(new UserPairIconUpdateMessage(user, IconUpdateType.IndividualPermissions, data));
        _logger.LogDebug("Published individual permission icon update for user {user}: {permission} = {enabled}", 
            user.AliasOrUID, permissionType, isEnabled);
    }
    
    // Update group role icons for a specific user
    public void UpdateGroupRole(UserData user, bool isOwner, bool isModerator, bool isPinned)
    {
        var data = new GroupRoleData(isOwner, isModerator, isPinned);
        _mediator.Publish(new UserPairIconUpdateMessage(user, IconUpdateType.GroupRole, data));
        _logger.LogDebug("Published group role icon update for user {user}", user.AliasOrUID);
    }
    
    // Update reload timer icons for a specific user
    public void UpdateReloadTimer(UserData user, bool isActive, float progress)
    {
        var data = new ReloadTimerData(isActive, progress);
        _mediator.Publish(new UserPairIconUpdateMessage(user, IconUpdateType.ReloadTimer, data));
        _logger.LogDebug("Published reload timer icon update for user {user}: active={active}, progress={progress}", 
            user.AliasOrUID, isActive, progress);
    }
    
    // Convenience method to update acknowledgment status from existing events
    public void UpdateFromAcknowledgmentEvent(PairAcknowledgmentStatusChangedMessage message)
    {
        UpdateAcknowledgmentStatus(message.User, message.HasPendingAcknowledgment, 
            message.LastAcknowledgmentSuccess, message.LastAcknowledgmentTime);
    }
    
    public void Dispose()
    {
        _mediator.UnsubscribeAll(this);
    }
}