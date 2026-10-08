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
using Sphene.UI.Design;

namespace Sphene.UI.Components;

public class WelcomePageLivePreviewUI : WindowMediatorSubscriberBase
{
    private GroupFullInfoDto? _groupFullInfo;
    private string _welcomeText = string.Empty;
    private IDalamudTextureWrap? _welcomeImageTexture;

    public WelcomePageLivePreviewUI(ILogger<WelcomePageLivePreviewUI> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Welcome Page Preview###WelcomePagePreview", performanceCollectorService)
    {
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
            SpheneUI.Text.Muted("No welcome page data available.");
            return;
        }

        // Simulate the welcome popup header
        SpheneUI.Text.PageHeader($"Welcome to {_groupFullInfo.GroupAliasOrGID}!");

        // Scrollable content area reserving room for the sticky footer
        if (SpheneUI.Layout.BeginFillToBottomScrollArea("WelcomePreviewContent", 80f * SpheneUI.Scale))
        {
            // Display welcome image if available (same logic as SyncshellWelcomePageUI)
            if (_welcomeImageTexture != null)
            {
                var imageWindowWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                SpheneUI.Media.Image(_welcomeImageTexture, new Vector2(imageWindowWidth, 200f), ImageFit.Fit);
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
        SpheneUI.Layout.EndScrollArea();

        // Sticky button area at the bottom
        SpheneUI.Layout.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Center the close button (same logic as SyncshellWelcomePageUI)
        var buttonSize = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, "Got it!");
        var windowWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var buttonX = (windowWidth - buttonSize) * 0.5f;
        
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + buttonX);
        
        // Make the button disabled to show it's just a preview
        ImGui.BeginDisabled();
        SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Got it!");
        ImGui.EndDisabled();
        
        ImGuiHelpers.ScaledDummy(1f);
        
        // Add preview note
        SpheneUI.Layout.Separator();
        ImGuiHelpers.ScaledDummy(1f);
        
        var noteText = "This is a preview of how the welcome popup will appear to users.";
        var textSize = ImGui.CalcTextSize(noteText);
        var noteX = (windowWidth - textSize.X) * 0.5f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + noteX);
        SpheneUI.Text.Colored(noteText, ImGuiColors.DalamudGrey);
    }
}