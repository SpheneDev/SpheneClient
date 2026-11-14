using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// A flapping bat weaves around the name band on a lemniscate (figure-8):
/// it dips below the letters in front, slips over them on the upper arcs and
/// threads the name plane four times per lap. Every element - head and both
/// trail ghosts - carries its own depth, so a ghost can still be cut by the
/// plate's letters on <see cref="NameplateFxLayer.BehindText"/> while the head
/// is already back in front.
/// </summary>
internal sealed class FxBatOrbit : INameplateFx
{
    public const string FxId = "bat.orbit";
    string INameplateFx.Id => FxId;
    string INameplateFx.DisplayName => "Bat Orbit";

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct Bat(Vector2 Pos, float Scale, float Flap,
        uint Alpha, float Depth);

    public void Draw(in NameplateFxContext ctx)
    {
        var n = ctx.NameBand;
        var cx = (n.X + n.Z) * 0.5f;
        var cy = (n.Y + n.W) * 0.5f;
        var rx = (n.Z - n.X) * 0.5f + 16f * ctx.Scale;
        // keep the lobes inside the text band so the weave visibly threads
        // the letters - a wide orbit spends its whole front half below the
        // name where "in front" occludes nothing
        var ry = (n.W - n.Y) * 0.5f + 2f * ctx.Scale;
        var t = ctx.Time * 1.35f + ctx.Plate * 1.37f;

        Span<Bat> bats = stackalloc Bat[3];
        for (var g = 0; g < 3; g++)
        {
            var gt = t - g * 0.15f;
            var depth = MathF.Sin(gt * 2f);
            var pos = new Vector2(cx + MathF.Sin(gt) * rx, cy + depth * ry);
            var sc = (0.58f + 0.42f * ((depth + 1f) * 0.5f)) * ctx.Scale * (1f - g * 0.2f);
            // dim near the plane crossing so the layer swap reads as slipping
            // between letters instead of popping on top of them
            var cross = 0.68f + 0.32f * Math.Clamp(MathF.Abs(depth) * 3f, 0f, 1f);
            var alphaByte = g == 0
                ? depth >= 0f ? 255f : 170f
                : (0.20f - g * 0.05f) * 255f;
            var alpha = (uint)(byte)Math.Clamp(alphaByte * cross * ctx.PlateAlpha, 0f, 255f) << 24;
            var flap = MathF.Sin(ctx.Time * 14f + ctx.Plate - g * 0.9f);
            bats[g] = new Bat(pos, sc, flap, alpha, depth);
        }
        DrawPass(ctx, bats, behind: true);
        DrawPass(ctx, bats, behind: false);
    }

    private static void DrawPass(in NameplateFxContext ctx, ReadOnlySpan<Bat> bats, bool behind)
    {
        var any = false;
        for (var i = 0; i < bats.Length; i++)
        {
            if ((bats[i].Depth < 0f) == behind)
            {
                any = true;
                break;
            }
        }
        if (!any)
            return;
        var token = ctx.Begin(behind ? NameplateFxLayer.BehindText : NameplateFxLayer.Front);
        for (var i = 0; i < bats.Length; i++)
        {
            ref readonly var b = ref bats[i];
            if ((b.Depth < 0f) != behind)
                continue;
            DrawBat(ctx.Draw, b.Pos, b.Scale, b.Flap, b.Alpha);
        }
        ctx.End(token);
    }

    private static void DrawBat(ImDrawListPtr draw, Vector2 pos, float s, float flap, uint alpha)
    {
        var body = alpha | 0x005A1E46u;
        var wing = alpha | 0x008C3C6Eu;
        var wy = -flap * 6.5f * s;
        var lt = pos + new Vector2(-9f * s, wy);
        var rt = pos + new Vector2(9f * s, wy);
        draw.AddTriangleFilled(pos + new Vector2(-1.4f * s, -0.8f * s), lt, pos + new Vector2(-4.5f * s, 2.2f * s), wing);
        draw.AddTriangleFilled(lt, pos + new Vector2(-4.5f * s, 2.2f * s), pos + new Vector2(-7.5f * s, 4f * s), wing);
        draw.AddTriangleFilled(pos + new Vector2(1.4f * s, -0.8f * s), rt, pos + new Vector2(4.5f * s, 2.2f * s), wing);
        draw.AddTriangleFilled(rt, pos + new Vector2(4.5f * s, 2.2f * s), pos + new Vector2(7.5f * s, 4f * s), wing);
        draw.AddCircleFilled(pos, 2.8f * s, body);
        draw.AddTriangleFilled(pos + new Vector2(-2.2f * s, -1.2f * s), pos + new Vector2(-1.1f * s, -4f * s), pos + new Vector2(-0.2f * s, -1.6f * s), body);
        draw.AddTriangleFilled(pos + new Vector2(2.2f * s, -1.2f * s), pos + new Vector2(1.1f * s, -4f * s), pos + new Vector2(0.2f * s, -1.6f * s), body);
    }
}
