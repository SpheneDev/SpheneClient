using System.Numerics;
using Dalamud.Interface;

namespace Sphene.UI.Styling;

/// <summary>
/// Sphene-themed color palette inspired by FFXIV's Sphene aesthetic
/// Features crystalline blues, ethereal purples, and golden accents
/// </summary>
public static class SpheneColors
{
    // Primary Sphene Colors - Crystalline Blues and Purples
    public static readonly Vector4 CrystalBlue = new(0.4f, 0.7f, 1.0f, 1.0f);          // #66B3FF - Main crystal blue
    public static readonly Vector4 DeepCrystal = new(0.2f, 0.4f, 0.8f, 1.0f);          // #3366CC - Deeper crystal tone
    public static readonly Vector4 EtherealPurple = new(0.6f, 0.4f, 0.9f, 1.0f);       // #9966E6 - Mystical purple
    public static readonly Vector4 VoidPurple = new(0.4f, 0.2f, 0.7f, 1.0f);           // #6633B3 - Darker void purple
    
    // Accent Colors - Golden and Light Tones
    public static readonly Vector4 SpheneGold = new(1.0f, 0.8f, 0.3f, 1.0f);           // #FFCC4D - Warm golden accent
    public static readonly Vector4 LuminousGold = new(1.0f, 0.9f, 0.6f, 1.0f);         // #FFE699 - Bright luminous gold
    public static readonly Vector4 CrystalWhite = new(0.95f, 0.98f, 1.0f, 1.0f);       // #F2FAFF - Pure crystal white
    public static readonly Vector4 EtherGlow = new(0.8f, 0.9f, 1.0f, 1.0f);            // #CCE6FF - Soft ethereal glow
    
    // Status Colors - Network themed
    public static readonly Vector4 NetworkActive = new(0.3f, 0.8f, 0.5f, 1.0f);        // #4DCC80 - Active connection
    public static readonly Vector4 NetworkConnected = new(0.3f, 0.8f, 0.5f, 1.0f);     // #4DCC80 - Connected state
    public static readonly Vector4 NetworkDisconnected = new(0.9f, 0.3f, 0.4f, 1.0f);  // #E64D66 - Disconnected state
    public static readonly Vector4 NetworkWarning = new(1.0f, 0.7f, 0.2f, 1.0f);       // #FFB333 - Network warning
    public static readonly Vector4 NetworkError = new(0.9f, 0.3f, 0.4f, 1.0f);         // #E64D66 - Network error
    public static readonly Vector4 NetworkInactive = new(0.6f, 0.6f, 0.7f, 1.0f);      // #9999B3 - Inactive/offline
    public static readonly Vector4 TransmissionActive = new(0.4f, 0.7f, 1.0f, 1.0f);   // #66B3FF - Active transmission
    public static readonly Vector4 ReceptionActive = new(0.6f, 0.4f, 0.9f, 1.0f);      // #9966E6 - Active reception
    
    // UI Element Colors
    public static readonly Vector4 BackgroundDark = new(0.1f, 0.12f, 0.18f, 0.0f);     //rgb(26, 31, 46) - Dark background
    public static readonly Vector4 BackgroundMid = new(0.04f, 0.0f, 0.12f, 0.8f);    //rgba(50, 47, 87, 0.81) - Mid-tone background
    public static readonly Vector4 BorderColor = new(0.3f, 0.4f, 0.6f, 1.0f);          // #4D6699 - Border color
    public static readonly Vector4 TextPrimary = new(0.9f, 0.95f, 1.0f, 1.0f);         // #E6F2FF - Primary text
    public static readonly Vector4 TextSecondary = new(0.7f, 0.8f, 0.9f, 1.0f);        // #B3CCE6 - Secondary text
    public static readonly Vector4 UITextSecondary = new(0.7f, 0.8f, 0.9f, 1.0f);      // #B3CCE6 - UI secondary text
    
    // Hover and Selection States
    public static readonly Vector4 HoverBlue = new(0.5f, 0.7f, 0.9f, 0.3f);            // Semi-transparent hover
    public static readonly Vector4 SelectionBlue = new(0.4f, 0.6f, 0.8f, 0.5f);        // Semi-transparent selection
    public static readonly Vector4 ActiveTab = new(0.3f, 0.5f, 0.8f, 1.0f);            // Active tab color
    
    // Utility Methods
    public static uint ToImGuiColor(Vector4 color)
    {
        return UiSharedService.Color(color);
    }
    
    public static Vector4 WithAlpha(Vector4 color, float alpha)
    {
        return new Vector4(color.X, color.Y, color.Z, alpha);
    }
    
    // Gradient helpers for advanced styling
    public static Vector4 LerpColor(Vector4 from, Vector4 to, float t)
    {
        return Vector4.Lerp(from, to, Math.Clamp(t, 0f, 1f));
    }
    
    // Get status color based on boolean state with Sphene theming
    public static Vector4 GetSpheneStatusColor(bool isActive)
    {
        return isActive ? NetworkActive : NetworkInactive;
    }
    
    // Get connection status color
    public static Vector4 GetConnectionStatusColor(bool isConnected, bool hasWarning = false, bool hasError = false)
    {
        if (hasError) return NetworkError;
        if (hasWarning) return NetworkWarning;
        return isConnected ? NetworkActive : NetworkInactive;
    }
}