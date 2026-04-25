using System.Collections.Generic;
using System.Linq;

namespace Sphene.Services;

public static class GameSoundRegistry
{
    private static readonly List<PredefinedGameSound> _sounds =
    [
        new() { Name = "Default", Path = "sound/system/SE_UI.scd", Index = 52 }
    ];

    public static IReadOnlyList<PredefinedGameSound> Sounds => _sounds;

    public static PredefinedGameSound? FindByName(string name)
    {
        return _sounds.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
    }

    public static void Add(PredefinedGameSound sound)
    {
        _sounds.Add(sound);
    }
}
