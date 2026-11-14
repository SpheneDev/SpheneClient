using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// Small 4-point star ornaments twinkling along the title band's top and
/// bottom edges (name band fallback), bobbing in place. Front layer.
/// </summary>
internal sealed class FxStarOrnaments : INameplateFx
{
    public const string FxId = "stars.ornaments";
    string INameplateFx.Id => FxId;
    string INameplateFx.DisplayName => "Title Star Ornaments";

    public void Draw(in NameplateFxContext ctx)
    {
        var band = ctx.MainBand;
        var w = band.Z - band.X;
        var h = band.W - band.Y;
        if (w < 4f)
            return;
        var token = ctx.Begin(NameplateFxLayer.Front);
        for (var p = 0; p < 5; p++)
        {
            var pp = (p + 0.5f) / 5f;
            var tw = MathF.Pow(MathF.Max(0f, MathF.Sin(ctx.Time * 2.6f + ctx.Plate * 0.9f + p * 2.1f)), 6f);
            if (tw < 0.02f)
                continue;
            var bob = MathF.Sin(ctx.Time * 1.7f + p * 1.9f) * 2f;
            var edge = (p & 1) == 0 ? band.Y - 3f - bob : band.W + 3f + bob;
            var center = new Vector2(band.X + w * pp, edge);
            var star = MathF.Max(2.4f, h * 0.16f) * (0.7f + 0.6f * tw);
            DrawStar(ctx.Draw, center, star,
                ctx.ApplyAlpha((uint)(200 * tw) << 24 | 0x00D8CCFFu));
        }
        ctx.End(token);
    }

    internal static void DrawStar(ImDrawListPtr draw, Vector2 c, float r, uint col)
    {
        draw.AddQuadFilled(
            c + new Vector2(0f, -r), c + new Vector2(r * 0.22f, 0f),
            c + new Vector2(0f, r), c + new Vector2(-r * 0.22f, 0f), col);
        draw.AddQuadFilled(
            c + new Vector2(-r * 0.72f, 0f), c + new Vector2(0f, -r * 0.22f),
            c + new Vector2(r * 0.72f, 0f), c + new Vector2(0f, r * 0.22f), col);
    }
}
