using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// Slow snowflakes drifting down across the name band with a gentle lateral
/// sway - subtle ambient weather on the plate. Front layer.
/// </summary>
internal sealed class FxSnowDrift : INameplateFx
{
    public const string FxId = "snow.drift";
    string INameplateFx.Id => FxId;
    string INameplateFx.DisplayName => "Snow Drift";

    private const int FlakeCount = 6;
    private const float FallSpan = 34f;
    private const float Period = 4.8f;
    private const uint FlakeColor = 0x00FFF4E8u;

    public void Draw(in NameplateFxContext ctx)
    {
        var n = ctx.NameBand;
        var w = n.Z - n.X;
        if (w < 4f)
            return;
        var token = ctx.Begin(NameplateFxLayer.Front);
        for (var p = 0; p < FlakeCount; p++)
        {
            var ph = (ctx.Time / Period + ctx.Plate * 0.11f + p * (1f / FlakeCount)) % 1f;
            var sway = MathF.Sin(ctx.Time * 1.1f + p * 1.7f + ctx.Plate) * 6f;
            var x = n.X + w * ((p * 0.61f + ctx.Plate * 0.23f + 0.09f) % 1f);
            var y = n.Y - 8f + ph * (n.W - n.Y + FallSpan * ctx.Scale);
            var fade = MathF.Sin(ph * MathF.PI);
            var r = (0.9f + 0.7f * ((p * 7 + ctx.Plate) % 3) * 0.4f) * ctx.Scale;
            ctx.Draw.AddCircleFilled(new Vector2(x + sway, y), r,
                ctx.ApplyAlpha((uint)(170 * fade) << 24 | FlakeColor));
        }
        ctx.End(token);
    }
}
