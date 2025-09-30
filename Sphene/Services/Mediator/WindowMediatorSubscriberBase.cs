using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;

namespace Sphene.Services.Mediator;

public abstract class WindowMediatorSubscriberBase : Window, IMediatorSubscriber, IDisposable
{
    protected readonly ILogger _logger;
    private readonly PerformanceCollectorService _performanceCollectorService;

    protected WindowMediatorSubscriberBase(ILogger logger, SpheneMediator mediator, string name,
        PerformanceCollectorService performanceCollectorService) : base(name)
    {
        _logger = logger;
        Mediator = mediator;
        _performanceCollectorService = performanceCollectorService;
        _logger.LogTrace("Creating {type}", GetType());

        Mediator.Subscribe<UiToggleMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                var wasOpen = IsOpen;
                _logger.LogDebug("UiToggleMessage received for {type}. Toggling window. Previous IsOpen={wasOpen}", GetType().Name, wasOpen);
                Toggle();
                _logger.LogDebug("Window toggled for {type}. New IsOpen={isOpen}", GetType().Name, IsOpen);
            }
        });
    }

    public SpheneMediator Mediator { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public override void Draw()
    {
        _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
    }

    protected abstract void DrawInternal();

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        _logger.LogTrace("Disposing {type}", GetType());

        Mediator.UnsubscribeAll(this);
    }
}
