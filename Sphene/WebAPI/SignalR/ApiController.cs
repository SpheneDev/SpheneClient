using Dalamud.Utility;
using Sphene.API.Data;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.API.Dto.CharaData;
using Sphene.API.SignalR;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.WebAPI.SignalR;
using Sphene.WebAPI.SignalR.Utils;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;

namespace Sphene.WebAPI;

#pragma warning disable MA0040
public sealed partial class ApiController : DisposableMediatorSubscriberBase, ISpheneHubClient
{
    public const string MainServer = "Sphene Server";
    public const string MainServiceUri = "ws://sphene.online:6000";

    private readonly DalamudUtilService _dalamudUtil;
    private readonly HubFactory _hubFactory;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly TokenProvider _tokenProvider;
    private readonly SpheneConfigService _SpheneConfigService;
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly CircuitBreakerService _circuitBreaker;
    private CancellationTokenSource _connectionCancellationTokenSource;
    private ConnectionDto? _connectionDto;
    private bool _doNotNotifyOnNextInfo = false;
    private CancellationTokenSource? _healthCheckTokenSource = new();
    private bool _initialized;
    private string? _lastUsedToken;
    private HubConnection? _spheneHub;
    private ServerState _serverState;
    private CensusUpdateMessage? _lastCensus;
    private readonly ConcurrentDictionary<string, FileTransferAckMessage> _pendingFileTransferAcks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectionLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _disconnectSimulationGate = new(1, 1);
    private CancellationTokenSource? _disconnectGraceTokenSource;
    private bool _hasPendingDisconnectGrace;
    private bool _showConnectedDuringGrace;
    private DateTimeOffset _suppressInfoMessagesUntil = DateTimeOffset.MinValue;
    private readonly TimeSpan _disconnectGraceWindow = TimeSpan.FromSeconds(10);
    private DateTimeOffset _disconnectGraceStartedAtUtc = DateTimeOffset.MinValue;
    private bool _didHardDisconnectAfterGrace;
    private bool _pendingPostHardReconnect;
    private bool _postGraceSecondReconnectRequested;
    private bool _postGraceUiHoldActive;
    private string _postGraceUiHoldReason = string.Empty;
    private PostGraceUiHoldStage _postGraceUiHoldStage = PostGraceUiHoldStage.None;
    private DateTimeOffset _postGraceUiHoldStartedAtUtc = DateTimeOffset.MinValue;
    private bool _hasEverConnected;
    private DateTimeOffset _initialConnectAttemptStartedAtUtc = DateTimeOffset.MinValue;

    private enum PostGraceUiHoldStage
    {
        None = 0,
        TryingToReconnect = 1,
        ConnectedReloading = 2,
        RefreshingConnection = 3,
    }

