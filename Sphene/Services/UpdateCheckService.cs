using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using Sphene.Services.Mediator;
using Sphene.UI;
using Microsoft.Extensions.Hosting;
using Dalamud.Plugin;

namespace Sphene.Services;

public class UpdateCheckService : IHostedService, IDisposable
{
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SpheneMediator _mediator;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IDalamudPluginInterface _pluginInterface;
    private Timer? _updateCheckTimer;
    private const string UPDATE_CHECK_URL = "https://raw.githubusercontent.com/SpheneDev/repo/refs/heads/main/plogonmaster.json";
    private const int UPDATE_CHECK_INTERVAL_MINUTES = 5;
    
    // Debug/Testing properties
    public bool DebugMode { get; set; } = false;
    public Version? DebugCurrentVersion { get; set; } = null;
    public Version? DebugDalamudVersion { get; set; } = null;
    
    public UpdateCheckService(ILogger<UpdateCheckService> logger, HttpClient httpClient, SpheneMediator mediator, DalamudUtilService dalamudUtilService, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _httpClient = httpClient;
        _mediator = mediator;
        _dalamudUtilService = dalamudUtilService;
        _pluginInterface = pluginInterface;
    }
    
    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool skipCombatCheck = false)
    {
        try
        {
            // Skip update check if player is in combat or performing, unless explicitly overridden
            if (!skipCombatCheck && _dalamudUtilService.IsInCombatOrPerforming)
            {
                _logger.LogDebug("Skipping update check - player is in combat or performing");
                return null;
            }
            
            _logger.LogInformation("Checking for updates from {url}", UPDATE_CHECK_URL);
            
            var response = await _httpClient.GetStringAsync(UPDATE_CHECK_URL);
            var updateData = JsonSerializer.Deserialize<UpdateData[]>(response);
            
            if (updateData == null || updateData.Length == 0)
            {
                _logger.LogWarning("No update data found in response");
                return null;
            }
            
            var spheneData = updateData.FirstOrDefault(x => x.InternalName == "Sphene");
            if (spheneData == null)
            {
                _logger.LogWarning("Sphene data not found in update response");
                return null;
            }
            
            var currentVersion = GetCurrentVersion();
            var remoteVersion = Version.Parse(spheneData.AssemblyVersion);
            
            _logger.LogInformation("Current version: {current}, Remote version: {remote}", currentVersion, remoteVersion);
            
            if (remoteVersion > currentVersion)
            {
                // Check if Dalamud has the update available before showing notification
                var dalamudHasUpdate = CheckDalamudHasUpdate(remoteVersion);
                if (!dalamudHasUpdate)
                {
                    _logger.LogDebug("Update {version} available but not yet available in Dalamud, skipping notification", remoteVersion);
                    return new UpdateInfo
                    {
                        CurrentVersion = currentVersion,
                        LatestVersion = remoteVersion,
                        IsUpdateAvailable = false
                    };
                }
                
                _logger.LogInformation("Update available: {version}", remoteVersion);
                var updateInfo = new UpdateInfo
                {
                    CurrentVersion = currentVersion,
                    LatestVersion = remoteVersion,
                    Changelog = spheneData.Changelog,
                    DownloadUrl = spheneData.DownloadLinkUpdate,
                    IsUpdateAvailable = true
                };
                
                // Publish update info; UI will show a banner and provide a button to open the popup
                _mediator.Publish(new ShowUpdateNotificationMessage(updateInfo));
                
                return updateInfo;
            }
            
            _logger.LogInformation("No update available");
            return new UpdateInfo
            {
                CurrentVersion = currentVersion,
                LatestVersion = remoteVersion,
                IsUpdateAvailable = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for updates");
            return null;
        }
    }
    

    private Version GetCurrentVersion()
    {
        if (DebugMode && DebugCurrentVersion != null)
        {
            _logger.LogDebug("Using debug current version: {version}", DebugCurrentVersion);
            return DebugCurrentVersion;
        }
        
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version ?? new Version(0, 0, 0, 0);
    }
    
    private bool CheckDalamudHasUpdate(Version remoteVersion)
    {
        try
        {
            Version dalamudVersion;
            
            if (DebugMode && DebugDalamudVersion != null)
            {
                dalamudVersion = DebugDalamudVersion;
                _logger.LogDebug("Using debug Dalamud version: {dalamudVersion}, Remote version: {remoteVersion}", dalamudVersion, remoteVersion);
            }
            else
            {
                var sphenePlugin = _pluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "Sphene");
                if (sphenePlugin == null)
                {
                    _logger.LogDebug("Sphene plugin not found in Dalamud's installed plugins list");
                    return false;
                }
                
                dalamudVersion = sphenePlugin.Version;
                _logger.LogDebug("Dalamud has Sphene version: {dalamudVersion}, Remote version: {remoteVersion}", dalamudVersion, remoteVersion);
            }
            
            // Only show update if Dalamud has the same or newer version available
            return dalamudVersion >= remoteVersion;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check Dalamud plugin version, allowing update notification");
            // If we can't check Dalamud's version, allow the update notification to prevent blocking legitimate updates
            return true;
        }
    }
    
    private async void OnUpdateCheckTimer(object? state)
    {
        try
        {
            _logger.LogDebug("Periodic update check triggered");
            await CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during periodic update check");
        }
    }
    
    // Public method for testing update scenarios
    public async Task<UpdateInfo?> TestUpdateCheckAsync(Version? testCurrentVersion = null, Version? testDalamudVersion = null)
    {
        var originalDebugMode = DebugMode;
        var originalDebugCurrentVersion = DebugCurrentVersion;
        var originalDebugDalamudVersion = DebugDalamudVersion;
        
        try
        {
            DebugMode = true;
            DebugCurrentVersion = testCurrentVersion;
            DebugDalamudVersion = testDalamudVersion;
            
            _logger.LogInformation("Testing update check with Current: {current}, Dalamud: {dalamud}", 
                testCurrentVersion?.ToString() ?? "actual", 
                testDalamudVersion?.ToString() ?? "actual");
            
            return await CheckForUpdatesAsync();
        }
        finally
        {
            // Restore original debug settings
            DebugMode = originalDebugMode;
            DebugCurrentVersion = originalDebugCurrentVersion;
            DebugDalamudVersion = originalDebugDalamudVersion;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UpdateCheckService started - will check for updates every {minutes} minutes", UPDATE_CHECK_INTERVAL_MINUTES);
        
        // Start the timer for periodic update checks
        _updateCheckTimer = new Timer(
            OnUpdateCheckTimer,
            null,
            TimeSpan.FromMinutes(1), // First check after 1 minute
            TimeSpan.FromMinutes(UPDATE_CHECK_INTERVAL_MINUTES) // Then every 5 minutes
        );
        
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("UpdateCheckService stopping");
        _updateCheckTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        _updateCheckTimer?.Dispose();
    }
}

public class UpdateInfo
{
    public Version CurrentVersion { get; set; } = new(0, 0, 0, 0);
    public Version LatestVersion { get; set; } = new(0, 0, 0, 0);
    public string? Changelog { get; set; }
    public string? DownloadUrl { get; set; }
    public bool IsUpdateAvailable { get; set; }
}

public class UpdateData
{
    public string Author { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string InternalName { get; set; } = string.Empty;
    public string AssemblyVersion { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ApplicableVersion { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public int DalamudApiLevel { get; set; }
    public int LoadPriority { get; set; }
    public string IconUrl { get; set; } = string.Empty;
    public string Punchline { get; set; } = string.Empty;
    public string Changelog { get; set; } = string.Empty;
    public string DownloadLinkInstall { get; set; } = string.Empty;
    public string DownloadLinkTesting { get; set; } = string.Empty;
    public string DownloadLinkUpdate { get; set; } = string.Empty;
    public string TestingAssemblyVersion { get; set; } = string.Empty;
    public string TestingDalamudApiLevel { get; set; } = string.Empty;
}