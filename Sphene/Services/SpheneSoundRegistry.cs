namespace Sphene.Services;

public static class SpheneSoundRegistry
{
    private static readonly List<SpheneBuiltinSound> _sounds =
    [
        new() { Name = "Default", ResourceName = "Sphene.Resources.Sounds.not1.wav" },
        new() { Name = "Attention", ResourceName = "Sphene.Resources.Sounds.not2.wav" },
        new() { Name = "Error", ResourceName = "Sphene.Resources.Sounds.not3.wav" }
    ];

    public static IReadOnlyList<SpheneBuiltinSound> Sounds => _sounds;

    public static SpheneBuiltinSound? FindByName(string name)
    {
        return _sounds.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
    }

    public static void Add(SpheneBuiltinSound sound)
    {
        _sounds.Add(sound);
    }
}