    public ApiController(ILogger<ApiController> logger, HubFactory hubFactory, DalamudUtilService dalamudUtil,
        PairManager pairManager, ServerConfigurationManager serverManager, SpheneMediator mediator,
        TokenProvider tokenProvider, SpheneConfigService SpheneConfigService, 
        ConnectionHealthMonitor healthMonitor, CircuitBreakerService circuitBreaker) : base(logger, mediator)
    {
        _hubFactory = hubFactory;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _tokenProvider = tokenProvider;
        _SpheneConfigService = SpheneConfigService;
        _healthMonitor = healthMonitor;
        _circuitBreaker = circuitBreaker;
        _connectionCancellationTokenSource = new CancellationTokenSource();

        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => DalamudUtilOnLogIn());
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => DalamudUtilOnLogOut());
        Mediator.Subscribe<HubClosedMessage>(this, (msg) => SpheneHubOnClosed(msg.Exception));
        Mediator.Subscribe<HubReconnectedMessage>(this, (msg) => _ = SpheneHubOnReconnectedAsync());
        Mediator.Subscribe<HubReconnectingMessage>(this, (msg) => SpheneHubOnReconnecting(msg.Exception));
        Mediator.Subscribe<CyclePauseMessage>(this, (msg) => _ = CyclePauseAsync(msg.UserData));
        Mediator.Subscribe<CensusUpdateMessage>(this, (msg) => _lastCensus = msg);
        Mediator.Subscribe<PauseMessage>(this, (msg) => _ = PauseAsync(msg.UserData));
        Mediator.Subscribe<SendCharacterDataAcknowledgmentMessage>(this, (msg) => _ = UserSendCharacterDataAcknowledgmentV2(new CharacterDataAcknowledgmentEventDto(msg.AcknowledgmentDto)));
        Mediator.Subscribe<FileTransferAckMessage>(this, (msg) => _ = UserAckFileTransfer(msg));

        ServerState = ServerState.Offline;

        if (_dalamudUtil.IsLoggedIn)
        {
            DalamudUtilOnLogIn();
        }
    }

    public string AuthFailureMessage { get; private set; } = string.Empty;

    public Version CurrentClientVersion => _connectionDto?.CurrentClientVersion ?? new Version(0, 0, 0);

    public DefaultPermissionsDto? DefaultPermissions => _connectionDto?.DefaultPreferredPermissions ?? null;
    public string DisplayName => _connectionDto?.User.AliasOrUID ?? string.Empty;

    public bool IsConnected => ServerState == ServerState.Connected;

    public bool IsCurrentVersion => (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0)) >= (_connectionDto?.CurrentClientVersion ?? new Version(0, 0, 0, 0));

    public bool IsAdmin => _connectionDto?.IsAdmin ?? false;

    public bool IsModerator => _connectionDto?.IsModerator ?? false;

    public int OnlineUsers => SystemInfoDto.OnlineUsers;

    public bool ServerAlive => ServerState is ServerState.Connected or ServerState.RateLimited or ServerState.Unauthorized or ServerState.Disconnected;

    public ServerInfo ServerInfo => _connectionDto?.ServerInfo ?? new ServerInfo();

    public ServerState DisplayServerState
        => (_showConnectedDuringGrace || _hasPendingDisconnectGrace) ? ServerState.Connected : ServerState;
    public bool IsTransientDisconnectInProgress => _hasPendingDisconnectGrace;
    public bool IsDisconnectSimulationRunning { get; private set; }
    public bool IsPostHardDisconnectReconnectPending => _didHardDisconnectAfterGrace || _pendingPostHardReconnect;
    public bool IsPostGraceUiHoldActive => _postGraceUiHoldActive || _pendingPostHardReconnect;
    public string PostGraceUiHoldReason => _postGraceUiHoldReason;
    public int PostGraceUiHoldStageValue => (int)_postGraceUiHoldStage;
    public TimeSpan PostGraceUiHoldElapsed => _postGraceUiHoldStartedAtUtc == DateTimeOffset.MinValue ? TimeSpan.Zero : (DateTimeOffset.UtcNow - _postGraceUiHoldStartedAtUtc);
    public bool HasEverConnected => _hasEverConnected;
    public TimeSpan InitialConnectElapsed => _initialConnectAttemptStartedAtUtc == DateTimeOffset.MinValue ? TimeSpan.Zero : (DateTimeOffset.UtcNow - _initialConnectAttemptStartedAtUtc);

    public ServerState ServerState
    {
        get => _serverState;
        private set
        {
            Logger.LogDebug("New ServerState: {value}, prev ServerState: {_serverState}", value, _serverState);
            _serverState = value;
        }
    }

    public SystemInfoDto SystemInfoDto { get; private set; } = new();

    public string UID => _connectionDto?.User.UID ?? string.Empty;

    public async Task<bool> CheckClientHealth()
    {
        return await _spheneHub!.InvokeAsync<bool>(nameof(CheckClientHealth)).ConfigureAwait(false);
    }

    private bool ShouldSuppressInfoServerMessage()
    {
        if (_doNotNotifyOnNextInfo)
        {
            _doNotNotifyOnNextInfo = false;
            return true;
        }

        if (IsTransientDisconnectInProgress)
        {
            return true;
        }

        return DateTimeOffset.UtcNow < _suppressInfoMessagesUntil;
    }

    private void SuppressInfoServerMessagesFor(TimeSpan duration)
    {
        var until = DateTimeOffset.UtcNow.Add(duration);
        if (until > _suppressInfoMessagesUntil)
        {
            _suppressInfoMessagesUntil = until;
        }
    }

    public async Task SimulateDisconnectForTestingAsync(TimeSpan disconnectDuration)
    {
        if (disconnectDuration < TimeSpan.FromSeconds(1))
        {
            disconnectDuration = TimeSpan.FromSeconds(1);
        }

        if (disconnectDuration > TimeSpan.FromMinutes(5))
        {
            disconnectDuration = TimeSpan.FromMinutes(5);
        }

        await _disconnectSimulationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IsDisconnectSimulationRunning = true;
            Logger.LogInformation("Starting disconnect simulation for {seconds}s", disconnectDuration.TotalSeconds);
            await StopConnectionAsync(ServerState.Disconnected, useGrace: true).ConfigureAwait(false);
            await Task.Delay(disconnectDuration).ConfigureAwait(false);

            if (_serverManager.CurrentServer == null || _serverManager.CurrentServer.FullPause)
            {
                Logger.LogInformation("Disconnect simulation ended without reconnect because server is paused or unavailable");
                return;
            }

            Logger.LogInformation("Disconnect simulation reconnecting now");
            await CreateConnectionsAsync().ConfigureAwait(false);
        }
        finally
        {
            IsDisconnectSimulationRunning = false;
            _disconnectSimulationGate.Release();
        }
    }

    public async Task CreateConnectionsAsync(bool forceCharacterDataReload = false)
    {
        await _connectionLifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_serverManager.ShownCensusPopup)
            {
                Mediator.Publish(new OpenCensusPopupMessage());
                while (!_serverManager.ShownCensusPopup)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);

            Logger.LogDebug("CreateConnections called");

            if (_serverManager.CurrentServer?.FullPause ?? true)
            {
                Logger.LogDebug("Not recreating Connection, paused");
                _connectionDto = null;
                await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
                if (_connectionCancellationTokenSource is not null)
                    await _connectionCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                return;
            }

            if (!_serverManager.CurrentServer.UseOAuth2)
            {
                var secretKey = _serverManager.GetSecretKey(out bool multi);
                if (multi)
                {
                    Logger.LogWarning("Multiple secret keys for current character");
                    _connectionDto = null;
                    Mediator.Publish(new NotificationMessage("Multiple Identical Characters detected", "Your Service configuration has multiple characters with the same name and world set up. Delete the duplicates in the character management to be able to connect to Sphene.",
                        NotificationType.Error));
                    await StopConnectionAsync(ServerState.MultiChara).ConfigureAwait(false);
                    if (_connectionCancellationTokenSource is not null)
                        await _connectionCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (secretKey.IsNullOrEmpty())
                {
                    Logger.LogWarning("No secret key set for current character");
                    _connectionDto = null;
                    await StopConnectionAsync(ServerState.NoSecretKey).ConfigureAwait(false);
                    if (_connectionCancellationTokenSource is not null)
                        await _connectionCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                var oauth2 = _serverManager.GetOAuth2(out bool multi);
                if (multi)
                {
                    Logger.LogWarning("Multiple secret keys for current character");
                    _connectionDto = null;
                    Mediator.Publish(new NotificationMessage("Multiple Identical Characters detected", "Your Service configuration has multiple characters with the same name and world set up. Delete the duplicates in the character management to be able to connect to Sphene.",
                        NotificationType.Error));
                    await StopConnectionAsync(ServerState.MultiChara).ConfigureAwait(false);
                    if (_connectionCancellationTokenSource is not null)
                        await _connectionCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (!oauth2.HasValue)
                {
                    Logger.LogWarning("No UID/OAuth set for current character");
                    _connectionDto = null;
                    await StopConnectionAsync(ServerState.OAuthMisconfigured).ConfigureAwait(false);
                    if (_connectionCancellationTokenSource is not null)
                        await _connectionCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (!await _tokenProvider.TryUpdateOAuth2LoginTokenAsync(_serverManager.CurrentServer).ConfigureAwait(false))
                {
                    Logger.LogWarning("OAuth2 login token could not be updated");
                    _connectionDto = null;
                    await StopConnectionAsync(ServerState.OAuthLoginTokenStale).ConfigureAwait(false);
                    if (_connectionCancellationTokenSource is not null)
                        await _connectionCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }

            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);

            Logger.LogDebug("Recreating Connection");
            Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Informational,
                $"Starting Connection to {_serverManager.CurrentServer.ServerName}")));

            if (_connectionCancellationTokenSource is not null)
                await _connectionCancellationTokenSource.CancelAsync().ConfigureAwait(false);
            _connectionCancellationTokenSource?.Dispose();
            _connectionCancellationTokenSource = new CancellationTokenSource();
            var token = _connectionCancellationTokenSource.Token;
            while (ServerState is not ServerState.Connected && !token.IsCancellationRequested)
            {
                AuthFailureMessage = string.Empty;

                await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
                ServerState = ServerState.Connecting;
                if (!_hasEverConnected && _initialConnectAttemptStartedAtUtc == DateTimeOffset.MinValue)
                {
                    _initialConnectAttemptStartedAtUtc = DateTimeOffset.UtcNow;
                }

                try
                {
                    Logger.LogDebug("Building connection");

                    try
                    {
                        _lastUsedToken = await _tokenProvider.GetOrUpdateToken(token).ConfigureAwait(false);
                    }
                    catch (SpheneAuthFailureException ex)
                    {
                        AuthFailureMessage = ex.Reason;
                        throw new HttpRequestException("Error during authentication", ex, System.Net.HttpStatusCode.Unauthorized);
                    }

                    while (!await _dalamudUtil.GetIsPlayerPresentAsync().ConfigureAwait(false) && !token.IsCancellationRequested)
                    {
                        Logger.LogDebug("Player not loaded in yet, waiting");
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested) break;

                    _spheneHub = _hubFactory.GetOrCreate(token);
                    InitializeApiHooks();

                    await _spheneHub.StartAsync(token).ConfigureAwait(false);

                    var preserveUiDuringGraceReconnect = _hasPendingDisconnectGrace;
                    if (preserveUiDuringGraceReconnect)
                    {
                        SuppressInfoServerMessagesFor(TimeSpan.FromSeconds(20));
                    }
                    _connectionDto = await GetConnectionDtoAsync(
                        publishConnected: false,
                        forceCharacterDataReload: forceCharacterDataReload).ConfigureAwait(false);

                    ServerState = ServerState.Connected;
                    _hasEverConnected = true;
                    _initialConnectAttemptStartedAtUtc = DateTimeOffset.MinValue;
                    CancelPendingDisconnectGrace(clearUiGraceOnly: false);
                    _healthMonitor.RecordSuccessfulConnection();
                    if (_postGraceUiHoldActive || _postGraceSecondReconnectRequested)
                    {
                        _postGraceUiHoldStage = PostGraceUiHoldStage.ConnectedReloading;
                    }
                    SchedulePostHardDisconnectReconnectIfNeeded();

                    var currentClientVer = Assembly.GetExecutingAssembly().GetName().Version!;

                    if (_connectionDto.ServerVersion != ISpheneHub.ApiVersion)
                    {
                        if (_connectionDto.CurrentClientVersion > currentClientVer)
                        {
                            Mediator.Publish(new NotificationMessage("Client incompatible",
                                $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                                $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
                                $"This client version is incompatible and will not be able to connect. Please update your Sphene client.",
                                NotificationType.Error));
                        }
                        await StopConnectionAsync(ServerState.VersionMisMatch).ConfigureAwait(false);
                        return;
                    }

                    if (_connectionDto.CurrentClientVersion > currentClientVer)
                    {
                        Mediator.Publish(new NotificationMessage("Client outdated",
                            $"Your client is outdated ({currentClientVer.Major}.{currentClientVer.Minor}.{currentClientVer.Build}), current is: " +
                            $"{_connectionDto.CurrentClientVersion.Major}.{_connectionDto.CurrentClientVersion.Minor}.{_connectionDto.CurrentClientVersion.Build}. " +
                            $"Please keep your Sphene client up-to-date.",
                            NotificationType.Warning));
                    }

                    await FlushPendingFileTransferAcksAsync().ConfigureAwait(false);

                    if (_dalamudUtil.HasModifiedGameFiles)
                    {
                        Logger.LogError("Detected modified game files on connection");
                        if (!_SpheneConfigService.Current.DebugStopWhining)
                            Mediator.Publish(new NotificationMessage("Modified Game Files detected",
                                "Dalamud is reporting your FFXIV installation has modified game files. Any mods installed through TexTools will produce this message. " +
                                "Sphene, Penumbra, and some other plugins assume your FFXIV installation is unmodified in order to work. " +
                                "Synchronization with pairs/shells can break because of this. Exit the game, open XIVLauncher, click the arrow next to Log In " +
                                "and select 'repair game files' to resolve this issue. Afterwards, do not install any mods with TexTools. Your plugin configurations will remain, as will mods enabled in Penumbra.",
                                NotificationType.Error, TimeSpan.FromSeconds(15)));
                    }

                    if (_dalamudUtil.IsLodEnabled && !_naggedAboutLod)
                    {
                        _naggedAboutLod = true;
                        Logger.LogWarning("Model LOD is enabled during connection");
                        if (!_SpheneConfigService.Current.DebugStopWhining)
                        {
                            Mediator.Publish(new NotificationMessage("Model LOD is enabled",
                                "You have \"Use low-detail models on distant objects (LOD)\" enabled. Having model LOD enabled is known to be a reason to cause " +
                                "random crashes when loading in or rendering modded pairs. Disabling LOD has a very low performance impact. Disable LOD while using Sphene: " +
                                "Go to XIV Menu -> System Configuration -> Graphics Settings and disable the model LOD option.", NotificationType.Warning, TimeSpan.FromSeconds(15)));
                        }
                    }

                    if (_naggedAboutLod && !_dalamudUtil.IsLodEnabled)
                    {
                        _naggedAboutLod = false;
                    }

                    if (preserveUiDuringGraceReconnect)
                    {
                        await RefreshPairStateAfterReconnectAsync(forceCharacterDataReload: forceCharacterDataReload).ConfigureAwait(false);
                        Logger.LogDebug("CreateConnections completed within grace window, refreshed online state");
                    }
                    else
                    {
                        await RefreshPairStateAfterReconnectAsync(forceCharacterDataReload: forceCharacterDataReload).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.LogWarning("Connection attempt cancelled");
                    return;
                }
                catch (HttpRequestException ex)
                {
                    Logger.LogWarning(ex, "HttpRequestException on Connection");
                    _healthMonitor.RecordConnectionFailure(ex);

                    if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        await StopConnectionAsync(ServerState.Unauthorized).ConfigureAwait(false);
                        return;
                    }

                    ServerState = ServerState.Reconnecting;
                    Logger.LogInformation("Failed to establish connection, retrying");

                    var delay = CalculateRetryDelay(_healthMonitor.ConsecutiveFailures);
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    Logger.LogWarning(ex, "InvalidOperationException on connection");
                    _healthMonitor.RecordConnectionFailure(ex);
                    await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Exception on Connection");
                    _healthMonitor.RecordConnectionFailure(ex);

                    Logger.LogInformation("Failed to establish connection, retrying");

                    var delay = CalculateRetryDelay(_healthMonitor.ConsecutiveFailures);
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _connectionLifecycleGate.Release();
        }
    }

    private bool _naggedAboutLod = false;

    public Task CyclePauseAsync(UserData userData)
    {
        CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        _ = Task.Run(async () =>
        {
            var pair = _pairManager.GetOnlineUserPairs().Single(p => p.UserPair != null && p.UserData == userData);
            var perm = pair.UserPair!.OwnPermissions;
            perm.SetPaused(paused: true);
            await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
            // wait until it's changed
            while (pair.UserPair!.OwnPermissions != perm)
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                Logger.LogTrace("Waiting for permissions change for {data}", userData);
            }
            perm.SetPaused(paused: false);
            await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
        }, cts.Token).ContinueWith((t) => cts.Dispose());

        return Task.CompletedTask;
    }

    public async Task PauseAsync(UserData userData)
    {
        var pair = _pairManager.GetOnlineUserPairs().Single(p => p.UserPair != null && p.UserData == userData);
        var perm = pair.UserPair!.OwnPermissions;
        perm.SetPaused(paused: true);
        await UserSetPairPermissions(new UserPermissionsDto(userData, perm)).ConfigureAwait(false);
    }

    public Task<ConnectionDto> GetConnectionDto() => GetConnectionDtoAsync(true);

    public async Task<ConnectionDto> GetConnectionDtoAsync(bool publishConnected, bool forceCharacterDataReload = false)
    {
        var dto = await _spheneHub!.InvokeAsync<ConnectionDto>(nameof(GetConnectionDto)).ConfigureAwait(false);
        Logger.LogDebug("ConnectionDto received - FileServerAddress: {fileServerAddress}, ServerVersion: {serverVersion}, User: {user}", 
            dto.ServerInfo.FileServerAddress, dto.ServerVersion, dto.User.AliasOrUID);
        if (publishConnected) 
        {
            Logger.LogDebug("Publishing ConnectedMessage with FileServerAddress: {fileServerAddress}", dto.ServerInfo.FileServerAddress);
            Mediator.Publish(new ConnectedMessage(dto, forceCharacterDataReload));
        }
        return dto;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _healthMonitor?.Dispose();
            _circuitBreaker?.Dispose();
        }
        
        base.Dispose(disposing);

        if (_healthCheckTokenSource is not null)
            _ = _healthCheckTokenSource.CancelAsync();
        _ = Task.Run(async () => await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false));
        if (_connectionCancellationTokenSource is not null)
            _ = _connectionCancellationTokenSource.CancelAsync();
    }

    private async Task ClientHealthCheckAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _spheneHub != null)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            Logger.LogDebug("Checking Client Health State");

            bool requireReconnect = await RefreshTokenAsync(ct).ConfigureAwait(false);

            if (requireReconnect) break;

            try
            {
                var healthCheckResult = await CheckClientHealth().ConfigureAwait(false);
                Logger.LogDebug("Health check completed with result: {result}", healthCheckResult);
                
                // Record successful health check regardless of server response
                // The fact that we can communicate with the server means the connection is healthy
                _healthMonitor.RecordSuccessfulConnection();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Health check failed");
                _healthMonitor.RecordConnectionFailure(ex);
            }
        }
    }

    private void DalamudUtilOnLogIn()
    {
        var charaName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        var auth = _serverManager.CurrentServer.Authentications.Find(f => string.Equals(f.CharacterName, charaName, StringComparison.Ordinal) && f.WorldId == worldId);
        if (auth?.AutoLogin ?? false)
        {
            Logger.LogDebug("Logging into {chara}", charaName);
            _ = Task.Run(() => CreateConnectionsAsync());
        }
        else
        {
            Logger.LogDebug("Not logging into {chara}, auto login disabled", charaName);
            _ = Task.Run(async () => await StopConnectionAsync(ServerState.NoAutoLogon).ConfigureAwait(false));
        }
    }

    private void DalamudUtilOnLogOut()
    {
        _ = Task.Run(async () => await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false));
        ServerState = ServerState.Offline;
    }

    private void InitializeApiHooks()
    {
        if (_spheneHub == null) return;

        ClearApiHooks();

        Logger.LogDebug("Initializing data");
        OnDownloadReady((guid) => _ = Client_DownloadReady(guid));
        OnReceiveServerMessage((sev, msg) => _ = Client_ReceiveServerMessage(sev, msg));
        OnUpdateSystemInfo((dto) => _ = Client_UpdateSystemInfo(dto));

        OnUserSendOffline((dto) => _ = Client_UserSendOffline(dto));
        OnUserAddClientPair((dto) => _ = Client_UserAddClientPair(dto));
        OnUserReceiveCharacterData((dto) => _ = Client_UserReceiveCharacterData(dto));
        OnUserRemoveClientPair(dto => _ = Client_UserRemoveClientPair(dto));
        OnUserSendOnline(dto => _ = Client_UserSendOnline(dto));
        OnUserUpdateOtherPairPermissions(dto => _ = Client_UserUpdateOtherPairPermissions(dto));
        OnUserUpdateSelfPairPermissions(dto => _ = Client_UserUpdateSelfPairPermissions(dto));
        OnUserAckYouUpdate(dto => _ = Client_UserAckYouUpdate(dto));
        OnUserPenumbraReceivePreferenceUpdate(dto => _ = Client_UserPenumbraReceivePreferenceUpdate(dto));
        OnUserMutualVisibilityUpdate(dto => _ = Client_UserMutualVisibilityUpdate(dto));
        OnUserGposeStateUpdate(dto => _ = Client_UserGposeStateUpdate(dto));
        OnUserReceiveUploadStatus(dto => _ = Client_UserReceiveUploadStatus(dto));
        OnUserUpdateProfile(dto => _ = Client_UserUpdateProfile(dto));
        OnUserDefaultPermissionUpdate(dto => _ = Client_UserUpdateDefaultPermissions(dto));
        OnUpdateUserIndividualPairStatusDto(dto => _ = Client_UpdateUserIndividualPairStatusDto(dto));
        OnUserReceiveFileNotification(dto => _ = Client_UserReceiveFileNotification(dto));
        OnUserReceiveBypassEmote(dto => _ = Client_UserReceiveBypassEmote(dto));
        OnUserReceiveCharacterDataAcknowledgment(dto => _ = Client_UserReceiveCharacterDataAcknowledgment(dto));
        OnUserReceiveCharacterDataAcknowledgmentV2(dto => _ = Client_UserReceiveCharacterDataAcknowledgmentV2(dto));
        OnUserCharacterDataRefreshRequested(dto => _ = Client_UserCharacterDataRefreshRequested(dto));

        OnGroupChangePermissions((dto) => _ = Client_GroupChangePermissions(dto));
        OnGroupDelete((dto) => _ = Client_GroupDelete(dto));
        OnGroupPairChangeUserInfo((dto) => _ = Client_GroupPairChangeUserInfo(dto));
        OnGroupPairJoined((dto) => _ = Client_GroupPairJoined(dto));
        OnGroupPairLeft((dto) => _ = Client_GroupPairLeft(dto));
        OnGroupSendFullInfo((dto) => _ = Client_GroupSendFullInfo(dto));
        OnGroupSendInfo((dto) => _ = Client_GroupSendInfo(dto));
        OnGroupChangeUserPairPermissions((dto) => _ = Client_GroupChangeUserPairPermissions(dto));

        // Register area-bound syncshell callbacks
        OnAreaBoundSyncshellBroadcast((dto) => _ = Client_AreaBoundSyncshellBroadcast(dto));
        OnAreaBoundJoinRequest((dto) => _ = Client_AreaBoundJoinRequest(dto));
        OnAreaBoundJoinResponse((dto) => _ = Client_AreaBoundJoinResponse(dto));
        OnAreaBoundSyncshellConfigurationUpdate(() => _ = Client_AreaBoundSyncshellConfigurationUpdate());

        OnGposeLobbyJoin((dto) => _ = Client_GposeLobbyJoin(dto));
        OnGposeLobbyLeave((dto) => _ = Client_GposeLobbyLeave(dto));
        OnGposeLobbyPushCharacterData((dto) => _ = Client_GposeLobbyPushCharacterData(dto));
        OnGposeLobbyPushPoseData((dto, data) => _ = Client_GposeLobbyPushPoseData(dto, data));
        OnGposeLobbyPushWorldData((dto, data) => _ = Client_GposeLobbyPushWorldData(dto, data));

        if (_healthCheckTokenSource is not null)
            _ = _healthCheckTokenSource.CancelAsync();
        _healthCheckTokenSource?.Dispose();
        _healthCheckTokenSource = new CancellationTokenSource();
        _ = ClientHealthCheckAsync(_healthCheckTokenSource.Token);

        _initialized = true;
    }

    private void ClearApiHooks()
    {
        if (_spheneHub == null) return;

        _spheneHub.Remove(nameof(Client_DownloadReady));
        _spheneHub.Remove(nameof(Client_ReceiveServerMessage));
        _spheneHub.Remove(nameof(Client_UpdateSystemInfo));
        _spheneHub.Remove(nameof(Client_UserSendOffline));
        _spheneHub.Remove(nameof(Client_UserAddClientPair));
        _spheneHub.Remove(nameof(Client_UserReceiveCharacterData));
        _spheneHub.Remove(nameof(Client_UserRemoveClientPair));
        _spheneHub.Remove(nameof(Client_UserSendOnline));
        _spheneHub.Remove(nameof(Client_UserUpdateOtherPairPermissions));
        _spheneHub.Remove(nameof(Client_UserUpdateSelfPairPermissions));
        _spheneHub.Remove(nameof(Client_UserAckYouUpdate));
        _spheneHub.Remove(nameof(Client_UserPenumbraReceivePreferenceUpdate));
        _spheneHub.Remove(nameof(Client_UserMutualVisibilityUpdate));
        _spheneHub.Remove(nameof(Client_UserGposeStateUpdate));
        _spheneHub.Remove(nameof(Client_UserReceiveUploadStatus));
        _spheneHub.Remove(nameof(Client_UserUpdateProfile));
        _spheneHub.Remove(nameof(Client_UserUpdateDefaultPermissions));
        _spheneHub.Remove(nameof(Client_UpdateUserIndividualPairStatusDto));
        _spheneHub.Remove(nameof(Client_UserReceiveFileNotification));
        _spheneHub.Remove(nameof(Client_UserReceiveBypassEmote));
        _spheneHub.Remove(nameof(Client_UserReceiveCharacterDataAcknowledgment));
        _spheneHub.Remove(nameof(Client_UserReceiveCharacterDataAcknowledgmentV2));
        _spheneHub.Remove(nameof(Client_UserCharacterDataRefreshRequested));
        _spheneHub.Remove(nameof(Client_GroupChangePermissions));
        _spheneHub.Remove(nameof(Client_GroupDelete));
        _spheneHub.Remove(nameof(Client_GroupPairChangeUserInfo));
        _spheneHub.Remove(nameof(Client_GroupPairJoined));
        _spheneHub.Remove(nameof(Client_GroupPairLeft));
        _spheneHub.Remove(nameof(Client_GroupSendFullInfo));
        _spheneHub.Remove(nameof(Client_GroupSendInfo));
        _spheneHub.Remove(nameof(Client_GroupChangeUserPairPermissions));
        _spheneHub.Remove(nameof(Client_AreaBoundSyncshellBroadcast));
        _spheneHub.Remove(nameof(Client_AreaBoundJoinRequest));
        _spheneHub.Remove(nameof(Client_AreaBoundJoinResponse));
        _spheneHub.Remove(nameof(Client_AreaBoundSyncshellConfigurationUpdate));
        _spheneHub.Remove(nameof(Client_GposeLobbyJoin));
        _spheneHub.Remove(nameof(Client_GposeLobbyLeave));
        _spheneHub.Remove(nameof(Client_GposeLobbyPushCharacterData));
        _spheneHub.Remove(nameof(Client_GposeLobbyPushPoseData));
        _spheneHub.Remove(nameof(Client_GposeLobbyPushWorldData));
    }

    private async Task LoadIninitialPairsAsync()
    {
        foreach (var entry in await GroupsGetAll().ConfigureAwait(false))
        {
            Logger.LogDebug("Group: {entry}", entry);
            _pairManager.AddGroup(entry);
        }

        foreach (var userPair in await UserGetPairedClients().ConfigureAwait(false))
        {
            Logger.LogDebug("Individual Pair: {userPair}", userPair);
            _pairManager.AddUserPair(userPair);
        }
    }

    private async Task LoadOnlinePairsAsync()
    {
        CensusDataDto? dto = null;
        if (_serverManager.SendCensusData && _lastCensus != null)
        {
            var world = await _dalamudUtil.GetWorldIdAsync().ConfigureAwait(false);
            dto = new((ushort)world, _lastCensus.RaceId, _lastCensus.TribeId, _lastCensus.Gender);
            Logger.LogDebug("Attaching Census Data: {data}", dto);
        }

        foreach (var entry in await UserGetOnlinePairs(dto).ConfigureAwait(false))
        {
            Logger.LogDebug("Pair online: {pair}", entry);
            _pairManager.MarkPairOnline(entry, sendNotif: false);
        }
    }

    private async Task RefreshPairStateAfterReconnectAsync(bool forceCharacterDataReload)
    {
        var hasAnyPairs = _pairManager.DirectPairs.Count > 0 || _pairManager.GroupPairs.Count > 0;
        if (!hasAnyPairs)
        {
            await LoadIninitialPairsAsync().ConfigureAwait(false);
        }

        await LoadOnlinePairsAsync().ConfigureAwait(false);
        if (_connectionDto != null)
        {
            Mediator.Publish(new ConnectedMessage(_connectionDto, forceCharacterDataReload));
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    await LoadOnlinePairsAsync().ConfigureAwait(false);
                    Mediator.Publish(new DelayedFrameworkUpdateMessage());
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Post-connect reconcile failed");
                }
            });
        }
    }

    private void SpheneHubOnClosed(Exception? arg)
    {
        _ = Task.Run(async () => await StopConnectionAsync(ServerState.Offline, useGrace: true).ConfigureAwait(false));
        if (arg != null)
        {
            Logger.LogWarning(arg, "Connection closed");
        }
        else
        {
            Logger.LogInformation("Connection closed");
        }
    }

    private async Task SpheneHubOnReconnectedAsync()
    {
        var graceStarted = _disconnectGraceStartedAtUtc;
        var graceExceededByTime = graceStarted != DateTimeOffset.MinValue
                                  && (DateTimeOffset.UtcNow - graceStarted) >= _disconnectGraceWindow;
        var reconnectedDuringGrace = _hasPendingDisconnectGrace && !graceExceededByTime;
        ServerState = ServerState.Reconnecting;
        try
        {
            InitializeApiHooks();
            _connectionDto = await GetConnectionDtoAsync(publishConnected: false).ConfigureAwait(false);
            if (_connectionDto.ServerVersion != ISpheneHub.ApiVersion)
            {
                await StopConnectionAsync(ServerState.VersionMisMatch).ConfigureAwait(false);
                return;
            }
            ServerState = ServerState.Connected;

            if (graceExceededByTime)
            {
                _didHardDisconnectAfterGrace = true;
                _postGraceSecondReconnectRequested = true;
                _postGraceUiHoldActive = true;
                _postGraceUiHoldReason = "Connection issues detected. Recovering session state.";
                if (_postGraceUiHoldStartedAtUtc == DateTimeOffset.MinValue)
                {
                    _postGraceUiHoldStartedAtUtc = DateTimeOffset.UtcNow;
                }
            }
                if (_postGraceUiHoldActive || _postGraceSecondReconnectRequested)
                {
                    _postGraceUiHoldStage = PostGraceUiHoldStage.ConnectedReloading;
                }

            CancelPendingDisconnectGrace(clearUiGraceOnly: false);
            _hasEverConnected = true;
            _initialConnectAttemptStartedAtUtc = DateTimeOffset.MinValue;
            if (reconnectedDuringGrace)
            {
                SuppressInfoServerMessagesFor(TimeSpan.FromSeconds(20));
            }
            else
            {
                SchedulePostHardDisconnectReconnectIfNeeded();
            }
            await FlushPendingFileTransferAcksAsync().ConfigureAwait(false);
            if (reconnectedDuringGrace)
            {
                await RefreshPairStateAfterReconnectAsync(forceCharacterDataReload: false).ConfigureAwait(false);
                Logger.LogDebug("Reconnect completed within grace window, refreshed online state");
            }
            else
            {
                await RefreshPairStateAfterReconnectAsync(forceCharacterDataReload: false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Failure to obtain data after reconnection");
            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
        }
    }

    private void SpheneHubOnReconnecting(Exception? arg)
    {
        _doNotNotifyOnNextInfo = true;
        if (_healthCheckTokenSource is not null)
            _ = _healthCheckTokenSource.CancelAsync();
        if (_connectionDto != null)
        {
            _hasPendingDisconnectGrace = true;
            _showConnectedDuringGrace = true;
            if (_disconnectGraceStartedAtUtc == DateTimeOffset.MinValue)
            {
                _disconnectGraceStartedAtUtc = DateTimeOffset.UtcNow;
            }
            StartDisconnectGrace();
        }
        ServerState = ServerState.Reconnecting;
        Logger.LogWarning(arg, "Connection closed... Reconnecting");
        if (!_showConnectedDuringGrace)
        {
            Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Warning,
                $"Connection interrupted, reconnecting to {_serverManager.CurrentServer.ServerName}")));
        }

    }

    private async Task<bool> RefreshTokenAsync(CancellationToken ct)
    {
        bool requireReconnect = false;
        try
        {
            var token = await _tokenProvider.GetOrUpdateToken(ct).ConfigureAwait(false);
            if (!string.Equals(token, _lastUsedToken, StringComparison.Ordinal))
            {
                Logger.LogDebug("Reconnecting due to updated token");

                _doNotNotifyOnNextInfo = true;
                await CreateConnectionsAsync().ConfigureAwait(false);
                requireReconnect = true;
            }
        }
        catch (SpheneAuthFailureException ex)
        {
            AuthFailureMessage = ex.Reason;
            await StopConnectionAsync(ServerState.Unauthorized).ConfigureAwait(false);
            requireReconnect = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not refresh token, forcing reconnect");
            _doNotNotifyOnNextInfo = true;
            await CreateConnectionsAsync().ConfigureAwait(false);
            requireReconnect = true;
        }

        return requireReconnect;
    }

    private async Task StopConnectionAsync(ServerState state, bool useGrace = false)
    {
        if (useGrace && _connectionDto != null)
        {
            _hasPendingDisconnectGrace = true;
            _showConnectedDuringGrace = true;
            if (_disconnectGraceStartedAtUtc == DateTimeOffset.MinValue)
            {
                _disconnectGraceStartedAtUtc = DateTimeOffset.UtcNow;
            }
            SuppressInfoServerMessagesFor(TimeSpan.FromSeconds(20));
        }
        ServerState = ServerState.Disconnecting;

        Logger.LogDebug("Stopping existing connection");
        await _hubFactory.DisposeHubAsync().ConfigureAwait(false);

        var hadHub = _spheneHub is not null;
        if (_spheneHub is not null)
        {
            Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Informational,
                $"Stopping existing connection to {_serverManager.CurrentServer.ServerName}")));

            _initialized = false;
            if (_healthCheckTokenSource is not null)
                await _healthCheckTokenSource.CancelAsync().ConfigureAwait(false);
            _spheneHub = null;
        }

        if (useGrace && hadHub && state is ServerState.Disconnected or ServerState.Offline)
        {
            StartDisconnectGrace();
        }
        else
        {
            if (!useGrace && !hadHub && _hasPendingDisconnectGrace && state == ServerState.Disconnected)
            {
                Logger.LogDebug("Suppressing disconnected publish during grace reconnect");
                ServerState = state;
                return;
            }

            var shouldPublishDisconnected = hadHub || _hasPendingDisconnectGrace;
            var graceExceeded = _hasPendingDisconnectGrace
                                && _disconnectGraceStartedAtUtc != DateTimeOffset.MinValue
                                && (DateTimeOffset.UtcNow - _disconnectGraceStartedAtUtc) >= _disconnectGraceWindow;
            CancelPendingDisconnectGrace(clearUiGraceOnly: false);
            if (shouldPublishDisconnected)
            {
                if (graceExceeded)
                {
                    _didHardDisconnectAfterGrace = true;
                    _postGraceSecondReconnectRequested = true;
                    _postGraceUiHoldActive = true;
                    _postGraceUiHoldReason = "Connection issues detected. Preparing a clean reconnect.";
                    _postGraceUiHoldStage = PostGraceUiHoldStage.TryingToReconnect;
                    if (_postGraceUiHoldStartedAtUtc == DateTimeOffset.MinValue)
                    {
                        _postGraceUiHoldStartedAtUtc = DateTimeOffset.UtcNow;
                    }
                }
                Mediator.Publish(new DisconnectedMessage());
            }
            _connectionDto = null;
        }

        ServerState = state;
    }

    private void StartDisconnectGrace()
    {
        if (_disconnectGraceTokenSource != null)
        {
            return;
        }

        _hasPendingDisconnectGrace = true;
        _showConnectedDuringGrace = _connectionDto != null;
        if (_disconnectGraceStartedAtUtc == DateTimeOffset.MinValue)
        {
            _disconnectGraceStartedAtUtc = DateTimeOffset.UtcNow;
        }
        _disconnectGraceTokenSource = new CancellationTokenSource();
        var token = _disconnectGraceTokenSource.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var remaining = _disconnectGraceWindow - (DateTimeOffset.UtcNow - _disconnectGraceStartedAtUtc);
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }
                await Task.Delay(remaining, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || ServerState == ServerState.Connected)
                {
                    Logger.LogDebug("Disconnect grace window finished but cancelled or already connected; cancelling hard-disconnect arm");
                    return;
                }

                _hasPendingDisconnectGrace = false;
                _showConnectedDuringGrace = false;
                _didHardDisconnectAfterGrace = true;
                _postGraceSecondReconnectRequested = true;
                _postGraceUiHoldActive = true;
                _postGraceUiHoldReason = "Connection issues detected. Preparing a clean reconnect.";
                _postGraceUiHoldStage = PostGraceUiHoldStage.TryingToReconnect;
                if (_postGraceUiHoldStartedAtUtc == DateTimeOffset.MinValue)
                {
                    _postGraceUiHoldStartedAtUtc = DateTimeOffset.UtcNow;
                }
                Logger.LogDebug("Grace exceeded; arming post-grace second reconnect and UI hold");
                Mediator.Publish(new DisconnectedMessage());
                _connectionDto = null;
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("Disconnect grace window cancelled");
            }
        }, token);
    }

    private void SchedulePostHardDisconnectReconnectIfNeeded()
    {
        if (!_postGraceSecondReconnectRequested || _pendingPostHardReconnect)
        {
            Logger.LogDebug("Post-grace second reconnect not scheduled: requested={requested} pending={pending}", _postGraceSecondReconnectRequested, _pendingPostHardReconnect);
            return;
        }

        _pendingPostHardReconnect = true;
        _postGraceUiHoldStage = PostGraceUiHoldStage.ConnectedReloading;
        Logger.LogDebug("Scheduling post-grace second reconnect");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                _postGraceUiHoldStage = PostGraceUiHoldStage.RefreshingConnection;
                _doNotNotifyOnNextInfo = true;
                await CreateConnectionsAsync(forceCharacterDataReload: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Post-hard-disconnect reconnect failed");
            }
            finally
            {
                _pendingPostHardReconnect = false;
                _didHardDisconnectAfterGrace = false;
                _postGraceSecondReconnectRequested = false;
                _postGraceUiHoldActive = false;
                _postGraceUiHoldReason = string.Empty;
                _postGraceUiHoldStage = PostGraceUiHoldStage.None;
                _postGraceUiHoldStartedAtUtc = DateTimeOffset.MinValue;
                Logger.LogDebug("Post-grace second reconnect completed; clearing UI hold");
            }
        });
    }

    private void CancelPendingDisconnectGrace(bool clearUiGraceOnly)
    {
        _disconnectGraceTokenSource?.Cancel();
        _disconnectGraceTokenSource?.Dispose();
        _disconnectGraceTokenSource = null;
        _showConnectedDuringGrace = false;
        if (!clearUiGraceOnly)
        {
            _hasPendingDisconnectGrace = false;
            _disconnectGraceStartedAtUtc = DateTimeOffset.MinValue;
        }
    }

    public void OnAreaBoundSyncshellBroadcast(Action<AreaBoundBroadcastDto> act)
    {
        _spheneHub?.On(nameof(Client_AreaBoundSyncshellBroadcast), act);
    }

    public void OnAreaBoundJoinRequest(Action<AreaBoundJoinRequestDto> act)
    {
        _spheneHub?.On(nameof(Client_AreaBoundJoinRequest), act);
    }

    public void OnAreaBoundJoinResponse(Action<AreaBoundJoinResponseDto> act)
    {
        _spheneHub?.On(nameof(Client_AreaBoundJoinResponse), act);
    }

    public void OnAreaBoundSyncshellConfigurationUpdate(Action act)
    {
        _spheneHub?.On(nameof(Client_AreaBoundSyncshellConfigurationUpdate), act);
    }

    public Task Client_AreaBoundSyncshellBroadcast(AreaBoundBroadcastDto dto)
    {
        ExecuteSafely(() =>
        {
            Logger.LogDebug("Received area-bound syncshell broadcast for group: {GroupId} ({GroupAlias})", dto.Group.GID, dto.Group.Alias);
            Logger.LogDebug("Broadcast area: {Area}, Users in area: {UserCount}", dto.Area, dto.UsersInArea.Count);

            var title = "Area Syncshell Available";
            var message = dto.IsLocked
                ? $"Area-bound syncshell '{dto.Group.Alias}' is locked. Joining is currently disabled."
                : $"Area-bound syncshell '{dto.Group.Alias}' is available in this area!";

            var notificationLocation = _SpheneConfigService?.Current?.AreaBoundSyncshellNotification ?? NotificationLocation.Toast;

            Logger.LogDebug("Creating area-bound notification: {Title} - {Message} (Location: {Location})", title, message, notificationLocation);

            var notificationMessage = new AreaBoundSyncshellNotificationMessage(title, message, notificationLocation);
            Mediator.Publish(notificationMessage);

            Logger.LogDebug("Published area-bound syncshell notification for group: {GroupAlias}", dto.Group.Alias);
        });
        return Task.CompletedTask;
    }

    private static string BuildFileTransferAckKey(string hash, string senderUid)
        => senderUid + "|" + hash;

    private static string NormalizeFileTransferHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        var candidate = hash.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[candidate.Length];
        var length = 0;
        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                continue;
            }

            buffer[length++] = char.ToUpperInvariant(c);
            if (length > 40)
            {
                return candidate.Trim();
            }
        }

        if (length == 40)
        {
            return new string(buffer[..length]);
        }

        return candidate.Trim();
    }

    private static string NormalizeFileTransferSenderUid(string senderUid)
        => senderUid?.Trim() ?? string.Empty;

    private void EnqueueFileTransferAck(FileTransferAckMessage msg)
    {
        var normalizedHash = NormalizeFileTransferHash(msg.Hash);
        var normalizedSenderUid = NormalizeFileTransferSenderUid(msg.SenderUID);

        if (string.IsNullOrWhiteSpace(normalizedHash) || string.IsNullOrWhiteSpace(normalizedSenderUid))
        {
            return;
        }

        var normalizedMsg = msg with { Hash = normalizedHash, SenderUID = normalizedSenderUid };
        var key = BuildFileTransferAckKey(normalizedHash, normalizedSenderUid);
        _pendingFileTransferAcks[key] = normalizedMsg;
    }

    private async Task<bool> TrySendFileTransferAckAsync(FileTransferAckMessage msg)
    {
        if (!IsConnected || _spheneHub == null)
        {
            return false;
        }

        var normalizedHash = NormalizeFileTransferHash(msg.Hash);
        var normalizedSenderUid = NormalizeFileTransferSenderUid(msg.SenderUID);
        if (string.IsNullOrWhiteSpace(normalizedHash) || string.IsNullOrWhiteSpace(normalizedSenderUid))
        {
            return true;
        }

        try
        {
            await _spheneHub.InvokeAsync("UserAckFileTransfer", normalizedHash, normalizedSenderUid).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to send file transfer acknowledgment (queued for retry)");
            return false;
        }
    }

    private async Task FlushPendingFileTransferAcksAsync()
    {
        if (!IsConnected || _spheneHub == null || _pendingFileTransferAcks.IsEmpty)
        {
            return;
        }

        foreach (var kvp in _pendingFileTransferAcks.ToArray())
        {
            if (!await TrySendFileTransferAckAsync(kvp.Value).ConfigureAwait(false))
            {
                return;
            }

            _pendingFileTransferAcks.TryRemove(kvp.Key, out _);
        }
    }


    public Task Client_AreaBoundSyncshellConfigurationUpdate()
    {
        ExecuteSafely(() =>
        {
            Logger.LogDebug("Received area-bound syncshell configuration update notification");

            Mediator.Publish(new AreaBoundSyncshellConfigurationUpdateMessage());

            Logger.LogDebug("Published area-bound syncshell configuration update message");
        });
        return Task.CompletedTask;
    }

    public async Task BroadcastAreaBoundSyncshells(LocationInfo userLocation)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(BroadcastAreaBoundSyncshells), userLocation).ConfigureAwait(false);
    }

    private TimeSpan CalculateRetryDelay(int failureCount)
    {
        const double baseDelaySeconds = 2;
        const double maxDelaySeconds = 60;
        const double jitterFactor = 0.1;

        var exponentialDelaySeconds = Math.Min(baseDelaySeconds * Math.Pow(2, failureCount), maxDelaySeconds);
        var jitterSeconds = exponentialDelaySeconds * jitterFactor * (Random.Shared.NextDouble() * 2 - 1);
        var finalDelaySeconds = exponentialDelaySeconds + jitterSeconds;

        Logger.LogDebug("Calculated retry delay: {delay}s (failure count: {count})", finalDelaySeconds, failureCount);
        return TimeSpan.FromSeconds(Math.Max(1, finalDelaySeconds));
    }
}
#pragma warning restore MA0040
