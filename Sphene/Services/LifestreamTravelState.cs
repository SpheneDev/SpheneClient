namespace Sphene.Services;

/// <summary>Travel state reported to the UI. <see cref="TravelRequested"/>
/// means the IPC returned without throwing - never confirmed arrival; the
/// background watch either resolves to <see cref="AlreadyAtDestination"/> or
/// <see cref="TravelFailed"/>.</summary>
public enum LifestreamTravelState
{
    Unavailable,
    DestinationUnsupported,
    AlreadyAtDestination,
    Busy,
    Ready,
    TravelRequested,
    TravelFailed,
}
