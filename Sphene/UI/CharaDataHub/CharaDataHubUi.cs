using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sphene.API.Dto.CharaData;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.CharaData;
using Sphene.Services.CharaData.Models;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using Sphene.UI.Design;
using System.Numerics;

namespace Sphene.UI.CharaDataHub;

internal sealed partial class CharaDataHubUi : WindowMediatorSubscriberBase
{
    /// <summary>Flat sub-pages of the Character Data section — shared by the standalone window rail and the Hub-embedded rail.</summary>
    public enum CharaDataPage
    {
        GposeTogether,
        GposeActors,
        PosesNearby,
        ApplyData,
        McdOnline,
        McdfExport,
        Settings
    }

    private const int maxPoses = 10;
    private readonly CharaDataManager _charaDataManager;
    private readonly CharaDataNearbyManager _charaDataNearbyManager;
    private readonly CharaDataConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly PairManager _pairManager;
    private readonly CharaDataGposeTogetherManager _charaDataGposeTogetherManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly UiSharedService _uiSharedService;
    private CancellationTokenSource? _closalCts;
    private bool _disableUI = false;
    private CancellationTokenSource? _disposalCts;
    private string _exportDescription = string.Empty;
    private string _filterCodeNote = string.Empty;
    private string _filterDescription = string.Empty;
    private Dictionary<string, List<CharaDataMetaInfoExtendedDto>>? _filteredDict;
    private Dictionary<string, (CharaDataFavorite Favorite, CharaDataMetaInfoExtendedDto? MetaInfo, bool DownloadedMetaInfo)> _filteredFavorites = [];
    private bool _filterPoseOnly = false;
    private bool _filterWorldOnly = false;
    private string _gposeTarget = string.Empty;
    private bool _hasValidGposeTarget;
    private nint _gposeTargetAddress;
    private DateTime _lastGposeTargetPoll = DateTime.MinValue;
    private Task<(nint Address, bool CanApply, string Name)>? _gposeTargetPollTask;
    private string _importCode = string.Empty;
    private bool _isHandlingSelf = false;
    private DateTime _lastFavoriteUpdateTime = DateTime.UtcNow;
    private PoseEntryExtended? _nearbyHovered;
    private bool _readExport;
    private string _selectedDtoId = string.Empty;
    private string SelectedDtoId
    {
        get => _selectedDtoId;
        set
        {
            if (!string.Equals(_selectedDtoId, value, StringComparison.Ordinal))
            {
                _charaDataManager.UploadTask = null;
                _selectedDtoId = value;
            }

        }
    }
    private string _selectedSpecificUserIndividual = string.Empty;
    private string _selectedSpecificGroupIndividual = string.Empty;
    private string _sharedWithYouDescriptionFilter = string.Empty;
    private bool _sharedWithYouDownloadableFilter = false;
    private string _sharedWithYouOwnerFilter = string.Empty;
    private string _specificIndividualAdd = string.Empty;
    private string _specificGroupAdd = string.Empty;
    private bool _abbreviateCharaName = false;
    private string? _openComboHybridId = null;
    private (string Id, string? Alias, string AliasOrId, string? Note)[]? _openComboHybridEntries = null;
    private bool _comboHybridUsedLastFrame = false;
    private CharaDataPage _standalonePage = CharaDataPage.GposeTogether;
    private CharaDataPage _embeddedPage = CharaDataPage.GposeTogether;
    private bool _embeddedScope;

    private enum ApplySourceTab { Favorites, Code, Own, Shared, Mcdf }
    private ApplySourceTab _applySourceTab = ApplySourceTab.Favorites;

    private CharaDataPage _activePage
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

