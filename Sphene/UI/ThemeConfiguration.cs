using System.Numerics;

namespace Sphene.UI;

public class ThemeConfiguration
{
    // General Theme Settings
    public float WindowRounding { get; set; } = 8.0f;
    public float ChildRounding { get; set; } = 8.0f;
    public float PopupRounding { get; set; } = 8.0f;
    public float FrameRounding { get; set; } = 6.0f;
    public float ScrollbarRounding { get; set; } = 6.0f;
    public float GrabRounding { get; set; } = 6.0f;
    public float TabRounding { get; set; } = 6.0f;
    
    // CompactUI Specific Settings
    public float CompactWindowRounding { get; set; } = 12.0f;
    public float CompactChildRounding { get; set; } = 12.0f;
    public float CompactPopupRounding { get; set; } = 12.0f;
    public float CompactFrameRounding { get; set; } = 8.0f;
    public float CompactScrollbarRounding { get; set; } = 8.0f;
    public float CompactGrabRounding { get; set; } = 8.0f;
    public float CompactTabRounding { get; set; } = 8.0f;
    public float CompactHeaderRounding { get; set; } = 6.0f;
    
    // Spacing Settings
    public Vector2 WindowPadding { get; set; } = new(16.0f, 12.0f);
    public Vector2 FramePadding { get; set; } = new(6.0f, 3.0f);
    public Vector2 ItemSpacing { get; set; } = new(6.0f, 4.0f);
    public Vector2 ItemInnerSpacing { get; set; } = new(4.0f, 3.0f);
    public float IndentSpacing { get; set; } = 20.0f;
    
    // CompactUI Specific Spacing and Sizing
    public Vector2 CompactWindowPadding { get; set; } = new(8.0f, 8.0f);
    public Vector2 CompactFramePadding { get; set; } = new(4.0f, 3.0f);
    public Vector2 CompactItemSpacing { get; set; } = new(8.0f, 4.0f);
    public Vector2 CompactItemInnerSpacing { get; set; } = new(4.0f, 4.0f);
    public Vector2 CompactCellPadding { get; set; } = new(4.0f, 2.0f);
    public Vector2 CompactChildPadding { get; set; } = new(8.0f, 8.0f);
    public float CompactIndentSpacing { get; set; } = 21.0f;
    public float CompactScrollbarSize { get; set; } = 14.0f;
    public float CompactGrabMinSize { get; set; } = 10.0f;
    
    // CompactUI Specific Text Alignment
    public Vector2 CompactButtonTextAlign { get; set; } = new(0.5f, 0.5f);
    public Vector2 CompactSelectableTextAlign { get; set; } = new(0.0f, 0.0f);
    
    // CompactUI Specific Border Thickness
    public float CompactWindowBorderSize { get; set; } = 1.0f;
    public float CompactChildBorderSize { get; set; } = 1.0f;
    public float CompactPopupBorderSize { get; set; } = 1.0f;
    public float CompactFrameBorderSize { get; set; } = 0.0f;
    
    // CompactUI Specific Tooltip and Context Menu Settings
    public float CompactTooltipRounding { get; set; } = 8.0f;
    public float CompactTooltipBorderSize { get; set; } = 1.0f;
    public float CompactContextMenuRounding { get; set; } = 8.0f;
    public float CompactContextMenuBorderSize { get; set; } = 1.0f;
    
    // Border Settings
    public float WindowBorderSize { get; set; } = 1.5f;
    public float ChildBorderSize { get; set; } = 1.5f;
    public float PopupBorderSize { get; set; } = 1.5f;
    public float FrameBorderSize { get; set; } = 1.0f;
    
    // Scrollbar Settings
    public float ScrollbarSize { get; set; } = 16.0f;
    public float GrabMinSize { get; set; } = 12.0f;
    
