using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using System.Numerics;

namespace Sphene.UI;

public class CitySyncshellExplanationUI : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private CitySyncshellExplanationRequestMessage? _currentRequest;
    private bool _enableCitySyncshells = true;

    public CitySyncshellExplanationUI(ILogger<CitySyncshellExplanationUI> logger, 
        SpheneMediator mediator, 
        PerformanceCollectorService performanceCollectorService,
        SpheneConfigService configService) 
        : base(logger, mediator, "City Syncshell Service###CitySyncshellExplanation", performanceCollectorService)
    {
        _configService = configService;
        
        Size = new Vector2(600, 450);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        
        _logger.LogDebug("CitySyncshellExplanationUI constructor called, subscribing to CitySyncshellExplanationRequestMessage");
        _logger.LogDebug("Mediator instance: {mediator}", Mediator?.GetType().Name ?? "null");
        Mediator.Subscribe<CitySyncshellExplanationRequestMessage>(this, OnExplanationRequest);
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

        // Header
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 0.8f, 1.0f, 1.0f));
        ImGui.Text($"Welcome to {_currentRequest.CityName} City Syncshell!");
        ImGui.PopStyleColor();
        
        ImGui.Separator();
        
        // Explanation text
        ImGui.TextWrapped("City Syncshells are public communication channels that allow players in the same city to chat and coordinate activities together.");
        
        ImGui.Spacing();
        ImGui.TextWrapped("Features of City Syncshells:");
        
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped("Connect with other players currently in the same city");
        
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped("Share information about events, hunts, and activities");
        
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped("Find groups for content or social activities");
        
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped("Automatically join when entering a city, leave when departing");
        
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.8f, 0.2f, 1.0f));
        ImGui.TextWrapped("Note: City Syncshells are public spaces. Please be respectful and follow community guidelines.");
        ImGui.PopStyleColor();
        
        ImGui.Separator();
        
        // Settings section
        ImGui.Text("Settings:");
        ImGui.Spacing();
        
        if (ImGui.Checkbox("Enable city syncshell join requests", ref _enableCitySyncshells))
        {
            _configService.Current.EnableCitySyncshellJoinRequests = _enableCitySyncshells;
            _configService.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When enabled, you will receive prompts to join city syncshells when entering major cities.\nYou can change this setting later in the Sphene settings.");
        }
        
        ImGui.Separator();
        
        // Buttons
        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
        
        // Join this time button
        if (ImGui.Button("Join This Time", new Vector2(buttonWidth, 0)))
        {
            HandleJoinThisTime();
        }
        
        ImGui.SameLine();
        
        // Close button
        if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
        {
            HandleClose();
        }
        
        ImGui.Spacing();
        ImGui.TextWrapped("This explanation will only be shown once. You can always change your city syncshell preferences in the Sphene settings under 'City Syncshell Settings'.");
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
        
        // Send response to join
        if (_currentRequest != null)
        {
            Mediator.Publish(new CitySyncshellExplanationResponseMessage(_currentRequest.CityName, true));
        }
        
        _currentRequest = null;
        IsOpen = false;
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

public record CitySyncshellExplanationRequestMessage(string CityName) : MessageBase;
public record CitySyncshellExplanationResponseMessage(string CityName, bool ShouldJoin) : MessageBase;