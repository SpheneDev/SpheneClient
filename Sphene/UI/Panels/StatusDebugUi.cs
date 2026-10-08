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
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class StatusDebugUi : WindowMediatorSubscriberBase
{
    private new readonly ILogger<StatusDebugUi> _logger;

    private enum StatusDebugPage
    {
        Summary = 0,
        Pairs = 1,
        CharacterData = 2,
        Acknowledgments = 3,
        Diagnostics = 4,
        Logs = 5,
    }

    private StatusDebugPage _page = StatusDebugPage.Summary;
    
    private readonly PairManager _pairManager;
    private readonly ApiController _apiController;
    private readonly ConnectionHealthMonitor _healthMonitor;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly SpheneConfigService _configService;
    private readonly EnhancedAcknowledgmentManager? _acknowledgmentManager;
    private readonly SessionAcknowledgmentManager? _sessionAcknowledgmentManager;
    
    private enum DebugLogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
    }

    private sealed record DebugLogEntry(DateTimeOffset Timestamp, DebugLogLevel Level, string Category, string? Uid, string Message, string? Details);

    private readonly Lock _debugLogLock = new();
    private readonly List<DebugLogEntry> _debugLogEntries = new();
    private const int MaxDebugLogEntries = 2500;
    private const long MaxDebugLogBytes = 50L * 1024L * 1024L;
    private long _debugLogBytes = 0;
    private string _lastReportPath = string.Empty;

    private bool _autoScroll = true;
    private DebugLogLevel _debugLogMinLevel = DebugLogLevel.Info;
    private string _debugLogSearch = string.Empty;
    private bool _debugLogSelectedCharacterOnly = false;
    private string _debugLogSpecificUid = string.Empty;
    private bool _debugLogAckContextFilterEnabled = false;
    private string _debugLogAckContextSessionId = string.Empty;
    private string _debugLogAckContextHashPrefix = string.Empty;
    private bool _showHealthChecks = true;
    private bool _showAcknowledgments = true;
    private bool _showCircuitBreaker = true;
    private bool _showConnections = true;
    private bool _showApplies = true;
    private bool _showHub = true;
    private bool _showNotifications = true;
    private bool _showDownloads = true;
    private bool _showMismatches = true;
    private bool _showIpc = true;
    private bool _showMinions = true;
    private bool _showMinionScd = true;
    private bool _includeApplyChangeDetails = false;
    private int _simulatedDisconnectSeconds = 3;
    private string? _selectedCharacterUid;
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
            MinimumSize = new Vector2(1050, 540),
            MaximumSize = new Vector2(1050, 800)
        };
        
        Flags = ImGuiWindowFlags.None;
        
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
        Mediator.Subscribe<CharacterDataApplicationCompletedMessage>(this, OnCharacterDataApplicationCompleted);
        Mediator.Subscribe<HubReconnectingMessage>(this, OnHubReconnecting);
        Mediator.Subscribe<HubReconnectedMessage>(this, OnHubReconnected);
        Mediator.Subscribe<HubClosedMessage>(this, OnHubClosed);
        Mediator.Subscribe<DebugLogEventMessage>(this, OnDebugLogEvent);
        Mediator.Subscribe<DownloadStartedMessage>(this, OnDownloadStarted);
        Mediator.Subscribe<DownloadFinishedMessage>(this, OnDownloadFinished);
        Mediator.Subscribe<DownloadReadyMessage>(this, OnDownloadReady);
        
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
        var directPairsCount = _pairManager.DirectPairs.Count;
        var inboundRequests = _pairManager.GetInboundIndividualPairRequestsSnapshot();
        var outboundRequests = _pairManager.GetOutboundIndividualPairRequestsSnapshot();
        var totalRequestsCount = inboundRequests.Count + outboundRequests.Count;
        var unseenInboundRequestCount = _pairManager.UnseenInboundIndividualPairRequestCount;
        var pendingAckCount = _sessionAcknowledgmentManager?.GetPendingAcknowledgments().Count ?? 0;

        var sidebarWidth = 210f * ImGuiHelpers.GlobalScale;
        if (SpheneUI.Layout.BeginScrollArea("##StatusDebugSidebar",
                new Vector2(sidebarWidth, ImGui.GetContentRegionAvail().Y), border: true))
        {
            DrawNavEntry(StatusDebugPage.Summary, FontAwesomeIcon.ChartLine, "Summary");
            DrawNavEntry(StatusDebugPage.Pairs, FontAwesomeIcon.UserFriends, "Pairs", directPairsCount, totalRequestsCount);
            DrawNavEntry(StatusDebugPage.CharacterData, FontAwesomeIcon.UserCog, "Character Data");
            DrawNavEntry(StatusDebugPage.Acknowledgments, FontAwesomeIcon.ClipboardCheck, "Acknowledgments", pendingAckCount);
            DrawNavEntry(StatusDebugPage.Diagnostics, FontAwesomeIcon.Stethoscope, "Diagnostics");
            DrawNavEntry(StatusDebugPage.Logs, FontAwesomeIcon.Stream, "Logs");
        }
        SpheneUI.Layout.EndScrollArea();

        ImGui.SameLine();
        if (SpheneUI.Layout.BeginScrollArea("##StatusDebugContent", ImGui.GetContentRegionAvail()))
        {
            switch (_page)
            {
                case StatusDebugPage.Summary:
                    DrawSummaryPage(unseenInboundRequestCount);
                    break;
                case StatusDebugPage.Pairs:
                    DrawPairsPage(inboundRequests, outboundRequests);
                    break;
                case StatusDebugPage.CharacterData:
                    DrawCharacterDataPage();
                    break;
                case StatusDebugPage.Acknowledgments:
                    DrawAcknowledgmentPage();
                    break;
                case StatusDebugPage.Diagnostics:
                    DrawDiagnosticsPage();
                    break;
                case StatusDebugPage.Logs:
                    DrawCommunicationLog();
                    break;
            }
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawNavEntry(StatusDebugPage page, FontAwesomeIcon icon, string label, int badgeA = 0, int badgeB = 0)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rowHeight = ImGui.GetFrameHeight() + 6f * scale;
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var cursor = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var selected = _page == page;

        ImGui.PushID((int)page);
        ImGui.InvisibleButton("##nav", new Vector2(rowWidth, rowHeight));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
        {
            _page = page;
        }
        var cursorAfter = ImGui.GetCursorPos();
        ImGui.PopID();

        var background = selected
            ? ImGui.GetColorU32(ImGuiCol.HeaderActive)
            : hovered
                ? ImGui.GetColorU32(ImGuiCol.HeaderHovered)
                : 0u;
        if (background != 0)
        {
            drawList.AddRectFilled(cursor, cursor + new Vector2(rowWidth, rowHeight), background, 6f * scale);
        }

        var paddingX = 10f * scale;
        var iconY = cursor.Y + (rowHeight - ImGui.GetFontSize()) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + paddingX, iconY));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            SpheneUI.Text.Plain(icon.ToIconString());
        }

        var textX = cursor.X + paddingX + 24f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textX, iconY));
        SpheneUI.Text.Plain(label);

        var badgeCursorX = cursor.X + rowWidth - paddingX;
        if (badgeB > 0)
        {
            badgeCursorX = DrawSidebarBadge(drawList, badgeCursorX, cursor.Y, rowHeight, badgeB, ImGuiColors.DalamudYellow, scale);
        }
        if (badgeA > 0)
        {
            _ = DrawSidebarBadge(drawList, badgeCursorX, cursor.Y, rowHeight, badgeA, ImGuiColors.HealerGreen, scale);
        }

        ImGui.SetCursorPos(cursorAfter);
    }

    private static float DrawSidebarBadge(ImDrawListPtr drawList, float rightEdgeX, float rowY, float rowHeight, int count, Vector4 color, float scale)
    {
        var text = count > 99 ? "99+" : count.ToString();
        Vector2 size;
        using (ImRaii.PushFont(UiBuilder.DefaultFont))
        {
            size = ImGui.CalcTextSize(text);
        }

        var padX = 5f * scale;
        var padY = 2f * scale;
        var badgeH = size.Y + padY * 2f;
        var badgeW = MathF.Max(badgeH, size.X + padX * 2f);
        var rounding = badgeH * 0.5f;
        var marginX = 6f * scale;
        var min = new Vector2(rightEdgeX - badgeW, rowY + (rowHeight - badgeH) * 0.5f);
        var max = new Vector2(min.X + badgeW, min.Y + badgeH);

        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(color), rounding);
        var textPos = new Vector2(min.X + (badgeW - size.X) / 2f, min.Y + (badgeH - size.Y) / 2f);
        using (ImRaii.PushFont(UiBuilder.DefaultFont))
        {
            drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), text);
        }

        return min.X - marginX;
    }

    private void DrawSummaryPage(int unseenInboundRequests)
    {
        if (ImGui.BeginTable("##SummaryTable", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 190f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

            DrawSummaryRow("Connection", () =>
            {
                if (_apiController.IsConnected)
                {
                    DrawStatusIcon(FontAwesomeIcon.CheckCircle, ImGuiColors.HealerGreen);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Connected", ImGuiColors.HealerGreen);
                }
                else
                {
                    DrawStatusIcon(FontAwesomeIcon.TimesCircle, ImGuiColors.DalamudRed);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Disconnected", ImGuiColors.DalamudRed);
                }
            });

            DrawSummaryRow("Server", () => SpheneUI.Text.Plain(_apiController.ServerInfo.ShardName ?? "Not connected"));
            DrawSummaryRow("User UID", () => SpheneUI.Text.Plain(_apiController.UID ?? "Not logged in"));

            var healthStatus = _healthMonitor.GetHealthStatus();
            DrawSummaryRow("Health", () =>
            {
                if (healthStatus.IsHealthy)
                {
                    DrawStatusIcon(FontAwesomeIcon.Heartbeat, ImGuiColors.HealerGreen);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Healthy", ImGuiColors.HealerGreen);
                }
                else
                {
                    DrawStatusIcon(FontAwesomeIcon.Heartbeat, ImGuiColors.DalamudRed);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Unhealthy", ImGuiColors.DalamudRed);
                }
                ImGui.SameLine();
                SpheneUI.Text.Plain($"({healthStatus.ConsecutiveFailures} failures)");
            });

            var circuitStatus = _circuitBreaker.GetStatus();
            DrawSummaryRow("Circuit Breaker", () =>
            {
                var stateColor = circuitStatus.State switch
                {
                    CircuitBreakerState.Closed => ImGuiColors.HealerGreen,
                    CircuitBreakerState.HalfOpen => ImGuiColors.DalamudYellow,
                    CircuitBreakerState.Open => ImGuiColors.DalamudRed,
                    _ => ImGuiColors.DalamudWhite
                };
                SpheneUI.Text.Colored(circuitStatus.State.ToString(), stateColor);
                ImGui.SameLine();
                SpheneUI.Text.Plain($"(failures={circuitStatus.FailureCount})");
            });

            DrawSummaryRow("Pairs", () => SpheneUI.Text.Plain(_pairManager.DirectPairs.Count.ToString()));
            DrawSummaryRow("Pair Requests", () =>
            {
                var incoming = _pairManager.GetInboundIndividualPairRequestsSnapshot().Count;
                var outgoing = _pairManager.GetOutboundIndividualPairRequestsSnapshot().Count;
                SpheneUI.Text.Plain($"incoming={incoming}, outgoing={outgoing}, new={unseenInboundRequests}");
            });

            ImGui.EndTable();
        }

        ImGui.Spacing();

        if (SpheneUI.Controls.CollapsibleSection("Actions", defaultOpen: true))
        {
            DrawControlButtons();
        }

        ImGui.Spacing();

        if (SpheneUI.Controls.CollapsibleSection("Health Details"))
        {
            DrawHealthMonitoringSection();
        }
    }

    private static void DrawSummaryRow(string key, Action drawValue)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        SpheneUI.Text.Plain(key);
        ImGui.TableSetColumnIndex(1);
        drawValue();
    }

    private static void DrawStatusIcon(FontAwesomeIcon icon, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            SpheneUI.Text.Plain(icon.ToIconString());
        }
    }

    private static string GetPairKey(Pair pair)
    {
        return !string.IsNullOrWhiteSpace(pair.UserData.UID) ? pair.UserData.UID : pair.UserData.AliasOrUID;
    }

    private List<Pair> BuildCharacterDataPairsSnapshot()
    {
        var pairsToCheck = new HashSet<Pair>(_pairManager.DirectPairs);
        foreach (var groupPairs in _pairManager.GroupPairs.Values)
        {
            foreach (var pair in groupPairs)
            {
                pairsToCheck.Add(pair);
            }
        }

        return pairsToCheck
            .OrderBy(p => (p.IsVisible || p.IsMutuallyVisible) ? 0 : p.IsOnline ? 1 : 2)
            .ThenBy(p => p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Pair? EnsureSelectedCharacterPair(List<Pair> pairs)
    {
        if (pairs.Count == 0)
        {
            _selectedCharacterUid = null;
            return null;
        }

        if (string.IsNullOrWhiteSpace(_selectedCharacterUid) || pairs.All(p => !string.Equals(GetPairKey(p), _selectedCharacterUid, StringComparison.Ordinal)))
        {
            _selectedCharacterUid = GetPairKey(pairs[0]);
        }

        return pairs.FirstOrDefault(p => string.Equals(GetPairKey(p), _selectedCharacterUid, StringComparison.Ordinal));
    }

    private static Vector4 GetPairListColor(Pair pair)
    {
        if (pair.IsVisible || pair.IsMutuallyVisible)
        {
            return ImGuiColors.ParsedGreen;
        }

        if (pair.IsOnline)
        {
            return ImGuiColors.HealerGreen;
        }

        return ImGuiColors.DalamudGrey;
    }

    private void DrawCharacterPairSelectionList(List<Pair> pairs)
    {
        var rowHeight = ImGui.GetFrameHeight();
        foreach (var pair in pairs)
        {
            var key = GetPairKey(pair);
            var selected = string.Equals(key, _selectedCharacterUid, StringComparison.Ordinal);
            var color = GetPairListColor(pair);

            using (ImRaii.PushColor(ImGuiCol.Text, color))
            {
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    var icon = (pair.IsVisible || pair.IsMutuallyVisible) ? FontAwesomeIcon.Eye : FontAwesomeIcon.Circle;
                    SpheneUI.Text.Plain(icon.ToIconString());
                }
            }

            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 1f * ImGuiHelpers.GlobalScale);
            if (SpheneUI.Controls.Selectable($"{pair.UserData.AliasOrUID}##pair_select_{key}", selected, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
            {
                _selectedCharacterUid = key;
            }
        }
    }

    private void DrawPairsPage(IReadOnlyList<Pair> inboundRequests, IReadOnlyList<Pair> outboundRequests)
    {
        if (SpheneUI.Controls.CollapsibleSection("Pair Requests", defaultOpen: true))
        {
            SpheneUI.Text.Plain($"Incoming: {inboundRequests.Count}");
            ImGui.SameLine();
            SpheneUI.Text.Plain($"Outgoing: {outboundRequests.Count}");
            ImGui.SameLine();
            SpheneUI.Text.Plain($"New: {_pairManager.UnseenInboundIndividualPairRequestCount}");
        }

        if (SpheneUI.Controls.CollapsibleSection("Pairs", defaultOpen: true))
        {
            DrawPairedUsersTable();
        }
    }

    private void DrawCharacterDataPage()
    {
        var pairs = BuildCharacterDataPairsSnapshot();
        var selectedPair = EnsureSelectedCharacterPair(pairs);
        if (pairs.Count == 0)
        {
            SpheneUI.Text.Plain("No paired users");
            return;
        }

        if (selectedPair == null)
        {
            SpheneUI.Text.Plain("No character selected");
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var headerHeight = 210f * scale;
        if (ImGui.BeginTable("##CharacterDataHeader", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Pairs", ImGuiTableColumnFlags.WidthFixed, 340f * scale);
            ImGui.TableSetupColumn("Summary", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            using (ImRaii.Child("##CharacterDataPairList", new Vector2(0, headerHeight), true))
            {
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("Pairs");
                ImGui.SameLine();
                SpheneUI.Text.Colored($"({pairs.Count})", ImGuiColors.DalamudGrey);
                SpheneUI.Layout.Separator();

                using (ImRaii.Child("##CharacterDataPairListScroll", new Vector2(0, 0), false))
                {
                    DrawCharacterPairSelectionList(pairs);
                }
            }

            ImGui.TableSetColumnIndex(1);
            using (ImRaii.Child("##CharacterDataPairSummary", new Vector2(0, headerHeight), true))
            {
                DrawCharacterHeaderSummary(selectedPair);
            }

            ImGui.EndTable();
        }

        SpheneUI.Layout.Separator();

        using var tabBar = ImRaii.TabBar("##CharacterDataTabs");
        if (!tabBar)
        {
            return;
        }

        using (var t = ImRaii.TabItem("Debug Logs"))
        {
            if (t) DrawCharacterDebugLogs(selectedPair);
        }

        using (var t = ImRaii.TabItem("Statistics"))
        {
            if (t) DrawCharacterStatistics(selectedPair);
        }

        using (var t = ImRaii.TabItem("Penumbra Collections"))
        {
            if (t) DrawPenumbraCollectionOverview(selectedPair);
        }
    }

    private void DrawAcknowledgmentPage()
    {
        DrawAcknowledgmentTable();
    }

    private void DrawDiagnosticsPage()
    {
        using var tabBar = ImRaii.TabBar("##DiagnosticsTabs");
        if (!tabBar)
        {
            return;
        }

        using (var t = ImRaii.TabItem("Mismatch Tracker"))
        {
            if (t) DrawActiveMismatchTracker();
        }

        using (var t = ImRaii.TabItem("Legacy Check"))
        {
            if (t) DrawLegacyCheck();
        }
    }

    private void DrawPairedUsersTable()
    {
        var pairs = BuildCharacterDataPairsSnapshot();
        if (pairs.Count == 0)
        {
            SpheneUI.Text.Plain("No paired users");
            return;
        }

        if (!ImGui.BeginTable("PairedUsersTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("Online", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("My Ack", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Partner Ack", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableHeadersRow();

        foreach (var pair in pairs)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            SpheneUI.Text.Plain(pair.UserData.AliasOrUID);

            ImGui.TableSetColumnIndex(1);
            var statusColor = pair.IndividualPairStatus switch
            {
                API.Data.Enum.IndividualPairStatus.Bidirectional => ImGuiColors.HealerGreen,
                API.Data.Enum.IndividualPairStatus.OneSided => ImGuiColors.DalamudYellow,
                _ => ImGuiColors.DalamudRed
            };
            SpheneUI.Text.Colored(pair.IndividualPairStatus.ToString(), statusColor);

            ImGui.TableSetColumnIndex(2);
            if (pair.IsOnline)
            {
                DrawStatusIcon(FontAwesomeIcon.Circle, ImGuiColors.HealerGreen);
            }
            else
            {
                DrawStatusIcon(FontAwesomeIcon.Circle, ImGuiColors.DalamudRed);
            }

            ImGui.TableSetColumnIndex(3);
            var incoming = pair.GetIncomingAckV3State();
            var incomingColor = incoming.Outcome switch
            {
                Pair.AckV3Outcome.Success => ImGuiColors.HealerGreen,
                Pair.AckV3Outcome.Fail => ImGuiColors.DalamudRed,
                Pair.AckV3Outcome.Pending => ImGuiColors.DalamudYellow,
                _ => ImGuiColors.DalamudGrey
            };
            DrawStatusIcon(FontAwesomeIcon.Eye, incomingColor);
            SpheneUI.AttachTooltip($"IN:{incoming.Outcome}"
                                          + (incoming.Hash != null ? $" hash={incoming.Hash[..Math.Min(8, incoming.Hash.Length)]}" : string.Empty)
                                          + (incoming.Outcome == Pair.AckV3Outcome.Fail ? $"{Environment.NewLine}{incoming.ErrorCode}{(string.IsNullOrWhiteSpace(incoming.ErrorMessage) ? string.Empty : $"{Environment.NewLine}{incoming.ErrorMessage}")}" : string.Empty));

            ImGui.TableSetColumnIndex(4);
            if (pair.HasPendingAcknowledgment)
            {
                DrawStatusIcon(FontAwesomeIcon.Clock, ImGuiColors.DalamudYellow);
                SpheneUI.AttachTooltip("OUT:Pending");
            }
            else if (pair.LastAcknowledgmentSuccess == true)
            {
                DrawStatusIcon(FontAwesomeIcon.CheckCircle, ImGuiColors.HealerGreen);
                SpheneUI.AttachTooltip("OUT:Success");
            }
            else if (pair.LastAcknowledgmentSuccess == false)
            {
                DrawStatusIcon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
                SpheneUI.AttachTooltip($"OUT:Fail{Environment.NewLine}{pair.LastAcknowledgmentErrorCode}{(string.IsNullOrWhiteSpace(pair.LastAcknowledgmentErrorMessage) ? string.Empty : $"{Environment.NewLine}{pair.LastAcknowledgmentErrorMessage}")}");
            }
            else
            {
                DrawStatusIcon(FontAwesomeIcon.Minus, ImGuiColors.DalamudGrey);
                SpheneUI.AttachTooltip("OUT:Unknown");
            }

        }

        ImGui.EndTable();
    }
    
    private void DrawAcknowledgmentTable()
    {
        if (_acknowledgmentManager == null || _sessionAcknowledgmentManager == null)
        {
            SpheneUI.Text.Plain("Acknowledgment system not available");
            return;
        }
        
        SpheneUI.Text.Plain("Acknowledgment System Status");
        SpheneUI.Layout.Separator();
        
        // Get metrics and configuration
        var sendMetrics = _acknowledgmentManager.GetMetrics();
        var resultMetrics = _sessionAcknowledgmentManager.GetResultMetrics();
        var config = _acknowledgmentManager.GetConfiguration();
        var pendingAcks = _sessionAcknowledgmentManager.GetPendingAcknowledgments();
        var successRate = resultMetrics.Total > 0 ? (double)resultMetrics.Success / resultMetrics.Total * 100d : 0d;
        
        // Metrics overview
        SpheneUI.Text.Plain("Metrics Overview:");
        ImGui.Columns(4, "AckMetrics", true);
        
        SpheneUI.Text.Plain("Total Results");
        ImGui.NextColumn();
        SpheneUI.Text.Plain("Success Rate");
        ImGui.NextColumn();
        SpheneUI.Text.Plain("Avg Response");
        ImGui.NextColumn();
        SpheneUI.Text.Plain("Pending");
        ImGui.NextColumn();
        
        ImGui.Separator();
        
        SpheneUI.Text.Plain($"{resultMetrics.Total}");
        ImGui.NextColumn();
        SpheneUI.Text.Colored($"{successRate:F1}%", 
            successRate > 90 ? ImGuiColors.HealerGreen : 
            successRate > 70 ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudRed);
        ImGui.NextColumn();
        SpheneUI.Text.Plain($"{resultMetrics.AverageResponseTimeMs:F0}ms");
        ImGui.NextColumn();
        SpheneUI.Text.Plain($"{pendingAcks.Count}");
        ImGui.NextColumn();
        
        ImGui.Columns(1);
        ImGui.Spacing();
        
        // Configuration settings
        if (SpheneUI.Controls.CollapsibleSection("Configuration"))
        {
            SpheneUI.Text.Plain($"Max Batch Size: {config.MaxBatchSize}");
            SpheneUI.Text.Plain($"Batch Timeout: {config.BatchTimeoutMs}ms");
            SpheneUI.Text.Plain($"Max Retries: {config.MaxRetryAttempts}");
            SpheneUI.Text.Plain($"Default Timeout: {config.DefaultTimeoutSeconds}s");
            SpheneUI.Text.Plain($"Batching Enabled: {(config.EnableBatching ? "Yes" : "No")}");
            SpheneUI.Text.Plain($"Auto Retry Enabled: {(config.EnableAutoRetry ? "Yes" : "No")}");
        }
        
        // Pending acknowledgments table
        if (SpheneUI.Controls.CollapsibleSection("Pending Acknowledgments"))
        {
            if (pendingAcks.Count == 0)
            {
                SpheneUI.Text.Plain("No pending acknowledgments");
            }
            else
            {
                ImGui.Columns(4, "PendingAcks", true);
                
                SpheneUI.Text.Plain("User");
                ImGui.NextColumn();
                SpheneUI.Text.Plain("Ack Key");
                ImGui.NextColumn();
                SpheneUI.Text.Plain("Status");
                ImGui.NextColumn();
                SpheneUI.Text.Plain("Actions");
                ImGui.NextColumn();
                
                ImGui.Separator();
                
                foreach (var kvp in pendingAcks)
                {
                    var userKey = kvp.Key;
                    var ackId = kvp.Value;
                    var displayName = GetDisplayName(userKey);
                    var pair = _pairManager.GetPairByUID(userKey);
                    var outgoing = pair?.GetOutgoingAckV3State();
                    
                    SpheneUI.Text.Plain(displayName);
                    ImGui.NextColumn();
                    SpheneUI.Text.Plain(ackId.Length > 16 ? $"{ackId[..16]}..." : ackId);
                    ImGui.NextColumn();

                    if (outgoing.HasValue)
                    {
                        var state = outgoing.Value;
                        var statusColor = state.Outcome switch
                        {
                            Pair.AckV3Outcome.Pending => ImGuiColors.DalamudYellow,
                            Pair.AckV3Outcome.Success => ImGuiColors.HealerGreen,
                            Pair.AckV3Outcome.Fail => ImGuiColors.DalamudRed,
                            _ => ImGuiColors.DalamudGrey
                        };
                        SpheneUI.Text.Colored(state.Outcome.ToString(), statusColor);
                        if (state.Outcome == Pair.AckV3Outcome.Fail)
                        {
                            SpheneUI.AttachTooltip($"{state.ErrorCode}{(string.IsNullOrWhiteSpace(state.ErrorMessage) ? string.Empty : $"{Environment.NewLine}{state.ErrorMessage}")}");
                        }
                    }
                    else
                    {
                        SpheneUI.Text.Plain("-");
                    }
                    ImGui.NextColumn();
                    
                    ImGui.PushID($"clear_{userKey}");
                    if (SpheneUI.Controls.Button("Clear"))
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
                    if (SpheneUI.Controls.Button("Mark Timeout"))
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
        if (SpheneUI.Controls.CollapsibleSection("Error Statistics"))
        {
            if (resultMetrics.ErrorCounts.Count == 0)
            {
                SpheneUI.Text.Plain("No errors recorded");
            }
            else
            {
                ImGui.Columns(2, "ErrorStats", true);
                
                SpheneUI.Text.Plain("Error Type");
                ImGui.NextColumn();
                SpheneUI.Text.Plain("Count");
                ImGui.NextColumn();
                
                ImGui.Separator();
                
                foreach (var error in resultMetrics.ErrorCounts.OrderByDescending(k => k.Value))
                {
                    SpheneUI.Text.Plain(error.Key.ToString());
                    ImGui.NextColumn();
                    SpheneUI.Text.Colored($"{error.Value}", ImGuiColors.DalamudRed);
                    ImGui.NextColumn();
                }
                
                ImGui.Columns(1);
            }
        }
        
        // Priority statistics
        if (SpheneUI.Controls.CollapsibleSection("Priority Statistics"))
        {
            if (sendMetrics.PriorityCounts.Count == 0)
            {
                SpheneUI.Text.Plain("No priority data available");
            }
            else
            {
                ImGui.Columns(2, "PriorityStats", true);
                
                SpheneUI.Text.Plain("Priority");
                ImGui.NextColumn();
                SpheneUI.Text.Plain("Count");
                ImGui.NextColumn();
                
                ImGui.Separator();
                
                foreach (var priority in sendMetrics.PriorityCounts)
                {
                    var color = priority.Key switch
                    {
                        AcknowledgmentPriority.High => ImGuiColors.DalamudRed,
                        AcknowledgmentPriority.Medium => ImGuiColors.DalamudYellow,
                        AcknowledgmentPriority.Low => ImGuiColors.HealerGreen,
                        _ => ImGuiColors.DalamudWhite
                    };
                    
                    SpheneUI.Text.Colored(priority.Key.ToString(), color);
                    ImGui.NextColumn();
                    SpheneUI.Text.Plain($"{priority.Value}");
                    ImGui.NextColumn();
                }
                
                ImGui.Columns(1);
            }
        }
    }
    
    private void DrawControlButtons()
    {
        SpheneUI.Text.Plain("Debug Controls:");
        
        if (SpheneUI.Controls.Button("Force Reconnect"))
        {
            LogCommunication("[DEBUG] Force reconnect triggered");
            _ = _apiController.CreateConnectionsAsync();
        }
        
        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Refresh Pairs"))
        {
            LogCommunication("[DEBUG] Pair refresh triggered");
            _ = _apiController.CreateConnectionsAsync();
        }
        
        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Clear Log"))
        {
            ClearDebugLog();
        }

        ImGui.Spacing();
        SpheneUI.Text.Plain("Disconnect Simulation:");
        _simulatedDisconnectSeconds = Math.Clamp(_simulatedDisconnectSeconds, 1, 300);
        ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
        SpheneUI.Controls.SliderInt("Duration (seconds)##DisconnectSimulationDuration", ref _simulatedDisconnectSeconds, 1, 300);

        if (SpheneUI.Controls.Button("Simulate Real Disconnect"))
        {
            LogCommunication($"[DEBUG] Simulating real disconnect for {_simulatedDisconnectSeconds}s");
            _ = _apiController.SimulateDisconnectForTestingAsync(TimeSpan.FromSeconds(_simulatedDisconnectSeconds));
        }

        if (_apiController.IsDisconnectSimulationRunning)
        {
            ImGui.SameLine();
            SpheneUI.Text.Colored("Simulation running...", ImGuiColors.DalamudYellow);
        }
    }
    
    private void DrawHealthMonitoringSection()
    {
        SpheneUI.Text.Plain("Connection Health Monitoring:");
        
        var healthStatus = _healthMonitor.GetHealthStatus();
        var circuitStatus = _circuitBreaker.GetStatus();
        
        // Health Monitor Status
        SpheneUI.Text.Plain("Health Status:");
        ImGui.SameLine();
        if (healthStatus.IsHealthy)
        {
            SpheneUI.Text.Colored("Healthy", ImGuiColors.HealerGreen);
        }
        else
        {
            SpheneUI.Text.Colored("Unhealthy", ImGuiColors.DalamudRed);
        }
        
        SpheneUI.Text.Plain($"Consecutive Failures: {healthStatus.ConsecutiveFailures}");
        SpheneUI.Text.Plain($"Time Since Last Success: {healthStatus.TimeSinceLastSuccess:hh\\:mm\\:ss}");
        SpheneUI.Text.Plain($"Last Health Check: {healthStatus.LastHealthCheck:HH:mm:ss}");
        
        ImGui.Spacing();
        
        // Circuit Breaker Status
        SpheneUI.Text.Plain("Circuit Breaker:");
        ImGui.SameLine();
        var stateColor = circuitStatus.State switch
        {
            CircuitBreakerState.Closed => ImGuiColors.HealerGreen,
            CircuitBreakerState.HalfOpen => ImGuiColors.DalamudYellow,
            CircuitBreakerState.Open => ImGuiColors.DalamudRed,
            _ => ImGuiColors.DalamudWhite
        };
        SpheneUI.Text.Colored(circuitStatus.State.ToString(), stateColor);
        
        SpheneUI.Text.Plain($"Failure Count: {circuitStatus.FailureCount}");
        if (circuitStatus.LastFailureTime != DateTime.MinValue)
        {
            SpheneUI.Text.Plain($"Last Failure: {circuitStatus.LastFailureTime:HH:mm:ss}");
        }
        
        if (circuitStatus.State == CircuitBreakerState.HalfOpen)
        {
            SpheneUI.Text.Plain($"Half-Open Attempts: {circuitStatus.HalfOpenAttempts}");
        }
        
        ImGui.Spacing();
        
        // Reset buttons
        if (SpheneUI.Controls.Button("Reset Circuit Breaker"))
        {
            _circuitBreaker.Reset();
            LogCommunication("[DEBUG] Circuit breaker manually reset");
        }
    }

    private static void DrawCharacterDebugLogs(Pair selectedPair)
    {
        var logText = GetApplyDebugText(selectedPair);
        if (SpheneUI.Controls.Button($"Copy##copy_apply_debug_{GetPairKey(selectedPair)}"))
        {
            ImGui.SetClipboardText(logText);
        }
        ImGui.SameLine();
        if (SpheneUI.Controls.Button($"Clear##clear_apply_debug_{GetPairKey(selectedPair)}"))
        {
            selectedPair.ClearApplyDebug();
        }

        var availableSize = ImGui.GetContentRegionAvail();
        using var child = ImRaii.Child("CharacterDebugLogContent", availableSize, true);
        if (child) SpheneUI.Text.Plain(logText);
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

    private static void DrawCharacterHeaderSummary(Pair pair)
    {
        var uid = GetPairKey(pair);
        SpheneUI.Text.Plain(pair.UserData.AliasOrUID);
        ImGui.SameLine();
        SpheneUI.Text.Colored($"({uid})", ImGuiColors.DalamudGrey);
        SpheneUI.Layout.Separator();

        if (ImGui.BeginTable("##CharacterHeaderSummaryTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 165f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

            DrawSummaryRow("State", () =>
            {
                if (pair.IsVisible || pair.IsMutuallyVisible)
                {
                    DrawStatusIcon(FontAwesomeIcon.Eye, ImGuiColors.ParsedGreen);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Visible", ImGuiColors.ParsedGreen);
                }
                else if (pair.IsOnline)
                {
                    DrawStatusIcon(FontAwesomeIcon.Circle, ImGuiColors.HealerGreen);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Online", ImGuiColors.HealerGreen);
                }
                else
                {
                    DrawStatusIcon(FontAwesomeIcon.Circle, ImGuiColors.DalamudGrey);
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("Offline", ImGuiColors.DalamudGrey);
                }
            });

            DrawSummaryRow("Pair Status", () =>
            {
                var statusColor = pair.IndividualPairStatus switch
                {
                    API.Data.Enum.IndividualPairStatus.Bidirectional => ImGuiColors.HealerGreen,
                    API.Data.Enum.IndividualPairStatus.OneSided => ImGuiColors.DalamudYellow,
                    _ => ImGuiColors.DalamudRed
                };
                SpheneUI.Text.Colored(pair.IndividualPairStatus.ToString(), statusColor);
            });

            DrawSummaryRow("Groups", () => SpheneUI.Text.Plain(pair.UserPair.Groups.Count.ToString()));
            DrawSummaryRow("Collection", () =>
            {
                var collectionId = pair.GetPenumbraCollectionId();
                SpheneUI.Text.Plain(collectionId == Guid.Empty ? "(none)" : collectionId.ToString());
            });

            DrawSummaryRow("Last Received", () =>
            {
                var hash = pair.LastReceivedCharacterDataHash;
                if (string.IsNullOrEmpty(hash))
                {
                    SpheneUI.Text.Plain("-");
                    return;
                }

                var shortHash = hash.Length > 10 ? hash[..10] : hash;
                SpheneUI.Text.Plain(shortHash);
                SpheneUI.AttachTooltip(hash);
            });

            DrawSummaryRow("Received At", () =>
            {
                SpheneUI.Text.Plain(pair.LastReceivedCharacterDataTime?.ToLocalTime().ToString("HH:mm:ss") ?? "-");
            });

            DrawSummaryRow("Ack", () =>
            {
                var outgoing = pair.GetOutgoingAckV3State();
                var incoming = pair.GetIncomingAckV3State();

                var outLabel = outgoing.Outcome.ToString();
                var outColor = outgoing.Outcome switch
                {
                    Pair.AckV3Outcome.Pending => ImGuiColors.DalamudYellow,
                    Pair.AckV3Outcome.Success => ImGuiColors.HealerGreen,
                    Pair.AckV3Outcome.Fail => ImGuiColors.DalamudRed,
                    _ => ImGuiColors.DalamudGrey
                };
                SpheneUI.Text.Colored($"OUT:{outLabel}", outColor);
                if (outgoing.Time.HasValue)
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Plain(outgoing.Time.Value.ToLocalTime().ToString("HH:mm:ss"));
                }
                if (outgoing.Outcome == Pair.AckV3Outcome.Fail)
                {
                    SpheneUI.AttachTooltip($"{outgoing.ErrorCode}{(string.IsNullOrWhiteSpace(outgoing.ErrorMessage) ? string.Empty : $"{Environment.NewLine}{outgoing.ErrorMessage}")}");
                }

                ImGui.SameLine();
                SpheneUI.Text.Plain(" | ");
                ImGui.SameLine();

                var inLabel = incoming.Outcome.ToString();
                var inColor = incoming.Outcome switch
                {
                    Pair.AckV3Outcome.Pending => ImGuiColors.DalamudYellow,
                    Pair.AckV3Outcome.Success => ImGuiColors.HealerGreen,
                    Pair.AckV3Outcome.Fail => ImGuiColors.DalamudRed,
                    _ => ImGuiColors.DalamudGrey
                };
                SpheneUI.Text.Colored($"IN:{inLabel}", inColor);
                if (incoming.Time.HasValue)
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Plain(incoming.Time.Value.ToLocalTime().ToString("HH:mm:ss"));
                }
                if (incoming.Outcome == Pair.AckV3Outcome.Fail)
                {
                    SpheneUI.AttachTooltip($"{incoming.ErrorCode}{(string.IsNullOrWhiteSpace(incoming.ErrorMessage) ? string.Empty : $"{Environment.NewLine}{incoming.ErrorMessage}")}");
                }
            });

            ImGui.EndTable();
        }
    }

    private void DrawCharacterStatistics(Pair selectedPair)
    {
        var selectedSnapshot = GetCharacterStatsSnapshot(selectedPair);
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

        SpheneUI.Layout.Separator();
        SpheneUI.Text.Plain("Received Created Character Data");
        DrawReceivedCharacterData(selectedPair);
    }

    private void DrawReceivedCharacterData(Pair pair)
    {
        var receivedData = pair.LastReceivedCharacterData;
        var lastReceivedHash = pair.LastReceivedCharacterDataHash ?? "-";
        var previousReceivedHash = pair.PreviousReceivedCharacterDataHash ?? "-";
        var lastReceivedTime = pair.LastReceivedCharacterDataTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        var lastChangeTime = pair.LastReceivedCharacterDataChangeTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

        SpheneUI.Text.Plain($"Last Received Hash: {lastReceivedHash}");
        SpheneUI.Text.Plain($"Previous Received Hash: {previousReceivedHash}");
        SpheneUI.Text.Plain($"Last Received Time: {lastReceivedTime}");
        SpheneUI.Text.Plain($"Last Change Time: {lastChangeTime}");
        SpheneUI.Layout.Separator();

        var receivedDataJson = receivedData != null
            ? JsonSerializer.Serialize(receivedData, new JsonSerializerOptions() { WriteIndented = true })
            : "No received character data.";

        if (SpheneUI.Controls.Button($"Copy##received_character_data_copy_{GetPairKey(pair)}"))
        {
            ImGui.SetClipboardText(receivedDataJson);
        }

        ImGui.SameLine();
        SpheneUI.Text.Colored($"{receivedDataJson.Length:n0} chars", ImGuiColors.DalamudGrey);

        var avail = ImGui.GetContentRegionAvail();
        using var child = ImRaii.Child($"##received_character_data_{GetPairKey(pair)}", new Vector2(0, MathF.Min(260f * ImGuiHelpers.GlobalScale, avail.Y)), true);
        if (child)
        {
            SpheneUI.Text.Plain(receivedDataJson);
        }
    }

    private static void DrawStatsRow(string metric, string current, string? last)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        SpheneUI.Text.Plain(metric);
        ImGui.TableSetColumnIndex(1);
        SpheneUI.Text.Plain(current);
        ImGui.TableSetColumnIndex(2);
        SpheneUI.Text.Plain(last ?? "-");
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

    private void DrawPenumbraCollectionOverview(Pair selectedPair)
    {
        SpheneUI.Text.Plain("Penumbra Collection Overview");

        var selectedUid = selectedPair.UserData.UID;
        var collectionId = selectedPair.GetPenumbraCollectionId();
        SpheneUI.Text.Plain($"User: {selectedPair.UserData.AliasOrUID} ({selectedUid})");
        SpheneUI.Text.Plain($"Collection ID: {(collectionId == Guid.Empty ? "(none)" : collectionId.ToString())}");
        SpheneUI.Text.Plain($"Character Visible: {(selectedPair.IsVisible ? "Yes" : "No")}, Mutually Visible: {(selectedPair.IsMutuallyVisible ? "Yes" : "No")}");

        var isRefreshing = _activePenumbraRefreshInProgress.Contains(selectedUid);
        if (SpheneUI.Controls.Button($"Refresh Active Paths##refresh_active_paths_{selectedUid}") && !isRefreshing)
        {
            _ = RefreshPenumbraActivePathsAsync(selectedPair);
        }

        ImGui.SameLine();
        if (isRefreshing)
        {
            SpheneUI.Text.Colored("Refreshing active paths...", ImGuiColors.DalamudYellow);
        }
        else if (_activePenumbraPathsUpdatedAt.TryGetValue(selectedUid, out var updatedAt))
        {
            SpheneUI.Text.Plain($"Last refresh: {updatedAt.ToLocalTime():HH:mm:ss}");
        }
        else
        {
            SpheneUI.Text.Plain("Active paths not refreshed yet.");
        }

        if (_activePenumbraPathsErrors.TryGetValue(selectedUid, out var refreshError) && !string.IsNullOrEmpty(refreshError))
        {
            SpheneUI.Text.ColoredWrapped($"Refresh error: {refreshError}", ImGuiColors.DalamudRed);
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
            SpheneUI.Text.Plain("No active paths delivered.");
            return;
        }

        SpheneUI.Text.Plain($"Active delivered paths: {activeDeliveredPaths.Count} | Loaded: {loadedByPath.Count} | Player Active: {playerActiveByPath.Count} | Minion Active: {minionActiveByPath.Count} | Pet Active: {petActiveByPath.Count}");

        var tableAvailY = MathF.Max(200f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("PenumbraCollectionOverviewTable", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(-1, tableAvailY)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
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
            SpheneUI.Text.Plain(deliveredState != null ? string.Join(", ", deliveredState.ObjectKinds.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) : "-");

            ImGui.TableSetColumnIndex(2);
            SpheneUI.Text.Plain(deliveredState != null ? string.Join(", ", deliveredState.Sources.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)) : "-");

            ImGui.TableSetColumnIndex(3);
            if (hasDelivered)
            {
                SpheneUI.Text.Colored(isDataFlagActive ? "Yes" : "No", isDataFlagActive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
            }
            else
            {
                SpheneUI.Text.Plain("-");
            }

            ImGui.TableSetColumnIndex(4);
            DrawTrimmedTextWithTooltip(string.IsNullOrEmpty(loadedSource) ? "-" : loadedSource, 72);

            ImGui.TableSetColumnIndex(5);
            DrawTrimmedTextWithTooltip(string.IsNullOrEmpty(activeSource) ? "-" : activeSource, 72);

            ImGui.TableSetColumnIndex(6);
            SpheneUI.Text.Colored(hasLoaded ? "Yes" : "No", hasLoaded ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(7);
            SpheneUI.Text.Colored(isPenumbraActive ? "Yes" : "No", isPenumbraActive ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);

            ImGui.TableSetColumnIndex(8);
            SpheneUI.Text.Colored(matches ? "OK" : "Diff", matches ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow);
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
            SpheneUI.Text.Plain("-");
            return;
        }

        var display = text.Length > maxLength
            ? "..." + text[^Math.Max(1, maxLength - 3)..]
            : text;
        SpheneUI.Text.Plain(display);
        if (ImGui.IsItemHovered())
        {
            SpheneUI.AttachTooltip(text);
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
            
            IReadOnlyDictionary<string, string> minionPaths;
            IReadOnlyDictionary<string, string> petPaths;
            if (_configService.Current.MismatchTrackerTrackMinionMountAndPetPaths)
            {
                minionPaths = await pair.GetMinionOrMountActivePathsByGamePathAsync().ConfigureAwait(false);
                petPaths = await pair.GetPetActivePathsByGamePathAsync().ConfigureAwait(false);
            }
            else
            {
                minionPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                petPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            _activeMinionPathsByUid[uid] = NormalizePathMap(minionPaths);
            _activePetPathsByUid[uid] = NormalizePathMap(petPaths);
            
            _activePenumbraPathsUpdatedAt[uid] = DateTimeOffset.UtcNow;
            
            // Track mismatches: delivered as active but not active in Penumbra
            TrackActiveMismatches(uid, pair.LastReceivedCharacterData, activePaths, minionPaths, petPaths);
        }
        catch (Exception ex)
        {
            _activePenumbraPathsErrors[uid] = ex.Message;
            _logger.LogWarning(ex, "Failed to refresh active Penumbra paths for {uid}", uid);
            AddDebugLog(DebugLogLevel.Warn, "PEN", "Failed to refresh active Penumbra paths", uid: uid, details: ex.ToString());
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
        var trackCompanions = _configService.Current.MismatchTrackerTrackMinionMountAndPetPaths;

        foreach (var kvp in deliveredByPath)
        {
            var gamePath = kvp.Key;
            var state = kvp.Value;

            if (!state.IsActive) continue; // Only track paths flagged as active
            if (!ShouldTrackMismatchTrackerPath(gamePath, state.ObjectKinds)) continue;

            // Check correct active paths based on ObjectKind
            var isMinionOrMountPath = state.ObjectKinds.Contains("MinionOrMount");
            var isPetPath = state.ObjectKinds.Contains("Pet");
            if ((isMinionOrMountPath || isPetPath) && !trackCompanions) continue;
            var activeByPath = isMinionOrMountPath ? minionActiveByPath 
                : isPetPath ? petActiveByPath 
                : playerActiveByPath;
            
            var isPenumbraActive = activeByPath.TryGetValue(gamePath, out var activeSource) && !string.IsNullOrEmpty(activeSource);
            if (!isPenumbraActive)
            {
                // Mismatch: delivered as active but not active in Penumbra
                var mismatchCount = _mismatchTracker.RecordMismatchAndGetMismatchCount(uid, gamePath, state.Sources, state.ObjectKinds);
                if (mismatchCount == 1 || mismatchCount % 25 == 0)
                {
                    var sourcesText = string.Join(", ", state.Sources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                    var kindsText = string.Join(", ", state.ObjectKinds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
                    AddDebugLog(DebugLogLevel.Warn, "MM", $"Mismatch: delivered active but not active in Penumbra ({mismatchCount})", uid: uid,
                        details: $"path={gamePath} sources={sourcesText} kinds={kindsText}");
                }
            }
        }
    }

    private bool ShouldTrackMismatchTrackerPath(string gamePath, IReadOnlySet<string> objectKinds)
    {
        if (!_configService.Current.MismatchTrackerTrackEquipmentPaths
            && (gamePath.StartsWith("chara/weapon", StringComparison.OrdinalIgnoreCase)
                || gamePath.StartsWith("chara/equipment", StringComparison.OrdinalIgnoreCase)
                || gamePath.StartsWith("chara/accessory", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var isMinionOrMountPath = objectKinds.Contains("MinionOrMount");
        var isPetPath = objectKinds.Contains("Pet");
        if ((isMinionOrMountPath || isPetPath) && !_configService.Current.MismatchTrackerTrackMinionMountAndPetPaths)
        {
            return false;
        }

        if (!_configService.Current.MismatchTrackerTrackPhybFiles && gamePath.EndsWith(".phyb", StringComparison.OrdinalIgnoreCase)) return false;
        if (!_configService.Current.MismatchTrackerTrackSkpFiles && gamePath.EndsWith(".skp", StringComparison.OrdinalIgnoreCase)) return false;
        if (!_configService.Current.MismatchTrackerTrackPbdFiles && gamePath.EndsWith(".pbd", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private void DrawActiveMismatchTracker()
    {
        SpheneUI.Text.Plain("Active Mismatch Tracker");
        SpheneUI.Text.Wrapped("Tracks paths flagged as IsActive=true in delivered data but not active in Penumbra collection. Auto-refreshes every 10 seconds.");
        SpheneUI.Layout.Separator();

        // Filter settings
        SpheneUI.Text.Plain("Filter Settings:");
        
        var trackEquipment = _configService.Current.MismatchTrackerTrackEquipmentPaths;
        if (SpheneUI.Controls.Checkbox("Track Equipment (weapon/equipment/accessory)", ref trackEquipment))
        {
            _configService.Current.MismatchTrackerTrackEquipmentPaths = trackEquipment;
            _configService.Save();
        }

        ImGui.SameLine();
        var trackCompanions = _configService.Current.MismatchTrackerTrackMinionMountAndPetPaths;
        if (SpheneUI.Controls.Checkbox("Minion/Mount/Pet", ref trackCompanions))
        {
            _configService.Current.MismatchTrackerTrackMinionMountAndPetPaths = trackCompanions;
            _configService.Save();
        }
        
        ImGui.SameLine();
        var trackPhyb = _configService.Current.MismatchTrackerTrackPhybFiles;
        if (SpheneUI.Controls.Checkbox(".phyb", ref trackPhyb))
        {
            _configService.Current.MismatchTrackerTrackPhybFiles = trackPhyb;
            _configService.Save();
        }
        
        ImGui.SameLine();
        var trackSkp = _configService.Current.MismatchTrackerTrackSkpFiles;
        if (SpheneUI.Controls.Checkbox(".skp", ref trackSkp))
        {
            _configService.Current.MismatchTrackerTrackSkpFiles = trackSkp;
            _configService.Save();
        }
        
        ImGui.SameLine();
        var trackPbd = _configService.Current.MismatchTrackerTrackPbdFiles;
        if (SpheneUI.Controls.Checkbox(".pbd", ref trackPbd))
        {
            _configService.Current.MismatchTrackerTrackPbdFiles = trackPbd;
            _configService.Save();
        }
        
        SpheneUI.Layout.Separator();

        if (SpheneUI.Controls.Button("Clear All Records"))
        {
            _mismatchTracker.Clear();
        }

        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Reset Counters"))
        {
            _mismatchTracker.ResetGlobalCounters();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reset total check counters without clearing mismatch records");
        }

        var records = _mismatchTracker.GetRecords()
            .Where(r => r.MismatchCount > 0 && ShouldTrackMismatchTrackerPath(r.GamePath, r.ObjectKinds))
            .ToList();
        var (globalChecks, _) = _mismatchTracker.GetGlobalStats();
        ImGui.SameLine();
        SpheneUI.Text.Plain($"| Mismatches: {records.Count} | Total Checks: {globalChecks} | Auto-refresh every 10s");

        if (records.Count == 0)
        {
            SpheneUI.Text.Plain("No mismatches recorded yet.");
            return;
        }

        SpheneUI.Layout.Separator();

        var mismatchAvailY = MathF.Max(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("ActiveMismatchTable", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(-1, mismatchAvailY)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
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
            SpheneUI.Text.Plain(displayName);

            ImGui.TableSetColumnIndex(1);
            DrawTrimmedTextWithTooltip(record.GamePath, 100);

            ImGui.TableSetColumnIndex(2);
            // Global % - mismatch count / total global checks
            var globalPercentage = record.GlobalMismatchPercentage;
            var globalColor = globalPercentage >= 10 ? ImGuiColors.DalamudRed 
                : globalPercentage >= 5 ? ImGuiColors.DalamudOrange 
                : globalPercentage >= 1 ? ImGuiColors.DalamudYellow 
                : ImGuiColors.HealerGreen;
            SpheneUI.Text.Colored($"{globalPercentage:F2}%", globalColor);

            ImGui.TableSetColumnIndex(3);
            // Local % - mismatch count / this path's own check count
            var localPercentage = record.MismatchPercentage;
            var localColor = localPercentage >= 50 ? ImGuiColors.DalamudRed 
                : localPercentage >= 25 ? ImGuiColors.DalamudOrange 
                : localPercentage >= 10 ? ImGuiColors.DalamudYellow 
                : ImGuiColors.HealerGreen;
            SpheneUI.Text.Colored($"{localPercentage:F1}%", localColor);

            ImGui.TableSetColumnIndex(4);
            SpheneUI.Text.Plain($"{record.MismatchCount}/{record.TotalCheckCount}");

            ImGui.TableSetColumnIndex(5);
            if (record.LastSeen != DateTimeOffset.MinValue)
            {
                SpheneUI.Text.Plain(record.LastSeen.ToLocalTime().ToString("HH:mm:ss"));
            }
            else
            {
                SpheneUI.Text.Plain("-");
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

        var outgoing = pair.GetOutgoingAckV3State();
        return new CharacterStats(
            dataHash: string.IsNullOrEmpty(dataHash) ? "-" : dataHash,
            appliedBytes: pair.LastAppliedDataBytes,
            appliedTris: pair.LastAppliedDataTris,
            appliedVramBytes: pair.LastAppliedApproximateVRAMBytes,
            isVisible: pair.IsVisible,
            isMutuallyVisible: pair.IsMutuallyVisible,
            isPaused: pair.IsPaused,
            lastAckSuccess: outgoing.Outcome == Pair.AckV3Outcome.Unknown ? null : outgoing.Outcome == Pair.AckV3Outcome.Success,
            lastAckTime: outgoing.Time,
            lastAckId: outgoing.Hash,
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
    
    private void DrawCommunicationLog()
    {
        DrawLogsPanel(showPopupButton: true);
    }
    
    internal void DrawLogsPanel(bool showPopupButton)
    {
        if (SpheneUI.Controls.Button("Clear"))
        {
            ClearDebugLog();
        }
        SpheneUI.AttachTooltip("Clears the in-window log buffer.");

        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Copy Visible"))
        {
            ImGui.SetClipboardText(BuildLogText(GetFilteredDebugLogSnapshot(maxEntries: 1200)));
        }
        SpheneUI.AttachTooltip("Copies the currently visible (filtered) log lines to clipboard.");

        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Copy Report"))
        {
            ImGui.SetClipboardText(BuildDebugReportJson(GetFilteredDebugLogSnapshot(maxEntries: 1200)));
        }
        SpheneUI.AttachTooltip("Copies a JSON report with filters and entries (filtered) to clipboard.");

        ImGui.SameLine();
        if (SpheneUI.Controls.Button("Write Report File"))
        {
            var path = WriteDebugReportFile(GetFilteredDebugLogSnapshot(maxEntries: 2500));
            if (!string.IsNullOrWhiteSpace(path))
            {
                _lastReportPath = path;
            }
        }
        SpheneUI.AttachTooltip("Writes a JSON report file to your plugin config folder.");

        if (showPopupButton)
        {
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Open Popup"))
            {
                Mediator.Publish(new UiToggleMessage(typeof(StatusDebugLogPopupUi)));
            }
            SpheneUI.AttachTooltip("Opens the log viewer in a separate popup window.");
        }

        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Auto-scroll", ref _autoScroll);
        SpheneUI.AttachTooltip("Keeps the view pinned to the newest entries.");

        if (!string.IsNullOrWhiteSpace(_lastReportPath))
        {
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Copy Report Path"))
            {
                ImGui.SetClipboardText(_lastReportPath);
            }
            SpheneUI.AttachTooltip(_lastReportPath);
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Min Level", _debugLogMinLevel.ToString()))
        {
            foreach (var level in Enum.GetValues<DebugLogLevel>())
            {
                var selected = level == _debugLogMinLevel;
                if (SpheneUI.Controls.Selectable(level.ToString(), selected))
                {
                    _debugLogMinLevel = level;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        SpheneUI.AttachTooltip("Minimum severity to show in the log list.");

        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Selected character only", ref _debugLogSelectedCharacterOnly);
        SpheneUI.AttachTooltip("Shows only entries for the currently selected character in this window.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        SpheneUI.Controls.InputTextWithHint("##LogSearch", "Search...", ref _debugLogSearch, 128);
        SpheneUI.AttachTooltip("Filters by text across message, details, and UID.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Apply details", ref _includeApplyChangeDetails);
        SpheneUI.AttachTooltip("Includes APPLY details for Info/Debug entries (can add a lot of text).");

        ImGui.Spacing();
        SpheneUI.Text.Plain("Show:");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Conn", ref _showConnections);
        SpheneUI.AttachTooltip("Connection state changes.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Health", ref _showHealthChecks);
        SpheneUI.AttachTooltip("Periodic client health checks.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Ack", ref _showAcknowledgments);
        SpheneUI.AttachTooltip("Acknowledgment / sync confirmation events.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Circuit", ref _showCircuitBreaker);
        SpheneUI.AttachTooltip("Circuit breaker state changes.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Apply", ref _showApplies);
        SpheneUI.AttachTooltip("Character data apply pipeline events.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Hub", ref _showHub);
        SpheneUI.AttachTooltip("SignalR hub reconnect/close messages.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Notif", ref _showNotifications);
        SpheneUI.AttachTooltip("In-game notifications emitted by Sphene.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("DL", ref _showDownloads);
        SpheneUI.AttachTooltip("File download / transfer events.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("MM", ref _showMismatches);
        SpheneUI.AttachTooltip("Mismatch tracking events.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("IPC", ref _showIpc);
        SpheneUI.AttachTooltip("IPC and Penumbra related events.");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Minion", ref _showMinions);
        SpheneUI.AttachTooltip("Minion/Mount/Pet/Companion diagnostic logs (reapply, binding, redraw).");
        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("SCD", ref _showMinionScd);
        SpheneUI.AttachTooltip("Minion sound (.scd) override logs. These can be frequent for effect-heavy minions.");

        var pairs = BuildCharacterDataPairsSnapshot();
        var selectedPair = EnsureSelectedCharacterPair(pairs);
        var uidLabel = _debugLogSelectedCharacterOnly
            ? selectedPair != null ? selectedPair.UserData.AliasOrUID : "Selected"
            : string.IsNullOrWhiteSpace(_debugLogSpecificUid) ? "All" : _debugLogSpecificUid;

        ImGui.SetNextItemWidth(260f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Character Filter", uidLabel))
        {
            var anySelected = string.IsNullOrWhiteSpace(_debugLogSpecificUid) && !_debugLogSelectedCharacterOnly;
            if (SpheneUI.Controls.Selectable("All", anySelected))
            {
                _debugLogSpecificUid = string.Empty;
                _debugLogSelectedCharacterOnly = false;
            }

            if (_selectedCharacterUid != null)
            {
                var selected = _debugLogSelectedCharacterOnly;
                if (SpheneUI.Controls.Selectable("Selected Character", selected))
                {
                    _debugLogSelectedCharacterOnly = true;
                    _debugLogSpecificUid = string.Empty;
                }
            }

            SpheneUI.Layout.Separator();
            foreach (var pair in pairs)
            {
                var key = GetPairKey(pair);
                var selected = string.Equals(_debugLogSpecificUid, key, StringComparison.Ordinal) && !_debugLogSelectedCharacterOnly;
                if (SpheneUI.Controls.Selectable(pair.UserData.AliasOrUID, selected))
                {
                    _debugLogSpecificUid = key;
                    _debugLogSelectedCharacterOnly = false;
                }
            }
            ImGui.EndCombo();
        }
        SpheneUI.AttachTooltip("Limits entries to a specific user (UID).");

        ImGui.SameLine();
        SpheneUI.Controls.Checkbox("Ack session filter", ref _debugLogAckContextFilterEnabled);
        SpheneUI.AttachTooltip("Filters to the selected character's acknowledgment session/hash context.");
        if (_debugLogAckContextFilterEnabled && selectedPair != null)
        {
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Use OUT"))
            {
                _debugLogAckContextSessionId = selectedPair.LastOutgoingAcknowledgmentSessionId ?? string.Empty;
                var hash = selectedPair.LastOutgoingAcknowledgmentHash ?? selectedPair.LastAcknowledgmentId ?? string.Empty;
                _debugLogAckContextHashPrefix = string.IsNullOrWhiteSpace(hash) ? string.Empty : hash[..Math.Min(8, hash.Length)];
            }
            SpheneUI.AttachTooltip("Uses the last outgoing acknowledgment context for the selected character.");
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Use IN"))
            {
                _debugLogAckContextSessionId = selectedPair.LastIncomingAckSessionId ?? string.Empty;
                var hash = selectedPair.LastIncomingAckHash ?? selectedPair.LastReceivedCharacterDataHash ?? string.Empty;
                _debugLogAckContextHashPrefix = string.IsNullOrWhiteSpace(hash) ? string.Empty : hash[..Math.Min(8, hash.Length)];
            }
            SpheneUI.AttachTooltip("Uses the last incoming acknowledgment context for the selected character.");
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Clear##ack_ctx"))
            {
                _debugLogAckContextSessionId = string.Empty;
                _debugLogAckContextHashPrefix = string.Empty;
            }
            SpheneUI.AttachTooltip("Clears the acknowledgment context filter values.");

            if (!string.IsNullOrWhiteSpace(_debugLogAckContextSessionId) || !string.IsNullOrWhiteSpace(_debugLogAckContextHashPrefix))
            {
                ImGui.SameLine();
                var sessionShort = string.IsNullOrWhiteSpace(_debugLogAckContextSessionId)
                    ? "-"
                    : _debugLogAckContextSessionId[..Math.Min(8, _debugLogAckContextSessionId.Length)];
                SpheneUI.Text.Plain($"session={sessionShort} hash={_debugLogAckContextHashPrefix}");
                SpheneUI.AttachTooltip("Active acknowledgment context used for filtering.");
            }
        }

        SpheneUI.Layout.Separator();

        var entries = GetFilteredDebugLogSnapshot(maxEntries: 2500);
        var avail = ImGui.GetContentRegionAvail();
        if (!ImGui.BeginTable("##DebugLogTable", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY,
                new Vector2(-1, avail.Y)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableSetupColumn("Lvl", ImGuiTableColumnFlags.WidthFixed, 45f);
        ImGui.TableSetupColumn("Cat", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 160f);
        ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 34f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            ImGui.TableSetColumnIndex(0);
            SpheneUI.Text.Plain(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"));

            ImGui.TableSetColumnIndex(1);
            DrawLevelIcon(entry.Level);

            ImGui.TableSetColumnIndex(2);
            SpheneUI.Text.Plain(entry.Category);

            ImGui.TableSetColumnIndex(3);
            SpheneUI.Text.Plain(string.IsNullOrEmpty(entry.Uid) ? "-" : GetDisplayName(entry.Uid));

            ImGui.TableSetColumnIndex(4);
            SpheneUI.Text.Plain(entry.Message);
            if (ShouldIncludeDetails(entry) && ImGui.IsItemHovered())
            {
                SpheneUI.AttachTooltip(entry.Details!);
            }

            if (SpheneUI.Popups.BeginContextStyled("##log_ctx"))
            {
                if (ImGui.MenuItem("Copy message"))
                {
                    ImGui.SetClipboardText(entry.Message);
                }
                if (ShouldIncludeDetails(entry) && ImGui.MenuItem("Copy details/stacktrace"))
                {
                    ImGui.SetClipboardText(entry.Details!);
                }
                SpheneUI.Popups.EndStyled();
            }

            ImGui.TableSetColumnIndex(5);
            if (ShouldIncludeDetails(entry))
            {
                var size = ImGui.GetFrameHeight();
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    if (SpheneUI.Controls.Button($"{FontAwesomeIcon.Copy.ToIconString()}##copy_details", new Vector2(size, size)))
                    {
                        ImGui.SetClipboardText(entry.Details!);
                    }
                }
                SpheneUI.AttachTooltip("Copy details / stacktrace");
            }

            ImGui.PopID();
        }

        if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
        {
            ImGui.SetScrollHereY(1.0f);
        }

        ImGui.EndTable();
    }
    
    private void ClearDebugLog()
    {
        _debugLogLock.Enter();
        try
        {
            _debugLogEntries.Clear();
            _debugLogBytes = 0;
        }
        finally
        {
            _debugLogLock.Exit();
        }
    }

    private void AddDebugLog(DebugLogLevel level, string category, string message, string? uid = null, string? details = null)
    {
        if (!string.IsNullOrWhiteSpace(uid) && (string.Equals(category, "APPLY", StringComparison.Ordinal) || string.Equals(category, "DL", StringComparison.Ordinal) || string.Equals(category, "MM", StringComparison.Ordinal)))
        {
            var pair = _pairManager.GetPairByUID(uid);
            if (pair != null)
            {
                var sessionId = pair.LastIncomingAckSessionId;
                var hash = pair.LastIncomingAckHash ?? pair.LastReceivedCharacterDataHash;
                var hashShort = string.IsNullOrWhiteSpace(hash) ? string.Empty : hash[..Math.Min(8, hash.Length)];

                if (!string.IsNullOrWhiteSpace(sessionId) && !(details?.Contains("session=", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    var prefix = $"session={sessionId} hash={hashShort}";
                    details = string.IsNullOrWhiteSpace(details) ? prefix : $"{prefix} | {details}";
                }
            }
        }

        var entry = new DebugLogEntry(DateTimeOffset.Now, level, category, uid, message, details);
        var entryBytes = EstimateEntryBytes(entry);
        _debugLogLock.Enter();
        try
        {
            _debugLogEntries.Add(entry);
            _debugLogBytes += entryBytes;
            if (_debugLogEntries.Count > MaxDebugLogEntries)
            {
                _debugLogEntries.RemoveRange(0, Math.Max(1, _debugLogEntries.Count - (MaxDebugLogEntries - 200)));
                _debugLogBytes = _debugLogEntries.Sum(EstimateEntryBytes);
            }
            while (_debugLogBytes > MaxDebugLogBytes && _debugLogEntries.Count > 0)
            {
                var removed = _debugLogEntries[0];
                _debugLogEntries.RemoveAt(0);
                _debugLogBytes -= EstimateEntryBytes(removed);
            }
        }
        finally
        {
            _debugLogLock.Exit();
        }
    }

    private static long EstimateEntryBytes(DebugLogEntry entry)
    {
        try
        {
            var message = entry.Message ?? string.Empty;
            var details = entry.Details ?? string.Empty;
            var category = entry.Category ?? string.Empty;
            var uid = entry.Uid ?? string.Empty;
            return System.Text.Encoding.UTF8.GetByteCount(message)
                + System.Text.Encoding.UTF8.GetByteCount(details)
                + System.Text.Encoding.UTF8.GetByteCount(category)
                + System.Text.Encoding.UTF8.GetByteCount(uid)
                + 64;
        }
        catch
        {
            return 512;
        }
    }

    private static void DrawLevelIcon(DebugLogLevel level)
    {
        var icon = level switch
        {
            DebugLogLevel.Trace => FontAwesomeIcon.DotCircle,
            DebugLogLevel.Debug => FontAwesomeIcon.Bug,
            DebugLogLevel.Info => FontAwesomeIcon.InfoCircle,
            DebugLogLevel.Warn => FontAwesomeIcon.ExclamationTriangle,
            DebugLogLevel.Error => FontAwesomeIcon.TimesCircle,
            _ => FontAwesomeIcon.InfoCircle
        };

        var color = level switch
        {
            DebugLogLevel.Trace => ImGuiColors.DalamudGrey,
            DebugLogLevel.Debug => ImGuiColors.ParsedBlue,
            DebugLogLevel.Info => ImGuiColors.DalamudWhite,
            DebugLogLevel.Warn => ImGuiColors.DalamudYellow,
            DebugLogLevel.Error => ImGuiColors.DalamudRed,
            _ => ImGuiColors.DalamudWhite
        };

        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            SpheneUI.Text.Plain(icon.ToIconString());
        }
    }

    private bool ShouldIncludeDetails(DebugLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Details))
        {
            return false;
        }

        if (string.Equals(entry.Category, "APPLY", StringComparison.Ordinal) && entry.Level < DebugLogLevel.Warn && !_includeApplyChangeDetails)
        {
            return false;
        }

        return true;
    }

    private List<DebugLogEntry> GetFilteredDebugLogSnapshot(int maxEntries)
    {
        List<DebugLogEntry> snapshot;
        _debugLogLock.Enter();
        try
        {
            snapshot = _debugLogEntries.ToList();
        }
        finally
        {
            _debugLogLock.Exit();
        }

        var selectedUid = _selectedCharacterUid;
        var uidFilter = _debugLogSelectedCharacterOnly ? selectedUid : (string.IsNullOrWhiteSpace(_debugLogSpecificUid) ? null : _debugLogSpecificUid);
        var search = _debugLogSearch;

        bool CategoryEnabled(string category) => category switch
        {
            "CONN" => _showConnections,
            "HEALTH" => _showHealthChecks,
            "ACK" => _showAcknowledgments,
            "CIRCUIT" => _showCircuitBreaker,
            "APPLY" => _showApplies,
            "HUB" => _showHub,
            "NOTIF" => _showNotifications,
            "DL" => _showDownloads,
            "MM" => _showMismatches,
            "IPC" => _showIpc,
            "PEN" => _showIpc,
            "MINION" => _showMinions,
            "MINION_SCD" => _showMinionScd,
            _ => true
        };

        IEnumerable<DebugLogEntry> filtered = snapshot
            .Where(e => e.Level >= _debugLogMinLevel)
            .Where(e => CategoryEnabled(e.Category))
            .Where(e => uidFilter == null || string.Equals(e.Uid, uidFilter, StringComparison.Ordinal))
            .Where(e => string.IsNullOrWhiteSpace(search)
                || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (e.Details?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.Uid?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        if (_debugLogAckContextFilterEnabled)
        {
            var sessionId = _debugLogAckContextSessionId;
            var hashPrefix = _debugLogAckContextHashPrefix;
            if (!string.IsNullOrWhiteSpace(sessionId) || !string.IsNullOrWhiteSpace(hashPrefix))
            {
                filtered = filtered.Where(e =>
                {
                    var message = e.Message ?? string.Empty;
                    var details = e.Details ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        return message.Contains(sessionId, StringComparison.OrdinalIgnoreCase)
                            || details.Contains(sessionId, StringComparison.OrdinalIgnoreCase);
                    }

                    return message.Contains(hashPrefix, StringComparison.OrdinalIgnoreCase)
                        || details.Contains(hashPrefix, StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        var list = filtered.ToList();
        if (list.Count > maxEntries)
        {
            list = list.Skip(list.Count - maxEntries).ToList();
        }

        return list;
    }

    private string BuildLogText(List<DebugLogEntry> entries)
    {
        var sb = new System.Text.StringBuilder(entries.Count * 80);
        foreach (var e in entries)
        {
            sb.Append('[').Append(e.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")).Append("] ");
            sb.Append('[').Append(e.Level.ToString().ToUpperInvariant()).Append("] ");
            sb.Append('[').Append(e.Category).Append("] ");
            if (!string.IsNullOrWhiteSpace(e.Uid))
            {
                sb.Append('[').Append(e.Uid).Append("] ");
            }
            sb.Append(e.Message);
            if (ShouldIncludeDetails(e))
            {
                sb.Append(" | ").Append(e.Details);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private string BuildDebugReportJson(List<DebugLogEntry> entries)
    {
        var payload = new
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Server = _apiController.ServerInfo.ShardName,
            Connected = _apiController.IsConnected,
            SelfUid = _apiController.UID,
            Filters = new
            {
                MinLevel = _debugLogMinLevel.ToString(),
                SelectedCharacterOnly = _debugLogSelectedCharacterOnly,
                SpecificUid = _debugLogSpecificUid,
                Search = _debugLogSearch,
                Categories = new
                {
                    Conn = _showConnections,
                    Health = _showHealthChecks,
                    Ack = _showAcknowledgments,
                    Circuit = _showCircuitBreaker,
                    Apply = _showApplies,
                    Hub = _showHub,
                    Notif = _showNotifications,
                    Downloads = _showDownloads,
                    Mismatches = _showMismatches,
                    Ipc = _showIpc,
                    Minions = _showMinions,
                    MinionScd = _showMinionScd
                }
            },
            Entries = entries.Select(e => new
            {
                e.Timestamp,
                Level = e.Level.ToString(),
                e.Category,
                e.Uid,
                e.Message,
                Details = ShouldIncludeDetails(e) ? e.Details : null
            }).ToList()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private string WriteDebugReportFile(List<DebugLogEntry> entries)
    {
        try
        {
            var json = BuildDebugReportJson(entries);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
            if (bytes > MaxDebugLogBytes)
            {
                var trimmed = entries.ToList();
                while (trimmed.Count > 0 && System.Text.Encoding.UTF8.GetByteCount(BuildDebugReportJson(trimmed)) > MaxDebugLogBytes)
                {
                    trimmed.RemoveAt(0);
                }
                json = BuildDebugReportJson(trimmed);
            }

            var folder = Path.Combine(_configService.ConfigurationDirectory, "DebugReports");
            Directory.CreateDirectory(folder);
            var fileName = $"sphene_debug_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
            var filePath = Path.Combine(folder, fileName);
            File.WriteAllText(filePath, json);
            AddDebugLog(DebugLogLevel.Info, "INFO", $"Wrote debug report file: {fileName}");
            return filePath;
        }
        catch (Exception ex)
        {
            AddDebugLog(DebugLogLevel.Error, "ERROR", "Failed to write debug report file", details: ex.ToString());
            return string.Empty;
        }
    }

    private void LogCommunication(string message, string type = "INFO")
    {
        var category = type switch
        {
            "CONN" => "CONN",
            "HEALTH" => "HEALTH",
            "ACK" => "ACK",
            "CIRCUIT" => "CIRCUIT",
            _ => "INFO"
        };

        var level = type switch
        {
            "WARN" => DebugLogLevel.Warn,
            "ERROR" => DebugLogLevel.Error,
            _ => message.StartsWith("[DEBUG]", StringComparison.Ordinal) ? DebugLogLevel.Debug
                : message.StartsWith("Health check OK", StringComparison.Ordinal) ? DebugLogLevel.Trace
                : DebugLogLevel.Info
        };

        AddDebugLog(level, category, message);
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
        var level = message.Type switch
        {
            Sphene.SpheneConfiguration.Models.NotificationType.Error => DebugLogLevel.Error,
            Sphene.SpheneConfiguration.Models.NotificationType.Warning => DebugLogLevel.Warn,
            _ => DebugLogLevel.Info
        };
        AddDebugLog(level, "NOTIF", $"[{typeStr}] {message.Title}", details: message.Message);
    }

    private void OnCharacterDataApplicationCompleted(CharacterDataApplicationCompletedMessage message)
    {
        if (message.Success)
        {
            return;
        }

        var shortHash = string.IsNullOrEmpty(message.DataHash) ? "-" : (message.DataHash.Length > 10 ? message.DataHash[..10] : message.DataHash);
        AddDebugLog(DebugLogLevel.Warn, "APPLY",
            $"Apply failed: {message.PlayerName} hash={shortHash}",
            uid: message.UserUID,
            details: $"ApplicationId={message.ApplicationId} error={message.ErrorCode} {message.ErrorMessage}");
    }

    private void OnHubReconnecting(HubReconnectingMessage message)
    {
        AddDebugLog(DebugLogLevel.Warn, "HUB", "Hub reconnecting", details: message.Exception?.ToString());
    }

    private void OnHubReconnected(HubReconnectedMessage message)
    {
        AddDebugLog(DebugLogLevel.Info, "HUB", "Hub reconnected", details: message.Arg);
    }

    private void OnHubClosed(HubClosedMessage message)
    {
        AddDebugLog(DebugLogLevel.Error, "HUB", "Hub closed", details: message.Exception?.ToString());
    }

    private void OnDebugLogEvent(DebugLogEventMessage message)
    {
        var level = message.Level switch
        {
            LogLevel.Trace => DebugLogLevel.Trace,
            LogLevel.Debug => DebugLogLevel.Debug,
            LogLevel.Information => DebugLogLevel.Info,
            LogLevel.Warning => DebugLogLevel.Warn,
            LogLevel.Error => DebugLogLevel.Error,
            LogLevel.Critical => DebugLogLevel.Error,
            _ => DebugLogLevel.Info
        };

        AddDebugLog(level, message.Category, message.Message, uid: message.Uid, details: message.Details);
    }

    private void OnDownloadStarted(DownloadStartedMessage message)
    {
        try
        {
            var totalBytes = message.DownloadStatus.Sum(k => k.Value.TotalBytes);
            var totalFiles = message.DownloadStatus.Sum(k => k.Value.TotalFiles);
            var name = string.IsNullOrWhiteSpace(message.DownloadId.Name) ? "(unknown)" : message.DownloadId.Name;
            AddDebugLog(DebugLogLevel.Info, "DL", $"Download started: {name} ({message.DownloadId.ObjectKind}) files={totalFiles}",
                details: $"bytes={totalBytes:n0} groups={message.DownloadStatus.Count}");
        }
        catch (Exception ex)
        {
            AddDebugLog(DebugLogLevel.Warn, "DL", "Download started (failed to summarize)", details: ex.ToString());
        }
    }

    private void OnDownloadFinished(DownloadFinishedMessage message)
    {
        var name = string.IsNullOrWhiteSpace(message.DownloadId.Name) ? "(unknown)" : message.DownloadId.Name;
        AddDebugLog(DebugLogLevel.Info, "DL", $"Download finished: {name} ({message.DownloadId.ObjectKind})");
    }

    private void OnDownloadReady(DownloadReadyMessage message)
    {
        AddDebugLog(DebugLogLevel.Debug, "DL", $"Download ready: requestId={message.RequestId}");
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
                SpheneUI.Text.Plain(pair.UserData.AliasOrUID);

                ImGui.TableNextColumn();
                var data = pair.LastReceivedCharacterData;
                if (data == null)
                {
                    SpheneUI.Text.Colored("No Data", ImGuiColors.DalamudGrey);
                    ImGui.TableNextColumn();
                    SpheneUI.Text.Plain("-");
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
                            SpheneUI.Text.Colored("FOUND (Filtered)", ImGuiColors.DalamudYellow);
                            details.Insert(0, "Inbound filter active: characterlegacy.shpk entries were blocked for apply/download.");
                        }
                        else
                        {
                            SpheneUI.Text.Colored("FOUND", ImGuiColors.DalamudRed);
                        }
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Wrapped(string.Join("\n", details));
                    }
                    else
                    {
                        SpheneUI.Text.Colored("Clean", ImGuiColors.HealerGreen);
                        ImGui.TableNextColumn();
                        SpheneUI.Text.Plain("-");
                    }
                }
            }

            ImGui.EndTable();
        }
    }
}
