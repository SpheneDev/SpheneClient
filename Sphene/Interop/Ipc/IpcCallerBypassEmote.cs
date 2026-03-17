using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;

namespace Sphene.Interop.Ipc;

public sealed class IpcCallerBypassEmote : IIpcCaller
{
    private const int RequiredApiMajorVersion = 3;
    private const int RequiredApiMinorVersion = 0;
    private static readonly TimeSpan ImmediateDuplicateSuppressWindow = TimeSpan.FromMilliseconds(800);

    private readonly ILogger<IpcCallerBypassEmote> _logger;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SpheneMediator _mediator;

    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<bool> _isReady;
    private readonly ICallGateSubscriber<nint, string> _getStateForCharacter;
    private readonly ICallGateSubscriber<nint, string> _getStateForCharacterV1;
    private readonly ICallGateSubscriber<string, bool, object> _setStateV1;
    private readonly ICallGateSubscriber<nint, string, object> _setStateForCharacter;
    private readonly ICallGateSubscriber<nint, string, object> _setStateForCharacterV1;
    private readonly ICallGateSubscriber<nint, object> _clearStateForCharacter;
    private readonly ICallGateSubscriber<nint, object> _clearStateForCharacterV1;
    private readonly ICallGateSubscriber<string, object> _onStateChangeLegacy;
    private readonly ICallGateSubscriber<string, string?, bool, object> _onStateChange;
    private readonly ICallGateSubscriber<string, string?, bool, object> _onStateChangeV1;
    private readonly ICallGateSubscriber<string, object> _onStateChangeImmediateLegacy;
    private readonly ICallGateSubscriber<string, string?, bool, object> _onStateChangeImmediate;
    private readonly ICallGateSubscriber<string, string?, bool, object> _onStateChangeImmediateV1;
    private readonly ICallGateSubscriber<object> _onReady;
    private readonly ICallGateSubscriber<object> _onDispose;
    private string _lastApiDiagnostics = string.Empty;
    private string _lastLiveIpcData = string.Empty;
    private string _lastCacheIpcData = string.Empty;
    private string _lastImmediateData = string.Empty;
    private DateTime _lastImmediateTime = DateTime.MinValue;

    public IpcCallerBypassEmote(
        ILogger<IpcCallerBypassEmote> logger,
        IDalamudPluginInterface pi,
        DalamudUtilService dalamudUtil,
        SpheneMediator mediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _mediator = mediator;

        _apiVersion = pi.GetIpcSubscriber<(int, int)>("BypassEmote.ApiVersion");
        _isReady = pi.GetIpcSubscriber<bool>("BypassEmote.IsReady");
        _getStateForCharacter = pi.GetIpcSubscriber<nint, string>("BypassEmote.GetStateForCharacter");
        _getStateForCharacterV1 = pi.GetIpcSubscriber<nint, string>("BypassEmote.GetStateForCharacterV1");
        _setStateV1 = pi.GetIpcSubscriber<string, bool, object>("BypassEmote.SetStateV1");
        _setStateForCharacter = pi.GetIpcSubscriber<nint, string, object>("BypassEmote.SetStateForCharacter");
        _setStateForCharacterV1 = pi.GetIpcSubscriber<nint, string, object>("BypassEmote.SetStateForCharacterV1");
        _clearStateForCharacter = pi.GetIpcSubscriber<nint, object>("BypassEmote.ClearStateForCharacter");
        _clearStateForCharacterV1 = pi.GetIpcSubscriber<nint, object>("BypassEmote.ClearStateForCharacterV1");
        _onStateChangeLegacy = pi.GetIpcSubscriber<string, object>("BypassEmote.OnStateChange");
        _onStateChange = pi.GetIpcSubscriber<string, string?, bool, object>("BypassEmote.OnStateChange");
        _onStateChangeV1 = pi.GetIpcSubscriber<string, string?, bool, object>("BypassEmote.OnStateChangeV1");
        _onStateChangeImmediateLegacy = pi.GetIpcSubscriber<string, object>("BypassEmote.OnStateChangeImmediate");
        _onStateChangeImmediate = pi.GetIpcSubscriber<string, string?, bool, object>("BypassEmote.OnStateChangeImmediate");
        _onStateChangeImmediateV1 = pi.GetIpcSubscriber<string, string?, bool, object>("BypassEmote.OnStateChangeImmediateV1");
        _onReady = pi.GetIpcSubscriber<object>("BypassEmote.OnReady");
        _onDispose = pi.GetIpcSubscriber<object>("BypassEmote.OnDispose");

        TrySubscribe(() => _onStateChangeLegacy.Subscribe(OnStateChangeLegacy), "BypassEmote.OnStateChange (legacy)");
        TrySubscribe(() => _onStateChange.Subscribe(OnStateChange), "BypassEmote.OnStateChange");
        TrySubscribe(() => _onStateChangeV1.Subscribe(OnStateChangeV1), "BypassEmote.OnStateChangeV1");
        TrySubscribe(() => _onStateChangeImmediateLegacy.Subscribe(OnStateChangeImmediateLegacy), "BypassEmote.OnStateChangeImmediate (legacy)");
        TrySubscribe(() => _onStateChangeImmediate.Subscribe(OnStateChangeImmediate), "BypassEmote.OnStateChangeImmediate");
        TrySubscribe(() => _onStateChangeImmediateV1.Subscribe(OnStateChangeImmediateV1), "BypassEmote.OnStateChangeImmediateV1");
        TrySubscribe(() => _onReady.Subscribe(OnReady), "BypassEmote.OnReady");
        TrySubscribe(() => _onDispose.Subscribe(OnDispose), "BypassEmote.OnDispose");

        CheckAPI();
    }

