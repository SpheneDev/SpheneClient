using System;

namespace Sphene.Services;

public class UpdateInfo
{
    public Version CurrentVersion { get; set; } = new(0, 0, 0, 0);
    public Version LatestVersion { get; set; } = new(0, 0, 0, 0);
    public string? Changelog { get; set; }
    public string? DownloadUrl { get; set; }
    public bool IsUpdateAvailable { get; set; }
}
