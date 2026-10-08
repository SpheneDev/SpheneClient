namespace Sphene.UI.Design;

/// <summary>How an image is fitted into its target box.</summary>
public enum ImageFit
{
    /// <summary>Scale to fit inside the box, letterboxed, aspect preserved.</summary>
    Fit,
    /// <summary>Scale to cover the box, overflowing edges cropped via UVs.</summary>
    Cover,
    /// <summary>Stretch to the box, aspect ignored.</summary>
    Stretch,
}
