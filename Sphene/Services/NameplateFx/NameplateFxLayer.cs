namespace Sphene.Services;

/// <summary>Where an effect layer sits relative to the nameplate's own text.</summary>
public enum NameplateFxLayer
{
    /// <summary>In front of the plate's text - occluded by world depth and by
    /// nameplates rendered in front of this one, but not by its own letters.</summary>
    Front,

    /// <summary>Behind the plate's own text - additionally cut by the plate's
    /// glyph coverage, so letter ink occludes the effect pixel-exact.</summary>
    BehindText,
}
