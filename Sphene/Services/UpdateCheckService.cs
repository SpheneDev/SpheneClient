using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using Sphene.Services.Mediator;
using Sphene.UI;

namespace Sphene.Services;

public class UpdateCheckService
{
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SpheneMediator _mediator;
    private const string UPDATE_CHECK_URL = "https://raw.githubusercontent.com/SpheneDev/repo/refs/heads/main/plogonmaster.json";
    
    public UpdateCheckService(ILogger<UpdateCheckService> logger, HttpClient httpClient, SpheneMediator mediator)
    {
        _logger = logger;
        _httpClient = httpClient;
        _mediator = mediator;
    }
    
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
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
                _logger.LogInformation("Update available: {version}", remoteVersion);
                var updateInfo = new UpdateInfo
                {
                    CurrentVersion = currentVersion,
                    LatestVersion = remoteVersion,
                    Changelog = spheneData.Changelog,
                    DownloadUrl = spheneData.DownloadLinkUpdate,
                    IsUpdateAvailable = true
                };
                
                // Show update notification UI
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
    
    // Test method to manually trigger update check
    public async Task TestUpdateCheckAsync()
    {
        _logger.LogInformation("Manual update check triggered for testing");
        await CheckForUpdatesAsync();
    }
    
    private Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version ?? new Version(0, 0, 0, 0);
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