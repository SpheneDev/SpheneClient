using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Sphene.Services;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;

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
    private readonly ICallGateSubscriber<nint, string, object> _setStateForCharacter;
    private readonly ICallGateSubscriber<nint, object> _clearStateForCharacter;
    private readonly ICallGateSubscriber<string, object> _onStateChange;
    private readonly ICallGateSubscriber<string, object> _onStateChangeImmediate;
    private readonly ICallGateSubscriber<object> _onReady;
    private readonly ICallGateSubscriber<object> _onDispose;
    private string _lastImmediateData = string.Empty;
    private DateTime _lastImmediateTime = DateTime.MinValue;

    public IpcCallerBypassEmote(ILogger<IpcCallerBypassEmote> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SpheneMediator mediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _mediator = mediator;

        _apiVersion = pi.GetIpcSubscriber<(int, int)>("BypassEmote.ApiVersion");
        _isReady = pi.GetIpcSubscriber<bool>("BypassEmote.IsReady");
        _getStateForCharacter = pi.GetIpcSubscriber<nint, string>("BypassEmote.GetStateForCharacter");
        _setStateForCharacter = pi.GetIpcSubscriber<nint, string, object>("BypassEmote.SetStateForCharacter");
        _clearStateForCharacter = pi.GetIpcSubscriber<nint, object>("BypassEmote.ClearStateForCharacter");
        _onStateChange = pi.GetIpcSubscriber<string, object>("BypassEmote.OnStateChange");
        _onStateChangeImmediate = pi.GetIpcSubscriber<string, object>("BypassEmote.OnStateChangeImmediate");
        _onReady = pi.GetIpcSubscriber<object>("BypassEmote.OnReady");
        _onDispose = pi.GetIpcSubscriber<object>("BypassEmote.OnDispose");

        _onStateChange.Subscribe(OnStateChange);
        _onStateChangeImmediate.Subscribe(OnStateChangeImmediate);
        _onReady.Subscribe(OnReady);
        _onDispose.Subscribe(OnDispose);

        CheckAPI();
    }

    public bool APIAvailable { get; private set; }

    public void CheckAPI()
    {
        try
        {
            var version = _apiVersion.InvokeFunc();
            APIAvailable = version is { Item1: RequiredApiMajorVersion, Item2: >= RequiredApiMinorVersion } && _isReady.InvokeFunc();
        }
        catch
        {
            APIAvailable = false;
        }
    }

    public async Task<string> GetStateForCharacterAsync(nint characterAddress)
    {
        if (!APIAvailable || characterAddress == nint.Zero) return string.Empty;

        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() => _getStateForCharacter.InvokeFunc(characterAddress)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not obtain BypassEmote data");
        }

        return string.Empty;
    }

    public async Task SetStateForCharacterAsync(nint characterAddress, string data)
    {
        if (!APIAvailable || characterAddress == nint.Zero) return;

        try
        {
            await _dalamudUtil.RunOnFrameworkThread(() =>
            {
                if (string.IsNullOrEmpty(data))
                {
                    _clearStateForCharacter.InvokeAction(characterAddress);
                }
                else
                {
                    _setStateForCharacter.InvokeAction(characterAddress, data);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply BypassEmote data");
        }
    }

    private void OnStateChange(string data)
    {
        if (string.Equals(data, _lastImmediateData, StringComparison.Ordinal)
            && DateTime.UtcNow - _lastImmediateTime <= ImmediateDuplicateSuppressWindow)
        {
            return;
        }

        PublishState(data);
    }

    private void OnStateChangeImmediate(string data)
    {
        _lastImmediateData = data;
        _lastImmediateTime = DateTime.UtcNow;
        PublishState(data);
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
        PublishState(state);
    }

    public void Dispose()
    {
        _onStateChange.Unsubscribe(OnStateChange);
        _onStateChangeImmediate.Unsubscribe(OnStateChangeImmediate);
        _onReady.Unsubscribe(OnReady);
        _onDispose.Unsubscribe(OnDispose);
    }
}
