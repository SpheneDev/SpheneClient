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
    private readonly ILogger<StatusDebugUi> _logger;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly UpdateCheckService _updateCheckService;
    
    private string _communicationLog = "Communication Log:\n";
    private bool _autoScroll = true;
    private Vector2 _logScrollPosition = Vector2.Zero;
    
    // Update testing fields
    private string _testCurrentVersion = "1.0.0.0";
    private string _testDalamudVersion = "1.0.0.0";
    private bool _isTestingUpdate = false;
    private string _lastTestResult = "";
    
    public StatusDebugUi(ILogger<StatusDebugUi> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, PairManager pairManager, ApiController apiController,
        UpdateCheckService updateCheckService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Sphene Status Debug###SpheneStatusDebug", performanceCollectorService)
    {
        _logger = logger;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _apiController = apiController;
        _updateCheckService = updateCheckService;
        
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
        DrawUpdateTestingSection();
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
                var myAckYouStatus = pair.UserPair.OwnPermissions.IsAckYou() ? "✓" : "✗";
                var partnerAckYouStatus = pair.UserPair.OtherPermissions.IsAckYou() ? "✓" : "✗";
                
                ImGui.Text($"  {pair.UserData.AliasOrUID} - Status: {pair.IndividualPairStatus}");
                ImGui.SameLine();
                UiSharedService.ColorText($" [My AckYou: {myAckYouStatus}]", pair.UserPair.OwnPermissions.IsAckYou() ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
                ImGui.SameLine();
                UiSharedService.ColorText($" [Partner AckYou: {partnerAckYouStatus}]", pair.UserPair.OtherPermissions.IsAckYou() ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
                
                // Add toggle button for AckYou only
                ImGui.SameLine();
                var currentAckYou = pair.UserPair.OwnPermissions.IsAckYou();
                var ackYouButtonText = currentAckYou ? "Disable My AckYou" : "Enable My AckYou";
                if (ImGui.Button($"{ackYouButtonText}##{pair.UserData.UID}"))
                {
                    var newAckYouValue = !currentAckYou;
                    LogCommunication($"[DEBUG] Setting my AckYou to {newAckYouValue} for {pair.UserData.AliasOrUID} only");
                    
                    // Use pair-specific permission update instead of global update
                    var permissions = pair.UserPair.OwnPermissions;
                    permissions.SetAckYou(newAckYouValue);
                    _ = _apiController.UserSetPairPermissions(new(pair.UserData, permissions));
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
    
    private void DrawUpdateTestingSection()
    {
        ImGui.Text("Update Testing:");
        ImGui.Spacing();
        
        // Current version input
        ImGui.Text("Test Current Version:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.InputText("##testCurrentVersion", ref _testCurrentVersion, 20);
        
        // Dalamud version input
        ImGui.Text("Test Dalamud Version:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.InputText("##testDalamudVersion", ref _testDalamudVersion, 20);
        
        ImGui.Spacing();
        
        // Test buttons
        if (ImGui.Button("Test Update Check") && !_isTestingUpdate)
        {
            _ = TestUpdateCheckAsync();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Test with Current = Dalamud") && !_isTestingUpdate)
        {
            _testDalamudVersion = _testCurrentVersion;
            _ = TestUpdateCheckAsync();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Test Current < Dalamud") && !_isTestingUpdate)
        {
            if (Version.TryParse(_testCurrentVersion, out var currentVer))
            {
                var newVersion = new Version(currentVer.Major, currentVer.Minor, currentVer.Build, currentVer.Revision + 1);
                _testDalamudVersion = newVersion.ToString();
                _ = TestUpdateCheckAsync();
            }
        }
        
        if (_isTestingUpdate)
        {
            ImGui.Text("Testing in progress...");
        }
        
        if (!string.IsNullOrEmpty(_lastTestResult))
        {
            ImGui.Spacing();
            ImGui.Text("Last Test Result:");
            ImGui.TextWrapped(_lastTestResult);
        }
        
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Use these controls to simulate different version scenarios:");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "- Current = Dalamud: No update notification (Dalamud already has it)");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "- Current < Dalamud: Update notification shown (Dalamud has newer version)");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "- Current > Dalamud: No update notification (Dalamud doesn't have it yet)");
    }
    
    private async Task TestUpdateCheckAsync()
    {
        _isTestingUpdate = true;
        _lastTestResult = "";
        
        try
        {
            Version? testCurrentVersion = null;
            Version? testDalamudVersion = null;
            
            if (Version.TryParse(_testCurrentVersion, out var currentVer))
                testCurrentVersion = currentVer;
            else
                _lastTestResult += "Invalid current version format. ";
                
            if (Version.TryParse(_testDalamudVersion, out var dalamudVer))
                testDalamudVersion = dalamudVer;
            else
                _lastTestResult += "Invalid Dalamud version format. ";
            
            if (testCurrentVersion != null && testDalamudVersion != null)
            {
                var result = await _updateCheckService.TestUpdateCheckAsync(testCurrentVersion, testDalamudVersion);
                
                if (result != null)
                {
                    _lastTestResult = $"Test completed successfully!\n" +
                                    $"Current: {result.CurrentVersion}\n" +
                                    $"Latest: {result.LatestVersion}\n" +
                                    $"Update Available: {result.IsUpdateAvailable}\n" +
                                    $"Would show notification: {result.IsUpdateAvailable}";
                }
                else
                {
                    _lastTestResult = "Test failed - no result returned (check logs for details)";
                }
            }
            else
            {
                _lastTestResult += "Please provide valid version numbers in format: Major.Minor.Build.Revision (e.g., 1.0.0.0)";
            }
        }
        catch (Exception ex)
        {
            _lastTestResult = $"Test failed with exception: {ex.Message}";
            _logger.LogError(ex, "Update test failed");
        }
        finally
        {
            _isTestingUpdate = false;
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