    public bool APIAvailable { get; private set; }
    public bool PluginDetected { get; private set; }

    public void CheckAPI()
    {
        var hasAnySignal = false;
        var versionCompatible = true;
        var versionText = "n/a";
        var isReadyText = "n/a";

        try
        {
            var version = _apiVersion.InvokeFunc();
            hasAnySignal = true;
            versionText = $"{version.Item1}.{version.Item2}";
            versionCompatible = version.Item1 > RequiredApiMajorVersion
                                || (version.Item1 == RequiredApiMajorVersion && version.Item2 >= RequiredApiMinorVersion);
        }
        catch (Exception ex)
        {
            if (ex is IpcNotReadyError)
            {
                hasAnySignal = true;
            }
            _logger.LogDebug(ex, "BypassEmote.ApiVersion IPC gate is not available");
        }

        try
        {
            isReadyText = _isReady.InvokeFunc().ToString();
            hasAnySignal = true;
        }
        catch (Exception ex)
        {
            if (ex is IpcNotReadyError)
            {
                hasAnySignal = true;
            }
            _logger.LogDebug(ex, "BypassEmote.IsReady IPC gate is not available");
        }

        PluginDetected = hasAnySignal;
        APIAvailable = hasAnySignal && versionCompatible;
        var diagnostics = $"apiAvailable={APIAvailable};hasSignal={hasAnySignal};version={versionText};ready={isReadyText}";
        if (!string.Equals(_lastApiDiagnostics, diagnostics, StringComparison.Ordinal))
        {
            _lastApiDiagnostics = diagnostics;
            _logger.LogDebug("BypassEmote API state changed: {state}", diagnostics);
        }
    }

    public async Task<string> GetStateForCharacterAsync(nint characterAddress)
    {
        if (characterAddress == nint.Zero) return string.Empty;

        if (IsIpcDataPayload(_lastCacheIpcData))
        {
            return _lastCacheIpcData;
        }

        if (IsIpcDataPayload(_lastLiveIpcData))
        {
            return _lastLiveIpcData;
        }

        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() => TryGetState(characterAddress)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not obtain BypassEmote data");
        }

