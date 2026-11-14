using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// Tiny hearts floating up off the name band - the classic supporter tell.
/// Front layer, depth- and front-plate occluded.
/// </summary>
internal sealed class FxHeartsFloat : INameplateFx
{
    public const string FxId = "hearts.float";
    string INameplateFx.Id => FxId;
    string INameplateFx.DisplayName => "Floating Hearts";

    private const int HeartCount = 3;
    private const float RiseSpan = 30f;
    private const float Period = 3.4f;
    private const uint HeartColor = 0x008787FFu;

    public void Draw(in NameplateFxContext ctx)
    {
        var n = ctx.NameBand;
        var w = n.Z - n.X;
        if (w < 4f)
            return;
        var token = ctx.Begin(NameplateFxLayer.Front);
        for (var p = 0; p < HeartCount; p++)
        {
            var ph = (ctx.Time / Period + ctx.Plate * 0.29f + p * (1f / HeartCount)) % 1f;
            var sway = MathF.Sin(ph * 7f + ctx.Plate + p * 2.2f) * 5f;
            var x = n.X + w * ((p * 0.53f + ctx.Plate * 0.13f + 0.31f) % 1f);
            var y = n.Y - 2f - ph * RiseSpan * ctx.Scale;
            var fade = MathF.Sin(ph * MathF.PI);
            DrawHeart(ctx.Draw, new Vector2(x + sway, y), 3.6f * ctx.Scale,
                ctx.ApplyAlpha((uint)(220 * fade) << 24 | HeartColor));
        }
        ctx.End(token);
    }

    private static void DrawHeart(ImDrawListPtr draw, Vector2 c, float r, uint col)
    {
        var lobe = r * 0.52f;
        draw.AddCircleFilled(c + new Vector2(-lobe * 0.72f, -lobe * 0.42f), lobe, col);
        draw.AddCircleFilled(c + new Vector2(lobe * 0.72f, -lobe * 0.42f), lobe, col);
        draw.AddTriangleFilled(
            c + new Vector2(-lobe * 1.34f, -lobe * 0.18f),
            c + new Vector2(lobe * 1.34f, -lobe * 0.18f),
            c + new Vector2(0f, r), col);
    }
}
