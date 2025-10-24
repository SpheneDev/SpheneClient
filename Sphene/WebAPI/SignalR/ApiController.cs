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
using System.Reflection;

namespace Sphene.WebAPI;

#pragma warning disable MA0040
public sealed partial class ApiController : DisposableMediatorSubscriberBase, ISpheneHubClient
{

#if DEBUG
    public const string MainServer = "Sphene Debug Server";
    public const string MainServiceUri = "ws://test.sphene.online:6000";
#else
    public const string MainServer = "Sphene Server";
    public const string MainServiceUri = "ws://sphene.online:6000";
#endif

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
        Mediator.Subscribe<SendCharacterDataAcknowledgmentMessage>(this, (msg) => _ = UserSendCharacterDataAcknowledgment(msg.AcknowledgmentDto));

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

    public async Task CreateConnectionsAsync()
    {
        if (!_serverManager.ShownCensusPopup)
        {
            Mediator.Publish(new OpenCensusPopupMessage());
            while (!_serverManager.ShownCensusPopup)
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        Logger.LogDebug("CreateConnections called");

        if (_serverManager.CurrentServer?.FullPause ?? true)
        {
            Logger.LogInformation("Not recreating Connection, paused");
            _connectionDto = null;
            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
            _connectionCancellationTokenSource?.Cancel();
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
                _connectionCancellationTokenSource?.Cancel();
                return;
            }

            if (secretKey.IsNullOrEmpty())
            {
                Logger.LogWarning("No secret key set for current character");
                _connectionDto = null;
                await StopConnectionAsync(ServerState.NoSecretKey).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
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
                _connectionCancellationTokenSource?.Cancel();
                return;
            }

            if (!oauth2.HasValue)
            {
                Logger.LogWarning("No UID/OAuth set for current character");
                _connectionDto = null;
                await StopConnectionAsync(ServerState.OAuthMisconfigured).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
                return;
            }

            if (!await _tokenProvider.TryUpdateOAuth2LoginTokenAsync(_serverManager.CurrentServer).ConfigureAwait(false))
            {
                Logger.LogWarning("OAuth2 login token could not be updated");
                _connectionDto = null;
                await StopConnectionAsync(ServerState.OAuthLoginTokenStale).ConfigureAwait(false);
                _connectionCancellationTokenSource?.Cancel();
                return;
            }
        }

        await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);

