using Sphene.API.Data;
using Sphene.Services.Mediator;

namespace Sphene.Services.Events;

// Event for updating specific icons without full UI rebuild
public record UserPairIconUpdateMessage(
    UserData User,
    IconUpdateType UpdateType,
    object? UpdateData = null
) : MessageBase;

// Types of icon updates that can be performed independently
public enum IconUpdateType
{
    // Connection status icons (eye, user, users)
    ConnectionStatus,
    
    // Acknowledgment status icons (clock, check, exclamation)
    AcknowledgmentStatus,
    
    // Permission icons (pause/play, reload)
    PermissionStatus,
    
    // Individual permission icons (sticky, sounds, animations, VFX)
    IndividualPermissions,
    
    // Group role icons (crown, shield, thumbtack)
    GroupRole,
    
    // Reload timer status
    ReloadTimer
}

// Specific data for connection status updates
public record ConnectionStatusData(
    bool IsVisible,
    bool IsOnline,
    bool IsPaused
);

// Specific data for acknowledgment status updates
public record AcknowledgmentStatusData(
    bool HasPending,
    bool? LastSuccess,
    DateTimeOffset? LastTime
);

// Specific data for permission updates
public record PermissionStatusData(
    bool IsPaused,
    bool CanReload
);

// Specific data for individual permission updates
public record IndividualPermissionData(
    string PermissionType,
    bool IsEnabled
);

// Specific data for group role updates
public record GroupRoleData(
    bool IsOwner,
    bool IsModerator,
    bool IsPinned
);

// Specific data for reload timer updates
public record ReloadTimerData(
    bool IsActive,
    float Progress
);