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
    private readonly Dictionary<string, CharacterStatsSnapshot> _characterStats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _activePenumbraPathsByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _activePenumbraPathsUpdatedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activePenumbraPathsErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activePenumbraRefreshInProgress = new(StringComparer.Ordinal);
    private bool _hideLikelyDefaultGamePaths = true;
    
    public StatusDebugUi(ILogger<StatusDebugUi> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, PairManager pairManager, ApiController apiController,
        ConnectionHealthMonitor healthMonitor, CircuitBreakerService circuitBreaker,
        SpheneConfigService configService,
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
        _configService = configService;
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

    private void DrawPenumbraCollectionDebugComparison(Pair selectedPair)
    {
        ImGui.Text("Penumbra Collection Comparison");

        var uid = selectedPair.UserData.UID;
        var collectionId = selectedPair.GetPenumbraCollectionId();
        ImGui.TextUnformatted($"Collection ID: {(collectionId == Guid.Empty ? "(none)" : collectionId.ToString())}");

        var isRefreshing = _activePenumbraRefreshInProgress.Contains(uid);
        if (ImGui.Button($"Refresh Active Paths##refresh_active_paths_{uid}") && !isRefreshing)
        {
            _ = RefreshPenumbraActivePathsAsync(selectedPair);
        }

        ImGui.SameLine();
        if (isRefreshing)
        {
            UiSharedService.ColorText("Refreshing active paths...", ImGuiColors.DalamudYellow);
        }
        else if (_activePenumbraPathsUpdatedAt.TryGetValue(uid, out var updatedAt))
        {
            ImGui.TextUnformatted($"Last refresh: {updatedAt.ToLocalTime():HH:mm:ss}");
        }
        else
        {
            ImGui.TextUnformatted("Active paths not refreshed yet.");
        }

        if (_activePenumbraPathsErrors.TryGetValue(uid, out var error) && !string.IsNullOrEmpty(error))
        {
            UiSharedService.ColorTextWrapped($"Refresh error: {error}", ImGuiColors.DalamudRed);
        }

        var deliveredGroups = BuildDeliveredHashGroupState(selectedPair.LastReceivedCharacterData);
        var loadedPaths = NormalizePathMap(selectedPair.GetLoadedCollectionPathsSnapshot());
        _activePenumbraPathsByUid.TryGetValue(uid, out var activePathsRaw);
        activePathsRaw ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var activePaths = NormalizePathMap(activePathsRaw);

        HashSet<string> deliveredGamePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var deliveredGroup in deliveredGroups)
        {
            foreach (var gamePath in deliveredGroup.GamePaths)
            {
                deliveredGamePaths.Add(gamePath);
            }
        }

        HashSet<string> nonDeliveredPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in loadedPaths.Keys)
        {
            if (!deliveredGamePaths.Contains(item))
            {
                nonDeliveredPaths.Add(item);
            }
        }

        foreach (var item in activePaths.Keys)
        {
            if (!deliveredGamePaths.Contains(item))
            {
                nonDeliveredPaths.Add(item);
            }
        }

        if (deliveredGroups.Count == 0 && nonDeliveredPaths.Count == 0)
        {
            ImGui.TextUnformatted("No paths available for comparison.");
            return;
        }

        UiSharedService.ColorTextWrapped(
            "Hint: If a path is active in Penumbra but was not delivered (no IsActive from remote character data), this is most likely the standard game path and is treated as OK.",
            ImGuiColors.DalamudGrey);

        ImGui.Checkbox("Hide likely standard game paths", ref _hideLikelyDefaultGamePaths);

        var sortedNonDeliveredPaths = nonDeliveredPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var likelyDefaultCount = 0;
        foreach (var gamePath in sortedNonDeliveredPaths)
        {
            activePaths.TryGetValue(gamePath, out var activeSource);
            var isPenumbraActive = !string.IsNullOrEmpty(activeSource);
            var isLikelyDefaultGamePath = isPenumbraActive;
            if (isLikelyDefaultGamePath)
            {
                likelyDefaultCount++;
            }
        }

        var visibleNonDeliveredPaths = _hideLikelyDefaultGamePaths
            ? sortedNonDeliveredPaths.Where(gamePath =>
            {
                activePaths.TryGetValue(gamePath, out var activeSource);
                var isPenumbraActive = !string.IsNullOrEmpty(activeSource);
                return !isPenumbraActive;
            }).ToList()
            : sortedNonDeliveredPaths;

        var totalVisibleRows = deliveredGroups.Count + visibleNonDeliveredPaths.Count;
        ImGui.TextUnformatted($"Rows: {totalVisibleRows} | Delivered Hash Groups: {deliveredGroups.Count} | Loaded: {loadedPaths.Count} | Active: {activePaths.Count}");
        ImGui.TextUnformatted($"Likely standard game paths: {likelyDefaultCount} | Visible non-delivered rows: {visibleNonDeliveredPaths.Count}");
        UiSharedService.ColorTextWrapped("Delivered entries that share the same hash are merged, even if they target different game paths.", ImGuiColors.DalamudGrey);

        if (!ImGui.BeginTable("PenumbraPathCompareTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new Vector2(-1, 320)))
        {
            return;
        }

        ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Delivered", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Loaded", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Pen Active", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Data Flag", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Match", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableHeadersRow();

        foreach (var deliveredGroup in deliveredGroups)
        {
            var loadedSources = CollectPathValues(loadedPaths, deliveredGroup.GamePaths);
            var activeSources = CollectPathValues(activePaths, deliveredGroup.GamePaths);

            var isPenumbraActive = activeSources.Count > 0;
            var isDataFlagActive = deliveredGroup.IsActive;
            var activeMatchesFlag = isPenumbraActive == isDataFlagActive;

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawStackedValues(deliveredGroup.GamePaths, 58);

            ImGui.TableSetColumnIndex(1);
            DrawDeliveredSourcesStacked(deliveredGroup.SourcePaths, 42);

            ImGui.TableSetColumnIndex(2);
            DrawStackedValues(loadedSources, 42);

            ImGui.TableSetColumnIndex(3);
            DrawStackedValues(activeSources, 42);

            ImGui.TableSetColumnIndex(4);
            UiSharedService.ColorText(isPenumbraActive ? "Yes" : "No", isPenumbraActive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(5);
            UiSharedService.ColorText(isDataFlagActive ? "Yes" : "No", isDataFlagActive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(6);
            UiSharedService.ColorText(activeMatchesFlag ? "OK" : "Diff", activeMatchesFlag ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow);
        }

        foreach (var gamePath in visibleNonDeliveredPaths)
        {
            loadedPaths.TryGetValue(gamePath, out var loadedSource);
            activePaths.TryGetValue(gamePath, out var activeSource);

            var isPenumbraActive = !string.IsNullOrEmpty(activeSource);
            var isLikelyDefaultGamePath = isPenumbraActive;

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawTrimmedPathWithTooltip(gamePath, 58);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted("-");

            ImGui.TableSetColumnIndex(2);
            DrawTrimmedPathWithTooltip(string.IsNullOrEmpty(loadedSource) ? "-" : loadedSource, 42);

            ImGui.TableSetColumnIndex(3);
            DrawTrimmedPathWithTooltip(string.IsNullOrEmpty(activeSource) ? "-" : activeSource, 42);

            ImGui.TableSetColumnIndex(4);
            UiSharedService.ColorText(isPenumbraActive ? "Yes" : "No", isPenumbraActive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(5);
            UiSharedService.ColorText("No", ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(6);
            UiSharedService.ColorText(isLikelyDefaultGamePath ? "Likely Default" : "-", ImGuiColors.DalamudGrey);
        }

        ImGui.EndTable();
    }

    private static List<DeliveredHashGroupState> BuildDeliveredHashGroupState(Sphene.API.Data.CharacterData? characterData)
    {
        Dictionary<string, DeliveredHashGroupState> delivered = new(StringComparer.OrdinalIgnoreCase);
        if (characterData == null)
        {
            return [];
        }

        if (!characterData.FileReplacements.TryGetValue(API.Data.Enum.ObjectKind.Player, out var replacements) || replacements == null)
        {
            return [];
        }

        foreach (var replacement in replacements)
        {
            var groupKey = GetDeliveredSourceKey(replacement);
            if (!delivered.TryGetValue(groupKey, out var group))
            {
                group = new DeliveredHashGroupState(groupKey);
                delivered[groupKey] = group;
            }

            if (!group.SourcePaths.Contains(groupKey, StringComparer.OrdinalIgnoreCase))
            {
                group.SourcePaths.Add(groupKey);
            }

            group.IsActive |= replacement.IsActive;

            foreach (var gamePath in replacement.GamePaths)
            {
                var normalizedGamePath = NormalizeGamePath(gamePath);
                if (string.IsNullOrEmpty(normalizedGamePath))
                {
                    continue;
                }

                if (!group.GamePaths.Contains(normalizedGamePath, StringComparer.OrdinalIgnoreCase))
                {
                    group.GamePaths.Add(normalizedGamePath);
                }
            }
        }

        return delivered.Values
            .OrderBy(g => g.GamePaths.Count > 0 ? g.GamePaths[0] : g.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> CollectPathValues(IReadOnlyDictionary<string, string> valuesByPath, IReadOnlyList<string> gamePaths)
    {
        List<string> values = [];
        foreach (var gamePath in gamePaths)
        {
            if (!valuesByPath.TryGetValue(gamePath, out var value) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static Dictionary<string, string> NormalizePathMap(IReadOnlyDictionary<string, string> paths)
    {
        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var normalizedKey = NormalizeGamePath(path.Key);
            if (string.IsNullOrEmpty(normalizedKey))
            {
                continue;
            }

            if (!normalized.TryGetValue(normalizedKey, out var existingValue) || string.IsNullOrEmpty(existingValue))
            {
                normalized[normalizedKey] = path.Value;
            }
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

        normalized = normalized.Trim('/');
        return normalized;
    }

    private static string GetDeliveredSourceKey(FileReplacementData replacement)
    {
        var hash = NormalizeHash(replacement.Hash);
        if (!string.IsNullOrEmpty(hash))
        {
            return hash;
        }

        var fileSwapPath = replacement.FileSwapPath?.Trim();
        if (!string.IsNullOrEmpty(fileSwapPath))
        {
            return fileSwapPath;
        }

        return "-";
    }

    private static string NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        var normalized = hash.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return normalized.ToUpperInvariant();
    }

    private static void DrawDeliveredSourcesStacked(IReadOnlyList<string> sourcePaths, int maxLength)
    {
        if (sourcePaths.Count == 0)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        for (var index = 0; index < sourcePaths.Count; index++)
        {
            var sourcePath = sourcePaths[index];
            var trimmed = TrimPath(sourcePath, maxLength);

            if (sourcePaths.Count > 1)
            {
                var color = GetDeliveredSourceColor(index);
                UiSharedService.ColorText($"[{index + 1}] {trimmed}", color);
            }
            else
            {
                ImGui.TextUnformatted(trimmed);
            }

            if (ImGui.IsItemHovered())
            {
                UiSharedService.AttachToolTip(sourcePath);
            }
        }
    }

    private static void DrawStackedValues(IReadOnlyList<string> values, int maxLength)
    {
        if (values.Count == 0)
        {
            ImGui.TextUnformatted("-");
            return;
        }

        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var trimmed = TrimPath(value, maxLength);
            ImGui.TextUnformatted(trimmed);
            if (ImGui.IsItemHovered())
            {
                UiSharedService.AttachToolTip(value);
            }
        }
    }

    private static string TrimPath(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "-";
        }

        return path.Length > maxLength ? "..." + path[^Math.Max(1, maxLength - 3)..] : path;
    }

    private static Vector4 GetDeliveredSourceColor(int index)
    {
        return (index % 6) switch
        {
            0 => new Vector4(0.52f, 0.85f, 1.00f, 1.00f),
            1 => new Vector4(0.65f, 1.00f, 0.68f, 1.00f),
            2 => new Vector4(1.00f, 0.85f, 0.55f, 1.00f),
            3 => new Vector4(1.00f, 0.64f, 0.78f, 1.00f),
            4 => new Vector4(0.90f, 0.76f, 1.00f, 1.00f),
            _ => new Vector4(0.75f, 0.90f, 0.90f, 1.00f),
        };
    }

    private static void DrawTrimmedPathWithTooltip(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path))
        {
            ImGui.TextUnformatted("-");
            return;
        }

        var trimmed = TrimPath(path, maxLength);
        ImGui.TextUnformatted(trimmed);
        if (ImGui.IsItemHovered())
        {
            UiSharedService.AttachToolTip(path);
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
            var activePaths = await pair.GetCurrentPenumbraActivePathsByGamePathAsync().ConfigureAwait(false);
            _activePenumbraPathsByUid[uid] = activePaths;
            _activePenumbraPathsUpdatedAt[uid] = DateTimeOffset.UtcNow;
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

        // Overview Tab - Consolidated status and health info
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

        // Character Data Tab - Combined character debug and statistics
        using (var characterTab = ImRaii.TabItem("Character Data"))
        {
            if (characterTab)
            {
                DrawCharacterDataCombined();
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
    
    private void DrawCharacterDataCombined()
    {
        var pairs = _pairManager.DirectPairs.ToList();
        if (pairs.Count == 0)
        {
            ImGui.Text("No paired users");
            return;
        }
        if (ImGui.BeginCombo("##character_select", pairs.FirstOrDefault(p => string.Equals(p.UserData.UID, _selectedCharacterDebugUid, StringComparison.Ordinal))?.UserData.AliasOrUID ?? "Select..."))
        {
            foreach (var pair in pairs)
            {
                var isSelected = string.Equals(pair.UserData.UID, _selectedCharacterDebugUid, StringComparison.Ordinal);
                if (ImGui.Selectable(pair.UserData.AliasOrUID, isSelected))
                {
                    _selectedCharacterDebugUid = pair.UserData.UID;
                }
                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();

        // Find selected pair
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

        // Tab bar within character data for different views
        using var characterTabBar = ImRaii.TabBar("CharacterDataTabs");
        if (!characterTabBar) return;

        // Statistics Tab
        using (var statsTab = ImRaii.TabItem("Statistics"))
        {
            if (statsTab)
            {
                DrawCharacterStatisticsSection(selectedPair, pairs);
            }
        }

        // Debug Logs Tab
        using (var debugTab = ImRaii.TabItem("Debug Logs"))
        {
            if (debugTab)
            {
                DrawCharacterDebugLogsSection(selectedPair);
            }
        }

        // Penumbra Comparison Tab
        using (var penumbraTab = ImRaii.TabItem("Penumbra Comparison"))
        {
            if (penumbraTab)
            {
                DrawPenumbraCollectionDebugComparison(selectedPair);
            }
        }

        // Raw Data Tab
        using (var rawDataTab = ImRaii.TabItem("Raw Data"))
        {
            if (rawDataTab)
            {
                DrawRawCharacterDataSection(selectedPair);
            }
        }
    }

    private void DrawCharacterStatisticsSection(Pair selectedPair, List<Pair> allPairs)
    {
        ImGui.Text("Character Statistics Overview");
        
        // Summary table for all characters
        if (ImGui.BeginTable("CharacterStatsSummaryTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Hash", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Last Action", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Visibility", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Ack Status", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableHeadersRow();

            foreach (var pair in allPairs)
            {
                var snapshot = GetCharacterStatsSnapshot(pair);
                var isSelected = string.Equals(pair.UserData.UID, _selectedCharacterDebugUid, StringComparison.Ordinal);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable(pair.UserData.AliasOrUID, isSelected))
                {
                    _selectedCharacterDebugUid = pair.UserData.UID;
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(snapshot.Current.DataHash.Length > 12 ? snapshot.Current.DataHash[..12] + "..." : snapshot.Current.DataHash);

                ImGui.TableSetColumnIndex(2);
                var statusColor = pair.IndividualPairStatus switch
                {
                    API.Data.Enum.IndividualPairStatus.Bidirectional => ImGuiColors.HealerGreen,
                    API.Data.Enum.IndividualPairStatus.OneSided => ImGuiColors.DalamudYellow,
                    _ => ImGuiColors.DalamudRed
                };
                UiSharedService.ColorText(pair.IndividualPairStatus.ToString(), statusColor);

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(snapshot.Current.LastAction.Length > 20 ? snapshot.Current.LastAction[..20] + "..." : snapshot.Current.LastAction);

                ImGui.TableSetColumnIndex(4);
                var visibilityText = snapshot.Current.IsVisible ? "Visible" : "Hidden";
                var visibilityColor = snapshot.Current.IsVisible ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed;
                UiSharedService.ColorText(visibilityText, visibilityColor);

                ImGui.TableSetColumnIndex(5);
                var ackText = snapshot.Current.LastAckSuccess == true ? "✓" : snapshot.Current.LastAckSuccess == false ? "✗" : "?";
                var ackColor = snapshot.Current.LastAckSuccess == true ? ImGuiColors.HealerGreen : snapshot.Current.LastAckSuccess == false ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
                UiSharedService.ColorText(ackText, ackColor);
            }

            ImGui.EndTable();
        }

        ImGui.Separator();

        // Detailed stats for selected character
        var selectedSnapshot = GetCharacterStatsSnapshot(selectedPair);
        ImGui.Text($"Detailed Statistics for {selectedPair.UserData.AliasOrUID}:");

        if (ImGui.BeginTable("CharacterStatsDetailTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            DrawStatsRow("Data Hash", selectedSnapshot.Current.DataHash);
            DrawStatsRow("Applied Bytes", $"{selectedSnapshot.Current.AppliedBytes:N0}");
            DrawStatsRow("Applied Triangles", $"{selectedSnapshot.Current.AppliedTris:N0}");
            DrawStatsRow("Applied VRAM Bytes", $"{selectedSnapshot.Current.AppliedVramBytes:N0}");
            DrawStatsRow("Visibility", selectedSnapshot.Current.IsVisible ? "Visible" : "Hidden");
            DrawStatsRow("Mutual Visibility", selectedSnapshot.Current.IsMutuallyVisible ? "Yes" : "No");
            DrawStatsRow("Paused", selectedSnapshot.Current.IsPaused ? "Yes" : "No");
            DrawStatsRow("Last Ack Success", selectedSnapshot.Current.LastAckSuccess?.ToString() ?? "Unknown");
            DrawStatsRow("Last Ack Time", selectedSnapshot.Current.LastAckTime?.ToLocalTime().ToString("HH:mm:ss") ?? "Never");
            DrawStatsRow("Retry Count", selectedSnapshot.Current.ApplyRetryCount.ToString());
            DrawStatsRow("Last Action", selectedSnapshot.Current.LastAction);
            DrawStatsRow("Snapshot Time", selectedSnapshot.Current.SnapshotTime.ToLocalTime().ToString("HH:mm:ss"));

            ImGui.EndTable();
        }

        ImGui.Separator();
        
        // Legacy Check section
        DrawLegacyCheckForCharacter(selectedPair);
    }

    private static void DrawCharacterDebugLogsSection(Pair selectedPair)
    {
        ImGui.Text("Debug Logs");
        
        // Action buttons
        if (ImGui.Button("Copy Logs"))
        {
            ImGui.SetClipboardText(GetApplyDebugText(selectedPair));
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Clear Logs"))
        {
            selectedPair.ClearApplyDebug();
        }

        ImGui.Separator();

        var logText = GetApplyDebugText(selectedPair);
        var availableSize = ImGui.GetContentRegionAvail();
        using var child = ImRaii.Child("CharacterDebugLogContent", availableSize, true);
        if (child)
        {
            ImGui.TextUnformatted(logText);
        }
    }

    private void DrawRawCharacterDataSection(Pair selectedPair)
    {
        ImGui.Text("Raw Character Data");
        
        var receivedData = selectedPair.LastReceivedCharacterData;
        var receivedDataJson = receivedData != null
            ? JsonSerializer.Serialize(receivedData, new JsonSerializerOptions() { WriteIndented = true })
            : "No received character data.";
        
        var lastReceivedHash = selectedPair.LastReceivedCharacterDataHash ?? "-";
        var previousReceivedHash = selectedPair.PreviousReceivedCharacterDataHash ?? "-";
        var lastReceivedTime = selectedPair.LastReceivedCharacterDataTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        var lastChangeTime = selectedPair.LastReceivedCharacterDataChangeTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

        ImGui.Text($"Last Received Hash: {lastReceivedHash}");
        ImGui.Text($"Previous Received Hash: {previousReceivedHash}");
        ImGui.Text($"Last Received Time: {lastReceivedTime}");
        ImGui.Text($"Last Change Time: {lastChangeTime}");
        ImGui.Separator();

        if (ImGui.Button("Copy JSON Data") && receivedData != null)
        {
            ImGui.SetClipboardText(receivedDataJson);
        }

        ImGui.Separator();

        var availableSize = ImGui.GetContentRegionAvail();
        using var child = ImRaii.Child("RawCharacterDataContent", availableSize, true);
        if (child)
        {
            ImGui.TextUnformatted(receivedDataJson);
        }
    }

    private void DrawLegacyCheckForCharacter(Pair selectedPair)
    {
        ImGui.Text("Legacy Shader Check");
        ImGui.Separator();

        var data = selectedPair.LastReceivedCharacterData;
        if (data == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No character data available");
            return;
        }

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
                        details.Add($"Swap Path: {replacement.FileSwapPath}");
                    }
                    foreach (var gamePath in replacement.GamePaths)
                    {
                        if (gamePath.EndsWith("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            details.Add($"Game Path: {gamePath}");
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
                UiSharedService.ColorText("LEGACY SHADER FOUND (Filtered)", ImGuiColors.DalamudYellow);
                ImGui.TextWrapped("Inbound filter is active: characterlegacy.shpk entries were blocked for apply/download.");
            }
            else
            {
                UiSharedService.ColorText("LEGACY SHADER FOUND", ImGuiColors.DalamudRed);
                ImGui.TextWrapped("Legacy shader detected in incoming data. Consider enabling the filter in settings.");
            }
            
            ImGui.Separator();
            ImGui.Text("Details:");
            foreach (var detail in details)
            {
                ImGui.TextWrapped($"• {detail}");
            }
        }
        else
        {
            UiSharedService.ColorText("CLEAN - No legacy shaders detected", ImGuiColors.HealerGreen);
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

    private static string GetApplyDebugText(Pair pair)
    {
        var lines = pair.GetApplyDebugLines();
        if (lines.Length == 0)
        {
            return "No debug logs for this character.";
        }

        return string.Join('\n', lines);
    }

    private static void DrawStatsRow(string metric, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.Text(metric);
        ImGui.TableSetColumnIndex(1);
        ImGui.Text(value);
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

    private sealed class DeliveredHashGroupState
    {
        public DeliveredHashGroupState(string groupKey)
        {
            GroupKey = groupKey;
        }

        public string GroupKey { get; }
        public List<string> GamePaths { get; } = [];
        public List<string> SourcePaths { get; } = [];
        public bool IsActive { get; set; }
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
}
