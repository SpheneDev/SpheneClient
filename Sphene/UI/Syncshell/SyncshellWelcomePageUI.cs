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
using Sphene.UI.Components;
using Sphene.UI.Design;

namespace Sphene.UI.Syncshell;

public class SyncshellWelcomePageUI : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private readonly SyncshellWelcomePageDto _welcomePage;
    private readonly string _syncshellName;
    private readonly string _welcomeImageKey;

    public SyncshellWelcomePageUI(ILogger<SyncshellWelcomePageUI> logger, SpheneMediator mediator,
        SpheneConfigService configService, SyncshellWelcomePageDto welcomePage, string syncshellName,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, $"Welcome to {syncshellName}!###SyncshellWelcomePage", performanceCollectorService)
    {
        _configService = configService;
        _welcomePage = welcomePage;
        _syncshellName = syncshellName;
        _welcomeImageKey = $"welcome:{welcomePage.GroupGID}";

        _logger.LogDebug("SyncshellWelcomePageUI created with syncshellName: {SyncshellName}", syncshellName);

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
        SpheneUI.Text.PageHeader($"Welcome to {_syncshellName}!");

        // Scrollable content area reserving room for the sticky footer
        if (SpheneUI.Layout.BeginFillToBottomScrollArea("WelcomeContent", 100f * SpheneUI.Scale))
        {
            // Display welcome image if available
            var welcomeImage = string.IsNullOrEmpty(_welcomePage.WelcomeImageBase64)
                ? null
                : SpheneUI.Media.Texture(_welcomeImageKey,
                    () => Convert.FromBase64String(_welcomePage.WelcomeImageBase64));
            if (welcomeImage != null)
            {
                var imageWindowWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                SpheneUI.Media.Image(welcomeImage, new Vector2(imageWindowWidth, 200f), ImageFit.Fit);
                ImGuiHelpers.ScaledDummy(2f);
            }
            else if (!string.IsNullOrEmpty(_welcomePage.WelcomeImageBase64))
            {
                SpheneUI.Media.Placeholder(new Vector2(ImGui.GetContentRegionAvail().X, 200f));
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
        SpheneUI.Layout.EndScrollArea();

        // Sticky button area at the bottom
        SpheneUI.Layout.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Add checkbox to disable future welcome messages
        var showWelcomeMessages = _configService.Current.ShowAreaBoundSyncshellWelcomeMessages;
        if (SpheneUI.Controls.Toggle("Show welcome messages in the future", ref showWelcomeMessages))
        {
            _configService.Current.ShowAreaBoundSyncshellWelcomeMessages = showWelcomeMessages;
            _configService.Save();
        }
        SpheneUI.AttachTooltip("Uncheck this to disable welcome messages for area-bound syncshells");
        
        ImGuiHelpers.ScaledDummy(2f);

        // Center the close button
        var buttonSize = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, "Got it!");
        var windowWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var buttonX = (windowWidth - buttonSize) * 0.5f;
        
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + buttonX);
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Got it!"))
        {
            IsOpen = false;
        }
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}