    // Basic Color Settings (as Vector4 for RGBA)
    public Vector4 PrimaryDark { get; set; } = new(0.1f, 0.1f, 0.15f, 0.8f);
    public Vector4 SecondaryDark { get; set; } = new(0.15f, 0.15f, 0.2f, 0.8f);
    public Vector4 AccentBlue { get; set; } = new(0.2f, 0.4f, 0.8f, 1.0f);
    public Vector4 AccentCyan { get; set; } = new(0.3f, 0.6f, 0.9f, 1.0f);
    public Vector4 TextPrimary { get; set; } = new(0.9f, 0.9f, 0.9f, 1.0f);
    public Vector4 TextSecondary { get; set; } = new(0.7f, 0.7f, 0.7f, 1.0f);
    public Vector4 Border { get; set; } = new(0.3f, 0.3f, 0.4f, 1.0f);
    public Vector4 Hover { get; set; } = new(0.25f, 0.45f, 0.85f, 1.0f);
    public Vector4 Active { get; set; } = new(0.3f, 0.5f, 0.9f, 1.0f);
    public Vector4 HeaderBg { get; set; } = new(0.2f, 0.4f, 0.8f, 0.3f);
    
    // Extended Color Settings for complete customization
    // Window Colors
    public Vector4 WindowBg { get; set; } = new(0.06f, 0.06f, 0.07f, 0.94f);
    public Vector4 ChildBg { get; set; } = new(0.0f, 0.0f, 0.0f, 0.0f);
    public Vector4 PopupBg { get; set; } = new(0.08f, 0.08f, 0.08f, 0.94f);
    public Vector4 BorderShadow { get; set; } = new(0.0f, 0.0f, 0.0f, 0.0f);
    
    // Frame Colors
    public Vector4 FrameBg { get; set; } = new(0.16f, 0.29f, 0.48f, 0.54f);
    public Vector4 FrameBgHovered { get; set; } = new(0.26f, 0.59f, 0.98f, 0.40f);
    public Vector4 FrameBgActive { get; set; } = new(0.26f, 0.59f, 0.98f, 0.67f);
    
    // Title Bar Colors
    public Vector4 TitleBg { get; set; } = new(0.04f, 0.04f, 0.04f, 1.0f);
    public Vector4 TitleBgActive { get; set; } = new(0.16f, 0.29f, 0.48f, 1.0f);
    public Vector4 TitleBgCollapsed { get; set; } = new(0.0f, 0.0f, 0.0f, 0.51f);
    
    // Menu Colors
    public Vector4 MenuBarBg { get; set; } = new(0.14f, 0.14f, 0.14f, 1.0f);
    
    // Scrollbar Colors
    public Vector4 ScrollbarBg { get; set; } = new(0.02f, 0.02f, 0.02f, 0.53f);
    public Vector4 ScrollbarGrab { get; set; } = new(0.31f, 0.31f, 0.31f, 1.0f);
    public Vector4 ScrollbarGrabHovered { get; set; } = new(0.41f, 0.41f, 0.41f, 1.0f);
    public Vector4 ScrollbarGrabActive { get; set; } = new(0.51f, 0.51f, 0.51f, 1.0f);
    
    // Check Mark Colors
    public Vector4 CheckMark { get; set; } = new(0.26f, 0.59f, 0.98f, 1.0f);
    
    // Slider Colors
    public Vector4 SliderGrab { get; set; } = new(0.24f, 0.52f, 0.88f, 1.0f);
    public Vector4 SliderGrabActive { get; set; } = new(0.26f, 0.59f, 0.98f, 1.0f);
    
    // Button Colors
    public Vector4 Button { get; set; } = new(0.26f, 0.59f, 0.98f, 0.40f);
    public Vector4 ButtonHovered { get; set; } = new(0.26f, 0.59f, 0.98f, 1.0f);
    public Vector4 ButtonActive { get; set; } = new(0.06f, 0.53f, 0.98f, 1.0f);
    
