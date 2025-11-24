using System.Collections.Generic;

namespace Sphene.Services;

public sealed class ReleaseChangeView
{
    public string Text { get; set; } = string.Empty;
    public List<string> Sub { get; set; } = new();
}
