using Dalamud.Game.ClientState.Objects.Types;
using Sphene.PlayerData.Handlers;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Sphene.Interop.Ipc;

public class RedrawManager : IDisposable
{
    private readonly SpheneMediator _spheneMediator;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ConcurrentDictionary<nint, RedrawRequestState> _penumbraRedrawRequests = [];
    private readonly TimeSpan _redrawEventTimeout = TimeSpan.FromSeconds(5);
    private CancellationTokenSource _disposalCts = new();

    public SemaphoreSlim RedrawSemaphore { get; init; } = new(2, 2);

    public RedrawManager(SpheneMediator spheneMediator, DalamudUtilService dalamudUtil)
    {
        _spheneMediator = spheneMediator;
        _dalamudUtil = dalamudUtil;
    }

    public async Task PenumbraRedrawInternalAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, Action<ICharacter> action, CancellationToken token, bool waitForRedrawEvent = true)
    {
        _spheneMediator.Publish(new PenumbraStartRedrawMessage(handler.Address));

        var requestState = new RedrawRequestState();
        requestState.PendingRequest = waitForRedrawEvent ? 1 : 0;
        _penumbraRedrawRequests[handler.Address] = requestState;
        var start = Stopwatch.GetTimestamp();
        var actionMs = 0d;
        var waitDrawMs = 0d;
        var redrawWaitMs = 0d;
        var redrawEventReceived = !waitForRedrawEvent;

        try
        {
            using CancellationTokenSource cancelToken = new CancellationTokenSource();
            using CancellationTokenSource combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken.Token, token, _disposalCts.Token);
            var combinedToken = combinedCts.Token;
            cancelToken.CancelAfter(TimeSpan.FromSeconds(15));
            var actionStart = Stopwatch.GetTimestamp();
            await handler.ActOnFrameworkAfterEnsureNoDrawAsync(action, combinedToken).ConfigureAwait(false);
            actionMs = (Stopwatch.GetTimestamp() - actionStart) * 1000.0 / Stopwatch.Frequency;

            if (!_disposalCts.Token.IsCancellationRequested)
            {
                var waitStart = Stopwatch.GetTimestamp();
                await _dalamudUtil.WaitWhileCharacterIsDrawing(logger, handler, applicationId, 30000, combinedToken).ConfigureAwait(false);
                waitDrawMs = (Stopwatch.GetTimestamp() - waitStart) * 1000.0 / Stopwatch.Frequency;
            }

            if (waitForRedrawEvent)
            {
                var redrawStart = Stopwatch.GetTimestamp();
                redrawEventReceived = await WaitForRequestedRedrawEventAsync(requestState, combinedToken).ConfigureAwait(false);
                redrawWaitMs = (Stopwatch.GetTimestamp() - redrawStart) * 1000.0 / Stopwatch.Frequency;
            }
        }
        finally
        {
            var totalMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (totalMs >= 500 || !redrawEventReceived)
            {
                logger.LogDebug("[{applicationId}] PenumbraRedrawInternal timing: kind={kind} addr={addr} totalMs={totalMs:F0} actionMs={actionMs:F0} waitDrawMs={waitDrawMs:F0} redrawWaitMs={redrawWaitMs:F0} redrawEvent={redrawEvent}",
                    applicationId, handler.ObjectKind, handler.Address, totalMs, actionMs, waitDrawMs, redrawWaitMs, redrawEventReceived);
            }
            _penumbraRedrawRequests.TryRemove(handler.Address, out _);
            _spheneMediator.Publish(new PenumbraEndRedrawMessage(handler.Address));
        }
    }

    internal bool TryConsumeRequestedRedraw(nint objectAddress)
    {
        if (!_penumbraRedrawRequests.TryGetValue(objectAddress, out var state))
        {
            return false;
        }

        return Interlocked.Exchange(ref state.PendingRequest, 0) == 1;
    }

    internal void NotifyGameObjectRedrawn(nint objectAddress, int objectTableIndex)
    {
        if (_penumbraRedrawRequests.TryGetValue(objectAddress, out var state))
        {
            state.RedrawnTcs.TrySetResult(true);
        }
    }

    private async Task<bool> WaitForRequestedRedrawEventAsync(RedrawRequestState requestState, CancellationToken token)
    {
        if (Volatile.Read(ref requestState.PendingRequest) == 0)
        {
            return true;
        }

        var completed = await Task.WhenAny(requestState.RedrawnTcs.Task, Task.Delay(_redrawEventTimeout, token)).ConfigureAwait(false);
        if (completed == requestState.RedrawnTcs.Task)
        {
            return requestState.RedrawnTcs.Task.Result;
        }

        if (token.IsCancellationRequested)
        {
            return false;
        }

        requestState.RedrawnTcs.TrySetResult(false);
        return false;
    }

    internal void Cancel()
    {
        _disposalCts = _disposalCts.CancelRecreate();
    }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _disposalCts.Cancel();
            _disposalCts.Dispose();
        }
        _disposed = true;
    }

    private sealed class RedrawRequestState
    {
        public int PendingRequest = 1;
        public TaskCompletionSource<bool> RedrawnTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