    // Header Colors
    public Vector4 Header { get; set; } = new(0.26f, 0.59f, 0.98f, 0.31f);
    public Vector4 HeaderHovered { get; set; } = new(0.26f, 0.59f, 0.98f, 0.80f);
    public Vector4 HeaderActive { get; set; } = new(0.26f, 0.59f, 0.98f, 1.0f);
    
    // Separator Colors
    public Vector4 Separator { get; set; } = new(0.43f, 0.43f, 0.50f, 0.50f);
    public Vector4 SeparatorHovered { get; set; } = new(0.10f, 0.40f, 0.75f, 0.78f);
    public Vector4 SeparatorActive { get; set; } = new(0.10f, 0.40f, 0.75f, 1.0f);
    
    // Resize Grip Colors
    public Vector4 ResizeGrip { get; set; } = new(0.26f, 0.59f, 0.98f, 0.20f);
    public Vector4 ResizeGripHovered { get; set; } = new(0.26f, 0.59f, 0.98f, 0.67f);
    public Vector4 ResizeGripActive { get; set; } = new(0.26f, 0.59f, 0.98f, 0.95f);
    
    // Tab Colors
    public Vector4 Tab { get; set; } = new(0.18f, 0.35f, 0.58f, 0.86f);
    public Vector4 TabHovered { get; set; } = new(0.26f, 0.59f, 0.98f, 0.80f);
    public Vector4 TabActive { get; set; } = new(0.20f, 0.41f, 0.68f, 1.0f);
    public Vector4 TabUnfocused { get; set; } = new(0.07f, 0.10f, 0.15f, 0.97f);
    public Vector4 TabUnfocusedActive { get; set; } = new(0.14f, 0.26f, 0.42f, 1.0f);
    
    // Table Colors
    public Vector4 TableHeaderBg { get; set; } = new(0.19f, 0.19f, 0.20f, 1.0f);
    public Vector4 TableBorderStrong { get; set; } = new(0.31f, 0.31f, 0.35f, 1.0f);
    public Vector4 TableBorderLight { get; set; } = new(0.23f, 0.23f, 0.25f, 1.0f);
    public Vector4 TableRowBg { get; set; } = new(0.0f, 0.0f, 0.0f, 0.0f);
    public Vector4 TableRowBgAlt { get; set; } = new(1.0f, 1.0f, 1.0f, 0.06f);
    
    // Text Colors
    public Vector4 TextDisabled { get; set; } = new(0.5f, 0.5f, 0.5f, 1.0f);
    public Vector4 TextSelectedBg { get; set; } = new(0.26f, 0.59f, 0.98f, 0.35f);
    
    // Drag Drop Colors
    public Vector4 DragDropTarget { get; set; } = new(1.0f, 1.0f, 0.0f, 0.90f);
    
    // Navigation Colors
    public Vector4 NavHighlight { get; set; } = new(0.26f, 0.59f, 0.98f, 1.0f);
    public Vector4 NavWindowingHighlight { get; set; } = new(1.0f, 1.0f, 1.0f, 0.70f);
    public Vector4 NavWindowingDimBg { get; set; } = new(0.80f, 0.80f, 0.80f, 0.20f);
    
    // Modal Colors
    public Vector4 ModalWindowDimBg { get; set; } = new(0.80f, 0.80f, 0.80f, 0.35f);
    
