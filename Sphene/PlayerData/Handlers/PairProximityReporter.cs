namespace Sphene.PlayerData.Handlers;

/// <summary>
/// Decides when <see cref="PairHandler"/> should report proximity visibility to the server.
/// Encapsulates the post-zone reaffirm window and a cooldown-based reaffirm that runs
/// indefinitely afterwards, preventing the deadlock where a player remains invisible
/// after a teleport because the original 20-second window expired without mutual visibility.
/// </summary>
/// <remarks>
/// Not thread-safe; intended to be called only from the Framework update thread,
/// mirroring the threading guarantees of the previous inline logic in <see cref="PairHandler"/>.
/// </remarks>
public sealed class PairProximityReporter
{
    private const int PostZoneCheckSeconds = 20;
    private const int ReaffirmCooldownSeconds = 5;

    private DateTime _postZoneCheckUntil = DateTime.MinValue;
    private DateTime _postZoneLastCheck = DateTime.MinValue;
    private DateTime _lastReaffirmTime = DateTime.MinValue;
    private bool _postZoneReaffirmDone = false;
    private bool _proximityReportedVisible = false;

    /// <summary>
    /// Called when a zone switch starts. Resets all proximity tracking so the
    /// next <see cref="OnFrameworkUpdate"/> after the zone ends will re-report.
    /// </summary>
    public void OnZoneSwitchStart()
    {
        _proximityReportedVisible = false;
        _postZoneReaffirmDone = false;
        _postZoneCheckUntil = DateTime.MinValue;
    }

    /// <summary>
    /// Called when a zone switch ends. Opens the post-zone reaffirm window.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    public void OnZoneSwitchEnd(DateTime now)
    {
        _proximityReportedVisible = false;
        _postZoneReaffirmDone = false;
        _postZoneCheckUntil = now.AddSeconds(PostZoneCheckSeconds);
        _postZoneLastCheck = DateTime.MinValue;
    }

    /// <summary>
    /// Evaluates the current proximity state and returns the action the caller should take.
    /// </summary>
    /// <param name="charaHandlerValid">Whether the local character handler has a valid (non-zero) address.</param>
    /// <param name="withinPartyRange">Whether the paired character is within the dynamic around-range for the current location.</param>
    /// <param name="isMutuallyVisible">Whether the server has confirmed mutual visibility for this pair.</param>
    /// <param name="localVisibilityGateActive">Whether the local visibility gate (cutscene/zone switch) is currently active.</param>
    /// <param name="isCurrentlyVisible">Whether <see cref="PairHandler.IsVisible"/> is currently true.</param>
    /// <param name="now">Current UTC time.</param>
    /// <returns>The action to take; never null.</returns>
    public VisibilityReportAction OnFrameworkUpdate(
        bool charaHandlerValid,
        bool withinPartyRange,
        bool isMutuallyVisible,
        bool localVisibilityGateActive,
        bool isCurrentlyVisible,
        DateTime now)
    {
        // If the character handler is invalid, we cannot be visible.
        if (!charaHandlerValid)
        {
            if (isCurrentlyVisible || _proximityReportedVisible)
            {
                _proximityReportedVisible = false;
                return VisibilityReportAction.ReportNotVisible;
            }
            return VisibilityReportAction.None;
        }

        // If out of range (and not in GPose, which is handled by the caller), revoke visibility.
        if (!withinPartyRange && _proximityReportedVisible)
        {
            _proximityReportedVisible = false;
            return VisibilityReportAction.ReportNotVisible;
        }

        bool shouldReportVisible = withinPartyRange && !localVisibilityGateActive;
        if (!shouldReportVisible)
        {
            return VisibilityReportAction.None;
        }

        bool isInPostZone = now < _postZoneCheckUntil;

        if (isInPostZone)
        {
            // During the post-zone window, re-check at most once per second.
            if ((now - _postZoneLastCheck) > TimeSpan.FromSeconds(1))
            {
                _postZoneLastCheck = now;

                if (!_proximityReportedVisible)
                {
                    _proximityReportedVisible = true;
                    return VisibilityReportAction.ReportVisible;
                }

                // Reaffirm once during the post-zone window if mutual visibility has not yet been established.
                if (!isMutuallyVisible && !_postZoneReaffirmDone)
                {
                    _postZoneReaffirmDone = true;
                    return VisibilityReportAction.ReportVisible;
                }
            }
        }
        else
        {
            // Outside the post-zone window: use a cooldown-based reaffirm so the client
            // never gets stuck assuming the server already knows we are visible.
            if (!_proximityReportedVisible)
            {
                _proximityReportedVisible = true;
                _lastReaffirmTime = now;
                return VisibilityReportAction.ReportVisible;
            }

            if (!isMutuallyVisible && (now - _lastReaffirmTime) > TimeSpan.FromSeconds(ReaffirmCooldownSeconds))
            {
                _lastReaffirmTime = now;
                return VisibilityReportAction.ReportVisible;
            }
        }

        return VisibilityReportAction.None;
    }

    /// <summary>
    /// Resets all internal state. Useful when a pair is being disposed or recreated.
    /// </summary>
    public void Reset()
    {
        _proximityReportedVisible = false;
        _postZoneReaffirmDone = false;
        _postZoneCheckUntil = DateTime.MinValue;
        _postZoneLastCheck = DateTime.MinValue;
        _lastReaffirmTime = DateTime.MinValue;
    }
}
