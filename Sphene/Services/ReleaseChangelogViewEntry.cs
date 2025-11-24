using System.Collections.Generic;

namespace Sphene.Services;

public sealed class ReleaseChangelogViewEntry
{
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ReleaseChangeView> Changes { get; set; } = new();
    public bool IsPrerelease { get; set; }
}
