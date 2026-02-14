using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Sphene.API.Data.Extensions;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.Events;
using Sphene.PlayerData.Pairs;
using Sphene.API.Data;
using Sphene.WebAPI;
using Sphene.WebAPI.SignalR.Utils;
using System.Numerics;
using System.Text.Json;

namespace Sphene.UI.Panels;

public class StatusDebugUi : WindowMediatorSubscriberBase
{
    private new readonly ILogger<StatusDebugUi> _logger;
    
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly EnhancedAcknowledgmentManager? _acknowledgmentManager;
    private readonly SessionAcknowledgmentManager? _sessionAcknowledgmentManager;
    
    private string _communicationLog = "Communication Log:\n";
    private bool _autoScroll = true;
    private bool _showHealthChecks = true;
    private bool _showAcknowledgments = true;
    private bool _showCircuitBreaker = true;
    private bool _showConnections = true;
    private string? _selectedCharacterDebugUid;
    private string? _selectedCharacterStatsUid;
    private readonly Dictionary<string, CharacterStatsSnapshot> _characterStats = new(StringComparer.Ordinal);
    
    public StatusDebugUi(ILogger<StatusDebugUi> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, PairManager pairManager, ApiController apiController,
        ConnectionHealthMonitor healthMonitor, CircuitBreakerService circuitBreaker,
        PerformanceCollectorService performanceCollectorService,
        EnhancedAcknowledgmentManager? acknowledgmentManager = null,
        SessionAcknowledgmentManager? sessionAcknowledgmentManager = null)
        : base(logger, mediator, "Sphene Status Debug###SpheneStatusDebug", performanceCollectorService)
    {
        _logger = logger;
        _pairManager = pairManager;
        _apiController = apiController;
        _healthMonitor = healthMonitor;
        _circuitBreaker = circuitBreaker;
        _acknowledgmentManager = acknowledgmentManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        
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
        
        // Subscribe to acknowledgment events
        Mediator.Subscribe<AcknowledgmentStatusChangedMessage>(this, OnAcknowledgmentStatusChanged);
        Mediator.Subscribe<AcknowledgmentReceivedMessage>(this, OnAcknowledgmentReceived);
        Mediator.Subscribe<AcknowledgmentTimeoutMessage>(this, OnAcknowledgmentTimeout);
        Mediator.Subscribe<AcknowledgmentBatchCompletedMessage>(this, OnAcknowledgmentBatchCompleted);
        
        // Subscribe to notification events
        Mediator.Subscribe<NotificationMessage>(this, OnNotification);
        
        // Subscribe to health monitor events by monitoring the health status changes
        // We'll use a timer to periodically check and log health status changes
        _healthStatusTimer = new Timer(CheckHealthStatus, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        
        LogCommunication("Status Debug UI initialized", "INFO");
    }
    
    private readonly Timer _healthStatusTimer;
    private bool _lastHealthyState = true;
    private CircuitBreakerState _lastCircuitBreakerState = CircuitBreakerState.Closed;
    private int _lastConsecutiveFailures = 0;
    
    private void CheckHealthStatus(object? state)
    {
        try
        {
            var healthStatus = _healthMonitor.GetHealthStatus();
            var circuitStatus = _circuitBreaker.GetStatus();
            
            // Log health status changes
            if (healthStatus.IsHealthy != _lastHealthyState)
            {
                var statusText = healthStatus.IsHealthy ? "HEALTHY" : "UNHEALTHY";
                LogCommunication($"Health status changed to {statusText}", "HEALTH");
                _lastHealthyState = healthStatus.IsHealthy;
            }
            
            // Log circuit breaker state changes
            if (circuitStatus.State != _lastCircuitBreakerState)
            {
                LogCommunication($"Circuit breaker state changed from {_lastCircuitBreakerState} to {circuitStatus.State}", "CIRCUIT");
                _lastCircuitBreakerState = circuitStatus.State;
            }
            
            // Log consecutive failure count changes
            if (healthStatus.ConsecutiveFailures != _lastConsecutiveFailures && healthStatus.ConsecutiveFailures > 0)
            {
                LogCommunication($"Consecutive failures: {healthStatus.ConsecutiveFailures}", "HEALTH");
                _lastConsecutiveFailures = healthStatus.ConsecutiveFailures;
            }
            else if (healthStatus.ConsecutiveFailures == 0 && _lastConsecutiveFailures > 0)
            {
                LogCommunication("Consecutive failures reset to 0", "HEALTH");
                _lastConsecutiveFailures = 0;
            }
            
            // Log periodic health check status (only if enabled in settings)
            if (_showHealthChecks && healthStatus.IsHealthy)
            {
                var timeSinceLastSuccess = healthStatus.TimeSinceLastSuccess.TotalSeconds;
                LogCommunication($"Health check OK (last success: {timeSinceLastSuccess:F0}s ago)", "HEALTH");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking health status for logging");
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _healthStatusTimer.Dispose();
        }
        base.Dispose(disposing);
    }
    
    protected override void DrawInternal()
    {
        // Tab bar for different sections
        using var tabBar = ImRaii.TabBar("StatusDebugTabs");
        if (!tabBar) return;

        // Overview Tab
        using (var overviewTab = ImRaii.TabItem("Overview"))
        {
            if (overviewTab)
            {
                DrawStatusSection();
                ImGui.Separator();
                DrawHealthMonitoringSection();
                ImGui.Separator();
                DrawControlButtons();
            }
        }

        // Acknowledgment System Tab
        using (var ackTab = ImRaii.TabItem("Acknowledgments"))
        {
            if (ackTab)
            {
                DrawAcknowledgmentTable();
            }
        }

        // Communication Log Tab
        using (var logTab = ImRaii.TabItem("Communication Log"))
        {
            if (logTab)
            {
                DrawCommunicationLog();
            }
        }

        using (var characterLogTab = ImRaii.TabItem("Character Debug Logs"))
        {
            if (characterLogTab)
            {
                DrawCharacterDebugLogs();
            }
        }

        using (var characterStatsTab = ImRaii.TabItem("Character Statistics"))
        {
            if (characterStatsTab)
            {
                DrawCharacterStatistics();
            }
        }
        
        // Draw popup outside of tabs so it persists when switching tabs
        if (_showLogPopup)
        {
            DrawLogPopup();
        }
    }
    
    private void DrawAcknowledgmentTable()
    {
        if (_acknowledgmentManager == null || _sessionAcknowledgmentManager == null)
        {
            ImGui.Text("Acknowledgment system not available");
            return;
        }
        
        ImGui.Text("Acknowledgment System Status");
        ImGui.Separator();
        
        // Get metrics and configuration
        var metrics = _acknowledgmentManager.GetMetrics();
        var config = _acknowledgmentManager.GetConfiguration();
        var pendingAcks = _sessionAcknowledgmentManager.GetPendingAcknowledgments();
        
        // Metrics overview
        ImGui.Text("Metrics Overview:");
        ImGui.Columns(4, "AckMetrics", true);
        
        ImGui.Text("Total Sent");
        ImGui.NextColumn();
        ImGui.Text("Success Rate");
        ImGui.NextColumn();
        ImGui.Text("Avg Response");
        ImGui.NextColumn();
        ImGui.Text("Pending");
        ImGui.NextColumn();
        
        ImGui.Separator();
        
        ImGui.Text($"{metrics.TotalSent}");
        ImGui.NextColumn();
        UiSharedService.ColorText($"{metrics.SuccessRate:F1}%", 
            metrics.SuccessRate > 90 ? ImGuiColors.HealerGreen : 
            metrics.SuccessRate > 70 ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudRed);
        ImGui.NextColumn();
        ImGui.Text($"{metrics.AverageResponseTimeMs:F0}ms");
        ImGui.NextColumn();
        ImGui.Text($"{pendingAcks.Count}");
        ImGui.NextColumn();
        
        ImGui.Columns(1);
        ImGui.Spacing();
        
        // Configuration settings
        if (ImGui.CollapsingHeader("Configuration"))
        {
            ImGui.Text($"Max Batch Size: {config.MaxBatchSize}");
            ImGui.Text($"Batch Timeout: {config.BatchTimeoutMs}ms");
            ImGui.Text($"Max Retries: {config.MaxRetryAttempts}");
            ImGui.Text($"Default Timeout: {config.DefaultTimeoutSeconds}s");
            ImGui.Text($"Batching Enabled: {(config.EnableBatching ? "Yes" : "No")}");
            ImGui.Text($"Auto Retry Enabled: {(config.EnableAutoRetry ? "Yes" : "No")}");
        }
        
        // Pending acknowledgments table
        if (ImGui.CollapsingHeader("Pending Acknowledgments"))
        {
            if (pendingAcks.Count == 0)
            {
                ImGui.Text("No pending acknowledgments");
            }
            else
            {
                ImGui.Columns(3, "PendingAcks", true);
                
                ImGui.Text("User");
                ImGui.NextColumn();
                ImGui.Text("Acknowledgment ID");
                ImGui.NextColumn();
                ImGui.Text("Actions");
                ImGui.NextColumn();
                
                ImGui.Separator();
                
                foreach (var kvp in pendingAcks)
                {
                    var userKey = kvp.Key;
                    var ackId = kvp.Value;
                    
                    ImGui.Text(userKey);
                    ImGui.NextColumn();
                    ImGui.Text(ackId.Length > 16 ? $"{ackId[..16]}..." : ackId);
                    ImGui.NextColumn();
                    
                    ImGui.PushID($"clear_{userKey}");
                    if (ImGui.Button("Clear"))
                    {
                        var user = new UserData(userKey);
                        var removed = _sessionAcknowledgmentManager.RemovePendingAcknowledgment(user, ackId);
                        if (removed)
                        {
                            LogCommunication($"[DEBUG] Manually cleared acknowledgment for {userKey}", "ACK");
                        }
                        else
                        {
                            LogCommunication($"[DEBUG] Clear failed for {userKey} (no match)", "ACK");
                        }
                    }
                    ImGui.PopID();
                    
                    ImGui.SameLine();
                    ImGui.PushID($"timeout_{userKey}");
                    if (ImGui.Button("Mark Timeout"))
                    {
                        _sessionAcknowledgmentManager.ProcessTimeoutAcknowledgment(ackId);
                        LogCommunication($"[DEBUG] Marked acknowledgment as timeout for {userKey}", "ACK");
                    }
                    ImGui.PopID();
                    
                    ImGui.NextColumn();
                }
                
                ImGui.Columns(1);
            }
        }
        
        // Error statistics
        if (ImGui.CollapsingHeader("Error Statistics"))
        {
            if (metrics.ErrorCounts.Count == 0)
            {
                ImGui.Text("No errors recorded");
            }
            else
            {
                ImGui.Columns(2, "ErrorStats", true);
                
                ImGui.Text("Error Type");
                ImGui.NextColumn();
                ImGui.Text("Count");
                ImGui.NextColumn();
                
                ImGui.Separator();
                
                foreach (var error in metrics.ErrorCounts)
                {
                    ImGui.Text(error.Key.ToString());
                    ImGui.NextColumn();
                    UiSharedService.ColorText($"{error.Value}", ImGuiColors.DalamudRed);
                    ImGui.NextColumn();
                }
                
                ImGui.Columns(1);
            }
        }
        
        // Priority statistics
        if (ImGui.CollapsingHeader("Priority Statistics"))
        {
            if (metrics.PriorityCounts.Count == 0)
            {
                ImGui.Text("No priority data available");
            }
            else
            {
                ImGui.Columns(2, "PriorityStats", true);
                
                ImGui.Text("Priority");
                ImGui.NextColumn();
                ImGui.Text("Count");
                ImGui.NextColumn();
                
                ImGui.Separator();
                
                foreach (var priority in metrics.PriorityCounts)
                {
                    var color = priority.Key switch
                    {
                        AcknowledgmentPriority.High => ImGuiColors.DalamudRed,
                        AcknowledgmentPriority.Medium => ImGuiColors.DalamudYellow,
                        AcknowledgmentPriority.Low => ImGuiColors.HealerGreen,
                        _ => ImGuiColors.DalamudWhite
                    };
                    
                    UiSharedService.ColorText(priority.Key.ToString(), color);
                    ImGui.NextColumn();
                    ImGui.Text($"{priority.Value}");
                    ImGui.NextColumn();
                }
                
                ImGui.Columns(1);
            }
        }
        
        // Paired Users table
        if (ImGui.CollapsingHeader("Paired Users"))
        {
            var pairs = _pairManager.DirectPairs.ToList();
            if (pairs.Count == 0)
            {
                ImGui.Text("No paired users");
            }
            else
            {
                if (ImGui.BeginTable("PairedUsersTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
                {
                    ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 150);
                    ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("My AckYou", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("Partner AckYou", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();
                    
                    foreach (var pair in pairs)
                    {
                        ImGui.TableNextRow();
                        
                        // User column
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text(pair.UserData.AliasOrUID);
                        
                        // Status column
                        ImGui.TableSetColumnIndex(1);
                        var statusColor = pair.IndividualPairStatus switch
                        {
                            API.Data.Enum.IndividualPairStatus.Bidirectional => ImGuiColors.HealerGreen,
                            API.Data.Enum.IndividualPairStatus.OneSided => ImGuiColors.DalamudYellow,
                            _ => ImGuiColors.DalamudRed
                        };
                        UiSharedService.ColorText(pair.IndividualPairStatus.ToString(), statusColor);
                        
                        // My AckYou column
                        ImGui.TableSetColumnIndex(2);
                        var myAckYouStatus = pair.UserPair.OwnPermissions.IsAckYou() ? "✓" : "✗";
                        var myAckYouColor = pair.UserPair.OwnPermissions.IsAckYou() ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed;
                        UiSharedService.ColorText(myAckYouStatus, myAckYouColor);
                        
                        // Partner AckYou column
                        ImGui.TableSetColumnIndex(3);
                        var partnerAckYouStatus = pair.UserPair.OtherPermissions.IsAckYou() ? "✓" : "✗";
                        var partnerAckYouColor = pair.UserPair.OtherPermissions.IsAckYou() ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed;
                        UiSharedService.ColorText(partnerAckYouStatus, partnerAckYouColor);
                        
                        // Actions column
                        ImGui.TableSetColumnIndex(4);
                        var currentAckYou = pair.UserPair.OwnPermissions.IsAckYou();
                        var ackYouButtonText = currentAckYou ? "Disable" : "Enable";
                        if (ImGui.Button($"{ackYouButtonText}##{pair.UserData.UID}"))
                        {
                            var newAckYouValue = !currentAckYou;
                            LogCommunication($"[DEBUG] Setting my AckYou to {newAckYouValue} for {pair.UserData.AliasOrUID}");
                            
                            var permissions = pair.UserPair.OwnPermissions;
                            permissions.SetAckYou(newAckYouValue);
                            _ = _apiController.UserSetPairPermissions(new(pair.UserData, permissions));
                        }
                    }
                    
                    ImGui.EndTable();
                }
            }
        }
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
    }
    
    private void DrawHealthMonitoringSection()
    {
        ImGui.Text("Connection Health Monitoring:");
        
        var healthStatus = _healthMonitor.GetHealthStatus();
        var circuitStatus = _circuitBreaker.GetStatus();
        
        // Health Monitor Status
        ImGui.Text("Health Status:");
        ImGui.SameLine();
        if (healthStatus.IsHealthy)
        {
            UiSharedService.ColorText("Healthy", ImGuiColors.HealerGreen);
        }
        else
        {
            UiSharedService.ColorText("Unhealthy", ImGuiColors.DalamudRed);
        }
        
        ImGui.Text($"Consecutive Failures: {healthStatus.ConsecutiveFailures}");
        ImGui.Text($"Time Since Last Success: {healthStatus.TimeSinceLastSuccess:hh\\:mm\\:ss}");
        ImGui.Text($"Last Health Check: {healthStatus.LastHealthCheck:HH:mm:ss}");
        
        ImGui.Spacing();
        
        // Circuit Breaker Status
        ImGui.Text("Circuit Breaker:");
        ImGui.SameLine();
        var stateColor = circuitStatus.State switch
        {
            CircuitBreakerState.Closed => ImGuiColors.HealerGreen,
            CircuitBreakerState.HalfOpen => ImGuiColors.DalamudYellow,
            CircuitBreakerState.Open => ImGuiColors.DalamudRed,
            _ => ImGuiColors.DalamudWhite
        };
        UiSharedService.ColorText(circuitStatus.State.ToString(), stateColor);
        
        ImGui.Text($"Failure Count: {circuitStatus.FailureCount}");
        if (circuitStatus.LastFailureTime != DateTime.MinValue)
        {
            ImGui.Text($"Last Failure: {circuitStatus.LastFailureTime:HH:mm:ss}");
        }
        
        if (circuitStatus.State == CircuitBreakerState.HalfOpen)
        {
            ImGui.Text($"Half-Open Attempts: {circuitStatus.HalfOpenAttempts}");
        }
        
        ImGui.Spacing();
        
        // Reset buttons
        if (ImGui.Button("Reset Circuit Breaker"))
        {
            _circuitBreaker.Reset();
            LogCommunication("[DEBUG] Circuit breaker manually reset");
        }
    }

    private void DrawCharacterDebugLogs()
    {
        ImGui.Text("Character Debug Logs");

        var pairs = _pairManager.DirectPairs.ToList();
        if (pairs.Count == 0)
        {
            ImGui.Text("No paired users");
            return;
        }

        if (string.IsNullOrEmpty(_selectedCharacterDebugUid) || pairs.All(p => !string.Equals(p.UserData.UID, _selectedCharacterDebugUid, StringComparison.Ordinal)))
        {
            _selectedCharacterDebugUid = pairs[0].UserData.UID;
        }

        if (ImGui.BeginTable("CharacterDebugLogTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn("UID", ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn("Visibility", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var pair in pairs)
            {
                var uid = pair.UserData.UID;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(pair.UserData.AliasOrUID);

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(uid);

                ImGui.TableSetColumnIndex(2);
                var visibilityText = pair.IsVisible ? "Visible" : "Hidden";
                var visibilityColor = pair.IsVisible ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed;
                UiSharedService.ColorText(visibilityText, visibilityColor);

                ImGui.TableSetColumnIndex(3);
                var isSelected = string.Equals(uid, _selectedCharacterDebugUid, StringComparison.Ordinal);
                var selectText = isSelected ? "Selected" : "Select";
                if (ImGui.Button($"{selectText}##select_{uid}"))
                {
                    _selectedCharacterDebugUid = uid;
                }

                ImGui.SameLine();
                if (ImGui.Button($"Copy##copy_{uid}"))
                {
                    ImGui.SetClipboardText(GetApplyDebugText(pair));
                }

                ImGui.SameLine();
                if (ImGui.Button($"Clear##clear_{uid}"))
                {
                    pair.ClearApplyDebug();
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();

        Pair? selectedPair = null;
        foreach (var pair in pairs)
        {
            if (string.Equals(pair.UserData.UID, _selectedCharacterDebugUid, StringComparison.Ordinal))
            {
                selectedPair = pair;
                break;
            }
        }

        if (selectedPair == null)
        {
            ImGui.Text("No character selected");
            return;
        }

        ImGui.Text($"Selected: {selectedPair.UserData.AliasOrUID} ({selectedPair.UserData.UID})");

        var logText = GetApplyDebugText(selectedPair);
        var availableSize = ImGui.GetContentRegionAvail();
        using var child = ImRaii.Child("CharacterDebugLogContent", availableSize, true);
        if (child)
        {
            ImGui.TextUnformatted(logText);
        }
    }

    private static string GetApplyDebugText(Pair pair)
    {
        var lines = pair.GetApplyDebugLines();
        if (lines.Length == 0)
        {
            return "No debug logs for this character.";
        }

        return string.Join('\n', lines);
    }

    private void DrawCharacterStatistics()
    {
        ImGui.Text("Character Statistics");

        var pairs = _pairManager.DirectPairs.ToList();
        if (pairs.Count == 0)
        {
            ImGui.Text("No paired users");
            return;
        }

        if (string.IsNullOrEmpty(_selectedCharacterStatsUid) || pairs.All(p => !string.Equals(p.UserData.UID, _selectedCharacterStatsUid, StringComparison.Ordinal)))
        {
            _selectedCharacterStatsUid = pairs[0].UserData.UID;
        }

        if (ImGui.BeginTable("CharacterStatsTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn("Current Hash", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Last Hash", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Last Change", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Last Action", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            foreach (var pair in pairs)
            {
                var snapshot = GetCharacterStatsSnapshot(pair);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(pair.UserData.AliasOrUID);

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(snapshot.Current.DataHash);

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(snapshot.Previous?.DataHash ?? "-");

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(snapshot.Previous?.SnapshotTime.ToLocalTime().ToString("HH:mm:ss") ?? "-");

                ImGui.TableSetColumnIndex(4);
                ImGui.Text(snapshot.Current.LastAction);

                ImGui.TableSetColumnIndex(5);
                var uid = pair.UserData.UID;
                var isSelected = string.Equals(uid, _selectedCharacterStatsUid, StringComparison.Ordinal);
                var selectText = isSelected ? "Selected" : "Select";
                if (ImGui.Button($"{selectText}##stats_select_{uid}"))
                {
                    _selectedCharacterStatsUid = uid;
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();

        Pair? selectedPair = null;
        foreach (var pair in pairs)
        {
            if (string.Equals(pair.UserData.UID, _selectedCharacterStatsUid, StringComparison.Ordinal))
            {
                selectedPair = pair;
                break;
            }
        }

        if (selectedPair == null)
        {
            ImGui.Text("No character selected");
            return;
        }

        var selectedSnapshot = GetCharacterStatsSnapshot(selectedPair);
        ImGui.Text($"Selected: {selectedPair.UserData.AliasOrUID} ({selectedPair.UserData.UID})");

        if (ImGui.BeginTable("CharacterStatsDetailTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 240);
            ImGui.TableSetupColumn("Last", ImGuiTableColumnFlags.WidthFixed, 240);
            ImGui.TableHeadersRow();

            DrawStatsRow("Data Hash", selectedSnapshot.Current.DataHash, selectedSnapshot.Previous?.DataHash);
            DrawStatsRow("Applied Bytes", selectedSnapshot.Current.AppliedBytes.ToString(), selectedSnapshot.Previous?.AppliedBytes.ToString());
            DrawStatsRow("Applied Triangles", selectedSnapshot.Current.AppliedTris.ToString(), selectedSnapshot.Previous?.AppliedTris.ToString());
            DrawStatsRow("Applied VRAM Bytes", selectedSnapshot.Current.AppliedVramBytes.ToString(), selectedSnapshot.Previous?.AppliedVramBytes.ToString());
            DrawStatsRow("Visible", selectedSnapshot.Current.IsVisible ? "Yes" : "No", selectedSnapshot.Previous != null ? (selectedSnapshot.Previous.IsVisible ? "Yes" : "No") : null);
            DrawStatsRow("Mutually Visible", selectedSnapshot.Current.IsMutuallyVisible ? "Yes" : "No", selectedSnapshot.Previous != null ? (selectedSnapshot.Previous.IsMutuallyVisible ? "Yes" : "No") : null);
            DrawStatsRow("Paused", selectedSnapshot.Current.IsPaused ? "Yes" : "No", selectedSnapshot.Previous != null ? (selectedSnapshot.Previous.IsPaused ? "Yes" : "No") : null);
            DrawStatsRow("Last Ack Success", FormatNullableBool(selectedSnapshot.Current.LastAckSuccess), FormatNullableBool(selectedSnapshot.Previous?.LastAckSuccess));
            DrawStatsRow("Last Ack Time", FormatNullableTime(selectedSnapshot.Current.LastAckTime), FormatNullableTime(selectedSnapshot.Previous?.LastAckTime));
            DrawStatsRow("Last Ack Id", selectedSnapshot.Current.LastAckId ?? "-", selectedSnapshot.Previous?.LastAckId);
            DrawStatsRow("Retry Count", selectedSnapshot.Current.ApplyRetryCount.ToString(), selectedSnapshot.Previous?.ApplyRetryCount.ToString());
            DrawStatsRow("Snapshot Time", selectedSnapshot.Current.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"), selectedSnapshot.Previous?.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"));

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.Text("Received Created Character Data");

        foreach (var pair in pairs)
        {
            var uid = pair.UserData.UID;
            var label = $"{pair.UserData.AliasOrUID} ({uid})##received_character_data_{uid}";
            if (ImGui.TreeNode(label))
            {
                var receivedData = pair.LastReceivedCharacterData;
                var receivedDataJson = receivedData != null
                    ? JsonSerializer.Serialize(receivedData, new JsonSerializerOptions() { WriteIndented = true })
                    : "No received character data.";
                var lastReceivedHash = pair.LastReceivedCharacterDataHash ?? "-";
                var previousReceivedHash = pair.PreviousReceivedCharacterDataHash ?? "-";
                var lastReceivedTime = pair.LastReceivedCharacterDataTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                var lastChangeTime = pair.LastReceivedCharacterDataChangeTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

                ImGui.TextUnformatted($"Last Received Hash: {lastReceivedHash}");
                ImGui.TextUnformatted($"Previous Received Hash: {previousReceivedHash}");
                ImGui.TextUnformatted($"Last Received Time: {lastReceivedTime}");
                ImGui.TextUnformatted($"Last Change Time: {lastChangeTime}");
                ImGui.Separator();

                if (ImGui.Button($"Copy##received_character_data_copy_{uid}"))
                {
                    if (receivedData != null)
                    {
                        ImGui.SetClipboardText(receivedDataJson);
                    }
                    else
                    {
                        ImGui.SetClipboardText("ERROR: No received character data, cannot copy.");
                    }
                }

                foreach (var line in receivedDataJson.Split('\n'))
                {
                    ImGui.TextUnformatted(line);
                }

                ImGui.TreePop();
            }
        }
    }

    private static void DrawStatsRow(string metric, string current, string? last)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.Text(metric);
        ImGui.TableSetColumnIndex(1);
        ImGui.Text(current);
        ImGui.TableSetColumnIndex(2);
        ImGui.Text(last ?? "-");
    }

    private static string FormatNullableBool(bool? value)
    {
        if (!value.HasValue)
        {
            return "-";
        }

        return value.Value ? "Yes" : "No";
    }

    private static string FormatNullableTime(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToLocalTime().ToString("HH:mm:ss") : "-";
    }

    private CharacterStatsSnapshot GetCharacterStatsSnapshot(Pair pair)
    {
        var uid = pair.UserData.UID;
        if (!_characterStats.TryGetValue(uid, out var snapshot))
        {
            var initial = BuildCharacterStats(pair);
            snapshot = new CharacterStatsSnapshot(initial);
            _characterStats[uid] = snapshot;
            return snapshot;
        }

        var updated = BuildCharacterStats(pair);
        if (HasStatsChanged(snapshot.Current, updated))
        {
            snapshot.Previous = snapshot.Current;
            snapshot.Current = updated;
        }
        else
        {
            snapshot.Current = updated;
        }

        return snapshot;
    }

    private static bool HasStatsChanged(CharacterStats current, CharacterStats updated)
    {
        return !string.Equals(current.DataHash, updated.DataHash, StringComparison.Ordinal)
               || current.AppliedBytes != updated.AppliedBytes
               || current.AppliedTris != updated.AppliedTris
               || current.AppliedVramBytes != updated.AppliedVramBytes
               || current.IsVisible != updated.IsVisible
               || current.IsMutuallyVisible != updated.IsMutuallyVisible
               || current.IsPaused != updated.IsPaused
               || current.ApplyRetryCount != updated.ApplyRetryCount
               || current.LastAckSuccess != updated.LastAckSuccess
               || !string.Equals(current.LastAckId, updated.LastAckId, StringComparison.Ordinal)
               || current.LastAckTime != updated.LastAckTime;
    }

    private static CharacterStats BuildCharacterStats(Pair pair)
    {
        var dataHash = pair.GetCurrentDataHash();
        var lastAction = GetLastApplyDebugLine(pair);

        return new CharacterStats(
            dataHash: string.IsNullOrEmpty(dataHash) ? "-" : dataHash,
            appliedBytes: pair.LastAppliedDataBytes,
            appliedTris: pair.LastAppliedDataTris,
            appliedVramBytes: pair.LastAppliedApproximateVRAMBytes,
            isVisible: pair.IsVisible,
            isMutuallyVisible: pair.IsMutuallyVisible,
            isPaused: pair.IsPaused,
            lastAckSuccess: pair.LastAcknowledgmentSuccess,
            lastAckTime: pair.LastAcknowledgmentTime,
            lastAckId: pair.LastAcknowledgmentId,
            applyRetryCount: pair.ApplyRetryCount,
            snapshotTime: DateTimeOffset.Now,
            lastAction: string.IsNullOrEmpty(lastAction) ? "-" : lastAction);
    }

    private static string? GetLastApplyDebugLine(Pair pair)
    {
        var lines = pair.GetApplyDebugLines();
        if (lines.Length == 0)
        {
            return null;
        }

        return lines[^1];
    }

    private sealed class CharacterStatsSnapshot
    {
        public CharacterStatsSnapshot(CharacterStats current)
        {
            Current = current;
        }

        public CharacterStats Current { get; set; }
        public CharacterStats? Previous { get; set; }
    }

    private sealed class CharacterStats
    {
        public CharacterStats(string dataHash, long appliedBytes, long appliedTris, long appliedVramBytes, bool isVisible,
            bool isMutuallyVisible, bool isPaused, bool? lastAckSuccess, DateTimeOffset? lastAckTime, string? lastAckId,
            int applyRetryCount, DateTimeOffset snapshotTime, string lastAction)
        {
            DataHash = dataHash;
            AppliedBytes = appliedBytes;
            AppliedTris = appliedTris;
            AppliedVramBytes = appliedVramBytes;
            IsVisible = isVisible;
            IsMutuallyVisible = isMutuallyVisible;
            IsPaused = isPaused;
            LastAckSuccess = lastAckSuccess;
            LastAckTime = lastAckTime;
            LastAckId = lastAckId;
            ApplyRetryCount = applyRetryCount;
            SnapshotTime = snapshotTime;
            LastAction = lastAction;
        }

        public string DataHash { get; }
        public long AppliedBytes { get; }
        public long AppliedTris { get; }
        public long AppliedVramBytes { get; }
        public bool IsVisible { get; }
        public bool IsMutuallyVisible { get; }
        public bool IsPaused { get; }
        public bool? LastAckSuccess { get; }
        public DateTimeOffset? LastAckTime { get; }
        public string? LastAckId { get; }
        public int ApplyRetryCount { get; }
        public DateTimeOffset SnapshotTime { get; }
        public string LastAction { get; }
    }
    
    private bool _showLogPopup = false;
    
    private void DrawCommunicationLog()
    {
        ImGui.Text("Communication Log");
        
        // Filter options
        ImGui.SameLine();
        if (ImGui.Button("Clear Log"))
        {
            _communicationLog = "Communication Log:\n";
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Open in Popup"))
        {
            _showLogPopup = true;
        }
        
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);
        
        // Event type filters
        ImGui.Text("Show:");
        ImGui.SameLine();
        ImGui.Checkbox("Connections", ref _showConnections);
        ImGui.SameLine();
        ImGui.Checkbox("Health Checks", ref _showHealthChecks);
        ImGui.SameLine();
        ImGui.Checkbox("Acknowledgments", ref _showAcknowledgments);
        ImGui.SameLine();
        ImGui.Checkbox("Circuit Breaker", ref _showCircuitBreaker);
        
        // Draw the log content
        DrawLogContent();
    }
    
    private void DrawLogContent()
    {
        var availableSize = ImGui.GetContentRegionAvail();
        availableSize.Y -= ImGui.GetStyle().ItemSpacing.Y;
        
        using var child = ImRaii.Child("CommunicationLog", availableSize, true);
        if (child)
        {
            DrawLogLines();
        }
    }
    
    private void DrawLogPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(800, 600), ImGuiCond.FirstUseEver);
        
        if (ImGui.Begin("Communication Log###CommunicationLogPopup", ref _showLogPopup))
        {
            // Filter controls in popup
            ImGui.Text("Show:");
            ImGui.SameLine();
            ImGui.Checkbox("Connections##popup", ref _showConnections);
            ImGui.SameLine();
            ImGui.Checkbox("Health Checks##popup", ref _showHealthChecks);
            ImGui.SameLine();
            ImGui.Checkbox("Acknowledgments##popup", ref _showAcknowledgments);
            ImGui.SameLine();
            ImGui.Checkbox("Circuit Breaker##popup", ref _showCircuitBreaker);
            
            ImGui.SameLine();
            ImGui.Checkbox("Auto-scroll##popup", ref _autoScroll);
            
            ImGui.SameLine();
            if (ImGui.Button("Clear Log##popup"))
            {
                _communicationLog = "Communication Log:\n";
            }
            
            ImGui.Separator();
            
            // Log content in popup
            var availableSize = ImGui.GetContentRegionAvail();
            using var child = ImRaii.Child("CommunicationLogPopupContent", availableSize, true);
            if (child)
            {
                DrawLogLines();
            }
        }
        ImGui.End();
    }
    
    private void DrawLogLines()
    {
        // Parse and display log with colors
        var lines = _communicationLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            // Extract timestamp, type, and message
            var parts = line.Split("] ", 3);
            if (parts.Length >= 3)
            {
                var timestamp = parts[0].TrimStart('[');
                var type = parts[1].TrimStart('[');
                var message = parts[2];
                
                // Color based on type
                var color = type switch
                {
                    "CONN" => ImGuiColors.ParsedBlue,
                    "HEALTH" => ImGuiColors.ParsedGreen,
                    "ACK" => ImGuiColors.ParsedPurple,
                    "CIRCUIT" => ImGuiColors.DalamudOrange,
                    "INFO" => ImGuiColors.DalamudWhite,
                    "WARN" => ImGuiColors.DalamudYellow,
                    "ERROR" => ImGuiColors.DalamudRed,
                    _ => ImGuiColors.DalamudGrey
                };
                
                // Display with formatting
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"[{timestamp}]");
                ImGui.SameLine();
                ImGui.TextColored(color, $"[{type}]");
                ImGui.SameLine();
                ImGui.TextUnformatted(message);
            }
            else
            {
                // Fallback for malformed lines
                ImGui.TextUnformatted(line);
            }
        }
        
        if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
        {
            ImGui.SetScrollHereY(1.0f);
        }
    }
    
    private void LogCommunication(string message, string type = "INFO")
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logEntry = $"[{timestamp}] [{type}] {message}\n";
        
        // Apply filtering based on type
        bool shouldLog = type switch
        {
            "CONN" => _showConnections,
            "HEALTH" => _showHealthChecks,
            "ACK" => _showAcknowledgments,
            "CIRCUIT" => _showCircuitBreaker,
            _ => true // Always show INFO, WARN, ERROR
        };
        
        if (shouldLog)
        {
            _communicationLog += logEntry;
            
            // Limit log size to prevent memory issues
            var lines = _communicationLog.Split('\n');
            if (lines.Length > 1000)
            {
                _communicationLog = string.Join('\n', lines.Skip(lines.Length - 800));
            }
        }
    }
    
    private void OnConnected(ConnectedMessage message)
    {
        LogCommunication($"Connected to server: {message.Connection.ServerInfo.ShardName}", "CONN");
    }
    
    private void OnDisconnected(DisconnectedMessage message)
    {
        LogCommunication("Disconnected from server", "CONN");
    }
    
    private void OnAcknowledgmentStatusChanged(AcknowledgmentStatusChangedMessage message)
    {
        LogCommunication($"Acknowledgment {message.Status}: {message.User.AliasOrUID} - ID: {message.AcknowledgmentId}", "ACK");
    }
    
    private void OnAcknowledgmentReceived(AcknowledgmentReceivedMessage message)
    {
        LogCommunication($"Acknowledgment received from {message.User.AliasOrUID} - ID: {message.AcknowledgmentId}", "ACK");
    }
    
    private void OnAcknowledgmentTimeout(AcknowledgmentTimeoutMessage message)
    {
        LogCommunication($"Acknowledgment timeout for {message.User.AliasOrUID} - ID: {message.AcknowledgmentId}", "ACK");
    }
    
    private void OnAcknowledgmentBatchCompleted(AcknowledgmentBatchCompletedMessage message)
    {
        var userNames = string.Join(", ", message.Recipients.Select(u => u.AliasOrUID));
        LogCommunication($"Acknowledgment batch completed - Batch ID: {message.BatchId}, Users: {userNames}", "ACK");
    }
    
    private void OnNotification(NotificationMessage message)
    {
        var typeStr = message.Type.ToString().ToUpper();
        LogCommunication($"Notification [{typeStr}]: {message.Title} - {message.Message}", "INFO");
    }
}
