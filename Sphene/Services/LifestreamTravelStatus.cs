namespace Sphene.Services;

/// <summary>Snapshot of <see cref="LifestreamTravelState"/> plus an optional
/// human-readable detail for the UI.</summary>
public sealed record LifestreamTravelStatus(LifestreamTravelState State, string? Detail = null);
