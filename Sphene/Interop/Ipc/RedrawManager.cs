using Dalamud.Game.ClientState.Objects.Types;
using Sphene.PlayerData.Handlers;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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

    public async Task PenumbraRedrawInternalAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, Action<ICharacter> action, CancellationToken token)
    {
        _spheneMediator.Publish(new PenumbraStartRedrawMessage(handler.Address));

        var requestState = new RedrawRequestState();
        _penumbraRedrawRequests[handler.Address] = requestState;

        try
        {
            using CancellationTokenSource cancelToken = new CancellationTokenSource();
            using CancellationTokenSource combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken.Token, token, _disposalCts.Token);
            var combinedToken = combinedCts.Token;
            cancelToken.CancelAfter(TimeSpan.FromSeconds(15));
            await handler.ActOnFrameworkAfterEnsureNoDrawAsync(action, combinedToken).ConfigureAwait(false);

            if (!_disposalCts.Token.IsCancellationRequested)
                await _dalamudUtil.WaitWhileCharacterIsDrawing(logger, handler, applicationId, 30000, combinedToken).ConfigureAwait(false);
            await WaitForRequestedRedrawEventAsync(requestState, combinedToken).ConfigureAwait(false);
        }
        finally
        {
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

    private async Task WaitForRequestedRedrawEventAsync(RedrawRequestState requestState, CancellationToken token)
    {
        if (Volatile.Read(ref requestState.PendingRequest) == 0)
        {
            return;
        }

        var completed = await Task.WhenAny(requestState.RedrawnTcs.Task, Task.Delay(_redrawEventTimeout, token)).ConfigureAwait(false);
        if (completed == requestState.RedrawnTcs.Task)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        requestState.RedrawnTcs.TrySetResult(false);
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
