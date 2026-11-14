using Microsoft.Extensions.Logging;

namespace Sphene.Services.Mediator;

public abstract class DisposableMediatorSubscriberBase : MediatorSubscriberBase, IDisposable
{
    protected DisposableMediatorSubscriberBase(ILogger logger, SpheneMediator mediator) : base(logger, mediator)
    {
    }

    // The DI scope can run the dispose chain more than once (a shared instance
    // is captured once per descriptor). The guard lives here - not in
    // Dispose(bool) - so derived overrides are skipped on re-entry too.
    private int _disposed;

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Dispose(disposing: true);
        }
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        Logger.LogTrace("Disposing {type} ({this})", GetType().Name, this);
        UnsubscribeAll();
    }
}
