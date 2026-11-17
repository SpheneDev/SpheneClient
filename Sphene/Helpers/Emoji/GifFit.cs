using System;
using System.Numerics;

namespace Sphene.Helpers.Emoji;

/// <summary>
/// Display size for a chat GIF: aspect-fit inside a maximum box of
/// <see cref="MaxWidth"/>x<see cref="MaxHeight"/> logical pixels at the
/// current UI scale, never upscaling sources that already fit, and
/// shrinking further when the available content width is narrower.
/// </summary>
public static class GifFit
{
    /// <summary>Maximum rendered width at scale 1.0 (owner requirement).</summary>
    public const float MaxWidth = 320f;

    /// <summary>Maximum rendered height at scale 1.0 (owner requirement).</summary>
    public const float MaxHeight = 220f;

    /// <summary>
    /// Returns the drawn size for a <paramref name="srcW"/>x<paramref name="srcH"/>
    /// source inside an <paramref name="availWidth"/>-wide content column at
    /// <paramref name="scale"/> UI scale. Zero/negative sources yield a
    /// small placeholder square so the bubble keeps a sane shape.
    /// </summary>
    public static Vector2 Fit(int srcW, int srcH, float availWidth, float scale = 1f)
    {
        var boxW = MathF.Min(MaxWidth * scale, MathF.Max(1f, availWidth));
        var boxH = MaxHeight * scale;
        if (srcW <= 0 || srcH <= 0)
        {
            var s = MathF.Min(boxW, MathF.Min(boxH, 96f * scale));
            return new Vector2(s, s);
        }
        var factor = MathF.Min(boxW / srcW, boxH / srcH);
        // Never enlarge: sources smaller than the box keep their size.
        factor = MathF.Min(factor, 1f);
        return new Vector2(MathF.Max(1f, srcW * factor), MathF.Max(1f, srcH * factor));
    }
}