    // CompactUI Specific Colors (separate from normal windows)
    public Vector4 CompactWindowBg { get; set; } = new(0.08f, 0.08f, 0.12f, 0.9f);
    public Vector4 CompactChildBg { get; set; } = new(0.0f, 0.0f, 0.0f, 0.0f);
    public Vector4 CompactPopupBg { get; set; } = new(0.1f, 0.1f, 0.15f, 0.95f);
    public Vector4 CompactTitleBg { get; set; } = new(0.06f, 0.06f, 0.1f, 1.0f);
    public Vector4 CompactTitleBgActive { get; set; } = new(0.1f, 0.2f, 0.4f, 1.0f);
    public Vector4 CompactFrameBg { get; set; } = new(0.12f, 0.2f, 0.35f, 0.6f);
    public Vector4 CompactButton { get; set; } = new(0.1f, 0.15f, 0.25f, 0.8f);
    public Vector4 CompactButtonHovered { get; set; } = new(0.15f, 0.25f, 0.4f, 0.9f);
    public Vector4 CompactButtonActive { get; set; } = new(0.2f, 0.35f, 0.55f, 1.0f);
    public Vector4 CompactHeaderBg { get; set; } = new(0.08f, 0.15f, 0.3f, 0.8f);
    public Vector4 CompactBorder { get; set; } = new(0.3f, 0.5f, 0.8f, 0.8f);
    public Vector4 CompactText { get; set; } = new(0.9f, 0.9f, 0.9f, 1.0f);
    public Vector4 CompactTextSecondary { get; set; } = new(0.7f, 0.7f, 0.7f, 1.0f);
    public Vector4 CompactAccent { get; set; } = new(0.2f, 0.4f, 0.8f, 1.0f);
    public Vector4 CompactHover { get; set; } = new(0.3f, 0.5f, 0.9f, 0.8f);
    public Vector4 CompactActive { get; set; } = new(0.4f, 0.6f, 1.0f, 0.9f);
    public Vector4 CompactHeaderText { get; set; } = new(0.8f, 0.6f, 0.2f, 1.0f); // Golden header text for CompactUI
    
    // CompactUI Specific Status Colors
    public Vector4 CompactUidColor { get; set; } = new(0.2f, 0.8f, 0.4f, 1.0f); // Green for UID display
    public Vector4 CompactServerStatusConnected { get; set; } = new(0.2f, 0.8f, 0.4f, 1.0f); // Green for connected
    public Vector4 CompactServerStatusWarning { get; set; } = new(1.0f, 0.8f, 0.2f, 1.0f); // Yellow for warnings
    public Vector4 CompactServerStatusError { get; set; } = new(0.8f, 0.2f, 0.2f, 1.0f); // Red for errors
    
    // CompactUI Action Button Colors (for syncshell buttons, etc.)
    public Vector4 CompactActionButton { get; set; } = new(0.15f, 0.3f, 0.6f, 0.8f); // Blue action buttons
    public Vector4 CompactActionButtonHovered { get; set; } = new(0.2f, 0.4f, 0.7f, 0.9f); // Lighter blue on hover
    public Vector4 CompactActionButtonActive { get; set; } = new(0.25f, 0.5f, 0.8f, 1.0f); // Even lighter blue when pressed
    
    // CompactUI Syncshell Button Colors (separate from general action buttons)
    public Vector4 CompactSyncshellButton { get; set; } = new(0.3f, 0.5f, 0.9f, 0.4f); // Syncshell buttons
    public Vector4 CompactSyncshellButtonHovered { get; set; } = new(0.4f, 0.6f, 1.0f, 0.6f); // Syncshell buttons on hover
    public Vector4 CompactSyncshellButtonActive { get; set; } = new(0.5f, 0.7f, 1.0f, 0.8f); // Syncshell buttons when pressed
    
    // CompactUI Navigation and Header Text Colors
    public Vector4 CompactPanelTitleText { get; set; } = new(0.8f, 0.6f, 0.2f, 1.0f); // Golden panel title text
    public Vector4 CompactConnectedText { get; set; } = new(0.7f, 0.9f, 0.7f, 1.0f); // Light green for connected pairs count
    
