using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Sphene.Services;

/// <summary>
/// Per-plate draw context handed to an effect each frame. Carries the plate's
/// resolved screen bands, world position, stack order and render scale plus
/// the mask bridge used to occlude a layer like the supporter icon.
/// </summary>
public readonly struct NameplateFxContext
{
    public readonly ImDrawListPtr Draw;
    public readonly float Time;
    public readonly int Plate;
    /// <summary>Screen rect of the name line.</summary>
    public readonly Vector4 NameBand;
    /// <summary>Screen rect of the title line - <see cref="Vector4.Zero"/> when the plate has none.</summary>
    public readonly Vector4 TitleBand;
    /// <summary>Union of the plate's ink coverage.</summary>
    public readonly Vector4 InkRect;
    /// <summary>Owner's world position lifted to nameplate height.</summary>
    public readonly Vector3 WorldPos;
    /// <summary>Nameplate stack order - higher renders in front.</summary>
    public readonly float Order;
    /// <summary>The plate's real render scale (size option × distance).</summary>
    public readonly float Scale;
    /// <summary>The plate's effective draw alpha (0-1) - effects multiply their
    /// particle colors by it so they fade with the plate instead of popping.</summary>
    public readonly float PlateAlpha;
    /// <summary>Preview mode (settings galleries): <see cref="Begin"/> routes
    /// layers through draw-list channels instead of the GPU mask - channel 0
    /// holds behind-text passes, channel 1 the plate text and front passes.</summary>
    public readonly bool IsPreview;
    internal readonly SupporterNameplateService Host;

    /// <summary>Opaque token returned by <see cref="Begin"/> in preview mode -
    /// any nonzero value works since <see cref="End"/> checks for it first.</summary>
    private const nint PreviewLayerToken = 1;

    internal NameplateFxContext(ImDrawListPtr draw, float time, int plate,
        Vector4 nameBand, Vector4 titleBand, Vector4 inkRect, Vector3 worldPos,
        float order, float scale, float plateAlpha, SupporterNameplateService host,
        bool isPreview = false)
    {
        Draw = draw;
        Time = time;
        Plate = plate;
        NameBand = nameBand;
        TitleBand = titleBand;
        InkRect = inkRect;
        WorldPos = worldPos;
        Order = order;
        Scale = scale;
        PlateAlpha = plateAlpha;
        Host = host;
        IsPreview = isPreview;
    }

    /// <summary>
    /// Context for UI previews (settings stage and effect gallery cells): no
    /// world position or plate alpha, and the caller must have split
    /// <paramref name="draw"/> into two channels - behind-text layers land on
    /// channel 0 beneath the plate text, front layers on channel 1 above it.
    /// </summary>
    public static NameplateFxContext ForPreview(ImDrawListPtr draw, float time, int plate,
        Vector4 nameBand, Vector4 titleBand, Vector4 inkRect, float scale,
        SupporterNameplateService host)
        => new(draw, time, plate, nameBand, titleBand, inkRect, Vector3.Zero,
            0f, scale, 1f, host, isPreview: true);

    /// <summary>Scales a packed ABGR color's alpha by <see cref="PlateAlpha"/>.</summary>
    public uint ApplyAlpha(uint col)
    {
        if (PlateAlpha >= 0.996f)
            return col;
        var a = (uint)Math.Clamp((int)((col >> 24) * PlateAlpha), 0, 255);
        return (col & 0x00FFFFFFu) | (a << 24);
    }

    /// <summary>The band an effect should anchor to - title when present.</summary>
    public Vector4 MainBand => TitleBand != Vector4.Zero ? TitleBand
        : NameBand != Vector4.Zero ? NameBand : InkRect;

    /// <summary>Bounds generously covering anything an effect may draw around
    /// the plate - used to bound the occluder collection.</summary>
    public (Vector2 Min, Vector2 Max) FxBounds
    {
        get
        {
            var r = InkRect != Vector4.Zero ? InkRect : MainBand;
            return (new Vector2(r.X - 44f, r.Y - 44f), new Vector2(r.Z + 44f, r.W + 44f));
        }
    }

    /// <summary>Begins an occluded draw layer - in preview mode a channel hop
    /// instead of the GPU mask. Returns 0 when the mask path is unavailable -
    /// the effect should still draw, just unoccluded.</summary>
    public nint Begin(NameplateFxLayer layer)
    {
        if (IsPreview)
        {
            Draw.ChannelsSetCurrent(layer == NameplateFxLayer.BehindText ? 0 : 1);
            return PreviewLayerToken;
        }
        return Host.BeginFxLayer(in this, layer);
    }

    /// <summary>Ends a layer started with <see cref="Begin"/>.</summary>
    public void End(nint token)
    {
        if (IsPreview)
        {
            if (token == PreviewLayerToken)
            {
                Draw.ChannelsSetCurrent(1);
            }
            return;
        }
        SupporterNameplateService.EndFxLayer(Draw, token);
    }
}
