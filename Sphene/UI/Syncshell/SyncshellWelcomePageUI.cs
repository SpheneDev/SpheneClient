using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Sphene.UI;

public class SyncshellWelcomePageUI : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private readonly SpheneConfigService _configService;
    private readonly SyncshellWelcomePageDto _welcomePage;
    private readonly string _syncshellName;
    private IDalamudTextureWrap? _welcomeImageTexture = null;

    public SyncshellWelcomePageUI(ILogger<SyncshellWelcomePageUI> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, SpheneConfigService configService, SyncshellWelcomePageDto welcomePage, string syncshellName,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, $"Welcome to {syncshellName}!###SyncshellWelcomePage", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _configService = configService;
        _welcomePage = welcomePage;
        _syncshellName = syncshellName;
        
        _logger.LogDebug("SyncshellWelcomePageUI created with syncshellName: {SyncshellName}", syncshellName);
        
        // Load image texture if available
        if (!string.IsNullOrEmpty(_welcomePage.WelcomeImageBase64))
        {
            try
            {
                var imageData = Convert.FromBase64String(_welcomePage.WelcomeImageBase64);
                _welcomeImageTexture = _uiSharedService.LoadImage(imageData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load welcome page image texture");
                _welcomeImageTexture = null;
            }
        }
        
        SizeConstraints = new()
        {
            MinimumSize = new(700, 500),
            MaximumSize = new(700, 500)
        };

        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize;
        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        using (_uiSharedService.UidFont.Push())
            ImGui.TextUnformatted($"Welcome to {_syncshellName}!");

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Calculate available height for content (total height minus header, separator, button area)
        var windowHeight = ImGui.GetWindowHeight();
        var headerHeight = ImGui.GetCursorPosY();
        var buttonHeight = 100f; // Space for checkbox, button and padding
        var availableContentHeight = windowHeight - headerHeight - buttonHeight;

        // Create scrollable content area
        using (var contentChild = ImRaii.Child("WelcomeContent", new Vector2(0, availableContentHeight), false, ImGuiWindowFlags.None))
        {
            if (contentChild.Success)
            {
                // Display welcome image if available
                if (_welcomeImageTexture != null)
                {
                    var imageSize = new Vector2(_welcomeImageTexture.Width, _welcomeImageTexture.Height);
                    var imageWindowWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                    var maxWidth = imageWindowWidth; // Use full popup width
                    var maxHeight = 200f; // Banner-style height
                    
                    // Scale image to fit within max dimensions while maintaining aspect ratio
                    if (imageSize.X > maxWidth || imageSize.Y > maxHeight)
                    {
                        var scaleX = maxWidth / imageSize.X;
                        var scaleY = maxHeight / imageSize.Y;
                        var scale = Math.Min(scaleX, scaleY);
                        imageSize = new Vector2(imageSize.X * scale, imageSize.Y * scale);
                    }
                    
                    // Center the image horizontally
                    var imageX = (imageWindowWidth - imageSize.X) * 0.5f;
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + imageX);
                    
                    ImGui.Image(_welcomeImageTexture.Handle, imageSize);
                    ImGuiHelpers.ScaledDummy(2f);
                }
                else if (!string.IsNullOrEmpty(_welcomePage.WelcomeImageBase64))
                {
                    ImGui.TextColored(ImGuiColors.DalamudRed, "[Failed to load image]");
                    ImGuiHelpers.ScaledDummy(2f);
                }

                // Display welcome text with Markdown support
                if (!string.IsNullOrEmpty(_welcomePage.WelcomeText))
                {
                    MarkdownRenderer.RenderMarkdown(_welcomePage.WelcomeText);
                }
                else
                {
                    MarkdownRenderer.RenderMarkdown("Welcome to this syncshell! We're glad to have you here.");
                }
            }
        }

        // Sticky button area at the bottom
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Add checkbox to disable future welcome messages
        var showWelcomeMessages = _configService.Current.ShowAreaBoundSyncshellWelcomeMessages;
        if (ImGui.Checkbox("Show welcome messages in the future", ref showWelcomeMessages))
        {
            _configService.Current.ShowAreaBoundSyncshellWelcomeMessages = showWelcomeMessages;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Uncheck this to disable welcome messages for area-bound syncshells");
        
        ImGuiHelpers.ScaledDummy(2f);

        // Center the close button
        var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Check, "Got it!");
        var windowWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var buttonX = (windowWidth - buttonSize) * 0.5f;
        
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + buttonX);
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, "Got it!"))
        {
            IsOpen = false;
        }
    }

    public override void OnClose()
    {
        _welcomeImageTexture?.Dispose();
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}