        return string.Empty;
    }

    public async Task SetStateForCharacterAsync(nint characterAddress, string data)
    {
        if (characterAddress == nint.Zero) return;

        try
        {
            _logger.LogDebug("Applying BypassEmote state: addr={address}, len={length}, apiAvailable={apiAvailable}",
                characterAddress, data.Length, APIAvailable);
            await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                if (string.IsNullOrEmpty(data))
                {
                    TryInvokeClear(characterAddress);
                }
                else
                {
                    TryInvokeSet(characterAddress, data);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply BypassEmote data");
        }
    }

    private string TryGetState(nint characterAddress)
    {
        try
        {
            PluginDetected = true;
            return _getStateForCharacter.InvokeFunc(characterAddress);
        }
        catch (Exception ex)
        {
            if (ex is IpcNotReadyError)
            {
                PluginDetected = true;
            }
            _logger.LogDebug(ex, "BypassEmote.GetStateForCharacter failed, trying V1");
            try
            {
                PluginDetected = true;
                return _getStateForCharacterV1.InvokeFunc(characterAddress);
            }
            catch (Exception v1Ex)
            {
                if (v1Ex is IpcNotReadyError)
                {
                    PluginDetected = true;
                }
                _logger.LogWarning(v1Ex, "BypassEmote.GetStateForCharacterV1 failed");
                return string.Empty;
            }
        }
    }

    private void TryInvokeSet(nint characterAddress, string data)
    {
        if (IsIpcDataPayload(data))
        {
            try
            {
                PluginDetected = true;
                _setStateV1.InvokeAction(data, true);
                return;
            }
            catch (Exception ex)
            {
                if (ex is IpcNotReadyError)
                {
                    PluginDetected = true;
                }

                _logger.LogDebug(ex, "BypassEmote.SetStateV1 failed while applying IpcData payload");
                return;
            }
        }

        try
        {
            PluginDetected = true;
            _setStateForCharacter.InvokeAction(characterAddress, data);
        }
        catch (Exception ex)
        {
            if (ex is IpcNotReadyError)
            {
                PluginDetected = true;
            }
            _logger.LogDebug(ex, "BypassEmote.SetStateForCharacter failed, trying V1");
            try
            {
                PluginDetected = true;
                _setStateForCharacterV1.InvokeAction(characterAddress, data);
            }
            catch (Exception v1Ex)
            {
                if (v1Ex is IpcNotReadyError)
                {
                    PluginDetected = true;
                }
                _logger.LogWarning(v1Ex, "BypassEmote.SetStateForCharacterV1 failed");
            }
        }
    }

    private void TryInvokeClear(nint characterAddress)
    {
        try
        {
            PluginDetected = true;
            _clearStateForCharacter.InvokeAction(characterAddress);
        }
        catch (Exception ex)
        {
            if (ex is IpcNotReadyError)
            {
                PluginDetected = true;
            }
            _logger.LogDebug(ex, "BypassEmote.ClearStateForCharacter failed, trying V1");
            try
            {
                PluginDetected = true;
                _clearStateForCharacterV1.InvokeAction(characterAddress);
            }
            catch (Exception v1Ex)
            {
                if (v1Ex is IpcNotReadyError)
                {
                    PluginDetected = true;
                }
                _logger.LogWarning(v1Ex, "BypassEmote.ClearStateForCharacterV1 failed");
            }
        }
    }

    private void OnStateChangeLegacy(string data)
    {
        HandleStateChange(data, null, false);
    }

    private void OnStateChange(string liveData, string? cacheData, bool isLocalPlayer)
    {
        HandleStateChange(liveData, cacheData, isLocalPlayer);
    }

    private void OnStateChangeV1(string liveData, string? cacheData, bool isLocalPlayer)
    {
        HandleStateChange(liveData, cacheData, isLocalPlayer);
    }

    private void OnStateChangeImmediateLegacy(string data)
    {
        HandleImmediateStateChange(data, null, false);
    }

    private void OnStateChangeImmediate(string liveData, string? cacheData, bool isLocalPlayer)
    {
        HandleImmediateStateChange(liveData, cacheData, isLocalPlayer);
    }

    private void OnStateChangeImmediateV1(string liveData, string? cacheData, bool isLocalPlayer)
    {
        HandleImmediateStateChange(liveData, cacheData, isLocalPlayer);
    }

    private void HandleStateChange(string liveData, string? cacheData, bool _)
    {
        CacheIpcPayload(liveData, cacheData);

        if (string.Equals(liveData, _lastImmediateData, StringComparison.Ordinal)
            && DateTime.UtcNow - _lastImmediateTime <= ImmediateDuplicateSuppressWindow)
        {
            return;
        }

        PublishState(liveData);
    }

    private void HandleImmediateStateChange(string liveData, string? cacheData, bool _)
    {
        CacheIpcPayload(liveData, cacheData);
        _lastImmediateData = liveData;
        _lastImmediateTime = DateTime.UtcNow;
        PublishState(liveData);
    }

    private void PublishState(string data)
    {
        _mediator.Publish(new BypassEmoteMessage($"{data}|{DateTime.UtcNow.Ticks}"));
    }

    private void OnReady()
    {
        CheckAPI();
        _mediator.Publish(new BypassEmoteReadyMessage());
        _ = PublishCurrentStateAsync();
    }

    private void OnDispose()
    {
        _mediator.Publish(new BypassEmoteMessage(string.Empty));
    }

    private async Task PublishCurrentStateAsync()
    {
        var playerAddress = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
        var state = await GetStateForCharacterAsync(playerAddress).ConfigureAwait(false);
        if (IsIpcDataPayload(state))
        {
            PublishState(state);
        }
    }

    private void CacheIpcPayload(string liveData, string? cacheData)
    {
        if (IsIpcDataPayload(liveData))
        {
            _lastLiveIpcData = liveData;
        }

        if (IsIpcDataPayload(cacheData))
        {
            _lastCacheIpcData = cacheData!;
        }
        else if (cacheData == null)
        {
            _lastCacheIpcData = string.Empty;
        }
    }

    private static bool IsIpcDataPayload(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        return data.Contains("\"PlayerData\"", StringComparison.Ordinal)
               && data.Contains("\"Configuration\"", StringComparison.Ordinal);
    }

    private void TrySubscribe(Action subscribeAction, string gateName)
    {
        try
        {
            subscribeAction.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BypassEmote IPC gate unavailable during subscribe: {gate}", gateName);
        }
    }

    private void TryUnsubscribe(Action unsubscribeAction, string gateName)
    {
        try
        {
            unsubscribeAction.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "BypassEmote IPC gate unavailable during unsubscribe: {gate}", gateName);
        }
    }

    public void Dispose()
    {
        TryUnsubscribe(() => _onStateChangeLegacy.Unsubscribe(OnStateChangeLegacy), "BypassEmote.OnStateChange (legacy)");
        TryUnsubscribe(() => _onStateChange.Unsubscribe(OnStateChange), "BypassEmote.OnStateChange");
        TryUnsubscribe(() => _onStateChangeV1.Unsubscribe(OnStateChangeV1), "BypassEmote.OnStateChangeV1");
        TryUnsubscribe(() => _onStateChangeImmediateLegacy.Unsubscribe(OnStateChangeImmediateLegacy), "BypassEmote.OnStateChangeImmediate (legacy)");
        TryUnsubscribe(() => _onStateChangeImmediate.Unsubscribe(OnStateChangeImmediate), "BypassEmote.OnStateChangeImmediate");
        TryUnsubscribe(() => _onStateChangeImmediateV1.Unsubscribe(OnStateChangeImmediateV1), "BypassEmote.OnStateChangeImmediateV1");
        TryUnsubscribe(() => _onReady.Unsubscribe(OnReady), "BypassEmote.OnReady");
        TryUnsubscribe(() => _onDispose.Unsubscribe(OnDispose), "BypassEmote.OnDispose");
    }
}