        Logger.LogInformation("Recreating Connection");
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Informational,
            $"Starting Connection to {_serverManager.CurrentServer.ServerName}")));

        _connectionCancellationTokenSource?.Cancel();
        _connectionCancellationTokenSource?.Dispose();
        _connectionCancellationTokenSource = new CancellationTokenSource();
        var token = _connectionCancellationTokenSource.Token;
        while (ServerState is not ServerState.Connected && !token.IsCancellationRequested)
        {
            AuthFailureMessage = string.Empty;

            await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false);
            ServerState = ServerState.Connecting;

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

                _connectionDto = await GetConnectionDto().ConfigureAwait(false);

                ServerState = ServerState.Connected;
                _healthMonitor.RecordSuccessfulConnection();

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

                await LoadIninitialPairsAsync().ConfigureAwait(false);
                await LoadOnlinePairsAsync().ConfigureAwait(false);
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
                
                // Use exponential backoff instead of random delay
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
                
                // Use exponential backoff instead of random delay
                var delay = CalculateRetryDelay(_healthMonitor.ConsecutiveFailures);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
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

    public async Task<ConnectionDto> GetConnectionDtoAsync(bool publishConnected)
    {
        var dto = await _spheneHub!.InvokeAsync<ConnectionDto>(nameof(GetConnectionDto)).ConfigureAwait(false);
        Logger.LogInformation("[DEBUG] ConnectionDto received - FileServerAddress: {fileServerAddress}, ServerVersion: {serverVersion}, User: {user}", 
            dto.ServerInfo.FileServerAddress, dto.ServerVersion, dto.User.AliasOrUID);
        if (publishConnected) 
        {
            Logger.LogInformation("[DEBUG] Publishing ConnectedMessage with FileServerAddress: {fileServerAddress}", dto.ServerInfo.FileServerAddress);
            Mediator.Publish(new ConnectedMessage(dto));
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

        _healthCheckTokenSource?.Cancel();
        _ = Task.Run(async () => await StopConnectionAsync(ServerState.Disconnected).ConfigureAwait(false));
        _connectionCancellationTokenSource?.Cancel();
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
            Logger.LogInformation("Logging into {chara}", charaName);
            _ = Task.Run(CreateConnectionsAsync);
        }
        else
        {
            Logger.LogInformation("Not logging into {chara}, auto login disabled", charaName);
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
        OnUserReceiveUploadStatus(dto => _ = Client_UserReceiveUploadStatus(dto));
        OnUserUpdateProfile(dto => _ = Client_UserUpdateProfile(dto));
        OnUserDefaultPermissionUpdate(dto => _ = Client_UserUpdateDefaultPermissions(dto));
        OnUpdateUserIndividualPairStatusDto(dto => _ = Client_UpdateUserIndividualPairStatusDto(dto));
        OnUserReceiveCharacterDataAcknowledgment(dto => _ = Client_UserReceiveCharacterDataAcknowledgment(dto));

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

        _healthCheckTokenSource?.Cancel();
        _healthCheckTokenSource?.Dispose();
        _healthCheckTokenSource = new CancellationTokenSource();
        _ = ClientHealthCheckAsync(_healthCheckTokenSource.Token);

        _initialized = true;
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

    private void SpheneHubOnClosed(Exception? arg)
    {
        _healthCheckTokenSource?.Cancel();
        Mediator.Publish(new DisconnectedMessage());
        ServerState = ServerState.Offline;
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
            await LoadIninitialPairsAsync().ConfigureAwait(false);
            await LoadOnlinePairsAsync().ConfigureAwait(false);
            Mediator.Publish(new ConnectedMessage(_connectionDto));
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
        _healthCheckTokenSource?.Cancel();
        ServerState = ServerState.Reconnecting;
        Logger.LogWarning(arg, "Connection closed... Reconnecting");
        Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Warning,
            $"Connection interrupted, reconnecting to {_serverManager.CurrentServer.ServerName}")));

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

    private async Task StopConnectionAsync(ServerState state)
    {
        ServerState = ServerState.Disconnecting;

        Logger.LogInformation("Stopping existing connection");
        await _hubFactory.DisposeHubAsync().ConfigureAwait(false);

        if (_spheneHub is not null)
        {
            Mediator.Publish(new EventMessage(new Services.Events.Event(nameof(ApiController), Services.Events.EventSeverity.Informational,
                $"Stopping existing connection to {_serverManager.CurrentServer.ServerName}")));

            _initialized = false;
            _healthCheckTokenSource?.Cancel();
            Mediator.Publish(new DisconnectedMessage());
            _spheneHub = null;
            _connectionDto = null;
        }

        ServerState = state;
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

    public async Task Client_AreaBoundSyncshellBroadcast(AreaBoundBroadcastDto dto)
    {
        await ExecuteSafely(async () =>
        {
            Logger.LogDebug("Received area-bound syncshell broadcast for group: {GroupId} ({GroupAlias})", dto.Group.GID, dto.Group.Alias);
            Logger.LogDebug("Broadcast area: {Area}, Users in area: {UserCount}", dto.Area, dto.UsersInArea.Count);
            
            // Create and publish an area-bound syncshell notification message
            var title = "Area Syncshell Available";
            var message = $"Area-bound syncshell '{dto.Group.Alias}' is available in this area!";
            
            // Get the notification location setting from config
            var notificationLocation = _SpheneConfigService?.Current?.AreaBoundSyncshellNotification ?? NotificationLocation.Toast;
            
            Logger.LogDebug("Creating area-bound notification: {Title} - {Message} (Location: {Location})", title, message, notificationLocation);
            
            // Create a custom notification message for area-bound syncshells
            var notificationMessage = new AreaBoundSyncshellNotificationMessage(title, message, notificationLocation);
            Mediator.Publish(notificationMessage);
            
            Logger.LogDebug("Published area-bound syncshell notification for group: {GroupAlias}", dto.Group.Alias);
        });
    }

    public async Task Client_AreaBoundSyncshellConfigurationUpdate()
    {
        await ExecuteSafely(async () =>
        {
            Logger.LogDebug("Received area-bound syncshell configuration update notification");
            
            // Publish a mediator message to notify the AreaBoundSyncshellService
            Mediator.Publish(new AreaBoundSyncshellConfigurationUpdateMessage());
            
            Logger.LogDebug("Published area-bound syncshell configuration update message");
        });
    }

    public async Task BroadcastAreaBoundSyncshells(LocationInfo userLocation)
    {
        CheckConnection();
        await _spheneHub!.SendAsync(nameof(BroadcastAreaBoundSyncshells), userLocation).ConfigureAwait(false);
    }

    private TimeSpan CalculateRetryDelay(int failureCount)
    {
        // Exponential backoff with jitter
        var baseDelaySeconds = 2;
        var maxDelaySeconds = 60;
        var jitterFactor = 0.1;
        
        var exponentialDelay = Math.Min(baseDelaySeconds * Math.Pow(2, failureCount), maxDelaySeconds);
        var jitter = exponentialDelay * jitterFactor * (new Random().NextDouble() * 2 - 1);
        var finalDelay = exponentialDelay + jitter;
        
        Logger.LogDebug("Calculated retry delay: {delay}s (failure count: {count})", finalDelay, failureCount);
        return TimeSpan.FromSeconds(Math.Max(1, finalDelay));
    }
}
#pragma warning restore MA0040
