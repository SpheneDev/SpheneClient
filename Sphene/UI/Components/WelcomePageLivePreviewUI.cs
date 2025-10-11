using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Sphene.UI.Components;

public class WelcomePageLivePreviewUI : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiSharedService;
    private GroupFullInfoDto? _groupFullInfo;
    private string _welcomeText = string.Empty;
    private IDalamudTextureWrap? _welcomeImageTexture;

    public WelcomePageLivePreviewUI(ILogger<WelcomePageLivePreviewUI> logger, SpheneMediator mediator,
        UiSharedService uiSharedService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Welcome Page Preview###WelcomePagePreview", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        
        SizeConstraints = new()
        {
            MinimumSize = new(700, 500),
            MaximumSize = new(700, 500)
        };
        
        Mediator.Subscribe<OpenWelcomePageLivePreviewMessage>(this, (msg) =>
        {
            _groupFullInfo = msg.GroupFullInfo;
            _welcomeText = msg.WelcomeText;
            _welcomeImageTexture = msg.WelcomeImageTexture;
            WindowName = $"Welcome Page Preview - {_groupFullInfo.GroupAliasOrGID}###WelcomePagePreview";
            IsOpen = true;
        });
        
        Mediator.Subscribe<UpdateWelcomePageLivePreviewMessage>(this, (msg) =>
        {
            _welcomeText = msg.WelcomeText;
            _welcomeImageTexture = msg.WelcomeImageTexture;
        });
        
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize;
    }

    protected override void DrawInternal()
    {
        if (_groupFullInfo == null)
        {
            ImGui.TextUnformatted("No welcome page data available.");
            return;
        }
        
        // Simulate the welcome popup header
        using (_uiSharedService.UidFont.Push())
            ImGui.TextUnformatted($"Welcome to {_groupFullInfo.GroupAliasOrGID}!");

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Calculate available height for content (total height minus header, separator, button area, preview note)
        var windowHeight = ImGui.GetWindowHeight();
        var headerHeight = ImGui.GetCursorPosY();
        var buttonHeight = 80f; // Space for button, preview note and padding
        var availableContentHeight = windowHeight - headerHeight - buttonHeight;

        // Create scrollable content area
        using (var contentChild = ImRaii.Child("WelcomePreviewContent", new Vector2(0, availableContentHeight), false, ImGuiWindowFlags.None))
        {
            if (contentChild.Success)
            {
                // Display welcome image if available (same logic as SyncshellWelcomePageUI)
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

                // Display welcome text with Markdown support (same logic as SyncshellWelcomePageUI)
                if (!string.IsNullOrEmpty(_welcomeText))
                {
                    MarkdownRenderer.RenderMarkdown(_welcomeText);
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

        // Center the close button (same logic as SyncshellWelcomePageUI)
        var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Check, "Got it!");
        var windowWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var buttonX = (windowWidth - buttonSize) * 0.5f;
        
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + buttonX);
        
        // Make the button disabled to show it's just a preview
        ImGui.BeginDisabled();
        _uiSharedService.IconTextButton(FontAwesomeIcon.Check, "Got it!");
        ImGui.EndDisabled();
        
        ImGuiHelpers.ScaledDummy(1f);
        
        // Add preview note
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(1f);
        
        var noteText = "This is a preview of how the welcome popup will appear to users.";
        var textSize = ImGui.CalcTextSize(noteText);
        var noteX = (windowWidth - textSize.X) * 0.5f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + noteX);
        ImGui.TextColored(ImGuiColors.DalamudGrey, noteText);
    }
}