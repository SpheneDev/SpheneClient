using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;
using Sphene.Services.Mediator;
using System.Numerics;

namespace Sphene.Interop.Ipc;

/// <summary>
/// Optional IPC caller for the Lifestream plugin. Endpoint contract verified
/// against upstream <c>Lifestream/IPC/IPCProvider.cs</c> (EzIPC →
/// <c>Lifestream.&lt;Method&gt;</c>). Lifestream exports no version gate -
/// <see cref="CheckAPI"/> probes <c>IsBusy</c> and treats every other call as
/// able to throw. All Invoke calls may throw when the plugin is absent or
/// unloads mid-call; callers must catch.
/// </summary>
public sealed class IpcCallerLifestream : IIpcCaller
{
    private readonly ILogger<IpcCallerLifestream> _logger;

    private readonly ICallGateSubscriber<bool> _isBusy;
    private readonly ICallGateSubscriber<LifestreamAddressTuple, bool> _isHere;
    private readonly ICallGateSubscriber<LifestreamAddressTuple, bool> _isQuickTravelAvailable;
    private readonly ICallGateSubscriber<LifestreamAddressTuple, object?> _goToHousingAddress;
    private readonly ICallGateSubscriber<string, bool> _canVisitSameDc;
    private readonly ICallGateSubscriber<string, bool> _canVisitCrossDc;
    private readonly ICallGateSubscriber<(int Kind, int Ward, int Plot)?> _getCurrentPlotInfo;
    private readonly ICallGateSubscriber<uint, int, Vector3?> _getPlotEntrance;
    private readonly ICallGateSubscriber<object> _onHouseEnterError;

    public IpcCallerLifestream(ILogger<IpcCallerLifestream> logger,
        IDalamudPluginInterface dalamudPluginInterface)
    {
        _logger = logger;

        _isBusy = dalamudPluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _isHere = dalamudPluginInterface.GetIpcSubscriber<LifestreamAddressTuple, bool>(
            "Lifestream.IsHere");
        _isQuickTravelAvailable = dalamudPluginInterface
            .GetIpcSubscriber<LifestreamAddressTuple, bool>(
            "Lifestream.IsQuickTravelAvailable");
        _goToHousingAddress = dalamudPluginInterface
            .GetIpcSubscriber<LifestreamAddressTuple, object?>(
            "Lifestream.GoToHousingAddress");
        _canVisitSameDc = dalamudPluginInterface.GetIpcSubscriber<string, bool>(
            "Lifestream.CanVisitSameDC");
        _canVisitCrossDc = dalamudPluginInterface.GetIpcSubscriber<string, bool>(
            "Lifestream.CanVisitCrossDC");
        _getCurrentPlotInfo = dalamudPluginInterface
            .GetIpcSubscriber<(int Kind, int Ward, int Plot)?>(
            "Lifestream.GetCurrentPlotInfo");
        _getPlotEntrance = dalamudPluginInterface
            .GetIpcSubscriber<uint, int, Vector3?>("Lifestream.GetPlotEntrance");
        _onHouseEnterError = dalamudPluginInterface.GetIpcSubscriber<object>(
            "Lifestream.OnHouseEnterError");

        _onHouseEnterError.Subscribe(OnHouseEnterErrorReceived);
    }

    public bool APIAvailable { get; private set; }

    /// <summary>Raised when Lifestream reports a housing-entry failure while a
    /// travel request is in flight.</summary>
    public event Action? HouseEnterError;

    public void CheckAPI()
    {
        try
        {
            _isBusy.InvokeFunc();
            if (!APIAvailable)
            {
                _logger.LogInformation("Lifestream IPC is available");
            }
            APIAvailable = true;
        }
        catch (Exception ex)
        {
            if (APIAvailable)
            {
                _logger.LogWarning(ex, "Lifestream IPC became unavailable");
            }
            else
            {
                _logger.LogDebug("Lifestream IPC not available: {msg}", ex.Message);
            }
            APIAvailable = false;
        }
    }

    public bool IsBusy() => _isBusy.InvokeFunc();

    public bool IsHere(LifestreamAddressTuple address) => _isHere.InvokeFunc(address);

    public bool IsQuickTravelAvailable(LifestreamAddressTuple address)
        => _isQuickTravelAvailable.InvokeFunc(address);

    /// <summary>Requests travel. Lifestream silently drops invalid input -
    /// a clean return only means the IPC was invoked, not that travel started.</summary>
    public void GoToHousingAddress(LifestreamAddressTuple address)
        => _goToHousingAddress.InvokeAction(address);

    public bool CanVisitSameDataCenter(string world) => _canVisitSameDc.InvokeFunc(world);

    public bool CanVisitCrossDataCenter(string world) => _canVisitCrossDc.InvokeFunc(world);

    /// <summary>Lifestream's plot readout - <b>Ward and Plot are 0-based</b>
    /// here (unlike the address tuple). Null outside residential districts.</summary>
    public (int Kind, int Ward, int Plot)? GetCurrentPlotInfo() => _getCurrentPlotInfo.InvokeFunc();

    /// <summary>Entrance position for a 0-based plot in a housing territory -
    /// the basis for the PublicLocation distance check.</summary>
    public Vector3? GetPlotEntrance(uint territory, int plot0)
        => _getPlotEntrance.InvokeFunc(territory, plot0);

    public void Dispose()
    {
        _onHouseEnterError.Unsubscribe(OnHouseEnterErrorReceived);
    }

    private void OnHouseEnterErrorReceived()
        => HouseEnterError?.Invoke();
}
