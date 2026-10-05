using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using System.Numerics;

namespace Sphene.Services;

public sealed class SupporterAnimatedIcon : IDisposable
{
    private readonly float _totalDuration;

    public SupporterAnimatedIcon(IDalamudTextureWrap atlas, int frameWidth, int frameHeight, float[] frameDurations)
    {
        Atlas = atlas;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        FrameDurations = frameDurations;
        var total = 0f;
        for (var i = 0; i < frameDurations.Length; i++)
            total += frameDurations[i];
        _totalDuration = total;
    }

    public IDalamudTextureWrap Atlas { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public float[] FrameDurations { get; }
    public int FrameCount => FrameDurations.Length;

    public Vector2 AspectSize(float height) => new(FrameHeight <= 0 ? height : height * ((float)FrameWidth / FrameHeight), height);

    public (ImTextureID Handle, Vector2 UvMin, Vector2 UvMax) GetFrame(double timeSeconds)
    {
        var index = 0;
        if (FrameCount > 1 && _totalDuration > 0)
        {
            var t = (float)(timeSeconds % _totalDuration);
            var acc = 0f;
            for (; index < FrameCount - 1; index++)
            {
                acc += FrameDurations[index];
                if (t < acc)
                    break;
            }
        }

        var atlasWidth = Math.Max(1f, Atlas.Width);
        var u0 = index * FrameWidth / atlasWidth;
        var u1 = (index + 1) * FrameWidth / atlasWidth;
        return (Atlas.Handle, new Vector2(u0, 0f), new Vector2(u1, 1f));
    }

    public void Dispose() => Atlas.Dispose();
}
