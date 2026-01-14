using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;

namespace Sphene.Services;

public sealed class ReleaseChangelogStartupService : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<ReleaseChangelogStartupService> _logger;
    private readonly SpheneConfigService _configService;
    private readonly SpheneMediator _mediator;
    private readonly ChangelogService _changelogService;
    private CancellationTokenSource? _cts;
    private volatile bool _uiReady;
    private string _pendingVersionString = string.Empty;
    private int _published;
    private Task? _publishTask;

    public SpheneMediator Mediator => _mediator;

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

        _mediator.Subscribe<UiServiceInitializedMessage>(this, (msg) =>
        {
            _uiReady = true;
            StartPublish();
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var assembly = Assembly.GetExecutingAssembly();
            var version = NormalizeVersion(assembly.GetName().Version);
            var currentVersion = ToVersionString(version);

            var lastSeen = _configService.Current.LastSeenVersionChangelog ?? string.Empty;
            _logger.LogDebug("ReleaseChangelogStartupService: current={current}, lastSeen={last}", currentVersion, lastSeen);

            if (ShouldShowChangelog(version, lastSeen))
            {
                _pendingVersionString = currentVersion;
                _logger.LogDebug("Release changelog pending until UI is initialized. version={version}", currentVersion);
                StartPublish();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ReleaseChangelogStartupService StartAsync");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _mediator.UnsubscribeAll(this);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    private async Task TryPublishAsync()
    {
        try
        {
            if (!_uiReady)
                return;

            var versionString = _pendingVersionString;
            if (string.IsNullOrWhiteSpace(versionString))
                return;

            if (Interlocked.Exchange(ref _published, 1) != 0)
                return;

            string? text = null;
            try
            {
                text = await _changelogService.GetChangelogTextForVersionAsync(versionString, _cts?.Token ?? CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch release changelog text");
            }

            _logger.LogInformation("Publishing ShowReleaseChangelogMessage for version {version}", versionString);
            _mediator.Publish(new ShowReleaseChangelogMessage(versionString, text));
            _configService.Current.LastSeenVersionChangelog = versionString;
            _configService.Save();
            _pendingVersionString = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while publishing startup changelog");
        }
    }

    private void StartPublish()
    {
        if (_publishTask is { IsCompleted: false })
            return;

        _publishTask = TryPublishAsync();
    }

    private static bool ShouldShowChangelog(Version current, string lastSeen)
    {
        if (string.IsNullOrWhiteSpace(lastSeen))
            return true;

        if (!Version.TryParse(lastSeen.Trim(), out var parsed))
            return true;

        var lastSeenNormalized = NormalizeVersion(parsed);
        return current > lastSeenNormalized;
    }

    private static Version NormalizeVersion(Version? v)
    {
        if (v == null)
            return new Version(0, 0, 0, 0);

        var major = v.Major < 0 ? 0 : v.Major;
        var minor = v.Minor < 0 ? 0 : v.Minor;
        var build = v.Build < 0 ? 0 : v.Build;
        var revision = v.Revision < 0 ? 0 : v.Revision;
        return new Version(major, minor, build, revision);
    }

    private static string ToVersionString(Version v)
        => $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
}
