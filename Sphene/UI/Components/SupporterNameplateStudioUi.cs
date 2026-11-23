using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.UI.Design;
using Sphene.WebAPI;
using System.Linq;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Nameplate Studio · the interactive supporter customization surface.
/// A live stage shows the player's real nameplate (captured by
/// <see cref="SupporterNameplateService.LocalPlate"/>) with every configured
/// part and the equipped nameplate effect animating; galleries below present
/// every effect, icon and mark symbol as live animated cards. Clicking a
/// card stages the pick on the stage without saving; the Apply button next
/// to the stage equips everything staged. Non-supporters can stage previews
/// but not equip.
/// </summary>
public sealed class SupporterNameplateStudioUi
{
    private const float RowLabelColumn = 110f;
    private const int UiForegroundKeyInlineMax = 77;
    private const int UiForegroundKeyExtendedStart = 500;
    private const int UiForegroundKeyExtendedMax = 511;
    private const int UiForegroundPaletteMaxIndex = UiForegroundKeyInlineMax + (UiForegroundKeyExtendedMax - UiForegroundKeyExtendedStart + 1);
    private const float HueBucketDegrees = 24f;
    private static readonly string[] SymbolPresets = ["None", "♦", "★", "☆", "♥", "♡", "◆", "◇"];
    private static readonly string[] TeaserLabels = ["VIP", "Star", "OG", "Ace", "Pro", "Dono"];
    private string? _teaserSymbol;
    private string? _teaserLabel;
    private SupporterSymbolPosition? _teaserPosition;

    private void EnsureTeaserRoll()
    {
        if (_teaserSymbol != null) return;
        var rng = new Random();
        _teaserSymbol = SymbolPresets[1 + rng.Next(SymbolPresets.Length - 1)];
        _teaserLabel = TeaserLabels[rng.Next(TeaserLabels.Length)];
        _teaserPosition = (SupporterSymbolPosition)rng.Next(3);
    }
    private static readonly string[] PositionItems = ["Left", "Right", "Both"];
    private static readonly string[] LabelOrderItems = ["Symbol then Text", "Text then Symbol"];

    private readonly SpheneConfigService _configService;
    private readonly SupporterIconService _iconService;
    private readonly SupporterNameplateService _nameplateService;
    private readonly UiSharedService _uiShared;
    private readonly ApiController _apiController;
    private readonly SpheneMediator _mediator;
    private readonly SpheneProfileManager _profileManager;

    /// <summary>Effect staged by a card click · the stage previews it without
    /// touching config until Apply. <see cref="string.Empty"/> is the None
    /// cell, null means nothing staged.</summary>
    private string? _pendingFxId;
    /// <summary>Icon tile staged by a click: null = nothing staged,
    /// otherwise the (enabled, iconId) pick · <c>false</c> is the Off tile.</summary>
    private (bool Enabled, string Id)? _pendingIcon;
    /// <summary>Mark symbol staged by a chip click or the custom input  · 
    /// <see cref="string.Empty"/> is the None chip, null means nothing
    /// staged.</summary>
    private string? _pendingSymbol;
    /// <summary>Staged customization settings (colors, label, position, …) ·
    /// created lazily on the first change so untouched fields never dirty the
    /// draft. Commits together with the card picks in <see cref="ApplyPending"/>.</summary>
    private StudioDraft? _draft;
    private double _teaserStart = -1;
    /// <summary>Mouse wheel that landed on the pinned stage · forwarded into
    /// the catalog's scroll child since a wheel over parent content never
    /// reaches it (the community view's nudge pattern).</summary>
    private float _scrollNudge;
    private readonly UnsavedChangesToast _unsavedToast = new();
    private readonly LodestoneAvatarService _lodestoneAvatarService;
    private readonly ILogger<SupporterNameplateStudioUi> _logger;

    // "What you unlock" window - shared between the first-run setup and the
    // supporter settings page · opens via OpenUnlockWindow, drawn via
    // DrawUnlockWindow inside any hosting window's draw.
    private bool _showUnlockWindow;
    private RowBackdropPresets.Preset _unlockPreset = RowBackdropPresets.All[0];
    private SpheneProfileTheme _unlockTheme = SpheneProfileTheme.Default;
    private double _unlockStart;
    private int _unlockCycle = -1;

    /// <summary>Opens the supporter perks preview window and starts its
    /// 4s style carousel from cycle zero.</summary>
    public void OpenUnlockWindow()
    {
        _showUnlockWindow = true;
        _unlockStart = ImGui.GetTime();
        _unlockCycle = -1;
    }

    /// <summary>Draws the perks window when open - call inside the owning
    /// window's Draw so it works from setup and settings alike.</summary>
    public void DrawUnlockWindow()
    {
        if (!_showUnlockWindow) return;
        var open = _showUnlockWindow;
        ImGui.SetNextWindowSize(new Vector2(560f * SpheneUI.Scale, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(460f * SpheneUI.Scale, 0),
            new Vector2(640f * SpheneUI.Scale, float.MaxValue));
        if (ImGui.Begin("##sphene-supporter-perks", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse))
        {
            SpheneUI.Chrome.TitleBar("sphene-supporter-perks", "What you unlock",
                FontAwesomeIcon.Gift, SpheneUI.Palette.SpheneGold,
                rightButtons:
                [
                    new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.Times, "Close", () => open = false),
                ]);
            SpheneUI.Layout.SectionCard(
                () => SupporterPerks.DrawList(),
                title: "Supporter perks",
                icon: FontAwesomeIcon.Gift,
                accentColor: SpheneUI.Palette.SpheneGold,
                description: "Supporter status is synced automatically once your Ko-fi donation is linked to your Discord account.");
            SpheneUI.Layout.VerticalSpace();
            var teaserIndent = SpheneUI.Spacing.X12.Scaled;
            ImGui.Indent(teaserIndent);
            DrawTeaserCard(null);
            ImGui.Unindent(teaserIndent);

            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Text.SectionHeader("In a friends list",
                "Your row gets the same backdrop and hover treatment every supporter row does.",
                icon: FontAwesomeIcon.Users, accent: SpheneUI.Palette.SpheneGold);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            var cycle = (int)((ImGui.GetTime() - _unlockStart) / 4.0);
            if (cycle != _unlockCycle)
            {
                _unlockCycle = cycle;
                RollUnlockStyle();
            }
            var rowDrawList = ImGui.GetWindowDrawList();
            var rowW = ImGui.GetContentRegionAvail().X;
            var rowH = 46f * SpheneUI.Scale;
            var rowPos = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##unlockrowprev", new Vector2(rowW, rowH));
            var rowHovered = ImGui.IsItemHovered();
            var rowT = SpheneUI.Fx.HoverT(ImGui.GetID("##unlockrowprev"), rowHovered);
            SpheneUI.Profile.FriendRow(rowDrawList, rowPos, rowW, rowH,
                new SpheneUI.Profile.FriendRowContent(
                    _lodestoneAvatarService.PrefetchedPortrait is { } portrait
                        ? SpheneUI.Media.Texture("lodestone:preview", portrait)
                        : SpheneBrandIcons.SpheneLogo(),
                    "You", "● Online",
                    SpheneUI.Palette.NetworkActive, null, false),
                SpheneUI.Palette.SpheneGold, rowHovered, rowT,
                _unlockTheme, UnlockRowImage(), 8f * SpheneUI.Scale);
            ImGui.SetCursorScreenPos(new Vector2(rowPos.X, rowPos.Y + rowH));
            SpheneUI.Text.Muted("Hover the row · supporter rows can carry animated backdrops.");

            // trial hint - the actual start happens on the first studio apply
            if (_apiController.IsConnected && !_apiController.IsSupporter)
            {
                if (!_apiController.SupporterTrialUsed)
                {
                    SpheneUI.Layout.VerticalSpace();
                    SpheneUI.Layout.Callout(
                        "Free 1-day trial available - it starts the moment you apply your first perks in the nameplate studio.",
                        FontAwesomeIcon.Gift, SpheneUI.Palette.SpheneGold,
                        title: "Try before you support");
                }
                else if (_apiController.SupporterTrialActive
                    && _apiController.SupporterTrialEndsUtc.HasValue)
                {
                    var left = Math.Max(1, (int)Math.Ceiling(
                        (_apiController.SupporterTrialEndsUtc.Value - DateTime.UtcNow).TotalHours));
                    SpheneUI.Layout.VerticalSpace();
                    SpheneUI.Layout.Callout(
                        $"Your free supporter trial is active · {left}h left.",
                        FontAwesomeIcon.HourglassHalf, SpheneUI.Palette.SpheneGold,
                        title: "Trial running");
                }
                else
                {
                    SpheneUI.Layout.VerticalSpace();
                    SpheneUI.Layout.Callout(
                        "Your free trial has been used - supporter perks need a Ko-fi donation now.",
                        FontAwesomeIcon.Ban, ImGuiColors.DalamudGrey3,
                        title: "Trial expired");
                }
            }

            SpheneUI.Layout.VerticalSpace();
            if (SpheneBrandIcons.IconTextButton(SpheneBrandIcons.Brand.Kofi,
                    "Support on Ko-fi", "unlock-kofi", width: -1f,
                    tooltip: "Open the Sphene Ko-fi page", height: 34f * SpheneUI.Scale))
            {
                Util.OpenLink("https://ko-fi.com/sphenedev");
            }
            ImGui.End();
        }
        _showUnlockWindow = open;
    }

    /// <summary>Curated "pretty" color pool for free-form backdrop gradients -
    /// saturated jewel tones that stay readable behind a row.</summary>
    private static readonly Vector4[] PrettyColors =
    [
        new(0.94f, 0.40f, 0.55f, 1f), new(0.95f, 0.55f, 0.20f, 1f),
        new(0.30f, 0.65f, 0.95f, 1f), new(0.45f, 0.35f, 0.90f, 1f),
        new(0.10f, 0.60f, 0.65f, 1f), new(0.15f, 0.65f, 0.40f, 1f),
        new(0.90f, 0.70f, 0.25f, 1f), new(0.75f, 0.25f, 0.65f, 1f),
        new(0.20f, 0.25f, 0.60f, 1f), new(0.85f, 0.35f, 0.30f, 1f),
    ];

    private static readonly Vector4[] DarkBases =
    [
        new(0.03f, 0.05f, 0.16f, 1f), new(0.10f, 0.05f, 0.18f, 1f),
        new(0.04f, 0.10f, 0.12f, 1f), new(0.12f, 0.06f, 0.08f, 1f),
    ];

    /// <summary>Half the time a shipped preset, otherwise a fully rolled
    /// gradient: pretty head color melting into a dark base.</summary>
    private static RowBackdropPresets.Preset RollBackdrop(Random rng)
    {
        if (rng.Next(2) == 0)
        {
            return RowBackdropPresets.All[rng.Next(RowBackdropPresets.All.Length)];
        }
        var from = PrettyColors[rng.Next(PrettyColors.Length)];
        var to = DarkBases[rng.Next(DarkBases.Length)];
        if (rng.Next(3) == 0)
        {
            (from, to) = (to, from);
        }
        var via = (from + to) * 0.5f;
        via.W = 1f;
        return new RowBackdropPresets.Preset($"custom-{rng.Next():X}", "Custom", from, via, to);
    }

    private static ProfileRowFx RollFx(Random rng, ProfileRowFx[] pool)
    {
        var fx = pool[rng.Next(pool.Length)];
        for (var extra = rng.Next(3); extra > 0; extra--)
        {
            fx |= pool[rng.Next(pool.Length)];
        }
        return fx;
    }

    /// <summary>Re-rolls backdrop + fx each 4s cycle - same carousel rhythm as
    /// the nameplate teaser above it.</summary>
    private void RollUnlockStyle()
    {
        var rng = new Random();
        _unlockPreset = RollBackdrop(rng);
        ProfileRowFx[] hoverPool = [ProfileRowFx.Glow, ProfileRowFx.Sheen,
            ProfileRowFx.Sweep, ProfileRowFx.Lift, ProfileRowFx.EdgeBar,
            ProfileRowFx.Tint];
        ProfileRowFx[] idlePool = [ProfileRowFx.Sparkle, ProfileRowFx.Pulse,
            ProfileRowFx.Sheen, ProfileRowFx.Sweep];
        _unlockTheme = new SpheneProfileTheme(null, null, null,
            RollFx(rng, hoverPool), RollFx(rng, idlePool));
    }

    /// <summary>The rolled backdrop as a single-frame AnimatedImage - the
    /// direct texture path is reliable for PNGs where the async animated
    /// decode can silently stay null.</summary>
    private SpheneUI.Media.AnimatedImage? UnlockRowImage()
    {
        var wrap = SpheneUI.Media.Texture($"rowpreset-img:{_unlockPreset.Id}",
            RowBackdropPresets.Bytes(_unlockPreset));
        return wrap == null ? null : new SpheneUI.Media.AnimatedImage
        {
            Atlas = wrap,
            FrameCount = 1,
            FrameWidth = 1024,
            FrameHeight = 256,
            AtlasColumns = 1,
            AtlasRows = 1,
            Durations = new float[] { 0.1f },
            TotalDuration = 0.1f,
        };
    }

