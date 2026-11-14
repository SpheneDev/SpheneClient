namespace Sphene.SpheneConfiguration.Models;

/// <summary>
/// User override for the application-wide layout density · Auto resolves the
/// profile from each window's actual size, the other values pin it globally.
/// </summary>
public enum LayoutDensityMode
{
    Auto,
    Compact,
    Balanced,
    Comfortable
}
