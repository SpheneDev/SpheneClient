using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using Sphene.Services.Mediator;
using Sphene.UI;
using Microsoft.Extensions.Hosting;
using Dalamud.Plugin;
using Sphene.SpheneConfiguration;

namespace Sphene.Services;

public class UpdateCheckService : IHostedService, IDisposable
{
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SpheneMediator _mediator;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SpheneConfigService _configService;
    private readonly bool _isTestBuildClient;
    private Timer? _updateCheckTimer;
    private const string UPDATE_CHECK_URL = "https://raw.githubusercontent.com/SpheneDev/repo/refs/heads/main/plogonmaster.json";
    // Test builds publish to their own manifest as "SpheneTest" - the release
    // pipeline has not maintained plogonmaster's TestingAssemblyVersion since
    // the split, so watching the stable file never surfaces a test update.
    private const string TEST_UPDATE_CHECK_URL = "https://raw.githubusercontent.com/SpheneDev/repo/refs/heads/main/plogontest.json";
    private const int UPDATE_CHECK_INTERVAL_MINUTES = 5;

    public UpdateCheckService(ILogger<UpdateCheckService> logger, HttpClient httpClient, SpheneMediator mediator, DalamudUtilService dalamudUtilService, SpheneConfigService configService, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _httpClient = httpClient;
        _mediator = mediator;
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _isTestBuildClient = IsTestBuildClient();
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
            
            var currentVersion = GetCurrentVersion();
            var latestVersion = new Version(0, 0, 0, 0);
            var isTestBuildUpdate = false;
            var downloadUrl = string.Empty;
            var changelog = string.Empty;
            var anyChannel = false;

            // Stable channel - every client watches it.
            var stableData = await FetchManifestEntry(UPDATE_CHECK_URL, "Sphene").ConfigureAwait(false);
            if (stableData != null && Version.TryParse(stableData.AssemblyVersion, out var stableVersion))
            {
                anyChannel = true;
                latestVersion = stableVersion;
                downloadUrl = stableData.DownloadLinkUpdate;
                changelog = stableData.Changelog;
            }

            // Test channel - test clients ALWAYS watch it (the display option
            // only gates stable clients opting in), stable clients opt in via
            // ShowTestBuildUpdates.
            if (ShouldCheckTestBuildUpdates())
            {
                var testData = await FetchManifestEntry(TEST_UPDATE_CHECK_URL, "SpheneTest").ConfigureAwait(false);
                if (testData != null && Version.TryParse(testData.AssemblyVersion, out var testingVersion))
                {
                    anyChannel = true;
                    if (testingVersion > latestVersion)
                    {
                        latestVersion = testingVersion;
                        isTestBuildUpdate = true;
                        downloadUrl = string.IsNullOrWhiteSpace(testData.DownloadLinkUpdate)
                            ? testData.DownloadLinkTesting : testData.DownloadLinkUpdate;
                        changelog = testData.Changelog;
                    }
                }
            }

            if (!anyChannel)
            {
                _logger.LogWarning("No update data found in any channel manifest");
                return null;
            }

            _logger.LogDebug("Current version: {current}, Latest version considered: {latest}, IsTestBuildUpdate: {isTestBuildUpdate}", currentVersion, latestVersion, isTestBuildUpdate);
            
            if (latestVersion > currentVersion)
            {
                // Check if Dalamud has the update available before showing notification
                var dalamudHasUpdate = CheckDalamudHasUpdate(latestVersion);
                if (!dalamudHasUpdate)
                {
                    _logger.LogDebug("Update {version} available but not yet available in Dalamud, skipping notification", latestVersion);
                    return new UpdateInfo
                    {
                        CurrentVersion = currentVersion,
                        LatestVersion = latestVersion,
                        IsUpdateAvailable = false
                    };
                }
                
                _logger.LogInformation("Update available: {version}, TestBuildUpdate: {isTestBuildUpdate}", latestVersion, isTestBuildUpdate);
                var updateInfo = new UpdateInfo
                {
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    Changelog = changelog,
                    DownloadUrl = downloadUrl,
                    IsUpdateAvailable = true,
                    IsTestBuildUpdate = isTestBuildUpdate
                };
                
                // Publish update info; UI will show a banner and provide a button to open the popup
                _mediator.Publish(new ShowUpdateNotificationMessage(updateInfo));
                
                return updateInfo;
            }
            
            _logger.LogDebug("No update available");
            return new UpdateInfo
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                IsUpdateAvailable = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for updates");
            return null;
        }
    }
    

    /// <summary>Fetches one plugin-repository manifest and returns the entry
    /// matching <paramref name="internalName"/> - null when the file or the
    /// entry is unreachable/unparseable, so one dead channel never silences
    /// the other.</summary>
    private async Task<UpdateData?> FetchManifestEntry(string url, string internalName)
    {
        try
        {
            _logger.LogDebug("Checking for updates from {url}", url);
            var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            var updateData = JsonSerializer.Deserialize<UpdateData[]>(response);
            var entry = updateData?.FirstOrDefault(x =>
                string.Equals(x.InternalName, internalName, StringComparison.Ordinal));
            if (entry == null)
            {
                _logger.LogWarning("{name} entry not found in {url}", internalName, url);
            }
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update manifest fetch failed for {url}", url);
            return null;
        }
    }

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version ?? new Version(0, 0, 0, 0);
    }

    private static bool IsTestBuildClient()
    {
        foreach (var metadata in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, "IsTestBuild", StringComparison.Ordinal) &&
                string.Equals(metadata.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool ShouldCheckTestBuildUpdates()
    {
        return _isTestBuildClient || _configService.Current.ShowTestBuildUpdates;
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
