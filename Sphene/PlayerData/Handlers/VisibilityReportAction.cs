namespace Sphene.PlayerData.Handlers;

/// <summary>
/// The action <see cref="PairProximityReporter"/> requests the caller to perform.
/// </summary>
public enum VisibilityReportAction
{
    /// <summary>No action required this frame.</summary>
    None,

    /// <summary>Report the paired character as visible to the server.</summary>
    ReportVisible,

    /// <summary>Report the paired character as not visible to the server.</summary>
    ReportNotVisible,
}
