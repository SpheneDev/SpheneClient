using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Sphene.API.Data.Extensions;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.PlayerData.Pairs;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI;

public class StatusDebugUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    
    private string _communicationLog = "Communication Log:\n";
    private bool _autoScroll = true;
    private Vector2 _logScrollPosition = Vector2.Zero;
    
    public StatusDebugUi(ILogger<StatusDebugUi> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, PairManager pairManager, ApiController apiController,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Sphene Status Debug###SpheneStatusDebug", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _apiController = apiController;
        
        IsOpen = false;
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(1200, 800)
        };
        
        Flags = ImGuiWindowFlags.NoCollapse;
        
        // Subscribe to communication events for logging
        Mediator.Subscribe<ConnectedMessage>(this, OnConnected);
        Mediator.Subscribe<DisconnectedMessage>(this, OnDisconnected);
    }
    
    protected override void DrawInternal()
    {
        DrawStatusSection();
        ImGui.Separator();
        DrawControlButtons();
        ImGui.Separator();
        DrawCommunicationLog();
    }
    
    private void DrawStatusSection()
    {
        ImGui.Text("Connection Status:");
        ImGui.SameLine();
        
        var isConnected = _apiController.IsConnected;
        if (isConnected)
        {
            UiSharedService.ColorText("Connected", ImGuiColors.HealerGreen);
        }
        else
        {
            UiSharedService.ColorText("Disconnected", ImGuiColors.DalamudRed);
        }
        
        ImGui.Text($"Server: {_apiController.ServerInfo.ShardName ?? "Not connected"}");
        ImGui.Text($"User ID: {_apiController.UID ?? "Not logged in"}");
        
        ImGui.Spacing();
        ImGui.Text("Paired Users:");
        
        var pairs = _pairManager.DirectPairs.ToList();
        if (pairs.Count == 0)
        {
            ImGui.Text("  No paired users");
        }
        else
        {
            foreach (var pair in pairs)
            {
                var ackYouStatus = pair.UserPair.OwnPermissions.IsAckYou() ? "✓" : "✗";
                var ackOtherStatus = pair.UserPair.OwnPermissions.IsAckOther() ? "✓" : "✗";
                
                ImGui.Text($"  {pair.UserData.AliasOrUID} - Status: {pair.IndividualPairStatus}");
                ImGui.SameLine();
                UiSharedService.ColorText($" [AckYou: {ackYouStatus}]", pair.UserPair.OwnPermissions.IsAckYou() ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
                ImGui.SameLine();
                UiSharedService.ColorText($" [AckOther: {ackOtherStatus}]", pair.UserPair.OwnPermissions.IsAckOther() ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
                
                // Add toggle button for AckYou only
                ImGui.SameLine();
                var currentAckYou = pair.UserPair.OwnPermissions.IsAckYou();
                var ackYouButtonText = currentAckYou ? "Disable My AckYou" : "Enable My AckYou";
                if (ImGui.Button($"{ackYouButtonText}##{pair.UserData.UID}"))
                {
                    var newAckYouValue = !currentAckYou;
                    LogCommunication($"[DEBUG] Setting my AckYou to {newAckYouValue} (this will update {pair.UserData.AliasOrUID}'s AckOther)");
                    _apiController.UserUpdateAckYou(newAckYouValue);
                }
            }
        }
    }
    
    private void DrawControlButtons()
    {
        ImGui.Text("Debug Controls:");
        
        if (ImGui.Button("Force Reconnect"))
        {
            LogCommunication("[DEBUG] Force reconnect triggered");
            // Trigger reconnection logic here if available
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Refresh Pairs"))
        {
            LogCommunication("[DEBUG] Pair refresh triggered");
            // Trigger pair refresh logic here if available
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Clear Log"))
        {
            _communicationLog = "Communication Log:\n";
        }
        
        ImGui.Spacing();
        
        var pairs = _pairManager.DirectPairs.ToList();
        if (pairs.Count > 0)
        {
            ImGui.Text("Pair Status Controls:");
            
            foreach (var pair in pairs)
            {
                ImGui.PushID(pair.UserData.UID);
                
                ImGui.Text($"{pair.UserData.AliasOrUID}:");
                ImGui.SameLine();
                
                if (ImGui.Button("Pause"))
                {
                    LogCommunication($"[DEBUG] Pausing pair: {pair.UserData.AliasOrUID}");
                    Mediator.Publish(new PauseMessage(pair.UserData));
                }
                
                ImGui.SameLine();
                if (ImGui.Button("Resume"))
                {
                    LogCommunication($"[DEBUG] Resuming pair: {pair.UserData.AliasOrUID}");
                    // Add resume logic here if available
                }
                
                ImGui.PopID();
            }
        }
    }
    
    private void DrawCommunicationLog()
    {
        ImGui.Text("Communication Log:");
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);
        
        var availableSize = ImGui.GetContentRegionAvail();
        availableSize.Y -= ImGui.GetStyle().ItemSpacing.Y;
        
        using var child = ImRaii.Child("CommunicationLog", availableSize, true);
        if (child)
        {
            ImGui.TextUnformatted(_communicationLog);
            
            if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            {
                ImGui.SetScrollHereY(1.0f);
            }
        }
    }
    
    private void LogCommunication(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _communicationLog += $"[{timestamp}] {message}\n";
    }
    
    private void OnConnected(ConnectedMessage message)
    {
        LogCommunication($"Connected to server: {message.Connection.ServerInfo.ShardName}");
    }
    
    private void OnDisconnected(DisconnectedMessage message)
    {
        LogCommunication("Disconnected from server");
    }
}