using System.Collections.Generic;

namespace Sphene.Services;

public sealed class ReleaseChangelogEntry
{
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ReleaseChange> Changes { get; set; } = new();
    public bool IsPrerelease { get; set; }
}
