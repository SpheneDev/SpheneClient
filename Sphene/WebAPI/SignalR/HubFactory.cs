using Sphene.API.SignalR;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.WebAPI.SignalR.Utils;
using Sphene.SpheneConfiguration;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Sphene.WebAPI.SignalR;

public class HubFactory : MediatorSubscriberBase
{
    private readonly ILoggerProvider _loggingProvider;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly TokenProvider _tokenProvider;
    private readonly SpheneConfigService _configService;
    private HubConnection? _instance;
    private bool _isDisposed = false;
    private readonly bool _isWine = false;

    private static bool IsDevTestBuild
    {
        get
        {
#if IS_TEST_BUILD
            return true;
#else
            return false;
#endif
        }
    }

    public HubFactory(ILogger<HubFactory> logger, SpheneMediator mediator,
        ServerConfigurationManager serverConfigurationManager,
        TokenProvider tokenProvider, ILoggerProvider pluginLog,
        DalamudUtilService dalamudUtilService, SpheneConfigService configService) : base(logger, mediator)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _tokenProvider = tokenProvider;
        _loggingProvider = pluginLog;
        _isWine = dalamudUtilService.IsWine;
        _configService = configService;
    }

    public async Task DisposeHubAsync()
    {
        if (_instance == null || _isDisposed) return;

        Logger.LogDebug("Disposing current HubConnection");

        _isDisposed = true;

        _instance.Closed -= HubOnClosed;
        _instance.Reconnecting -= HubOnReconnecting;
        _instance.Reconnected -= HubOnReconnected;

        await _instance.StopAsync().ConfigureAwait(false);
        await _instance.DisposeAsync().ConfigureAwait(false);

        _instance = null;

        Logger.LogDebug("Current HubConnection disposed");
    }

    public HubConnection GetOrCreate(CancellationToken ct)
    {
        if (!_isDisposed && _instance != null) return _instance;

        return BuildHubConnection(ct);
    }

    private HubConnection BuildHubConnection(CancellationToken ct)
    {
        var transportType = _serverConfigurationManager.GetTransport() switch
        {
            HttpTransportType.None => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
            HttpTransportType.WebSockets => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
            HttpTransportType.ServerSentEvents => HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
            HttpTransportType.LongPolling => HttpTransportType.LongPolling,
            _ => HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling
        };

        if (_isWine && !_serverConfigurationManager.CurrentServer.ForceWebSockets
            && transportType.HasFlag(HttpTransportType.WebSockets))
        {
            Logger.LogDebug("Wine detected, falling back to ServerSentEvents / LongPolling");
            transportType = HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
        }

        Logger.LogDebug("Building new HubConnection using transport {transport}", transportType);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var userAgent = $"Sphene/{version!.Major}.{version!.Minor}.{version!.Build}";
        
        var baseUrl = GetBaseApiUrl();
        _instance = new HubConnectionBuilder()
            .WithUrl(baseUrl + ISpheneHub.Path, options =>
            {
                options.AccessTokenProvider = () => _tokenProvider.GetOrUpdateToken(ct);
                options.Transports = transportType;
                options.Headers.Add("User-Agent", userAgent);
            })
            .AddMessagePackProtocol(opt =>
            {
                var resolver = CompositeResolver.Create(StandardResolverAllowPrivate.Instance,
                    BuiltinResolver.Instance,
                    AttributeFormatterResolver.Instance,
                    // replace enum resolver
                    DynamicEnumAsStringResolver.Instance,
                    DynamicGenericResolver.Instance,
                    DynamicUnionResolver.Instance,
                    DynamicObjectResolver.Instance,
                    PrimitiveObjectResolver.Instance,
                    // final fallback(last priority)
                    StandardResolver.Instance);

                opt.SerializerOptions =
                    MessagePackSerializerOptions.Standard
                        .WithCompression(MessagePackCompression.Lz4Block)
                        .WithResolver(resolver);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy(Mediator))
            .ConfigureLogging(a =>
            {
                a.ClearProviders().AddProvider(_loggingProvider);
                a.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        _instance.Closed += HubOnClosed;
        _instance.Reconnecting += HubOnReconnecting;
        _instance.Reconnected += HubOnReconnected;

        _isDisposed = false;

        return _instance;
    }

    private string GetBaseApiUrl()
    {
        try
        {
            if (IsDevTestBuild && _configService.Current.UseTestServerOverride && !string.IsNullOrWhiteSpace(_configService.Current.TestServerApiUrl))
            {
                return _configService.Current.TestServerApiUrl.TrimEnd('/');
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read TestServer override from config");
        }
        return _serverConfigurationManager.CurrentApiUrl;
    }

    private Task HubOnClosed(Exception? arg)
    {
        Mediator.Publish(new HubClosedMessage(arg));
        return Task.CompletedTask;
    }

    private Task HubOnReconnected(string? arg)
    {
        Mediator.Publish(new HubReconnectedMessage(arg));
        return Task.CompletedTask;
    }

    private Task HubOnReconnecting(Exception? arg)
    {
        Mediator.Publish(new HubReconnectingMessage(arg));
        return Task.CompletedTask;
    }
}
