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
using Sphene.WebAPI;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

// UI component for monitoring and configuring the acknowledgment system
public class AcknowledgmentMonitorUI : WindowMediatorSubscriberBase
{
    private readonly EnhancedAcknowledgmentManager _acknowledgmentManager;
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private readonly ApiController _apiController;
    private AcknowledgmentConfiguration _config;
    private AcknowledgmentMetrics _metrics;
    private bool _showAdvancedSettings = false;
    private bool _showMetricsDetails = false;
    private bool _showSessionDetails = false;
    
    public AcknowledgmentMonitorUI(ILogger<AcknowledgmentMonitorUI> logger, EnhancedAcknowledgmentManager acknowledgmentManager,
        SessionAcknowledgmentManager sessionAcknowledgmentManager, SpheneMediator mediator, PerformanceCollectorService performanceCollectorService, ApiController apiController)
        : base(logger, mediator, "Acknowledgment Monitor###SpheneAckMonitor", performanceCollectorService)
    {
        _acknowledgmentManager = acknowledgmentManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _apiController = apiController;
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

        if (SpheneUI.Layout.BeginScrollArea("ack-monitor-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawMonitorContent();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawMonitorContent()
    {
        if (SpheneUI.Controls.CollapsibleSection("Acknowledgment System Monitor"))
        {
            SpheneUI.Text.Wrapped("Monitor and configure acknowledgment batching, retries, and sessions.");
            DrawMetricsOverview();
            SpheneUI.Layout.Separator();
            DrawSessionInformation();
            SpheneUI.Layout.Separator();
            
            // Only show configuration to admin users
            if (_apiController.IsAdmin)
            {
                DrawConfiguration();
            }
            
            if (_showMetricsDetails)
            {
                SpheneUI.Layout.Separator();
                DrawDetailedMetrics();
            }
            
            if (_showSessionDetails)
            {
                SpheneUI.Layout.Separator();
                DrawSessionDetails();
            }
        }
    }
    
    // Draws the metrics overview section
    private void DrawMetricsOverview()
    {
        SpheneUI.Text.SectionHeader("System Status");
        
        // Success rate with color coding
        var successRate = _metrics.SuccessRate;
        var successColor = successRate >= 95 ? ImGuiColors.ParsedGreen : 
                          successRate >= 80 ? ImGuiColors.DalamudYellow : 
                          ImGuiColors.DalamudRed;
        
        SpheneUI.Text.Colored($"Success Rate: {successRate:F1}%", successColor);
        SpheneUI.AttachTooltip($"Successful acknowledgments: {_metrics.TotalSuccessful}/{_metrics.TotalReceived}");
        
        ImGui.SameLine();
        SpheneUI.Text.Plain($"| Avg Response: {_metrics.AverageResponseTimeMs:F0}ms");
        
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
            SpheneUI.Text.Plain(_metrics.TotalSent.ToString());
            ImGui.TableNextColumn();
            SpheneUI.Text.Plain(_metrics.TotalReceived.ToString());
            ImGui.TableNextColumn();
            SpheneUI.Text.Colored(_metrics.TotalFailed.ToString(), _metrics.TotalFailed > 0 ? ImGuiColors.DalamudRed : ImGuiColors.DalamudWhite);
            ImGui.TableNextColumn();
            SpheneUI.Text.Colored(_metrics.TotalRetries.ToString(), _metrics.TotalRetries > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudWhite);
            
            ImGui.EndTable();
        }
        
        // Show/hide detailed metrics button
        if (SpheneUI.Controls.Button(_showMetricsDetails ? "Hide Details" : "Show Details"))
        {
            _showMetricsDetails = !_showMetricsDetails;
        }
    }
    
    // Draws session information section
    private void DrawSessionInformation()
    {
        SpheneUI.Text.SectionHeader("Session Information");
        
        // Current session ID
        SpheneUI.Text.Plain($"Current Session: {_sessionAcknowledgmentManager.CurrentSessionId}");
        
        // Pending acknowledgments count
        var pendingCount = _sessionAcknowledgmentManager.GetPendingAcknowledgmentCount();
        var pendingColor = pendingCount > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.ParsedGreen;
        SpheneUI.Text.Colored($"Pending Acknowledgments: {pendingCount}", pendingColor);
        
        // Show/hide session details button
        ImGui.SameLine();
        if (SpheneUI.Controls.Button(_showSessionDetails ? "Hide Session Details" : "Show Session Details"))
        {
            _showSessionDetails = !_showSessionDetails;
        }
    }
    
    // Draws detailed session information
    private void DrawSessionDetails()
    {
        SpheneUI.Text.SectionHeader("Session Details");
        
        var acknowledgmentStatuses = _sessionAcknowledgmentManager.GetAcknowledgmentStatuses();
        var pendingAcks = _sessionAcknowledgmentManager.GetPendingAcknowledgments();
        
        if (acknowledgmentStatuses.Any())
        {
            if (ImGui.BeginTable("SessionAcks", 1, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Acknowledgment Status");
                ImGui.TableHeadersRow();
                
                foreach (var status in acknowledgmentStatuses)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain(status);
                }
                
                ImGui.EndTable();
            }
        }
        else
        {
            SpheneUI.Text.Colored("No pending acknowledgments in current session.", ImGuiColors.ParsedGreen);
        }

        SpheneUI.Layout.Separator();
        SpheneUI.Text.SectionHeader("Pending Acknowledgments");
        if (pendingAcks.Count == 0)
        {
            SpheneUI.Text.Colored("None pending", ImGuiColors.ParsedGreen);
        }
        else
        {
            if (ImGui.BeginTable("PendingAckTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 160);
                ImGui.TableSetupColumn("Ack ID", ImGuiTableColumnFlags.WidthFixed, 220);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                
                foreach (var kvp in pendingAcks)
                {
                    var userKey = kvp.Key;
                    var ackId = kvp.Value;
                    
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain(userKey);
                    
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain(ackId.Length > 16 ? $"{ackId[..16]}..." : ackId);
                    
                    ImGui.TableNextColumn();
                    
                    ImGui.PushID($"ack_clear_{userKey}");
                    if (SpheneUI.Controls.Button("Clear"))
                    {
                        var removed = _sessionAcknowledgmentManager.RemovePendingAcknowledgment(new Sphene.API.Data.UserData(userKey), ackId);
                        if (removed)
                        {
                            SpheneUI.AttachTooltip("Cleared");
                        }
                    }
                    ImGui.PopID();
                    
                    ImGui.SameLine();
                    ImGui.PushID($"ack_timeout_{userKey}");
                    if (SpheneUI.Controls.Button("Mark Timeout"))
                    {
                        _sessionAcknowledgmentManager.ProcessTimeoutAcknowledgment(ackId);
                    }
                    ImGui.PopID();
                }
                
                ImGui.EndTable();
            }
        }
    }
    
    // Draws the detailed metrics section
    private void DrawDetailedMetrics()
    {
        SpheneUI.Text.SectionHeader("Detailed Metrics");
        
        // Error breakdown
        if (_metrics.ErrorCounts.Any())
        {
            SpheneUI.Text.Plain("Error Breakdown:");
            foreach (var error in _metrics.ErrorCounts.OrderByDescending(kvp => kvp.Value))
            {
                SpheneUI.Text.Bullet($"{error.Key}: {error.Value}");
            }
        }
        
        SpheneUI.Layout.VerticalSpace();
        
        // Priority breakdown
        if (_metrics.PriorityCounts.Any())
        {
            SpheneUI.Text.Plain("Priority Distribution:");
            foreach (var priority in _metrics.PriorityCounts.OrderByDescending(kvp => kvp.Value))
            {
                var color = priority.Key switch
                {
                    AcknowledgmentPriority.High => ImGuiColors.DalamudRed,
                    AcknowledgmentPriority.Medium => ImGuiColors.DalamudYellow,
                    AcknowledgmentPriority.Low => ImGuiColors.ParsedGreen,
                    _ => ImGuiColors.DalamudWhite
                };
                
                SpheneUI.Text.Colored($"● {priority.Key}: {priority.Value}", color);
            }
        }
        
        SpheneUI.Text.Plain($"Last Updated: {_metrics.LastUpdated:HH:mm:ss}");
    }
    
    // Draws the configuration section
    private void DrawConfiguration()
    {
        SpheneUI.Text.SectionHeader("Configuration");
        
        // Basic settings
        var enableBatching = _config.EnableBatching;
        if (SpheneUI.Controls.ToggleChip("Enable Batching", ref enableBatching))
        {
            _config.EnableBatching = enableBatching;
        }
        SpheneUI.AttachTooltip("Group multiple acknowledgments together for better performance");
        
        ImGui.SameLine();
        var enableAutoRetry = _config.EnableAutoRetry;
        if (SpheneUI.Controls.ToggleChip("Auto Retry", ref enableAutoRetry))
        {
            _config.EnableAutoRetry = enableAutoRetry;
        }
        SpheneUI.AttachTooltip("Automatically retry failed acknowledgments");
        

        
        // Timeout settings
        var defaultTimeout = _config.DefaultTimeoutSeconds;
        if (SpheneUI.Controls.SliderInt("Default Timeout (s)", ref defaultTimeout, 5, 120))
        {
            _config.DefaultTimeoutSeconds = defaultTimeout;
        }
        
        if (_config.EnableBatching)
        {
            var maxBatchSize = _config.MaxBatchSize;
            if (SpheneUI.Controls.SliderInt("Max Batch Size", ref maxBatchSize, 1, 50))
            {
                _config.MaxBatchSize = maxBatchSize;
            }
            
            var batchTimeoutMs = _config.BatchTimeoutMs;
            if (SpheneUI.Controls.SliderInt("Batch Timeout (ms)", ref batchTimeoutMs, 1000, 30000))
            {
                _config.BatchTimeoutMs = batchTimeoutMs;
            }
        }
        
        // Advanced settings toggle
        if (SpheneUI.Controls.Button(_showAdvancedSettings ? "Hide Advanced" : "Show Advanced"))
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
        SpheneUI.Layout.Separator();
        SpheneUI.Text.SectionHeader("Advanced Settings");
        
        // Priority system
        var enablePriority = _config.EnablePrioritySystem;
        if (SpheneUI.Controls.ToggleChip("Enable Priority System", ref enablePriority))
        {
            _config.EnablePrioritySystem = enablePriority;
        }
        
        if (_config.EnablePrioritySystem)
        {
            ImGui.Indent();
            
            var highTimeout = _config.HighPriorityTimeoutSeconds;
            if (SpheneUI.Controls.SliderInt("High Priority Timeout (s)", ref highTimeout, 1, 60))
            {
                _config.HighPriorityTimeoutSeconds = highTimeout;
            }
            
            var mediumTimeout = _config.MediumPriorityTimeoutSeconds;
            if (SpheneUI.Controls.SliderInt("Medium Priority Timeout (s)", ref mediumTimeout, 5, 120))
            {
                _config.MediumPriorityTimeoutSeconds = mediumTimeout;
            }
            
            var lowTimeout = _config.LowPriorityTimeoutSeconds;
            if (SpheneUI.Controls.SliderInt("Low Priority Timeout (s)", ref lowTimeout, 10, 300))
            {
                _config.LowPriorityTimeoutSeconds = lowTimeout;
            }
            
            ImGui.Unindent();
        }
        
        // Retry settings
        if (_config.EnableAutoRetry)
        {
            var maxRetries = _config.MaxRetryAttempts;
            if (SpheneUI.Controls.SliderInt("Max Retry Attempts", ref maxRetries, 0, 10))
            {
                _config.MaxRetryAttempts = maxRetries;
            }
            
            var baseDelay = _config.BaseRetryDelayMs;
            if (SpheneUI.Controls.SliderInt("Base Retry Delay (ms)", ref baseDelay, 100, 10000))
            {
                _config.BaseRetryDelayMs = baseDelay;
            }
            
            var maxDelay = _config.MaxRetryDelayMs;
            if (SpheneUI.Controls.SliderInt("Max Retry Delay (ms)", ref maxDelay, 1000, 60000))
            {
                _config.MaxRetryDelayMs = Math.Max(maxDelay, _config.BaseRetryDelayMs);
            }
        }
        
        // Cache settings
        var maxCacheSize = _config.MaxCacheSize;
        if (SpheneUI.Controls.SliderInt("Max Cache Size", ref maxCacheSize, 100, 10000))
        {
            _config.MaxCacheSize = maxCacheSize;
        }
        
        var cacheExpiration = _config.CacheExpirationMinutes;
        if (SpheneUI.Controls.SliderInt("Cache Expiration (min)", ref cacheExpiration, 5, 180))
        {
            _config.CacheExpirationMinutes = cacheExpiration;
        }
        

        
        // Performance settings
        var maxPending = _config.MaxPendingAcknowledgmentsPerUser;
        if (SpheneUI.Controls.SliderInt("Max Pending per User", ref maxPending, 10, 1000))
        {
            _config.MaxPendingAcknowledgmentsPerUser = maxPending;
        }
        
        var enableMetrics = _config.EnableMetrics;
        if (SpheneUI.Controls.ToggleChip("Enable Metrics Collection", ref enableMetrics))
        {
            _config.EnableMetrics = enableMetrics;
        }
        
        // Reset to defaults button
        SpheneUI.Layout.VerticalSpace();
        if (SpheneUI.Controls.Button("Reset to Defaults"))
        {
            _config = new AcknowledgmentConfiguration();
        }
        SpheneUI.AttachTooltip("Reset all settings to their default values");
    }
}