    public CharaDataHubUi(ILogger<CharaDataHubUi> logger, SpheneMediator mediator, PerformanceCollectorService performanceCollectorService,
                         CharaDataManager charaDataManager, CharaDataNearbyManager charaDataNearbyManager, CharaDataConfigService configService,
                         UiSharedService uiSharedService, ServerConfigurationManager serverConfigurationManager,
                         DalamudUtilService dalamudUtilService, FileDialogManager fileDialogManager, PairManager pairManager,
                         CharaDataGposeTogetherManager charaDataGposeTogetherManager)
        : base(logger, mediator, "Sphene Character Data Hub###SpheneCharaDataUI", performanceCollectorService)
    {
        SetWindowSizeConstraints();

        _charaDataManager = charaDataManager;
        _charaDataNearbyManager = charaDataNearbyManager;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        _pairManager = pairManager;
        _charaDataGposeTogetherManager = charaDataGposeTogetherManager;
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen |= _configService.Current.OpenSpheneHubOnGposeStart);
        Mediator.Subscribe<OpenCharaDataHubWithFilterMessage>(this, (msg) =>
        {
            IsOpen = true;
            _standalonePage = CharaDataPage.ApplyData;
            _openDataApplicationShared = true;
            _sharedWithYouOwnerFilter = msg.UserData.AliasOrUID;
            UpdateFilteredItems();
        });
    }

    private bool _openDataApplicationShared = false;

    public string CharaName(string name)
    {
        if (_abbreviateCharaName)
        {
            var split = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Single-word names (pets, Brio actors) have no second part — abbreviate what exists.
            if (split.Length >= 2 && split[0].Length > 0 && split[1].Length > 0)
            {
                return split[0][0] + ". " + split[1][0] + ".";
            }
            if (split.Length == 1 && split[0].Length > 0)
            {
                return split[0][0] + ".";
            }
        }

        return name;
    }

    public override void OnClose()
    {
        if (_disableUI)
        {
            IsOpen = true;
            return;
        }

        _closalCts?.Cancel();
        SelectedDtoId = string.Empty;
        _filteredDict = null;
        _sharedWithYouOwnerFilter = string.Empty;
        _importCode = string.Empty;
        _charaDataNearbyManager.ComputeNearbyData = false;
        _charaDataNearbyManager.SetHoveredVfx(null);
        _nearbyHovered = null;
        _openComboHybridId = null;
        _openComboHybridEntries = null;
    }

    public override void OnOpen()
    {
        _closalCts = _closalCts.CancelRecreate();
        _disposalCts = _disposalCts.CancelRecreate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closalCts.CancelDispose();
            _disposalCts.CancelDispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Raised when the active character data page changes through user navigation — args are (previous, next, embedded). The hub folds embedded-surface changes into its own breadcrumb/history.</summary>
    internal event Action<CharaDataPage, CharaDataPage, bool>? SubPageChanged;

    /// <summary>The page the Hub-embedded character data shows — read by the hub for its crumb. The standalone window has its own page state.</summary>
    internal CharaDataPage ActiveSubPageValue => _embeddedPage;

    /// <summary>Label of the embedded character data page for the hub breadcrumb.</summary>
    internal string ActiveCrumbLabel => PageLabel(_embeddedPage);

    /// <summary>Applies a page to the Hub-embedded character data without recording it — used for hub-driven restores.</summary>
    internal void ApplySubPageQuiet(CharaDataPage page)
    {
        _embeddedPage = page;
    }

    private void SetPage(CharaDataPage page)
    {
        if (page == _activePage)
        {
            return;
        }
        var previous = _activePage;
        _activePage = page;
        SubPageChanged?.Invoke(previous, page, _embeddedScope);
    }

    private static string PageLabel(CharaDataPage page) => page switch
    {
        CharaDataPage.GposeTogether => "GPose Together",
        CharaDataPage.GposeActors => "GPose Actors",
        CharaDataPage.PosesNearby => "Poses Nearby",
        CharaDataPage.ApplyData => "Apply Data",
        CharaDataPage.McdOnline => "MCD Online",
        CharaDataPage.McdfExport => "MCDF Export",
        CharaDataPage.Settings => "Settings",
        _ => "GPose Together"
    };

    private void DrawNavItems(
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories, float pinnedCenterY)
    {
        var accentPosing = SpheneUI.Palette.EtherealPurple;
        var accentCreation = SpheneUI.Palette.SpheneGold;
        var accentGeneral = SpheneTheme.Current.AccentBlue;

        void SidebarButton(string label, FontAwesomeIcon icon, CharaDataPage page, Vector4 accent)
        {
            if (SpheneUI.Layout.NavRailSegmentItem(label, icon, _activePage == page, accent))
            {
                SetPage(page);
            }
        }

        SpheneUI.Layout.NavRailCategoryLabel("Posing", accentPosing, pinnedCategories, pinnedCenterY);
        SidebarButton("GPose Together", FontAwesomeIcon.Camera, CharaDataPage.GposeTogether, accentPosing);
        using (ImRaii.Disabled(!_uiSharedService.IsInGpose))
        {
            SidebarButton("GPose Actors", FontAwesomeIcon.Users, CharaDataPage.GposeActors, accentPosing);
        }
        if (!_uiSharedService.IsInGpose)
        {
            SpheneUI.AttachTooltip("Only available in GPose");
        }
        SidebarButton("Poses Nearby", FontAwesomeIcon.StreetView, CharaDataPage.PosesNearby, accentPosing);
        SidebarButton("Apply Data", FontAwesomeIcon.FileImport, CharaDataPage.ApplyData, accentPosing);
        SpheneUI.Layout.NavRailDivider(accentPosing, accentCreation);

        SpheneUI.Layout.NavRailCategoryLabel("Creation", accentCreation, pinnedCategories, pinnedCenterY);
        using (ImRaii.Disabled(_isHandlingSelf))
        {
            SidebarButton("MCD Online", FontAwesomeIcon.Globe, CharaDataPage.McdOnline, accentCreation);
            SidebarButton("MCDF Export", FontAwesomeIcon.FileExport, CharaDataPage.McdfExport, accentCreation);
        }
        if (_isHandlingSelf)
        {
            SpheneUI.AttachTooltip("Cannot use creation tools while having Character Data applied to self.");
        }
        SpheneUI.Layout.NavRailDivider(accentCreation, accentGeneral);

        SpheneUI.Layout.NavRailCategoryLabel("General", accentGeneral, pinnedCategories, pinnedCenterY);
        SidebarButton("Settings", FontAwesomeIcon.Cog, CharaDataPage.Settings, accentGeneral);
    }

    /// <summary>
    /// The character data nav rendered inside the Sphene Hub rail — same
    /// items as the standalone rail plus a pop-out shortcut so the windowed
    /// section stays one click away.
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
                Mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
            }
            SpheneUI.AttachTooltip("Open Character Data in a separate window");
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    /// <summary>
    /// The active character data page rendered inside an already-scrollable
    /// container (the Sphene Hub nav content child) — no own scroll area.
    /// </summary>
    internal void DrawEmbeddedContent()
    {
        var previousScope = _embeddedScope;
        _embeddedScope = true;
        try
        {
            UpdateSharedState();
            DrawBrioWarning();
            using (ImRaii.Disabled(_disableUI))
            {
                DrawDataApplicationStatus();
                DrawPageContent();
            }
            SetWindowSizeConstraints(IsCompactPage(_activePage));
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    /// <summary>Cleans up embedded state when the host window/page hides the data hub content.</summary>
    internal void OnEmbeddedHidden()
    {
        _charaDataNearbyManager.ComputeNearbyData = false;
        _charaDataNearbyManager.SetHoveredVfx(null);
        _nearbyHovered = null;
        _openComboHybridId = null;
        _openComboHybridEntries = null;
    }

    private static bool IsCompactPage(CharaDataPage page)
        => page is CharaDataPage.GposeTogether or CharaDataPage.GposeActors
            or CharaDataPage.PosesNearby or CharaDataPage.ApplyData;

    /// <summary>Per-frame state refresh shared by both surfaces — no drawing, runs before rail and content.</summary>
    private void UpdateSharedState()
    {
        if (!_comboHybridUsedLastFrame)
        {
            _openComboHybridId = null;
            _openComboHybridEntries = null;
        }
        _comboHybridUsedLastFrame = false;

        _disableUI = !(_charaDataManager.UiBlockingComputation?.IsCompleted ?? true);
        if (DateTime.UtcNow.Subtract(_lastFavoriteUpdateTime).TotalSeconds > 2)
        {
            _lastFavoriteUpdateTime = DateTime.UtcNow;
            UpdateFilteredFavorites();
        }

        // The GPose target lookup crosses the framework thread — blocking the
        // UI thread on it every frame stutters. Poll asynchronously instead:
        // the result is consumed on the UI thread once the task completes.
        if (_gposeTargetPollTask?.IsCompletedSuccessfully == true)
        {
            var (address, canApply, name) = _gposeTargetPollTask.Result;
            _gposeTargetAddress = address;
            _hasValidGposeTarget = canApply;
            _gposeTarget = name;
            _gposeTargetPollTask = null;
        }
        else if (_gposeTargetPollTask is { IsCompleted: true, IsCompletedSuccessfully: false })
        {
            _logger.LogDebug(_gposeTargetPollTask.Exception, "Failed to poll GPose target state");
            _gposeTargetAddress = nint.Zero;
            _hasValidGposeTarget = false;
            _gposeTarget = string.Empty;
            _gposeTargetPollTask = null;
        }

        if (_gposeTargetPollTask == null
            && DateTime.UtcNow - _lastGposeTargetPoll > TimeSpan.FromMilliseconds(250))
        {
            _lastGposeTargetPoll = DateTime.UtcNow;
            _gposeTargetPollTask = PollGposeTargetAsync();
        }
        _isHandlingSelf = _charaDataManager.HandledCharaData.Any(c => c.IsSelf);
    }

    /// <summary>
    /// Single round-trip GPose target probe — mirrors CanApplyInGpose but also
    /// reports the object address so the actor list can mark the live target.
    /// </summary>
    private async Task<(nint Address, bool CanApply, string Name)> PollGposeTargetAsync()
    {
        var obj = await _dalamudUtilService.GetGposeTargetGameObjectAsync().ConfigureAwait(false);
        var canApply = _dalamudUtilService.IsInGpose && obj != null
            && obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc;
        return (obj?.Address ?? nint.Zero, canApply,
            canApply ? obj!.Name.TextValue : "Invalid Target");
    }

    private void DrawBrioWarning()
    {
        if (!_charaDataManager.BrioAvailable)
        {
            SpheneUI.Layout.Callout("To use any features related to posing or spawning characters, Brio must be installed.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed, "Brio not available");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }
    }

    private void DrawDataApplicationStatus()
    {
        DisableDisabled(() =>
        {
            if (_charaDataManager.DataApplicationTask != null)
            {
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Muted("Applying data to actor");
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                if (SpheneUI.Controls.SmallButton("Cancel Application"))
                {
                    _charaDataManager.CancelDataApplication();
                }
            }
            if (!string.IsNullOrEmpty(_charaDataManager.DataApplicationProgress))
            {
                SpheneUI.Text.ColoredWrapped(_charaDataManager.DataApplicationProgress, ImGuiColors.DalamudYellow);
            }
            if (_charaDataManager.DataApplicationTask != null)
            {
                SpheneUI.Layout.Callout("During the data application avoid interacting with this actor to prevent potential crashes.",
                    FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
        });
    }

    private void DrawPageContent()
    {
        _charaDataNearbyManager.ComputeNearbyData = _activePage == CharaDataPage.PosesNearby;
        switch (_activePage)
        {
            case CharaDataPage.GposeTogether:
                DrawGposeTogether();
                break;
            case CharaDataPage.GposeActors:
                using (ImRaii.PushId("gposeControls"))
                {
                    DrawGposeControls();
                }
                break;
            case CharaDataPage.PosesNearby:
                using (ImRaii.PushId("nearbyPoseControls"))
                {
                    DrawNearbyPoses();
                }
                break;
            case CharaDataPage.ApplyData:
                using (ImRaii.PushId("applyData"))
                {
                    DrawDataApplication();
                }
                break;
            case CharaDataPage.McdOnline:
                using (ImRaii.PushId("mcdOnline"))
                {
                    DrawMcdOnline();
                }
                break;
            case CharaDataPage.McdfExport:
                using (ImRaii.PushId("mcdfExport"))
                {
                    DrawMcdfExport();
                }
                break;
            case CharaDataPage.Settings:
                using (ImRaii.PushId("settings"))
                {
                    DrawSettings();
                }
                break;
        }
    }

    protected override void DrawInternal()
    {
        UpdateSharedState();

        var railWidth = SpheneUI.Layout.NavRailSegmentedWidth([
            (FontAwesomeIcon.Camera, "GPose Together"),
            (FontAwesomeIcon.Users, "GPose Actors"),
            (FontAwesomeIcon.StreetView, "Poses Nearby"),
            (FontAwesomeIcon.FileImport, "Apply Data"),
            (FontAwesomeIcon.Globe, "MCD Online"),
            (FontAwesomeIcon.FileExport, "MCDF Export"),
            (FontAwesomeIcon.Cog, "Settings")
        ]);
        SpheneUI.Layout.BeginNavRail("charadata", railWidth, backdropPattern: true);
        // Same top offset as the hub rail so the first button sits at the
        // same height in both surfaces.
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        DrawNavItems(null, 0f);
        SpheneUI.Layout.EndNavRail();
        if (SpheneUI.Layout.BeginNavContent("charadata"))
        {
            DrawBrioWarning();
            using (ImRaii.Disabled(_disableUI))
            {
                DrawDataApplicationStatus();
                DrawPageContent();
            }
        }
        SpheneUI.Layout.EndNavContent();
        SetWindowSizeConstraints(IsCompactPage(_activePage));
    }


    private void DrawAddOrRemoveFavorite(CharaDataFullDto dto)
    {
        DrawFavorite(dto.Uploader.UID + ":" + dto.Id);
    }

    private void DrawAddOrRemoveFavorite(CharaDataMetaInfoExtendedDto? dto)
    {
        if (dto == null) return;
        DrawFavorite(dto.FullId);
    }

    private void DrawFavorite(string id)
    {
        bool isFavorite = _configService.Current.FavoriteCodes.TryGetValue(id, out var favorite);
        if (_configService.Current.FavoriteCodes.ContainsKey(id))
        {
            SpheneUI.Icons.Icon(FontAwesomeIcon.Star, ImGuiColors.ParsedGold);
            SpheneUI.AttachTooltip($"Custom Description: {favorite?.CustomDescription ?? string.Empty}" + SpheneUI.TooltipSeparator
                + "Click to remove from Favorites");
        }
        else
        {
            SpheneUI.Icons.Icon(FontAwesomeIcon.Star, ImGuiColors.DalamudGrey);
            SpheneUI.AttachTooltip("Click to add to Favorites");
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            if (isFavorite) _configService.Current.FavoriteCodes.Remove(id);
            else _configService.Current.FavoriteCodes[id] = new();
            _configService.Save();
        }
    }

    private void DrawGposeControls()
    {
        DrawPageTitle("GPose Actors",
            "The actors currently in your GPose — target them, review applied data, or revert.");

        var actors = _dalamudUtilService.GetGposeCharactersFromObjectTable()
            .Where(a => a != null).ToList();
        if (actors.Count == 0)
        {
            SpheneUI.Layout.Callout("No actors in GPose yet — spawn or possess characters to manage them here.",
                FontAwesomeIcon.Users, ImGuiColors.DalamudGrey);
            return;
        }

        var iconBtn = ImGui.GetFrameHeight();
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var rowInset = ImGui.GetStyle().ItemInnerSpacing.X;

        foreach (var actor in actors)
        {
            using var actorId = ImRaii.PushId($"{actor!.Name.TextValue}#{actor.Address}");
            var handled = _charaDataManager.HandledCharaData
                .FirstOrDefault(c => string.Equals(c.Name, actor.Name.TextValue, StringComparison.Ordinal));
            var isCurrentTarget = actor.Address == _gposeTargetAddress;
            var canDelete = !actor.Name.TextValue.StartsWith("Brio ", StringComparison.Ordinal);
            // info + [trash] + undo — only present for handled actors
            var actionCount = handled == null ? 0 : (canDelete ? 3 : 2);

            SpheneUI.Layout.Group(() =>
            {
                var rowStartX = ImGui.GetCursorPosX();
                // Line 1: target button + actor name; actions docked right.
                if (SpheneUI.Controls.IconButton("##target", FontAwesomeIcon.Crosshairs, size: iconBtn))
                {
                    unsafe
                    {
                        DalamudUtilService.GposeTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
                    }
                }
                SpheneUI.AttachTooltip($"Target the GPose character {CharaName(actor.Name.TextValue)}");
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Colored(CharaName(actor.Name.TextValue),
                    isCurrentTarget ? ImGuiColors.HealerGreen : ImGuiColors.DalamudWhite);
                if (isCurrentTarget)
                {
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    SpheneUI.Layout.Badge("TARGET", ImGuiColors.HealerGreen);
                }

                if (handled != null)
                {
                    ImGui.SameLine();
                    // Dock inside the row frame — the group box's inner padding
                    // is the trailing margin, not the label-icon reserve.
                    SpheneUI.Controls.DockToCardEdge(actionCount * iconBtn
                        + (actionCount - 1) * spacingX + rowInset);
                    var id = string.IsNullOrEmpty(handled.MetaInfo.Uploader.UID) ? handled.MetaInfo.Id : handled.MetaInfo.FullId;
                    SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudGrey);
                    SpheneUI.AttachTooltip($"Applied data: {id ?? "No data applied"}");
                    ImGui.SameLine();

                    if (canDelete)
                    {
                        if (SpheneUI.Controls.IconButton("##remove", FontAwesomeIcon.Trash, size: iconBtn))
                        {
                            _charaDataManager.RemoveChara(actor.Name.TextValue);
                        }
                        SpheneUI.AttachTooltip($"Remove character {CharaName(actor.Name.TextValue)}");
                        ImGui.SameLine();
                    }
                    if (SpheneUI.Controls.IconButton("##revert", FontAwesomeIcon.Undo, size: iconBtn))
                    {
                        _charaDataManager.RevertChara(handled);
                    }
                    SpheneUI.AttachTooltip($"Revert applied data from {CharaName(actor.Name.TextValue)}");
                }

                // Line 2: poses stored on the applied data.
                if (handled != null)
                {
                    ImGui.SetCursorPosX(rowStartX + iconBtn + SpheneUI.Spacing.X8.Scaled);
                    DrawPoseData(handled.MetaInfo, actor.Name.TextValue, true);
                }
            }, 5, SpheneUI.Layout.CardAlignedAvailX());

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
    }

    private void DrawDataApplication()
    {
        DrawPageTitle("Apply Character Appearance",
            "Apply character data to a GPose actor — from favorites, codes, your own sets, shared data, or MCDF.");

        // Target status strip — the action context every apply action binds to.
        if (_uiSharedService.IsInGpose)
        {
            var rowInset = ImGui.GetStyle().ItemInnerSpacing.X;
            var hint = _hasValidGposeTarget ? "actions apply to this actor" : "select an actor in GPose";
            var hintW = ImGui.CalcTextSize(hint).X + rowInset;
            SpheneUI.Layout.Group(() =>
            {
                ImGui.AlignTextToFramePadding();
                SpheneUI.Icons.Icon(FontAwesomeIcon.Crosshairs,
                    _hasValidGposeTarget ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                SpheneUI.Text.Muted("GPose Target");
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                SpheneUI.Text.Colored(CharaName(_gposeTarget), SpheneUI.Colors.Bool(_hasValidGposeTarget));
                if (_hasValidGposeTarget)
                {
                    ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
                    SpheneUI.Layout.Badge("ACTIVE", ImGuiColors.HealerGreen);
                }
                ImGui.SameLine();
                SpheneUI.Controls.DockToCardEdge(hintW);
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Muted(hint);
            }, 5f, SpheneUI.Layout.CardAlignedAvailX());
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        if (!_hasValidGposeTarget)
        {
            SpheneUI.Layout.Callout("Applying data requires an active GPose with a selected target actor.",
                FontAwesomeIcon.Crosshairs, ImGuiColors.DalamudYellow);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        if (_openDataApplicationShared)
        {
            _applySourceTab = ApplySourceTab.Shared;
        }

        DrawApplySourceTabs();

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        switch (_applySourceTab)
        {
            case ApplySourceTab.Favorites: DrawApplyTabFavorites(); break;
            case ApplySourceTab.Code: DrawApplyTabCode(); break;
            case ApplySourceTab.Own: DrawApplyTabOwn(); break;
            case ApplySourceTab.Shared: DrawApplyTabShared(); break;
            case ApplySourceTab.Mcdf: DrawApplyTabMcdf(); break;
        }
    }

    /// <summary>
    /// Segmented source switcher — replaces the old ImGui tab bar with the
    /// design-system pill row the other hub pages use.
    /// </summary>
    private void DrawApplySourceTabs()
    {
        var accent = SpheneUI.Palette.EtherealPurple;
        (ApplySourceTab Tab, string Label, FontAwesomeIcon Icon)[] sources =
        [
            (ApplySourceTab.Favorites, "Favorites", FontAwesomeIcon.Star),
            (ApplySourceTab.Code, "Code", FontAwesomeIcon.Key),
            (ApplySourceTab.Own, "Your Own", FontAwesomeIcon.User),
            (ApplySourceTab.Shared, "Shared With You", FontAwesomeIcon.Users),
            (ApplySourceTab.Mcdf, "MCDF", FontAwesomeIcon.File),
        ];

        bool first = true;
        foreach (var (tab, label, icon) in sources)
        {
            if (!first) ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            first = false;
            if (SpheneUI.Controls.SegmentButton(label, icon, accent: accent,
                    active: _applySourceTab == tab))
            {
                _applySourceTab = tab;
            }
        }
    }

    private void DrawApplyTabFavorites()
    {
        using var id = ImRaii.PushId("byFavorite");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        int favFilters = 0;
        if (!string.IsNullOrEmpty(_filterCodeNote)) favFilters++;
        if (!string.IsNullOrEmpty(_filterDescription)) favFilters++;
        if (_filterPoseOnly) favFilters++;
        if (_filterWorldOnly) favFilters++;
        SpheneUI.Layout.Collapsible("Filters", () =>
        {
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
            SpheneUI.Controls.InputTextWithHint("##ownFilter", "Code/Owner Filter", ref _filterCodeNote, 100);
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
            SpheneUI.Controls.InputTextWithHint("##descFilter", "Custom Description Filter", ref _filterDescription, 100);
            SpheneUI.Controls.Toggle("Only show entries with pose data", ref _filterPoseOnly);
            SpheneUI.Controls.Toggle("Only show entries with world data", ref _filterWorldOnly);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            if (SpheneUI.Controls.SmallButton("Reset Filter"))
            {
                _filterCodeNote = string.Empty;
                _filterDescription = string.Empty;
                _filterPoseOnly = false;
                _filterWorldOnly = false;
            }
        }, icon: FontAwesomeIcon.Filter,
            hint: favFilters == 0 ? null : $"{favFilters} active");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        foreach (var favorite in _filteredFavorites.OrderByDescending(k => k.Value.Favorite.LastDownloaded))
        {
            SpheneUI.Layout.Group(() =>
            {
                using var tableid = ImRaii.PushId(favorite.Key);
                var rowInset = ImGui.GetStyle().ItemInnerSpacing.X;
                var iconBtn = ImGui.GetFrameHeight();

                // Line 1: favorite star + code, action cluster docked right.
                ImGui.AlignTextToFramePadding();
                DrawFavorite(favorite.Key);
                ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
                bool metaInfoDownloaded = favorite.Value.DownloadedMetaInfo;
                var metaInfo = favorite.Value.MetaInfo;
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey, !metaInfoDownloaded))
                using (ImRaii.PushColor(ImGuiCol.Text, SpheneUI.Colors.Bool(metaInfo != null), metaInfoDownloaded))
                    SpheneUI.Text.Plain(favorite.Key);

                var statusIconW = SpheneUI.Icons.Size(FontAwesomeIcon.Check).X;
                var clusterW = statusIconW + iconBtn * 3f
                    + ImGui.GetStyle().ItemSpacing.X * 3f + rowInset;
                ImGui.SameLine();
                SpheneUI.Controls.DockToCardEdge(clusterW);
                if (metaInfoDownloaded)
                {
                    SpheneUI.Icons.BoolIcon(metaInfo != null, false);
                    if (metaInfo != null)
                    {
                        SpheneUI.AttachTooltip("Metainfo present" + SpheneUI.TooltipSeparator
                            + $"Last Updated: {metaInfo!.UpdatedDate}" + Environment.NewLine
                            + $"Description: {metaInfo!.Description}" + Environment.NewLine
                            + $"Poses: {metaInfo!.PoseData.Count}");
                    }
                    else
                    {
                        SpheneUI.AttachTooltip("Metainfo could not be downloaded." + SpheneUI.TooltipSeparator
                            + "The data associated with the code is either not present on the server anymore or you have no access to it");
                    }
                }
                else
                {
                    SpheneUI.Icons.Icon(FontAwesomeIcon.QuestionCircle, ImGuiColors.DalamudGrey);
                    SpheneUI.AttachTooltip("Unknown accessibility state. Click the button on the right to refresh.");
                }

                ImGui.SameLine();
                bool isInTimeout = _charaDataManager.IsInTimeout(favorite.Key);
                using (ImRaii.Disabled(isInTimeout))
                {
                    if (SpheneUI.Controls.IconButton("##refresh", FontAwesomeIcon.ArrowsSpin, size: iconBtn))
                    {
                        _charaDataManager.DownloadMetaInfo(favorite.Key, false);
                        UpdateFilteredItems();
                    }
                }
                SpheneUI.AttachTooltip(isInTimeout ? "Timeout for refreshing active, please wait before refreshing again."
                    : "Refresh data for this entry from the Server.");

                ImGui.SameLine();
                GposeMetaInfoAction((meta) =>
                {
                    if (SpheneUI.Controls.IconButton("##apply", FontAwesomeIcon.ArrowRight, size: iconBtn))
                    {
                        _ = _charaDataManager.ApplyCharaDataToGposeTarget(metaInfo!);
                    }
                }, "Apply Character Data to GPose Target", metaInfo, _hasValidGposeTarget, false);
                ImGui.SameLine();
                GposeMetaInfoAction((meta) =>
                {
                    if (SpheneUI.Controls.IconButton("##spawn", FontAwesomeIcon.Plus, size: iconBtn))
                    {
                        _ = _charaDataManager.SpawnAndApplyData(meta!);
                    }
                }, "Spawn Actor with Brio and apply Character Data", metaInfo, _hasValidGposeTarget, true);

                // Line 2: muted owner + last use.
                var uid = favorite.Key;
                var sep = uid.IndexOf(':');
                if (sep > 0) uid = uid[..sep];
                var uidText = metaInfo != null ? metaInfo.Uploader.AliasOrUID : uid;
                var note = _serverConfigurationManager.GetNoteForUid(uid);
                if (note != null)
                {
                    uidText = $"{note} ({uidText})";
                }
                var lastUse = favorite.Value.Favorite.LastDownloaded == DateTime.MaxValue
                    ? "never" : favorite.Value.Favorite.LastDownloaded.ToString("g");
                SpheneUI.Text.Muted($"{uidText} · Last use: {lastUse}");

                var desc = favorite.Value.Favorite.CustomDescription;
                ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
                if (SpheneUI.Controls.InputTextWithHint("##desc", "Custom description for this favorite…", ref desc, 100))
                {
                    favorite.Value.Favorite.CustomDescription = desc;
                    _configService.Save();
                }

                DrawPoseData(metaInfo, _gposeTarget, _hasValidGposeTarget);
            }, 5f, SpheneUI.Layout.CardAlignedAvailX());

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        if (_configService.Current.FavoriteCodes.Count == 0)
        {
            SpheneUI.Layout.Callout(
                "No favorites yet — star entries on the other tabs and they show up here.",
                FontAwesomeIcon.Star, ImGuiColors.DalamudGrey);
        }
    }

    private void DrawApplyTabCode()
    {
        using var id = ImRaii.PushId("byCodeTab");
        DrawHelpFoldout("You can apply character data you have a code for in this tab. Provide the code in it's given format \"OwnerUID:DataId\" into the field below and click on " +
                        "\"Get Info from Code\". This will provide you basic information about the data behind the code. Afterwards select an actor in GPose and press on \"Download and apply to <actor>\"." + Environment.NewLine + Environment.NewLine
                        + "Description: as set by the owner of the code to give you more or additional information of what this code may contain." + Environment.NewLine
                        + "Last Update: the date and time the owner of the code has last updated the data." + Environment.NewLine
                        + "Is Downloadable: whether or not the code is downloadable and applicable. If the code is not downloadable, contact the owner so they can attempt to fix it." + Environment.NewLine + Environment.NewLine
                        + "To download a code the code requires correct access permissions to be set by the owner. If getting info from the code fails, contact the owner to make sure they set their Access Permissions for the code correctly.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
        SpheneUI.Controls.InputTextWithHint("##importCode", "Enter data code — OwnerUID:DataId", ref _importCode, 100);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_importCode)))
        {
            if (SpheneUI.Controls.SmallButton("Get Info from Code"))
            {
                _charaDataManager.DownloadMetaInfo(_importCode);
            }
        }

        var metaDownload = _charaDataManager.DownloadMetaInfoTask;
        if (metaDownload is { IsCompleted: false })
        {
            SpheneUI.Text.Muted("Downloading meta info. Please wait.");
        }
        else if (metaDownload is { IsCompletedSuccessfully: true } && !metaDownload.Result.Success)
        {
            SpheneUI.Layout.Callout(metaDownload.Result.Result,
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
        }
        else if (metaDownload is { IsFaulted: true })
        {
            SpheneUI.Layout.Callout("Meta info download failed — check the log and try again.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
        }

        var metaInfo = _charaDataManager.LastDownloadedMetaInfo;
        if (metaInfo != null)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.SectionCard(() =>
            {
                DrawInfoRow("Description", () => SpheneUI.Text.Wrapped(
                    string.IsNullOrEmpty(metaInfo.Description) ? "-" : metaInfo.Description));
                DrawInfoRow("Last Update",
                    () => SpheneUI.Text.Plain(metaInfo.UpdatedDate.ToLocalTime().ToString()));
                DrawInfoRow("Downloadable",
                    () => SpheneUI.Icons.BoolIcon(metaInfo.CanBeDownloaded, false));
                DrawInfoRow("Poses", () =>
                {
                    if (metaInfo.HasPoses)
                        DrawPoseData(metaInfo, _gposeTarget, _hasValidGposeTarget);
                    else
                        SpheneUI.Icons.BoolIcon(false, false);
                });

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Layout.Separator();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                GposeMetaInfoAction((meta) =>
                {
                    if (SpheneUI.Controls.SegmentButton("Download and Apply", FontAwesomeIcon.ArrowRight,
                            accent: SpheneUI.Palette.EtherealPurple))
                    {
                        _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta!);
                    }
                }, "Apply this Character Data to the current GPose actor", metaInfo, _hasValidGposeTarget, false);
                ImGui.SameLine();
                GposeMetaInfoAction((meta) =>
                {
                    if (SpheneUI.Controls.SmallButton("Download and Spawn"))
                    {
                        _ = _charaDataManager.SpawnAndApplyData(meta!);
                    }
                }, "Spawn a new Brio actor and apply this Character Data", metaInfo, _hasValidGposeTarget, true);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                DrawAddOrRemoveFavorite(metaInfo);
            }, title: "Code Info", accentColor: SpheneUI.Palette.EtherealPurple);
        }
    }

    private void DrawApplyTabOwn()
    {
        using var id = ImRaii.PushId("yourOwnTab");
        DrawHelpFoldout("You can apply character data you created yourself in this tab. If the list is not populated press on \"Download your Character Data\"." + Environment.NewLine + Environment.NewLine
                         + "To create new and edit your existing character data use the \"MCD Online\" tab.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        using (ImRaii.Disabled(_charaDataManager.GetAllDataTask != null
            || (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)))
        {
            if (SpheneUI.Controls.SegmentButton("Download your Character Data", FontAwesomeIcon.ArrowCircleDown))
            {
                var token = _disposalCts?.Token ?? CancellationToken.None;
                _ = _charaDataManager.GetAllData(token);
            }
        }
        if (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
        {
            SpheneUI.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        foreach (var data in _charaDataManager.OwnCharaData.Values)
        {
            var hasMetaInfo = _charaDataManager.TryGetMetaInfo(data.FullId, out var metaInfo);
            if (!hasMetaInfo) continue;
            DrawMetaInfoData(_gposeTarget, _hasValidGposeTarget, metaInfo!, true);
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    private void DrawApplyTabShared()
    {
        using var id = ImRaii.PushId("sharedWithYouTab");
        DrawHelpFoldout("You can apply character data shared with you implicitly in this tab. Shared Character Data are Character Data entries that have \"Sharing\" set to \"Shared\" and you have access through those by meeting the access restrictions, " +
                        "i.e. you were specified by your handle to gain access or are paired with the other user according to the Access Restrictions setting." + Environment.NewLine + Environment.NewLine
                        + "Filter if needed to find a specific entry, then just press on \"Apply to <actor>\" and it will download and apply the Character Data to the currently targeted GPose actor." + Environment.NewLine + Environment.NewLine
                        + "Note: Shared Data of Pairs you have paused will not be shown here.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        DrawUpdateSharedDataButton();

        int activeFilters = 0;
        if (!string.IsNullOrEmpty(_sharedWithYouOwnerFilter)) activeFilters++;
        if (!string.IsNullOrEmpty(_sharedWithYouDescriptionFilter)) activeFilters++;
        if (_sharedWithYouDownloadableFilter) activeFilters++;
        SpheneUI.Layout.Collapsible("Filters##shared", () =>
        {
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
            if (SpheneUI.Controls.InputTextWithHint("##filter", "Filter by handle/note", ref _sharedWithYouOwnerFilter, 30))
            {
                UpdateFilteredItems();
            }
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
            if (SpheneUI.Controls.InputTextWithHint("##filterDesc", "Filter by Description", ref _sharedWithYouDescriptionFilter, 50))
            {
                UpdateFilteredItems();
            }
            if (SpheneUI.Controls.Toggle("Only show downloadable", ref _sharedWithYouDownloadableFilter))
            {
                UpdateFilteredItems();
            }
        }, icon: FontAwesomeIcon.Filter,
            hint: activeFilters == 0 ? null : $"{activeFilters} active");

        if (_filteredDict == null && _charaDataManager.GetSharedWithYouTask == null)
        {
            _filteredDict = _charaDataManager.SharedWithYouData
                .ToDictionary(k =>
                {
                    var note = _serverConfigurationManager.GetNoteForUid(k.Key.UID);
                    if (note == null) return k.Key.AliasOrUID;
                    return $"{note} ({k.Key.AliasOrUID})";
                }, k => k.Value, StringComparer.OrdinalIgnoreCase)
                .Where(k => string.IsNullOrEmpty(_sharedWithYouOwnerFilter) || k.Key.Contains(_sharedWithYouOwnerFilter))
                .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToDictionary();
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        foreach (var entry in _filteredDict ?? [])
        {
            bool isFilteredAndHasToBeOpened = !string.IsNullOrEmpty(_sharedWithYouOwnerFilter)
                && entry.Key.Contains(_sharedWithYouOwnerFilter, StringComparison.OrdinalIgnoreCase)
                && _openDataApplicationShared;
            SpheneUI.Layout.Collapsible($"{entry.Key}##{entry.Key}", () =>
            {
                foreach (var data in entry.Value)
                {
                    DrawMetaInfoData(_gposeTarget, _hasValidGposeTarget, data);
                }
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }, icon: FontAwesomeIcon.User, hint: $"{entry.Value.Count} sets",
                setOpen: isFilteredAndHasToBeOpened);
            if (isFilteredAndHasToBeOpened)
                _openDataApplicationShared = false;
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
    }

    private void DrawApplyTabMcdf()
    {
        using var id = ImRaii.PushId("applyMcdfTab");
        DrawHelpFoldout("You can apply character data shared with you using a MCDF file in this tab." + Environment.NewLine + Environment.NewLine
                        + "Load the MCDF first via the \"Load MCDF\" button which will give you the basic description that the owner has set during export." + Environment.NewLine
                        + "You can then apply it to any handled GPose actor." + Environment.NewLine + Environment.NewLine
                        + "MCDF to share with others can be generated using the \"MCDF Export\" tab at the top.");

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        if (_charaDataManager.LoadedMcdfHeader == null || _charaDataManager.LoadedMcdfHeader.IsCompleted)
        {
            if (SpheneUI.Controls.SmallButton("Load MCDF"))
            {
                _fileDialogManager.OpenFileDialog("Pick MCDF file", ".mcdf", (success, paths) =>
                {
                    if (!success) return;
                    if (paths.FirstOrDefault() is not string path) return;

                    _configService.Current.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty;
                    _configService.Save();

                    _charaDataManager.LoadMcdf(path);
                }, 1, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
            }
            SpheneUI.AttachTooltip("Load MCDF Metadata into memory");
            if ((_charaDataManager.LoadedMcdfHeader?.IsCompletedSuccessfully ?? false))
            {
                var loaded = _charaDataManager.LoadedMcdfHeader.Result.LoadedFile;
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Layout.SectionCard(() =>
                {
                    DrawInfoRow("File", () => SpheneUI.Text.Wrapped(loaded.FilePath));
                    DrawInfoRow("Description", () => SpheneUI.Text.Wrapped(
                        string.IsNullOrEmpty(loaded.CharaFileData.Description) ? "-" : loaded.CharaFileData.Description));

                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    SpheneUI.Layout.Separator();
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                    using (ImRaii.Disabled(!_hasValidGposeTarget))
                    {
                        if (SpheneUI.Controls.SegmentButton("Apply", FontAwesomeIcon.ArrowRight,
                                accent: SpheneUI.Palette.EtherealPurple))
                        {
                            _ = _charaDataManager.McdfApplyToGposeTarget();
                        }
                        SpheneUI.AttachTooltip($"Apply to {_gposeTarget}");
                        ImGui.SameLine();
                        using (ImRaii.Disabled(!_charaDataManager.BrioAvailable))
                        {
                            if (SpheneUI.Controls.SmallButton("Spawn Actor and Apply"))
                            {
                                _charaDataManager.McdfSpawnApplyToGposeTarget();
                            }
                        }
                    }
                }, title: "Loaded MCDF", accentColor: SpheneUI.Palette.EtherealPurple);
            }
            if ((_charaDataManager.LoadedMcdfHeader?.IsFaulted ?? false) || (_charaDataManager.McdfApplicationTask?.IsFaulted ?? false))
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Layout.Callout("Failure to read MCDF file. MCDF file is possibly corrupt. Re-export the MCDF file and try again. "
                    + "If this is your MCDF, try redrawing yourself, wait and re-export the file — if you received it from someone else have them do the same.",
                    FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed);
            }
        }
        else
        {
            SpheneUI.Text.Muted("Loading character…");
        }
    }

    private void DrawMcdfExport()
    {
        DrawPageTitle("MCDF Export",
            "Pack your current character into a portable file to share manually.");

        DrawHelpFoldout("This feature allows you to pack your character into a MCDF file and manually send it to other people. MCDF files can officially only be imported during GPose through Sphene. " +
            "Be aware that the possibility exists that people write unofficial custom exporters to extract the containing data.");

        SpheneUI.Layout.Callout(
            "Exporting your character data into a file gives away your current appearance irrevocably — recipients can share it further without limitations.",
            FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow, "Sharing is permanent");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.SectionCard(() =>
        {
            // Numbered flow — same step language as the mod sharing pages.
            SpheneUI.Text.Muted("1 · Consent");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Controls.Toggle("I understand and want to export my character data", ref _readExport);
            SpheneUI.Controls.HelpText("Required — a MCDF file contains your full appearance and cannot be taken back once shared.");

            if (!_readExport) return;

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            SpheneUI.Text.Muted("2 · Descriptor");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            ImGui.SetNextItemWidth(SpheneUI.Controls.BoundedAvailX());
            SpheneUI.Controls.InputTextWithHint("##exportDesc", "e.g. my summer glam — shown when the file is loaded", ref _exportDescription, 255);

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

            SpheneUI.Text.Muted("3 · Export");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            if (SpheneUI.Controls.SegmentButton("Export Character as MCDF", FontAwesomeIcon.Save,
                    accent: SpheneUI.Palette.EtherealPurple))
            {
                string defaultFileName = string.IsNullOrEmpty(_exportDescription)
                    ? "export.mcdf"
                    : string.Join('_', $"{_exportDescription}.mcdf".Split(Path.GetInvalidFileNameChars()));
                _uiSharedService.FileDialogManager.SaveFileDialog("Export Character to file", ".mcdf", defaultFileName, ".mcdf", (success, path) =>
                {
                    if (!success) return;

                    _configService.Current.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty;
                    _configService.Save();

                    _charaDataManager.SaveSpheneCharaFile(_exportDescription, path);
                    _exportDescription = string.Empty;
                }, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Text.MutedWrapped("For best results, have everything you want shared equipped and redraw your character before exporting.");
        }, title: "Export your character", accentColor: SpheneUI.Palette.EtherealPurple);
    }

    private void DrawMetaInfoData(string selectedGposeActor, bool hasValidGposeTarget, CharaDataMetaInfoExtendedDto data, bool canOpen = false)
    {
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        using var entryId = ImRaii.PushId(data.FullId);

        var rowInset = ImGui.GetStyle().ItemInnerSpacing.X;
        var iconBtn = ImGui.GetFrameHeight();
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        SpheneUI.Layout.Group(() =>
        {
            // Line 1: favorite star + full id, meta actions docked right.
            ImGui.AlignTextToFramePadding();
            DrawAddOrRemoveFavorite(data);
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            SpheneUI.Text.Colored(data.FullId, SpheneUI.Colors.Bool(data.CanBeDownloaded));
            if (!data.CanBeDownloaded)
            {
                SpheneUI.AttachTooltip("This data is incomplete on the server and cannot be downloaded. Contact the owner so they can fix it. If you are the owner, review the data in the MCD Online tab.");
            }

            ImGui.SameLine();
            SpheneUI.Controls.DockToCardEdge(
                SpheneUI.Icons.Size(FontAwesomeIcon.Calendar).X + iconBtn * 2f
                + spacingX * 2f + rowInset);
            SpheneUI.Icons.Icon(FontAwesomeIcon.Calendar, ImGuiColors.DalamudGrey);
            SpheneUI.AttachTooltip($"Last Update: {data.UpdatedDate}");

            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (SpheneUI.Controls.IconButton("##apply", FontAwesomeIcon.ArrowRight, size: iconBtn))
                {
                    _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta!);
                }
            }, $"Apply Character data to {CharaName(selectedGposeActor)}", data, hasValidGposeTarget, false);
            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (SpheneUI.Controls.IconButton("##spawn", FontAwesomeIcon.Plus, size: iconBtn))
                {
                    _ = _charaDataManager.SpawnAndApplyData(meta!);
                }
            }, "Spawn and Apply Character data", data, hasValidGposeTarget, true);

            // Line 2: description + optional editor link.
            if (string.IsNullOrEmpty(data.Description))
            {
                SpheneUI.Text.Muted("No description set");
            }
            else
            {
                SpheneUI.Text.MutedWrapped(data.Description);
            }

            if (canOpen)
            {
                using (ImRaii.Disabled(_isHandlingSelf))
                {
                    if (SpheneUI.Controls.SmallButton("Open in MCD Online Editor"))
                    {
                        SelectedDtoId = data.Id;
                        SetPage(CharaDataPage.McdOnline);
                    }
                }
                if (_isHandlingSelf)
                {
                    SpheneUI.AttachTooltip("Cannot use MCD Online while having Character Data applied to self.");
                }
            }

            DrawPoseData(data, selectedGposeActor, hasValidGposeTarget);
        }, 5f, SpheneUI.Layout.CardAlignedAvailX());
    }


    private void DrawPoseData(CharaDataMetaInfoExtendedDto? metaInfo, string actor, bool hasValidGposeTarget)
    {
        if (metaInfo == null || !metaInfo.HasPoses) return;

        bool isInGpose = _uiSharedService.IsInGpose;
        var start = ImGui.GetCursorPosX();
        foreach (var item in metaInfo.PoseExtended)
        {
            if (!item.HasPoseData) continue;

            float DrawIcon(float s)
            {
                ImGui.SetCursorPosX(s);
                var posX = ImGui.GetCursorPosX();
                SpheneUI.Icons.Icon(item.HasWorldData ? FontAwesomeIcon.Circle : FontAwesomeIcon.Running);
                if (item.HasWorldData)
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(posX);
                    using var col = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.WindowBg));
                    SpheneUI.Icons.Icon(FontAwesomeIcon.Running);
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(posX);
                    SpheneUI.Icons.Icon(FontAwesomeIcon.Running);
                }
                ImGui.SameLine();
                return ImGui.GetCursorPosX();
            }

            string tooltip = string.IsNullOrEmpty(item.Description) ? "No description set" : "Pose Description: " + item.Description;
            if (!isInGpose)
            {
                start = DrawIcon(start);
                SpheneUI.AttachTooltip(tooltip + SpheneUI.TooltipSeparator + (item.HasWorldData ? GetWorldDataTooltipText(item) + SpheneUI.TooltipSeparator + "Click to show on Map" : string.Empty));
                if (item.HasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _dalamudUtilService.SetMarkerAndOpenMap(item.Position, item.Map);
                }
            }
            else
            {
                tooltip += SpheneUI.TooltipSeparator + $"Left Click: Apply this pose to {CharaName(actor)}";
                if (item.HasWorldData) tooltip += Environment.NewLine + $"CTRL+Right Click: Apply world position to {CharaName(actor)}."
                        + SpheneUI.TooltipSeparator + "!!! CAUTION: Applying world position will likely yeet this actor into nirvana. Use at your own risk !!!";
                GposePoseAction(() =>
                {
                    start = DrawIcon(start);
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    {
                        _ = _charaDataManager.ApplyPoseData(item, actor);
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && UiSharedService.CtrlPressed())
                    {
                        _ = _charaDataManager.ApplyWorldDataToTarget(item, actor);
                    }
                }, tooltip, hasValidGposeTarget);
                ImGui.SameLine();
            }
        }
        if (metaInfo.PoseExtended.Any()) ImGui.NewLine();
    }

    private void DrawSettings()
    {
        DrawPageTitle("Settings", "Character data hub preferences.");
        var accent = SpheneUI.Palette.EtherealPurple;

        SpheneUI.Layout.SectionCard(() =>
        {
            bool openInGpose = _configService.Current.OpenSpheneHubOnGposeStart;
            if (SpheneUI.Controls.Toggle("Open Character Data Hub when GPose loads", ref openInGpose))
            {
                _configService.Current.OpenSpheneHubOnGposeStart = openInGpose;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("Automatically open the import menu when loading into GPose. If unchecked you can open the menu manually with /sphene gpose.");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

            bool downloadDataOnConnection = _configService.Current.DownloadMcdDataOnConnection;
            if (SpheneUI.Controls.Toggle("Download MCD Online data on connecting", ref downloadDataOnConnection))
            {
                _configService.Current.DownloadMcdDataOnConnection = downloadDataOnConnection;
                _configService.Save();
            }
            SpheneUI.Controls.HelpText("Automatically download MCD Online data (Your Own and Shared with You) once a connection is established.");
        }, title: "Behavior", accentColor: accent);

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.SectionCard(() =>
        {
            bool showHelpTexts = _configService.Current.ShowHelpTexts;
            if (SpheneUI.Controls.Toggle("Show \"What is this? (Explanation / Help)\" foldouts", ref showHelpTexts))
            {
                _configService.Current.ShowHelpTexts = showHelpTexts;
                _configService.Save();
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

            SpheneUI.Controls.Toggle("Abbreviate character names", ref _abbreviateCharaName);
            SpheneUI.Controls.HelpText("Abbreviate displayed names (e.g. F. L.). Not persistent — resets on restart.");
        }, title: "Display", accentColor: accent);

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.SectionCard(() =>
        {
            var lastExport = _configService.Current.LastSavedCharaDataLocation;
            SpheneUI.Text.Muted("Last export folder");
            SpheneUI.Text.ColoredWrapped(string.IsNullOrEmpty(lastExport) ? "Not set" : lastExport,
                string.IsNullOrEmpty(lastExport) ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudWhite);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            using (ImRaii.Disabled(string.IsNullOrEmpty(lastExport)))
            {
                if (SpheneUI.Controls.SmallButton("Clear Last Export Folder"))
                {
                    _configService.Current.LastSavedCharaDataLocation = string.Empty;
                    _configService.Save();
                }
            }
            SpheneUI.Controls.HelpText("Use this if the Load or Save MCDF file dialog does not open.");
        }, title: "File Dialogs", accentColor: accent);
    }

    private void DrawHelpFoldout(string text)
    {
        if (_configService.Current.ShowHelpTexts)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Collapsible("What is this? (Explanation / Help)", () =>
            {
                SpheneUI.Text.MutedWrapped(text);
            }, icon: FontAwesomeIcon.QuestionCircle);
        }
    }

    /// <summary>Muted label column plus an inline value — the shared info-row
    /// look for the hub's detail blocks.</summary>
    private static void DrawInfoRow(string label, Action drawValue)
    {
        SpheneUI.Text.Muted(label);
        ImGui.SameLine(140f * SpheneUI.Scale);
        drawValue();
    }

    /// <summary>
    /// Uniform page title for every character data page: embedded pages draw
    /// the compact one-line intro (the hub breadcrumb carries the title),
    /// the standalone window draws the full page header.
    /// </summary>
    private void DrawPageTitle(string title, string intro)
    {
        if (_embeddedScope)
        {
            SpheneUI.Text.PageIntro(intro);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }
        else
        {
            SpheneUI.Text.PageHeader(title, intro);
        }
    }

    private void DisableDisabled(Action drawAction)
    {
        if (_disableUI) ImGui.EndDisabled();
        drawAction();
        if (_disableUI) ImGui.BeginDisabled();
    }
}
