using System.Numerics;

namespace Sphene.UI.Theme;

public class ButtonStyleOverride
{
    public float WidthDelta { get; set; } = 0f;
    public float HeightDelta { get; set; } = 0f;
    public Vector2 IconOffset { get; set; } = Vector2.Zero;
    public Vector4? Button { get; set; }
    public Vector4? ButtonHovered { get; set; }
    public Vector4? ButtonActive { get; set; }
    public Vector4? Text { get; set; }
    public Vector4? Icon { get; set; }
    public Vector4? Border { get; set; }
    public float? BorderSize { get; set; }
}