using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.UI.Components;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.UI.Panels;

public class NewOptionsUi : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private readonly UiSharedService _uiShared;
    private readonly ApiController _apiController;
    private readonly bool _shouldAutoOpen;
    private bool _hasAutoOpened;
    private bool _waitingDelayAfterClose;
    private DateTime _changelogClosedAtUtc;

    public NewOptionsUi(ILogger<NewOptionsUi> logger, SpheneMediator mediator, SpheneConfigService configService, UiSharedService uiShared, PerformanceCollectorService performanceCollectorService, ApiController apiController)
        : base(logger, mediator, "Sphene - New Options###SpheneNewOptions", performanceCollectorService)
    {
        _configService = configService;
        _uiShared = uiShared;
        _apiController = apiController;
        _shouldAutoOpen = UpdateOptionPanel.HasUnseenTag(_configService, supporterFeaturesEnabled: true);
        IsOpen = false;
        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 420),
            MaximumSize = new Vector2(1100, 1400)
        };

        Mediator.Subscribe<ShowReleaseChangelogMessage>(this, _ =>
        {
            if (_hasAutoOpened || !_shouldAutoOpen || !UpdateOptionPanel.HasUnseenTag(_configService, _apiController.SupporterFeaturesEnabled))
            {
                return;
            }

            _waitingDelayAfterClose = false;
            IsOpen = false;
        });

        Mediator.Subscribe<ReleaseChangelogClosedMessage>(this, _ =>
        {
            if (_hasAutoOpened || !_shouldAutoOpen || !UpdateOptionPanel.HasUnseenTag(_configService, _apiController.SupporterFeaturesEnabled))
            {
                return;
            }

            _waitingDelayAfterClose = true;
            _changelogClosedAtUtc = DateTime.UtcNow;
            IsOpen = false;
        });

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ =>
        {
            if (_hasAutoOpened || !_shouldAutoOpen || !UpdateOptionPanel.HasUnseenTag(_configService, _apiController.SupporterFeaturesEnabled))
            {
                return;
            }

            var changelogOpen = IsReleaseChangelogOpen();
            if (changelogOpen)
            {
                _waitingDelayAfterClose = false;
                IsOpen = false;
                return;
            }

            if (!_waitingDelayAfterClose)
            {
                return;
            }

            if ((DateTime.UtcNow - _changelogClosedAtUtc).TotalSeconds >= 3)
            {
                IsOpen = true;
                _hasAutoOpened = true;
                _waitingDelayAfterClose = false;
            }
        });
    }

    protected override void DrawInternal()
    {
        if (IsReleaseChangelogOpen())
        {
            IsOpen = false;
            return;
        }

        if (!UpdateOptionPanel.HasUnseenTag(_configService, _apiController.SupporterFeaturesEnabled))
        {
            IsOpen = false;
            return;
        }

        UiSharedService.ColorText("New Options", ImGuiColors.ParsedBlue);
        UiSharedService.ColorTextWrapped("These settings were added in recent updates. Review them here and adjust what you need.", ImGuiColors.DalamudGrey);
        ImGuiHelpers.ScaledDummy(0, 6);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0, 4);

        var footerHeight = ImGui.GetFrameHeightWithSpacing()
            + (ImGui.GetTextLineHeightWithSpacing() * 2f)
            + (ImGui.GetStyle().ItemSpacing.Y * 3f)
            + (ImGui.GetStyle().FramePadding.Y * 2f)
            + (ImGuiHelpers.GlobalScale * 4f);
        var optionsPaneHeight = Math.Max(0f, ImGui.GetContentRegionAvail().Y - footerHeight);
        if (ImGui.BeginChild("NewOptionsPane", new Vector2(-1, optionsPaneHeight), true, ImGuiWindowFlags.NoNav))
        {
            var pendingReleaseGroups = UpdateOptionPanel.GetPendingReleaseGroups(_configService, _apiController.SupporterFeaturesEnabled);
            for (var i = 0; i < pendingReleaseGroups.Count; i++)
            {
                var releaseGroup = pendingReleaseGroups[i];
                UiSharedService.ColorText($"Added in {releaseGroup.Tag}", ImGuiColors.ParsedBlue);
                ImGuiHelpers.ScaledDummy(0, 4);

                foreach (var link in releaseGroup.Links)
                {
                    UiSharedService.ColorText(UpdateOptionPanel.GetTitle(link), ImGuiColors.ParsedBlue);
                    UpdateOptionPanel.DrawByLink(link, _configService, _uiShared, Mediator, 240f, _apiController.SupporterFeaturesEnabled);
                    ImGuiHelpers.ScaledDummy(0, 6);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 4);
                }

                if (i < pendingReleaseGroups.Count - 1)
                {
                    ImGuiHelpers.ScaledDummy(0, 4);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(0, 4);
                }
            }
        }
        ImGui.EndChild();

        ImGuiHelpers.ScaledDummy(0, 6);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0, 4);
        UiSharedService.ColorTextWrapped("After confirmation, this window will stay hidden untill new Settings have been added.", ImGuiColors.DalamudGrey);
        ImGuiHelpers.ScaledDummy(0, 4);
        if (ImGui.Button("I reviewed everything, confirm", new Vector2(-1, 0)))
        {
            UpdateOptionPanel.MarkCurrentTagAsSeen(_configService, _apiController.SupporterFeaturesEnabled);
            IsOpen = false;
        }
    }

    private bool IsReleaseChangelogOpen()
    {
        var isOpen = false;
        Mediator.Publish(new QueryWindowOpenStateMessage(typeof(ReleaseChangelogUi), open => isOpen = open));
        return isOpen;
    }
}
