using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Sphene.Services.Mediator;

namespace Sphene.Services;

public class LoginHandler : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly UpdateCheckService _updateCheckService;
    private readonly ILogger<LoginHandler> _logger;

    public LoginHandler(ILogger<LoginHandler> logger, SpheneMediator mediator, UpdateCheckService updateCheckService)
        : base(logger, mediator)
    {
        _updateCheckService = updateCheckService;
        _logger = logger;

        // Subscribe to login event to check for updates
        Mediator.Subscribe<DalamudLoginMessage>(this, async (_) =>
        {
            _logger.LogInformation("Player logged in, checking for updates...");
            await _updateCheckService.CheckForUpdatesAsync();
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // LoginHandler is now initialized as a hosted service
        _logger.LogInformation("LoginHandler started and ready to check for updates on login");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Cleanup if needed
        }
        base.Dispose(disposing);
    }
}