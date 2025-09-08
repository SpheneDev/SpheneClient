using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using System.Numerics;
using Microsoft.Extensions.Logging;

namespace Sphene.UI.Components;

// UI component for monitoring and configuring the acknowledgment system
public class AcknowledgmentMonitorUI : WindowMediatorSubscriberBase
{
    private readonly EnhancedAcknowledgmentManager _acknowledgmentManager;
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private readonly UiSharedService _uiSharedService;
    private AcknowledgmentConfiguration _config;
    private AcknowledgmentMetrics _metrics;
    private bool _showAdvancedSettings = false;
    private bool _showMetricsDetails = false;
    private bool _showSessionDetails = false;
    
    public AcknowledgmentMonitorUI(ILogger<AcknowledgmentMonitorUI> logger, EnhancedAcknowledgmentManager acknowledgmentManager, 
        SessionAcknowledgmentManager sessionAcknowledgmentManager, UiSharedService uiSharedService, SpheneMediator mediator, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Acknowledgment Monitor###SpheneAckMonitor", performanceCollectorService)
    {
        _acknowledgmentManager = acknowledgmentManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _uiSharedService = uiSharedService;
        _config = _acknowledgmentManager.GetConfiguration();
        _metrics = _acknowledgmentManager.GetMetrics();
        
        SizeConstraints = new()
        {
            MinimumSize = new(600, 400),
            MaximumSize = new(1200, 800)
        };
        
        IsOpen = false;
        
        // Subscribe to RefreshUiMessage to update the UI when acknowledgments change
        Mediator.Subscribe<RefreshUiMessage>(this, (_) =>
        {
            // Refresh metrics when UI refresh is requested
            _metrics = _acknowledgmentManager.GetMetrics();
        });
    }
    
    // Draws the acknowledgment monitor UI
    protected override void DrawInternal()
    {
        // Refresh metrics
        _metrics = _acknowledgmentManager.GetMetrics();
        
        if (ImGui.CollapsingHeader("Acknowledgment System Monitor"))
        {
            DrawMetricsOverview();
            ImGui.Separator();
            DrawSessionInformation();
            ImGui.Separator();
            DrawConfiguration();
            
            if (_showMetricsDetails)
            {
                ImGui.Separator();
                DrawDetailedMetrics();
            }
            
            if (_showSessionDetails)
            {
                ImGui.Separator();
                DrawSessionDetails();
            }
        }
    }
    
    // Draws the metrics overview section
    private void DrawMetricsOverview()
    {
        ImGui.Text("System Status");
        
        // Success rate with color coding
        var successRate = _metrics.SuccessRate;
        var successColor = successRate >= 95 ? ImGuiColors.ParsedGreen : 
                          successRate >= 80 ? ImGuiColors.DalamudYellow : 
                          ImGuiColors.DalamudRed;
        
        ImGui.TextColored(successColor, $"Success Rate: {successRate:F1}%");
        UiSharedService.AttachToolTip($"Successful acknowledgments: {_metrics.TotalSuccessful}/{_metrics.TotalReceived}");
        
        ImGui.SameLine();
        ImGui.Text($"| Avg Response: {_metrics.AverageResponseTimeMs:F0}ms");
        
        // Statistics in columns
        if (ImGui.BeginTable("AckStats", 4, ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn("Sent");
            ImGui.TableSetupColumn("Received");
            ImGui.TableSetupColumn("Failed");
            ImGui.TableSetupColumn("Retries");
            ImGui.TableHeadersRow();
            
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(_metrics.TotalSent.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(_metrics.TotalReceived.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(_metrics.TotalFailed > 0 ? ImGuiColors.DalamudRed : ImGuiColors.DalamudWhite, 
                _metrics.TotalFailed.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(_metrics.TotalRetries > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudWhite, 
                _metrics.TotalRetries.ToString());
            
            ImGui.EndTable();
        }
        
        // Show/hide detailed metrics button
        if (ImGui.Button(_showMetricsDetails ? "Hide Details" : "Show Details"))
        {
            _showMetricsDetails = !_showMetricsDetails;
        }
    }
    
    // Draws session information section
    private void DrawSessionInformation()
    {
        ImGui.Text("Session Information");
        
        // Current session ID
        ImGui.Text($"Current Session: {_sessionAcknowledgmentManager.CurrentSessionId}");
        
        // Pending acknowledgments count
        var pendingCount = _sessionAcknowledgmentManager.GetPendingAcknowledgmentCount();
        var pendingColor = pendingCount > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.ParsedGreen;
        ImGui.TextColored(pendingColor, $"Pending Acknowledgments: {pendingCount}");
        
        // Show/hide session details button
        ImGui.SameLine();
        if (ImGui.Button(_showSessionDetails ? "Hide Session Details" : "Show Session Details"))
        {
            _showSessionDetails = !_showSessionDetails;
        }
    }
    
    // Draws detailed session information
    private void DrawSessionDetails()
    {
        ImGui.Text("Session Details");
        
        var pendingAcks = _sessionAcknowledgmentManager.GetPendingAcknowledgments();
        
        if (pendingAcks.Any())
        {
            if (ImGui.BeginTable("SessionAcks", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Acknowledgment ID");
                ImGui.TableSetupColumn("Pending Users");
                ImGui.TableSetupColumn("Count");
                ImGui.TableHeadersRow();
                
                foreach (var ack in pendingAcks)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    
                    // Truncate long acknowledgment IDs for display
                    var displayId = ack.Key.Length > 30 ? ack.Key.Substring(0, 27) + "..." : ack.Key;
                    ImGui.Text(displayId);
                    UiSharedService.AttachToolTip(ack.Key);
                    
                    ImGui.TableNextColumn();
                    var userList = string.Join(", ", ack.Value.Select(u => u.AliasOrUID).Take(3));
                    if (ack.Value.Count > 3)
                    {
                        userList += $" (+{ack.Value.Count - 3} more)";
                    }
                    ImGui.Text(userList);
                    
                    ImGui.TableNextColumn();
                    ImGui.Text(ack.Value.Count.ToString());
                }
                
                ImGui.EndTable();
            }
        }
        else
        {
            ImGui.TextColored(ImGuiColors.ParsedGreen, "No pending acknowledgments in current session.");
        }
    }
    
    // Draws the detailed metrics section
    private void DrawDetailedMetrics()
    {
        ImGui.Text("Detailed Metrics");
        
        // Error breakdown
        if (_metrics.ErrorCounts.Any())
        {
            ImGui.Text("Error Breakdown:");
            foreach (var error in _metrics.ErrorCounts.OrderByDescending(kvp => kvp.Value))
            {
                ImGui.BulletText($"{error.Key}: {error.Value}");
            }
        }
        
        ImGui.Spacing();
        
        // Priority breakdown
        if (_metrics.PriorityCounts.Any())
        {
            ImGui.Text("Priority Distribution:");
            foreach (var priority in _metrics.PriorityCounts.OrderByDescending(kvp => kvp.Value))
            {
                var color = priority.Key switch
                {
                    AcknowledgmentPriority.High => ImGuiColors.DalamudRed,
                    AcknowledgmentPriority.Medium => ImGuiColors.DalamudYellow,
                    AcknowledgmentPriority.Low => ImGuiColors.ParsedGreen,
                    _ => ImGuiColors.DalamudWhite
                };
                
                ImGui.TextColored(color, $"● {priority.Key}: {priority.Value}");
            }
        }
        
        ImGui.Text($"Last Updated: {_metrics.LastUpdated:HH:mm:ss}");
    }
    
    // Draws the configuration section
    private void DrawConfiguration()
    {
        ImGui.Text("Configuration");
        
        // Basic settings
        var enableBatching = _config.EnableBatching;
        if (ImGui.Checkbox("Enable Batching", ref enableBatching))
        {
            _config.EnableBatching = enableBatching;
        }
        UiSharedService.AttachToolTip("Group multiple acknowledgments together for better performance");
        
        ImGui.SameLine();
        var enableAutoRetry = _config.EnableAutoRetry;
        if (ImGui.Checkbox("Auto Retry", ref enableAutoRetry))
        {
            _config.EnableAutoRetry = enableAutoRetry;
        }
        UiSharedService.AttachToolTip("Automatically retry failed acknowledgments");
        
        ImGui.SameLine();
        var enableSilentAck = _config.EnableSilentAcknowledgments;
        if (ImGui.Checkbox("Silent Acks", ref enableSilentAck))
        {
            _config.EnableSilentAcknowledgments = enableSilentAck;
        }
        UiSharedService.AttachToolTip("Send periodic acknowledgments to maintain connection health");
        
        // Timeout settings
        var defaultTimeout = _config.DefaultTimeoutSeconds;
        if (ImGui.SliderInt("Default Timeout (s)", ref defaultTimeout, 5, 120))
        {
            _config.DefaultTimeoutSeconds = defaultTimeout;
        }
        
        if (_config.EnableBatching)
        {
            var maxBatchSize = _config.MaxBatchSize;
            if (ImGui.SliderInt("Max Batch Size", ref maxBatchSize, 1, 50))
            {
                _config.MaxBatchSize = maxBatchSize;
            }
            
            var batchTimeoutMs = _config.BatchTimeoutMs;
            if (ImGui.SliderInt("Batch Timeout (ms)", ref batchTimeoutMs, 1000, 30000))
            {
                _config.BatchTimeoutMs = batchTimeoutMs;
            }
        }
        
        // Advanced settings toggle
        if (ImGui.Button(_showAdvancedSettings ? "Hide Advanced" : "Show Advanced"))
        {
            _showAdvancedSettings = !_showAdvancedSettings;
        }
        
        if (_showAdvancedSettings)
        {
            DrawAdvancedSettings();
        }
    }
    
    // Draws the advanced settings section
    private void DrawAdvancedSettings()
    {
        ImGui.Separator();
        ImGui.Text("Advanced Settings");
        
        // Priority system
        var enablePriority = _config.EnablePrioritySystem;
        if (ImGui.Checkbox("Enable Priority System", ref enablePriority))
        {
            _config.EnablePrioritySystem = enablePriority;
        }
        
        if (_config.EnablePrioritySystem)
        {
            ImGui.Indent();
            
            var highTimeout = _config.HighPriorityTimeoutSeconds;
            if (ImGui.SliderInt("High Priority Timeout (s)", ref highTimeout, 1, 60))
            {
                _config.HighPriorityTimeoutSeconds = highTimeout;
            }
            
            var mediumTimeout = _config.MediumPriorityTimeoutSeconds;
            if (ImGui.SliderInt("Medium Priority Timeout (s)", ref mediumTimeout, 5, 120))
            {
                _config.MediumPriorityTimeoutSeconds = mediumTimeout;
            }
            
            var lowTimeout = _config.LowPriorityTimeoutSeconds;
            if (ImGui.SliderInt("Low Priority Timeout (s)", ref lowTimeout, 10, 300))
            {
                _config.LowPriorityTimeoutSeconds = lowTimeout;
            }
            
            ImGui.Unindent();
        }
        
        // Retry settings
        if (_config.EnableAutoRetry)
        {
            var maxRetries = _config.MaxRetryAttempts;
            if (ImGui.SliderInt("Max Retry Attempts", ref maxRetries, 0, 10))
            {
                _config.MaxRetryAttempts = maxRetries;
            }
            
            var baseDelay = _config.BaseRetryDelayMs;
            if (ImGui.SliderInt("Base Retry Delay (ms)", ref baseDelay, 100, 10000))
            {
                _config.BaseRetryDelayMs = baseDelay;
            }
            
            var maxDelay = _config.MaxRetryDelayMs;
            if (ImGui.SliderInt("Max Retry Delay (ms)", ref maxDelay, 1000, 60000))
            {
                _config.MaxRetryDelayMs = Math.Max(maxDelay, _config.BaseRetryDelayMs);
            }
        }
        
        // Cache settings
        var maxCacheSize = _config.MaxCacheSize;
        if (ImGui.SliderInt("Max Cache Size", ref maxCacheSize, 100, 10000))
        {
            _config.MaxCacheSize = maxCacheSize;
        }
        
        var cacheExpiration = _config.CacheExpirationMinutes;
        if (ImGui.SliderInt("Cache Expiration (min)", ref cacheExpiration, 5, 180))
        {
            _config.CacheExpirationMinutes = cacheExpiration;
        }
        
        // Silent acknowledgment settings
        if (_config.EnableSilentAcknowledgments)
        {
            var silentInterval = _config.SilentAcknowledgmentIntervalMinutes;
            if (ImGui.SliderInt("Silent Ack Interval (min)", ref silentInterval, 1, 30))
            {
                _config.SilentAcknowledgmentIntervalMinutes = silentInterval;
            }
        }
        
        // Performance settings
        var maxPending = _config.MaxPendingAcknowledgmentsPerUser;
        if (ImGui.SliderInt("Max Pending per User", ref maxPending, 10, 1000))
        {
            _config.MaxPendingAcknowledgmentsPerUser = maxPending;
        }
        
        var enableMetrics = _config.EnableMetrics;
        if (ImGui.Checkbox("Enable Metrics Collection", ref enableMetrics))
        {
            _config.EnableMetrics = enableMetrics;
        }
        
        // Reset to defaults button
        ImGui.Spacing();
        if (ImGui.Button("Reset to Defaults"))
        {
            _config = new AcknowledgmentConfiguration();
        }
        UiSharedService.AttachToolTip("Reset all settings to their default values");
    }
}