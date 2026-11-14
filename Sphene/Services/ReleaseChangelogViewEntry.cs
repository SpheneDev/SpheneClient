using System.Collections.Generic;

namespace Sphene.Services;

public sealed class ReleaseChangelogViewEntry
{
    public string Version { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ReleaseChangeView> Changes { get; set; } = new();
    public bool IsPrerelease { get; set; }

    // structure v3 · optional presentation fields (mockup layout)
    public string Banner { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public bool Pinned { get; set; }
    public List<ReleaseHighlightView> Highlights { get; set; } = new();
    public List<string> Gallery { get; set; } = new();
    public List<ReleaseLinkView> Links { get; set; } = new();
}
