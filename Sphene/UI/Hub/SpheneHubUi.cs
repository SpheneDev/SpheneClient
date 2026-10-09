using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.CharaDataHub;
using Sphene.UI.Components;
using Sphene.UI.Components.Popup;
using Sphene.UI.Design;
using Sphene.UI.Panels;
using Sphene.UI.Syncshell;
using Sphene.WebAPI;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using System.Numerics;

namespace Sphene.UI.Hub;

/// <summary>
/// Central "Sphene Hub" window: a nav-rail main UI that hosts the user's
/// profile (always-visible avatar in the rail header), syncshell overview
/// with an inline admin panel, the character data hub and mod sharing —
/// one extensible entry point next to the CompactUI.
/// </summary>
internal sealed class SpheneHubUi : WindowMediatorSubscriberBase
{
    private static readonly string[] _hubNavLabels =
        ["My Profile", "Friends", "Syncshells", "Character Data", "Mod Sharing"];

    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly UiFactory _uiFactory;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly EditProfileUi _editProfileUi;
    private readonly CharaDataHubUi _charaDataHubUi;
    private readonly ModSharingUi _modSharingUi;
    private readonly FileDialogManager _fileDialogManager;
    private readonly ImageCropUi _imageCropUi;

    private SpheneHubPage _activePage = SpheneHubPage.Profile;
    private bool _profileEditMode;
    private readonly SpheneUI.Profile.HeroEdit _heroEdit = new();
    private bool _styleUseAccent, _styleUseTop, _styleUseBottom;
    private bool _styleSeeded;
    private bool _styleTopOver, _styleBottomOver;
    private Vector4 _styleAccent = new(0.30f, 0.80f, 0.95f, 1f);
    private Vector4 _styleTop = new(0.30f, 0.80f, 0.95f, 1f);
    private Vector4 _styleBottom = new(0.40f, 0.30f, 0.80f, 1f);
    private ProfileRowFx _styleHoverFx = ProfileRowFx.Glow;
    private ProfileRowFx _styleIdleFx = ProfileRowFx.None;
    private ProfileRowFx _styleCardHoverFx = ProfileRowFx.None;
    private ProfileRowFx _styleCardIdleFx = ProfileRowFx.None;
    private bool _styleCardShimmer = true;
    private float _stylePreviewH;
    private int _stylePreviewTab;
    private byte[] _ownRowImageBytes = [];
    private bool _rowImageError;
    private string? _selectedGroupGid;
    private Pair? _viewingFriend;
    private readonly Dictionary<string, SyncshellAdminUI> _embeddedAdminPanels = new(StringComparer.Ordinal);
    private byte[] _lastAvatarBytes = [];
    private byte[] _lastBannerBytes = [];
    private byte[] _friendAvatarBytes = [];
    private byte[] _friendBannerBytes = [];
    private readonly Dictionary<string, byte[]> _friendListImageBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _friendRowImageBytes = new(StringComparer.Ordinal);
    private List<AreaBoundSyncshellDto>? _areaBoundShells;
    private bool _areaBoundLoading;
    private bool _charaDataVisible;
    private bool _wasOpenBeforeGpose;
    private bool _suppressOpenStatePersist;
    private readonly SpheneConfigService _configService;

    /// <summary>One location in the hub's back/forward history: page plus its detail context.</summary>
    private readonly record struct HubLocation(SpheneHubPage Page, string? GroupGid, Pair? Friend);
    private readonly List<HubLocation> _navBack = [];
    private readonly List<HubLocation> _navForward = [];

    public SpheneHubUi(ILogger<SpheneHubUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        ApiController apiController, PairManager pairManager,
        SpheneProfileManager spheneProfileManager, UiFactory uiFactory,
        DalamudUtilService dalamudUtilService, EditProfileUi editProfileUi,
        CharaDataHubUi charaDataHubUi, ModSharingUi modSharingUi,
        FileDialogManager fileDialogManager, ImageCropUi imageCropUi,
        SpheneConfigService configService)
        : base(logger, mediator, "Sphene Hub###SpheneHubUI", performanceCollectorService)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        _spheneProfileManager = spheneProfileManager;
        _uiFactory = uiFactory;
        _dalamudUtilService = dalamudUtilService;
        _editProfileUi = editProfileUi;
        _charaDataHubUi = charaDataHubUi;
        _modSharingUi = modSharingUi;
        _fileDialogManager = fileDialogManager;
        _imageCropUi = imageCropUi;
        _configService = configService;

