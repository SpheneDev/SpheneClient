namespace Sphene.Services;

/// <summary>
/// Registry of all built-in nameplate effects. An effect's string id is the
/// stable handle supporter styles name - when the server ships per-tier
/// assignments, the synced style's FxId resolves through <see cref="ById"/>.
/// </summary>
public static class NameplateFx
{
    public static readonly INameplateFx[] All =
    [
        new FxBatOrbit(),
        new FxStarOrnaments(),
        new FxEmbers(),
        new FxPetals(),
        new FxHeartsFloat(),
        new FxSnowDrift(),
    ];

    public static INameplateFx? ById(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        for (var i = 0; i < All.Length; i++)
        {
            if (string.Equals(All[i].Id, id, StringComparison.Ordinal))
                return All[i];
        }
        return null;
    }
}
