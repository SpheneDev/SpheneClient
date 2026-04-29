using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Sphene.Services;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sphene.Interop.Ipc;

public sealed class IpcCallerMoodles : IIpcCaller
{
    private const int MinimumApiVersion = 4;
    private readonly ICallGateSubscriber<int> _moodlesApiVersion;
    private readonly ICallGateSubscriber<nint, object> _moodlesOnChange;
    private readonly ICallGateSubscriber<nint, string> _moodlesGetStatus;
    private readonly ICallGateSubscriber<nint, string, object> _moodlesSetStatus;
    private readonly ICallGateSubscriber<nint, object> _moodlesRevertStatus;
    private readonly ILogger<IpcCallerMoodles> _logger;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SpheneMediator _spheneMediator;

    public IpcCallerMoodles(ILogger<IpcCallerMoodles> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SpheneMediator spheneMediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _spheneMediator = spheneMediator;

        _moodlesApiVersion = pi.GetIpcSubscriber<int>("Moodles.Version");
        _moodlesOnChange = pi.GetIpcSubscriber<nint, object>("Moodles.StatusManagerModified");
        _moodlesGetStatus = pi.GetIpcSubscriber<nint, string>("Moodles.GetStatusManagerByPtrV2");
        _moodlesSetStatus = pi.GetIpcSubscriber<nint, string, object>("Moodles.SetStatusManagerByPtrV2");
        _moodlesRevertStatus = pi.GetIpcSubscriber<nint, object>("Moodles.ClearStatusManagerByPtrV2");

        _moodlesOnChange.Subscribe(OnMoodlesChange);

        CheckAPI();
    }

    private void OnMoodlesChange(nint characterAddress)
    {
        _ = PublishMoodlesChangeAsync(characterAddress);
    }

    private async Task PublishMoodlesChangeAsync(nint characterAddress)
    {
        var data = await GetStatusAsync(characterAddress).ConfigureAwait(false);
        _spheneMediator.Publish(new MoodlesMessage(characterAddress, data));
    }

    public bool APIAvailable { get; private set; } = false;

    public void CheckAPI()
    {
        try
        {
            APIAvailable = _moodlesApiVersion.InvokeFunc() >= MinimumApiVersion;
        }
        catch
        {
            APIAvailable = false;
        }
    }

    public void Dispose()
    {
        _moodlesOnChange.Unsubscribe(OnMoodlesChange);
    }

    public async Task<string?> GetStatusAsync(nint address)
    {
        if (!APIAvailable) return null;

        try
        {
            return await _dalamudUtil.RunOnFrameworkThread(() => _moodlesGetStatus.InvokeFunc(address)).ConfigureAwait(false);

        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not Get Moodles Status");
            _spheneMediator.Publish(new DebugLogEventMessage(LogLevel.Warning, "IPC", "Moodles GetStatus failed", Details: e.ToString()));
            return null;
        }
    }

    public async Task SetStatusAsync(nint pointer, string status)
    {
        if (!APIAvailable) return;
        try
        {
            var updatedStatus = await UpdateApplierToLocalPlayerAsync(status).ConfigureAwait(false);
            await _dalamudUtil.RunOnFrameworkThread(() => _moodlesSetStatus.InvokeAction(pointer, updatedStatus)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not Set Moodles Status");
            _spheneMediator.Publish(new DebugLogEventMessage(LogLevel.Warning, "IPC", "Moodles SetStatus failed", Details: e.ToString()));
        }
    }

    private async Task<string> UpdateApplierToLocalPlayerAsync(string status)
    {
        try
        {
            var jsonNode = JsonNode.Parse(status);
            if (jsonNode is JsonObject jsonObject && jsonObject.ContainsKey("Applier"))
            {
                var localPlayer = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
                if (localPlayer != null)
                {
                    var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
                    var worldData = _dalamudUtil.WorldData.Value;
                    var worldName = worldData.TryGetValue((ushort)worldId, out var name) ? name : "Unknown";
                    var nameWithWorld = $"{localPlayer.Name.TextValue}@{worldName}";
                    jsonObject["Applier"] = nameWithWorld;
                    _logger.LogDebug("Updated Moodles Applier to local player: {applier}", nameWithWorld);
                }
            }
            return jsonNode?.ToJsonString() ?? status;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not update Moodles Applier field, using original status");
            return status;
        }
    }

    public async Task RevertStatusAsync(nint pointer)
    {
        if (!APIAvailable) return;
        try
        {
            await _dalamudUtil.RunOnFrameworkThread(() => _moodlesRevertStatus.InvokeAction(pointer)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not Revert Moodles Status");
            _spheneMediator.Publish(new DebugLogEventMessage(LogLevel.Warning, "IPC", "Moodles RevertStatus failed", Details: e.ToString()));
        }
    }
}
