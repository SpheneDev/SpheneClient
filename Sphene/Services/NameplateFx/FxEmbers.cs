using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// Warm ember motes drifting upward from the name band, dimming as they rise
/// and fade out - like heat sparks off the nameplate. Front layer.
/// </summary>
internal sealed class FxEmbers : INameplateFx
{
    public const string FxId = "embers.rise";
    string INameplateFx.Id => FxId;
    string INameplateFx.DisplayName => "Rising Embers";

    private const int EmberCount = 4;
    private const float RiseSpan = 26f;
    private const float Period = 2.6f;

    public void Draw(in NameplateFxContext ctx)
    {
        var n = ctx.NameBand;
        var w = n.Z - n.X;
        if (w < 4f)
            return;
        var token = ctx.Begin(NameplateFxLayer.Front);
        for (var p = 0; p < EmberCount; p++)
        {
            var ph = (ctx.Time / Period + ctx.Plate * 0.23f + p * (1f / EmberCount)) % 1f;
            var sway = MathF.Sin(ph * 9f + ctx.Plate * 2f + p * 3f) * 4f;
            var x = n.X + w * ((p * 0.37f + ctx.Plate * 0.19f + MathF.Sin(ctx.Time * 0.21f + p) * 0.02f) % 1f);
            var y = n.Y - ph * RiseSpan * ctx.Scale;
            var fade = MathF.Sin(ph * MathF.PI);
            var r = (1.6f + 1.4f * (1f - ph)) * ctx.Scale;
            var col = ctx.ApplyAlpha((uint)(210 * fade) << 24 | 0x003599FFu);
            ctx.Draw.AddCircleFilled(new Vector2(x + sway, y), r, col);
            ctx.Draw.AddCircleFilled(new Vector2(x + sway, y), r * 0.45f,
                ctx.ApplyAlpha((uint)(255 * fade) << 24 | 0x0088CCFFu));
        }
        ctx.End(token);
    }
}
