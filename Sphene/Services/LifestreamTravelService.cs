using Microsoft.Extensions.Logging;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.Interop.Ipc;
using Sphene.Services.Mediator;

namespace Sphene.Services;

/// <summary>
/// Evaluates destinations against the optional Lifestream IPC and executes
/// deliberate, user-initiated travel requests. All IPC calls run on the
/// framework thread; arrival is observed by polling
/// <c>IsBusy</c>/<c>IsHere</c> - Lifestream never reports failure itself, so
/// a clean <c>GoToHousingAddress</c> return only means "invoked".
/// </summary>
public sealed class LifestreamTravelService : DisposableMediatorSubscriberBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    // Lifestream's task manager takes a moment to latch IsBusy after GoTo -
    // don't call "did not start" before this grace expires.
    private static readonly TimeSpan BusyLatchGrace = TimeSpan.FromSeconds(8);
    // Generous bound for cross-DC journeys; intermediate idle moments are
    // filtered by the seen-busy state machine, not by a wall clock.
    private static readonly TimeSpan MaxWatchDuration = TimeSpan.FromMinutes(10);

    private readonly IpcCallerLifestream _lifestream;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly Lock _watchLock = new();
    private CancellationTokenSource? _watchCts;
    private bool _seenBusy;
    private volatile LifestreamTravelStatus _currentStatus = new(LifestreamTravelState.Unavailable);

    public LifestreamTravelService(ILogger<LifestreamTravelService> logger,
        SpheneMediator mediator, IpcCallerLifestream lifestream,
        DalamudUtilService dalamudUtil) : base(logger, mediator)
    {
        _lifestream = lifestream;
        _dalamudUtil = dalamudUtil;
        _lifestream.HouseEnterError += OnHouseEnterError;
    }

    /// <summary>Latest status from a state evaluation or the active watch.</summary>
    public LifestreamTravelStatus CurrentStatus => _currentStatus;

    /// <summary>Whether a travel request is currently being observed.</summary>
    public bool HasActiveRequest => _currentStatus.State == LifestreamTravelState.TravelRequested;

    /// <summary>
    /// Evaluates what the UI may offer for a destination right now - never
    /// starts travel. All IPC probes happen on the framework thread.
    /// </summary>
    public async Task<LifestreamTravelStatus> GetStateAsync(
        EventDestinationDto? destination, CancellationToken token = default)
    {
        var tuple = LifestreamAddressMapping.ToTuple(destination);
        if (tuple == null)
        {
            return new LifestreamTravelStatus(LifestreamTravelState.DestinationUnsupported,
                destination != null && (EventDestinationKind)destination.Kind
                    == EventDestinationKind.PublicLocation
                    ? "Automated travel to public locations is not supported"
                    : "No supported housing destination");
        }
        if (!_lifestream.APIAvailable)
        {
            return new LifestreamTravelStatus(LifestreamTravelState.Unavailable,
                "Lifestream plugin is not available");
        }
        try
        {
            var status = await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                if (_lifestream.IsBusy())
                {
                    return new LifestreamTravelStatus(LifestreamTravelState.Busy,
                        "Lifestream is currently busy");
                }
                if (_lifestream.IsHere(tuple.Value))
                {
                    return new LifestreamTravelStatus(LifestreamTravelState.AlreadyAtDestination);
                }
                var unreachable = EvaluateWorldReach(tuple.Value.World);
                if (unreachable != null) return unreachable;
                return new LifestreamTravelStatus(LifestreamTravelState.Ready,
                    _lifestream.IsQuickTravelAvailable(tuple.Value)
                        ? "Quick travel available" : null);
            }).ConfigureAwait(false);
            return status;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Lifestream state evaluation failed");
            return new LifestreamTravelStatus(LifestreamTravelState.Unavailable,
                "Lifestream did not respond");
        }
    }

    /// <summary>
    /// Executes one explicit travel request. Re-checks the state first and
    /// refuses while another request is in flight - at most one watched
    /// journey at a time.
    /// </summary>
    public async Task<LifestreamTravelStatus> RequestTravelAsync(
        EventDestinationDto destination, CancellationToken token = default)
    {
        if (HasActiveRequest) return _currentStatus;
        var state = await GetStateAsync(destination, token).ConfigureAwait(false);
        if (state.State != LifestreamTravelState.Ready) return PublishState(state);
        var tuple = LifestreamAddressMapping.ToTuple(destination);
        if (tuple == null)
        {
            return PublishState(new LifestreamTravelStatus(
                LifestreamTravelState.DestinationUnsupported, "No supported housing destination"));
        }
        try
        {
            await _dalamudUtil.RunOnFrameworkThread(
                () => _lifestream.GoToHousingAddress(tuple.Value)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Lifestream travel request failed");
            return PublishState(new LifestreamTravelStatus(
                LifestreamTravelState.TravelFailed, "Travel request failed"));
        }
        StartWatch(tuple.Value);
        return PublishState(new LifestreamTravelStatus(LifestreamTravelState.TravelRequested));
    }

    private LifestreamTravelStatus? EvaluateWorldReach(int world)
    {
        if (!_dalamudUtil.WorldData.Value.TryGetValue((ushort)world, out var name))
        {
            return new LifestreamTravelStatus(LifestreamTravelState.DestinationUnsupported,
                "Unknown destination world");
        }
        var reachable = _lifestream.CanVisitSameDataCenter(name)
            || _lifestream.CanVisitCrossDataCenter(name);
        return reachable
            ? null
            : new LifestreamTravelStatus(LifestreamTravelState.DestinationUnsupported,
                $"World {name} cannot be visited");
    }

    private void StartWatch(LifestreamAddressTuple tuple)
    {
        lock (_watchLock)
        {
            _watchCts?.Cancel();
            _watchCts?.Dispose();
            _watchCts = new CancellationTokenSource();
            _seenBusy = false;
            _ = WatchTravelAsync(tuple, _watchCts.Token);
        }
    }

    private async Task WatchTravelAsync(LifestreamAddressTuple tuple, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                if (DateTime.UtcNow - started > MaxWatchDuration)
                {
                    FinishWatch(new LifestreamTravelStatus(LifestreamTravelState.TravelFailed,
                        "Travel timed out"));
                    return;
                }
                var (busy, here) = await ProbeAsync(tuple).ConfigureAwait(false);
                if (busy)
                {
                    _seenBusy = true;
                    continue;
                }
                if (here)
                {
                    FinishWatch(new LifestreamTravelStatus(LifestreamTravelState.AlreadyAtDestination));
                    return;
                }
                if (_seenBusy)
                {
                    FinishWatch(new LifestreamTravelStatus(LifestreamTravelState.TravelFailed,
                        "Travel ended before reaching the destination"));
                    return;
                }
                if (DateTime.UtcNow - started > BusyLatchGrace)
                {
                    FinishWatch(new LifestreamTravelStatus(LifestreamTravelState.TravelFailed,
                        "Lifestream did not start the journey"));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogTrace("Lifestream travel watch cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Lifestream travel watch failed");
            FinishWatch(new LifestreamTravelStatus(LifestreamTravelState.TravelFailed,
                "Travel state could not be determined"));
        }
    }

    /// <summary>Probes busy/here state. A dead IPC throws to the watch
    /// handler; a missing APIAvailable flag fails immediately.</summary>
    private async Task<(bool Busy, bool Here)> ProbeAsync(
        LifestreamAddressTuple tuple)
    {
        if (!_lifestream.APIAvailable)
        {
            throw new InvalidOperationException("Lifestream became unavailable");
        }
        return await _dalamudUtil.RunOnFrameworkThread(
            () => (_lifestream.IsBusy(), _lifestream.IsHere(tuple))).ConfigureAwait(false);
    }

    private void OnHouseEnterError()
    {
        if (_currentStatus.State != LifestreamTravelState.TravelRequested) return;
        FinishWatch(new LifestreamTravelStatus(LifestreamTravelState.TravelFailed,
            "Lifestream reported a housing entry error"));
    }

    private void FinishWatch(LifestreamTravelStatus status)
    {
        PublishState(status);
        lock (_watchLock)
        {
            _watchCts?.Cancel();
            _watchCts?.Dispose();
            _watchCts = null;
        }
    }

    private LifestreamTravelStatus PublishState(LifestreamTravelStatus status)
    {
        _currentStatus = status;
        Mediator.Publish(new LifestreamTravelStateChangedMessage(status));
        return status;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifestream.HouseEnterError -= OnHouseEnterError;
            lock (_watchLock)
            {
                _watchCts?.Cancel();
                _watchCts?.Dispose();
                _watchCts = null;
            }
        }
        base.Dispose(disposing);
    }
}
