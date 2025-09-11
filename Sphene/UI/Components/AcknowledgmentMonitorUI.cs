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
using Sphene.SpheneConfiguration;
using Sphene.API.Data;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sphene.FileCache;

namespace Sphene.UI.Components;

// UI component for monitoring and configuring the acknowledgment system
public class AcknowledgmentMonitorUI : WindowMediatorSubscriberBase
{
    private readonly ILogger<AcknowledgmentMonitorUI> _logger;
    private readonly EnhancedAcknowledgmentManager _acknowledgmentManager;
    private readonly SessionAcknowledgmentManager _sessionAcknowledgmentManager;
    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    private readonly AcknowledgmentConfigService _configService;
    private readonly VisibleUserDataDistributor _visibleUserDataDistributor;
    private readonly PairManager _pairManager;
    private readonly CharacterHashTracker _characterHashTracker;
    private readonly AcknowledgmentRequestSystem _acknowledgmentRequestSystem;
    private readonly FileCacheManager _fileCacheManager;
    private AcknowledgmentMetrics _metrics;

    private bool _showMetricsDetails = false;
    private bool _showSessionDetails = false;

    private bool _showUserHashMapping = false;
    
    public AcknowledgmentMonitorUI(ILogger<AcknowledgmentMonitorUI> logger, EnhancedAcknowledgmentManager acknowledgmentManager, 
        SessionAcknowledgmentManager sessionAcknowledgmentManager, UiSharedService uiSharedService, SpheneMediator mediator, PerformanceCollectorService performanceCollectorService, ApiController apiController, AcknowledgmentConfigService configService, VisibleUserDataDistributor visibleUserDataDistributor, PairManager pairManager, CharacterHashTracker characterHashTracker, AcknowledgmentRequestSystem acknowledgmentRequestSystem, FileCacheManager fileCacheManager)
        : base(logger, mediator, "Acknowledgment Monitor###SpheneAckMonitor", performanceCollectorService)
    {
        _logger = logger;
        _acknowledgmentManager = acknowledgmentManager;
        _sessionAcknowledgmentManager = sessionAcknowledgmentManager;
        _uiSharedService = uiSharedService;
        _apiController = apiController;
        _configService = configService;
        _visibleUserDataDistributor = visibleUserDataDistributor;
        _pairManager = pairManager;
        _characterHashTracker = characterHashTracker;
        _acknowledgmentRequestSystem = acknowledgmentRequestSystem;
        _fileCacheManager = fileCacheManager;
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
            DrawCharacterHashStatus();
            ImGui.Separator();
            

            
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
            

            

            
            if (_showUserHashMapping)
            {
                ImGui.Separator();
                DrawUserHashMapping();
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
    

    

    

    

    

    
    // Draws character hash status section with yellow indicators for potentially outdated hashes
    private void DrawCharacterHashStatus()
    {
        ImGui.Text("Character Hash Status");
        
        // Current player hash from VisibleUserDataDistributor (more accurate)
        var currentHash = _visibleUserDataDistributor.GetMyCurrentCharacterHash();
        if (!string.IsNullOrEmpty(currentHash))
        {
            ImGui.Text($"Current Hash: {currentHash[..8]}...");
            UiSharedService.AttachToolTip($"Full Hash: {currentHash}");
            
            // Add button to copy full hash to clipboard
            ImGui.SameLine();
            if (ImGui.Button("Copy Full Hash"))
            {
                ImGui.SetClipboardText(currentHash);
                _logger.LogInformation("Character data hash copied to clipboard: {hash}", currentHash);
            }
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "No hash available");
        }
        
        // Hash change detection status
        var hasChanged = _characterHashTracker.HasHashChanged("current_player", currentHash);
        var changeColor = hasChanged ? ImGuiColors.DalamudYellow : ImGuiColors.ParsedGreen;
        ImGui.TextColored(changeColor, $"Hash Changed: {(hasChanged ? "Yes" : "No")}");
        
        // Show/hide user hash mapping button
        ImGui.SameLine();
        if (ImGui.Button(_showUserHashMapping ? "Hide User Hash Mapping" : "Show User Hash Mapping"))
        {
            _showUserHashMapping = !_showUserHashMapping;
        }
        
        // Acknowledgment request status
        var pendingRequests = _acknowledgmentRequestSystem.GetPendingRequestCount();
        var requestColor = pendingRequests > 0 ? ImGuiColors.DalamudYellow : ImGuiColors.ParsedGreen;
        ImGui.TextColored(requestColor, $"Pending Hash Requests: {pendingRequests}");
        
        // Pending visibility requests status
        var pendingVisibilityRequests = _acknowledgmentRequestSystem.GetPendingVisibilityRequestCount();
        var visibilityColor = pendingVisibilityRequests > 0 ? ImGuiColors.DalamudOrange : ImGuiColors.ParsedGreen;
        ImGui.TextColored(visibilityColor, $"Pending Visibility Checks: {pendingVisibilityRequests}");
        

    }
    

    
    // Draws user hash mapping information showing which hash each user has for Penumbra collections
    private void DrawUserHashMapping()
    {
        ImGui.Text("User Hash Mapping)");
        
        var visibleUsers = _pairManager.GetVisibleUsers();
        
        if (visibleUsers.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No visible users to display hash mapping for");
            return;
        }
        
        if (ImGui.BeginTable("UserHashMapping", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Sent Hash", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Received Hash", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Last Sent", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Last Received", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableHeadersRow();
            
            foreach (var user in visibleUsers)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(user.AliasOrUID);
                
                // Sent Hash column
                ImGui.TableNextColumn();
                var sentHash = _visibleUserDataDistributor.GetLastSentHashForUser(user);
                if (!string.IsNullOrEmpty(sentHash))
                {
                    ImGui.Text($"{sentHash[..8]}...");
                    UiSharedService.AttachToolTip($"Full Sent Hash: {sentHash}");
                }
                else
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "No hash");
                }
                
                // Received Hash column
                ImGui.TableNextColumn();
                var receivedHash = _visibleUserDataDistributor.GetLastReceivedHashForUser(user);
                
                if (!string.IsNullOrEmpty(receivedHash))
                {
                    ImGui.Text($"{receivedHash[..8]}...");
                    UiSharedService.AttachToolTip($"Full Received Hash: {receivedHash}");
                }
                else
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "No hash");
                }
                
                
                // Last Sent column
                ImGui.TableNextColumn();
                var lastSentTime = _visibleUserDataDistributor.GetLastSentTimeForUser(user);
                if (lastSentTime.HasValue)
                {
                    var timeDiff = DateTime.UtcNow - lastSentTime.Value;
                    if (timeDiff.TotalMinutes < 1)
                    {
                        ImGui.TextColored(ImGuiColors.ParsedGreen, "Just now");
                    }
                    else if (timeDiff.TotalMinutes < 60)
                    {
                        ImGui.Text($"{timeDiff.TotalMinutes:F0}m ago");
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudYellow, $"{timeDiff.TotalHours:F0}h ago");
                    }
                }
                else
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Never");
                }
                
                // Last Received column
                ImGui.TableNextColumn();
                var lastReceivedTime = _visibleUserDataDistributor.GetLastReceivedTimeForUser(user);
                if (lastReceivedTime.HasValue)
                {
                    var timeDiff = DateTime.UtcNow - lastReceivedTime.Value;
                    if (timeDiff.TotalMinutes < 1)
                    {
                        ImGui.TextColored(ImGuiColors.ParsedGreen, "Just now");
                    }
                    else if (timeDiff.TotalMinutes < 60)
                    {
                        ImGui.Text($"{timeDiff.TotalMinutes:F0}m ago");
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.DalamudYellow, $"{timeDiff.TotalHours:F0}h ago");
                    }
                }
                else
                {
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Never");
                }
            }
            
            ImGui.EndTable();
        }
        
        ImGui.Spacing();
        
        // Action buttons
        if (ImGui.Button("Refresh All Hashes"))
        {
            foreach (var user in visibleUsers)
            {
                _ = _acknowledgmentRequestSystem.SendAcknowledgmentRequestAsync(user.UID, _characterHashTracker.GetCurrentPlayerHash());
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Clear Hash Cache"))
        {
            _visibleUserDataDistributor.ClearAllUserHashCaches();
        }
        
        ImGui.Spacing();
        
        // Debug information for received hashes
        var allReceivedHashes = _visibleUserDataDistributor.GetAllTrackedReceivedCharacterHashes();
        ImGui.TextColored(ImGuiColors.DalamudYellow, $"Debug: Total received hashes tracked: {allReceivedHashes.Count}");
        if (allReceivedHashes.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Received hashes:");
            foreach (var kvp in allReceivedHashes)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"  {kvp.Key.AliasOrUID}: {kvp.Value[..Math.Min(8, kvp.Value.Length)]}");
            }
        }
        
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "This shows which character data hash each user has stored for your Penumbra collection.");
    }
}