using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using Sphene.API.Data;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.User;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.SpheneConfiguration.Models;
using Sphene.WebAPI;

namespace Sphene.Services;

// Service to handle housing ownership verification for area syncshell creation
public partial class HousingOwnershipService
{
    private readonly ILogger<HousingOwnershipService> _logger;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SpheneConfigService _configService;
    private readonly ApiController _apiController;
    private readonly ICondition _condition;
    
    // Cache of verified owned properties to avoid repeated checks
    private readonly Dictionary<string, DateTime> _verifiedOwnedProperties = new();
    private readonly TimeSpan _verificationCacheExpiry = TimeSpan.FromMinutes(30);
    
    // Server-side housing properties cache
    private List<UserHousingPropertyDto>? _serverHousingProperties = null;
    private DateTime _lastServerSync = DateTime.MinValue;
    private readonly TimeSpan _serverSyncInterval = TimeSpan.FromMinutes(5);
    
    public HousingOwnershipService(
        ILogger<HousingOwnershipService> logger,
        DalamudUtilService dalamudUtilService,
        SpheneConfigService configService,
        ApiController apiController,
        ICondition condition)
    {
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _apiController = apiController;
        _condition = condition;
    }
    
    // Check if the current player can create an area syncshell at the given location
    public async Task<OwnershipVerificationResult> VerifyOwnershipAsync(LocationInfo location)
    {
        try
        {
            // Non-housing areas are always allowed (no ownership restrictions)
            if (!IsHousingArea(location))
            {
                return new OwnershipVerificationResult(true, "Non-housing areas are unrestricted");
            }
            
            // Check persistent ownership verification first
            if (IsInVerifiedOwnedProperties(location))
            {
                return new OwnershipVerificationResult(true, "Ownership previously verified and cached");
            }
            
            // Check cache for temporary verification
            var locationKey = GetLocationKey(location);
            if (_verifiedOwnedProperties.TryGetValue(locationKey, out var cachedTime))
            {
                if (DateTime.UtcNow - cachedTime < _verificationCacheExpiry)
                {
                    return new OwnershipVerificationResult(true, "Ownership verified (session cache)");
                }
                else
                {
                    _verifiedOwnedProperties.Remove(locationKey);
                }
            }
            
            // Perform ownership verification
            var verificationResult = await PerformOwnershipVerificationAsync(location);
            
            // Cache positive results in session cache
            if (verificationResult.IsOwner)
            {
                _verifiedOwnedProperties[locationKey] = DateTime.UtcNow;
            }
            
            return verificationResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying ownership for location {Location}", location);
            return new OwnershipVerificationResult(false, "Error during ownership verification");
        }
    }
    
    // Method for one-time ownership verification - user enters housing menu to verify ownership
    public async Task<OwnershipVerificationResult> PerformOneTimeOwnershipVerificationAsync(LocationInfo location)
    {
        try
        {
            // Non-housing areas don't need verification
            if (!IsHousingArea(location))
            {
                return new OwnershipVerificationResult(true, "Non-housing areas are unrestricted");
            }
            
            // Check if already verified and stored persistently
            if (IsInVerifiedOwnedProperties(location))
            {
                return new OwnershipVerificationResult(true, "Ownership already verified and stored");
            }
            
            // Perform the verification process
            var verificationResult = await PerformOwnershipVerificationAsync(location);
            
            // If verification successful, store it permanently
            if (verificationResult.IsOwner)
            {
                AddVerifiedOwnedProperty(location);
                _logger.LogInformation("Successfully verified and stored ownership for Ward {Ward}, House {House}, Room {Room}", 
                    location.WardId, location.HouseId, location.RoomId);
                return new OwnershipVerificationResult(true, "Ownership verified and stored permanently");
            }
            
            return verificationResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during one-time ownership verification for location {Location}", location);
            return new OwnershipVerificationResult(false, "Error during ownership verification");
        }
    }
    
