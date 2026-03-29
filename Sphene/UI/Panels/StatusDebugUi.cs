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
using Sphene.SpheneConfiguration;
using System.Numerics;
using System.Text.Json;
using Sphene.PlayerData.Data;

namespace Sphene.UI.Panels;

public class StatusDebugUi : WindowMediatorSubscriberBase
{
    private new readonly ILogger<StatusDebugUi> _logger;
    
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly SpheneConfigService _configService;
    private readonly EnhancedAcknowledgmentManager? _acknowledgmentManager;
    private readonly SessionAcknowledgmentManager? _sessionAcknowledgmentManager;
    
    private string _communicationLog = "Communication Log:\n";
    private bool _autoScroll = true;
    private bool _showHealthChecks = true;
    private bool _showAcknowledgments = true;
    private bool _showCircuitBreaker = true;
    private bool _showConnections = true;
    private int _simulatedDisconnectSeconds = 3;
    private string? _selectedCharacterDebugUid;
    private string? _selectedCharacterStatsUid;
    private string? _selectedCollectionOverviewUid;
    private readonly Dictionary<string, CharacterStatsSnapshot> _characterStats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _activePenumbraPathsByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _activeMinionPathsByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _activePetPathsByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _activePenumbraPathsUpdatedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activePenumbraPathsErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activePenumbraRefreshInProgress = new(StringComparer.Ordinal);
    private readonly ActiveMismatchTrackerService _mismatchTracker;
    
