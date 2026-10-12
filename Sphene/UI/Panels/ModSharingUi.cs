using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using System.Collections.Generic;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public sealed class ModSharingUi : WindowMediatorSubscriberBase
{
    /// <summary>Flat sub-pages of the Mod Sharing section — shared by the standalone window rail and the Hub-embedded rail.</summary>
    public enum ModSharingPage
    {
        Send,
        Receive,
        Activity
    }

    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private readonly PenumbraSendModUi _penumbraSendModUi;
    private readonly PenumbraReceiveModUi _penumbraReceiveModUi;
    private readonly ModPackageHistoryUi _modPackageHistoryUi;

    private ModSharingPage _standalonePage = ModSharingPage.Send;
    private ModSharingPage _embeddedPage = ModSharingPage.Send;
    private ModSharingPage? _pendingStandalonePage;
    private bool _embeddedScope;

    private ModSharingPage _activePage
    {
        get => _embeddedScope ? _embeddedPage : _standalonePage;
        set
        {
            if (_embeddedScope)
            {
                _embeddedPage = value;
            }
            else
            {
                _standalonePage = value;
            }
        }
    }

    public ModSharingUi(
        ILogger<ModSharingUi> logger,
        SpheneMediator mediator,
        ApiController apiController,
        SpheneConfigService configService,
        PenumbraSendModUi penumbraSendModUi,
        PenumbraReceiveModUi penumbraReceiveModUi,
        ModPackageHistoryUi modPackageHistoryUi,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Mod Sharing###SpheneModSharing", performanceCollectorService)
    {
        _apiController = apiController;
        _configService = configService;
        _penumbraSendModUi = penumbraSendModUi;
        _penumbraReceiveModUi = penumbraReceiveModUi;
        _modPackageHistoryUi = modPackageHistoryUi;

        Size = new Vector2(1050f, 750f);
        SizeCondition = ImGuiCond.FirstUseEver;

        Mediator.Subscribe<OpenModSharingWindow>(this, msg =>
        {
            _pendingStandalonePage = MapTab(msg.Tab);
            IsOpen = true;
        });

        Mediator.Subscribe<OpenSendPenumbraModWindow>(this, _ =>
        {
            _pendingStandalonePage = ModSharingPage.Send;
            IsOpen = true;
        });

        Mediator.Subscribe<OpenPenumbraReceiveModWindow>(this, _ =>
        {
            _pendingStandalonePage = ModSharingPage.Receive;
            IsOpen = true;
        });

        IsOpen = false;
    }

    private static ModSharingPage MapTab(ModSharingTab tab) => tab switch
    {
        ModSharingTab.Send => ModSharingPage.Send,
        ModSharingTab.Receive => ModSharingPage.Receive,
        _ => ModSharingPage.Activity
    };

    /// <summary>Raised when the active mod sharing page changes through user navigation — args are (previous, next, embedded). The hub folds embedded-surface changes into its own breadcrumb/history.</summary>
    internal event Action<ModSharingPage, ModSharingPage, bool>? SubPageChanged;

    /// <summary>The page the Hub-embedded mod sharing shows — read by the hub for its crumb. The standalone window has its own page state.</summary>
    internal ModSharingPage ActiveSubPageValue => _embeddedPage;

    /// <summary>Label of the embedded mod sharing page for the hub breadcrumb.</summary>
    internal string ActiveCrumbLabel => PageLabel(_embeddedPage);

    /// <summary>Applies a page to the Hub-embedded mod sharing without recording it — used for hub-driven restores.</summary>
    internal void ApplySubPageQuiet(ModSharingPage page)
    {
        _embeddedPage = page;
    }

    private void SetPage(ModSharingPage page)
    {
        if (page == _activePage)
        {
            return;
        }
        var previous = _activePage;
        _activePage = page;
        SubPageChanged?.Invoke(previous, page, _embeddedScope);
    }

    private static string PageLabel(ModSharingPage page) => page switch
    {
        ModSharingPage.Send => "Send Mods",
        ModSharingPage.Receive => "Receive Mods",
        ModSharingPage.Activity => "Activity",
        _ => "Send Mods"
    };

    private void DrawNavItems(
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories, float pinnedCenterY)
    {
        var accentSharing = SpheneUI.Palette.EtherealPurple;
        var accentHistory = SpheneUI.Palette.NetworkActive;

        void SidebarButton(string label, FontAwesomeIcon icon, ModSharingPage page, Vector4 accent)
        {
            if (SpheneUI.Layout.NavRailSegmentItem(label, icon, _activePage == page, accent))
            {
                SetPage(page);
            }
        }

        SpheneUI.Layout.NavRailCategoryLabel("Sharing", accentSharing, pinnedCategories, pinnedCenterY);
        SidebarButton("Send Mods", FontAwesomeIcon.PaperPlane, ModSharingPage.Send, accentSharing);
        SidebarButton("Receive Mods", FontAwesomeIcon.Download, ModSharingPage.Receive, accentSharing);
        SpheneUI.Layout.NavRailDivider(accentSharing, accentHistory);

        SpheneUI.Layout.NavRailCategoryLabel("History", accentHistory, pinnedCategories, pinnedCenterY);
        SidebarButton("Activity", FontAwesomeIcon.History, ModSharingPage.Activity, accentHistory);
    }

    /// <summary>
    /// The mod sharing nav rendered inside the Sphene Hub rail — same items
    /// as the standalone rail plus a pop-out shortcut so the windowed section
    /// stays one click away.
    /// </summary>
    internal void DrawEmbeddedRail(
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories = null,
        float pinnedCenterY = 0f)
    {
        var previousScope = _embeddedScope;
        _embeddedScope = true;
        try
        {
            DrawNavItems(pinnedCategories, pinnedCenterY);
            var accent = SpheneUI.Palette.EtherealPurple;
            SpheneUI.Layout.NavRailDivider(accent, accent);
            if (SpheneUI.Layout.NavRailSegmentItem("Pop Out", FontAwesomeIcon.ExternalLinkAlt, false, accent))
            {
                Mediator.Publish(new UiToggleMessage(typeof(ModSharingUi)));
            }
            SpheneUI.AttachTooltip("Open Mod Sharing in a separate window");
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    /// <summary>
    /// The active mod sharing page rendered inside an already-scrollable
    /// container (the Sphene Hub nav content child) — no own scroll area.
    /// </summary>
    internal void DrawEmbeddedContent()
    {
        var previousScope = _embeddedScope;
        _embeddedScope = true;
        try
        {
            DrawPageContent();
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    private void DrawPageContent()
    {
        switch (_activePage)
        {
            case ModSharingPage.Send:
                _penumbraSendModUi.DrawEmbedded();
                break;
            case ModSharingPage.Receive:
                DrawReceivePage();
                break;
            case ModSharingPage.Activity:
                _modPackageHistoryUi.DrawActivityPage();
                break;
        }
    }

    private void DrawReceivePage()
    {
        var accent = SpheneUI.Palette.NetworkActive;
        SpheneUI.Layout.SectionCard(() =>
        {
            var allowPenumbraMods = _configService.Current.AllowReceivingPenumbraMods;
            if (SpheneUI.Controls.Toggle("Allow receiving Penumbra mod packages", ref allowPenumbraMods))
            {
                _configService.Current.AllowReceivingPenumbraMods = allowPenumbraMods;
                _configService.Save();
                _ = _apiController.UserUpdatePenumbraReceivePreference(allowPenumbraMods);
            }
            SpheneUI.Controls.HelpText("When disabled, incoming Penumbra mod packages are ignored and no install popups are shown.");
        }, title: "Incoming Packages", accentColor: accent);

        SpheneUI.Layout.VerticalSpace();

        if (!_configService.Current.AllowReceivingPenumbraMods)
        {
            SpheneUI.Layout.Callout("Receiving mod packages is currently disabled — enable it above to review pending packages.",
                FontAwesomeIcon.Inbox, ImGuiColors.DalamudGrey);
            SpheneUI.Layout.VerticalSpace();
        }
        using (ImRaii.Disabled(!_configService.Current.AllowReceivingPenumbraMods))
        {
            _penumbraReceiveModUi.DrawEmbedded();
        }
    }

    protected override void DrawInternal()
    {
        var requested = _pendingStandalonePage;
        _pendingStandalonePage = null;
        if (requested.HasValue)
        {
            _standalonePage = requested.Value;
        }

        var railWidth = SpheneUI.Layout.NavRailSegmentedWidth([
            (FontAwesomeIcon.PaperPlane, "Send Mods"),
            (FontAwesomeIcon.Download, "Receive Mods"),
            (FontAwesomeIcon.History, "Activity")
        ]);
        SpheneUI.Layout.BeginNavRail("mod-sharing", railWidth);
        // Same top offset as the hub rail so the first button sits at the
        // same height in both surfaces.
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        DrawNavItems(null, 0f);
        SpheneUI.Layout.EndNavRail();
        if (SpheneUI.Layout.BeginNavContent("mod-sharing"))
        {
            DrawPageContent();
        }
        SpheneUI.Layout.EndNavContent();
    }
}