    public SupporterNameplateStudioUi(ILogger<SupporterNameplateStudioUi> logger,
        SpheneConfigService configService,
        SupporterIconService iconService, SupporterNameplateService nameplateService,
        UiSharedService uiShared, ApiController apiController,
        LodestoneAvatarService lodestoneAvatarService,
        SpheneMediator mediator, SpheneProfileManager profileManager)
    {
        _logger = logger;
        _configService = configService;
        _iconService = iconService;
        _nameplateService = nameplateService;
        _uiShared = uiShared;
        _apiController = apiController;
        _lodestoneAvatarService = lodestoneAvatarService;
        _mediator = mediator;
        _profileManager = profileManager;
    }

    /// <summary>Unindents to the shared card edge · settings content sits
    /// indented, surfaces run full width like the other supporter cards.</summary>
    private static void FullWidth(Action draw)
    {
        var indent = SpheneUI.Spacing.X12.Scaled;
        ImGui.Unindent(indent);
        draw();
        ImGui.Indent(indent);
    }

    /// <summary>Height <see cref="DrawPreviewStrip"/> renders for a given
    /// vertical padding · duplicated geometry so the action column can be
    /// sized before the strip is drawn.</summary>
    private float PreviewStripHeight(float padY)
    {
        var plate = _nameplateService.LocalPlate;
        var titleShown = !string.IsNullOrEmpty(plate?.QuotedTitle)
            && (plate?.DisplayTitle ?? false);
        var textH = ImGui.GetTextLineHeight();
        return textH + padY * SpheneUI.Scale * 2f
            + (titleShown ? textH + 3f * SpheneUI.Scale : 0f);
    }

    /// <summary>
    /// Draws a preview strip narrowed by the action column and parks a
    /// strip-height capsule button in the freed space · the strip never needs
    /// the full card width, so the primary action rides beside it. Returns
    /// the button's click.
    /// </summary>
    private bool DrawStripWithSideButton(string? fxId, string? iconId, float padY,
        int plateIndex, string label, FontAwesomeIcon icon,
        Vector4 color, string? tooltip, string? symbolOverride = null,
        bool previewing = false)
    {
        var scale = SpheneUI.Scale;
        var gap = SpheneUI.Metrics.Gap;
        var buttonH = PreviewStripHeight(padY);
        var buttonW = UnsavedChangesToast.CapsuleButtonWidth(buttonH, icon, label);
        var stripMin = ImGui.GetCursorScreenPos();
        var strip = DrawPreviewStrip(fxId, iconId, padY, plateIndex, previewing,
            symbolOverride,
            MathF.Max(60f * scale, SpheneUI.Controls.BoundedAvailX() - buttonW - gap));
        var clicked = UnsavedChangesToast.DrawCapsuleButton(
            new Vector2(stripMin.X + strip.X + gap, stripMin.Y),
            new Vector2(buttonW, strip.Y), label, icon, color, tooltip);
        // The strip's Dummy owns the row · restore the cursor below it.
        ImGui.SetCursorScreenPos(new Vector2(stripMin.X, stripMin.Y + strip.Y));
        return clicked;
    }

    /// <summary>
    /// The supporter's card on the Supporter page: the live strip plus the
    /// entry into the studio.
    /// </summary>
    public void DrawSummaryCard(Action openStudio)
    {
        ClearPending();
        var cfg = _configService.Current;
        // No enclosing surface on purpose: the preview strip splits draw-list
        // channels for its behind/in-front layers and ImGui splits don't nest.
        FullWidth(() =>
        {
            SpheneUI.Text.SectionHeader("Your Nameplate",
                "How your name appears to other Sphene users in game · live.",
                icon: FontAwesomeIcon.IdCard, accent: SpheneUI.Palette.LuminousGold);
            var effect = FxDisplayName(cfg.SupporterNameplateFxId);
            var icon = cfg.SupporterIconEnabled
                ? IconDisplayName(cfg.SupporterIconId)
                : "icon off";
            if (DrawStripWithSideButton(cfg.SupporterNameplateFxId,
                    cfg.SupporterIconEnabled ? cfg.SupporterIconId : null,
                    padY: 14f, plateIndex: 0,
                    "Customize", FontAwesomeIcon.Pen,
                    SpheneUI.Palette.LuminousGold,
                    $"Open the editor · try effects, icons and symbols live on your nameplate. Equipped: {effect} · {icon}."))
            {
                openStudio();
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Rows.Note("Symbol, label, icon and effect travel inside your supporter style · other Sphene users see the same nameplate.");
        });
    }

    /// <summary>
    /// The non-supporter teaser on the Supporter page: a demo strip cycling
    /// through every effect, plus the browse-only studio entry.
    /// </summary>
    public void DrawTeaserCard(Action? openStudio)
    {
        ClearPending();
        if (_teaserStart < 0)
        {
            _teaserStart = ImGui.GetTime();
        }
        var cycle = (int)((ImGui.GetTime() - _teaserStart) / 4.0) % (NameplateFx.All.Length + 1);
        var fx = cycle < NameplateFx.All.Length ? NameplateFx.All[cycle].Id : null;
        FullWidth(() =>
        {
            SpheneUI.Text.SectionHeader("Your Nameplate",
                "Every supporter effect and icon, previewed live on your nameplate.",
                icon: FontAwesomeIcon.IdCard, accent: SpheneUI.Palette.LuminousGold);
            if (openStudio != null)
            {
                if (DrawStripWithSideButton(fx, SupporterIconService.BuiltInHaloId,
                        padY: 14f, plateIndex: 0,
                        "Browse", FontAwesomeIcon.Eye,
                        SpheneUI.Palette.CrystalBlue,
                        "Open the gallery · try every effect and icon live on your nameplate."))
                {
                    openStudio();
                }
            }
            else
            {
                // contexts without a reachable studio (setup wizard) get the
                // plain strip at full width - no dead-end Browse button, and
                // a rolled symbol + label so the plate doesn't render bare
                EnsureTeaserRoll();
                DrawPreviewStrip(fx, SupporterIconService.BuiltInHaloId, padY: 14f,
                    plateIndex: 0, symbolOverride: _teaserSymbol,
                    labelOverride: _teaserLabel,
                    positionOverride: _teaserPosition);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Rows.Note("Animated nameplate effects and icons are a supporter perk · the gallery is open to browse.");
        });
    }

    private byte[] _ownAvatarBytes = [];
    private byte[] _ownRowBytes = [];
    private Vector4 _rowCustomFrom = new(0.05f, 0.45f, 0.55f, 1f);
    private Vector4 _rowCustomTo = new(0.02f, 0.05f, 0.20f, 1f);
    private bool _rowCustomReverse;
    private bool _rowUseAccent;
    private Vector4 _rowAccent = new(0.55f, 0.45f, 0.95f, 1f);
    private ProfileRowFx _rowHoverFx;
    private ProfileRowFx _rowIdleFx;
    private string _rowThemeSeed = string.Empty;
    private string? _rowAppliedTheme;
    /// <summary>Staged row backdrop · null = untouched, empty = cleared,
    /// base64 = a preset, generated fade or uploaded strip.</summary>
    private string? _stagedRowImage;

    /// <summary>
    /// The list-row summary card on the Supporter page: the real friends-list
    /// row renderer bound to the owner's live profile with the action capsule
    /// parked beside it - Customize for supporters, trial/Ko-fi CTA otherwise.
    /// </summary>
    public void DrawListRowCard(Action openStudio)
    {
        FullWidth(() =>
        {
            var supporter = _apiController.IsSupporterEffective;
            SpheneUI.Text.SectionHeader("Your List Row",
                supporter
                    ? "How your entry looks in friends' lists · backdrop, fades and effects."
                    : "Supporter perk · animated backdrops and effects behind your entry.",
                icon: FontAwesomeIcon.Bars, accent: SpheneUI.Palette.LuminousGold);
            var profile = _profileManager.GetSpheneProfile(new UserData(_apiController.UID));

            var scale = SpheneUI.Scale;
            var gap = SpheneUI.Metrics.Gap;
            var rowH = 46f * scale;
            var trialOpen = !_apiController.SupporterTrialUsed;
            var label = supporter ? "Customize" : trialOpen ? "Try free day" : "Become a supporter";
            var icon = supporter ? FontAwesomeIcon.Pen
                : trialOpen ? FontAwesomeIcon.Gift : FontAwesomeIcon.Heart;
            var color = supporter ? SpheneUI.Palette.LuminousGold
                : trialOpen ? SpheneUI.Palette.SpheneGold
                : SpheneBrandIcons.GetBrandColor(SpheneBrandIcons.Brand.Kofi);
            var buttonW = UnsavedChangesToast.CapsuleButtonWidth(rowH, icon, label);
            var min = ImGui.GetCursorScreenPos();
            var rowW = MathF.Max(60f * scale,
                SpheneUI.Controls.BoundedAvailX() - buttonW - gap);
            DrawOwnRowPreview(profile, profile.ProfileTheme.Value,
                profile.RowImageData.Value, "##suprowprev", rowW);
            var clicked = UnsavedChangesToast.DrawCapsuleButton(
                new Vector2(min.X + rowW + gap, min.Y),
                new Vector2(buttonW, rowH), label, icon, color,
                supporter
                    ? "Open the list row studio · effects, fades, presets and uploads."
                    : trialOpen
                        ? "Start your free 1-day supporter trial · one-time, then supporter only."
                        : "Support Sphene on Ko-fi to unlock supporter perks.");
            ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + rowH));
            if (clicked)
            {
                if (supporter) openStudio();
                else if (trialOpen) StartRowTrial();
                else Util.OpenLink("https://ko-fi.com/sphenedev");
            }
            SpheneUI.Text.Muted("Hover the row · this is exactly how your entry renders for viewers.");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Rows.Note(supporter
                ? "Effects, preset fades, custom fades and uploads live in the list row studio."
                : "Row backdrops and effects are a supporter perk · a free 1-day trial unlocks them.");
        });
    }

    /// <summary>
    /// The list-row studio sub-page (Supporter › List Row): the live row
    /// preview stays pinned on top while the setting cards scroll beneath —
    /// every control only stages (theme buffers + <see cref="_stagedRowImage"/>)
    /// and the staged look rides the preview until Apply commits it in a
    /// single profile write. Same contract as the nameplate studio.
    /// </summary>
    public void DrawListRowStudioPage()
    {
        var supporter = _apiController.IsSupporterEffective;
        var accent = SpheneUI.Palette.LuminousGold;
        var profile = _profileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var theme = profile.ProfileTheme.Value;
        EnsureRowThemeSeeded(profile);
        var stagedTheme = RowThemeFromBuffers(theme);
        var rowBytes = StagedRowBytes(profile);
        var pinMin = ImGui.GetCursorScreenPos();

        // the preview stage stays pinned while the setting cards scroll
        // beneath - same contract as the nameplate studio
        FullWidth(() =>
        {
            SpheneUI.Text.SectionHeader("Your List Row",
                supporter
                    ? "Stage effects, fades and a backdrop below · Apply commits them together."
                    : "Supporter perk · the preview is live, styling needs supporter status.",
                icon: FontAwesomeIcon.Bars, accent: accent);
            DrawOwnRowPreview(profile, stagedTheme, rowBytes, "##suprowstudio");
            SpheneUI.Text.Muted("Hover the row · this is the staged look your friends get on Apply.");
            if (!supporter)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                if (!_apiController.SupporterTrialUsed)
                {
                    if (SpheneUI.Controls.PrimaryButton("Try free day", FontAwesomeIcon.Gift))
                    {
                        StartRowTrial();
                    }
                    SpheneUI.AttachTooltip(
                        "Start your free 1-day supporter trial · one-time, then supporter only.");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    SpheneUI.Rows.Note("The free trial unlocks every supporter surface - nameplate and list row alike.");
                }
                else
                {
                    SpheneUI.Rows.Note("Row backdrops are a supporter perk · support Sphene on Ko-fi to unlock them.");
                }
            }
        });
        if (!supporter)
        {
            return;
        }

