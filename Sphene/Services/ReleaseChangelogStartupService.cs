using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;

namespace Sphene.Services;

public sealed class ReleaseChangelogStartupService : IHostedService
{
    private readonly ILogger<ReleaseChangelogStartupService> _logger;
    private readonly SpheneConfigService _configService;
    private readonly SpheneMediator _mediator;
    private readonly ChangelogService _changelogService;

    public ReleaseChangelogStartupService(
        ILogger<ReleaseChangelogStartupService> logger,
        SpheneConfigService configService,
        SpheneMediator mediator,
        ChangelogService changelogService)
    {
        _logger = logger;
        _configService = configService;
        _mediator = mediator;
        _changelogService = changelogService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var currentVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : string.Empty;

            var lastSeen = _configService.Current.LastSeenVersionChangelog ?? string.Empty;
            _logger.LogDebug("ReleaseChangelogStartupService: current={current}, lastSeen={last}", currentVersion, lastSeen);

            if (!string.IsNullOrEmpty(currentVersion) && !string.Equals(currentVersion, lastSeen, StringComparison.Ordinal))
            {
                string? text = null;
                try
                {
                    text = _changelogService != null
                        ? await _changelogService.GetChangelogTextForVersionAsync(currentVersion, cancellationToken).ConfigureAwait(false)
                        : null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch release changelog text");
                }

                _logger.LogInformation("Publishing ShowReleaseChangelogMessage for version {version}", currentVersion);
                _mediator.Publish(new ShowReleaseChangelogMessage(currentVersion, text));
                _configService.Current.LastSeenVersionChangelog = currentVersion;
                _configService.Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ReleaseChangelogStartupService StartAsync");
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}