    public StatusDebugUi(ILogger<StatusDebugUi> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, PairManager pairManager, ApiController apiController,
        ConnectionHealthMonitor healthMonitor, CircuitBreakerService circuitBreaker,
        SpheneConfigService configService,
        PerformanceCollectorService performanceCollectorService,
        ActiveMismatchTrackerService mismatchTracker,
        EnhancedAcknowledgmentManager? acknowledgmentManager = null,
        SessionAcknowledgmentManager? sessionAcknowledgmentManager = null)
        : base(logger, mediator, "Sphene Status Debug###SpheneStatusDebug", performanceCollectorService)
    {
        _logger = logger;
        _pairManager = pairManager;
        _apiController = apiController;
        _healthMonitor = healthMonitor;
        _circuitBreaker = circuitBreaker;
        _configService = configService;
        _mismatchTracker = mismatchTracker;
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

        using (var collectionOverviewTab = ImRaii.TabItem("Penumbra Collections"))
        {
            if (collectionOverviewTab)
            {
                DrawPenumbraCollectionOverview();
            }
        }

        using (var mismatchTrackerTab = ImRaii.TabItem("Active Mismatch Tracker"))
        {
            if (mismatchTrackerTab)
            {
                DrawActiveMismatchTracker();
            }
        }

        using (var legacyCheckTab = ImRaii.TabItem("Legacy Check"))
        {
            if (legacyCheckTab)
            {
                DrawLegacyCheck();
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
            _ = _apiController.CreateConnectionsAsync();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Refresh Pairs"))
        {
            LogCommunication("[DEBUG] Pair refresh triggered");
            _ = _apiController.CreateConnectionsAsync();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Clear Log"))
        {
            _communicationLog = "Communication Log:\n";
        }

        ImGui.Spacing();
        ImGui.Text("Disconnect Simulation:");
        _simulatedDisconnectSeconds = Math.Clamp(_simulatedDisconnectSeconds, 1, 300);
        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
        ImGui.SliderInt("Duration (seconds)##DisconnectSimulationDuration", ref _simulatedDisconnectSeconds, 1, 300);

        if (ImGui.Button("Simulate Real Disconnect"))
        {
            LogCommunication($"[DEBUG] Simulating real disconnect for {_simulatedDisconnectSeconds}s");
            _ = _apiController.SimulateDisconnectForTestingAsync(TimeSpan.FromSeconds(_simulatedDisconnectSeconds));
        }

        if (_apiController.IsDisconnectSimulationRunning)
        {
            ImGui.SameLine();
            UiSharedService.ColorText("Simulation running...", ImGuiColors.DalamudYellow);
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

    private void DrawPenumbraCollectionOverview()
    {
        ImGui.Text("Penumbra Collection Overview");

        var pairsToCheck = new HashSet<Pair>(_pairManager.DirectPairs);
        foreach (var groupPairs in _pairManager.GroupPairs.Values)
        {
            foreach (var pair in groupPairs)
            {
                pairsToCheck.Add(pair);
            }
        }

        var pairs = pairsToCheck
            .OrderBy(p => p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pairs.Count == 0)
        {
            ImGui.Text("No paired users");
            return;
        }

        if (string.IsNullOrEmpty(_selectedCollectionOverviewUid)
            || pairs.All(p => !string.Equals(p.UserData.UID, _selectedCollectionOverviewUid, StringComparison.Ordinal)))
        {
            _selectedCollectionOverviewUid = pairs[0].UserData.UID;
        }

        var selectedLabel = pairs.FirstOrDefault(p => string.Equals(p.UserData.UID, _selectedCollectionOverviewUid, StringComparison.Ordinal))?.UserData.AliasOrUID
            ?? "Select...";
        if (ImGui.BeginCombo("Character##PenumbraCollectionCharacter", selectedLabel))
        {
            foreach (var pair in pairs)
            {
                var selected = string.Equals(pair.UserData.UID, _selectedCollectionOverviewUid, StringComparison.Ordinal);
                if (ImGui.Selectable(pair.UserData.AliasOrUID, selected))
                {
                    _selectedCollectionOverviewUid = pair.UserData.UID;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        var selectedPair = pairs.FirstOrDefault(p => string.Equals(p.UserData.UID, _selectedCollectionOverviewUid, StringComparison.Ordinal));
        if (selectedPair == null)
        {
            ImGui.Text("No character selected");
            return;
        }

        var selectedUid = selectedPair.UserData.UID;
        var collectionId = selectedPair.GetPenumbraCollectionId();
        ImGui.TextUnformatted($"User: {selectedPair.UserData.AliasOrUID} ({selectedUid})");
        ImGui.TextUnformatted($"Collection ID: {(collectionId == Guid.Empty ? "(none)" : collectionId.ToString())}");
        ImGui.TextUnformatted($"Character Visible: {(selectedPair.IsVisible ? "Yes" : "No")}, Mutually Visible: {(selectedPair.IsMutuallyVisible ? "Yes" : "No")}");

        var isRefreshing = _activePenumbraRefreshInProgress.Contains(selectedUid);
        if (ImGui.Button($"Refresh Active Paths##refresh_active_paths_{selectedUid}") && !isRefreshing)
        {
            _ = RefreshPenumbraActivePathsAsync(selectedPair);
        }

        ImGui.SameLine();
        if (isRefreshing)
        {
            UiSharedService.ColorText("Refreshing active paths...", ImGuiColors.DalamudYellow);
        }
        else if (_activePenumbraPathsUpdatedAt.TryGetValue(selectedUid, out var updatedAt))
        {
            ImGui.TextUnformatted($"Last refresh: {updatedAt.ToLocalTime():HH:mm:ss}");
        }
        else
        {
            ImGui.TextUnformatted("Active paths not refreshed yet.");
        }

        if (_activePenumbraPathsErrors.TryGetValue(selectedUid, out var refreshError) && !string.IsNullOrEmpty(refreshError))
        {
            UiSharedService.ColorTextWrapped($"Refresh error: {refreshError}", ImGuiColors.DalamudRed);
        }

        var deliveredByPath = BuildDeliveredPathState(selectedPair.LastReceivedCharacterData);
        var loadedByPath = NormalizePathMap(selectedPair.GetLoadedCollectionPathsSnapshot());

        _activePenumbraPathsByUid.TryGetValue(selectedUid, out var playerActiveByPathRaw);
        playerActiveByPathRaw ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var playerActiveByPath = NormalizePathMap(playerActiveByPathRaw);
        
        _activeMinionPathsByUid.TryGetValue(selectedUid, out var minionActiveByPathRaw);
        minionActiveByPathRaw ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var minionActiveByPath = NormalizePathMap(minionActiveByPathRaw);
        
        _activePetPathsByUid.TryGetValue(selectedUid, out var petActiveByPathRaw);
        petActiveByPathRaw ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var petActiveByPath = NormalizePathMap(petActiveByPathRaw);

        // Only show paths that were delivered as active
        var activeDeliveredPaths = deliveredByPath
            .Where(kvp => kvp.Value.IsActive)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (activeDeliveredPaths.Count == 0)
        {
            ImGui.TextUnformatted("No active paths delivered.");
            return;
        }

        ImGui.TextUnformatted($"Active delivered paths: {activeDeliveredPaths.Count} | Loaded: {loadedByPath.Count} | Player Active: {playerActiveByPath.Count} | Minion Active: {minionActiveByPath.Count} | Pet Active: {petActiveByPath.Count}");

        if (!ImGui.BeginTable("PenumbraCollectionOverviewTable", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(-1, 420)))
        {
            return;
        }

        ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Kinds", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Delivered Source", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Data Flag", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Loaded Source", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Active Source", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Loaded", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Match", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableHeadersRow();

        foreach (var path in activeDeliveredPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            deliveredByPath.TryGetValue(path, out var deliveredState);
            loadedByPath.TryGetValue(path, out var loadedSource);
            
            // Check correct active paths based on ObjectKind
            var isMinionOrMountPath = deliveredState?.ObjectKinds.Contains("MinionOrMount") ?? false;
            var isPetPath = deliveredState?.ObjectKinds.Contains("Pet") ?? false;
            var activeByPath = isMinionOrMountPath ? minionActiveByPath 
                : isPetPath ? petActiveByPath 
                : playerActiveByPath;
            activeByPath.TryGetValue(path, out var activeSource);

            var hasDelivered = deliveredState != null;
            var hasLoaded = !string.IsNullOrEmpty(loadedSource);
            var isPenumbraActive = !string.IsNullOrEmpty(activeSource);
            var isDataFlagActive = deliveredState?.IsActive ?? false;

            var matches = hasDelivered
                ? isPenumbraActive == isDataFlagActive
                : !isPenumbraActive;

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawTrimmedTextWithTooltip(path, 96);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(deliveredState != null ? string.Join(", ", deliveredState.ObjectKinds.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) : "-");

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(deliveredState != null ? string.Join(", ", deliveredState.Sources.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) : "-");

            ImGui.TableSetColumnIndex(3);
            if (hasDelivered)
            {
                UiSharedService.ColorText(isDataFlagActive ? "Yes" : "No", isDataFlagActive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
            }
            else
            {
                ImGui.TextUnformatted("-");
            }

            ImGui.TableSetColumnIndex(4);
            DrawTrimmedTextWithTooltip(string.IsNullOrEmpty(loadedSource) ? "-" : loadedSource, 72);

            ImGui.TableSetColumnIndex(5);
            DrawTrimmedTextWithTooltip(string.IsNullOrEmpty(activeSource) ? "-" : activeSource, 72);

            ImGui.TableSetColumnIndex(6);
            UiSharedService.ColorText(hasLoaded ? "Yes" : "No", hasLoaded ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(7);
            UiSharedService.ColorText(isPenumbraActive ? "Yes" : "No", isPenumbraActive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(8);
            UiSharedService.ColorText(matches ? "OK" : "Diff", matches ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow);
        }

        ImGui.EndTable();
    }

    private static Dictionary<string, DeliveredPathState> BuildDeliveredPathState(Sphene.API.Data.CharacterData? characterData)
    {
        Dictionary<string, DeliveredPathState> deliveredByPath = new(StringComparer.OrdinalIgnoreCase);
        if (characterData == null)
        {
            return deliveredByPath;
        }

        foreach (var kindData in characterData.FileReplacements)
        {
            var objectKind = kindData.Key.ToString();
            foreach (var replacement in kindData.Value)
            {
                var source = !string.IsNullOrWhiteSpace(replacement.Hash)
                    ? replacement.Hash
                    : (!string.IsNullOrWhiteSpace(replacement.FileSwapPath) ? replacement.FileSwapPath : "-");

                foreach (var rawPath in replacement.GamePaths)
                {
                    var normalizedPath = NormalizeGamePath(rawPath);
                    if (string.IsNullOrEmpty(normalizedPath))
                    {
                        continue;
                    }

                    if (!deliveredByPath.TryGetValue(normalizedPath, out var state))
                    {
                        state = new DeliveredPathState();
                        deliveredByPath[normalizedPath] = state;
                    }

                    state.IsActive |= replacement.IsActive;
                    state.ObjectKinds.Add(objectKind);
                    state.Sources.Add(source);
                }
            }
        }

        return deliveredByPath;
    }

    private static Dictionary<string, string> NormalizePathMap(IReadOnlyDictionary<string, string> paths)
    {
        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalizedPath = NormalizeGamePath(path.Key);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                continue;
            }

            normalized[normalizedPath] = path.Value;
        }

        return normalized;
    }

    private static string NormalizeGamePath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return string.Empty;
        }

        var normalized = gamePath.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Trim('/').ToLowerInvariant();
    }

    private static void DrawTrimmedTextWithTooltip(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            ImGui.TextUnformatted("-");
            return;
        }

        var display = text.Length > maxLength
            ? "..." + text[^Math.Max(1, maxLength - 3)..]
            : text;
        ImGui.TextUnformatted(display);
        if (ImGui.IsItemHovered())
        {
            UiSharedService.AttachToolTip(text);
        }
    }

    private async Task RefreshPenumbraActivePathsAsync(Pair pair)
    {
        var uid = pair.UserData.UID;
        if (!_activePenumbraRefreshInProgress.Add(uid))
        {
            return;
        }

        try
        {
            _activePenumbraPathsErrors.Remove(uid);
            
            // Get Player active paths
            var activePaths = await pair.GetCurrentPenumbraActivePathsByGamePathAsync().ConfigureAwait(false);
            _activePenumbraPathsByUid[uid] = NormalizePathMap(activePaths);
            
            // Get Minion/Mount active paths
            var minionPaths = await pair.GetMinionOrMountActivePathsByGamePathAsync().ConfigureAwait(false);
            _activeMinionPathsByUid[uid] = NormalizePathMap(minionPaths);
            
            // Get Pet active paths
            var petPaths = await pair.GetPetActivePathsByGamePathAsync().ConfigureAwait(false);
            _activePetPathsByUid[uid] = NormalizePathMap(petPaths);
            
            _activePenumbraPathsUpdatedAt[uid] = DateTimeOffset.UtcNow;
            
            // Track mismatches: delivered as active but not active in Penumbra
            TrackActiveMismatches(uid, pair.LastReceivedCharacterData, activePaths, minionPaths, petPaths);
        }
        catch (Exception ex)
        {
            _activePenumbraPathsErrors[uid] = ex.Message;
            _logger.LogWarning(ex, "Failed to refresh active Penumbra paths for {uid}", uid);
        }
        finally
        {
            _activePenumbraRefreshInProgress.Remove(uid);
        }
    }

    private void TrackActiveMismatches(string uid, Sphene.API.Data.CharacterData? characterData, IReadOnlyDictionary<string, string> playerActivePaths, IReadOnlyDictionary<string, string> minionActivePaths, IReadOnlyDictionary<string, string> petActivePaths)
    {
        if (characterData == null) return;

        var deliveredByPath = BuildDeliveredPathState(characterData);
        var playerActiveByPath = NormalizePathMap(playerActivePaths);
        var minionActiveByPath = NormalizePathMap(minionActivePaths);
        var petActiveByPath = NormalizePathMap(petActivePaths);

        foreach (var kvp in deliveredByPath)
        {
            var gamePath = kvp.Key;
            var state = kvp.Value;

            if (!state.IsActive) continue; // Only track paths flagged as active

            // Check correct active paths based on ObjectKind
            var isMinionOrMountPath = state.ObjectKinds.Contains("MinionOrMount");
            var isPetPath = state.ObjectKinds.Contains("Pet");
            var activeByPath = isMinionOrMountPath ? minionActiveByPath 
                : isPetPath ? petActiveByPath 
                : playerActiveByPath;
            
            var isPenumbraActive = activeByPath.TryGetValue(gamePath, out var activeSource) && !string.IsNullOrEmpty(activeSource);
            if (!isPenumbraActive)
            {
                // Mismatch: delivered as active but not active in Penumbra
                _mismatchTracker.RecordMismatch(uid, gamePath, state.Sources, state.ObjectKinds);
            }
        }
    }

    private void DrawActiveMismatchTracker()
    {
        ImGui.Text("Active Mismatch Tracker");
        ImGui.TextWrapped("Tracks paths flagged as IsActive=true in delivered data but not active in Penumbra collection. Auto-refreshes every 10 seconds.");
        ImGui.Separator();

        if (ImGui.Button("Clear All Records"))
        {
            _mismatchTracker.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Counters"))
        {
            _mismatchTracker.ResetGlobalCounters();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reset total check counters without clearing mismatch records");
        }

        var records = _mismatchTracker.GetRecords().Where(r => r.MismatchCount > 0).ToList();
        var (globalChecks, globalMismatches) = _mismatchTracker.GetGlobalStats();
        ImGui.SameLine();
        ImGui.Text($"| Mismatches: {records.Count} | Total Checks: {globalChecks} | Auto-refresh every 10s");

        if (records.Count == 0)
        {
            ImGui.Text("No mismatches recorded yet.");
            return;
        }

        ImGui.Separator();

        if (!ImGui.BeginTable("ActiveMismatchTable", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(-1, 400)))
        {
            return;
        }

        ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("Global %", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("Local %", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("Checks", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Last Mismatch", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Object Kinds", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableHeadersRow();

        // Sort by global mismatch percentage (based on total checks across all paths)
        // A path with 500 mismatches out of 1000 global checks = 50%
        // A path with 1 mismatch out of 1000 global checks = 0.1%
        foreach (var record in records
            .OrderByDescending(r => r.GlobalMismatchPercentage)
            .ThenByDescending(r => r.MismatchCount)
            .ThenBy(r => r.Uid, StringComparer.Ordinal))
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var displayName = GetDisplayName(record.Uid);
            ImGui.TextUnformatted(displayName);

            ImGui.TableSetColumnIndex(1);
            DrawTrimmedTextWithTooltip(record.GamePath, 100);

            ImGui.TableSetColumnIndex(2);
            // Global % - mismatch count / total global checks
            var globalPercentage = record.GlobalMismatchPercentage;
            var globalColor = globalPercentage >= 10 ? ImGuiColors.DalamudRed 
                : globalPercentage >= 5 ? ImGuiColors.DalamudOrange 
                : globalPercentage >= 1 ? ImGuiColors.DalamudYellow 
                : ImGuiColors.HealerGreen;
            UiSharedService.ColorText($"{globalPercentage:F2}%", globalColor);

            ImGui.TableSetColumnIndex(3);
            // Local % - mismatch count / this path's own check count
            var localPercentage = record.MismatchPercentage;
            var localColor = localPercentage >= 50 ? ImGuiColors.DalamudRed 
                : localPercentage >= 25 ? ImGuiColors.DalamudOrange 
                : localPercentage >= 10 ? ImGuiColors.DalamudYellow 
                : ImGuiColors.HealerGreen;
            UiSharedService.ColorText($"{localPercentage:F1}%", localColor);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted($"{record.MismatchCount}/{record.TotalCheckCount}");

            ImGui.TableSetColumnIndex(5);
            if (record.LastSeen != DateTimeOffset.MinValue)
            {
                ImGui.TextUnformatted(record.LastSeen.ToLocalTime().ToString("HH:mm:ss"));
            }
            else
            {
                ImGui.TextUnformatted("-");
            }

            ImGui.TableSetColumnIndex(6);
            var sourcesText = string.Join(", ", record.Sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            DrawTrimmedTextWithTooltip(sourcesText, 60);

            ImGui.TableSetColumnIndex(7);
            var kindsText = string.Join(", ", record.ObjectKinds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            DrawTrimmedTextWithTooltip(kindsText, 50);
        }

        ImGui.EndTable();
    }

    private string GetDisplayName(string uid)
    {
        // Try to find the pair and get its alias
        foreach (var pair in _pairManager.DirectPairs)
        {
            if (string.Equals(pair.UserData.UID, uid, StringComparison.Ordinal))
            {
                return pair.UserData.AliasOrUID;
            }
        }

        foreach (var groupPairs in _pairManager.GroupPairs.Values)
        {
            foreach (var pair in groupPairs)
            {
                if (string.Equals(pair.UserData.UID, uid, StringComparison.Ordinal))
                {
                    return pair.UserData.AliasOrUID;
                }
            }
        }

        return uid;
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

    private sealed class DeliveredPathState
    {
        public bool IsActive { get; set; }
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ObjectKinds { get; } = new(StringComparer.OrdinalIgnoreCase);
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
        LogCommunication($"Acknowledgment {message.Event.Status}: {message.Event.User.AliasOrUID} - ID: {message.Event.AcknowledgmentId}", "ACK");
    }
    
    private void OnAcknowledgmentReceived(AcknowledgmentReceivedMessage message)
    {
        LogCommunication($"Acknowledgment received from {message.Event.User.AliasOrUID} - ID: {message.Event.AcknowledgmentId}", "ACK");
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

    private void DrawLegacyCheck()
    {
        if (ImGui.BeginTable("LegacyCheckTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            var pairsToCheck = new HashSet<Pair>(_pairManager.DirectPairs);
            foreach (var groupPairs in _pairManager.GroupPairs.Values)
            {
                foreach (var pair in groupPairs)
                {
                    pairsToCheck.Add(pair);
                }
            }

            foreach (var pair in pairsToCheck)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(pair.UserData.AliasOrUID);

                ImGui.TableNextColumn();
                var data = pair.LastReceivedCharacterData;
                if (data == null)
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "No Data");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted("-");
                }
                else
                {
                    var found = false;
                    var details = new List<string>();

                    if (data.FileReplacements != null)
                    {
                        foreach (var kvp in data.FileReplacements)
                        {
                            foreach (var replacement in kvp.Value)
                            {
                                if (!string.IsNullOrEmpty(replacement.FileSwapPath) && replacement.FileSwapPath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    details.Add($"Swap: {replacement.FileSwapPath}");
                                }
                                foreach (var gamePath in replacement.GamePaths)
                                {
                                    if (gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                                    {
                                        found = true;
                                        details.Add($"GamePath: {gamePath}");
                                    }
                                }
                            }
                        }
                    }

                    if (found)
                    {
                        var filterEnabled = _configService.Current.FilterCharacterLegacyShpkInOutgoingCharacterData;
                        if (filterEnabled)
                        {
                            ImGui.TextColored(ImGuiColors.DalamudYellow, "FOUND (Filtered)");
                            details.Insert(0, "Inbound filter active: characterlegacy.shpk entries were blocked for apply/download.");
                        }
                        else
                        {
                            ImGui.TextColored(ImGuiColors.DalamudRed, "FOUND");
                        }
                        ImGui.TableNextColumn();
                        ImGui.TextWrapped(string.Join("\n", details));
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.HealerGreen, "Clean");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted("-");
                    }
                }
            }

            ImGui.EndTable();
        }
    }
}
