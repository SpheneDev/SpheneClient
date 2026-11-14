using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// Small petals drifting sideways across the name band and sinking slowly,
/// tumbling as they fall; they wrap around and restart. Front layer.
/// </summary>
internal sealed class FxPetals : INameplateFx
{
    public const string FxId = "petals.drift";
    string INameplateFx.Id => FxId;
    string INameplateFx.DisplayName => "Drifting Petals";

    private const int PetalCount = 3;

    public void Draw(in NameplateFxContext ctx)
    {
        var n = ctx.NameBand;
        var w = n.Z - n.X;
        var h = n.W - n.Y;
        if (w < 4f)
            return;
        var token = ctx.Begin(NameplateFxLayer.Front);
        for (var p = 0; p < PetalCount; p++)
        {
            var ph = (ctx.Time / 5.5f + ctx.Plate * 0.31f + p * (1f / PetalCount)) % 1f;
            var x = n.X + w * ((ph + p * 0.41f + ctx.Plate * 0.17f) % 1f);
            var y = n.Y - 10f * ctx.Scale + ph * (h + 20f * ctx.Scale)
                + MathF.Sin(ctx.Time * 1.3f + p * 2.4f) * 3f;
            var rot = ctx.Time * 1.8f + p * 2.1f + ctx.Plate;
            var fade = MathF.Sin(ph * MathF.PI);
            DrawPetal(ctx.Draw, new Vector2(x, y), 4.4f * ctx.Scale, rot,
                ctx.ApplyAlpha((uint)(150 * fade) << 24 | 0x00C8B4FFu));
        }
        ctx.End(token);
    }

    private static void DrawPetal(ImDrawListPtr draw, Vector2 c, float r, float rot, uint col)
    {
        var co = MathF.Cos(rot);
        var si = MathF.Sin(rot);
        var tipA = c + new Vector2(-si, -co) * r;
        var tipB = c - new Vector2(-si, -co) * r;
        var side = new Vector2(co, si) * (r * 0.55f);
        draw.AddQuadFilled(tipA, c + side, tipB, c - side, col);
    }
}
