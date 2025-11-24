using System;

namespace Sphene.Services;

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
