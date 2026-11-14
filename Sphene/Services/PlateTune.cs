using System.Numerics;
using System.Runtime.InteropServices;

namespace Sphene.Services;

/// <summary>Per-plate tuning values resolved from the nameplate settings and
/// distance presets. Offsets apply per line - title vs. name band. Mask values
/// are normalized: scales are multipliers, <see cref="GfdMaskOffset"/> is px,
/// <see cref="IconSize"/> is a multiplier.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PlateTune(Vector2 TitleOffset, Vector2 NameOffset, float PitchMul, float AdvanceMul, Vector2 Edge,
    float IconMaskScale, float IconSize, float GfdMaskScale, Vector2 GfdMaskOffset);