    // CompactUI Syncshell State Text Colors
    public Vector4 CompactAllSyncshellsText { get; set; } = new(0.9f, 0.9f, 0.9f, 1.0f); // White for "All Syncshells" text
    public Vector4 CompactOfflinePausedText { get; set; } = new(0.8f, 0.6f, 0.4f, 1.0f); // Orange for "Offline / Paused by other" text
    public Vector4 CompactOfflineSyncshellText { get; set; } = new(0.7f, 0.5f, 0.5f, 1.0f); // Light red for "Offline Syncshell Users" text
    public Vector4 CompactVisibleText { get; set; } = new(0.6f, 0.9f, 0.6f, 1.0f); // Light green for "Visible" text
    public Vector4 CompactPairsText { get; set; } = new(0.8f, 0.8f, 1.0f, 1.0f); // Light blue for "Pairs" text
    
    // CompactUI Header Settings
    public bool CompactShowImGuiHeader { get; set; } = false; // Show ImGui header instead of custom header
    
    // Event to notify when theme changes
    public event Action? ThemeChanged;
    
    // Method to trigger theme change notification
    public void NotifyThemeChanged()
    {
        ThemeChanged?.Invoke();
    }
    
    // Create a copy of the current configuration
    public ThemeConfiguration Clone()
    {
        return new ThemeConfiguration
        {
            WindowRounding = WindowRounding,
            ChildRounding = ChildRounding,
            PopupRounding = PopupRounding,
            FrameRounding = FrameRounding,
            ScrollbarRounding = ScrollbarRounding,
            GrabRounding = GrabRounding,
            TabRounding = TabRounding,
            CompactWindowRounding = CompactWindowRounding,
            CompactChildRounding = CompactChildRounding,
            CompactPopupRounding = CompactPopupRounding,
            CompactFrameRounding = CompactFrameRounding,
            CompactScrollbarRounding = CompactScrollbarRounding,
            CompactGrabRounding = CompactGrabRounding,
            CompactTabRounding = CompactTabRounding,
            CompactHeaderRounding = CompactHeaderRounding,
            WindowPadding = WindowPadding,
            FramePadding = FramePadding,
            ItemSpacing = ItemSpacing,
            ItemInnerSpacing = ItemInnerSpacing,
            IndentSpacing = IndentSpacing,
            CompactWindowPadding = CompactWindowPadding,
            CompactFramePadding = CompactFramePadding,
            CompactItemSpacing = CompactItemSpacing,
            CompactItemInnerSpacing = CompactItemInnerSpacing,
            CompactCellPadding = CompactCellPadding,
            CompactChildPadding = CompactChildPadding,
            CompactIndentSpacing = CompactIndentSpacing,
            CompactScrollbarSize = CompactScrollbarSize,
            CompactGrabMinSize = CompactGrabMinSize,
            CompactButtonTextAlign = CompactButtonTextAlign,
            CompactSelectableTextAlign = CompactSelectableTextAlign,
            WindowBorderSize = WindowBorderSize,
            ChildBorderSize = ChildBorderSize,
            PopupBorderSize = PopupBorderSize,
            FrameBorderSize = FrameBorderSize,
            CompactWindowBorderSize = CompactWindowBorderSize,
            CompactChildBorderSize = CompactChildBorderSize,
            CompactPopupBorderSize = CompactPopupBorderSize,
            CompactFrameBorderSize = CompactFrameBorderSize,
            CompactTooltipRounding = CompactTooltipRounding,
            CompactTooltipBorderSize = CompactTooltipBorderSize,
            CompactContextMenuRounding = CompactContextMenuRounding,
            CompactContextMenuBorderSize = CompactContextMenuBorderSize,
            ScrollbarSize = ScrollbarSize,
            GrabMinSize = GrabMinSize,
            PrimaryDark = PrimaryDark,
            SecondaryDark = SecondaryDark,
            AccentBlue = AccentBlue,
            AccentCyan = AccentCyan,
            TextPrimary = TextPrimary,
            TextSecondary = TextSecondary,
            Border = Border,
            Hover = Hover,
            Active = Active,
            HeaderBg = HeaderBg,
            CompactWindowBg = CompactWindowBg,
            CompactChildBg = CompactChildBg,
            CompactPopupBg = CompactPopupBg,
            CompactTitleBg = CompactTitleBg,
            CompactTitleBgActive = CompactTitleBgActive,
            CompactFrameBg = CompactFrameBg,
            CompactButton = CompactButton,
            CompactButtonHovered = CompactButtonHovered,
            CompactButtonActive = CompactButtonActive,
            CompactHeaderBg = CompactHeaderBg,
            CompactBorder = CompactBorder,
            CompactText = CompactText,
            CompactTextSecondary = CompactTextSecondary,
            CompactAccent = CompactAccent,
            CompactHover = CompactHover,
            CompactActive = CompactActive,
            CompactHeaderText = CompactHeaderText,
            CompactUidColor = CompactUidColor,
            CompactServerStatusConnected = CompactServerStatusConnected,
            CompactServerStatusWarning = CompactServerStatusWarning,
            CompactServerStatusError = CompactServerStatusError,
            CompactActionButton = CompactActionButton,
            CompactActionButtonHovered = CompactActionButtonHovered,
            CompactActionButtonActive = CompactActionButtonActive,
            CompactSyncshellButton = CompactSyncshellButton,
            CompactSyncshellButtonHovered = CompactSyncshellButtonHovered,
            CompactSyncshellButtonActive = CompactSyncshellButtonActive,
            CompactPanelTitleText = CompactPanelTitleText,
            CompactConnectedText = CompactConnectedText,
            CompactAllSyncshellsText = CompactAllSyncshellsText,
            CompactOfflinePausedText = CompactOfflinePausedText,
            CompactOfflineSyncshellText = CompactOfflineSyncshellText,
            CompactVisibleText = CompactVisibleText,
            CompactPairsText = CompactPairsText,
            CompactShowImGuiHeader = CompactShowImGuiHeader
        };
    }
    
