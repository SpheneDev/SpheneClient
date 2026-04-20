namespace Sphene.Services;

public static class HousingInteriorRelativeMapIds
{
    public const uint Ground = 0xFFFFFFF1;
    public const uint Basement = 0xFFFFFFF2;
    public const uint Second = 0xFFFFFFF3;

    public static bool TryGetLabel(uint mapId, out string label)
    {
        label = mapId switch
        {
            Ground => "Ground floor",
            Basement => "Basement",
            Second => "Second floor",
            _ => string.Empty
        };
        return label.Length > 0;
    }
}
