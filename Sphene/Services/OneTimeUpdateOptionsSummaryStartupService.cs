using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.UI.Panels;

namespace Sphene.Services;

public sealed class OneTimeUpdateOptionsSummaryStartupService : IHostedService, IMediatorSubscriber
{
    private static readonly (string Key, bool ExistingUsersOnly)[] PromptSequence =
    [
        ("2026-03-sync-char-temp-whitelist", true)
    ];

    private readonly ILogger<OneTimeUpdateOptionsSummaryStartupService> _logger;
    private readonly SpheneConfigService _configService;
    private readonly SpheneMediator _mediator;
    private bool _uiReady;
    private string _pendingPromptKey = string.Empty;
    private int _published;
    private CancellationTokenSource? _cts;
    private Task? _publishTask;
    private bool _releaseChangelogStateKnown;
    private bool _releaseChangelogWillShow;
    private bool _releaseChangelogClosed = true;

    public SpheneMediator Mediator => _mediator;

    public OneTimeUpdateOptionsSummaryStartupService(
        ILogger<OneTimeUpdateOptionsSummaryStartupService> logger,
        SpheneConfigService configService,
        SpheneMediator mediator)
    {
        _logger = logger;
        _configService = configService;
        _mediator = mediator;

        _mediator.Subscribe<UiServiceInitializedMessage>(this, _ =>
        {
            _uiReady = true;
            StartPublishLoop();
        });
        _mediator.Subscribe<ReleaseChangelogStartupStateMessage>(this, msg =>
        {
            _releaseChangelogStateKnown = true;
            _releaseChangelogWillShow = msg.WillShow;
            _releaseChangelogClosed = !msg.WillShow;
            StartPublishLoop();
        });
        _mediator.Subscribe<ShowReleaseChangelogMessage>(this, _ =>
        {
            _releaseChangelogStateKnown = true;
            _releaseChangelogWillShow = true;
            _releaseChangelogClosed = false;
            StartPublishLoop();
        });
        _mediator.Subscribe<ReleaseChangelogClosedMessage>(this, _ =>
        {
            _releaseChangelogStateKnown = true;
            _releaseChangelogClosed = true;
            StartPublishLoop();
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pendingPromptKey = ResolvePendingPromptKey();
            StartPublishLoop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize one-time update options summary startup service");
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

    private string ResolvePendingPromptKey()
    {
        foreach (var (key, existingUsersOnly) in PromptSequence)
        {
            if (_configService.Current.SeenOneTimeOptionSummaryPrompts.Contains(key))
            {
                continue;
            }

            if (existingUsersOnly && !_configService.Current.HasValidSetup())
            {
                _configService.Current.SeenOneTimeOptionSummaryPrompts.Add(key);
                _configService.Save();
                _logger.LogDebug("Skipping one-time update options summary for key={key} because setup is not completed", key);
                continue;
            }

            return key;
        }

        return string.Empty;
    }

    private void StartPublishLoop()
    {
        if (_publishTask is { IsCompleted: false }) return;
        _publishTask = PublishWhenReadyAsync(_cts?.Token ?? CancellationToken.None);
    }

    private async Task PublishWhenReadyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_uiReady || string.IsNullOrWhiteSpace(_pendingPromptKey))
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            if (!_releaseChangelogStateKnown)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            if (_releaseChangelogWillShow && !_releaseChangelogClosed)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            if (Interlocked.CompareExchange(ref _published, 0, 0) != 0)
            {
                return;
            }

            if (_releaseChangelogWillShow && IsReleaseChangelogOpen())
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            if (Interlocked.Exchange(ref _published, 1) != 0)
            {
                return;
            }

            _logger.LogInformation("Publishing one-time update options summary prompt key={key}", _pendingPromptKey);
            _mediator.Publish(new ShowOneTimeUpdateOptionsSummaryMessage(_pendingPromptKey));
            return;
        }
    }

    private bool IsReleaseChangelogOpen()
    {
        var isOpen = false;
        _mediator.Publish(new QueryWindowOpenStateMessage(typeof(ReleaseChangelogUi), state => isOpen = state));
        return isOpen;
    }
}