        DrawStageSeparator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var pinMax = new Vector2(pinMin.X + SpheneUI.Controls.BoundedAvailX(),
            ImGui.GetCursorScreenPos().Y);
        var wheel = ImGui.GetIO().MouseWheel;
        if (MathF.Abs(wheel) > 0.001f && ImGui.IsWindowHovered()
            && ImGui.IsMouseHoveringRect(
                pinMin - new Vector2(SpheneUI.Spacing.X12.Scaled, 0f), pinMax))
        {
            _scrollNudge = wheel;
        }
        var bottomReserve = ImGui.GetStyle().ItemSpacing.Y
            + SpheneUI.Layout.ScrollAreaContentInset * SpheneUI.Scale;
        var catalogTop = ImGui.GetCursorScreenPos().Y;
        var catalogH = SpheneUI.Layout.FillToViewBottomHeight(bottomReserve);
        var viewBottom = catalogTop + catalogH + bottomReserve;
        var catalogInset = SpheneUI.Spacing.X12.Scaled;
        ImGui.SetCursorScreenPos(new Vector2(pinMin.X - catalogInset,
            ImGui.GetCursorScreenPos().Y));
        if (SpheneUI.Layout.BeginScrollArea("##rowstudio-cards",
                new Vector2(pinMax.X - pinMin.X + catalogInset, catalogH),
                windowFlags: ImGuiWindowFlags.NoBackground, contentPadding: Vector2.Zero))
        {
            ImGui.Indent(catalogInset);
            if (MathF.Abs(_scrollNudge) > 0.001f)
            {
                var target = ImGui.GetScrollY()
                    - _scrollNudge * ImGui.GetTextLineHeightWithSpacing() * 4f;
                ImGui.SetScrollY(Math.Clamp(target, 0f, ImGui.GetScrollMaxY()));
            }
            _scrollNudge = 0f;
            FullWidth(() =>
            {
                SpheneUI.Layout.SectionCard(() =>
                {
                    SpheneUI.Profile.ThemeColorRow("Custom accent color",
                        ref _rowUseAccent, ref _rowAccent);
                    SpheneUI.Text.Muted("Glow + Pulse are always on - add more below.");
                    SpheneUI.Profile.FxChips("On hover", ref _rowHoverFx,
                        ProfileRowFx.HoverMask & ~SpheneProfileTheme.RequiredRowFx, accent);
                    SpheneUI.Profile.FxChips("While idle", ref _rowIdleFx,
                        ProfileRowFx.IdleMask & ~SpheneProfileTheme.RequiredRowFx, accent);
                }, "List row effects", accent, icon: FontAwesomeIcon.Magic,
                    description: "Stack as many as you like · they show on your entry in your friends' lists.");
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

                // shipped backdrops - a click only stages, Apply commits
                SpheneUI.Layout.SectionCard(() =>
                {
                    SpheneUI.Text.Muted("One click stages the backdrop · Apply below commits it.");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    var presetSize = new Vector2(96f * SpheneUI.Scale, 30f * SpheneUI.Scale);
                    var presetGap = 8f * SpheneUI.Scale;
                    var presetStart = ImGui.GetCursorScreenPos();
                    var presetRight = presetStart.X + SpheneUI.Controls.BoundedAvailX();
                    var presetX = presetStart.X;
                    var presetY = presetStart.Y;
                    for (var i = 0; i < RowBackdropPresets.All.Length; i++)
                    {
                        var preset = RowBackdropPresets.All[i];
                        if (presetX + presetSize.X > presetRight && presetX > presetStart.X)
                        {
                            presetX = presetStart.X;
                            presetY += presetSize.Y + presetGap;
                        }
                        var pos = new Vector2(presetX, presetY);
                        RowBackdropPresets.DrawSwatch(pos, presetSize, preset, 6f * SpheneUI.Scale);
                        ImGui.SetCursorScreenPos(pos);
                        if (ImGui.InvisibleButton($"##rowpreset-{preset.Id}", presetSize))
                        {
                            _stagedRowImage = RowBackdropPresets.Base64(preset);
                        }
                        SpheneUI.AttachTooltip(preset.Label);
                        presetX += presetSize.X + presetGap;
                    }
                    ImGui.SetCursorScreenPos(new Vector2(presetStart.X, presetY + presetSize.Y));
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

                    // custom gradient: two colors + fade direction · inline
                    // swatches, not ColorEdit rows - the settings-row dock
                    // would pin both swatches to the card's right edge
                    SpheneUI.Text.Muted("Or build your own fade:");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    SpheneUI.Text.Muted("From");
                    ImGui.SameLine();
                    SpheneUI.Controls.ColorPickerSwatch("rowfade-from", ref _rowCustomFrom);
                    ImGui.SameLine();
                    SpheneUI.Text.Muted("→");
                    ImGui.SameLine();
                    SpheneUI.Text.Muted("To");
                    ImGui.SameLine();
                    SpheneUI.Controls.ColorPickerSwatch("rowfade-to", ref _rowCustomTo);
                    ImGui.SameLine();
                    if (SpheneUI.Icons.TextButton(
                            _rowCustomReverse ? FontAwesomeIcon.ArrowLeft : FontAwesomeIcon.ArrowRight,
                            _rowCustomReverse ? "Right → Left" : "Left → Right"))
                    {
                        _rowCustomReverse = !_rowCustomReverse;
                    }
                    SpheneUI.AttachTooltip("Left color fades into right color · direction toggle flips it");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    if (SpheneUI.Controls.PrimaryButton("Stage fade", FontAwesomeIcon.Check))
                    {
                        var from = _rowCustomReverse ? _rowCustomTo : _rowCustomFrom;
                        var to = _rowCustomReverse ? _rowCustomFrom : _rowCustomTo;
                        var via = (from + to) * 0.5f;
                        via.W = 1f;
                        _stagedRowImage = Convert.ToBase64String(
                            RowBackdropPresets.Render(new RowBackdropPresets.Preset(
                                "custom", "Custom", from, via, to)));
                    }
                    SpheneUI.AttachTooltip("Generates the fade strip and stages it as your row background");
                }, "Row background", accent, icon: FontAwesomeIcon.Image,
                    description: "Generated strips and gradients shown behind your entry.");

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                SpheneUI.Layout.SectionCard(() =>
                {
                    SpheneUI.Text.Muted(rowBytes.Length > 0
                        ? "Backdrop staged · shown behind your entry with a right-side fade."
                        : "No backdrop · upload a PNG or GIF cropped to the row strip.");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    if (SpheneUI.Controls.PrimaryButton("Upload row image", FontAwesomeIcon.FileUpload))
                    {
                        _mediator.Publish(new OpenPhotoStudioMessage(null,
                            PhotoStudioTarget.ProfileRowImage,
                            prepared => _stagedRowImage = prepared.Base64));
                    }
                    SpheneUI.AttachTooltip(
                        "PNG or GIF · you pick the shown strip in the cropper; animated GIFs play for viewers");
                    if (rowBytes.Length > 0)
                    {
                        ImGui.SameLine();
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear"))
                        {
                            _stagedRowImage = string.Empty;
                        }
                    }
                }, "Custom image", accent, icon: FontAwesomeIcon.FileUpload,
                    description: "Your own backdrop · staged here, applied below.");
            });
            ImGui.Unindent(catalogInset);
        }
        _scrollNudge = 0f;
        SpheneUI.Layout.EndScrollArea();