        IsOpen = configService.Current.ReopenSpheneHubOnStartup
            && configService.Current.SpheneHubWasOpen;
        Size = new Vector2(1150, 750);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(1024, 560),
            MaximumSize = new(2000, 1600),
        };

        Mediator.Subscribe<OpenSpheneHubMessage>(this, (msg) =>
        {
            if (!string.IsNullOrEmpty(msg.GroupGid))
            {
                NavigateTo(new HubLocation(SpheneHubPage.Syncshells, msg.GroupGid, null));
            }
            else if (msg.Page is { } page)
            {
                SetPage(page);
            }
            IsOpen = true;
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            SetOpenWithoutPersist(false);
            ResetTransientState();
        });
        Mediator.Subscribe<GposeStartMessage>(this, (_) => { _wasOpenBeforeGpose = IsOpen; SetOpenWithoutPersist(false); });
        Mediator.Subscribe<GposeEndMessage>(this, (_) => SetOpenWithoutPersist(_wasOpenBeforeGpose));
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData == null || string.Equals(msg.UserData.UID, _apiController.UID, StringComparison.Ordinal))
            {
                SpheneUI.Media.Evict(AvatarKey);
                SpheneUI.Media.Evict(BannerKey);
            }
            if (msg.UserData == null
                || (_viewingFriend != null
                    && string.Equals(msg.UserData.UID, _viewingFriend.UserData.UID, StringComparison.Ordinal)))
            {
                SpheneUI.Media.Evict(FriendAvatarKey);
                SpheneUI.Media.Evict(FriendBannerKey);
                _friendAvatarBytes = [];
                _friendBannerBytes = [];
            }
        });
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (_suppressOpenStatePersist)
        {
            _suppressOpenStatePersist = false;
            return;
        }
        PersistOpenState(true);
    }

    private void SetOpenWithoutPersist(bool open)
    {
        // Programmatic transitions (disconnect, GPose) must not overwrite the
        // remembered open state — they are not the user's intent. The flag is
        // consumed by whichever OnOpen/OnClose the transition actually fires,
        // regardless of whether the window backend calls it synchronously or
        // deferred.
        if (IsOpen == open)
        {
            return;
        }
        _suppressOpenStatePersist = true;
        IsOpen = open;
    }

    private void PersistOpenState(bool open)
    {
        if (_configService.Current.SpheneHubWasOpen == open)
        {
            return;
        }
        _configService.Current.SpheneHubWasOpen = open;
        // OnOpen/OnClose run outside the window draw try/catch and Save()
        // fires ConfigSave synchronously — a throwing subscriber must not
        // crash the WindowSystem pass.
        try
        {
            _configService.Save();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConfigSave subscriber failed while persisting hub open state");
        }
    }

    protected override FontAwesomeIcon? ChromeIcon => FontAwesomeIcon.Globe;

    protected override IDalamudTextureWrap? ChromeIconTexture => SpheneBrandIcons.SpheneLogo();

    protected override string? ChromeTrailingText
        => !_apiController.IsConnected ? "Offline"
            : _apiController.IsConnectionPaused ? "Paused" : "Online";

    protected override IReadOnlyList<SpheneUI.Chrome.TitleBarButton>? ChromeRightButtons
        => ShowCloseButton
            ? [new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.Times,
                "Close", () => IsOpen = false)]
            : [];

    private string AvatarKey => $"sphenehub:avatar:{_apiController.UID}";
    private string BannerKey => $"sphenehub:banner:{_apiController.UID}";
    private string FriendAvatarKey => $"sphenehub:friendavatar:{_viewingFriend?.UserData.UID}";
    private string FriendBannerKey => $"sphenehub:friendbanner:{_viewingFriend?.UserData.UID}";

    protected override void DrawInternal()
    {
        var theme = SpheneTheme.Current;
        var ownTheme = _spheneProfileManager
            .GetSpheneProfile(new UserData(_apiController.UID)).ProfileTheme.Value;
        var accentMe = ownTheme.AccentColor(theme.AccentCyan);
        var accentCommunity = theme.AccentBlue;
        var accentTools = SpheneUI.Palette.EtherealPurple;

        var railWidth = MathF.Max(SpheneUI.Layout.NavRailWidth(_hubNavLabels), 236f * SpheneUI.Scale);
        var bandHeight = 52f * SpheneUI.Scale;
        var bandMin = new Vector2(ImGui.GetWindowPos().X,
            ImGui.GetWindowPos().Y + SpheneUI.Chrome.TitleBarHeight);
        var railRightX = ImGui.GetCursorScreenPos().X + railWidth;

        // Reserve the full-width header band; the nav rail begins below it and
        // the band itself is painted after EndNavRail so both share one color.
        var reserve = bandMin.Y + bandHeight - ImGui.GetCursorScreenPos().Y;
        if (reserve > 0f)
        {
            ImGui.Dummy(new Vector2(1f, reserve));
        }

        SpheneUI.Layout.BeginNavRail("sphenehub", railWidth);
        ImGuiHelpers.ScaledDummy(0, 6);
        SpheneUI.Layout.NavRailCategory("Me", accentMe);
        if (SpheneUI.Layout.NavRailItem("My Profile",
                _activePage is SpheneHubPage.Profile or SpheneHubPage.ProfileStyle, accentMe))
        {
            SetPage(SpheneHubPage.Profile);
        }
        if (SpheneUI.Layout.NavRailItem("Friends", _activePage == SpheneHubPage.Friends, accentMe))
        {
            SetPage(SpheneHubPage.Friends);
        }
        SpheneUI.Layout.NavRailDivider(accentMe, accentCommunity);
        SpheneUI.Layout.NavRailCategory("Community", accentCommunity);
        if (SpheneUI.Layout.NavRailItem("Syncshells", _activePage == SpheneHubPage.Syncshells, accentCommunity))
        {
            SetPage(SpheneHubPage.Syncshells);
        }
        SpheneUI.Layout.NavRailDivider(accentCommunity, accentTools);
        SpheneUI.Layout.NavRailCategory("Tools", accentTools);
        if (SpheneUI.Layout.NavRailItem("Character Data", _activePage == SpheneHubPage.CharacterData, accentTools))
        {
            SetPage(SpheneHubPage.CharacterData);
        }
        if (SpheneUI.Layout.NavRailItem("Mod Sharing", _activePage == SpheneHubPage.ModSharing, accentTools))
        {
            SetPage(SpheneHubPage.ModSharing);
        }
        SpheneUI.Layout.EndNavRail();

        DrawHubHeaderBand(bandMin, bandHeight, railRightX, accentMe);

        // Embedded admin needs a fixed container: it owns its inner rail
        // and scroll regions; an outer scroll area would fight its layout.
        var adminEmbedded = _activePage == SpheneHubPage.Syncshells
            && CanAdminister(GetSelectedGroup());
        if (SpheneUI.Layout.BeginNavContent("sphenehub", scrollable: !adminEmbedded))
        {
            switch (_activePage)
            {
                case SpheneHubPage.Profile:
                    DrawProfilePage(accentMe);
                    break;
                case SpheneHubPage.ProfileStyle:
                    DrawStylePage(accentMe);
                    break;
                case SpheneHubPage.Friends:
                    DrawFriendsPage(accentCommunity);
                    break;
                case SpheneHubPage.Syncshells:
                    DrawSyncshellsPage();
                    break;
                case SpheneHubPage.CharacterData:
                    _charaDataVisible = true;
                    _charaDataHubUi.DrawEmbedded();
                    break;
                case SpheneHubPage.ModSharing:
                    _modSharingUi.DrawEmbedded();
                    break;
            }
        }
        SpheneUI.Layout.EndNavContent();
    }

    public override void OnClose()
    {
        base.OnClose();
        if (_suppressOpenStatePersist)
        {
            _suppressOpenStatePersist = false;
        }
        else
        {
            PersistOpenState(false);
        }
        ResetTransientState();
    }

    private HubLocation CurrentLocation => new(_activePage, _selectedGroupGid, _viewingFriend);

    private void SetPage(SpheneHubPage page)
        => NavigateTo(new HubLocation(page, null, null));

    /// <summary>User-initiated navigation: pushes the current location onto the back stack.</summary>
    private void NavigateTo(HubLocation target)
    {
        var current = CurrentLocation;
        if (current.Equals(target)) return;
        _navBack.Add(current);
        _navForward.Clear();
        ApplyLocation(target);
    }

    private void NavigateBack()
    {
        if (_navBack.Count == 0) return;
        _navForward.Add(CurrentLocation);
        var target = _navBack[^1];
        _navBack.RemoveAt(_navBack.Count - 1);
        ApplyLocation(target);
    }

    private void NavigateForward()
    {
        if (_navForward.Count == 0) return;
        _navBack.Add(CurrentLocation);
        var target = _navForward[^1];
        _navForward.RemoveAt(_navForward.Count - 1);
        ApplyLocation(target);
    }

    /// <summary>Applies a location — leaving-cleanup of the previous page, then the target state.</summary>
    private void ApplyLocation(HubLocation target)
    {
        if (_charaDataVisible)
        {
            _charaDataVisible = false;
            _charaDataHubUi.OnEmbeddedHidden();
        }
        if (_activePage == SpheneHubPage.Syncshells
            && (target.Page != SpheneHubPage.Syncshells
                || !string.Equals(_selectedGroupGid, target.GroupGid, StringComparison.Ordinal)))
        {
            DisposeEmbeddedAdminPanels();
        }
        if (IsProfileFamily(_activePage) && !IsProfileFamily(target.Page))
        {
            _profileEditMode = false;
            _heroEdit.AddingTag = false;
        }
        var nextFriend = target.Page == SpheneHubPage.Friends ? target.Friend : null;
        if (!ReferenceEquals(nextFriend, _viewingFriend))
        {
            _viewingFriend = nextFriend;
            _friendAvatarBytes = [];
            _friendBannerBytes = [];
        }
        _activePage = target.Page;
        _selectedGroupGid = target.Page == SpheneHubPage.Syncshells ? target.GroupGid : null;
    }

    private static bool IsProfileFamily(SpheneHubPage page)
        => page is SpheneHubPage.Profile or SpheneHubPage.ProfileStyle;

    private void ResetTransientState()
    {
        if (_charaDataVisible)
        {
            _charaDataVisible = false;
            _charaDataHubUi.OnEmbeddedHidden();
        }
        _selectedGroupGid = null;
        _areaBoundShells = null;
        _areaBoundLoading = false;
        _profileEditMode = false;
        _styleSeeded = false;
        _navBack.Clear();
        _navForward.Clear();
        _heroEdit.AddingTag = false;
        ClearViewingFriend();
        DisposeEmbeddedAdminPanels();
        SpheneUI.Fx.Reset();
        SpheneUI.Popups.ResetDropdowns();
        SpheneUI.Layout.ResetExpanders();
    }

    private void ClearViewingFriend()
    {
        _viewingFriend = null;
        _friendAvatarBytes = [];
        _friendBannerBytes = [];
    }

    private void DrawHubHeaderBand(Vector2 bandMin, float bandHeight, float railRightX, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var bandMax = new Vector2(windowPos.X + windowSize.X, bandMin.Y + bandHeight);
        var railBg = SpheneUI.Layout.NavRailBandColor();
        var railBgU32 = SpheneUI.ColorUtils.ToU32(railBg);

        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var wrap = GetAvatarTexture(profile);
        var bannerWrap = GetBannerTexture(profile);

        ImGui.PushClipRect(windowPos, windowPos + windowSize, false);
        drawList.AddRectFilled(bandMin, bandMax, railBgU32);
        // Backdrop: the user's banner image (or avatar as fallback) right of
        // the rail edge; it fades out toward the rail so the transition
        // stays seamless and the nav cluster keeps the pure rail color.
        var backdrop = bannerWrap ?? wrap;
        if (backdrop != null)
        {
            var imgMin = new Vector2(railRightX, bandMin.Y);
            SpheneUI.Media.ImageInRect(drawList, backdrop, imgMin, bandMax,
                ImageFit.Cover, new Vector4(1f, 1f, 1f, 0.85f));
            drawList.AddRectFilledMultiColor(imgMin, bandMax,
                railBgU32,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(railBg, 0.5f)),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(railBg, 0.5f)),
                railBgU32);
        }
        // Accent seam along the bottom edge of the band.
        drawList.AddRectFilledMultiColor(
            new Vector2(bandMin.X, bandMax.Y - 1f), bandMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.05f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.05f)));
        ImGui.PopClipRect();

        // toolbar row: the profile card claims the band's left (rail-colored)
        // segment — the sidebar owns that part of the header — and the nav
        // cluster sits right behind it at the content edge. Bell/burger
        // mirror it on the right — one row, one height.
        var btnSize = 30f * scale;
        var btnY = bandMin.Y + (bandHeight - btnSize) * 0.5f;
        var burgerX = bandMax.X - 14f * scale - btnSize;
        var bellX = burgerX - 8f * scale - btnSize;
        DrawHeaderProfileCard(bandMin, bandHeight, railRightX, profile, accent);
        DrawHeaderNavCluster(new Vector2(railRightX + 12f * scale, btnY),
            btnSize, bellX);
        DrawHeaderActionsZone(bandMin, bandHeight, bandMax);
    }

    /// <summary>
    /// The user identity card inside the band's rail-colored left segment
    /// (Discord-style, but top-docked): circular avatar with presence badge
    /// plus name/status, hugging the rail width. Clicking opens the profile
    /// page. Lives in the parent window — no child covers the band area.
    /// </summary>
    private void DrawHeaderProfileCard(Vector2 bandMin, float bandHeight, float railRightX,
        SpheneProfileData profile, Vector4 accent)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var cardMin = new Vector2(bandMin.X + 4f * scale, bandMin.Y + 4f * scale);
        var cardMax = new Vector2(railRightX - 4f * scale, bandMin.Y + bandHeight - 4f * scale);
        var cardW = cardMax.X - cardMin.X;
        var cardH = cardMax.Y - cardMin.Y;

        // same scrim + hairline language as the breadcrumb pill
        drawList.AddRectFilled(cardMin, cardMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                new Vector4(0.02f, 0.02f, 0.04f, 1f), 0.55f)), 10f * scale);

        ImGui.SetCursorScreenPos(cardMin);
        var clicked = ImGui.InvisibleButton("##hub-header-profile", new Vector2(cardW, cardH));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            drawList.AddRectFilled(cardMin, cardMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.06f)),
                10f * scale);
        }
        drawList.AddRect(cardMin, cardMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.10f)),
            10f * scale);
        if (clicked)
        {
            SetPage(SpheneHubPage.Profile);
        }

        var avatarSize = 34f * scale;
        var avatarPos = new Vector2(cardMin.X + 6f * scale,
            cardMin.Y + (cardH - avatarSize) * 0.5f);
        drawList.AddCircleFilled(avatarPos + new Vector2(avatarSize * 0.5f),
            avatarSize * 0.5f + 2f * scale,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.30f)), 0);
        ImGui.SetCursorScreenPos(avatarPos);
        SpheneUI.Media.Avatar(GetAvatarTexture(profile), avatarSize);

        var connected = _apiController.IsConnected;
        var paused = _apiController.IsConnectionPaused;
        var connColor = !connected ? SpheneUI.Palette.NetworkDisconnected
            : paused ? SpheneUI.Palette.NetworkWarning
            : SpheneUI.Palette.NetworkActive;
        var connText = !connected ? "Offline" : paused ? "Paused" : "Online";
        SpheneUI.Media.PresenceDot(drawList, avatarPos, avatarSize, connColor);

        var textX = avatarPos.X + avatarSize + 9f * scale;
        var textMaxW = MathF.Max(30f * scale, cardMax.X - textX - 8f * scale);
        var lineH = ImGui.GetTextLineHeight();
        var lineGap = 2f * scale;
        var nameY = cardMin.Y + (cardH - lineH * 2f - lineGap) * 0.5f;

        var playerName = _dalamudUtilService.GetPlayerName();
        var displayName = string.IsNullOrEmpty(playerName)
            || string.Equals(playerName, "--", StringComparison.Ordinal)
            ? _apiController.UID : playerName;
        var statusLine = HeaderStatusLine(profile, out var statusColor);
        var statusText = Ellipsize(statusLine, textMaxW);
        drawList.AddText(new Vector2(textX, nameY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
            Ellipsize(displayName, textMaxW));
        drawList.AddText(new Vector2(textX, nameY + lineH + lineGap),
            SpheneUI.ColorUtils.ToU32(statusColor), statusText);

        if (hovered)
        {
            var tip = $"{connText} — UID: {_apiController.UID}";
            if (!string.Equals(statusText, statusLine, StringComparison.Ordinal))
            {
                tip = $"{statusLine}\n{tip}";
            }
            SpheneUI.AttachTooltip(tip + " — open your profile page");
        }
    }

    private static string HeaderStatusLine(SpheneProfileData profile, out Vector4 color)
    {
        if (!string.IsNullOrWhiteSpace(profile.StatusMessage))
        {
            color = SpheneUI.Colors.TextSecondary;
            return profile.StatusMessage;
        }
        if (!string.IsNullOrWhiteSpace(profile.Description)
            && !string.Equals(profile.Description, SpheneProfileManager.NoDescriptionPlaceholder,
                StringComparison.Ordinal))
        {
            color = SpheneUI.Colors.TextMuted;
            return profile.Description;
        }
        color = SpheneUI.Colors.TextMuted;
        return "Set a status in your profile";
    }

    private void DrawHeaderActionsZone(Vector2 bandMin, float bandHeight, Vector2 bandMax)
    {
        var scale = SpheneUI.Scale;
        var btnSize = 30f * scale;
        var gap = 8f * scale;
        var burgerX = bandMax.X - 14f * scale - btnSize;
        var bellX = burgerX - gap - btnSize;
        var btnY = bandMin.Y + (bandHeight - btnSize) * 0.5f;

        var pending = _pairManager.UnseenInboundIndividualPairRequestCount;
        ImGui.SetCursorScreenPos(new Vector2(bellX, btnY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Bell, btnSize, null, null, btnSize))
        {
            Mediator.Publish(new OpenPairRequestsUiMessage());
        }
        SpheneUI.AttachTooltip(pending > 0
            ? $"{pending} pending pair request(s)"
            : "Pair requests");
        SpheneUI.Chrome.DrawTitleBarButtonBadge(ImGui.GetItemRectMin(), btnSize, pending, scale);

        ImGui.SetCursorScreenPos(new Vector2(burgerX, btnY));
        if (SpheneUI.Icons.Button(FontAwesomeIcon.Bars, btnSize, null, null, btnSize))
        {
            ImGui.SetNextWindowPos(new Vector2(burgerX + btnSize, btnY + btnSize + 2f * scale),
                ImGuiCond.Always, new Vector2(1f, 0f));
            ImGui.OpenPopup("##hub-header-menu");
        }
        SpheneUI.AttachTooltip("Menu");

        if (ImGui.BeginPopup("##hub-header-menu"))
        {
            if (ImGui.MenuItem(pending > 0
                    ? $"Pair Requests ({pending})###hub-menu-requests"
                    : "Pair Requests###hub-menu-requests"))
            {
                Mediator.Publish(new OpenPairRequestsUiMessage());
            }
            if (ImGui.MenuItem("Edit Profile###hub-menu-profile"))
            {
                SetPage(SpheneHubPage.Profile);
                _profileEditMode = true;
                SeedHeroEditBuffers(_spheneProfileManager.GetSpheneProfile(
                    new UserData(_apiController.UID)));
            }
            ImGui.Separator();
            var reopenOnStart = _configService.Current.ReopenSpheneHubOnStartup;
            if (ImGui.MenuItem("Reopen Hub on startup###hub-menu-reopen", string.Empty, reopenOnStart))
            {
                _configService.Current.ReopenSpheneHubOnStartup = !reopenOnStart;
                _configService.Save();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Settings###hub-menu-settings"))
            {
                Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
            }
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// Back/forward buttons plus the breadcrumb pill, anchored right of the
    /// profile zone's content edge — replaces the former centered page title.
    /// </summary>
    private void DrawHeaderNavCluster(Vector2 pos, float btnSize, float rightLimitX)
    {
        var scale = SpheneUI.Scale;
        ImGui.SetCursorScreenPos(pos);
        using (ImRaii.Disabled(_navBack.Count == 0))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowLeft, btnSize, null, null, btnSize))
            {
                NavigateBack();
            }
        }
        SpheneUI.AttachTooltip("Back");

        ImGui.SetCursorScreenPos(pos + new Vector2(btnSize + 4f * scale, 0f));
        using (ImRaii.Disabled(_navForward.Count == 0))
        {
            if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowRight, btnSize, null, null, btnSize))
            {
                NavigateForward();
            }
        }
        SpheneUI.AttachTooltip("Forward");

        // breadcrumb continues on the same toolbar row, vertically centered
        // on the buttons
        var pillH = 26f * scale;
        var crumbX = pos.X + btnSize * 2f + 4f * scale + 10f * scale;
        DrawBreadcrumbPill(new Vector2(crumbX, pos.Y + (btnSize - pillH) * 0.5f),
            MathF.Max(0f, rightLimitX - crumbX - 8f * scale));
    }

    /// <summary>
    /// The current location as a pill ("Page › detail") — a dark scrim keeps it
    /// legible over bright banner art and dark surfaces alike; the parent
    /// segment is clickable and navigates one level up.
    /// </summary>
    private void DrawBreadcrumbPill(Vector2 pillMin, float maxWidth)
    {
        var scale = SpheneUI.Scale;
        var drawList = ImGui.GetWindowDrawList();
        var pillH = 26f * scale;
        var padX = 12f * scale;
        var sepGap = 7f * scale;
        const string sep = "\u203A";

        string? parentLabel = null;
        var parentTarget = default(HubLocation);
        var currentLabel = PageCrumbLabel(_activePage);
        if (_activePage == SpheneHubPage.ProfileStyle)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Profile);
            parentTarget = new HubLocation(SpheneHubPage.Profile, null, null);
        }
        else if (_activePage == SpheneHubPage.Friends && _viewingFriend != null)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Friends);
            parentTarget = new HubLocation(SpheneHubPage.Friends, null, null);
            currentLabel = _viewingFriend.UserData.AliasOrUID;
        }
        else if (_activePage == SpheneHubPage.Syncshells && GetSelectedGroup() is { } group)
        {
            parentLabel = PageCrumbLabel(SpheneHubPage.Syncshells);
            parentTarget = new HubLocation(SpheneHubPage.Syncshells, null, null);
            currentLabel = group.GroupAliasOrGID;
        }

        var sepW = ImGui.CalcTextSize(sep).X + sepGap * 2f;
        var maxTextW = MathF.Max(0f, maxWidth - padX * 2f);
        var parentW = parentLabel != null ? ImGui.CalcTextSize(parentLabel).X + sepW : 0f;
        var currentText = Ellipsize(currentLabel, MathF.Max(24f * scale, maxTextW - parentW));
        if (ImGui.CalcTextSize(currentText).X + parentW > maxTextW)
        {
            // still too wide — drop the parent segment before shrinking further
            parentLabel = null;
            parentW = 0f;
            currentText = Ellipsize(currentLabel, maxTextW);
        }

        var pillW = padX * 2f + parentW + ImGui.CalcTextSize(currentText).X;
        var pillMax = pillMin + new Vector2(pillW, pillH);
        drawList.AddRectFilled(pillMin, pillMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                new Vector4(0.02f, 0.02f, 0.04f, 1f), 0.55f)), pillH * 0.5f);
        drawList.AddRect(pillMin, pillMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.10f)),
            pillH * 0.5f);

        var tx = pillMin.X + padX;
        var ty = pillMin.Y + (pillH - ImGui.GetTextLineHeight()) * 0.5f;
        if (parentLabel != null)
        {
            var pw = ImGui.CalcTextSize(parentLabel).X;
            ImGui.SetCursorScreenPos(new Vector2(tx, pillMin.Y));
            var parentClicked = ImGui.InvisibleButton("##hubcrumb", new Vector2(pw, pillH));
            var parentHovered = ImGui.IsItemHovered();
            drawList.AddText(new Vector2(tx, ty),
                SpheneUI.ColorUtils.ToU32(parentHovered
                    ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextSecondary),
                parentLabel);
            if (parentHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            if (parentClicked)
            {
                NavigateTo(parentTarget);
            }
            tx += pw;
            drawList.AddText(new Vector2(tx + sepGap, ty),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), sep);
            tx += sepW;
        }
        drawList.AddText(new Vector2(tx, ty),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), currentText);
    }

    private static string PageCrumbLabel(SpheneHubPage page) => page switch
    {
        SpheneHubPage.Profile => "My Profile",
        SpheneHubPage.Friends => "Friends",
        SpheneHubPage.Syncshells => "Syncshells",
        SpheneHubPage.CharacterData => "Character Data",
        SpheneHubPage.ModSharing => "Mod Sharing",
        SpheneHubPage.ProfileStyle => "Style",
        _ => "Hub",
    };

    private static string Ellipsize(string text, float maxWidth)
        => SpheneUI.Text.Ellipsize(text, maxWidth);

    private IDalamudTextureWrap? GetAvatarTexture(SpheneProfileData profile)
    {
        if (!profile.ImageData.Value.SequenceEqual(_lastAvatarBytes))
        {
            _lastAvatarBytes = profile.ImageData.Value;
            SpheneUI.Media.Evict(AvatarKey);
        }
        return SpheneUI.Media.Texture(AvatarKey, _lastAvatarBytes);
    }

    private IDalamudTextureWrap? GetBannerTexture(SpheneProfileData profile)
    {
        var data = profile.BannerImageData.Value;
        if (data.Length == 0)
        {
            return null;
        }
        if (!data.SequenceEqual(_lastBannerBytes))
        {
            _lastBannerBytes = data;
            SpheneUI.Media.Evict(BannerKey);
        }
        return SpheneUI.Media.Texture(BannerKey, _lastBannerBytes);
    }

    private static IDalamudTextureWrap? GetFriendTexture(string key, byte[] data, ref byte[] last)
    {
        if (data.Length == 0)
        {
            return null;
        }
        if (!data.SequenceEqual(last))
        {
            last = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Texture(key, last);
    }

    private SpheneUI.Media.AnimatedImage? GetFriendRowImage(string uid, byte[] data)
    {
        var key = $"friendrow:{uid}";
        if (!_friendRowImageBytes.TryGetValue(uid, out var last)
            || !last.SequenceEqual(data))
        {
            _friendRowImageBytes[uid] = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Animated(key, data);
    }

    private IDalamudTextureWrap? FriendListAvatar(string uid, SpheneProfileData profile)
    {
        var data = profile.ImageData.Value;
        var key = $"hubfriend-list:{uid}";
        if (!_friendListImageBytes.TryGetValue(uid, out var last)
            || !last.SequenceEqual(data))
        {
            _friendListImageBytes[uid] = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Texture(key, data);
    }

    /// <summary>Start page: own community profile hero (theme + banner + stats) followed by the editor.</summary>
    private void DrawProfilePage(Vector4 accent)
    {
        var theme = SpheneTheme.Current;
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        var pfpWrap = GetAvatarTexture(profile);
        var bannerWrap = GetBannerTexture(profile);
        var profileTheme = profile.ProfileTheme.Value;

        var playerName = _dalamudUtilService.GetPlayerName();
        var displayName = string.IsNullOrEmpty(playerName)
            || string.Equals(playerName, "--", StringComparison.Ordinal)
            ? _apiController.UID : playerName;

        var pairCount = _pairManager.DirectPairs.Count;
        var shellCount = _pairManager.GroupPairs.Count;
        var pendingRequests = _pairManager.UnseenInboundIndividualPairRequestCount;

        _heroEdit.Enabled = _profileEditMode;
        _heroEdit.OnAvatarClicked = () => PickAndCropImage(ImageCropUi.CropKind.Avatar);
        _heroEdit.OnBannerClicked = () => PickAndCropImage(ImageCropUi.CropKind.Banner);
        _heroEdit.OnStatusCommitted = v => _ = _apiController.UserSetProfile(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: null, ProfilePictureBase64: null, Description: null, StatusMessage: v));
        _heroEdit.OnDescriptionCommitted = v => _ = _apiController.UserSetProfile(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: null, ProfilePictureBase64: null, Description: v));
        _heroEdit.OnTagsCommitted = v => _ = _apiController.UserSetProfile(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: null, ProfilePictureBase64: null, Description: null, Tags: v));
        _heroEdit.OnNsfwToggled = v => _ = _apiController.UserSetProfile(
            new UserProfileDto(new UserData(_apiController.UID), Disabled: false,
                IsNSFW: v, ProfilePictureBase64: null, Description: null));

        var heroActions = new List<SpheneUI.Profile.HeroAction>
        {
            new(FontAwesomeIcon.Swatchbook, "Style",
                "Accent, banner gradient and list row effects — opens the style page",
                () => NavigateTo(new HubLocation(SpheneHubPage.ProfileStyle, null, null))),
        };
        heroActions.Add(new SpheneUI.Profile.HeroAction(
            _profileEditMode ? FontAwesomeIcon.Check : FontAwesomeIcon.Pen,
            _profileEditMode ? "Done" : "Edit",
            _profileEditMode
                ? "Finish editing your profile"
                : "Edit your profile — click avatar/banner to change, edit status, tags and description inline",
            () =>
            {
                _profileEditMode = !_profileEditMode;
                _heroEdit.AddingTag = false;
                if (_profileEditMode)
                {
                    SeedHeroEditBuffers(profile);
                }
                else
                {
                    _styleSeeded = false;
                }
            }));

        SpheneUI.Profile.Hero(displayName, $"UID: {_apiController.UID}", profile,
            pfpWrap, bannerWrap, profileTheme, accent, theme.AccentBlue,
            isNsfw: profile.IsNSFW, watermark: SpheneBrandIcons.SpheneLogo(),
            actions: heroActions,
            drawStats: _ =>
            {
                if (ImGui.BeginTable("profile_stats", 3, ImGuiTableFlags.SizingStretchSame))
                {
                    ImGui.TableNextColumn();
                    SpheneUI.Profile.StatTile("Friends", FontAwesomeIcon.UserFriends, pairCount,
                        SpheneUI.Palette.NetworkActive, "Open your friends list",
                        () => SetPage(SpheneHubPage.Friends));
                    ImGui.TableNextColumn();
                    SpheneUI.Profile.StatTile("Syncshells", FontAwesomeIcon.Users, shellCount,
                        theme.AccentBlue, "Open your syncshells",
                        () => SetPage(SpheneHubPage.Syncshells));
                    ImGui.TableNextColumn();
                    SpheneUI.Profile.StatTile("Requests", FontAwesomeIcon.Inbox, pendingRequests,
                        pendingRequests > 0 ? SpheneUI.Colors.Warning : SpheneUI.Colors.TextMuted,
                        "Open pair requests", () => Mediator.Publish(new OpenPairRequestsUiMessage()));
                    ImGui.EndTable();
                }
            },
            edit: _heroEdit);
        if (_profileEditMode)
        {
            _editProfileUi.DrawEmbedded();
        }
    }

    private void SeedStyleBuffers(SpheneProfileTheme theme)
    {
        _styleUseAccent = theme.Accent.HasValue;
        _styleUseTop = theme.BannerTop.HasValue;
        _styleUseBottom = theme.BannerBottom.HasValue;
        _styleHoverFx = theme.RowHover;
        _styleIdleFx = theme.RowIdle;
        _styleCardHoverFx = theme.CardHover;
        _styleCardIdleFx = theme.CardIdle;
        _styleCardShimmer = theme.CardShimmer;
        _styleTopOver = theme.BannerTopOver;
        _styleBottomOver = theme.BannerBottomOver;
        if (theme.Accent.HasValue) _styleAccent = SpheneProfileTheme.ToVector4(theme.Accent.Value);
        if (theme.BannerTop.HasValue) _styleTop = SpheneProfileTheme.ToVector4(theme.BannerTop.Value);
        if (theme.BannerBottom.HasValue) _styleBottom = SpheneProfileTheme.ToVector4(theme.BannerBottom.Value);
    }

    /// <summary>The style buffers projected as a live-preview theme.</summary>
    private SpheneProfileTheme PreviewStyleTheme() => new(
        _styleUseAccent ? SpheneProfileTheme.FromVector4(_styleAccent) : null,
        _styleUseTop ? SpheneProfileTheme.FromVector4(_styleTop) : null,
        _styleUseBottom ? SpheneProfileTheme.FromVector4(_styleBottom) : null,
        _styleHoverFx, _styleIdleFx, _styleTopOver, _styleBottomOver,
        _styleCardHoverFx, _styleCardIdleFx, _styleCardShimmer);

    /// <summary>
    /// Dedicated style page reached via the hero's "Style" action or the
    /// "My Profile › Style" breadcrumb. Tab strip + live preview stay pinned
    /// under the header while the settings cards scroll beneath; Save/Reset
    /// persist and return to the previous location.
    /// </summary>
    private void DrawStylePage(Vector4 pageAccent)
    {
        var scale = SpheneUI.Scale;
        var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_apiController.UID));
        if (!_styleSeeded)
        {
            SeedStyleBuffers(profile.ProfileTheme.Value);
            _styleSeeded = true;
        }
        var playerName = _dalamudUtilService.GetPlayerName();
        var displayName = string.IsNullOrEmpty(playerName)
            || string.Equals(playerName, "--", StringComparison.Ordinal)
            ? _apiController.UID : playerName;

        var previewTheme = PreviewStyleTheme();
        var accent = previewTheme.AccentColor(pageAccent);
        var drawList = ImGui.GetWindowDrawList();

        // the header (tab strip + live preview) stays pinned to the visible
        // top while the settings scroll beneath — its flow space is reserved
        // via Dummy and the block itself is drawn LAST so it layers on top
        var headerNatural = ImGui.GetCursorScreenPos();
        var headerW = SpheneUI.Layout.CardAlignedAvailX();
        ImGui.Dummy(new Vector2(0f, _stylePreviewH));

        if (_stylePreviewTab == 0)
        {
            // profile-card context: accent plus everything that touches the
            // banner/card surface — nothing list-row related
            SpheneUI.Layout.SectionCard(() =>
            {
                SpheneUI.Profile.ThemeColorRow("Custom accent color",
                    ref _styleUseAccent, ref _styleAccent);
                SpheneUI.Profile.ThemeColorRow("Custom banner top color",
                    ref _styleUseTop, ref _styleTop);
                if (_styleUseTop)
                {
                    SpheneUI.Controls.Toggle("Over banner image##topover", ref _styleTopOver,
                        indented: true);
                    SpheneUI.AttachTooltip("Blend the top color over your banner image instead of replacing it");
                }
                SpheneUI.Profile.ThemeColorRow("Custom banner bottom color",
                    ref _styleUseBottom, ref _styleBottom);
                if (_styleUseBottom)
                {
                    SpheneUI.Controls.Toggle("Over banner image##bottomover",
                        ref _styleBottomOver, indented: true);
                    SpheneUI.AttachTooltip("Blend the bottom color over your banner image instead of replacing it");
                }
            }, "Appearance", accent,
                "Colors and banner blending your profile card shows to you and paired users.");
            SpheneUI.Layout.SectionCard(() =>
            {
                SpheneUI.Controls.Toggle("Ambient card shimmer##cardshimmer",
                    ref _styleCardShimmer);
                SpheneUI.AttachTooltip(
                    "The card's built-in sheen sweep and border pulse — turn it off for a still card or to let your picked effects stand alone");
                SpheneUI.Profile.FxChips("On hover", ref _styleCardHoverFx,
                    ProfileRowFx.HoverMask, accent);
                SpheneUI.Profile.FxChips("While idle", ref _styleCardIdleFx,
                    ProfileRowFx.IdleMask, accent);
                SpheneUI.Controls.HelpText(
                    "Stack as many as you like — they play on your profile card for everyone who views it.");
            }, "Profile card effects", accent);
        }
        else
        {
            // list-row context: the shared accent plus row effects and the
            // supporter row image — no banner controls here
            SpheneUI.Layout.SectionCard(() =>
            {
                SpheneUI.Profile.ThemeColorRow("Custom accent color",
                    ref _styleUseAccent, ref _styleAccent);
                SpheneUI.Profile.FxChips("On hover", ref _styleHoverFx,
                    ProfileRowFx.HoverMask, accent);
                SpheneUI.Profile.FxChips("While idle", ref _styleIdleFx,
                    ProfileRowFx.IdleMask, accent);
                SpheneUI.Controls.HelpText(
                    "Stack as many as you like — they show on your entry in your friends' lists.");
            }, "List row effects", accent);
            SpheneUI.Layout.SectionCard(() =>
            {
                DrawRowImageSection();
            }, "Row background", accent);
        }

        // footer buttons dock to the panel's right edge
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var footerW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Save, "Save style")
            + SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Undo, "Reset") + spacing;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX()
            + MathF.Max(0f, ImGui.GetContentRegionAvail().X - footerW));
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Save style"))
        {
            var theme = PreviewStyleTheme();
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                Theme: theme.Serialize()));
            _styleSeeded = false;
            LeaveStylePage();
        }
        ImGui.SameLine();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, "Reset"))
        {
            _ = _apiController.UserSetProfile(new UserProfileDto(new UserData(_apiController.UID),
                Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                Theme: ""));
            _styleSeeded = false;
            LeaveStylePage();
        }

        // ── sticky header ── drawn last so it layers over the scrolled
        // settings, pinned between its reserved slot and the visible top
        // edge (never leaving the page's own bounds)
        var flowEnd = ImGui.GetCursorScreenPos();
        var pad = ImGui.GetStyle().WindowPadding;
        var childPos = ImGui.GetWindowPos();
        var bottomLimit = MathF.Max(headerNatural.Y,
            childPos.Y + ImGui.GetWindowSize().Y - pad.Y - _stylePreviewH);
        var stickyY = MathF.Min(MathF.Max(headerNatural.Y, drawList.GetClipRectMin().Y),
            bottomLimit);
        var headerMin = new Vector2(headerNatural.X, stickyY);
        // near-opaque panel tone so scrolled settings cannot bleed through
        var panelBg = Vector4.Lerp(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg],
            Vector4.One, 0.04f);
        panelBg.W = 0.98f;
        drawList.AddRectFilled(headerMin, headerMin + new Vector2(headerW, _stylePreviewH),
            SpheneUI.ColorUtils.ToU32(panelBg));
        ImGui.SetCursorScreenPos(headerMin);

        _stylePreviewTab = SpheneUI.Layout.TabStrip("hubstyle_tabs",
            _styleTabLabels, _stylePreviewTab);
        if (_stylePreviewTab == 0)
        {
            // the complete profile card, live-bound to the unsaved buffers —
            // exactly what viewers see (banner, avatar, name, status, tags)
            SpheneUI.Profile.Hero(displayName, $"UID: {_apiController.UID}", profile,
                GetAvatarTexture(profile), GetBannerTexture(profile), previewTheme,
                pageAccent, SpheneTheme.Current.AccentBlue,
                isNsfw: profile.IsNSFW, watermark: SpheneBrandIcons.SpheneLogo());
        }
        else
        {
            // the REAL friends-list row with your own data — same renderer as
            // the friends list, so the preview is exactly what friends see
            var rowBytes = profile.RowImageData.Value;
            var rowAnim = rowBytes.Length > 0 ? GetOwnRowImage(rowBytes) : null;
            var rowH = 46f * scale;
            var rowPos = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##ownrowprev", new Vector2(headerW, rowH));
            var rowHovered = ImGui.IsItemHovered();
            var rowT = SpheneUI.Fx.HoverT(ImGui.GetID("##ownrowprev"), rowHovered);
            var online = _apiController.IsConnected;
            SpheneUI.Profile.FriendRow(drawList, rowPos, headerW, rowH,
                new SpheneUI.Profile.FriendRowContent(
                    GetAvatarTexture(profile), displayName,
                    online ? "● Online" : "● Offline",
                    online ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted,
                    profile.StatusMessage, profile.IsNSFW),
                accent, rowHovered, rowT, previewTheme, rowAnim, 8f * scale);
            SpheneUI.Controls.HelpText(
                "Hover the row — this is exactly how your entry looks in your friends' lists.");
        }
        ImGui.Dummy(new Vector2(0f, 8f * scale));
        _stylePreviewH = ImGui.GetItemRectMax().Y - stickyY;
        ImGui.SetCursorScreenPos(flowEnd);
    }

    /// <summary>Returns from the style page to wherever the user came from.</summary>
    private void LeaveStylePage()
    {
        if (_navBack.Count > 0)
        {
            NavigateBack();
        }
        else
        {
            SetPage(SpheneHubPage.Profile);
        }
    }

    private static readonly IReadOnlyList<string> _styleTabLabels = ["Profile card", "List row"];

    /// <summary>Supporter-gated animated row background (PNG or GIF) inside the style popup.</summary>
    private void DrawRowImageSection()
    {
        if (!_apiController.IsSupporter)
        {
            SpheneUI.Controls.HelpText(
                "Supporters can set an animated row background (PNG or GIF) shown "
                + "behind their entry in friends' lists — fading out to the right.");
            return;
        }

        var ownData = new UserData(_apiController.UID);
        var rowBytes = _spheneProfileManager.GetSpheneProfile(ownData).RowImageData.Value;
        if (rowBytes.Length > 0)
        {
            SpheneUI.Controls.HelpText(
                "Row image set — preview it live on the \"List row\" tab above.");
        }
        else
        {
            SpheneUI.Controls.HelpText("No row background set — your rows use the theme effects only.");
        }
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FileUpload, "Upload row image"))
        {
            PickRowImage();
        }
        SpheneUI.AttachTooltip(
            "PNG or GIF — you pick the shown strip in the cropper; animated GIFs play for viewers");
        if (rowBytes.Length > 0)
        {
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear"))
            {
                _ = _apiController.UserSetProfile(new UserProfileDto(ownData,
                    Disabled: false, IsNSFW: null, ProfilePictureBase64: null, Description: null,
                    RowImageBase64: ""));
            }
        }
        if (_rowImageError)
        {
            SpheneUI.Layout.Callout(
                "The row image must be a readable PNG or GIF file.",
                FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
        }
    }

    private SpheneUI.Media.AnimatedImage? GetOwnRowImage(byte[] data)
    {
        var key = $"hubstylerow:{_apiController.UID}";
        if (!_ownRowImageBytes.SequenceEqual(data))
        {
            _ownRowImageBytes = data;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Animated(key, data);
    }

    /// <summary>
    /// File-picks a PNG/GIF of any size, then opens the cropper locked to the
    /// row strip's exact aspect — the cropped result (downscaled to 512×128,
    /// GIFs keep their animation) is uploaded.
    /// </summary>
    private void PickRowImage()
    {
        _fileDialogManager.OpenFileDialog("Select row background", ".png,.gif", (success, file) =>
        {
            if (!success) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var format = Image.DetectFormat(bytes);
                    var info = Image.Identify(bytes);
                    if (format == null || info == null || bytes.Length > 32 * 1024 * 1024
                        || (!format.FileExtensions.Contains("png", StringComparer.OrdinalIgnoreCase)
                            && !format.FileExtensions.Contains("gif", StringComparer.OrdinalIgnoreCase)))
                    {
                        _rowImageError = true;
                        _logger.LogDebug("Picked row image failed validation: {File}", file);
                        return;
                    }
                    _rowImageError = false;
                    _imageCropUi.Open(ImageCropUi.CropKind.RowImage, bytes,
                        info.Width, info.Height, cropped =>
                        {
                            _ = _apiController.UserSetProfile(new UserProfileDto(
                                new UserData(_apiController.UID), Disabled: false,
                                IsNSFW: null, ProfilePictureBase64: null, Description: null,
                                RowImageBase64: Convert.ToBase64String(cropped)));
                        });
                }
                catch (Exception ex)
                {
                    _rowImageError = true;
                    _logger.LogWarning(ex, "Failed to read picked row image {File}", file);
                }
            });
        });
    }

    private void SeedHeroEditBuffers(SpheneProfileData profile)
    {
        _heroEdit.StatusBuffer = profile.StatusMessage ?? string.Empty;
        _heroEdit.DescriptionBuffer =
            string.Equals(profile.Description, SpheneProfileManager.NoDescriptionPlaceholder,
                StringComparison.Ordinal)
                ? string.Empty
                : profile.Description;
    }

    /// <summary>File-picks a PNG, then opens the interactive cropper; the cropped result is uploaded.</summary>
    private void PickAndCropImage(ImageCropUi.CropKind kind)
    {
        var title = kind == ImageCropUi.CropKind.Avatar
            ? "Select profile picture" : "Select banner image";
        _fileDialogManager.OpenFileDialog(title, ".png", (success, file) =>
        {
            if (!success) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    var info = Image.Identify(bytes);
                    if (info == null)
                    {
                        _logger.LogDebug("Picked file is not a readable image: {File}", file);
                        return;
                    }
                    _imageCropUi.Open(kind, bytes, info.Width, info.Height, cropped =>
                    {
                        var base64 = Convert.ToBase64String(cropped);
                        _ = _apiController.UserSetProfile(new UserProfileDto(
                            new UserData(_apiController.UID), Disabled: false, IsNSFW: null,
                            ProfilePictureBase64: kind == ImageCropUi.CropKind.Avatar ? base64 : null,
                            Description: null,
                            BannerImageBase64: kind == ImageCropUi.CropKind.Banner ? base64 : null));
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read picked image {File}", file);
                }
            });
        });
    }

    /// <summary>Friends list of paired users; selecting one shows their themed profile inline.</summary>
    private void DrawFriendsPage(Vector4 accent)
    {
        if (_viewingFriend != null && !IsStillPaired(_viewingFriend))
        {
            ApplyLocation(new HubLocation(SpheneHubPage.Friends, null, null));
        }
        if (_viewingFriend is { } friend)
        {
            DrawFriendView(friend, accent);
            return;
        }

        SpheneUI.Text.PageHeader("Friends");
        SpheneUI.Text.MutedWrapped(
            "Everyone you are paired with. Click a friend to view their profile.");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);

        var directPairs = _pairManager.DirectPairs;
        if (directPairs.Count == 0 && _pairManager.PairsWithGroups.Count == 0)
        {
            SpheneUI.Layout.Callout(
                "No paired users yet. Pair with someone or join a syncshell to see them here.",
                FontAwesomeIcon.UserFriends, SpheneUI.Palette.CrystalBlue, title: "No friends yet");
            return;
        }

        var scale = SpheneUI.Scale;
        var rowGap = 2f * scale;
        var drawnAny = DrawFriendSection("Directly Paired", rowGap, directPairs);
        var groupOnly = _pairManager.PairsWithGroups.Keys
            .Where(p => !p.IsDirectlyPaired)
            .OrderBy(p => p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase);
        drawnAny |= DrawFriendSection("Via Syncshells", rowGap, groupOnly);
        if (!drawnAny)
        {
            SpheneUI.Text.Muted("No friends found.");
        }
    }

    private bool DrawFriendSection(string title, float rowGap,
        IEnumerable<Pair> pairs)
    {
        var any = false;
        // Online friends first, then the rest — both passes keep list order.
        foreach (var onlinePass in new[] { true, false })
        {
            foreach (var pair in pairs)
            {
                if (pair.IsOnline != onlinePass)
                {
                    continue;
                }
                if (!any)
                {
                    SpheneUI.Text.Muted(title);
                    any = true;
                }
                DrawFriendRow(pair, rowGap);
            }
        }
        return any;
    }

    private void DrawFriendRow(Pair pair, float rowGap)
    {
        var scale = SpheneUI.Scale;
        var rowH = 46f * scale;
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorScreenPos(pos);
        var rowId = ImGui.GetID($"##friend-{pair.UserData.UID}");
        var clicked = ImGui.InvisibleButton($"##friend-{pair.UserData.UID}", new Vector2(width, rowH));
        var hovered = ImGui.IsItemHovered();
        var t = SpheneUI.Fx.HoverT(rowId, hovered);

        var profile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        // the friend's own accent drives their hover glow and avatar ring
        var friendAccent = profile.ProfileTheme.Value.AccentColor(SpheneUI.Palette.SpheneGold);

        // the full friends-list row via the shared renderer — identical to
        // what the style preview shows for your own entry
        var friendRowAnim = pair.UserData.IsSupporter && profile.RowImageData.Value.Length > 0
            ? GetFriendRowImage(pair.UserData.UID, profile.RowImageData.Value)
            : null;
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var note = pair.GetNote();
        var name = string.IsNullOrEmpty(note)
            ? pair.UserData.AliasOrUID : $"{note} ({pair.UserData.AliasOrUID})";
        var statusColor = pair.IsMutuallyVisible || pair.IsOnline
            ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted;
        var statusText = pair.IsMutuallyVisible ? "Visible" : pair.IsOnline ? "Online" : "Offline";
        SpheneUI.Profile.FriendRow(drawList, pos, width, rowH,
            new SpheneUI.Profile.FriendRowContent(
                FriendListAvatar(pair.UserData.UID, profile), name,
                $"\u25CF {statusText}", statusColor, profile.StatusMessage, profile.IsNSFW),
            friendAccent, hovered, t, profile.ProfileTheme.Value, friendRowAnim,
            8f * scale);

        if (clicked)
        {
            NavigateTo(new HubLocation(SpheneHubPage.Friends, null, pair));
        }
        ImGui.SetCursorScreenPos(pos + new Vector2(0f, rowH + rowGap));
    }

    /// <summary>A friend's profile rendered inside the hub with their theme and banner.</summary>
    private void DrawFriendView(Pair pair, Vector4 accent)
    {
        var theme = SpheneTheme.Current;
        var profile = _spheneProfileManager.GetSpheneProfile(pair.UserData);
        var friendTheme = profile.ProfileTheme.Value;
        var avatar = GetFriendTexture(FriendAvatarKey, profile.ImageData.Value, ref _friendAvatarBytes);
        var banner = GetFriendTexture(FriendBannerKey, profile.BannerImageData.Value, ref _friendBannerBytes);
        var vis = (ProfileVisibility)profile.Visibility;

        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ChevronLeft, "Back to friends"))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Friends, null, null));
            return;
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);

        SpheneUI.Profile.Hero(pair.UserData.AliasOrUID, $"UID: {pair.UserData.UID}", profile,
            avatar, banner, friendTheme, accent, theme.AccentBlue,
            isNsfw: profile.IsNSFW,
            actions:
            [
                new SpheneUI.Profile.HeroAction(FontAwesomeIcon.ExternalLinkAlt, "Popout",
                    "Open this profile in a separate window",
                    () => Mediator.Publish(new ProfileOpenStandaloneMessage(pair)))
            ]);

        SpheneUI.Layout.SectionCard(() =>
        {
            var statusText = pair.IsMutuallyVisible ? "Visible" : pair.IsOnline ? "Online" : "Offline";
            var statusColor = pair.IsMutuallyVisible || pair.IsOnline
                ? SpheneUI.Palette.NetworkActive : SpheneUI.Colors.TextMuted;
            SpheneUI.Text.Colored($"\u25CF {statusText}", statusColor);
            if (pair.IsMutuallyVisible && !string.IsNullOrEmpty(pair.PlayerName))
            {
                ImGui.SameLine();
                SpheneUI.Text.Muted($"({pair.PlayerName})");
            }
            var note = pair.GetNote();
            if (!string.IsNullOrEmpty(note))
            {
                SpheneUI.Text.Muted($"Your note: {note}");
            }
            if (pair.UserPair != null)
            {
                SpheneUI.Text.Plain(pair.IsDirectlyPaired ? "Directly paired" : "Paired via syncshells");
                if (pair.UserPair.OwnPermissions.IsPaused())
                {
                    SpheneUI.Text.Colored("You paused sync to them", SpheneUI.Colors.Warning);
                }
                if (pair.UserPair.OtherPermissions.IsPaused())
                {
                    SpheneUI.Text.Colored("They paused sync to you", SpheneUI.Colors.Warning);
                }
                if (pair.UserPair.Groups.Count > 0)
                {
                    SpheneUI.Text.Muted("Shared syncshells:");
                    foreach (var gid in pair.UserPair.Groups)
                    {
                        var shell = _pairManager.GroupPairs.Keys.FirstOrDefault(k =>
                            k.Group != null && string.Equals(k.Group.GID, gid, StringComparison.Ordinal));
                        if (shell?.Group != null)
                        {
                            SpheneUI.Text.Bullet(shell.GroupAliasOrGID);
                        }
                    }
                }
            }
        }, title: "Connection", accentColor: accent);

        var hiddenHints = new List<string>();
        if (vis.HasFlag(ProfileVisibility.HideProfilePicture)) hiddenHints.Add("profile picture");
        if (vis.HasFlag(ProfileVisibility.HideBanner)) hiddenHints.Add("banner");
        if (vis.HasFlag(ProfileVisibility.HideDescription)) hiddenHints.Add("description");
        if (vis.HasFlag(ProfileVisibility.HideStatusMessage)) hiddenHints.Add("status message");
        if (vis.HasFlag(ProfileVisibility.HideTags)) hiddenHints.Add("tags");
        if (hiddenHints.Count > 0)
        {
            SpheneUI.Layout.Callout(
                $"{pair.UserData.AliasOrUID} hides their {string.Join(", ", hiddenHints)}.",
                FontAwesomeIcon.EyeSlash, SpheneUI.Colors.TextMuted, title: "Private sections");
        }
    }

    private bool IsStillPaired(Pair pair)
    {
        var uid = pair.UserData.UID;
        foreach (var p in _pairManager.DirectPairs)
        {
            if (string.Equals(p.UserData.UID, uid, StringComparison.Ordinal)) return true;
        }
        foreach (var p in _pairManager.PairsWithGroups.Keys)
        {
            if (string.Equals(p.UserData.UID, uid, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private GroupFullInfoDto? GetSelectedGroup()
    {
        if (_selectedGroupGid == null)
        {
            return null;
        }
        return _pairManager.GroupPairs.Keys.FirstOrDefault(k =>
            k.Group != null && string.Equals(k.Group.GID, _selectedGroupGid, StringComparison.Ordinal));
    }

    private bool CanAdminister(GroupFullInfoDto? group)
    {
        if (group == null || group.Group == null)
        {
            return false;
        }
        return string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal)
            || group.GroupUserInfo.IsModerator();
    }

    private SyncshellAdminUI GetOrCreateAdminPanel(GroupFullInfoDto group)
    {
        if (!_embeddedAdminPanels.TryGetValue(group.Group.GID, out var panel))
        {
            panel = _uiFactory.CreateSyncshellAdminUi(group);
            _embeddedAdminPanels[group.Group.GID] = panel;
        }
        return panel;
    }

    private void DisposeEmbeddedAdminPanels()
    {
        foreach (var panel in _embeddedAdminPanels.Values)
        {
            panel.Dispose();
        }
        _embeddedAdminPanels.Clear();
    }

    private void DrawSyncshellsPage()
    {
        var selected = GetSelectedGroup();
        if (_selectedGroupGid != null && selected == null)
        {
            // Group vanished (left/kicked) — drop back to the list without
            // touching the history stacks.
            ApplyLocation(new HubLocation(SpheneHubPage.Syncshells, null, null));
        }

        if (selected != null)
        {
            DrawSyncshellDetail(selected);
            return;
        }

        DrawSyncshellList();
    }

    private void DrawBackRailHeader()
    {
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "All Syncshells",
                ImGui.GetContentRegionAvail().X))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null));
        }
        ImGuiHelpers.ScaledDummy(0, 4);
    }

    private void DrawSyncshellList()
    {
        SpheneUI.Text.PageHeader("Syncshells");

        var atCap = _pairManager.GroupPairs.Count >= _apiController.ServerInfo.MaxGroupsJoinedByUser;
        using (var disabled = ImRaii.Disabled(atCap))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Users, "Join existing Syncshell"))
            {
                Mediator.Publish(new UiToggleMessage(typeof(JoinSyncshellUI)));
            }
            ImGui.SameLine();
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Create Syncshell"))
            {
                Mediator.Publish(new UiToggleMessage(typeof(CreateSyncshellUI)));
            }
        }
        if (atCap)
        {
            ImGui.SameLine();
            SpheneUI.Text.Muted("Syncshell limit reached");
        }

        if (_pairManager.GroupPairs.Count == 0)
        {
            SpheneUI.Layout.Callout(
                "You are not in any syncshell yet. Join one with an invite code or create your own.",
                FontAwesomeIcon.InfoCircle, title: "No Syncshells");
        }

        foreach (var kv in _pairManager.GroupPairs
                     .OrderBy(k => k.Key.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            DrawSyncshellCard(kv.Key, kv.Value);
        }

        DrawAreaBoundSection();
    }

    private void DrawSyncshellCard(GroupFullInfoDto group, List<Pair> members)
    {
        if (group.Group == null)
        {
            return;
        }
        var isOwner = string.Equals(group.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        var isModerator = group.GroupUserInfo.IsModerator();
        // Area-bound syncshells (e.g. city syncshells) carry no member info dict.
        var isAreaBound = group.GroupPairUserInfos.Count == 0;
        var canAdmin = isOwner || isModerator;
        var accent = isOwner ? SpheneUI.Palette.NetworkWarning
            : isModerator ? SpheneUI.Palette.EtherealPurple
            : SpheneUI.Palette.NetworkActive;

        SpheneUI.Layout.SectionCard(() =>
        {
            var actionLabel = canAdmin ? "Manage" : "Open";
            var actionIcon = canAdmin ? FontAwesomeIcon.Cog : FontAwesomeIcon.ArrowRight;
            var actionWidth = SpheneUI.Icons.TextButtonWidth(actionIcon, actionLabel);

            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Plain(group.GroupAliasOrGID);
            ImGui.SameLine();
            if (isOwner)
            {
                SpheneUI.Text.Colored("Owner", SpheneUI.Palette.NetworkWarning);
                ImGui.SameLine();
            }
            else if (isModerator)
            {
                SpheneUI.Text.Colored("Moderator", SpheneUI.Palette.EtherealPurple);
                ImGui.SameLine();
            }
            if (isAreaBound)
            {
                SpheneUI.Text.Colored("Area-bound", SpheneUI.Palette.CrystalBlue);
                ImGui.SameLine();
            }
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - actionWidth);
            if (SpheneUI.Icons.TextButton(actionIcon, actionLabel))
            {
                NavigateTo(new HubLocation(SpheneHubPage.Syncshells, group.Group.GID, null));
            }

            var onlineCount = members.Count(p => p.IsOnline);
            if (isAreaBound)
            {
                SpheneUI.Text.Muted("Joins automatically in its bound area");
            }
            else
            {
                SpheneUI.Text.Muted($"{group.GroupPairUserInfos.Count} members · {onlineCount} online");
            }
        }, id: "hub-shell-" + group.Group.GID, accentColor: accent);
    }

    private void DrawAreaBoundSection()
    {
        EnsureAreaBoundShellsLoaded();
        SpheneUI.Layout.SectionCard(() =>
        {
            if (_areaBoundLoading)
            {
                SpheneUI.Text.Muted("Loading…");
            }
            else if (_areaBoundShells == null || _areaBoundShells.Count == 0)
            {
                SpheneUI.Text.Muted(_apiController.IsConnected
                    ? "No city or area-bound syncshells found."
                    : "Connect to discover city syncshells.");
            }
            else
            {
                var shown = 0;
                foreach (var shell in _areaBoundShells
                             .Where(s => s.Group != null)
                             .OrderBy(s => s.Group.AliasOrGID, StringComparer.OrdinalIgnoreCase))
                {
                    if (shown >= 10)
                    {
                        SpheneUI.Text.Muted($"…and {_areaBoundShells.Count - shown} more");
                        break;
                    }
                    var joined = _pairManager.GroupPairs.Keys.Any(g =>
                        g.Group != null && string.Equals(g.Group.GID, shell.Group.GID, StringComparison.Ordinal));
                    ImGui.AlignTextToFramePadding();
                    SpheneUI.Text.Plain(shell.Group.AliasOrGID);
                    ImGui.SameLine();
                    if (joined)
                    {
                        SpheneUI.Text.Colored("Joined", SpheneUI.Colors.Success);
                    }
                    else
                    {
                        SpheneUI.Text.Muted($"{shell.BoundAreas?.Count ?? 0} bound area(s)");
                    }
                    shown++;
                }
            }
            SpheneUI.Text.MutedWrapped(
                "City and area-bound syncshells join automatically when you enter their bound location.");
        }, title: "City & Area-Bound Syncshells", accentColor: SpheneUI.Palette.CrystalBlue);
    }

    private void EnsureAreaBoundShellsLoaded()
    {
        if (_areaBoundShells != null || _areaBoundLoading || !_apiController.IsConnected)
        {
            return;
        }
        _areaBoundLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _areaBoundShells = await _apiController.GroupGetAreaBoundSyncshells().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load area-bound syncshells");
                _areaBoundShells = [];
            }
            finally
            {
                _areaBoundLoading = false;
            }
        });
    }

    private void DrawSyncshellDetail(GroupFullInfoDto group)
    {
        if (CanAdminister(group))
        {
            var panel = GetOrCreateAdminPanel(group);
            // Back navigation lives inside the embedded admin rail so it
            // visually belongs to the sub-navigation.
            panel.EmbeddedRailHeader = DrawBackRailHeader;
            panel.DrawEmbedded();
            return;
        }

        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "Back to Syncshells"))
        {
            NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null));
            return;
        }

        if (group.Group == null)
        {
            SpheneUI.Text.Muted("Syncshell data unavailable.");
            return;
        }

        var isAreaBound = group.GroupPairUserInfos.Count == 0;
        _pairManager.GroupPairs.TryGetValue(group, out var members);
        var onlineCount = members?.Count(p => p.IsOnline) ?? 0;

        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Big(group.GroupAliasOrGID);
            SpheneUI.Text.Muted("GID: " + group.Group.GID);
            SpheneUI.Text.Plain("Owner: " + group.OwnerAliasOrUID);
            if (isAreaBound)
            {
                SpheneUI.Text.Muted("Area-bound syncshell");
            }
            else
            {
                SpheneUI.Text.Muted($"{group.GroupPairUserInfos.Count} members · {onlineCount} online");
            }

            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Book, "Open Welcome Page"))
            {
                OpenWelcomePageIfAvailable(group);
            }
        }, title: "Syncshell", accentColor: SpheneUI.Palette.CrystalBlue);

        SpheneUI.Layout.SectionCard(() =>
        {
            if (SpheneUI.Controls.Button("Leave Syncshell", danger: true))
            {
                _ = _apiController.GroupLeave(group);
                NavigateTo(new HubLocation(SpheneHubPage.Syncshells, null, null));
            }
        }, title: "Danger Zone", accentColor: SpheneUI.Colors.Danger);
    }

    private void OpenWelcomePageIfAvailable(GroupFullInfoDto group)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var welcomePage = await _apiController.GroupGetWelcomePage(new GroupDto(group.Group))
                    .ConfigureAwait(false);
                if (welcomePage != null && welcomePage.IsEnabled)
                {
                    Mediator.Publish(new OpenWelcomePageMessage(welcomePage, group));
                }
                else
                {
                    Mediator.Publish(new NotificationMessage("Welcome Page",
                        "This syncshell has no welcome page.", NotificationType.Info));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load welcome page for {GID}", group.Group.GID);
            }
        });
    }
}
