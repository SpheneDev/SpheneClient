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
    private Timer? _updateCheckTimer;
    private const string UPDATE_CHECK_URL = "https://raw.githubusercontent.com/SpheneDev/repo/refs/heads/main/plogonmaster.json";
    private const int UPDATE_CHECK_INTERVAL_MINUTES = 5;
    
    public UpdateCheckService(ILogger<UpdateCheckService> logger, HttpClient httpClient, SpheneMediator mediator, DalamudUtilService dalamudUtilService, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _httpClient = httpClient;
        _mediator = mediator;
        _dalamudUtilService = dalamudUtilService;
        _ = pluginInterface;
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
            
            _logger.LogDebug("Checking for updates from {url}", UPDATE_CHECK_URL);
            
            var response = await _httpClient.GetStringAsync(UPDATE_CHECK_URL).ConfigureAwait(false);
            var updateData = JsonSerializer.Deserialize<UpdateData[]>(response);
            
            if (updateData == null || updateData.Length == 0)
            {
                _logger.LogWarning("No update data found in response");
                return null;
            }
            
            var spheneData = updateData.FirstOrDefault(x => string.Equals(x.InternalName, "Sphene", StringComparison.Ordinal));
            if (spheneData == null)
            {
                _logger.LogWarning("Sphene data not found in update response");
                return null;
            }
            
            var currentVersion = GetCurrentVersion();
            var remoteVersion = Version.Parse(spheneData.AssemblyVersion);
            
            _logger.LogDebug("Current version: {current}, Remote version: {remote}", currentVersion, remoteVersion);
            
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
            
            _logger.LogDebug("No update available");
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
    

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version ?? new Version(0, 0, 0, 0);
    }
    
    private bool CheckDalamudHasUpdate(Version remoteVersion)
    {
        try
        {
            _logger.LogDebug("Remote version {remoteVersion} available, allowing update notification", remoteVersion);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check Dalamud plugin version, allowing update notification");
            return true;
        }
    }
    
    private void OnUpdateCheckTimer(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Periodic update check triggered");
                await CheckForUpdatesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic update check");
            }
        });
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
    
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updateCheckTimer?.Dispose();
            _updateCheckTimer = null;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
