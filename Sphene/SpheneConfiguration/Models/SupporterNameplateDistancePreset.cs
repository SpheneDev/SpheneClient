namespace Sphene.SpheneConfiguration.Models;

/// <summary>Per-distance cutout tuning overrides for supporter nameplates -
/// the two presets nearest the plate's real yalm distance are interpolated.
/// Offsets apply per line: the title band shifts by TitleOffsetX/Y, the name
/// band by NameOffsetX/Y on titled plates or NameOnlyOffsetX/Y on plates
/// without a title line. When at least one preset exists it replaces every
/// global tuning slider for that plate.</summary>
public class SupporterNameplateDistancePreset
{
    public float Distance { get; set; }
    public float TitleOffsetX { get; set; }
    public float TitleOffsetY { get; set; }
    public float NameOffsetX { get; set; }
    public float NameOffsetY { get; set; }
    public float NameOnlyOffsetX { get; set; }
    public float NameOnlyOffsetY { get; set; }
    public float PitchMul { get; set; } = 1f;
    public float AdvanceMul { get; set; } = 1f;
    public float EdgeX { get; set; }
    public float EdgeY { get; set; }
    // mask tuning - percent values matching the global sliders' tuned defaults
    public float IconMaskScale { get; set; } = 100f;
    public float IconSizePercent { get; set; } = 100f;
    public float GfdMaskScale { get; set; } = 74f;
    public float GfdMaskOffsetX { get; set; } = -2f;
    public float GfdMaskOffsetY { get; set; } = 1f;
}