        var dirty = stagedTheme != theme || _stagedRowImage != null;
        var resetBtn = new UnsavedChangesToast.Button("Reset", FontAwesomeIcon.Undo,
            SpheneUI.Colors.Accent, true,
            "Drop the staged theme and backdrop · back to the live look.",
            () => ResetRowStage(profile));
        var toastMsg = "You have unsaved changes";
        if (!_apiController.IsSupporter && _apiController.SupporterTrialEndsUtc.HasValue)
        {
            var hoursLeft = Math.Max(1, (int)Math.Ceiling(
                (_apiController.SupporterTrialEndsUtc.Value - DateTime.UtcNow).TotalHours));
            toastMsg = $"Free trial · {hoursLeft}h left";
        }
        _unsavedToast.Draw(dirty, toastMsg,
            (pinMin.X + pinMax.X) * 0.5f, viewBottom, resetBtn,
            new UnsavedChangesToast.Button("Apply", FontAwesomeIcon.Check,
                SpheneUI.Palette.LuminousGold, true,
                "Commit the staged effects, backdrop and upload to your profile.",
                () => ApplyRowStage(profile), Solid: true));
    }

    /// <summary>Backdrop bytes under the staged draft - the preview renders
    /// the staged pick before it is committed.</summary>
    private byte[] StagedRowBytes(SpheneProfileData profile)
        => _stagedRowImage == null ? profile.RowImageData.Value
            : _stagedRowImage.Length == 0 ? []
            : Convert.FromBase64String(_stagedRowImage);

    /// <summary>Seeds the effect buffers from the stored theme once - and
    /// again whenever the stored theme changes to something this surface
    /// neither imported nor wrote itself.</summary>
    private void EnsureRowThemeSeeded(SpheneProfileData profile)
    {
        if (!string.Equals(profile.Theme, _rowThemeSeed, StringComparison.Ordinal)
            && !string.Equals(profile.Theme, _rowAppliedTheme, StringComparison.Ordinal))
        {
            SeedRowTheme(profile);
        }
    }

    private void SeedRowTheme(SpheneProfileData profile)
    {
        var theme = profile.ProfileTheme.Value;
        _rowUseAccent = theme.Accent.HasValue;
        if (theme.Accent.HasValue)
        {
            _rowAccent = SpheneProfileTheme.ToVector4(theme.Accent.Value);
        }
        _rowHoverFx = theme.RowHover;
        _rowIdleFx = theme.RowIdle;
        _rowThemeSeed = profile.Theme;
        _rowAppliedTheme = null;
    }

    /// <summary>Drops the staged backdrop and every buffered theme field -
    /// the preview snaps back to the stored look.</summary>
    private void ResetRowStage(SpheneProfileData profile)
    {
        _stagedRowImage = null;
        SeedRowTheme(profile);
    }

    /// <summary>The stored theme with the buffered row fields swapped in -
    /// every other field (banner, card keys) rides through untouched.</summary>
    private SpheneProfileTheme RowThemeFromBuffers(SpheneProfileTheme theme) => theme with
    {
        Accent = _rowUseAccent ? SpheneProfileTheme.FromVector4(_rowAccent) : null,
        RowHover = _rowHoverFx,
        RowIdle = _rowIdleFx,
    };

    /// <summary>Commits the staged theme fields and the staged backdrop in a
    /// single profile write - null fields stay untouched, an empty string
    /// clears the image.</summary>
    private void ApplyRowStage(SpheneProfileData profile)
    {
        var theme = profile.ProfileTheme.Value;
        var built = RowThemeFromBuffers(theme);
        var serialized = built != theme ? built.Serialize() : null;
        if (serialized == null && _stagedRowImage == null)
        {
            return;
        }
        _rowAppliedTheme = serialized;
        _ = _apiController.UserSetProfile(new UserProfileDto(
            new UserData(_apiController.UID), Disabled: false,
            IsNSFW: null, ProfilePictureBase64: null, Description: null,
            Theme: serialized, RowImageBase64: _stagedRowImage));
        _stagedRowImage = null;
    }

    /// <summary>Activates the one-time 1-day supporter trial server-side ·
    /// once it lands, <see cref="ApiController.IsSupporterEffective"/> flips
    /// and this whole surface unlocks on the next frame.</summary>
    private void StartRowTrial()
        => _ = Task.Run(async () =>
        {
            try
            {
                await _apiController.ActivateSupporterTrialAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("[Studio] row trial activation failed: {err}", ex.Message);
            }
        });

    private void DrawOwnRowPreview(SpheneProfileData profile, SpheneProfileTheme theme,
        byte[] rowBytes, string id, float? width = null)
    {
        var rowAnim = rowBytes.Length > 0 ? OwnRowImage(rowBytes) : null;
        var scale = SpheneUI.Scale;
        var rowW = width ?? SpheneUI.Controls.BoundedAvailX();
        var rowH = 46f * scale;
        var rowPos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(rowW, rowH));
        var rowHovered = ImGui.IsItemHovered();
        var rowT = SpheneUI.Fx.HoverT(ImGui.GetID(id), rowHovered);
        var online = _apiController.IsConnected;
        SpheneUI.Profile.FriendRow(ImGui.GetWindowDrawList(), rowPos, rowW, rowH,
            new SpheneUI.Profile.FriendRowContent(
                OwnAvatar(profile),
                _apiController.OwnDisplayName ?? "You",
                online ? "● Online" : "● Offline",
                online ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted,
                profile.StatusMessage, profile.IsNSFW),
            theme.AccentColor(SpheneUI.Palette.LuminousGold), rowHovered, rowT,
            theme, rowAnim, 8f * scale);
        ImGui.SetCursorScreenPos(new Vector2(rowPos.X, rowPos.Y + rowH));
    }

    /// <summary>Own avatar for the preview · profile bytes first, Lodestone
    /// portrait or the logo as fallback, evicted whenever the bytes change.</summary>
    private IDalamudTextureWrap? OwnAvatar(SpheneProfileData profile)
    {
        var data = profile.ImageData.Value;
        var key = $"supporterownavatar:{_apiController.UID}";
        if (!data.SequenceEqual(_ownAvatarBytes))
        {
            _ownAvatarBytes = data;
            SpheneUI.Media.Evict(key);
        }
        var wrap = data.Length > 0 ? SpheneUI.Media.Texture(key, data) : null;
        return wrap ?? (_lodestoneAvatarService.PrefetchedPortrait is { } portrait
            ? SpheneUI.Media.Texture("lodestone:preview", portrait)
            : SpheneBrandIcons.SpheneLogo());
    }

    /// <summary>The owner's row backdrop as a live AnimatedImage - the async
    /// decoder keeps GIF frames, so this is the same path the style preview
    /// uses instead of the single-frame preset trick.</summary>
    private SpheneUI.Media.AnimatedImage? OwnRowImage(byte[] data)
    {
        var key = $"supporterownrow:{_apiController.UID}";
        if (!data.SequenceEqual(_ownRowBytes))
        {
            _ownRowBytes = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Animated(key, data);
    }

    /// <summary>The studio sub-page content (Settings › Supporter › Nameplate Studio).</summary>
    public void DrawStudioPage()
    {
        var cfg = _configService.Current;
        var supporter = _apiController.IsSupporterEffective;
        var pinMin = ImGui.GetCursorScreenPos();

        if (!supporter)
        {
            // slim perk banner - one line instead of callout + button rows:
            // lock glyph + message left, ko-fi capsule right
            FullWidth(() =>
            {
                var scale = SpheneUI.Scale;
                var dl = ImGui.GetWindowDrawList();
                var h = 32f * scale;
                var min = ImGui.GetCursorScreenPos();
                var w = SpheneUI.Controls.BoundedAvailX();
                var max = min + new Vector2(w, h);
                var r = 9f * scale;
                var gold = SpheneUI.Palette.SpheneGold;
                dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.FrameBg, 0.78f)), r);
                dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(gold, 0.35f)), r);
                var lockGlyph = FontAwesomeIcon.Lock.ToIconString();
                var lockW = 0f;
                using (SpheneUI.IconFont.Push())
                {
                    var gs = ImGui.CalcTextSize(lockGlyph);
                    lockW = gs.X;
                    dl.AddText(new Vector2(min.X + 12f * scale,
                            min.Y + (h - gs.Y) * 0.5f),
                        SpheneUI.ColorUtils.ToU32(gold), lockGlyph);
                }
                const string msg = "Supporter perk - browse freely, applying needs supporter status.";
                var ms = ImGui.CalcTextSize(msg);
                dl.AddText(new Vector2(min.X + 12f * scale + lockW + 8f * scale,
                        min.Y + (h - ms.Y) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                        SpheneUI.Colors.TextPrimary, 0.85f)), msg);
                // ko-fi capsule anchored right
                var kofiCol = SpheneBrandIcons.GetBrandColor(SpheneBrandIcons.Brand.Kofi);
                var bw = UnsavedChangesToast.CapsuleButtonWidth(22f * scale,
                    FontAwesomeIcon.Heart, "Ko-fi");
                var bMin = new Vector2(max.X - bw - 5f * scale,
                    min.Y + (h - 22f * scale) * 0.5f);
                var bSize = new Vector2(bw, 22f * scale);
                var bHover = ImGui.IsMouseHoveringRect(bMin, bMin + bSize);
                if (bHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    Util.OpenLink("https://ko-fi.com/sphenedev");
                }
                var bf = SpheneUI.ColorUtils.LerpColor(kofiCol, Vector4.One,
                    bHover ? 0.18f : 0f);
                dl.AddRectFilled(bMin, bMin + bSize, SpheneUI.ColorUtils.ToU32(
                    bf with { W = 0.95f }), bSize.Y * 0.5f);
                var ink = new Vector4(0.10f, 0.07f, 0.02f, 1f);
                var inkU32 = SpheneUI.ColorUtils.ToU32(ink);
                var kofiTex = SpheneBrandIcons.GetTexture(SpheneBrandIcons.Brand.Kofi);
                var iconSide = (bSize.Y - 10f * scale);
                var iconW = iconSide;
                var labelSize = ImGui.CalcTextSize("Ko-fi");
                var gx = bMin.X + (bSize.X - iconW - 5f * scale - labelSize.X) * 0.5f;
                if (kofiTex != null)
                {
                    SpheneUI.Media.ImageInRect(dl, kofiTex,
                        new Vector2(gx, bMin.Y + (bSize.Y - iconSide) * 0.5f),
                        new Vector2(gx + iconSide, bMin.Y + (bSize.Y + iconSide) * 0.5f),
                        ImageFit.Stretch, ink);
                }
                else
                {
                    using (SpheneUI.IconFont.Push())
                    {
                        var hs = ImGui.CalcTextSize(FontAwesomeIcon.Heart.ToIconString());
                        dl.AddText(new Vector2(gx,
                            bMin.Y + (bSize.Y - hs.Y) * 0.5f), inkU32,
                            FontAwesomeIcon.Heart.ToIconString());
                    }
                }
                dl.AddText(new Vector2(gx + iconW + 5f * scale,
                        bMin.Y + (bSize.Y - labelSize.Y) * 0.5f),
                    inkU32, "Ko-fi");
                if (bHover)
                {
                    SpheneUI.AttachTooltipForeground(
                        "Open Sphene's Ko-fi page in your browser.", true);
                }
                ImGui.Dummy(new Vector2(w, h));
            });
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }

        // The stage renders the staged picks (card clicks) over the equipped
        // config · nothing is written until Apply.
        var stageFx = _pendingFxId ?? cfg.SupporterNameplateFxId;
        var stageIconEnabled = _pendingIcon?.Enabled ?? cfg.SupporterIconEnabled;
        var stageIconId = _pendingIcon?.Id ?? cfg.SupporterIconId;
        var stageSymbol = _pendingSymbol ?? cfg.SupporterSymbolChar;
        var previewing = _pendingFxId != null || _pendingIcon != null
            || _pendingSymbol != null || _draft != null;
        DrawStage(stageFx, stageIconEnabled ? stageIconId : null, stageSymbol,
            previewing);

        DrawStageSeparator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // Everything above stays pinned · the catalog fills the remaining
        // view height in its own scroll child, so the outer page never
        // scrolls and the stage is always in view while browsing effects.
        var pinMax = new Vector2(pinMin.X + SpheneUI.Controls.BoundedAvailX(),
            ImGui.GetCursorScreenPos().Y);
        var wheel = ImGui.GetIO().MouseWheel;
        // the FullWidth surfaces hang a content indent left of the cursor  · 
        // wheel over them should scroll the catalog too
        if (MathF.Abs(wheel) > 0.001f && ImGui.IsWindowHovered()
            && ImGui.IsMouseHoveringRect(
                pinMin - new Vector2(SpheneUI.Spacing.X12.Scaled, 0f), pinMax))
        {
            _scrollNudge = wheel;
        }
        var bottomReserve = ImGui.GetStyle().ItemSpacing.Y
            + SpheneUI.Layout.ScrollAreaContentInset * SpheneUI.Scale;
        // Bottom of the visible page · the toast anchors here, floating over
        // the catalog's lower edge like Discord's unsaved-changes bar.
        var catalogTop = ImGui.GetCursorScreenPos().Y;
        var catalogH = SpheneUI.Layout.FillToViewBottomHeight(bottomReserve);
        var viewBottom = catalogTop + catalogH + bottomReserve;
        // The child starts at the unindented card edge (the FullWidth cards
        // reach that far left), so the scroll fades · clipped to the child's
        // own rect · cover the whole scrolled span. Content re-indents by
        // the same X12 so every FullWidth unindent lands back on the edge.
        var catalogInset = SpheneUI.Spacing.X12.Scaled;
        ImGui.SetCursorScreenPos(new Vector2(pinMin.X - catalogInset,
            ImGui.GetCursorScreenPos().Y));
        if (SpheneUI.Layout.BeginScrollArea("##studio-catalog",
                new Vector2(pinMax.X - pinMin.X + catalogInset, catalogH),
                windowFlags: ImGuiWindowFlags.NoBackground, contentPadding: Vector2.Zero))
        {
            ImGui.Indent(catalogInset);
            if (MathF.Abs(_scrollNudge) > 0.001f)
            {
                var target = ImGui.GetScrollY()
                    - _scrollNudge * ImGui.GetTextLineHeightWithSpacing() * 4f;
                ImGui.SetScrollY(Math.Clamp(target, 0f, ImGui.GetScrollMaxY()));
            }
            _scrollNudge = 0f;
            DrawEffectGallery(supporter);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawIconGallery(supporter);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawMarkGroup(supporter);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawLabelGroup(supporter);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            DrawAdvancedSection();
            ImGui.Unindent(catalogInset);
        }
        _scrollNudge = 0f;
        SpheneUI.Layout.EndScrollArea();

        var resetBtn = new UnsavedChangesToast.Button("Reset", FontAwesomeIcon.Undo,
            SpheneUI.Colors.Accent, true,
            "Drop the staged picks · back to the equipped look.",
            ClearPending);
        if (supporter)
        {
            var trialMsg = "You have unsaved changes";
            if (!_apiController.IsSupporter && _apiController.SupporterTrialEndsUtc.HasValue)
            {
                var hoursLeft = Math.Max(1, (int)Math.Ceiling(
                    (_apiController.SupporterTrialEndsUtc.Value - DateTime.UtcNow).TotalHours));
                trialMsg = $"Free trial · {hoursLeft}h left";
            }
            _unsavedToast.Draw(previewing, trialMsg,
                (pinMin.X + pinMax.X) * 0.5f, viewBottom, resetBtn,
                new UnsavedChangesToast.Button("Apply", FontAwesomeIcon.Check,
                    SpheneUI.Palette.LuminousGold, true,
                    "Equip the staged effect, icon, mark and label settings.",
                    ApplyPending, Solid: true));
        }
        else if (!_apiController.SupporterTrialUsed)
        {
            _unsavedToast.Draw(previewing,
                "Supporter perk · preview only",
                (pinMin.X + pinMax.X) * 0.5f, viewBottom, resetBtn,
                new UnsavedChangesToast.Button("Try free day",
                    FontAwesomeIcon.Gift, SpheneUI.Palette.SpheneGold, true,
                    "Start your free 1-day supporter trial · one-time, then supporter only.",
                    StartTrialAndApply, Solid: true));
        }
        else
        {
            _unsavedToast.Draw(previewing,
                "Supporter perk · preview only",
                (pinMin.X + pinMax.X) * 0.5f, viewBottom, resetBtn,
                new UnsavedChangesToast.Button("Become a supporter",
                    FontAwesomeIcon.Heart,
                    SpheneBrandIcons.GetBrandColor(SpheneBrandIcons.Brand.Kofi),
                    true,
                    "Support Sphene on Ko-fi to equip supporter perks.",
                    () => Util.OpenLink("https://ko-fi.com/sphenedev"), Solid: true));
        }
    }

    /// <summary>First apply as a non-supporter: stamps the one-time 24h trial
    /// server-side, then commits the staged picks like a normal apply.</summary>
    private void StartTrialAndApply()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var start = await _apiController.ActivateSupporterTrialAsync().ConfigureAwait(false);
                if (start.HasValue && _apiController.SupporterTrialActive)
                {
                    ApplyPending();
                }
                else
                {
                    _logger.LogInformation("[Studio] trial activation returned no start ({start})", start);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("[Studio] trial activation failed: {err}", ex.Message);
            }
        });
    }

    /// <summary>Gradient hairline - softly separates the pinned stage from the
    /// scrolling catalog (accent fading out to the right).</summary>
    private static void DrawStageSeparator()
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var sepY = ImGui.GetCursorScreenPos().Y + 4f * scale;
        var sepMin = new Vector2(ImGui.GetCursorScreenPos().X, sepY);
        var sepMax = sepMin + new Vector2(SpheneUI.Controls.BoundedAvailX(), 1f * scale);
        var accentU32 = SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Accent, 0.35f));
        var noneU32 = SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Accent, 0f));
        dl.AddRectFilledMultiColor(sepMin, sepMax, accentU32, noneU32,
            noneU32, accentU32);
    }

    /// <summary>Snapshot of every config field the studio's customization
    /// controls touch · reads fall back to the equipped config while a field
    /// was never staged.</summary>
    private sealed class StudioDraft
    {
        public ushort ColorKey;
        public bool SymbolGlowEnabled;
        public ushort SymbolGlowKey;
        public SupporterSymbolPosition SymbolPosition;
        public bool TextEnabled;
        public string LabelText = string.Empty;
        public SupporterLabelOrder LabelOrder;
        public ushort LabelColorKey;
        public bool LabelGlowEnabled;
        public ushort LabelGlowKey;
        public bool IconLabelEnabled;
        public string IconLabelText = string.Empty;
        public uint IconLabelColor;
        public bool IconLabelEdgeEnabled;
        public uint IconLabelEdgeColor;

        public static StudioDraft Snapshot(SpheneConfig cfg) => new()
        {
            ColorKey = cfg.SupporterColorKey,
            SymbolGlowEnabled = cfg.SupporterSymbolGlowEnabled,
            SymbolGlowKey = cfg.SupporterSymbolGlowKey,
            SymbolPosition = cfg.SupporterSymbolPosition,
            TextEnabled = cfg.SupporterTextEnabled,
            LabelText = cfg.SupporterLabelText ?? string.Empty,
            LabelOrder = cfg.SupporterLabelOrder,
            LabelColorKey = cfg.SupporterLabelColorKey,
            LabelGlowEnabled = cfg.SupporterLabelGlowEnabled,
            LabelGlowKey = cfg.SupporterLabelGlowKey,
            IconLabelEnabled = cfg.SupporterIconLabelEnabled,
            IconLabelText = cfg.SupporterIconLabelText ?? string.Empty,
            IconLabelColor = cfg.SupporterIconLabelColor,
            IconLabelEdgeEnabled = cfg.SupporterIconLabelEdgeEnabled,
            IconLabelEdgeColor = cfg.SupporterIconLabelEdgeColor,
        };
    }

    /// <summary>Stages a customization change · snapshots the equipped config
    /// on first use, then mutates the draft. Nothing reaches the config or
    /// disk until <see cref="ApplyPending"/>.</summary>
    private void StageDraft(Action<StudioDraft> mutate)
    {
        _draft ??= StudioDraft.Snapshot(_configService.Current);
        mutate(_draft);
    }

    private void ClearPending()
    {
        _pendingFxId = null;
        _pendingIcon = null;
        _pendingSymbol = null;
        _draft = null;
    }

    /// <summary>Commits every staged pick to the config · the single
    /// "accept" for the click-to-preview flow on the stage.</summary>
    private void ApplyPending()
    {
        var cfg = _configService.Current;
        if (_pendingFxId != null)
        {
            cfg.SupporterNameplateFxId = _pendingFxId;
        }
        if (_pendingIcon is { } icon)
        {
            if (icon.Enabled)
            {
                cfg.SupporterIconId = icon.Id;
            }
            cfg.SupporterIconEnabled = icon.Enabled;
        }
        if (_pendingSymbol != null)
        {
            cfg.SupporterSymbolChar = _pendingSymbol;
        }
        if (_draft is { } d)
        {
            cfg.SupporterColorKey = d.ColorKey;
            cfg.SupporterSymbolGlowEnabled = d.SymbolGlowEnabled;
            cfg.SupporterSymbolGlowKey = d.SymbolGlowKey;
            cfg.SupporterSymbolPosition = d.SymbolPosition;
            cfg.SupporterTextEnabled = d.TextEnabled;
            cfg.SupporterLabelText = d.LabelText;
            cfg.SupporterLabelOrder = d.LabelOrder;
            cfg.SupporterLabelColorKey = d.LabelColorKey;
            cfg.SupporterLabelGlowEnabled = d.LabelGlowEnabled;
            cfg.SupporterLabelGlowKey = d.LabelGlowKey;
            cfg.SupporterIconLabelEnabled = d.IconLabelEnabled;
            cfg.SupporterIconLabelText = d.IconLabelText;
            cfg.SupporterIconLabelColor = d.IconLabelColor;
            cfg.SupporterIconLabelEdgeEnabled = d.IconLabelEdgeEnabled;
            cfg.SupporterIconLabelEdgeColor = d.IconLabelEdgeColor;
        }
        _configService.Save();
        ClearPending();
    }

    private static string FxDisplayName(string? fxId)
    {
        if (string.IsNullOrEmpty(fxId))
        {
            return "no effect";
        }
        for (var i = 0; i < NameplateFx.All.Length; i++)
        {
            if (string.Equals(NameplateFx.All[i].Id, fxId, StringComparison.Ordinal))
            {
                return NameplateFx.All[i].DisplayName;
            }
        }
        return fxId;
    }

    private string IconDisplayName(string? iconId)
    {
        if (string.IsNullOrEmpty(iconId))
        {
            return "default icon";
        }
        if (SupporterIconService.IsBuiltInHalo(iconId))
        {
            return SupporterIconService.BuiltInHaloName;
        }
        var catalog = _iconService.GetCatalog();
        for (var i = 0; i < catalog.Count; i++)
        {
            if (string.Equals(catalog[i].Id, iconId, StringComparison.OrdinalIgnoreCase))
            {
                return catalog[i].Name;
            }
        }
        return iconId;
    }

    // ------------------------------------------------------------------
    // Stage
    // ------------------------------------------------------------------

    private void DrawStage(string? fxId, string? iconId, string? symbolOverride,
        bool previewing)
    {
        FullWidth(() =>
        {
            SpheneUI.Text.SectionHeader("Your Nameplate",
                "Live preview · click a card below to stage a change.",
                icon: FontAwesomeIcon.IdCard, accent: SpheneUI.Palette.LuminousGold);
            // Full-width strip · staged changes surface in the unsaved-changes
            // toast at the bottom of the view instead of a side control.
            DrawPreviewStrip(fxId, iconId, padY: 24f, plateIndex: 0,
                previewing, symbolOverride);
        });
    }

    // ------------------------------------------------------------------
    // Galleries
    // ------------------------------------------------------------------

    private void DrawEffectGallery(bool supporter)
    {
        FullWidth(() =>
        {
            SpheneUI.Text.SectionHeader("Effect",
                "Animated particles woven around your name · click a card to stage it, synced inside your supporter style.",
                icon: FontAwesomeIcon.Star, accent: SpheneUI.Palette.EtherealPurple);
            var cfg = _configService.Current;
            var current = cfg.SupporterNameplateFxId ?? string.Empty;
            var cells = new List<(string Id, string Name)>(NameplateFx.All.Length + 1)
            {
                (string.Empty, "None")
            };
            for (var i = 0; i < NameplateFx.All.Length; i++)
            {
                cells.Add((NameplateFx.All[i].Id, NameplateFx.All[i].DisplayName));
            }

            SpheneUI.Layout.CardGrid(cells, 150f * SpheneUI.Scale, (cell, i) =>
            {
                var equipped = string.Equals(current, cell.Id, StringComparison.Ordinal);
                var staged = _pendingFxId != null
                    && string.Equals(_pendingFxId, cell.Id, StringComparison.Ordinal);
                var (hovered, clicked) = DrawEffectCell(cell.Id, cell.Name, i, equipped, staged);
                if (hovered)
                {
                    SpheneUI.AttachTooltip(supporter
                        ? "Stage it on the preview above · Apply equips."
                        : "Supporter perk · stage it on the preview above.");
                }
                if (clicked)
                {
                    _pendingFxId = equipped || staged ? null : cell.Id;
                }
            }, maxCols: 4);
        });
    }

    /// <summary>One effect card: chrome + mini nameplate with the effect playing.</summary>
    private (bool Hovered, bool Clicked) DrawEffectCell(string fxId, string label,
        int plateIndex, bool equipped, bool staged)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var cellW = MathF.Max(60f, SpheneUI.Controls.BoundedAvailX());
        var textH = ImGui.GetTextLineHeight();
        var plateH = textH + 28f * scale;
        var labelH = textH + 6f * scale;
        var size = new Vector2(cellW, plateH + labelH);
        var min = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton($"##fxcell{fxId}_{plateIndex}", size);
        var hovered = ImGui.IsItemHovered();

        DrawCellChrome(drawList, min, size, equipped, hovered, staged);

        var plateMin = min + new Vector2(6f * scale);
        var plateMax = new Vector2(min.X + size.X - 6f * scale, plateMin.Y + plateH - 8f * scale);
        DrawMiniPlate(drawList, plateMin, plateMax, fxId, plateIndex);

        var labelW = ImGui.CalcTextSize(label).X;
        drawList.AddText(new Vector2(
                min.X + MathF.Max(6f * scale, (size.X - labelW) * 0.5f),
                plateMax.Y + 4f * scale),
            SpheneUI.ColorUtils.ToU32(equipped
                ? SpheneUI.Colors.TextPrimary
                : staged
                    ? SpheneUI.Palette.LuminousGold
                    : SpheneUI.Colors.TextMuted), label);
        if (equipped)
        {
            DrawEquippedMark(drawList, min, size, scale);
        }
        return (hovered, clicked);
    }

    /// <summary>Name-only plate with the effect playing · the gallery's
    /// miniaturized version of the stage strip.</summary>
    private void DrawMiniPlate(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        string? fxId, int plateIndex)
    {
        var scale = SpheneUI.Scale;
        var radius = 6f * scale;
        var plate = _nameplateService.LocalPlate;
        var name = plate?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = _uiShared.PlayerName;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Player Name";
        }
        var textColor = plate.HasValue
            ? PlateColor(plate.Value.TextColor)
            : SpheneUI.Colors.TextPrimary;
        var nameFg = plate?.NameColors.Foreground is { } nf ? PlateColor(nf) : textColor;
        var nameEdge = plate?.NameColors.Edge is { } ne ? (Vector4?)PlateColor(ne) : null;

        var textH = ImGui.GetTextLineHeight();
        var nameW = ImGui.CalcTextSize(name).X;
        var nameX = min.X + MathF.Max(4f * scale, (max.X - min.X - nameW) * 0.5f);
        var nameY = min.Y + (max.Y - min.Y - textH) * 0.5f;
        var nameBand = new Vector4(nameX, nameY, nameX + nameW, nameY + textH);

        var fx = NameplateFx.ById(fxId);
        drawList.ChannelsSplit(2);
        try
        {
            drawList.ChannelsSetCurrent(0);
            drawList.AddRectFilled(min, max,
                ImGui.ColorConvertFloat4ToU32(SpheneUI.Colors.FrameBg with { W = 0.45f }), radius);
            drawList.ChannelsSetCurrent(1);
            drawList.PushClipRect(min, max, true);
            try
            {
                if (nameEdge.HasValue)
                {
                    var glowU32 = ImGui.ColorConvertFloat4ToU32(nameEdge.Value);
                    var off = MathF.Max(1f, scale);
                    drawList.AddText(new Vector2(nameX + off, nameY), glowU32, name);
                    drawList.AddText(new Vector2(nameX - off, nameY), glowU32, name);
                    drawList.AddText(new Vector2(nameX, nameY + off), glowU32, name);
                    drawList.AddText(new Vector2(nameX, nameY - off), glowU32, name);
                }
                drawList.AddText(new Vector2(nameX, nameY),
                    ImGui.ColorConvertFloat4ToU32(nameFg), name);
                if (fx != null)
                {
                    fx.Draw(NameplateFxContext.ForPreview(drawList, (float)ImGui.GetTime(),
                        plateIndex, nameBand, Vector4.Zero, nameBand, scale, _nameplateService));
                }
            }
            finally
            {
                drawList.PopClipRect();
            }
            drawList.ChannelsSetCurrent(1);
        }
        finally
        {
            drawList.ChannelsMerge();
        }
    }

    private void DrawIconGallery(bool supporter)
    {
        FullWidth(() =>
        {
            SpheneUI.Text.SectionHeader("Sphene Icon",
                "The icon drawn at the end of your nameplate · click a card to stage it, server-provided icons included.",
                icon: FontAwesomeIcon.Icons, accent: SpheneUI.Palette.CrystalBlue);
            var cfg = _configService.Current;
            var cells = new List<IconCell>
            {
                new("off", false, string.Empty, "Off"),
                new("default", true, string.Empty, "Default"),
                new("halo", true, SupporterIconService.BuiltInHaloId,
                    SupporterIconService.BuiltInHaloName),
            };
            var catalog = _iconService.GetCatalog();
            for (var i = 0; i < catalog.Count; i++)
            {
                cells.Add(new IconCell(catalog[i].Id, true, catalog[i].Id,
                    catalog[i].Name + (catalog[i].Animated ? " (animated)" : string.Empty)));
            }
            if (cfg.SupporterIconEnabled && cfg.SupporterIconId.Length > 0
                && !_iconService.IsIconKnown(cfg.SupporterIconId))
            {
                cells.Add(new IconCell(cfg.SupporterIconId, true, cfg.SupporterIconId,
                    cfg.SupporterIconId + " (not on server)"));
            }

            SpheneUI.Layout.CardGrid(cells, 120f * SpheneUI.Scale, (cell, i) =>
            {
                var equipped = cell.Enabled
                    ? cfg.SupporterIconEnabled
                        && string.Equals(cfg.SupporterIconId, cell.IconId, StringComparison.OrdinalIgnoreCase)
                    : !cfg.SupporterIconEnabled;
                var staged = _pendingIcon is { } pending
                    && pending.Enabled == cell.Enabled
                    && string.Equals(pending.Id, cell.IconId, StringComparison.OrdinalIgnoreCase);
                var (hovered, clicked) = DrawIconCell(cell, i, equipped, staged);
                if (hovered)
                {
                    SpheneUI.AttachTooltip(supporter
                        ? "Stage it on the preview above · Apply equips."
                        : "Supporter perk · stage it on the preview above.");
                }
                if (clicked)
                {
                    _pendingIcon = equipped || staged ? null : (cell.Enabled, cell.IconId);
                }
            }, maxCols: 5);
        });
    }

    private readonly record struct IconCell(string Key, bool Enabled, string IconId, string Label);

    /// <summary>One icon tile: chrome + the icon's live frame + name.</summary>
    private (bool Hovered, bool Clicked) DrawIconCell(IconCell cell, int index,
        bool equipped, bool staged)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var cellW = MathF.Max(50f, SpheneUI.Controls.BoundedAvailX());
        var textH = ImGui.GetTextLineHeight();
        var tileH = 34f * scale + textH + 6f * scale;
        var size = new Vector2(cellW, tileH);
        var min = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton($"##iconcell{cell.Key}_{index}", size);
        var hovered = ImGui.IsItemHovered();

        DrawCellChrome(drawList, min, size, equipped, hovered, staged);

        var iconH = 26f * scale;
        var iconY = min.Y + 4f * scale;
        var cx = min.X + size.X * 0.5f;
        if (!cell.Enabled)
        {
            var glyph = FontAwesomeIcon.Ban.ToIconString();
            using (SpheneUI.IconFont.Push())
            {
                var gw = ImGui.CalcTextSize(glyph).X;
                drawList.AddText(new Vector2(cx - gw * 0.5f, iconY + (iconH - ImGui.GetTextLineHeight()) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), glyph);
            }
        }
        else if (cell.IconId.Length == 0
            && _nameplateService.TryGetDefaultIconWrap(out var wrap) && wrap != null)
        {
            var defaultH = iconH * SupporterNameplateService.DefaultIconScale;
            var w = defaultH * wrap.Width / MathF.Max(1f, wrap.Height);
            var dy = iconY + (iconH - defaultH) * 0.5f;
            SpheneUI.Media.ImageInRect(drawList, wrap,
                new Vector2(cx - w * 0.5f, dy), new Vector2(cx + w * 0.5f, dy + defaultH));
        }
        else
        {
            var icon = _iconService.GetIcon(cell.IconId);
            if (icon != null)
            {
                var (handle, uv0, uv1) = icon.GetFrame(ImGui.GetTime());
                var w = icon.AspectSize(iconH).X;
                SpheneUI.Media.SpriteInRect(drawList, handle,
                    new Vector2(cx - w * 0.5f, iconY), new Vector2(cx + w * 0.5f, iconY + iconH), uv0, uv1);
            }
            else
            {
                // icon still decoding or fetch failed · quiet placeholder
                var gw = ImGui.CalcTextSize("…").X;
                drawList.AddText(new Vector2(cx - gw * 0.5f, iconY + (iconH - textH) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), "…");
            }
        }

        var labelW = MathF.Min(ImGui.CalcTextSize(cell.Label).X, size.X - 8f * scale);
        drawList.PushClipRect(min + new Vector2(4f * scale, tileH - textH - 4f * scale),
            min + size - new Vector2(4f * scale, 2f * scale), true);
        drawList.AddText(new Vector2(
                min.X + MathF.Max(4f * scale, (size.X - labelW) * 0.5f),
                min.Y + tileH - textH - 4f * scale),
            SpheneUI.ColorUtils.ToU32(equipped
                ? SpheneUI.Colors.TextPrimary
                : staged
                    ? SpheneUI.Palette.LuminousGold
                    : SpheneUI.Colors.TextMuted), cell.Label);
        drawList.PopClipRect();
        if (equipped)
        {
            DrawEquippedMark(drawList, min, size, scale);
        }
        return (hovered, clicked);
    }

    /// <summary>Shared card chrome: tonal fill, hover lift, accent ring + check
    /// for the equipped cell and a gold ring for the one staged on the
    /// preview (Apply pending).</summary>
    private static void DrawCellChrome(ImDrawListPtr drawList, Vector2 min, Vector2 size,
        bool equipped, bool hovered, bool staged = false)
    {
        var scale = SpheneUI.Scale;
        var radius = 8f * scale;
        var accent = SpheneUI.Colors.Accent;
        var gold = SpheneUI.Palette.LuminousGold;
        var bg = equipped
            ? SpheneUI.ColorUtils.WithAlpha(accent, 0.14f)
            : staged
                ? SpheneUI.ColorUtils.WithAlpha(gold, 0.10f)
                : SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.FrameBg, hovered ? 0.85f : 0.6f);
        drawList.AddRectFilled(min, min + size, SpheneUI.ColorUtils.ToU32(bg), radius);
        var border = equipped
            ? SpheneUI.ColorUtils.WithAlpha(accent, 0.8f)
            : staged
                ? SpheneUI.ColorUtils.WithAlpha(gold, 0.9f)
                : hovered
                    ? SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)
                    : SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.4f);
        // stroke inset by half its width - AddRect centers the line on the
        // edge, and the outer half clips away on the child boundary
        var stroke = MathF.Max(1f, (equipped || staged ? 1.6f : 1f) * scale);
        var inset = new Vector2(stroke * 0.5f);
        drawList.AddRect(min + inset, min + size - inset,
            SpheneUI.ColorUtils.ToU32(border), radius - inset.X,
            ImDrawFlags.RoundCornersAll, stroke);
    }

    /// <summary>The accent check disc marking the equipped cell.</summary>
    private static void DrawEquippedMark(ImDrawListPtr drawList, Vector2 min, Vector2 size, float scale)
    {
        var r = 7f * scale;
        var c = new Vector2(min.X + size.X - r - 4f * scale, min.Y + r + 4f * scale);
        var accent = SpheneUI.Colors.Accent;
        drawList.AddCircleFilled(c, r, SpheneUI.ColorUtils.ToU32(accent with { W = 0.95f }));
        var glyph = FontAwesomeIcon.Check.ToIconString();
        using (SpheneUI.IconFont.Push())
        {
            var gw = ImGui.CalcTextSize(glyph).X;
            var gh = ImGui.GetTextLineHeight();
            drawList.AddText(c - new Vector2(gw, gh) * 0.5f,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBg), glyph);
        }
    }

    // ------------------------------------------------------------------
    // Option groups
    // ------------------------------------------------------------------

    private void DrawMarkGroup(bool supporter)
    {
        FullWidth(() =>
        {
            SpheneUI.Layout.HeadedRowGroup("Mark", () =>
            {
                var cfg = _configService.Current;
                var symbolChar = cfg.SupporterSymbolChar ?? string.Empty;

                // The symbol chips sit outside the supporter gate on purpose:
                // staging a look is free for everyone, Apply stays gated.
                BeginSupporterRow("Symbol");
                for (var i = 0; i < SymbolPresets.Length; i++)
                {
                    var value = i == 0 ? string.Empty : SymbolPresets[i];
                    var equipped = string.Equals(symbolChar, value, StringComparison.Ordinal);
                    var staged = _pendingSymbol != null
                        && string.Equals(_pendingSymbol, value, StringComparison.Ordinal);
                    var (_, clicked) = DrawSymbolChip($"studsym{i}", value, equipped, staged);
                    if (clicked)
                    {
                        _pendingSymbol = equipped || staged ? null : value;
                    }
                    SpheneUI.AttachTooltip(i == 0
                        ? "No symbol · stage it on the preview."
                        : $"{SymbolPresets[i]} · stage it on the preview.");
                    if (i + 1 < SymbolPresets.Length)
                    {
                        ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                    }
                }
                ImGui.SameLine(0, SpheneUI.Metrics.Gap * 2f);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Colored("Custom", ImGuiColors.DalamudGrey);
                ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                ImGui.SetNextItemWidth(64f * ImGuiHelpers.GlobalScale);
                var custom = symbolChar;
                if (SpheneUI.Controls.InputText("##studioSymbolCustom", ref custom, 4,
                        ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    _pendingSymbol = string.Equals(custom, symbolChar, StringComparison.Ordinal)
                        ? null
                        : custom;
                }
                SpheneUI.AttachTooltip("Type any short symbol; Enter stages it on the preview.");

                using (ImRaii.Disabled(!supporter))
                {
                    BeginSupporterRow("Colors");
                    BeginSupporterSwatchCluster(2);
                    DrawSupporterColorKeyPicker("studioSymbolColor",
                        _draft?.ColorKey ?? cfg.SupporterColorKey,
                        key => StageDraft(d => d.ColorKey = key), dockRight: false);
                    ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                    DrawSupporterGlowKeyPicker("studioSymbolGlow",
                        _draft?.SymbolGlowEnabled ?? cfg.SupporterSymbolGlowEnabled,
                        _draft?.SymbolGlowKey ?? cfg.SupporterSymbolGlowKey,
                        (enabled, key) => StageDraft(d =>
                        {
                            d.SymbolGlowEnabled = enabled;
                            d.SymbolGlowKey = key;
                        }), dockRight: false);
                    SpheneUI.AttachTooltip("Text color and optional edge glow for the mark next to your name.");

                    BeginSupporterRow("Position");
                    var position = (int)(_draft?.SymbolPosition ?? cfg.SupporterSymbolPosition);
                    if (SpheneUI.Controls.Segmented("studioPosition", ref position, PositionItems))
                    {
                        StageDraft(d => d.SymbolPosition = (SupporterSymbolPosition)position);
                    }
                    SpheneUI.AttachTooltip("Where the mark sits relative to your name. Both repeats the symbol on the right.");
                }
            }, FontAwesomeIcon.PenFancy,
                description: "Symbol and colors next to your name · click a chip to stage it on the preview.",
                accent: SpheneUI.Palette.SpheneGold, id: "studio-mark");
        });
    }

    /// <summary>A mark-symbol chip in the gallery's card chrome: glyph
    /// centered, accent ring when equipped, gold ring when staged · the ban
    /// glyph stands in for "no symbol".</summary>
    private static (bool Hovered, bool Clicked) DrawSymbolChip(string id, string symbol,
        bool equipped, bool staged)
    {
        var scale = SpheneUI.Scale;
        var size = new Vector2(ImGui.GetFrameHeight() + 4f * scale);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        DrawCellChrome(drawList, min, size, equipped, hovered, staged);
        if (symbol.Length == 0)
        {
            var glyph = FontAwesomeIcon.Ban.ToIconString();
            using (SpheneUI.IconFont.Push())
            {
                var gw = ImGui.CalcTextSize(glyph).X;
                var gh = ImGui.GetTextLineHeight();
                drawList.AddText(new Vector2(min.X + (size.X - gw) * 0.5f,
                        min.Y + (size.Y - gh) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), glyph);
            }
        }
        else
        {
            var textW = ImGui.CalcTextSize(symbol).X;
            var textH = ImGui.GetTextLineHeight();
            drawList.AddText(new Vector2(min.X + (size.X - textW) * 0.5f,
                    min.Y + (size.Y - textH) * 0.5f),
                ImGui.GetColorU32(ImGuiCol.Text), symbol);
        }
        return (hovered, clicked);
    }

    private void DrawLabelGroup(bool supporter)
    {
        FullWidth(() =>
        {
            SpheneUI.Layout.HeadedRowGroup("Label", () =>
            {
                using (ImRaii.Disabled(!supporter))
                {
                    var cfg = _configService.Current;
                    var textEnabled = _draft?.TextEnabled ?? cfg.SupporterTextEnabled;
                    if (SpheneUI.Controls.Toggle("Text label##studioTextEnabled", ref textEnabled))
                    {
                        StageDraft(d => d.TextEnabled = textEnabled);
                    }
                    SpheneUI.AttachTooltip("Show a custom text together with the symbol on your nameplate.");

                    if (textEnabled)
                    {
                        BeginSupporterRow("Text", true);
                        var labelText = _draft?.LabelText ?? cfg.SupporterLabelText ?? string.Empty;
                        if (SpheneUI.Controls.InputText("##studioLabelText", ref labelText, 30,
                                dockRight: true))
                        {
                            StageDraft(d => d.LabelText = labelText);
                        }

                        BeginSupporterRow("Order", true);
                        var labelOrder = (int)(_draft?.LabelOrder ?? cfg.SupporterLabelOrder);
                        var orderWidth = MathF.Min(SpheneUI.Controls.SegmentedWidth(LabelOrderItems),
                            ImGui.GetContentRegionAvail().X - SpheneUI.Metrics.TrailingIconReserve
                                - 2f * SpheneUI.Metrics.SwatchSize - 2f * SpheneUI.Metrics.Gap);
                        if (SpheneUI.Controls.Segmented("studioLabelOrder", ref labelOrder, LabelOrderItems,
                                width: orderWidth))
                        {
                            StageDraft(d => d.LabelOrder = (SupporterLabelOrder)labelOrder);
                        }
                        ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                        DrawSupporterColorKeyPicker("studioLabelColor",
                            _draft?.LabelColorKey ?? cfg.SupporterLabelColorKey,
                            key => StageDraft(d => d.LabelColorKey = key), dockRight: false);
                        ImGui.SameLine(0, SpheneUI.Metrics.Gap);
                        DrawSupporterGlowKeyPicker("studioLabelGlow",
                            _draft?.LabelGlowEnabled ?? cfg.SupporterLabelGlowEnabled,
                            _draft?.LabelGlowKey ?? cfg.SupporterLabelGlowKey,
                            (enabled, key) => StageDraft(d =>
                            {
                                d.LabelGlowEnabled = enabled;
                                d.LabelGlowKey = key;
                            }), dockRight: false);
                    }
                }
            }, FontAwesomeIcon.Font,
                description: "Custom text shown together with the mark.",
                accent: SpheneUI.Palette.SpheneGold, id: "studio-label");
        });
    }

    private void DrawAdvancedSection()
    {
        FullWidth(() =>
        {
            if (SpheneUI.Controls.CollapsibleSection("Advanced"))
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Layout.RowGroup(() =>
                {
                    SupporterNameplateOptionBlock.DrawSupporterIconSizeOption(_configService, "StudioIconSize");
                    SupporterNameplateOptionBlock.DrawGpuDepthOcclusionOption(_configService, "StudioGpuDepthOcclusion");
                    SupporterNameplateOptionBlock.DrawShowSupporterNameplateOption(_configService, _uiShared, "StudioShowSupporterNameplate");
                }, id: "studio-advanced");
                SpheneUI.Controls.HelpText("Occlusion, icon size and visibility apply to every supporter plate on your screen. Diagnostic overlays and mask tuning live on the Debug settings page.");
            }
        });
    }

    // ------------------------------------------------------------------
    // Preview strip (ported from SettingsUi, extended with the live effect)
    // ------------------------------------------------------------------

    /// <summary>
    /// Mock nameplate strip: renders the configured mark, label, icon and
    /// colors around the player's real name · and the picked effect animating
    /// behind/in front of the letters via draw-list channels.
    /// <paramref name="previewing"/> stamps a badge inside the strip instead
    /// of pushing a hint line below it, so staged previews never shift layout.
    /// <paramref name="symbolOverride"/> swaps the mark glyph for a staged
    /// pick; <paramref name="width"/> shrinks the strip to leave room for an
    /// action column. Returns the strip's rendered size.
    /// </summary>
    private Vector2 DrawPreviewStrip(string? fxId, string? iconId, float padY,
        int plateIndex, bool previewing = false, string? symbolOverride = null,
        float? width = null, string? labelOverride = null,
        SupporterSymbolPosition? positionOverride = null)
    {
        var cfg = _configService.Current;
        var scale = SpheneUI.Scale;
        var symbol = symbolOverride ?? cfg.SupporterSymbolChar ?? string.Empty;
        var label = labelOverride
            ?? ((_draft?.TextEnabled ?? cfg.SupporterTextEnabled)
                ? _draft?.LabelText ?? cfg.SupporterLabelText ?? string.Empty
                : string.Empty);

        var plate = _nameplateService.LocalPlate;
        var name = plate?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = _uiShared.PlayerName;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Player Name";
        }
        var fcTag = plate?.QuotedFreeCompanyTag ?? string.Empty;
        var title = plate?.QuotedTitle ?? string.Empty;
        var titleShown = title.Length > 0 && (plate?.DisplayTitle ?? false);
        var titleAbove = plate?.IsPrefixTitle ?? true;
        var textColor = plate.HasValue ? PlateColor(plate.Value.TextColor) : SpheneUI.Colors.TextPrimary;
        var edgeColor = plate.HasValue ? (Vector4?)PlateColor(plate.Value.EdgeColor) : null;
        var nameFg = plate?.NameColors.Foreground is { } nf ? PlateColor(nf) : textColor;
        var nameEdge = plate?.NameColors.Edge is { } ne ? (Vector4?)PlateColor(ne) : edgeColor;
        var titleFg = plate?.TitleColors.Foreground is { } tf ? PlateColor(tf) : textColor;
        var titleEdge = plate?.TitleColors.Edge is { } te ? (Vector4?)PlateColor(te) : edgeColor;
        var fcFg = plate?.FreeCompanyColors.Foreground is { } ff ? PlateColor(ff) : textColor;
        var fcEdge = plate?.FreeCompanyColors.Edge is { } fe ? (Vector4?)PlateColor(fe) : edgeColor;

        var symbolColorKey = _draft?.ColorKey ?? cfg.SupporterColorKey;
        var labelColorKey = _draft?.LabelColorKey ?? cfg.SupporterLabelColorKey;
        var symbolFg = symbolColorKey == 0 ? textColor : ResolveUiForegroundColor(symbolColorKey);
        var labelFg = labelColorKey == 0 ? textColor : ResolveUiForegroundColor(labelColorKey);
        var symbolGlowEnabled = _draft?.SymbolGlowEnabled ?? cfg.SupporterSymbolGlowEnabled;
        var symbolGlow = symbolGlowEnabled
            ? ResolveUiGlowColor(_draft?.SymbolGlowKey ?? cfg.SupporterSymbolGlowKey) : edgeColor;
        var labelGlowEnabled = _draft?.LabelGlowEnabled ?? cfg.SupporterLabelGlowEnabled;
        var labelGlow = labelGlowEnabled
            ? ResolveUiGlowColor(_draft?.LabelGlowKey ?? cfg.SupporterLabelGlowKey) : edgeColor;
        var symbolFirst = (_draft?.LabelOrder ?? cfg.SupporterLabelOrder)
            == SupporterLabelOrder.SymbolThenText;
        var position = positionOverride ?? (_draft?.SymbolPosition ?? cfg.SupporterSymbolPosition);
        var showLeft = position != SupporterSymbolPosition.Right;
        var showRight = position != SupporterSymbolPosition.Left;

        var icon = iconId != null ? _iconService.GetIcon(iconId) : null;
        IDalamudTextureWrap? defaultIconWrap = null;
        if (iconId != null && icon == null)
        {
            _ = _nameplateService.TryGetDefaultIconWrap(out defaultIconWrap);
        }
        var iconLabelEnabled = _draft?.IconLabelEnabled ?? cfg.SupporterIconLabelEnabled;
        var iconLabel = iconLabelEnabled
            ? _draft?.IconLabelText ?? cfg.SupporterIconLabelText ?? string.Empty : string.Empty;
        var iconLabelFg = ImGui.ColorConvertU32ToFloat4(
            _draft?.IconLabelColor ?? cfg.SupporterIconLabelColor);
        var iconLabelEdgeEnabled = _draft?.IconLabelEdgeEnabled ?? cfg.SupporterIconLabelEdgeEnabled;
        var iconLabelEdge = iconLabelEdgeEnabled
            ? (Vector4?)ImGui.ColorConvertU32ToFloat4(
                _draft?.IconLabelEdgeColor ?? cfg.SupporterIconLabelEdgeColor)
            : null;

        // The game's small name icon (job icon) leads the line · its texture
        // resolves async, so the slot reserves space before the wrap exists.
        var jobIconId = plate?.NameIconId ?? -1;
        var jobIconWrap = jobIconId > 0
            ? SpheneUI.Media.GameIcon((uint)jobIconId) : null;

        var textH = ImGui.GetTextLineHeight();
        var spaceW = ImGui.CalcTextSize(" ").X;
        var markGap = 8f * scale;
        // mirrors the in-game pass: the size slider scales every icon, the
        // edge-to-edge default icon draws smaller so it matches the rest
        var iconHeight = textH * 1.15f
            * Math.Clamp(cfg.SupporterIconSizePercent, 25, 400) / 100f;
        var iconSize = icon != null ? icon.AspectSize(iconHeight)
            : defaultIconWrap != null
                ? new Vector2(iconHeight * SupporterNameplateService.DefaultIconScale)
                : Vector2.Zero;
        var jobIconSize = jobIconId > 0 ? textH * 1.3f : 0f;
        var jobIconGap = jobIconSize > 0f ? 4f * scale : 0f;

        float PartW(string s) => s.Length == 0 ? 0f : ImGui.CalcTextSize(s).X;
        float MarkW(bool withLabel)
        {
            var w = (symbol.Length > 0 ? PartW(symbol) : 0f)
                + (withLabel && label.Length > 0 ? PartW(label) : 0f);
            if (w > 0f && symbol.Length > 0 && withLabel && label.Length > 0)
            {
                w += spaceW;
            }
            return w;
        }

        var leftW = showLeft ? MarkW(withLabel: true) : 0f;
        var rightW = showRight ? MarkW(withLabel: position == SupporterSymbolPosition.Right) : 0f;
        var fcW = PartW(fcTag);
        var titleW = PartW(title);
        var iconLabelW = PartW(iconLabel);
        var nameW = PartW(name);
        var totalW = jobIconSize + jobIconGap + leftW + nameW + rightW + fcW
            + iconSize.X + iconLabelW
            + (leftW > 0f ? markGap : 0f)
            + (rightW > 0f ? markGap : 0f)
            + (fcW > 0f ? markGap : 0f)
            + (iconSize.X > 0f ? markGap : 0f)
            + (iconLabelW > 0f && iconSize.X > 0f ? spaceW : 0f);

        var drawList = ImGui.GetWindowDrawList();
        var stripMin = ImGui.GetCursorScreenPos();
        var stripW = MathF.Max(0f, width ?? SpheneUI.Controls.BoundedAvailX());
        var pad = padY * scale;
        var lineGap = 3f * scale;
        var stripH = PreviewStripHeight(padY);
        var radius = 6f * scale;
        var stripMax = stripMin + new Vector2(stripW, stripH);
        ImGui.Dummy(new Vector2(stripW, stripH));

        var nameLineY = stripMin.Y + pad + (titleShown && titleAbove ? textH + lineGap : 0f);
        var x = stripMin.X + MathF.Max(8f * scale, (stripW - totalW) * 0.5f);
        var glowOff = MathF.Max(1f, scale);
        var y = nameLineY;

        void DrawPart(string text, Vector4 fg, Vector4? glow)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (glow.HasValue)
            {
                var glowU32 = ImGui.ColorConvertFloat4ToU32(glow.Value);
                drawList.AddText(new Vector2(x + glowOff, y), glowU32, text);
                drawList.AddText(new Vector2(x - glowOff, y), glowU32, text);
                drawList.AddText(new Vector2(x, y + glowOff), glowU32, text);
                drawList.AddText(new Vector2(x, y - glowOff), glowU32, text);
            }
            drawList.AddText(new Vector2(x, y), ImGui.ColorConvertFloat4ToU32(fg), text);
            x += PartW(text);
        }

        void DrawMark(bool withLabel)
        {
            var drew = false;
            void Part(string text, Vector4 fg, Vector4? glow)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }
                if (drew)
                {
                    x += spaceW;
                }
                DrawPart(text, fg, glow);
                drew = true;
            }
            if (symbolFirst)
            {
                Part(symbol, symbolFg, symbolGlow);
                if (withLabel)
                {
                    Part(label, labelFg, labelGlow);
                }
            }
            else
            {
                if (withLabel)
                {
                    Part(label, labelFg, labelGlow);
                }
                Part(symbol, symbolFg, symbolGlow);
            }
        }

        var fx = NameplateFx.ById(fxId);
        var contentX = stripMin.X + MathF.Max(8f * scale, (stripW - totalW) * 0.5f);
        // The game's name line = injected mark + name + FC tag · icons are
        // overlay slots outside it, so the fx band spans the text run only.
        var textLineX0 = contentX + jobIconSize + jobIconGap;
        var textLineX1 = textLineX0 + leftW + (leftW > 0f ? markGap : 0f) + nameW
            + (rightW > 0f ? markGap : 0f) + rightW
            + (fcW > 0f ? markGap : 0f) + fcW;
        var nameBand = new Vector4(textLineX0, nameLineY, textLineX1, nameLineY + textH);
        var titleCx = (textLineX0 + textLineX1) * 0.5f;
        var titleBand = titleShown
            ? new Vector4(titleCx - titleW * 0.5f,
                titleAbove ? stripMin.Y + pad : stripMin.Y + pad + textH + lineGap,
                titleCx + titleW * 0.5f,
                (titleAbove ? stripMin.Y + pad : stripMin.Y + pad + textH + lineGap) + textH)
            : Vector4.Zero;
        // Ink coverage mirrors the real plate's union: the text block across
        // both bands · overlay icons stay out of it like in game.
        var inkRect = new Vector4(
            MathF.Min(textLineX0, titleShown ? titleBand.X : float.MaxValue),
            titleShown && titleAbove ? titleBand.Y : nameBand.Y,
            MathF.Max(textLineX1, titleShown ? titleBand.Z : 0f),
            titleShown && !titleAbove ? titleBand.W : nameBand.W);

        drawList.ChannelsSplit(2);
        try
        {
            drawList.ChannelsSetCurrent(0);
            drawList.AddRectFilled(stripMin, stripMax,
                ImGui.ColorConvertFloat4ToU32(SpheneUI.Colors.FrameBg with { W = 0.55f }), radius);
            drawList.AddRect(stripMin, stripMax,
                ImGui.ColorConvertFloat4ToU32(SpheneUI.Colors.Border with { W = 0.6f }), radius);
            drawList.ChannelsSetCurrent(1);

            if (titleShown)
            {
                var titleY = titleAbove
                    ? stripMin.Y + pad
                    : stripMin.Y + pad + textH + lineGap;
                var savedX = x;
                x = titleCx - titleW * 0.5f;
                y = titleY;
                DrawPart(title, titleFg, titleEdge);
                x = savedX;
                y = nameLineY;
            }

            if (jobIconSize > 0f)
            {
                if (jobIconWrap != null)
                {
                    var jobIconY = nameLineY + (textH - jobIconSize) * 0.5f + textH * 0.05f;
                    SpheneUI.Media.ImageInRect(drawList, jobIconWrap,
                        new Vector2(x, jobIconY), new Vector2(x + jobIconSize, jobIconY + jobIconSize));
                }
                x += jobIconSize + jobIconGap;
            }

            if (leftW > 0f)
            {
                DrawMark(withLabel: true);
                x += markGap;
            }
            DrawPart(name, nameFg, nameEdge);
            if (rightW > 0f)
            {
                x += markGap;
                DrawMark(withLabel: position == SupporterSymbolPosition.Right);
            }
            if (fcW > 0f)
            {
                x += markGap;
                DrawPart(fcTag, fcFg, fcEdge);
            }
            var iconAnchorY = nameLineY + (textH - iconSize.Y) * 0.5f + textH * 0.05f;
            if (icon != null)
            {
                x += markGap;
                var (handle, uv0, uv1) = icon.GetFrame(ImGui.GetTime());
                SpheneUI.Media.SpriteInRect(drawList, handle,
                    new Vector2(x, iconAnchorY), new Vector2(x + iconSize.X, iconAnchorY + iconSize.Y), uv0, uv1);
                x += iconSize.X;
            }
            else if (defaultIconWrap != null)
            {
                x += markGap;
                SpheneUI.Media.ImageInRect(drawList, defaultIconWrap,
                    new Vector2(x, iconAnchorY), new Vector2(x + iconSize.X, iconAnchorY + iconSize.Y));
                x += iconSize.X;
            }
            if (iconLabelW > 0f && (icon != null || defaultIconWrap != null))
            {
                x += spaceW;
                DrawPart(iconLabel, iconLabelFg, iconLabelEdge);
            }

            if (fx != null)
            {
                // Particles may stray past the strip · give them breathing room
                // but keep them off the card's padding and borders.
                var fxMargin = 12f * scale;
                drawList.PushClipRect(stripMin - new Vector2(fxMargin), stripMax + new Vector2(fxMargin), true);
                try
                {
                    fx.Draw(NameplateFxContext.ForPreview(drawList, (float)ImGui.GetTime(),
                        plateIndex, nameBand, titleBand, inkRect, scale, _nameplateService));
                }
                finally
                {
                    drawList.PopClipRect();
                }
            }
            drawList.ChannelsSetCurrent(1);
        }
        finally
        {
            drawList.ChannelsMerge();
        }

        if (previewing)
        {
            const string badge = "Previewing";
            var badgePadX = 7f * scale;
            var badgeH = textH + 4f * scale;
            var badgeW = ImGui.CalcTextSize(badge).X + badgePadX * 2f;
            var badgeMax = new Vector2(stripMax.X - 6f * scale,
                stripMin.Y + MathF.Max(4f * scale, (pad - badgeH) * 0.5f) + badgeH);
            var badgeMin = new Vector2(badgeMax.X - badgeW, badgeMax.Y - badgeH);
            var gold = SpheneUI.ColorUtils.ToU32(SpheneUI.Palette.LuminousGold);
            drawList.AddRectFilled(badgeMin, badgeMax,
                ImGui.ColorConvertFloat4ToU32(SpheneUI.Colors.FrameBg with { W = 0.92f }),
                badgeH * 0.5f);
            drawList.AddRect(badgeMin, badgeMax, gold, badgeH * 0.5f,
                ImDrawFlags.None, MathF.Max(1f, scale));
            drawList.AddText(badgeMin + new Vector2(badgePadX, 2f * scale), gold, badge);
        }
        return new Vector2(stripW, stripH);
    }

    // ------------------------------------------------------------------
    // Moved from SettingsUi · rows and pickers
    // ------------------------------------------------------------------

    private static void BeginSupporterSwatchCluster(int count)
    {
        var swatch = SpheneUI.Metrics.SwatchSize;
        var gap = SpheneUI.Metrics.Gap;
        var width = count * swatch + (count - 1) * gap;
        var cursor = ImGui.GetCursorScreenPos();
        var right = cursor.X + SpheneUI.Controls.BoundedAvailX()
            - SpheneUI.Metrics.TrailingIconReserve;
        ImGui.SetCursorScreenPos(new Vector2(right - width,
            cursor.Y + (ImGui.GetFrameHeight() - swatch) * 0.5f));
    }

    private static void BeginSupporterRow(string label, bool indented = false)
    {
        SpheneUI.GroupState.RowStart();
        ImGui.AlignTextToFramePadding();
        if (indented)
        {
            SpheneUI.Text.Colored("  " + label, ImGuiColors.DalamudGrey3);
        }
        else
        {
            SpheneUI.Text.Colored(label, ImGuiColors.DalamudGrey);
        }
        ImGui.SameLine(RowLabelColumn * ImGuiHelpers.GlobalScale);
    }

    private static void DrawSupporterColorKeyPicker(string id, ushort colorKey, Action<ushort> apply,
        bool dockRight = true)
    {
        var preview = ResolveUiForegroundColor(colorKey);
        var swatchSize = SpheneUI.Metrics.SwatchSize;
        var popupId = "##" + id + "_palette";

        if (dockRight)
        {
            SpheneUI.Controls.DockToRightEdge(swatchSize);
        }

        var clicked = colorKey == 0
            ? SpheneUI.Controls.GlyphSwatch(popupId + "_btn", "-", swatchSize)
            : SpheneUI.Controls.ColorSwatch(popupId + "_btn", preview);
        if (clicked)
        {
            ImGui.OpenPopup(popupId);
        }
        SpheneUI.AttachTooltip(colorKey == 0
            ? "No text color · the nameplate default is used. Click to pick a color."
            : $"Game UI foreground color (key {colorKey}). Click to choose a color.");

        if (SpheneUI.Popups.BeginStyled(popupId))
        {
            DrawSupporterPaletteCells(SortedUiForegroundKeys(), ResolveUiForegroundColor,
                colorKey, colorKey != 0, apply, applyNone: () => apply(0));
            SpheneUI.Popups.EndStyled();
        }
    }

    private static void DrawSupporterGlowKeyPicker(string id, bool enabled, ushort colorKey,
        Action<bool, ushort> apply, bool dockRight = true)
    {
        var swatchSize = SpheneUI.Metrics.SwatchSize;
        var popupId = "##" + id + "_palette";

        if (dockRight)
        {
            SpheneUI.Controls.DockToRightEdge(swatchSize);
        }

        var clicked = enabled
            ? SpheneUI.Controls.FrameSwatch(popupId + "_btn", ResolveUiGlowColor(colorKey))
            : SpheneUI.Controls.GlyphSwatch(popupId + "_btn", "-", swatchSize);
        if (clicked)
        {
            ImGui.OpenPopup(popupId);
        }
        SpheneUI.AttachTooltip(enabled
            ? $"Game UI glow color (key {colorKey}). Click to change or disable."
            : "Edge glow color disabled. Click to pick a color.");

        if (SpheneUI.Popups.BeginStyled(popupId))
        {
            DrawSupporterPaletteCells(SortedUiGlowKeys(), ResolveUiGlowColor,
                colorKey, enabled, key => apply(true, key), frame: true,
                applyNone: () => apply(false, colorKey));
            SpheneUI.Popups.EndStyled();
        }
    }

    private static void DrawSupporterPaletteCells(List<ushort> palette, Func<ushort, Vector4> resolveColor,
        ushort selectedKey, bool hasSelection, Action<ushort> apply,
        bool frame = false, Action? applyNone = null)
    {
        const int columns = 8;
        var cell = ImGui.GetFrameHeight() + 2f * ImGuiHelpers.GlobalScale;
        var leadingNone = applyNone != null ? 1 : 0;
        if (applyNone != null)
        {
            if (SpheneUI.Controls.TransparentSwatch("swNone", cell, !hasSelection))
            {
                applyNone();
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip("None · no color.");
        }
        for (var i = 0; i < palette.Count; i++)
        {
            var key = palette[i];
            if ((i + leadingNone) % columns != 0)
            {
                ImGui.SameLine(0, SpheneUI.Metrics.Gap);
            }
            using var swatchId = ImRaii.PushId("sw" + i);
            var clicked = frame
                ? SpheneUI.Controls.FrameSwatch("swatch",
                    resolveColor(key), cell, hasSelection && key == selectedKey)
                : SpheneUI.Controls.ColorSwatch("swatch",
                    resolveColor(key), cell, hasSelection && key == selectedKey);
            if (clicked)
            {
                apply(key);
                ImGui.CloseCurrentPopup();
            }
            SpheneUI.AttachTooltip($"Key {key}");
        }
    }

    private static List<ushort>? _sortedUiForegroundKeys;

    private static List<ushort> SortedUiForegroundKeys()
        => _sortedUiForegroundKeys ??= SortedUiColorKeys(ResolveUiForegroundColor);

    private static List<ushort> SortedUiGlowKeys()
        => SortedUiForegroundKeys();

    private static List<ushort> SortedUiColorKeys(Func<ushort, Vector4> resolveColor)
    {
        var seen = new HashSet<uint>();
        var entries = new List<(ushort Key, int HueBucket, float L, float S, float H)>();
        for (var i = 0; i <= UiForegroundPaletteMaxIndex; i++)
        {
            var key = UiForegroundPaletteIndexToColorKey(i);
            if (key == 0)
            {
                continue;
            }
            var color = resolveColor(key);
            if (!seen.Add(ImGui.ColorConvertFloat4ToU32(color)))
            {
                continue;
            }
            RgbaToHsl(color, out var h, out var s, out var l);
            entries.Add((key, (int)(h / HueBucketDegrees), l, s, h));
        }
        entries.Sort(static (a, b) =>
        {
            var greyA = a.S < 0.08f;
            var greyB = b.S < 0.08f;
            if (greyA != greyB)
            {
                return greyA ? -1 : 1;
            }
            if (greyA)
            {
                return b.L.CompareTo(a.L);
            }
            var bucket = a.HueBucket.CompareTo(b.HueBucket);
            if (bucket != 0)
            {
                return bucket;
            }
            var light = b.L.CompareTo(a.L);
            if (light != 0)
            {
                return light;
            }
            var sat = b.S.CompareTo(a.S);
            return sat != 0 ? sat : a.H.CompareTo(b.H);
        });
        return entries.ConvertAll(static e => e.Key);
    }

    private static void RgbaToHsl(Vector4 c, out float h, out float s, out float l)
    {
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        var d = max - min;
        l = (max + min) * 0.5f;
        if (d <= 0f)
        {
            h = 0f;
            s = 0f;
            return;
        }
        s = d / (1f - MathF.Abs(2f * l - 1f));
        if (c.X >= c.Y && c.X >= c.Z)
        {
            h = (c.Y - c.Z) / d % 6f;
        }
        else if (c.Y >= c.Z)
        {
            h = (c.Z - c.X) / d + 2f;
        }
        else
        {
            h = (c.X - c.Y) / d + 4f;
        }
        h = (h * 60f + 360f) % 360f;
    }

    private static Vector4 PlateColor(uint c)
        => new(((c >> 24) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f,
            ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f);

    private static Vector4 ResolveUiForegroundColor(ushort colorKey)
    {
        try
        {
            var rgba = new UIForegroundPayload(colorKey).RGBA;
            return new Vector4(((rgba >> 24) & 0xFF) / 255f,
                ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, 1f);
        }
        catch
        {
            return SpheneUI.Palette.SpheneGold;
        }
    }

    private static Vector4 ResolveUiGlowColor(ushort colorKey)
    {
        try
        {
            var rgba = new UIGlowPayload(colorKey).RGBA;
            return new Vector4(((rgba >> 24) & 0xFF) / 255f,
                ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, 1f);
        }
        catch
        {
            return SpheneUI.Palette.SpheneGold;
        }
    }

    private static ushort UiForegroundPaletteIndexToColorKey(int index)
    {
        return index <= UiForegroundKeyInlineMax
            ? (ushort)index
            : (ushort)(UiForegroundKeyExtendedStart + index - UiForegroundKeyInlineMax - 1);
    }
}