    private async Task<OwnershipVerificationResult> PerformOwnershipVerificationAsync(LocationInfo location)
    {
        // Since direct ownership detection isn't available through Dalamud APIs, 
        // we use a strict multi-step verification approach to prevent abuse:
        
        // 1. Check if player is currently in the housing area they want to bind
        var currentLocation = _dalamudUtilService.GetMapData();
        if (!IsLocationInSameHousingArea(location, currentLocation))
        {
            return new OwnershipVerificationResult(false, 
                "You must be in the housing area you want to verify to perform ownership verification");
        }
        
        // 2. Check UsingHousingFunctions condition - only active when player owns or has permissions
        var housingFunctionsActive = _condition[ConditionFlag.UsingHousingFunctions];
        _logger.LogDebug("UsingHousingFunctions condition state: {IsActive}", housingFunctionsActive);
        
        if (!housingFunctionsActive)
        {
            _logger.LogDebug("UsingHousingFunctions condition not active - player needs to enter housing menu");
            return new OwnershipVerificationResult(false, 
                "Please enter the housing menu (right-click on your house placard or use housing functions) to verify ownership, then try again.");
        }
        
        _logger.LogDebug("UsingHousingFunctions condition is active - player has housing permissions");
        
        // 3. For indoor areas, apply strict validation
        if (location.IsIndoor && location.HouseId > 0)
        {
            // Player must be inside their own house to verify indoor ownership
            if (!currentLocation.IsIndoor)
            {
                return new OwnershipVerificationResult(false, 
                    "You must be inside the house to verify indoor ownership");
            }
            
            // Verify exact location match for indoor areas
            if (currentLocation.HouseId != location.HouseId || 
                currentLocation.WardId != location.WardId ||
                currentLocation.RoomId != location.RoomId)
            {
                return new OwnershipVerificationResult(false, 
                    "You must be in the exact room you want to verify");
            }
        }
        
        // 4. For outdoor plots, verify player is on the correct plot
        if (!location.IsIndoor && location.HouseId > 0)
        {
            if (currentLocation.HouseId != location.HouseId || currentLocation.WardId != location.WardId)
            {
                return new OwnershipVerificationResult(false, 
                    "You must be on the specific plot you want to verify");
            }
        }
        
        // 5. If UsingHousingFunctions is active and location checks pass, ownership is verified
        return new OwnershipVerificationResult(true, 
            "Ownership verified - housing functions are active and location matches");
    }
    
    public bool IsHousingArea(LocationInfo location)
    {
        return location.WardId > 0;
    }
    
    private bool IsLocationInSameHousingArea(LocationInfo targetLocation, LocationInfo currentLocation)
    {
        // Only allow area syncshells in housing areas
        if (!IsHousingArea(targetLocation))
        {
            return false; // Non-housing areas are not allowed for area syncshells
        }
        
        // Player must also be in a housing area to create area syncshells
        if (!IsHousingArea(currentLocation))
        {
            return false;
        }
        
        // Same server and territory
        if (targetLocation.ServerId != currentLocation.ServerId || 
            targetLocation.TerritoryId != currentLocation.TerritoryId)
        {
            return false;
        }
        
        // Same ward
        if (targetLocation.WardId != currentLocation.WardId)
        {
            return false;
        }
        
        // If targeting a specific house, must be in that house area
        if (targetLocation.HouseId > 0)
        {
            return currentLocation.HouseId == targetLocation.HouseId;
        }
        
        return true;
    }
    
    private bool IsInVerifiedOwnedProperties(LocationInfo location)
    {
        // First check server-side properties
        if (_serverHousingProperties != null)
        {
            var serverProperty = _serverHousingProperties.FirstOrDefault(p => LocationsMatch(p.Location, location));
            if (serverProperty != null)
            {
                return true;
            }
        }
        
        // Fallback to local config for backward compatibility
        var verifiedProperties = _configService.Current.VerifiedOwnedHousingProperties ?? new List<VerifiedHousingProperty>();
        return verifiedProperties.Any(owned => LocationsMatch(owned.Location, location));
    }
    
    private bool IsInConfiguredOwnedProperties(LocationInfo location)
    {
        // Check against user's manually configured owned properties (legacy)
        var ownedProperties = _configService.Current.OwnedHousingProperties ?? new List<LocationInfo>();
        return ownedProperties.Any(owned => LocationsMatch(owned, location));
    }
    
    private bool LocationsMatch(LocationInfo loc1, LocationInfo loc2)
    {
        return loc1.ServerId == loc2.ServerId &&
               loc1.TerritoryId == loc2.TerritoryId &&
               loc1.WardId == loc2.WardId &&
               loc1.HouseId == loc2.HouseId &&
               loc1.RoomId == loc2.RoomId;
    }
    
    private string GetLocationKey(LocationInfo location)
    {
        return $"{location.ServerId}_{location.TerritoryId}_{location.WardId}_{location.HouseId}_{location.RoomId}";
    }
    
