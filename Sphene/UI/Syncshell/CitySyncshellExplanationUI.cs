using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.Syncshell;

public class CitySyncshellExplanationUI : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private readonly AreaBoundSyncshellService _areaBoundSyncshellService;
    private CitySyncshellExplanationRequestMessage? _currentRequest;
    private bool _enableCitySyncshells = true;

    public CitySyncshellExplanationUI(ILogger<CitySyncshellExplanationUI> logger, 
        SpheneMediator mediator, 
        PerformanceCollectorService performanceCollectorService,
        SpheneConfigService configService,
        AreaBoundSyncshellService areaBoundSyncshellService) 
        : base(logger, mediator, "City Syncshell Service###CitySyncshellExplanation", performanceCollectorService)
    {
        _configService = configService;
        _areaBoundSyncshellService = areaBoundSyncshellService;
        
        Size = new Vector2(600, 450);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        
        _logger.LogDebug("CitySyncshellExplanationUI constructor called, subscribing to CitySyncshellExplanationRequestMessage");
        _logger.LogDebug("Mediator instance: {mediator}", Mediator?.GetType().Name ?? "null");
        if (Mediator != null)
        {
            Mediator.Subscribe<CitySyncshellExplanationRequestMessage>(this, OnExplanationRequest);
        }
        _logger.LogDebug("CitySyncshellExplanationUI subscription completed - subscribed to CitySyncshellExplanationRequestMessage");
    }

    public void ShowForCity(string cityName)
    {
        _logger.LogDebug("ShowForCity called for city: {cityName}", cityName);
        _currentRequest = new CitySyncshellExplanationRequestMessage(cityName);
        _enableCitySyncshells = _configService.Current.EnableCitySyncshellJoinRequests;
        IsOpen = true;
        _logger.LogInformation("City syncshell explanation UI opened for city: {cityName}, IsOpen: {isOpen}", cityName, IsOpen);
    }

    private void OnExplanationRequest(CitySyncshellExplanationRequestMessage message)
    {
        _logger.LogDebug("CitySyncshellExplanationUI.OnExplanationRequest called for city: {cityName}", message.CityName);
        _logger.LogInformation("Received CitySyncshellExplanationRequestMessage for city: {cityName}", message.CityName);
        _currentRequest = message;
        _enableCitySyncshells = _configService.Current.EnableCitySyncshellJoinRequests;
        IsOpen = true;
        _logger.LogInformation("City syncshell explanation UI opened for city: {cityName}, IsOpen: {isOpen}", message.CityName, IsOpen);
        _logger.LogDebug("CitySyncshellExplanationUI.OnExplanationRequest completed, IsOpen: {isOpen}, _currentRequest is null: {isNull}", IsOpen, _currentRequest == null);
    }

    protected override void DrawInternal()
    {
        _logger.LogDebug("CitySyncshellExplanationUI.DrawInternal called, _currentRequest is null: {isNull}, IsOpen: {isOpen}", _currentRequest == null, IsOpen);
        
        if (_currentRequest == null)
        {
            _logger.LogDebug("CitySyncshellExplanationUI.DrawInternal returning early - _currentRequest is null");
            return;
        }

        if (SpheneUI.Layout.BeginScrollArea("city-explanation", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawExplanationContent();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawExplanationContent()
    {
        // Header
        SpheneUI.Text.PageHeader($"Welcome to {_currentRequest!.CityName} City Syncshell!");

        // Explanation text
        SpheneUI.Text.Wrapped("City Syncshells are public communication channels that allow players in the same city to chat and coordinate activities together.");

        SpheneUI.Text.SectionHeader("Features");
        SpheneUI.Text.Bullet("Connect with other players currently in the same city");
        SpheneUI.Text.Bullet("Share information about events, hunts, and activities");
        SpheneUI.Text.Bullet("Find groups for content or social activities");
        SpheneUI.Text.Bullet("Automatically join when entering a city, leave when departing");

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Layout.Callout("City Syncshells are public spaces. Please be respectful and follow community guidelines.", Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);

        SpheneUI.Layout.Section("Settings", () =>
        {
            if (SpheneUI.Controls.Toggle("Enable city syncshell join requests", ref _enableCitySyncshells))
            {
                _configService.Current.EnableCitySyncshellJoinRequests = _enableCitySyncshells;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("When enabled, you will receive prompts to join city syncshells when entering major cities. You can change this setting later in the Sphene settings.");
        });

        // Buttons
        var clicked = SpheneUI.Controls.DialogButtons("citySyncshellActions", "Join This Time", "Close");
        if (clicked == 1)
        {
            HandleJoinThisTime();
        }
        else if (clicked == 2)
        {
            HandleClose();
        }

        SpheneUI.Text.MutedWrapped("This explanation will only be shown once. You can always change your city syncshell preferences in the Sphene settings under 'City Syncshell Settings'.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Mediator.UnsubscribeAll(this);
        }
        base.Dispose(disposing);
    }

    private void HandleJoinThisTime()
    {
        _logger.LogDebug("User chose to join city syncshell this time for city: {cityName}", _currentRequest?.CityName);
        
        // Mark as seen
        _configService.Current.HasSeenCitySyncshellExplanation = true;
        _configService.Save();
        
        // Close the explanation window
        _currentRequest = null;
        IsOpen = false;
        _areaBoundSyncshellService.TriggerAreaSyncshellSelection();
    }

    private void HandleClose()
    {
        _logger.LogDebug("User closed city syncshell explanation for city: {cityName}", _currentRequest?.CityName);
        
        // Mark as seen
        _configService.Current.HasSeenCitySyncshellExplanation = true;
        _configService.Save();
        
        // Send response to not join
        if (_currentRequest != null)
        {
            Mediator.Publish(new CitySyncshellExplanationResponseMessage(_currentRequest.CityName, false));
        }
        
        _currentRequest = null;
        IsOpen = false;
    }
}
