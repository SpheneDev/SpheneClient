using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.Services;

// Dummy hosted service that does nothing but allows us to force initialization of other services
public class DummyHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}