using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.CharaData;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.PlayerData.Pairs;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration.Models;
using Sphene.SpheneConfiguration;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.SignalR.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.Services;

public class AreaBoundSyncshellService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ILogger<AreaBoundSyncshellService> _logger;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SpheneMediator _mediator;
    private readonly ApiController _apiController;
    private readonly IFramework _framework;
    private readonly SpheneConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly HousingOwnershipService _housingOwnershipService;
    
    private LocationInfo? _lastLocation;
    private readonly Dictionary<string, AreaBoundSyncshellDto> _areaBoundSyncshells = new();
    private readonly HashSet<string> _currentlyJoinedAreaSyncshells = new();
    private readonly HashSet<string> _notifiedSyncshells = new(); // Track which syncshells we've already notified about
    private readonly Timer _locationCheckTimer;
    private bool _isEnabled = true;

    public AreaBoundSyncshellService(ILogger<AreaBoundSyncshellService> logger, 
        SpheneMediator mediator, 
        DalamudUtilService dalamudUtilService, 
        ApiController apiController,
        IFramework framework,
        SpheneConfigService configService,
        PairManager pairManager,
        HousingOwnershipService housingOwnershipService) : base(logger, mediator)
    {
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _mediator = mediator;
        _apiController = apiController;
        _framework = framework;
        _configService = configService;
        _pairManager = pairManager;
        _housingOwnershipService = housingOwnershipService;
        
        // Check location every 1 second
        _locationCheckTimer = new Timer(CheckLocationChange, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        
        Mediator.Subscribe<AreaBoundJoinRequestMessage>(this, OnAreaBoundJoinRequest);
        Mediator.Subscribe<AreaBoundJoinResponseMessage>(this, OnAreaBoundJoinResponse);
        Mediator.Subscribe<ConnectedMessage>(this, OnConnected);
        Mediator.Subscribe<DisconnectedMessage>(this, OnDisconnected);
        Mediator.Subscribe<AreaBoundSyncshellConfigurationUpdateMessage>(this, OnAreaBoundSyncshellConfigurationUpdate);
        
        _logger.LogDebug("AreaBoundSyncshellService initialized");
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            _logger.LogDebug("Area-bound syncshell service enabled: {Enabled}", _isEnabled);
        }
    }

    private async void CheckLocationChange(object? state)
    {
        if (!_isEnabled || !_apiController.IsConnected)
            return;

        try
        {
            var currentLocation = await _dalamudUtilService.GetMapDataAsync();
            
            if (_lastLocation == null || !LocationsMatch(_lastLocation.Value, currentLocation))
            {
                var oldLocation = _lastLocation;
                _lastLocation = currentLocation;
                
                _logger.LogDebug("Location changed from {OldLocation} to {NewLocation}", oldLocation, currentLocation);
                
                Mediator.Publish(new AreaBoundLocationChangedMessage(currentLocation, oldLocation));
                await HandleLocationChange(oldLocation, currentLocation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking location change");
        }
    }

    private async Task HandleLocationChange(LocationInfo? oldLocation, LocationInfo newLocation)
    {
        // If user hasn't seen the city syncshell explanation yet, don't show any area-bound syncshell UI
        // The explanation should be shown first by CitySyncshellService
        if (!_configService.Current.HasSeenCitySyncshellExplanation)
        {
            _logger.LogDebug("User hasn't seen city syncshell explanation yet, skipping area-bound syncshell processing");
            return;
        }
        
        // Check if area syncshell consent popups are disabled - this affects both area-bound and city syncshells
        if (!_configService.Current.AutoShowAreaBoundSyncshellConsent)
        {
            _logger.LogDebug("Area syncshell consent popups are disabled, skipping area-bound syncshell processing");
            return;
        }
        
        // Leave syncshells that are no longer applicable
        if (oldLocation != null)
        {
            var syncshellsToLeave = new List<string>();
            
            foreach (var syncshellId in _currentlyJoinedAreaSyncshells)
            {
                if (_areaBoundSyncshells.TryGetValue(syncshellId, out var syncshell))
                {
                    // Check if location is still in bounds
                    if (!IsLocationInBounds(syncshell, newLocation))
                    {
                        // Check if current user is the owner of this syncshell
                        bool isOwner = false;
                        try
                        {
                            var allGroups = await _apiController.GroupsGetAll();
                            var groupInfo = allGroups.FirstOrDefault(g => g.Group.GID == syncshellId);
                            isOwner = groupInfo != null && string.Equals(groupInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error checking ownership for syncshell {SyncshellId}", syncshellId);
                        }
                        
                        // Only leave if not the owner
                        if (!isOwner)
                        {
                            syncshellsToLeave.Add(syncshellId);
                        }
                        else
                        {
                            _logger.LogDebug("Skipping auto-leave for syncshell {SyncshellId} - user is the owner", syncshellId);
                        }
                    }
                }
            }

            foreach (var syncshellId in syncshellsToLeave)
            {
                await LeaveSyncshell(syncshellId);
                // Remove from notified list when leaving so we can notify again if we re-enter
                _notifiedSyncshells.Remove(syncshellId);
            }
        }

        // Join syncshells that are now applicable
        var syncshellsToJoin = _areaBoundSyncshells.Values
            .Where(syncshell => !_currentlyJoinedAreaSyncshells.Contains(syncshell.GID) && 
                               IsLocationInBounds(syncshell, newLocation))
            .ToList();

        if (syncshellsToJoin.Count == 0)
        {
            return; // No syncshells to join
        }

        // First, automatically join syncshells where user already has consent
        var syncshellsWithConsent = new List<AreaBoundSyncshellDto>();
        var syncshellsWithoutConsent = new List<AreaBoundSyncshellDto>();
        
        foreach (var syncshell in syncshellsToJoin)
        {
            try
            {
                var hasValidConsent = await _apiController.GroupCheckAreaBoundConsent(syncshell.GID);
                if (hasValidConsent)
                {
                    syncshellsWithConsent.Add(syncshell);
                }
                else
                {
                    syncshellsWithoutConsent.Add(syncshell);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking consent for syncshell {SyncshellId}, treating as no consent", syncshell.GID);
                syncshellsWithoutConsent.Add(syncshell);
            }
        }
        
        // Auto-join syncshells with existing consent
        foreach (var syncshell in syncshellsWithConsent)
        {
            try
            {
                _logger.LogDebug("User has valid consent for syncshell {SyncshellId}, auto-joining", syncshell.GID);
                await JoinAreaBoundSyncshell(syncshell.GID, true, syncshell.Settings.RulesVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error auto-joining syncshell {SyncshellId}", syncshell.GID);
            }
        }
        
        // Handle remaining syncshells without consent
        if (syncshellsWithoutConsent.Count == 0)
        {
            return; // All syncshells were auto-joined
        }

        // Check if automatic consent UI is enabled
        if (!_configService.Current.AutoShowAreaBoundSyncshellConsent)
        {
            _logger.LogDebug("Auto-show area syncshell consent is disabled, only notifying about available syncshells");
            
            // Only notify about available syncshells, don't show UI
            foreach (var syncshell in syncshellsWithoutConsent)
            {
                if (!_notifiedSyncshells.Contains(syncshell.GID))
                {
                    _notifiedSyncshells.Add(syncshell.GID);
                    SendAreaBoundNotification(syncshell);
                }
            }
            return;
        }

        // Check if multiple syncshells without consent are available
        if (syncshellsWithoutConsent.Count > 1)
        {
            // Show selection UI for multiple syncshells (only those without consent)
            _logger.LogDebug("Multiple area syncshells without consent available ({count}), showing selection UI", syncshellsWithoutConsent.Count);
            var selectionMessage = new AreaBoundSyncshellSelectionRequestMessage(syncshellsWithoutConsent);
            _mediator.Publish(selectionMessage);
            
            // Notify about available area-bound syncshells if not already notified
            foreach (var syncshell in syncshellsWithoutConsent)
            {
                if (!_notifiedSyncshells.Contains(syncshell.GID))
                {
                    _notifiedSyncshells.Add(syncshell.GID);
                    SendAreaBoundNotification(syncshell);
                }
            }
            return;
        }

        // Single syncshell without consent - handle as before
        var singleSyncshell = syncshellsWithoutConsent[0];
        
        // Check if user consent is required for new users
        bool requiresRulesAcceptance = singleSyncshell.Settings.RequireRulesAcceptance && 
                                     !string.IsNullOrEmpty(singleSyncshell.Settings.JoinRules);
        
        // Send consent request for new consent
        var consentMessage = new AreaBoundSyncshellConsentRequestMessage(singleSyncshell, requiresRulesAcceptance);
        _mediator.Publish(consentMessage);
        
        // Notify about available area-bound syncshell if not already notified
        if (!_notifiedSyncshells.Contains(singleSyncshell.GID))
        {
            _notifiedSyncshells.Add(singleSyncshell.GID);
            SendAreaBoundNotification(singleSyncshell);
        }
    }

    public async Task JoinAreaBoundSyncshell(string syncshellId, bool acceptRules = false, int rulesVersion = 0)
    {
        try
        {
            _logger.LogDebug("Attempting to join area-bound syncshell {SyncshellId} with consent", syncshellId);
            
            // Set consent first
            var consentDto = new AreaBoundJoinConsentRequestDto
            {
                SyncshellGID = syncshellId,
                AcceptJoin = true,
                AcceptRules = acceptRules,
                RulesVersion = rulesVersion
            };
            
            var consentResult = await _apiController.GroupSetAreaBoundConsent(consentDto);
            if (!consentResult)
            {
                _logger.LogWarning("Failed to set consent for area-bound syncshell {SyncshellId}", syncshellId);
                return;
            }
            
            // Now request to join
            await _apiController.AreaBoundJoinRequest(syncshellId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining area-bound syncshell {SyncshellId} with consent", syncshellId);
        }
    }

    public async Task LeaveAreaSyncshell(string syncshellId)
    {
        await LeaveSyncshell(syncshellId);
    }

    private async Task LeaveSyncshell(string syncshellId)
    {
        try
        {
            _logger.LogDebug("Leaving area-bound syncshell {SyncshellId}. Currently joined before removal: [{Joined}]", 
                syncshellId, string.Join(", ", _currentlyJoinedAreaSyncshells));
            await _apiController.GroupLeave(new GroupDto(new GroupData(syncshellId)));
            
            bool removed = _currentlyJoinedAreaSyncshells.Remove(syncshellId);
            _logger.LogDebug("Removed {SyncshellId} from joined list: {Removed}. Currently joined after removal: [{Joined}]", 
                syncshellId, removed, string.Join(", ", _currentlyJoinedAreaSyncshells));
            
            // Publish leave event to notify UI components
            _mediator.Publish(new AreaBoundSyncshellLeftMessage(syncshellId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leaving area-bound syncshell {SyncshellId}", syncshellId);
        }
    }

    private bool IsLocationInBounds(AreaBoundSyncshellDto syncshell, LocationInfo location)
    {
        // Check if any of the syncshell's bound areas match the user's location
        foreach (var boundArea in syncshell.BoundAreas)
        {
            bool matches = true;

            // Check server match (if specified)
            if (boundArea.Location.ServerId != 0 && boundArea.Location.ServerId != location.ServerId)
                matches = false;

            // Check territory match (if specified)
            if (matches && boundArea.Location.TerritoryId != 0 && boundArea.Location.TerritoryId != location.TerritoryId)
                matches = false;

            if (!matches) continue;

            // Apply matching mode logic
            switch (boundArea.MatchingMode)
            {
                case AreaMatchingMode.ExactMatch:
                    matches = (boundArea.Location.MapId == 0 || boundArea.Location.MapId == location.MapId) &&
                             (boundArea.Location.DivisionId == 0 || boundArea.Location.DivisionId == location.DivisionId) &&
                             (boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId) &&
                             (boundArea.Location.HouseId == 0 || boundArea.Location.HouseId == location.HouseId) &&
                             (boundArea.Location.RoomId == 0 || boundArea.Location.RoomId == location.RoomId);
                    break;
                case AreaMatchingMode.TerritoryOnly:
                    // Already checked territory above
                    break;
                case AreaMatchingMode.ServerAndTerritory:
                    // Already checked server and territory above
                    break;
                case AreaMatchingMode.HousingWardOnly:
                    matches = boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId;
                    break;
                case AreaMatchingMode.HousingPlotOnly:
                    matches = (boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId) &&
                             (boundArea.Location.HouseId == 0 || boundArea.Location.HouseId == location.HouseId);
                    break;
                case AreaMatchingMode.HousingPlotOutdoor:
                    matches = (boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId) &&
                             (boundArea.Location.HouseId == 0 || boundArea.Location.HouseId == location.HouseId) &&
                             !location.IsIndoor; // Only match when player is outside
                    break;
                case AreaMatchingMode.HousingPlotIndoor:
                    matches = (boundArea.Location.WardId == 0 || boundArea.Location.WardId == location.WardId) &&
                             (boundArea.Location.HouseId == 0 || boundArea.Location.HouseId == location.HouseId) &&
                             location.IsIndoor; // Only match when player is inside
                    break;
            }

            if (matches)
            {
                // Area-bound syncshells should auto-join without property verification
                // Property verification is only needed for ownership-based features
                _logger.LogDebug("[SpheneDebug] [AreaBoundSyncshellService] Location {Location} matches area-bound syncshell {SyncshellId}, allowing auto-join", location, syncshell.GID);
                return true;
            }
        }

        return false;
    }

    private bool LocationsMatch(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId &&
               loc1.TerritoryId == loc2.TerritoryId &&
               loc1.MapId == loc2.MapId &&
               loc1.WardId == loc2.WardId &&
               loc1.HouseId == loc2.HouseId &&
               loc1.RoomId == loc2.RoomId &&
               loc1.IsIndoor == loc2.IsIndoor;
    }

    private void OnAreaBoundJoinRequest(AreaBoundJoinRequestMessage message)
    {
        _logger.LogDebug("Processing area-bound join request for syncshell {SyncshellId}", message.JoinRequest.GID);
        
        // Automatically accept area-bound join requests
        _ = Task.Run(async () =>
        {
            try
            {
                var response = new AreaBoundJoinResponseDto(
                    message.JoinRequest.Group,
                    message.JoinRequest.User,
                    true,
                    string.Empty
                );
                
                await _apiController.GroupRespondToAreaBoundJoin(response);
                _logger.LogDebug("Automatically accepted area-bound join request for syncshell {SyncshellId}", message.JoinRequest.GID);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to automatically accept area-bound join request for syncshell {SyncshellId}", message.JoinRequest.GID);
            }
        });
    }

    private void OnAreaBoundJoinResponse(AreaBoundJoinResponseMessage message)
    {
        if (message.JoinResponse.Accepted)
        {
            _currentlyJoinedAreaSyncshells.Add(message.JoinResponse.Group.GID);
            _logger.LogDebug("Successfully joined area-bound syncshell {SyncshellId}", message.JoinResponse.Group.GID);
            
            // Check if there's a welcome page for this group that should be shown on area-bound join
            _ = Task.Run(async () =>
            {
                try
                {
                    var welcomePage = await _apiController.GroupGetWelcomePage(new GroupDto(message.JoinResponse.Group));
                     // Check if welcome page should be shown based on user preference
                if (welcomePage != null && welcomePage.IsEnabled && welcomePage.ShowOnAreaBoundJoin)
                {
                    // Check user preference for showing area-bound welcome messages
                    var config = _configService.Current;
                    if (config.ShowAreaBoundSyncshellWelcomeMessages)
                    {
                        // Wait for GroupFullInfo to arrive from server with retry mechanism
                        GroupFullInfoDto? groupFullInfo = null;
                        const int maxRetries = 10;
                        const int delayMs = 100;
                        
                        for (int retry = 0; retry < maxRetries; retry++)
                        {
                            groupFullInfo = _pairManager.Groups.Values.FirstOrDefault(g => g.Group.GID == message.JoinResponse.Group.GID);
                            if (groupFullInfo != null)
                            {
                                _logger.LogDebug("Found GroupFullInfo for GID: {GID} after {Retry} retries", message.JoinResponse.Group.GID, retry);
                                break;
                            }
                            
                            if (retry < maxRetries - 1)
                            {
                                await Task.Delay(delayMs);
                            }
                        }
                        
                        if (groupFullInfo != null)
                        {
                            _logger.LogDebug("Publishing OpenWelcomePageMessage with GroupFullInfo: {GroupAliasOrGID}", groupFullInfo.Group.AliasOrGID);
                            Mediator.Publish(new OpenWelcomePageMessage(welcomePage, groupFullInfo));
                        }
                        else
                        {
                            _logger.LogWarning("Could not find GroupFullInfo for GID: {GID} after {MaxRetries} retries, using fallback", message.JoinResponse.Group.GID, maxRetries);
                            // Fallback: create a minimal GroupFullInfoDto from message.JoinResponse
                            var fallbackGroupFullInfo = new GroupFullInfoDto(
                                message.JoinResponse.Group,
                                message.JoinResponse.User, // Use User as fallback for Owner
                                GroupPermissions.NoneSet, // Default permissions
                                GroupUserPreferredPermissions.NoneSet, // Default user permissions
                                new GroupPairUserInfo(), // Empty user info
                                new Dictionary<string, GroupPairUserInfo>() // Empty pair user infos
                            );
                            Mediator.Publish(new OpenWelcomePageMessage(welcomePage, fallbackGroupFullInfo));
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Welcome message display disabled by user preference for area-bound syncshell {SyncshellId}", message.JoinResponse.Group.GID);
                    }
                }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to check welcome page for area-bound syncshell {SyncshellId}", message.JoinResponse.Group.GID);
                }
            });
            
            // Refresh pair manager data to reflect the new syncshell membership
            // This ensures the UI shows the user as a syncshell member rather than just a visible user
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // Small delay to ensure server-side changes are propagated
                await _apiController.UserGetOnlinePairs(null);
            });
        }
        else
        {
            _logger.LogWarning("Failed to join area-bound syncshell {SyncshellId}: {ErrorMessage}", message.JoinResponse.Group.GID, message.JoinResponse.Reason);
        }
    }

    private void OnConnected(ConnectedMessage message)
    {
        _logger.LogDebug("Connected to server, refreshing area-bound syncshells");
        // Add a small delay to ensure the connection is fully established
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000); // Wait 1 second
            await RefreshAreaBoundSyncshells();
        });
    }

    private void OnDisconnected(DisconnectedMessage message)
    {
        _logger.LogDebug("Disconnected from server, clearing area-bound syncshells");
        _areaBoundSyncshells.Clear();
        _currentlyJoinedAreaSyncshells.Clear();
    }

    private void OnAreaBoundSyncshellConfigurationUpdate(AreaBoundSyncshellConfigurationUpdateMessage message)
    {
        _logger.LogDebug("Received area-bound syncshell configuration update, refreshing syncshells");
        
        // Refresh area-bound syncshells in the background
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAreaBoundSyncshells();
                _logger.LogDebug("Successfully refreshed area-bound syncshells after configuration update");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing area-bound syncshells after configuration update");
            }
        });
    }

    public async Task RefreshAreaBoundSyncshells()
    {
        try
        {
            // Check if we're in a valid state for API calls (same as CheckConnection in ApiController)
            if (_apiController.ServerState is not (ServerState.Connected or ServerState.Connecting or ServerState.Reconnecting))
            {
                _logger.LogDebug("Server not in valid state for API calls (State: {State}), skipping area-bound syncshells refresh", _apiController.ServerState);
                return;
            }

            var syncshells = await _apiController.GroupGetAreaBoundSyncshells();
            _areaBoundSyncshells.Clear();
            
            foreach (var syncshell in syncshells)
            {
                _areaBoundSyncshells[syncshell.GID] = syncshell;
            }
            
            _logger.LogDebug("Refreshed {Count} area-bound syncshells", syncshells.Count);
            
            // After reconnection, check if we're still members of area-bound syncshells that are no longer valid for our location
            await ValidateCurrentAreaBoundMemberships();
            
            // Check current location against new syncshells
            // Use current location as both old and new to properly handle leaving syncshells
            if (_lastLocation != null)
            {
                await HandleLocationChange(_lastLocation.Value, _lastLocation.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing area-bound syncshells: {Message}", ex.Message);
        }
    }

    private async Task ValidateCurrentAreaBoundMemberships()
    {
        try
        {
            // Get all current syncshell memberships from server
            var allGroups = await _apiController.GroupsGetAll();
            var currentAreaBoundMemberships = allGroups
                .Where(group => _areaBoundSyncshells.ContainsKey(group.Group.GID))
                .ToList();

            _logger.LogDebug("Found {Count} current area-bound syncshell memberships to validate", currentAreaBoundMemberships.Count);

            var syncshellsToLeave = new List<string>();

            // Check each membership against current location
            if (_lastLocation != null)
            {
                foreach (var groupInfo in currentAreaBoundMemberships)
                {
                    if (_areaBoundSyncshells.TryGetValue(groupInfo.Group.GID, out var syncshell))
                    {
                        // Check if current location is still valid for this syncshell
                        if (!IsLocationInBounds(syncshell, _lastLocation.Value))
                        {
                            // Check if current user is the owner of this syncshell
                            bool isOwner = string.Equals(groupInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
                            
                            // Only leave if not the owner
                            if (!isOwner)
                            {
                                syncshellsToLeave.Add(groupInfo.Group.GID);
                                _logger.LogDebug("User is no longer in valid area for syncshell {SyncshellId}, will leave", groupInfo.Group.GID);
                            }
                            else
                            {
                                _logger.LogDebug("Skipping auto-leave for syncshell {SyncshellId} after reconnection - user is the owner", groupInfo.Group.GID);
                            }
                        }
                    }
                }

                // Leave invalid syncshells
                foreach (var syncshellId in syncshellsToLeave)
                {
                    await LeaveSyncshell(syncshellId);
                    // Remove from notified list when leaving so we can notify again if we re-enter
                    _notifiedSyncshells.Remove(syncshellId);
                    _logger.LogInformation("Left area-bound syncshell {SyncshellId} due to invalid location after reconnection", syncshellId);
                }
            }

            // Update our local tracking with the validated memberships
            _currentlyJoinedAreaSyncshells.Clear();
            var remainingMemberships = currentAreaBoundMemberships.Select(g => g.Group.GID).Except(syncshellsToLeave);
            _currentlyJoinedAreaSyncshells.UnionWith(remainingMemberships);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating current area-bound memberships: {Message}", ex.Message);
        }
    }

    public bool IsAreaBoundSyncshell(string syncshellId)
    {
        return _areaBoundSyncshells.ContainsKey(syncshellId);
    }

    public async Task<bool> ResetAreaBoundConsent(string syncshellId)
    {
        try
        {
            _logger.LogDebug("Resetting area-bound consent for syncshell: {SyncshellId}", syncshellId);
            return await _apiController.GroupResetAreaBoundConsent(syncshellId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting area-bound consent for syncshell {SyncshellId}: {Message}", syncshellId, ex.Message);
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _locationCheckTimer?.Dispose();
        }
        base.Dispose(disposing);
    }

    public bool HasAvailableAreaSyncshells()
    {
        if (_lastLocation == null)
        {
            _logger.LogDebug("HasAvailableAreaSyncshells: No location available");
            return false;
        }

        // Check if there are any area syncshells available in the current location
        // that are not already joined
        var availableSyncshells = _areaBoundSyncshells.Values
            .Where(syncshell => !_currentlyJoinedAreaSyncshells.Contains(syncshell.GID) && 
                               IsLocationInBounds(syncshell, _lastLocation.Value))
            .ToList();
            
        _logger.LogDebug("HasAvailableAreaSyncshells: Found {Count} available syncshells. Currently joined: [{Joined}], All syncshells: [{All}]", 
            availableSyncshells.Count, 
            string.Join(", ", _currentlyJoinedAreaSyncshells),
            string.Join(", ", _areaBoundSyncshells.Keys));
            
        return availableSyncshells.Any();
    }

    public void TriggerAreaSyncshellSelection()
    {
        if (_lastLocation == null)
        {
            _logger.LogDebug("Cannot trigger area syncshell selection - no location available");
            return;
        }

        // Get all available syncshells in current location that are not already joined
        var availableSyncshells = _areaBoundSyncshells.Values
            .Where(syncshell => !_currentlyJoinedAreaSyncshells.Contains(syncshell.GID) && 
                               IsLocationInBounds(syncshell, _lastLocation.Value))
            .ToList();

        if (availableSyncshells.Count == 0)
        {
            _logger.LogDebug("No available area syncshells to show selection for");
            return;
        }

        if (availableSyncshells.Count == 1)
        {
            // Single syncshell - show consent UI
            var syncshell = availableSyncshells[0];
            bool requiresRulesAcceptance = syncshell.Settings.RequireRulesAcceptance && 
                                         !string.IsNullOrEmpty(syncshell.Settings.JoinRules);
            
            var consentMessage = new AreaBoundSyncshellConsentRequestMessage(syncshell, requiresRulesAcceptance);
            _mediator.Publish(consentMessage);
        }
        else
        {
            // Multiple syncshells - show selection UI
            var selectionMessage = new AreaBoundSyncshellSelectionRequestMessage(availableSyncshells);
            _mediator.Publish(selectionMessage);
        }
    }

    private void SendAreaBoundNotification(AreaBoundSyncshellDto syncshell)
    {
        _logger.LogDebug("SendAreaBoundNotification called for syncshell: {SyncshellId} ({Alias})", syncshell.GID, syncshell.Group.Alias);
        
        // Check if area-bound notifications are enabled
        if (_configService?.Current?.ShowAreaBoundSyncshellNotifications != true)
        {
            _logger.LogDebug("Area-bound notifications are disabled in config");
            return;
        }

        var notificationLocation = _configService.Current.AreaBoundSyncshellNotification;
        var title = "Area Syncshell Available";
        var message = $"Area-bound syncshell '{syncshell.Group.Alias}' is now available in this area!";

        _logger.LogDebug("Publishing area-bound notification: {Title} - {Message} (Location: {Location})", title, message, notificationLocation);

        // Create a custom notification message for area-bound syncshells
        var notificationMessage = new AreaBoundSyncshellNotificationMessage(title, message, notificationLocation);
        _mediator.Publish(notificationMessage);
        
        _logger.LogDebug("Area-bound notification published successfully");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("AreaBoundSyncshellService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("AreaBoundSyncshellService stopped");
        return Task.CompletedTask;
    }
}