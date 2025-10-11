using System.Numerics;

namespace Sphene.UI.Styling;

/// <summary>
/// Alternative Sphene color palette for non-CompactUI windows
/// Features warmer tones with soft purples, gentle blues, and golden accents
/// Designed to be more harmonious and easier on the eyes for extended use
/// </summary>
public static class AlternativeSpheneColors
{
    // Primary Colors - Warmer and softer tones
    public static readonly Vector4 SoftLavender = new(0.7f, 0.6f, 0.9f, 1.0f);        // #B399E6 - Gentle lavender
    public static readonly Vector4 WarmPeriwinkle = new(0.6f, 0.7f, 0.9f, 1.0f);      // #99B3E6 - Warm periwinkle blue
    public static readonly Vector4 DeepAmethyst = new(0.5f, 0.4f, 0.7f, 1.0f);        // #8066B3 - Rich amethyst
    public static readonly Vector4 MidnightBlue = new(0.3f, 0.4f, 0.6f, 1.0f);        // #4D6699 - Softer midnight blue
    
    // Accent Colors - Warm and inviting
    public static readonly Vector4 WarmGold = new(0.9f, 0.7f, 0.4f, 1.0f);            // #E6B366 - Warm golden tone
    public static readonly Vector4 SoftCream = new(0.95f, 0.93f, 0.88f, 1.0f);        // #F2EDE0 - Soft cream
    public static readonly Vector4 PearlWhite = new(0.92f, 0.94f, 0.96f, 1.0f);       // #EBF0F5 - Pearl white
    public static readonly Vector4 SoftGlow = new(0.85f, 0.88f, 0.92f, 1.0f);         // #D9E0EB - Soft ethereal glow
    
    // Status Colors - Harmonious and clear
    public static readonly Vector4 SuccessGreen = new(0.4f, 0.7f, 0.5f, 1.0f);        // #66B380 - Gentle success green
    public static readonly Vector4 WarningAmber = new(0.9f, 0.7f, 0.3f, 1.0f);        // #E6B34D - Warm amber warning
    public static readonly Vector4 ErrorRose = new(0.8f, 0.4f, 0.4f, 1.0f);           // #CC6666 - Soft rose error
    public static readonly Vector4 InactiveGray = new(0.6f, 0.6f, 0.65f, 1.0f);       // #9999A6 - Neutral gray
    
    // Background Colors - Warmer and more comfortable
    public static readonly Vector4 WarmDarkBg = new(0.12f, 0.13f, 0.16f, 1.0f);       // #1F2129 - Warm dark background
    public static readonly Vector4 WarmMidBg = new(0.18f, 0.19f, 0.23f, 0.85f);       // #2E303B - Warm mid background
    public static readonly Vector4 SoftBorder = new(0.4f, 0.45f, 0.55f, 1.0f);        // #66738C - Soft border
    
    // Text Colors - Better contrast and readability
    public static readonly Vector4 PrimaryText = new(0.92f, 0.94f, 0.96f, 1.0f);      // #EBF0F5 - Primary text
    public static readonly Vector4 SecondaryText = new(0.75f, 0.78f, 0.82f, 1.0f);    // #BFC7D1 - Secondary text
    public static readonly Vector4 MutedText = new(0.65f, 0.68f, 0.72f, 1.0f);        // #A6ADB8 - Muted text
    
    // Interactive States - Smooth transitions
    public static readonly Vector4 HoverState = new(0.6f, 0.7f, 0.8f, 0.4f);          // Soft hover
    public static readonly Vector4 ActiveState = new(0.5f, 0.6f, 0.75f, 0.6f);        // Gentle active
    public static readonly Vector4 SelectedState = new(0.55f, 0.65f, 0.8f, 0.5f);     // Comfortable selection
    
    // UI Element Colors - Harmonious and functional
    public static readonly Vector4 ButtonBase = new(0.45f, 0.5f, 0.65f, 1.0f);        // #73809F - Base button color
    public static readonly Vector4 ButtonHover = new(0.55f, 0.6f, 0.75f, 1.0f);       // #8C99BF - Button hover
    public static readonly Vector4 ButtonActive = new(0.6f, 0.65f, 0.8f, 1.0f);       // #99A6CC - Button active
    
    public static readonly Vector4 InputBg = new(0.2f, 0.22f, 0.26f, 1.0f);           // #333842 - Input background
    public static readonly Vector4 InputBorder = new(0.4f, 0.45f, 0.55f, 1.0f);       // #66738C - Input border
    public static readonly Vector4 InputFocus = new(0.6f, 0.7f, 0.8f, 1.0f);          // #99B3CC - Input focus
    
    // Scrollbar Colors - Subtle and unobtrusive
    public static readonly Vector4 ScrollbarBg = new(0.15f, 0.16f, 0.19f, 1.0f);      // #26292E - Scrollbar background
    public static readonly Vector4 ScrollbarGrab = new(0.5f, 0.55f, 0.65f, 1.0f);     // #808CA6 - Scrollbar grab
    public static readonly Vector4 ScrollbarHover = new(0.6f, 0.65f, 0.75f, 1.0f);    // #99A6BF - Scrollbar hover
    
    // Tab Colors - Clear hierarchy
    public static readonly Vector4 TabInactive = new(0.35f, 0.4f, 0.5f, 0.7f);        // Inactive tab
    public static readonly Vector4 TabActive = new(0.5f, 0.6f, 0.75f, 1.0f);          // Active tab
    public static readonly Vector4 TabHover = new(0.45f, 0.55f, 0.7f, 0.8f);          // Tab hover
    
    // Utility Methods
    public static uint ToImGuiColor(Vector4 color)
    {
        return UiSharedService.Color(color);
    }
    
    public static Vector4 WithAlpha(Vector4 color, float alpha)
    {
        return new Vector4(color.X, color.Y, color.Z, alpha);
    }
    
    public static Vector4 LerpColor(Vector4 from, Vector4 to, float t)
    {
        return Vector4.Lerp(from, to, Math.Clamp(t, 0f, 1f));
    }
    
    // Status color helpers
    public static Vector4 GetStatusColor(bool isActive, bool hasWarning = false, bool hasError = false)
    {
        if (hasError) return ErrorRose;
        if (hasWarning) return WarningAmber;
        return isActive ? SuccessGreen : InactiveGray;
    }
    
    // Get a softer version of any color
    public static Vector4 GetSofterVersion(Vector4 color, float softenAmount = 0.3f)
    {
        return LerpColor(color, PearlWhite, softenAmount);
    }
}