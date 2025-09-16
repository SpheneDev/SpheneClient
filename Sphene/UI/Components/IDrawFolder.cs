
using System.Collections.Immutable;

namespace Sphene.UI.Components;

public interface IDrawFolder : IDisposable
{
    int TotalPairs { get; }
    int OnlinePairs { get; }
    IImmutableList<DrawUserPair> DrawPairs { get; }
    void Draw();
    void RefreshIcons();
}
