namespace Sphene.UI.Theme;

public static class ThemePropertyCopier
{
    /// <summary>
    /// Copies all theme properties from <paramref name="source"/> to <paramref name="target"/>.
    /// Delegates to <see cref="ThemeConfiguration.ApplyFrom"/>, so new properties are picked up
    /// automatically without extending this class.
    /// </summary>
    public static void Copy(ThemeConfiguration source, ThemeConfiguration target)
    {
        target.ApplyFrom(source);
    }
}
