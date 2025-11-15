using System.Linq;

namespace Sphene.UI.Theme;

public static class ThemePropertyCopier
{
    public static void Copy(ThemeConfiguration source, ThemeConfiguration target)
    {
        // Clone to ensure we copy a stable snapshot of all fields
        var cloned = source.Clone();

        // Rounding
        target.WindowRounding = cloned.WindowRounding;
        target.ChildRounding = cloned.ChildRounding;
        target.PopupRounding = cloned.PopupRounding;
        target.FrameRounding = cloned.FrameRounding;
        target.ScrollbarRounding = cloned.ScrollbarRounding;
        target.GrabRounding = cloned.GrabRounding;
        target.TabRounding = cloned.TabRounding;
        target.CompactWindowRounding = cloned.CompactWindowRounding;
        target.CompactChildRounding = cloned.CompactChildRounding;
        target.CompactPopupRounding = cloned.CompactPopupRounding;
        target.CompactFrameRounding = cloned.CompactFrameRounding;
        target.CompactScrollbarRounding = cloned.CompactScrollbarRounding;
        target.CompactGrabRounding = cloned.CompactGrabRounding;
        target.CompactTabRounding = cloned.CompactTabRounding;
        target.CompactHeaderRounding = cloned.CompactHeaderRounding;

        // Spacing
        target.WindowPadding = cloned.WindowPadding;
        target.FramePadding = cloned.FramePadding;
        target.ItemSpacing = cloned.ItemSpacing;
        target.ItemInnerSpacing = cloned.ItemInnerSpacing;
        target.IndentSpacing = cloned.IndentSpacing;
        target.CompactWindowPadding = cloned.CompactWindowPadding;
        target.CompactFramePadding = cloned.CompactFramePadding;
        target.CompactItemSpacing = cloned.CompactItemSpacing;
        target.CompactItemInnerSpacing = cloned.CompactItemInnerSpacing;
        target.CompactCellPadding = cloned.CompactCellPadding;
        target.CompactChildPadding = cloned.CompactChildPadding;
        target.CompactIndentSpacing = cloned.CompactIndentSpacing;
        target.CompactScrollbarSize = cloned.CompactScrollbarSize;
        target.CompactGrabMinSize = cloned.CompactGrabMinSize;
        target.CompactButtonTextAlign = cloned.CompactButtonTextAlign;
        target.CompactSelectableTextAlign = cloned.CompactSelectableTextAlign;

        // Borders and sizes
        target.WindowBorderSize = cloned.WindowBorderSize;
        target.ChildBorderSize = cloned.ChildBorderSize;
        target.PopupBorderSize = cloned.PopupBorderSize;
        target.FrameBorderSize = cloned.FrameBorderSize;
        target.CompactWindowBorderSize = cloned.CompactWindowBorderSize;
        target.CompactChildBorderSize = cloned.CompactChildBorderSize;
        target.CompactPopupBorderSize = cloned.CompactPopupBorderSize;
        target.CompactFrameBorderSize = cloned.CompactFrameBorderSize;
        target.CompactTooltipRounding = cloned.CompactTooltipRounding;
        target.CompactTooltipBorderSize = cloned.CompactTooltipBorderSize;
        target.CompactContextMenuRounding = cloned.CompactContextMenuRounding;
        target.CompactContextMenuBorderSize = cloned.CompactContextMenuBorderSize;
        target.ScrollbarSize = cloned.ScrollbarSize;
        target.GrabMinSize = cloned.GrabMinSize;

        // Core palette
        target.PrimaryDark = cloned.PrimaryDark;
        target.SecondaryDark = cloned.SecondaryDark;
        target.AccentBlue = cloned.AccentBlue;
        target.AccentCyan = cloned.AccentCyan;
        target.TextPrimary = cloned.TextPrimary;
        target.TextSecondary = cloned.TextSecondary;
        target.Border = cloned.Border;
        target.Hover = cloned.Hover;
        target.Active = cloned.Active;
        target.HeaderBg = cloned.HeaderBg;

        // Window colors
        target.WindowBg = cloned.WindowBg;
        target.ChildBg = cloned.ChildBg;
        target.PopupBg = cloned.PopupBg;
        target.BorderShadow = cloned.BorderShadow;

        // Frame colors
        target.FrameBg = cloned.FrameBg;
        target.FrameBgHovered = cloned.FrameBgHovered;
        target.FrameBgActive = cloned.FrameBgActive;

        // Title bar colors
        target.TitleBg = cloned.TitleBg;
        target.TitleBgActive = cloned.TitleBgActive;
        target.TitleBgCollapsed = cloned.TitleBgCollapsed;

        // Menu colors
        target.MenuBarBg = cloned.MenuBarBg;

        // Scrollbar colors
        target.ScrollbarBg = cloned.ScrollbarBg;
        target.ScrollbarGrab = cloned.ScrollbarGrab;
        target.ScrollbarGrabHovered = cloned.ScrollbarGrabHovered;
        target.ScrollbarGrabActive = cloned.ScrollbarGrabActive;

        // Check mark colors
        target.CheckMark = cloned.CheckMark;

        // Slider colors
        target.SliderGrab = cloned.SliderGrab;
        target.SliderGrabActive = cloned.SliderGrabActive;

        // Button colors
        target.Button = cloned.Button;
        target.ButtonHovered = cloned.ButtonHovered;
        target.ButtonActive = cloned.ButtonActive;

        // Header colors
        target.Header = cloned.Header;
        target.HeaderHovered = cloned.HeaderHovered;
        target.HeaderActive = cloned.HeaderActive;

        // Separator colors
        target.Separator = cloned.Separator;
        target.SeparatorHovered = cloned.SeparatorHovered;
        target.SeparatorActive = cloned.SeparatorActive;

        // Resize grip colors
        target.ResizeGrip = cloned.ResizeGrip;
        target.ResizeGripHovered = cloned.ResizeGripHovered;
        target.ResizeGripActive = cloned.ResizeGripActive;

        // Tab colors
        target.Tab = cloned.Tab;
        target.TabHovered = cloned.TabHovered;
        target.TabActive = cloned.TabActive;
        target.TabUnfocused = cloned.TabUnfocused;
        target.TabUnfocusedActive = cloned.TabUnfocusedActive;

        // Table colors
        target.TableHeaderBg = cloned.TableHeaderBg;
        target.TableBorderStrong = cloned.TableBorderStrong;
        target.TableBorderLight = cloned.TableBorderLight;
        target.TableRowBg = cloned.TableRowBg;
        target.TableRowBgAlt = cloned.TableRowBgAlt;

        // Text extras
        target.TextDisabled = cloned.TextDisabled;
        target.TextSelectedBg = cloned.TextSelectedBg;

        // Drag drop
        target.DragDropTarget = cloned.DragDropTarget;

        // Navigation
        target.NavHighlight = cloned.NavHighlight;
        target.NavWindowingHighlight = cloned.NavWindowingHighlight;
        target.NavWindowingDimBg = cloned.NavWindowingDimBg;

        // Modal
        target.ModalWindowDimBg = cloned.ModalWindowDimBg;

        // CompactUI specific colors
        target.CompactWindowBg = cloned.CompactWindowBg;
        target.CompactChildBg = cloned.CompactChildBg;
        target.CompactPopupBg = cloned.CompactPopupBg;
        target.CompactTitleBg = cloned.CompactTitleBg;
        target.CompactTitleBgActive = cloned.CompactTitleBgActive;
        target.CompactFrameBg = cloned.CompactFrameBg;
        target.CompactButton = cloned.CompactButton;
        target.CompactButtonHovered = cloned.CompactButtonHovered;
        target.CompactButtonActive = cloned.CompactButtonActive;
        target.CompactHeaderBg = cloned.CompactHeaderBg;
        target.CompactBorder = cloned.CompactBorder;
        target.CompactText = cloned.CompactText;
        target.CompactTextSecondary = cloned.CompactTextSecondary;
        target.CompactAccent = cloned.CompactAccent;
        target.CompactHover = cloned.CompactHover;
        target.CompactActive = cloned.CompactActive;
        target.CompactHeaderText = cloned.CompactHeaderText;
        target.CompactUidFontScale = cloned.CompactUidFontScale;
        target.CompactUidColor = cloned.CompactUidColor;

        // Update Hint controls (runtime toggle ForceShowUpdateHint is not copied)
        target.CompactUpdateHintColor = cloned.CompactUpdateHintColor;
        target.CompactUpdateHintHeight = cloned.CompactUpdateHintHeight;
        target.CompactUpdateHintPaddingY = cloned.CompactUpdateHintPaddingY;
        target.CompactServerStatusConnected = cloned.CompactServerStatusConnected;
        target.CompactServerStatusWarning = cloned.CompactServerStatusWarning;
        target.CompactServerStatusError = cloned.CompactServerStatusError;
        target.CompactActionButton = cloned.CompactActionButton;
        target.CompactActionButtonHovered = cloned.CompactActionButtonHovered;
        target.CompactActionButtonActive = cloned.CompactActionButtonActive;
        target.CompactSyncshellButton = cloned.CompactSyncshellButton;
        target.CompactSyncshellButtonHovered = cloned.CompactSyncshellButtonHovered;
        target.CompactSyncshellButtonActive = cloned.CompactSyncshellButtonActive;
        target.CompactPanelTitleText = cloned.CompactPanelTitleText;
        target.CompactConnectedText = cloned.CompactConnectedText;
        target.CompactAllSyncshellsText = cloned.CompactAllSyncshellsText;
        target.CompactOfflinePausedText = cloned.CompactOfflinePausedText;
        target.CompactOfflineSyncshellText = cloned.CompactOfflineSyncshellText;
        target.CompactVisibleText = cloned.CompactVisibleText;
        target.CompactPairsText = cloned.CompactPairsText;
        target.CompactShowImGuiHeader = cloned.CompactShowImGuiHeader;

        // Progress bars
        target.ProgressBarRounding = cloned.ProgressBarRounding;
        target.CompactProgressBarHeight = cloned.CompactProgressBarHeight;
        target.CompactProgressBarWidth = cloned.CompactProgressBarWidth;
        target.CompactProgressBarBackground = cloned.CompactProgressBarBackground;
        target.CompactProgressBarForeground = cloned.CompactProgressBarForeground;
        target.CompactProgressBarBorder = cloned.CompactProgressBarBorder;
        target.ProgressBarUseGradient = cloned.ProgressBarUseGradient;
        target.ProgressBarGradientStart = cloned.ProgressBarGradientStart;
        target.ProgressBarGradientEnd = cloned.ProgressBarGradientEnd;

        // Bars
        target.AutoTransmissionBarWidth = cloned.AutoTransmissionBarWidth;
        target.TransmissionUseGradient = cloned.TransmissionUseGradient;
        target.TransmissionGradientStart = cloned.TransmissionGradientStart;
        target.TransmissionGradientEnd = cloned.TransmissionGradientEnd;

        target.SeparateTransmissionBarStyles = cloned.SeparateTransmissionBarStyles;
        target.UploadTransmissionBarRounding = cloned.UploadTransmissionBarRounding;
        target.UploadTransmissionBarHeight = cloned.UploadTransmissionBarHeight;
        target.UploadTransmissionBarBackground = cloned.UploadTransmissionBarBackground;
        target.UploadTransmissionBarForeground = cloned.UploadTransmissionBarForeground;
        target.UploadTransmissionBarBorder = cloned.UploadTransmissionBarBorder;
        target.UploadTransmissionGradientStart = cloned.UploadTransmissionGradientStart;
        target.UploadTransmissionGradientEnd = cloned.UploadTransmissionGradientEnd;
        target.DownloadTransmissionBarRounding = cloned.DownloadTransmissionBarRounding;
        target.DownloadTransmissionBarHeight = cloned.DownloadTransmissionBarHeight;
        target.DownloadTransmissionBarBackground = cloned.DownloadTransmissionBarBackground;
        target.DownloadTransmissionBarForeground = cloned.DownloadTransmissionBarForeground;
        target.DownloadTransmissionBarBorder = cloned.DownloadTransmissionBarBorder;
        target.DownloadTransmissionGradientStart = cloned.DownloadTransmissionGradientStart;
        target.DownloadTransmissionGradientEnd = cloned.DownloadTransmissionGradientEnd;

        target.ShowTransmissionPreview = cloned.ShowTransmissionPreview;
        target.TransmissionPreviewUploadFill = cloned.TransmissionPreviewUploadFill;
        target.TransmissionPreviewDownloadFill = cloned.TransmissionPreviewDownloadFill;

        // Button style overrides
        target.ButtonStyles = cloned.ButtonStyles.ToDictionary(
            kv => kv.Key,
            kv => new ButtonStyleOverride
            {
                WidthDelta = kv.Value.WidthDelta,
                HeightDelta = kv.Value.HeightDelta,
                IconOffset = kv.Value.IconOffset,
                Button = kv.Value.Button,
                ButtonHovered = kv.Value.ButtonHovered,
                ButtonActive = kv.Value.ButtonActive,
                Text = kv.Value.Text,
                Icon = kv.Value.Icon,
                Border = kv.Value.Border,
                BorderSize = kv.Value.BorderSize
            }
        );
    }
}