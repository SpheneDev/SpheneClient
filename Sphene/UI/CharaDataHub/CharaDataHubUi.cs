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
            var split = name.Split(" ");
            return split[0].First() + ". " + split[1].First() + ".";
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

        (_hasValidGposeTarget, _gposeTarget) = _charaDataManager.CanApplyInGpose().GetAwaiter().GetResult();
        _isHandlingSelf = _charaDataManager.HandledCharaData.Any(c => c.IsSelf);
    }

    private void DrawBrioWarning()
    {
        if (!_charaDataManager.BrioAvailable)
        {
            SpheneUI.Text.GroupedCentered("To utilize any features related to posing or spawning characters you require to have Brio installed.", ImGuiColors.DalamudRed);
            SpheneUI.Layout.DistanceSeparator();
        }
    }

    private void DrawDataApplicationStatus()
    {
        DisableDisabled(() =>
        {
            if (_charaDataManager.DataApplicationTask != null)
            {
                ImGui.AlignTextToFramePadding();
                SpheneUI.Text.Plain("Applying Data to Actor");
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Cancel Application"))
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
                SpheneUI.Text.ColoredWrapped("WARNING: During the data application avoid interacting with this actor to prevent potential crashes.", ImGuiColors.DalamudRed);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Layout.Separator();
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
        SpheneUI.Layout.BeginNavRail("charadata", railWidth);
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
        SpheneUI.Text.Big("GPose Actors");
        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
        using var indent = ImRaii.PushIndent(10f);

        foreach (var actor in _dalamudUtilService.GetGposeCharactersFromObjectTable())
        {
            if (actor == null) continue;
            using var actorId = ImRaii.PushId(actor.Name.TextValue);
            SpheneUI.Layout.Group(() =>
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Crosshairs))
                {
                    unsafe
                    {
                        DalamudUtilService.GposeTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
                    }
                }
                ImGui.SameLine();
                SpheneUI.AttachTooltip($"Target the GPose Character {CharaName(actor.Name.TextValue)}");
                ImGui.AlignTextToFramePadding();
                var pos = ImGui.GetCursorPosX();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, actor.Address == (_dalamudUtilService.GetGposeTargetGameObjectAsync().GetAwaiter().GetResult()?.Address ?? nint.Zero)))
                {
                    SpheneUI.Text.Plain(CharaName(actor.Name.TextValue));
                }
                ImGui.SameLine(250);
                var handled = _charaDataManager.HandledCharaData.FirstOrDefault(c => string.Equals(c.Name, actor.Name.TextValue, StringComparison.Ordinal));
                using (ImRaii.Disabled(handled == null))
                {
                    SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle);
                    var id = string.IsNullOrEmpty(handled?.MetaInfo.Uploader.UID) ? handled?.MetaInfo.Id : handled.MetaInfo.FullId;
                    SpheneUI.AttachTooltip($"Applied Data: {id ?? "No data applied"}");

                    ImGui.SameLine();
                    // maybe do this better, check with brio for handled charas or sth
                    using (ImRaii.Disabled(!actor.Name.TextValue.StartsWith("Brio ", StringComparison.Ordinal)))
                    {
                        if (SpheneUI.Icons.Button(FontAwesomeIcon.Trash))
                        {
                            _charaDataManager.RemoveChara(actor.Name.TextValue);
                        }
                        SpheneUI.AttachTooltip($"Remove character {CharaName(actor.Name.TextValue)}");
                    }
                    ImGui.SameLine();
                    if (SpheneUI.Icons.Button(FontAwesomeIcon.Undo))
                    {
                        _charaDataManager.RevertChara(handled);
                    }
                    SpheneUI.AttachTooltip($"Revert applied data from {CharaName(actor.Name.TextValue)}");
                    ImGui.SetCursorPosX(pos);
                    DrawPoseData(handled?.MetaInfo, actor.Name.TextValue, true);
                }
            });

            SpheneUI.Layout.Space(SpheneUI.Spacing.X2);
        }
    }

    private void DrawDataApplication()
    {
        SpheneUI.Text.Big("Apply Character Appearance");

        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

        if (_uiSharedService.IsInGpose)
        {
            SpheneUI.Text.Plain("GPose Target");
            ImGui.SameLine(200);
            SpheneUI.Text.Colored(CharaName(_gposeTarget), SpheneUI.Colors.Bool(_hasValidGposeTarget));
        }

        if (!_hasValidGposeTarget)
        {
            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
            SpheneUI.Text.GroupedCentered("Applying data is only available in GPose with a valid selected GPose target.", ImGuiColors.DalamudYellow, 350);
        }

        SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

        using var tabs = ImRaii.TabBar("Tabs");

        using (var byFavoriteTabItem = ImRaii.TabItem("Favorites"))
        {
            if (byFavoriteTabItem)
            {
                using var id = ImRaii.PushId("byFavorite");

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                var max = ImGui.GetWindowContentRegionMax();
                SpheneUI.Layout.Tree("Filters", () =>
                {
                    var maxIndent = ImGui.GetWindowContentRegionMax();
                    ImGui.SetNextItemWidth(maxIndent.X - ImGui.GetCursorPosX());
                    SpheneUI.Controls.InputTextWithHint("##ownFilter", "Code/Owner Filter", ref _filterCodeNote, 100);
                    ImGui.SetNextItemWidth(maxIndent.X - ImGui.GetCursorPosX());
                    SpheneUI.Controls.InputTextWithHint("##descFilter", "Custom Description Filter", ref _filterDescription, 100);
                    SpheneUI.Controls.Toggle("Only show entries with pose data", ref _filterPoseOnly);
                    SpheneUI.Controls.Toggle("Only show entries with world data", ref _filterWorldOnly);
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Reset Filter"))
                    {
                        _filterCodeNote = string.Empty;
                        _filterDescription = string.Empty;
                        _filterPoseOnly = false;
                        _filterWorldOnly = false;
                    }
                });

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Layout.Separator();
                using var scrollableChild = ImRaii.Child("favorite");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                using var totalIndent = ImRaii.PushIndent(5f);
                var cursorPos = ImGui.GetCursorPos();
                max = ImGui.GetWindowContentRegionMax();
                foreach (var favorite in _filteredFavorites.OrderByDescending(k => k.Value.Favorite.LastDownloaded))
                {
                    SpheneUI.Layout.Group(() =>
                    {
                        using var tableid = ImRaii.PushId(favorite.Key);
                        ImGui.AlignTextToFramePadding();
                        DrawFavorite(favorite.Key);
                        using var innerIndent = ImRaii.PushIndent(25f);
                        ImGui.SameLine();
                        var xPos = ImGui.GetCursorPosX();
                        var maxPos = (max.X - cursorPos.X);

                        bool metaInfoDownloaded = favorite.Value.DownloadedMetaInfo;
                        var metaInfo = favorite.Value.MetaInfo;

                        ImGui.AlignTextToFramePadding();
                        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey, !metaInfoDownloaded))
                        using (ImRaii.PushColor(ImGuiCol.Text, SpheneUI.Colors.Bool(metaInfo != null), metaInfoDownloaded))
                            SpheneUI.Text.Plain(favorite.Key);

                        var iconSize = SpheneUI.Icons.Size(FontAwesomeIcon.Check);
                        var refreshButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowsSpin);
                        var applyButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowRight);
                        var addButtonSize = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Plus);
                        var offsetFromRight = maxPos - (iconSize.X + refreshButtonSize.X + applyButtonSize.X + addButtonSize.X + (ImGui.GetStyle().ItemSpacing.X * 3.5f));

                        ImGui.SameLine();
                        ImGui.SetCursorPosX(offsetFromRight);
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
                            if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowsSpin))
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
                            if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowRight))
                            {
                                _ = _charaDataManager.ApplyCharaDataToGposeTarget(metaInfo!);
                            }
                        }, "Apply Character Data to GPose Target", metaInfo, _hasValidGposeTarget, false);
                        ImGui.SameLine();
                        GposeMetaInfoAction((meta) =>
                        {
                            if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                            {
                                _ = _charaDataManager.SpawnAndApplyData(meta!);
                            }
                        }, "Spawn Actor with Brio and apply Character Data", metaInfo, _hasValidGposeTarget, true);

                        string uidText = string.Empty;
                        var uid = favorite.Key.Split(":")[0];
                        if (metaInfo != null)
                        {
                            uidText = metaInfo.Uploader.AliasOrUID;
                        }
                        else
                        {
                            uidText = uid;
                        }

                        var note = _serverConfigurationManager.GetNoteForUid(uid);
                        if (note != null)
                        {
                            uidText = $"{note} ({uidText})";
                        }
                        SpheneUI.Text.Plain(uidText);

                        SpheneUI.Text.Plain("Last Use: ");
                        ImGui.SameLine();
                        SpheneUI.Text.Plain(favorite.Value.Favorite.LastDownloaded == DateTime.MaxValue ? "Never" : favorite.Value.Favorite.LastDownloaded.ToString());

                        var desc = favorite.Value.Favorite.CustomDescription;
                        ImGui.SetNextItemWidth(maxPos - xPos);
                        if (SpheneUI.Controls.InputTextWithHint("##desc", "Custom Description for Favorite", ref desc, 100))
                        {
                            favorite.Value.Favorite.CustomDescription = desc;
                            _configService.Save();
                        }

                        DrawPoseData(metaInfo, _gposeTarget, _hasValidGposeTarget);
                    });

                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                }

                if (_configService.Current.FavoriteCodes.Count == 0)
                {
                    SpheneUI.Text.ColoredWrapped("You have no favorites added. Add Favorites through the other tabs before you can use this tab.", ImGuiColors.DalamudYellow);
                }
            }
        }

        using (var byCodeTabItem = ImRaii.TabItem("Code"))
        {
            using var id = ImRaii.PushId("byCodeTab");
            if (byCodeTabItem)
            {
                using var child = ImRaii.Child("sharedWithYouByCode", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
                DrawHelpFoldout("You can apply character data you have a code for in this tab. Provide the code in it's given format \"OwnerUID:DataId\" into the field below and click on " +
                                "\"Get Info from Code\". This will provide you basic information about the data behind the code. Afterwards select an actor in GPose and press on \"Download and apply to <actor>\"." + Environment.NewLine + Environment.NewLine
                                + "Description: as set by the owner of the code to give you more or additional information of what this code may contain." + Environment.NewLine
                                + "Last Update: the date and time the owner of the code has last updated the data." + Environment.NewLine
                                + "Is Downloadable: whether or not the code is downloadable and applicable. If the code is not downloadable, contact the owner so they can attempt to fix it." + Environment.NewLine + Environment.NewLine
                                + "To download a code the code requires correct access permissions to be set by the owner. If getting info from the code fails, contact the owner to make sure they set their Access Permissions for the code correctly.");

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Controls.InputTextWithHint("##importCode", "Enter Data Code", ref _importCode, 100);
                using (ImRaii.Disabled(string.IsNullOrEmpty(_importCode)))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowCircleDown, "Get Info from Code"))
                    {
                        _charaDataManager.DownloadMetaInfo(_importCode);
                    }
                }
                GposeMetaInfoAction((meta) =>
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, $"Download and Apply"))
                    {
                        _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta!);
                    }
                }, "Apply this Character Data to the current GPose actor", _charaDataManager.LastDownloadedMetaInfo, _hasValidGposeTarget, false);
                ImGui.SameLine();
                GposeMetaInfoAction((meta) =>
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, $"Download and Spawn"))
                    {
                        _ = _charaDataManager.SpawnAndApplyData(meta!);
                    }
                }, "Spawn a new Brio actor and apply this Character Data", _charaDataManager.LastDownloadedMetaInfo, _hasValidGposeTarget, true);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                DrawAddOrRemoveFavorite(_charaDataManager.LastDownloadedMetaInfo);

                ImGui.NewLine();
                if (!_charaDataManager.DownloadMetaInfoTask?.IsCompleted ?? false)
                {
                    SpheneUI.Text.ColoredWrapped("Downloading meta info. Please wait.", ImGuiColors.DalamudYellow);
                }
                if ((_charaDataManager.DownloadMetaInfoTask?.IsCompleted ?? false) && !_charaDataManager.DownloadMetaInfoTask.Result.Success)
                {
                    SpheneUI.Text.ColoredWrapped(_charaDataManager.DownloadMetaInfoTask.Result.Result, ImGuiColors.DalamudRed);
                }

                using (ImRaii.Disabled(_charaDataManager.LastDownloadedMetaInfo == null))
                {
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                    var metaInfo = _charaDataManager.LastDownloadedMetaInfo;
                    SpheneUI.Text.Plain("Description");
                    ImGui.SameLine(150);
                    SpheneUI.Text.Wrapped(string.IsNullOrEmpty(metaInfo?.Description) ? "-" : metaInfo.Description);
                    SpheneUI.Text.Plain("Last Update");
                    ImGui.SameLine(150);
                    SpheneUI.Text.Plain(metaInfo?.UpdatedDate.ToLocalTime().ToString() ?? "-");
                    SpheneUI.Text.Plain("Is Downloadable");
                    ImGui.SameLine(150);
                    SpheneUI.Icons.BoolIcon(metaInfo?.CanBeDownloaded ?? false, inline: false);
                    SpheneUI.Text.Plain("Poses");
                    ImGui.SameLine(150);
                    if (metaInfo?.HasPoses ?? false)
                        DrawPoseData(metaInfo, _gposeTarget, _hasValidGposeTarget);
                    else
                        SpheneUI.Icons.BoolIcon(false, false);
                }
            }
        }

        using (var yourOwnTabItem = ImRaii.TabItem("Your Own"))
        {
            using var id = ImRaii.PushId("yourOwnTab");
            if (yourOwnTabItem)
            {
                DrawHelpFoldout("You can apply character data you created yourself in this tab. If the list is not populated press on \"Download your Character Data\"." + Environment.NewLine + Environment.NewLine
                                 + "To create new and edit your existing character data use the \"MCD Online\" tab.");

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                using (ImRaii.Disabled(_charaDataManager.GetAllDataTask != null
                    || (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowCircleDown, "Download your Character Data"))
                    {
                        var token = _disposalCts?.Token ?? CancellationToken.None;
                        _ = _charaDataManager.GetAllData(token);
                    }
                }
                if (_charaDataManager.DataGetTimeoutTask != null && !_charaDataManager.DataGetTimeoutTask.IsCompleted)
                {
                    SpheneUI.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
                }

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Layout.Separator();

                using var child = ImRaii.Child("ownDataChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
                using var indent = ImRaii.PushIndent(10f);
                foreach (var data in _charaDataManager.OwnCharaData.Values)
                {
                    var hasMetaInfo = _charaDataManager.TryGetMetaInfo(data.FullId, out var metaInfo);
                    if (!hasMetaInfo) continue;
                    DrawMetaInfoData(_gposeTarget, _hasValidGposeTarget, metaInfo!, true);
                }

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
            }
        }

        using (var sharedWithYouTabItem = ImRaii.TabItem("Shared With You", _openDataApplicationShared ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            using var id = ImRaii.PushId("sharedWithYouTab");
            if (sharedWithYouTabItem)
            {
                DrawHelpFoldout("You can apply character data shared with you implicitly in this tab. Shared Character Data are Character Data entries that have \"Sharing\" set to \"Shared\" and you have access through those by meeting the access restrictions, " +
                                "i.e. you were specified by your UID to gain access or are paired with the other user according to the Access Restrictions setting." + Environment.NewLine + Environment.NewLine
                                + "Filter if needed to find a specific entry, then just press on \"Apply to <actor>\" and it will download and apply the Character Data to the currently targeted GPose actor." + Environment.NewLine + Environment.NewLine
                                + "Note: Shared Data of Pairs you have paused will not be shown here.");

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                DrawUpdateSharedDataButton();

                int activeFilters = 0;
                if (!string.IsNullOrEmpty(_sharedWithYouOwnerFilter)) activeFilters++;
                if (!string.IsNullOrEmpty(_sharedWithYouDescriptionFilter)) activeFilters++;
                if (_sharedWithYouDownloadableFilter) activeFilters++;
                string filtersText = activeFilters == 0 ? "Filters" : $"Filters ({activeFilters} active)";
                SpheneUI.Layout.Tree($"{filtersText}##filters", () =>
                {
                    var filterWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                    ImGui.SetNextItemWidth(filterWidth);
                    if (SpheneUI.Controls.InputTextWithHint("##filter", "Filter by UID/Note", ref _sharedWithYouOwnerFilter, 30))
                    {
                        UpdateFilteredItems();
                    }
                    ImGui.SetNextItemWidth(filterWidth);
                    if (SpheneUI.Controls.InputTextWithHint("##filterDesc", "Filter by Description", ref _sharedWithYouDescriptionFilter, 50))
                    {
                        UpdateFilteredItems();
                    }
                    if (SpheneUI.Controls.Toggle("Only show downloadable", ref _sharedWithYouDownloadableFilter))
                    {
                        UpdateFilteredItems();
                    }
                });

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

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Layout.Separator();
                using var child = ImRaii.Child("sharedWithYouChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                foreach (var entry in _filteredDict ?? [])
                {
                    bool isFilteredAndHasToBeOpened = entry.Key.Contains(_sharedWithYouOwnerFilter) && _openDataApplicationShared;
                    if (isFilteredAndHasToBeOpened)
                        ImGui.SetNextItemOpen(isFilteredAndHasToBeOpened);
                    SpheneUI.Layout.Tree($"{entry.Key} - [{entry.Value.Count} Character Data Sets]##{entry.Key}", () =>
                    {
                        foreach (var data in entry.Value)
                        {
                            DrawMetaInfoData(_gposeTarget, _hasValidGposeTarget, data);
                        }
                        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                    });
                    if (isFilteredAndHasToBeOpened)
                        _openDataApplicationShared = false;
                }
            }
        }

        using (var mcdfTabItem = ImRaii.TabItem("From MCDF"))
        {
            using var id = ImRaii.PushId("applyMcdfTab");
            if (mcdfTabItem)
            {
                using var child = ImRaii.Child("applyMcdf", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
                DrawHelpFoldout("You can apply character data shared with you using a MCDF file in this tab." + Environment.NewLine + Environment.NewLine
                                + "Load the MCDF first via the \"Load MCDF\" button which will give you the basic description that the owner has set during export." + Environment.NewLine
                                + "You can then apply it to any handled GPose actor." + Environment.NewLine + Environment.NewLine
                                + "MCDF to share with others can be generated using the \"MCDF Export\" tab at the top.");

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                if (_charaDataManager.LoadedMcdfHeader == null || _charaDataManager.LoadedMcdfHeader.IsCompleted)
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FolderOpen, "Load MCDF"))
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
                    if ((_charaDataManager.LoadedMcdfHeader?.IsCompleted ?? false))
                    {
                        SpheneUI.Text.Plain("Loaded file");
                        ImGui.SameLine(200);
                        SpheneUI.Text.Wrapped(_charaDataManager.LoadedMcdfHeader.Result.LoadedFile.FilePath);
                        SpheneUI.Text.Plain("Description");
                        ImGui.SameLine(200);
                        SpheneUI.Text.Wrapped(_charaDataManager.LoadedMcdfHeader.Result.LoadedFile.CharaFileData.Description);

                        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                        using (ImRaii.Disabled(!_hasValidGposeTarget))
                        {
                            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Apply"))
                            {
                                _ = _charaDataManager.McdfApplyToGposeTarget();
                            }
                            SpheneUI.AttachTooltip($"Apply to {_gposeTarget}");
                            ImGui.SameLine();
                            using (ImRaii.Disabled(!_charaDataManager.BrioAvailable))
                            {
                                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Plus, "Spawn Actor and Apply"))
                                {
                                    _charaDataManager.McdfSpawnApplyToGposeTarget();
                                }
                            }
                        }
                    }
                    if ((_charaDataManager.LoadedMcdfHeader?.IsFaulted ?? false) || (_charaDataManager.McdfApplicationTask?.IsFaulted ?? false))
                    {
                        SpheneUI.Text.ColoredWrapped("Failure to read MCDF file. MCDF file is possibly corrupt. Re-export the MCDF file and try again.",
                            ImGuiColors.DalamudRed);
                        SpheneUI.Text.ColoredWrapped("Note: if this is your MCDF, try redrawing yourself, wait and re-export the file. " +
                            "If you received it from someone else have them do the same.", ImGuiColors.DalamudYellow);
                    }
                }
                else
                {
                    SpheneUI.Text.ColoredWrapped("Loading Character...", ImGuiColors.DalamudYellow);
                }
            }
        }
    }

    private void DrawMcdfExport()
    {
        SpheneUI.Text.Big("Sphene Character Data File Export");

        DrawHelpFoldout("This feature allows you to pack your character into a MCDF file and manually send it to other people. MCDF files can officially only be imported during GPose through Sphene. " +
            "Be aware that the possibility exists that people write unofficial custom exporters to extract the containing data.");

        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

        SpheneUI.Controls.Checkbox("##readExport", ref _readExport);
        ImGui.SameLine();
        SpheneUI.Text.Wrapped("I understand that by exporting my character data into a file and sending it to other people I am giving away my current character appearance irrevocably. People I am sharing my data with have the ability to share it with other people without limitations.");

        if (_readExport)
        {
            ImGui.Indent();

            SpheneUI.Controls.InputTextWithHint("Export Descriptor", "This description will be shown on loading the data", ref _exportDescription, 255);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Export Character as MCDF"))
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
            SpheneUI.Text.ColoredWrapped("Note: For best results make sure you have everything you want to be shared as well as the correct character appearance" +
                " equipped and redraw your character before exporting.", ImGuiColors.DalamudYellow);

            ImGui.Unindent();
        }
    }

    private void DrawMetaInfoData(string selectedGposeActor, bool hasValidGposeTarget, CharaDataMetaInfoExtendedDto data, bool canOpen = false)
    {
        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
        using var entryId = ImRaii.PushId(data.FullId);

        var startPos = ImGui.GetCursorPosX();
        var maxPos = ImGui.GetWindowContentRegionMax().X;
        var availableWidth = maxPos - startPos;
        SpheneUI.Layout.Group(() =>
        {
            ImGui.AlignTextToFramePadding();
            DrawAddOrRemoveFavorite(data);

            ImGui.SameLine();
            var favPos = ImGui.GetCursorPosX();
            ImGui.AlignTextToFramePadding();
            SpheneUI.Text.Colored(data.FullId, SpheneUI.Colors.Bool(data.CanBeDownloaded));
            if (!data.CanBeDownloaded)
            {
                SpheneUI.AttachTooltip("This data is incomplete on the server and cannot be downloaded. Contact the owner so they can fix it. If you are the owner, review the data in the MCD Online tab.");
            }

            var offsetFromRight = availableWidth - SpheneUI.Icons.Size(FontAwesomeIcon.Calendar).X - SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowRight).X
                - SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Plus).X - ImGui.GetStyle().ItemSpacing.X * 2;

            ImGui.SameLine();
            ImGui.SetCursorPosX(offsetFromRight);
            SpheneUI.Icons.Icon(FontAwesomeIcon.Calendar);
            SpheneUI.AttachTooltip($"Last Update: {data.UpdatedDate}");

            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.ArrowRight))
                {
                    _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta!);
                }
            }, $"Apply Character data to {CharaName(selectedGposeActor)}", data, hasValidGposeTarget, false);
            ImGui.SameLine();
            GposeMetaInfoAction((meta) =>
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.Plus))
                {
                    _ = _charaDataManager.SpawnAndApplyData(meta!);
                }
            }, "Spawn and Apply Character data", data, hasValidGposeTarget, true);

            using var indent = ImRaii.PushIndent(favPos - startPos);

            if (canOpen)
            {
                using (ImRaii.Disabled(_isHandlingSelf))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Edit, "Open in MCD Online Editor"))
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

            if (string.IsNullOrEmpty(data.Description))
            {
                SpheneUI.Text.ColoredWrapped("No description set", ImGuiColors.DalamudGrey, availableWidth);
            }
            else
            {
                SpheneUI.Text.Wrapped(data.Description, availableWidth);
            }

            DrawPoseData(data, selectedGposeActor, hasValidGposeTarget);
        });
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
        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
        SpheneUI.Text.Big("Settings");
        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
        bool openInGpose = _configService.Current.OpenSpheneHubOnGposeStart;
        if (SpheneUI.Controls.Toggle("Open Character Data Hub when GPose loads", ref openInGpose))
        {
            _configService.Current.OpenSpheneHubOnGposeStart = openInGpose;
            _configService.Save();
        }
        SpheneUI.Controls.HelpText("This will automatically open the import menu when loading into Gpose. If unchecked you can open the menu manually with /sphene gpose");
        bool downloadDataOnConnection = _configService.Current.DownloadMcdDataOnConnection;
        if (SpheneUI.Controls.Toggle("Download MCD Online Data on connecting", ref downloadDataOnConnection))
        {
            _configService.Current.DownloadMcdDataOnConnection = downloadDataOnConnection;
            _configService.Save();
        }
        SpheneUI.Controls.HelpText("This will automatically download MCD Online data (Your Own and Shared with You) once a connection is established to the server.");

        bool showHelpTexts = _configService.Current.ShowHelpTexts;
        if (SpheneUI.Controls.Toggle("Show \"What is this? (Explanation / Help)\" foldouts", ref showHelpTexts))
        {
            _configService.Current.ShowHelpTexts = showHelpTexts;
            _configService.Save();
        }

        SpheneUI.Controls.Toggle("Abbreviate Chara Names", ref _abbreviateCharaName);
        SpheneUI.Controls.HelpText("This setting will abbreviate displayed names. This setting is not persistent and will reset between restarts.");

        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain("Last Export Folder");
        ImGui.SameLine(300);
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain(string.IsNullOrEmpty(_configService.Current.LastSavedCharaDataLocation) ? "Not set" : _configService.Current.LastSavedCharaDataLocation);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Clear Last Export Folder"))
        {
            _configService.Current.LastSavedCharaDataLocation = string.Empty;
            _configService.Save();
        }
        SpheneUI.Controls.HelpText("Use this if the Load or Save MCDF file dialog does not open");
    }

    private void DrawHelpFoldout(string text)
    {
        if (_configService.Current.ShowHelpTexts)
        {
            SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Tree("What is this? (Explanation / Help)", () =>
            {
                SpheneUI.Text.Wrapped(text);
            });
        }
    }

    private void DisableDisabled(Action drawAction)
    {
        if (_disableUI) ImGui.EndDisabled();
        drawAction();
        if (_disableUI) ImGui.BeginDisabled();
    }
}
