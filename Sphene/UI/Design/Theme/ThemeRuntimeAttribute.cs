namespace Sphene.UI.Design;

/// <summary>
/// Marks a <see cref="ThemeConfiguration"/> property as runtime-only state.
/// Such properties are excluded from <see cref="ThemeConfiguration.ApplyFrom"/> so that
/// applying presets or resetting a theme never overwrites live UI toggles.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ThemeRuntimeAttribute : Attribute
{
}