    // Add a verified property to the persistent storage
    private async void AddVerifiedOwnedProperty(LocationInfo location, bool allowOutdoor = true, bool allowIndoor = true, bool preferOutdoorSyncshells = true, bool preferIndoorSyncshells = true)
    {
        try
        {
            var result = await _apiController.UserSetHousingProperty(new UserHousingPropertyUpdateDto
            {
                Location = location,
                AllowOutdoor = allowOutdoor,
                AllowIndoor = allowIndoor,
                PreferOutdoorSyncshells = preferOutdoorSyncshells,
                PreferIndoorSyncshells = preferIndoorSyncshells
            }).ConfigureAwait(false);

            if (result != null)
            {
                _logger.LogDebug("Successfully saved housing property to server: {location}", location);
                await SyncWithServer().ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save housing property to server: {location}", location);
        }
        
        // Fallback to local storage
        var verifiedProperties = _configService.Current.VerifiedOwnedHousingProperties?.ToList() ?? new List<VerifiedHousingProperty>();
        
        if (!verifiedProperties.Any(owned => LocationsMatch(owned.Location, location)))
        {
            verifiedProperties.Add(new VerifiedHousingProperty(location, allowOutdoor, allowIndoor, preferOutdoorSyncshells, preferIndoorSyncshells));
            _configService.Current.VerifiedOwnedHousingProperties = verifiedProperties;
            _configService.Save();
            
            _logger.LogInformation("Added verified owned property locally: Ward {Ward}, House {House}, Room {Room}, Outdoor: {Outdoor}, Indoor: {Indoor}", 
                location.WardId, location.HouseId, location.RoomId, allowOutdoor, allowIndoor);
        }
    }
    
    // Add a verified room to the persistent storage without outdoor/indoor preferences
    private void AddVerifiedOwnedPropertyForRoom(LocationInfo location)
    {
        // Use server-first approach for rooms - rooms are always indoor, so set AllowIndoor to true
        AddVerifiedOwnedProperty(location, false, true, true, true);
        
        _logger.LogInformation("Added verified owned room: Ward {Ward}, House {House}, Room {Room} (indoor only)", 
            location.WardId, location.HouseId, location.RoomId);
    }
    
    // Add a property to the user's owned properties list (legacy method)
    public void AddOwnedProperty(LocationInfo location)
    {
        var ownedProperties = _configService.Current.OwnedHousingProperties?.ToList() ?? new List<LocationInfo>();
        
        if (!ownedProperties.Any(owned => LocationsMatch(owned, location)))
        {
            ownedProperties.Add(location);
            _configService.Current.OwnedHousingProperties = ownedProperties;
            _configService.Save();
            
            _logger.LogInformation("Added owned property: Ward {Ward}, House {House}, Room {Room}", 
                location.WardId, location.HouseId, location.RoomId);
        }
    }
    
    // Remove a verified property from persistent storage
    public async void RemoveVerifiedOwnedProperty(LocationInfo location)
    {
        try
        {
            // Try to remove from server first
            var success = await _apiController.UserDeleteHousingProperty(location);
            if (success)
            {
                // Update local cache
                await SyncWithServer();
                
                // Also clear from session cache
                var locationKey = GetLocationKey(location);
                _verifiedOwnedProperties.Remove(locationKey);
                
                _logger.LogInformation("Removed verified owned property from server: Ward {Ward}, House {House}, Room {Room}", 
                    location.WardId, location.HouseId, location.RoomId);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove housing property from server, falling back to local removal");
        }
        
        // Fallback to local removal
        var verifiedProperties = _configService.Current.VerifiedOwnedHousingProperties?.ToList() ?? new List<VerifiedHousingProperty>();
        var toRemove = verifiedProperties.FirstOrDefault(owned => LocationsMatch(owned.Location, location));
        
        if (toRemove != null)
        {
            verifiedProperties.Remove(toRemove);
            _configService.Current.VerifiedOwnedHousingProperties = verifiedProperties;
            _configService.Save();
            
            // Also clear from session cache
            var locationKey = GetLocationKey(location);
            _verifiedOwnedProperties.Remove(locationKey);
            
            _logger.LogInformation("Removed verified owned property locally: Ward {Ward}, House {House}, Room {Room}", 
                location.WardId, location.HouseId, location.RoomId);
        }
    }
    
    // Remove a property from the user's owned properties list (legacy method)
    public void RemoveOwnedProperty(LocationInfo location)
    {
        var ownedProperties = _configService.Current.OwnedHousingProperties?.ToList() ?? new List<LocationInfo>();
        var toRemove = ownedProperties.FirstOrDefault(owned => LocationsMatch(owned, location));
        
        if (toRemove != null)
        {
            ownedProperties.Remove(toRemove);
            _configService.Current.OwnedHousingProperties = ownedProperties;
            _configService.Save();
            
            // Also clear from cache
            var locationKey = GetLocationKey(location);
            _verifiedOwnedProperties.Remove(locationKey);
            
            _logger.LogInformation("Removed owned property: Ward {Ward}, House {House}, Room {Room}", 
                location.WardId, location.HouseId, location.RoomId);
        }
    }
    
    // Clear the verification cache (useful when ownership changes)
    public void ClearVerificationCache()
    {
        _verifiedOwnedProperties.Clear();
        _logger.LogDebug("Cleared housing ownership verification cache");
    }
    
    // Clear all verified properties (for testing or if user wants to re-verify)
    public async void ClearAllVerifiedProperties()
    {
        try
        {
            // Clear from server first
            var serverProperties = await GetVerifiedOwnedPropertiesAsync();
            foreach (var property in serverProperties)
            {
                await _apiController.UserDeleteHousingProperty(property.Location);
            }
            
            _logger.LogInformation("Cleared all verified properties from server");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear properties from server, clearing local cache only");
        }
        
        // Clear local cache and config
        _configService.Current.VerifiedOwnedHousingProperties = new List<VerifiedHousingProperty>();
        _configService.Save();
        ClearVerificationCache();
        _logger.LogInformation("Cleared all verified owned properties");
    }
    
    // Get the list of verified owned properties for UI display
    public async Task<List<VerifiedHousingProperty>> GetVerifiedOwnedPropertiesAsync()
    {
        // Sync with server if needed
        await SyncWithServer();
        
        var result = new List<VerifiedHousingProperty>();
        
        // Add server-side properties
        if (_serverHousingProperties != null)
        {
            result.AddRange(_serverHousingProperties.Select(p => new VerifiedHousingProperty(p.Location, p.AllowOutdoor, p.AllowIndoor, p.PreferOutdoorSyncshells, p.PreferIndoorSyncshells)));
        }
        
        // Add local properties that aren't already in server list (for backward compatibility)
        var localProperties = _configService.Current.VerifiedOwnedHousingProperties ?? new List<VerifiedHousingProperty>();
        foreach (var localProp in localProperties)
        {
            if (!result.Any(r => LocationsMatch(r.Location, localProp.Location)))
            {
                result.Add(localProp);
            }
        }
        
        return result;
    }
    
    // Synchronous version for backward compatibility
    public List<VerifiedHousingProperty> GetVerifiedOwnedProperties()
    {
        // Return cached server properties if available, otherwise local properties
        if (_serverHousingProperties != null)
        {
            var result = _serverHousingProperties.Select(p => new VerifiedHousingProperty(p.Location, p.AllowOutdoor, p.AllowIndoor, p.PreferOutdoorSyncshells, p.PreferIndoorSyncshells)).ToList();
            
            // Add local properties that aren't in server list
            var localProperties = _configService.Current.VerifiedOwnedHousingProperties ?? new List<VerifiedHousingProperty>();
            foreach (var localProp in localProperties)
            {
                if (!result.Any(r => LocationsMatch(r.Location, localProp.Location)))
                {
                    result.Add(localProp);
                }
            }
            
            return result;
        }
        
        return _configService.Current.VerifiedOwnedHousingProperties?.ToList() ?? new List<VerifiedHousingProperty>();
    }
    
    // Sync housing properties with server
    public async Task SyncWithServer()
    {
        try
        {
            if (DateTime.UtcNow - _lastServerSync < _serverSyncInterval)
            {
                return; // Skip if synced recently
            }
            
            _serverHousingProperties = await _apiController.UserGetHousingProperties();
            _lastServerSync = DateTime.UtcNow;
            
            _logger.LogDebug("Synced {Count} housing properties from server", _serverHousingProperties?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync housing properties from server");
        }
    }
    
    // Get the list of owned properties for UI display (legacy)
    public List<LocationInfo> GetOwnedProperties()
    {
        return _configService.Current.OwnedHousingProperties?.ToList() ?? new List<LocationInfo>();
    }
}

// Result of ownership verification
public record OwnershipVerificationResult(bool IsOwner, string Reason);