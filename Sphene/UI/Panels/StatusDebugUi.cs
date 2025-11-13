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

namespace Sphene.UI.Panels;

public class StatusDebugUi : WindowMediatorSubscriberBase
{
    private readonly ILogger<StatusDebugUi> _logger;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly EnhancedAcknowledgmentManager? _acknowledgmentManager;
    private readonly SessionAcknowledgmentManager? _sessionAcknowledgmentManager;
    
    private string _communicationLog = "Communication Log:\n";
    private bool _autoScroll = true;
    private Vector2 _logScrollPosition = Vector2.Zero;
    private bool _showHealthChecks = true;
    private bool _showAcknowledgments = true;
    private bool _showCircuitBreaker = true;
    private bool _showConnections = true;
    
    public StatusDebugUi(ILogger<StatusDebugUi> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, PairManager pairManager, ApiController apiController,
        ConnectionHealthMonitor healthMonitor, CircuitBreakerService circuitBreaker,
        PerformanceCollectorService performanceCollectorService,
        EnhancedAcknowledgmentManager? acknowledgmentManager = null,
        SessionAcknowledgmentManager? sessionAcknowledgmentManager = null)
        : base(logger, mediator, "Sphene Status Debug###SpheneStatusDebug", performanceCollectorService)
    {
        _logger = logger;
        _uiSharedService = uiSharedService;
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
    
    private Timer? _healthStatusTimer;
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
            _healthStatusTimer?.Dispose();
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