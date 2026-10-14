namespace Sphene.Services;

/// <summary>
/// Combinable list-row effects a profile owner picks for what viewers see.
/// Effect subsets: hover offers Glow/Sheen/Pulse/Sweep/Lift, idle offers
/// Tint/Pulse/Sparkle/Wave — editors mask to the relevant subset via
/// <see cref="HoverMask"/>/<see cref="IdleMask"/>; bits stack independently.
/// </summary>
[Flags]
public enum ProfileRowFx
{
    None = 0,
    /// <summary>Accent edge bar grows in plus a soft accent fill on hover.</summary>
    Glow = 1 << 0,
    /// <summary>Traveling light band sweeps across the row on hover.</summary>
    Sheen = 1 << 1,
    /// <summary>Breathing accent border — fast on hover, slow while idle.</summary>
    Pulse = 1 << 2,
    /// <summary>Accent comet travels along the row border on hover.</summary>
    Sweep = 1 << 3,
    /// <summary>Row lifts: soft drop shadow below plus a top edge highlight.</summary>
    Lift = 1 << 4,
    /// <summary>Removed left-edge accent bar — bit stays reserved so stored
    /// themes still parse, but no layer draws it and masks strip it.</summary>
    EdgeBar = 1 << 5,
    /// <summary>Subtle accent gradient wash across the row.</summary>
    Tint = 1 << 6,
    /// <summary>Twinkling accent sparks scattered over the row.</summary>
    Sparkle = 1 << 7,
    /// <summary>Slow luminance wave drifting across the row.</summary>
    Wave = 1 << 8,

    /// <summary>Bits editors may offer for the hover layer.</summary>
    HoverMask = Glow | Sheen | Pulse | Sweep | Lift,
    /// <summary>Bits editors may offer for the idle layer.</summary>
    IdleMask = Tint | Pulse | Sparkle | Wave,
}