    // Reset to default values
    public void ResetToDefaults()
    {
        WindowRounding = 8.0f;
        ChildRounding = 8.0f;
        PopupRounding = 8.0f;
        FrameRounding = 6.0f;
        ScrollbarRounding = 6.0f;
        GrabRounding = 6.0f;
        TabRounding = 6.0f;
        CompactWindowRounding = 12.0f;
        CompactChildRounding = 12.0f;
        CompactPopupRounding = 12.0f;
        WindowPadding = new Vector2(16.0f, 12.0f);
        FramePadding = new Vector2(6.0f, 3.0f);
        ItemSpacing = new Vector2(6.0f, 4.0f);
        ItemInnerSpacing = new Vector2(4.0f, 3.0f);
        IndentSpacing = 20.0f;
        WindowBorderSize = 1.5f;
        ChildBorderSize = 1.5f;
        PopupBorderSize = 1.5f;
        FrameBorderSize = 1.0f;
        ScrollbarSize = 16.0f;
        GrabMinSize = 12.0f;
        PrimaryDark = new Vector4(0.1f, 0.1f, 0.15f, 0.8f);
        SecondaryDark = new Vector4(0.15f, 0.15f, 0.2f, 0.8f);
        AccentBlue = new Vector4(0.2f, 0.4f, 0.8f, 1.0f);
        AccentCyan = new Vector4(0.3f, 0.6f, 0.9f, 1.0f);
        TextPrimary = new Vector4(0.9f, 0.9f, 0.9f, 1.0f);
        TextSecondary = new Vector4(0.7f, 0.7f, 0.7f, 1.0f);
        Border = new Vector4(0.3f, 0.3f, 0.4f, 1.0f);
        Hover = new Vector4(0.25f, 0.45f, 0.85f, 1.0f);
        Active = new Vector4(0.3f, 0.5f, 0.9f, 1.0f);
        HeaderBg = new Vector4(0.2f, 0.4f, 0.8f, 0.3f);
        
        NotifyThemeChanged();
    }
}