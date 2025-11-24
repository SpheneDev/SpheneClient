using System.Collections.Generic;

namespace Sphene.Services;

public sealed class ReleaseChangelogDocument
{
    public List<ReleaseChangelogEntry> Changelogs { get; set; } = new();
}
