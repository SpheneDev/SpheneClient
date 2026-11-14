using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Sphene.WebAPI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class DataAnalysisUi : WindowMediatorSubscriberBase
{
    /// <summary>
    /// The pages of the character data analysis surface · shared between the
    /// standalone window rail and the embedded Sphene Hub rail.
    /// </summary>
    public enum DataAnalysisPage
    {
        Overview,
        Files,
        Textures,
        Transients,
        Recording,
        Mcdf
    }

    private readonly ApiController _apiController;
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly Progress<(string, int)> _conversionProgress = new();
    private readonly IDataManager _gameData;
    private readonly IpcManager _ipcManager;
    private readonly IObjectTable _objectTable;
    private readonly SpheneProfileManager _spheneProfileManager;
    private byte[] _lastProfileAvatarBytes = [];
    private readonly UiSharedService _uiSharedService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfig;
    private readonly TransientResourceManager _transientResourceManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly TransientConfigService _transientConfigService;
    private readonly TextureBackupService _textureBackupService;
    private readonly ShrinkU.Services.TextureBackupService _shrinkuBackupService;
    private readonly ShrinkU.Services.TextureConversionService _shrinkuConversionService;
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private string _conversionCurrentFileName = string.Empty;
    private int _conversionCurrentFileProgress = 0;
    private Task? _conversionTask;
    private bool _enableBc7ConversionMode = true;
    private bool _enableBackupBeforeConversion = true;
    private bool _hasUpdate = false;
    private bool _modalOpen = false;

    private readonly Progress<(string, int, int)> _revertProgress = new();
    private CancellationTokenSource _revertCancellationTokenSource = new();
    private Task<(long totalSize, int fileCount)>? _storageInfoTask;
    private (long totalSize, int fileCount) _cachedStorageInfo;
    private DateTime _lastStorageInfoUpdate = DateTime.MinValue;
    private Dictionary<string, List<string>>? _cachedBackupsForAnalysis;
    private DateTime _lastBackupAnalysisUpdate = DateTime.MinValue;
    private string _selectedFileTypeTab = string.Empty;
    private string _selectedHash = string.Empty;
    private ObjectKind _selectedObjectTab;
    private bool _showModal = false;
    private CancellationTokenSource _transientRecordCts = new();
    private Action<(string, int)>? _onShrinkuConversionProgress;
    private Action<(string modName, int current, int total, int fileTotal)>? _onShrinkuModProgress;
    private string _currentModName = string.Empty;
    private int _currentModIndex = 0;
    private int _totalMods = 0;
    private int _currentModTotalFiles = 0;
    private DateTime _currentModStartedAt = DateTime.MinValue;

    private DataAnalysisPage _standalonePage = DataAnalysisPage.Overview;
    private DataAnalysisPage _embeddedPage = DataAnalysisPage.Overview;
    private bool _embeddedScope;

    private DataAnalysisPage _activePage
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

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, SpheneMediator mediator,
        CharacterAnalyzer characterAnalyzer, ApiController apiController, IDataManager gameData, IpcManager ipcManager,
        IObjectTable objectTable, SpheneProfileManager spheneProfileManager,
        PerformanceCollectorService performanceCollectorService, UiSharedService uiSharedService,
        PlayerPerformanceConfigService playerPerformanceConfig, TransientResourceManager transientResourceManager,
        TransientConfigService transientConfigService, TextureBackupService textureBackupService,
        ShrinkU.Services.TextureBackupService shrinkuBackupService,
        ShrinkU.Services.TextureConversionService shrinkuConversionService,
        IServiceProvider serviceProvider)
        : base(logger, mediator, "Character Data###SpheneDataAnalysisUi", performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _apiController = apiController;
        _gameData = gameData;
        _ipcManager = ipcManager;
        _objectTable = objectTable;
        _spheneProfileManager = spheneProfileManager;
        _uiSharedService = uiSharedService;
        _playerPerformanceConfig = playerPerformanceConfig;
        _transientResourceManager = transientResourceManager;
        _transientConfigService = transientConfigService;
        _textureBackupService = textureBackupService;
        _shrinkuBackupService = shrinkuBackupService;
        _shrinkuConversionService = shrinkuConversionService;
        _serviceProvider = serviceProvider;
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) =>
        {
            _hasUpdate = true;
        });
        Size = new Vector2(1050, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new()
        {
            MinimumSize = new()
            {
                X = 800,
                Y = 600
            },
            MaximumSize = new()
            {
                X = 3840,
                Y = 2160
            }
        };

        _conversionProgress.ProgressChanged += ConversionProgress_ProgressChanged;
        _onShrinkuConversionProgress = e =>
        {
            try
            {
                _conversionCurrentFileName = e.Item1;
                _conversionCurrentFileProgress = e.Item2;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update conversion progress (service)");
            }
        };
        _shrinkuConversionService.OnConversionProgress += _onShrinkuConversionProgress;
        _onShrinkuModProgress = e =>
        {
            try
            {
                _currentModName = e.modName;
                _currentModIndex = e.current;
                _totalMods = e.total;
                _currentModTotalFiles = e.fileTotal;
                _conversionCurrentFileProgress = 0;
                _currentModStartedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update mod-level conversion progress (analysis)");
            }
        };
        _shrinkuConversionService.OnModProgress += _onShrinkuModProgress;
    }

    /// <summary>Raised when the active analysis page changes through user navigation · args are (previous, next, embedded). The hub folds embedded-surface changes into its own breadcrumb/history.</summary>
    internal event Action<DataAnalysisPage, DataAnalysisPage, bool>? SubPageChanged;

    /// <summary>The page the Hub-embedded analysis shows · read by the hub for its crumb. The standalone window has its own page state.</summary>
    internal DataAnalysisPage ActiveSubPageValue => _embeddedPage;

    /// <summary>Label of the embedded analysis page for the hub breadcrumb.</summary>
    internal string ActiveCrumbLabel => PageLabel(_embeddedPage);

    /// <summary>Applies a page to the Hub-embedded analysis without recording it · used for hub-driven restores.</summary>
    internal void ApplySubPageQuiet(DataAnalysisPage page)
    {
        _embeddedPage = page;
    }

    /// <summary>
    /// Fresh entry into the Hub-embedded analysis lands on the overview · the
    /// embedded surface doesn't resurrect its last tab on a new visit.
    /// History restores (SubPage on the hub location) bypass this.
    /// </summary>
    internal void ResetEmbeddedNavigation()
    {
        _embeddedPage = DataAnalysisPage.Overview;
    }

    private void SetPage(DataAnalysisPage page)
    {
        if (page == _activePage)
        {
            return;
        }
        var previous = _activePage;
        _activePage = page;
        SubPageChanged?.Invoke(previous, page, _embeddedScope);
    }

    private static string PageLabel(DataAnalysisPage page) => page switch
    {
        DataAnalysisPage.Overview => "Overview",
        DataAnalysisPage.Files => "Files",
        DataAnalysisPage.Textures => "Textures",
        DataAnalysisPage.Transients => "Transient Files",
        DataAnalysisPage.Recording => "Recording",
        DataAnalysisPage.Mcdf => "MCDF",
        _ => "Overview"
    };

    private static FontAwesomeIcon ObjectKindIcon(ObjectKind kind) => kind switch
    {
        ObjectKind.Player => FontAwesomeIcon.User,
        ObjectKind.MinionOrMount => FontAwesomeIcon.Horse,
        ObjectKind.Companion => FontAwesomeIcon.Feather,
        ObjectKind.Pet => FontAwesomeIcon.Paw,
        _ => FontAwesomeIcon.Cube
    };

    protected override void DrawInternal()
    {
        DrawConversionModal();
        UpdateSharedState();

        var railWidth = SpheneUI.Layout.NavRailSegmentedWidth([
            (FontAwesomeIcon.ChartPie, "Overview"),
            (FontAwesomeIcon.FolderOpen, "Files"),
            (FontAwesomeIcon.Images, "Textures"),
            (FontAwesomeIcon.Database, "Transient Files"),
            (FontAwesomeIcon.DotCircle, "Recording")
        ]);
        SpheneUI.Layout.BeginNavRail("dataanalysis", railWidth, backdropPattern: true);
        // Same top offset as the hub rail so the first button sits at the
        // same height in both surfaces.
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        DrawNavItems(null, 0f);
        SpheneUI.Layout.EndNavRail();
        // Non-scrollable content: every page owns its scrolling (the roster
        // scroll area and the fill-to-bottom ScrollY tables). An outer page
        // scroll would slide frozen table headers away and let the page fade
        // paint over them.
        if (SpheneUI.Layout.BeginNavContent("dataanalysis", scrollable: false))
        {
            DrawPageContent();
        }
        SpheneUI.Layout.EndNavContent();
    }

    private void DrawNavItems(
        List<(string Label, float CenterY, Vector4 Accent)>? pinnedCategories, float pinnedCenterY)
    {
        var accentAnalysis = SpheneTheme.Current.AccentBlue;
        var accentTransient = SpheneUI.Palette.SpheneGold;

        void SidebarButton(string label, FontAwesomeIcon icon, DataAnalysisPage page, Vector4 accent)
        {
            // one shared flyout id · only the active row may show it, or a
            // visited sibling would display the wrong page's intro.
            var helpId = _activePage == page ? "dataanalysis" : null;
            if (SpheneUI.Layout.NavRailSegmentItem(label, icon, _activePage == page, accent,
                    helpId: helpId))
            {
                SetPage(page);
            }
        }

        SpheneUI.Layout.NavRailCategoryLabel("Analysis", accentAnalysis, pinnedCategories, pinnedCenterY);
        SidebarButton("Overview", FontAwesomeIcon.ChartPie, DataAnalysisPage.Overview, accentAnalysis);
        SidebarButton("Files", FontAwesomeIcon.FolderOpen, DataAnalysisPage.Files, accentAnalysis);
        SidebarButton("Textures", FontAwesomeIcon.Images, DataAnalysisPage.Textures, accentAnalysis);
        SpheneUI.Layout.NavRailDivider(accentAnalysis, accentTransient);

        SpheneUI.Layout.NavRailCategoryLabel("Transient Data", accentTransient, pinnedCategories, pinnedCenterY);
        SidebarButton("Stored Files", FontAwesomeIcon.Database, DataAnalysisPage.Transients, accentTransient);
        SidebarButton("Recording", FontAwesomeIcon.DotCircle, DataAnalysisPage.Recording, accentTransient);
        SpheneUI.Layout.NavRailDivider(accentTransient, accentAnalysis);

        SpheneUI.Layout.NavRailCategoryLabel("MCDF", accentAnalysis, pinnedCategories, pinnedCenterY);
        SidebarButton("MCDF", FontAwesomeIcon.FileExport, DataAnalysisPage.Mcdf, accentAnalysis);
    }

    /// <summary>
    /// The analysis nav rendered inside the Sphene Hub rail · same
    /// items as the standalone rail plus a pop-out shortcut so the windowed
    /// surface stays one click away.
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
            var accent = SpheneTheme.Current.AccentBlue;
            SpheneUI.Layout.NavRailDivider(accent, accent);
            if (SpheneUI.Layout.NavRailSegmentItem("Pop Out", FontAwesomeIcon.ExternalLinkAlt, false, accent))
            {
                Mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
            }
            SpheneUI.AttachTooltip("Open Character Data Analysis in a separate window");
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    /// <summary>
    /// The active analysis page rendered inside the Hub's nav content child.
    /// The Hub hosts this page non-scrollable · the pages own their scrolling
    /// (the roster scroll area and the fill-to-bottom ScrollY tables).
    /// </summary>
    internal void DrawEmbeddedContent()
    {
        var previousScope = _embeddedScope;
        _embeddedScope = true;
        try
        {
            DrawConversionModal();
            UpdateSharedState();
            DrawPageContent();
        }
        finally
        {
            _embeddedScope = previousScope;
        }
    }

    private void UpdateSharedState()
    {
        // _cachedAnalysis == null covers first draw of the Hub-embedded view,
        // which never receives an OnOpen call.
        if (_hasUpdate || _cachedAnalysis == null)
        {
            _cachedAnalysis = _characterAnalyzer.LastAnalysis.DeepClone();
            _cachedBackupsForAnalysis = null; // Invalidate backup cache when analysis changes
            _hasUpdate = false;
        }
    }

    /// <summary>
    /// Uniform page opener for every analysis page: both surfaces draw the
    /// compact one-line intro since the rail selection and window/hub
    /// chrome already carry the page title.
    /// </summary>
    private static void DrawPageTitle(string intro)
    {
        // Both surfaces: the rail selection and window/hub chrome already
        // carry the title, so the page opens straight with its intro line.
        SpheneUI.Layout.RegisterPageHelp("dataanalysis", () =>
        {
            SpheneUI.Text.MutedWrapped(intro, ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });
        SpheneUI.Text.PageIntro(intro);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    private DataAnalysisPage _lastDrawnStandalonePage = (DataAnalysisPage)(-1);
    private DataAnalysisPage _lastDrawnEmbeddedPage = (DataAnalysisPage)(-1);

    private void DrawPageContent()
    {
        // Both surfaces share one scroll child each · switching pages keeps a
        // stale scroll offset that shifts fresh content and leaves an empty
        // tail. Snap back to the top so each page starts from its header.
        // Tracked per surface so a standalone window and the embedded hub view
        // open at the same time don't reset each other every frame.
        if (_embeddedScope)
        {
            if (_lastDrawnEmbeddedPage != _activePage)
            {
                _lastDrawnEmbeddedPage = _activePage;
                ImGui.SetScrollY(0f);
            }
        }
        else if (_lastDrawnStandalonePage != _activePage)
        {
            _lastDrawnStandalonePage = _activePage;
            ImGui.SetScrollY(0f);
        }
        switch (_activePage)
        {
            case DataAnalysisPage.Overview:
                DrawOverviewPage();
                break;
            case DataAnalysisPage.Files:
                DrawFilesPage();
                break;
            case DataAnalysisPage.Textures:
                DrawTexturesPage();
                break;
            case DataAnalysisPage.Transients:
                DrawTransientsPage();
                break;
            case DataAnalysisPage.Recording:
                DrawRecordingPage();
                break;
            case DataAnalysisPage.Mcdf:
                DrawMcdfPage();
                break;
        }
    }

    /// <summary>MCDF subpage - Apply and Export live side by side; the rest
    /// of the legacy hub pages are not offered here. The draws run through
    /// <see cref="Sphene.UI.CharaDataHub.CharaDataHubUi"/>, so there is still
    /// exactly one implementation of each.</summary>
    private int _mcdfSubTab;

    private void DrawMcdfPage()
    {
        var hub = _serviceProvider.GetRequiredService<Sphene.UI.CharaDataHub.CharaDataHubUi>();
        var labels = new[] { "Apply data", "MCDF export" };
        _mcdfSubTab = Math.Clamp(_mcdfSubTab, 0, labels.Length - 1);
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0f, SpheneUI.Spacing.X4.Scaled);
            var idx = i;
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Tag,
                    (_mcdfSubTab == idx ? "· " : string.Empty) + labels[idx]))
            {
                _mcdfSubTab = idx;
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        if (_mcdfSubTab == 0)
        {
            hub.DrawApplyDataContent();
        }
        else
        {
            hub.DrawMcdfExportContent();
        }
    }

    private void DrawConversionModal()
    {
        if (_conversionTask != null && !_conversionTask.IsCompleted)
        {
            _showModal = true;
            if (SpheneUI.Popups.BeginModalStyled("BC7 Conversion in Progress"))
            {
                SpheneUI.Text.SectionHeader("Converting Mods");
                SpheneUI.Layout.Separator();
                var modsPercent = _totalMods > 0 ? (float)Math.Clamp(_currentModIndex / (double)_totalMods, 0.0, 1.0) : 0f;
                SpheneUI.Text.Plain($"Batch progress: {_currentModIndex} / {_totalMods} mods");
                SpheneUI.Controls.ProgressBar(modsPercent, new Vector2(-1, 0), $"{modsPercent * 100:F1}%");
                if (_currentModStartedAt != DateTime.MinValue && _currentModIndex >= 0)
                {
                    var elapsedMods = DateTime.UtcNow - _currentModStartedAt;
                    SpheneUI.Text.Plain($"Current mod elapsed: {elapsedMods:mm\\:ss}");
                }
                SpheneUI.Layout.VerticalSpace();
                if (!string.IsNullOrEmpty(_currentModName))
                    SpheneUI.Text.Plain($"Current mod: {_currentModName}");
                var totalFiles = _currentModTotalFiles > 0 ? _currentModTotalFiles : _texturesToConvert.Count;
                var currentFile = _conversionCurrentFileProgress;
                var progressPercentage = totalFiles > 0 ? (float)currentFile / totalFiles : 0f;
                SpheneUI.Text.Plain($"Current mod progress: {currentFile} / {totalFiles} files");
                SpheneUI.Controls.ProgressBar(progressPercentage, new Vector2(-1, 0), $"{progressPercentage * 100:F1}%");
                if (!string.IsNullOrEmpty(_conversionCurrentFileName))
                    SpheneUI.Text.Wrapped("Current file: " + _conversionCurrentFileName);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.StopCircle, "Cancel conversion"))
                {
                    try { _shrinkuConversionService.Cancel(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel ShrinkU conversion"); }
                }
                SpheneUI.Layout.SetScaledWindowSize(500);
                SpheneUI.Popups.EndStyled();
            }
            else
            {
                _modalOpen = false;
            }
        }
        else if (_conversionTask != null && _conversionTask.IsCompleted)
        {
            // Ensure the progress modal always closes when conversion finishes
            _conversionTask = null;
            if (_texturesToConvert.Count > 0)
                _texturesToConvert.Clear();
            _showModal = false;
            _modalOpen = false;
        }

        if (_showModal && !_modalOpen)
        {
            ImGui.OpenPopup("BC7 Conversion in Progress");
            _modalOpen = true;
        }
    }

    private void DrawOverviewPage()
    {
        DrawPageTitle(
            "A summary of the files your character currently uses and where to dig deeper.");

        SpheneUI.Layout.HelpExtra(() =>
        {
            SpheneUI.Text.Wrapped("This section shows you all files and their sizes that are currently in use through your character and associated entities in Sphene.");
            SpheneUI.Text.Wrapped("Use the sidebar to browse files per entity type, manage texture backups and conversions, review stored transient file data, or record new transient data for broken animations.");
        });

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var isAnalyzing = _characterAnalyzer.IsAnalysisRunning;
        var hasMissing = _cachedAnalysis?.Any(c => c.Value.Any(f => !f.Value.IsComputed)) ?? false;
        var statusAccent = isAnalyzing || hasMissing
            ? ImGuiColors.DalamudYellow
            : ImGuiColors.ParsedGreen;

        DrawAuditCommandBar(isAnalyzing, hasMissing, statusAccent);

        if (_cachedAnalysis == null || _cachedAnalysis.Count == 0)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Callout("No analysis data yet · once Sphene has inspected your character, the entities in use appear here.",
                FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudGrey);
            return;
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        var roster = BuildEntityRoster(_cachedAnalysis);
        var drawListTop = ImGui.GetWindowDrawList();
        var rosterLabelY = ImGui.GetCursorScreenPos();
        var rosterW = MathF.Max(60f * SpheneUI.Scale, SpheneUI.Controls.BoundedAvailX());
        var rosterText = $"ACTIVE ENTITIES · {roster.Count}";
        var rosterTextW = ImGui.CalcTextSize(rosterText).X;
        var rosterMidY = rosterLabelY.Y + ImGui.GetTextLineHeight() * 0.5f;
        var rosterLineCol = SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.4f));
        var rosterTextPos = new Vector2(rosterLabelY.X + (rosterW - rosterTextW) * 0.5f, rosterLabelY.Y);
        drawListTop.AddText(rosterTextPos, SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), rosterText);
        var tickPad = 10f * SpheneUI.Scale;
        drawListTop.AddLine(new Vector2(rosterLabelY.X, rosterMidY),
            new Vector2(rosterTextPos.X - tickPad, rosterMidY), rosterLineCol,
            MathF.Max(1f, SpheneUI.Scale * 0.75f));
        drawListTop.AddLine(new Vector2(rosterTextPos.X + rosterTextW + tickPad, rosterMidY),
            new Vector2(rosterLabelY.X + rosterW, rosterMidY), rosterLineCol,
            MathF.Max(1f, SpheneUI.Scale * 0.75f));
        ImGui.Dummy(new Vector2(rosterW, ImGui.GetTextLineHeight()));

        // The roster owns its scroll · the hero bar and section label stay
        // pinned above while entity frames scroll under it.
        if (SpheneUI.Layout.BeginFillToBottomScrollArea("dataanalysis-overview-roster"))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            foreach (var entry in roster)
            {
                DrawEntityUnitFrame(entry);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            }
        }
        SpheneUI.Layout.EndScrollArea();
    }

    /// <summary>
    /// The audit command bar: status icon plate, title, live status line and
    /// the analyzer actions on the right · styled like a mission banner, not
    /// a settings card.
    /// </summary>
    /// <summary>The local player's profile avatar, cached by content.</summary>
    private IDalamudTextureWrap? GetProfileAvatar()
    {
        var uid = _apiController.UID;
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }
        var bytes = _spheneProfileManager.GetSpheneProfile(new UserData(uid)).ImageData.Value;
        if (bytes.Length == 0)
        {
            return null;
        }
        if (!bytes.SequenceEqual(_lastProfileAvatarBytes))
        {
            _lastProfileAvatarBytes = bytes;
            SpheneUI.Media.Evict(ProfileAvatarKey);
        }
        return SpheneUI.Media.Texture(ProfileAvatarKey, bytes);
    }

    private string ProfileAvatarKey => $"dataanalysis:avatar:{_apiController.UID}";

    /// <summary>
    /// The audit banner: the player's portrait plate with a live status badge,
    /// audit title + state, actions on the right, a cast bar while scanning
    /// and the totals ledger as a stat strip along the card's foot.
    /// </summary>
    private void DrawAuditCommandBar(bool isAnalyzing, bool hasMissing, Vector4 statusAccent)
    {
        SpheneUI.Layout.AccentCard(() =>
        {
            var scale = SpheneUI.Scale;
            var drawList = ImGui.GetWindowDrawList();
            var lineH = ImGui.GetTextLineHeight();

            // Portrait plate · the character's avatar, FFXIV-style double rim.
            var plate = 38f * scale;
            var plateMin = ImGui.GetCursorScreenPos();
            var plateMax = plateMin + new Vector2(plate);
            var plateRadius = plate * 0.18f;
            var avatar = GetProfileAvatar();
            drawList.AddRectFilled(plateMin, plateMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(statusAccent, 0.14f)), plateRadius);
            if (avatar != null)
            {
                var inset = 2f * scale;
                SpheneUI.Media.ImageInRect(drawList, avatar,
                    plateMin + new Vector2(inset), plateMax - new Vector2(inset),
                    ImageFit.Cover, null, plateRadius - inset);
            }
            else
            {
                var glyph = FontAwesomeIcon.UserAlt.ToIconString();
                using (SpheneUI.Icons.PushFont())
                {
                    var glyphSize = ImGui.CalcTextSize(glyph);
                    drawList.AddText(plateMin + (new Vector2(plate) - glyphSize) * 0.5f,
                        SpheneUI.ColorUtils.ToU32(statusAccent), glyph);
                }
            }
            drawList.AddRect(plateMin, plateMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(statusAccent, 0.5f)),
                plateRadius, ImDrawFlags.None, MathF.Max(1f, scale));
            drawList.AddRect(plateMin + new Vector2(1.5f * scale), plateMax - new Vector2(1.5f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.07f)),
                MathF.Max(1f, plateRadius - 1.5f * scale), ImDrawFlags.None, MathF.Max(1f, scale));

            // Status badge over the portrait's bottom-right corner.
            var badge = 11f * scale;
            var badgeMin = new Vector2(plateMax.X - badge * 0.72f - 1f * scale,
                plateMax.Y - badge * 0.72f - 1f * scale);
            var statusIcon = isAnalyzing ? FontAwesomeIcon.Sync
                : hasMissing ? FontAwesomeIcon.ExclamationTriangle
                : FontAwesomeIcon.Check;
            drawList.AddCircleFilled(badgeMin + new Vector2(badge * 0.5f),
                badge * 0.62f, SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBg));
            drawList.AddCircleFilled(badgeMin + new Vector2(badge * 0.5f),
                badge * 0.48f, SpheneUI.ColorUtils.ToU32(statusAccent));
            using (SpheneUI.Icons.PushFont())
            {
                var glyph = statusIcon.ToIconString();
                var glyphSize = ImGui.CalcTextSize(glyph) * 0.58f;
                drawList.AddText(ImGui.GetFont(), glyphSize.X,
                    badgeMin + (new Vector2(badge) - glyphSize) * 0.5f,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBg), glyph);
            }

            // Row bounds BEFORE any content · button docking must anchor to
            // the card's inner right edge, not the cursor after the text group.
            var rowRightEdge = plateMin.X + ImGui.GetContentRegionAvail().X;

            ImGui.Dummy(new Vector2(plate, plate));
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            using (var grp = ImRaii.Group())
            {
                SpheneUI.Text.Colored("CHARACTER AUDIT", statusAccent);
                SpheneUI.Text.Muted(isAnalyzing
                    ? $"Scanning entries · {_characterAnalyzer.CurrentFile}/{_characterAnalyzer.TotalFiles}"
                    : hasMissing
                        ? "Entries pending · some file sizes are not computed yet"
                        : "All entries measured · audit complete");
            }

            // Action buttons dock right, vertically centered against the
            // plate row. Widths are measured per icon · an icon button is
            // glyph + FramePadding wide, not a frame-height square.
            var buttonSize = ImGui.GetFrameHeight();
            var gap = SpheneUI.Spacing.X4.Scaled;
            var stopW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.StopCircle).X;
            var playW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.PlayCircle).X;
            var syncW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.SyncAlt).X;
            var buttonsW = isAnalyzing ? stopW : hasMissing ? playW + gap + syncW : syncW;
            var buttonsX = rowRightEdge - buttonsW;
            ImGui.SetCursorScreenPos(new Vector2(
                MathF.Max(ImGui.GetCursorScreenPos().X, buttonsX),
                plateMin.Y + (plate - buttonSize) * 0.5f));

            if (isAnalyzing)
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.StopCircle))
                {
                    _characterAnalyzer.CancelAnalyze();
                }
                SpheneUI.AttachTooltip("Cancel analysis");
            }
            else if (hasMissing)
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.PlayCircle))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false);
                }
                SpheneUI.AttachTooltip("Start analysis (missing entries)");
                ImGui.SameLine(0f, gap);
                if (SpheneUI.Icons.Button(FontAwesomeIcon.SyncAlt))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: true);
                }
                SpheneUI.AttachTooltip("Recalculate all entries");
            }
            else
            {
                if (SpheneUI.Icons.Button(FontAwesomeIcon.SyncAlt))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: true);
                }
                SpheneUI.AttachTooltip("Recalculate all entries");
            }

            // Scan progress as a thin cast bar under the header row.
            if (isAnalyzing && _characterAnalyzer.TotalFiles > 0)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var ratio = Math.Clamp(
                    _characterAnalyzer.CurrentFile / (float)_characterAnalyzer.TotalFiles, 0f, 1f);
                SpheneUI.Controls.ProgressBar(ratio,
                    new Vector2(SpheneUI.Controls.BoundedAvailX(), 10f * scale),
                    $"{_characterAnalyzer.CurrentFile} / {_characterAnalyzer.TotalFiles}",
                    ImGuiColors.DalamudYellow);
            }

            // Totals as an inspect-panel stat strip along the card's foot.
            if (_cachedAnalysis is { Count: > 0 })
            {
                var totalFiles = _cachedAnalysis.Values.Sum(c => c.Values.Count);
                var totalSize = _cachedAnalysis.Sum(c => c.Value.Sum(v => v.Value.OriginalSize));
                var totalCompressed = _cachedAnalysis.Sum(c => c.Value.Sum(v => v.Value.CompressedSize));
                var totalTris = _cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.Triangles));
                (string Label, string Value)[] stats =
                [
                    ("FILES", $"{totalFiles:N0}"),
                    ("ACTUAL", UiSharedService.ByteToString(totalSize)),
                    ("PACKED", UiSharedService.ByteToString(totalCompressed)),
                    ("TRIANGLES", $"{totalTris:N0}"),
                ];

                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var hairMin = ImGui.GetCursorScreenPos();
                var stripW = MathF.Max(60f * scale, SpheneUI.Controls.BoundedAvailX());
                drawList.AddLine(hairMin, hairMin + new Vector2(stripW, 0f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.4f)),
                    MathF.Max(1f, scale * 0.75f));
                ImGui.Dummy(new Vector2(stripW, 1f * scale));
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

                var statTop = ImGui.GetCursorScreenPos();
                var colW = stripW / stats.Length;
                var labelCol = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted);
                var valueCol = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary);
                var sepCol = SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.3f));
                var statH = lineH * 2f + 2f * scale;
                for (var i = 0; i < stats.Length; i++)
                {
                    var x = statTop.X + i * colW;
                    if (i > 0)
                    {
                        drawList.AddLine(new Vector2(x, statTop.Y + 1f * scale),
                            new Vector2(x, statTop.Y + statH - 1f * scale), sepCol,
                            MathF.Max(1f, scale * 0.75f));
                    }
                    var tx = x + (i > 0 ? 10f * scale : 0f);
                    drawList.AddText(new Vector2(tx, statTop.Y), labelCol, stats[i].Label);
                    drawList.AddText(new Vector2(tx, statTop.Y + lineH + 1f * scale),
                        valueCol, stats[i].Value);
                }
                ImGui.Dummy(new Vector2(stripW, statH));
            }
        }, accentColor: statusAccent);
    }

    /// <summary>Roster order · the player frame always leads, entities follow.</summary>
    private static readonly ObjectKind[] EntityOrder =
        [ObjectKind.Player, ObjectKind.MinionOrMount, ObjectKind.Companion, ObjectKind.Pet];

    // Minion/mount model ids live in the replaced game paths
    // (chara/minion/m####, chara/mount/m####) · the id doubles as the
    // Companion/Mount sheet row, so names and game icons resolve from it.
    private const string MinionPathMarker = "chara/minion/m";
    private const string MountPathMarker = "chara/mount/m";

    /// <summary>Sheet lookups are static per entity id · resolved once.</summary>
    private readonly Dictionary<(bool Mount, uint Id), (string? Name, uint Icon)> _entityIdentityCache = new();

    /// <summary>One roster row · an entity (or a single minion/mount model)
    /// with the files it contributes.</summary>
    private sealed record EntityRosterEntry(ObjectKind Kind, string Label, string? SubLabel,
        uint IconId, FontAwesomeIcon Glyph, bool Summoned,
        Dictionary<string, CharacterAnalyzer.FileDataEntry> Files);

    /// <summary>
    /// Builds the roster: entity kinds in fixed order, empty buckets skipped
    /// (no minion out → no minion frame), the MinionOrMount bucket split per
    /// model id so multiple minions/mounts show as their own entries.
    /// </summary>
    private List<EntityRosterEntry> BuildEntityRoster(
        Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>> analysis)
    {
        var roster = new List<EntityRosterEntry>();
        foreach (var kind in EntityOrder)
        {
            if (!analysis.TryGetValue(kind, out var files) || files.Count == 0)
            {
                continue;
            }
            if (kind != ObjectKind.MinionOrMount)
            {
                roster.Add(new EntityRosterEntry(kind, kind.ToString(), null,
                    0, ObjectKindIcon(kind), false, files));
                continue;
            }

            // Split the bucket per game entity so distinct minions/mounts
            // get their own frame with a resolved name and icon.
            var groups = new Dictionary<(bool Mount, uint Id), Dictionary<string, CharacterAnalyzer.FileDataEntry>>();
            var unclassified = new Dictionary<string, CharacterAnalyzer.FileDataEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var key = ClassifyEntityPath(file.Value.GamePaths);
                if (key == null)
                {
                    unclassified[file.Key] = file.Value;
                    continue;
                }
                if (!groups.TryGetValue(key.Value, out var group))
                {
                    groups[key.Value] = group = new(StringComparer.OrdinalIgnoreCase);
                }
                group[file.Key] = file.Value;
            }
            var live = GetLiveEntityIdentity();
            foreach (var group in groups.OrderBy(g => g.Key.Mount).ThenBy(g => g.Key.Id))
            {
                var (name, iconId) = ResolveEntityIdentity(group.Key.Mount, group.Key.Id);
                var typeLabel = group.Key.Mount ? "Mount" : "Minion";
                var summoned = live.Mount == group.Key.Mount && live.Id == group.Key.Id;
                roster.Add(new EntityRosterEntry(kind,
                    name ?? $"{typeLabel} m{group.Key.Id:D4}", typeLabel, iconId,
                    group.Key.Mount ? FontAwesomeIcon.Horse : FontAwesomeIcon.Paw,
                    summoned, group.Value));
            }
            if (unclassified.Count > 0)
            {
                // Game paths didn't carry a model id · fall back to the live
                // spawned entity so the frame still shows the real name+icon.
                var label = live.Name ?? "Minion / Mount";
                var sub = live.Mount == null ? null : live.Mount.Value ? "Mount" : "Minion";
                roster.Add(new EntityRosterEntry(kind, label, sub, live.Icon,
                    live.Mount == true ? FontAwesomeIcon.Horse : ObjectKindIcon(kind),
                    live.Mount != null, unclassified));
            }
        }
        return roster;
    }

    private static (bool Mount, uint Id)? ClassifyEntityPath(List<string> gamePaths)
    {
        foreach (var path in gamePaths)
        {
            var mount = ModelIdAfter(path, MountPathMarker);
            if (mount != null)
            {
                return (true, mount.Value);
            }
            var minion = ModelIdAfter(path, MinionPathMarker);
            if (minion != null)
            {
                return (false, minion.Value);
            }
        }
        return null;
    }

    /// <summary>Reads the digits after a "…/m" marker in a game path.</summary>
    private static uint? ModelIdAfter(string path, string marker)
    {
        var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }
        var i = start + marker.Length;
        uint id = 0;
        var digits = 0;
        while (i < path.Length && path[i] >= '0' && path[i] <= '9')
        {
            id = id * 10 + (uint)(path[i] - '0');
            i++;
            digits++;
        }
        return digits > 0 ? id : null;
    }

    /// <summary>The minion or mount currently summoned on the local player  · 
    /// the object table exposes the sheet row reference directly, so name and
    /// icon resolve through the same cached lookup as path-derived ids.
    /// <c>Id</c> is the sheet row id, matching the m#### model id in paths.</summary>
    private (bool? Mount, uint Id, string? Name, uint Icon) GetLiveEntityIdentity()
    {
        try
        {
            var player = _objectTable.LocalPlayer;
            if (player == null)
            {
                return (null, 0, null, 0);
            }
            if (player.CurrentMount is { IsValid: true } mount)
            {
                var (name, icon) = ResolveEntityIdentity(true, mount.RowId);
                return (true, mount.RowId, name, icon);
            }
            if (player.CurrentMinion is { IsValid: true } minion)
            {
                var (name, icon) = ResolveEntityIdentity(false, minion.RowId);
                return (false, minion.RowId, name, icon);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve live minion/mount identity");
        }
        return (null, 0, null, 0);
    }

    /// <summary>Companion/Mount sheet row → display name + game icon id.</summary>
    private (string? Name, uint Icon) ResolveEntityIdentity(bool mount, uint id)
    {
        if (_entityIdentityCache.TryGetValue((mount, id), out var cached))
        {
            return cached;
        }
        string? name = null;
        uint icon = 0;
        try
        {
            if (mount)
            {
                var row = _gameData.GetExcelSheet<Lumina.Excel.Sheets.Mount>()?.GetRowOrDefault(id);
                if (row.HasValue)
                {
                    name = row.Value.Singular.ToString();
                    icon = row.Value.Icon;
                }
            }
            else
            {
                var row = _gameData.GetExcelSheet<Lumina.Excel.Sheets.Companion>()?.GetRowOrDefault(id);
                if (row.HasValue)
                {
                    name = row.Value.Singular.ToString();
                    icon = row.Value.Icon;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve entity identity {type} {id}",
                mount ? "mount" : "minion", id);
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = null;
        }
        var result = (name, icon);
        _entityIdentityCache[(mount, id)] = result;
        return result;
    }

    /// <summary>
    /// One roster entry · a unit frame in standard surface chrome (tonal
    /// panel + top accent seam in the entity's class color): portrait plate
    /// (the real game icon for minions/mounts), name + stats, debuff icons
    /// for warnings and resource bars (VRAM / triangles) against the
    /// configured thresholds. Click opens the entity's file list.
    /// </summary>
    private void DrawEntityUnitFrame(EntityRosterEntry entry)
    {
        var scale = SpheneUI.Scale;
        var kindColor = ObjectKindColor(entry.Kind);
        var files = entry.Files;
        var groupedFiles = files.Values.GroupBy(f => f.FileType, StringComparer.Ordinal)
            .OrderByDescending(k => k.Count()).ToList();
        var hasUncomputed = files.Values.Any(f => !f.IsComputed);
        var texGroup = groupedFiles.SingleOrDefault(
            v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
        var actualTris = files.Values.Sum(f => f.Triangles);
        var totalSize = files.Values.Sum(v => v.OriginalSize);
        var totalCompressed = files.Values.Sum(v => v.CompressedSize);

        var warnThresholds = _playerPerformanceConfig.Current.WarnOnExceedingThresholds
            || _playerPerformanceConfig.Current.ShowPerformanceIndicator;
        var vramThreshold = _playerPerformanceConfig.Current.VRAMSizeWarningThresholdMiB * 1024f * 1024f;
        var trisThreshold = _playerPerformanceConfig.Current.TrisWarningThresholdThousands * 1000f;
        var vram = texGroup?.Sum(f => f.OriginalSize) ?? 0;
        var showVramBar = texGroup != null && warnThresholds && vramThreshold > 0f;
        var showTrisBar = warnThresholds && trisThreshold > 0f;
        var vramOver = showVramBar && vram > vramThreshold;
        var trisOver = showTrisBar && actualTris > trisThreshold;

        var lineH = ImGui.GetTextLineHeight();
        var pad = SpheneUI.Spacing.CardPadding.Scaled;
        var theme = SpheneTheme.Current;
        var barH = MathF.Min(theme.CompactProgressBarHeight, 14f * scale);
        var barGap = 5f * scale;
        var barCount = (showVramBar ? 1 : 0) + (showTrisBar ? 1 : 0);
        var lowerH = barCount > 0 ? barCount * barH + (barCount - 1) * barGap : lineH;
        var rowGap = 4f * scale;
        var frameH = pad * 2f + lineH + rowGap + lineH + rowGap + lowerH + rowGap;

        // Right-side navigation: two stacked icon buttons (Files / Textures),
        // each tinted · Files carries the entity class color like the seam.
        var navBtnH = ImGui.GetFrameHeight();
        var navGap = 4f * scale;
        var navW = MathF.Max(
            SpheneUI.Controls.SegAutoWidth("Files", FontAwesomeIcon.FolderOpen, navBtnH),
            texGroup != null
                ? SpheneUI.Controls.SegAutoWidth("Textures", FontAwesomeIcon.Images, navBtnH)
                : 0f);

        // The frame body is one invisible button · full-row hit zone, except
        // the nav reserve on the right - submitted first it would claim the
        // hover id and swallow every click meant for Files/Textures.
        var frameW = MathF.Max(120f * scale, SpheneUI.Controls.BoundedAvailX());
        ImGui.PushID(entry.Label + entry.SubLabel);
        var frameClicked = ImGui.InvisibleButton("##unitframe",
            new Vector2(MathF.Max(0f, frameW - navW - pad - 6f * scale), frameH));
        var frameHovered = ImGui.IsItemHovered();
        var fMin = ImGui.GetItemRectMin();
        var fMax = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        // SetCursorScreenPos for the nav buttons moves the layout cursor into
        // the frame · restore it afterwards so the next row doesn't overlap.
        var resumePos = ImGui.GetCursorScreenPos();

        // Standard section surface: tonal panel + accent seam along the top,
        // the seam carries the entity's class color like a party frame.
        var radius = SpheneUI.Layout.AccentCardRounding * scale;
        drawList.AddRectFilled(fMin, fMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.Layout.NavRailBandColor()), radius);
        SpheneUI.Chrome.SectionCardBackdrop(drawList, fMin, fMax, radius, kindColor);
        var seamAlpha = frameHovered ? 0.55f : 0.32f;
        var seamColor = SpheneUI.ColorUtils.WithAlpha(kindColor, seamAlpha);
        var seamCol = ImGui.ColorConvertFloat4ToU32(seamColor);
        var seamFade = ImGui.ColorConvertFloat4ToU32(SpheneUI.ColorUtils.WithAlpha(seamColor, 0f));
        var seamH = MathF.Max(1f, 1.5f * scale);
        var seamInset = radius * 0.9f;
        var seamFadeW = 24f * scale;
        var seamMin = new Vector2(fMin.X + seamInset, fMin.Y + 0.5f);
        drawList.AddRectFilledMultiColor(seamMin,
            new Vector2(seamMin.X + seamFadeW, seamMin.Y + seamH),
            seamFade, seamCol, seamCol, seamFade);
        drawList.AddRectFilled(new Vector2(seamMin.X + seamFadeW, seamMin.Y),
            new Vector2(fMax.X - seamInset - seamFadeW, seamMin.Y + seamH), seamCol);
        drawList.AddRectFilledMultiColor(
            new Vector2(fMax.X - seamInset - seamFadeW, seamMin.Y),
            new Vector2(fMax.X - seamInset, seamMin.Y + seamH),
            seamCol, seamFade, seamFade, seamCol);

        // Portrait plate · the player's avatar, the entity's real game icon
        // (minions/mounts), else a class-colored glyph. Double hairline rim.
        var plate = lowerH + lineH + rowGap + 4f * scale;
        var plateMin = fMin + new Vector2(pad, (frameH - plate) * 0.5f);
        var plateRadius = plate * 0.18f;
        var plateMax = plateMin + new Vector2(plate);
        drawList.AddRectFilled(plateMin, plateMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(kindColor, 0.15f)), plateRadius);
        var iconWrap = entry.IconId > 0
            ? SpheneUI.Media.GameIcon(entry.IconId)
            : entry.Kind == ObjectKind.Player ? GetProfileAvatar() : null;
        if (iconWrap != null)
        {
            var inset = 2f * scale;
            SpheneUI.Media.ImageInRect(drawList, iconWrap,
                plateMin + new Vector2(inset), plateMax - new Vector2(inset),
                ImageFit.Cover, null, plateRadius - inset);
        }
        else
        {
            var glyph = entry.Glyph.ToIconString();
            using (SpheneUI.Icons.PushFont())
            {
                var glyphSize = ImGui.CalcTextSize(glyph);
                drawList.AddText(plateMin + (new Vector2(plate) - glyphSize) * 0.5f,
                    SpheneUI.ColorUtils.ToU32(kindColor), glyph);
            }
        }
        drawList.AddRect(plateMin, plateMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(kindColor, 0.55f)),
            plateRadius, ImDrawFlags.None, MathF.Max(1f, scale));
        drawList.AddRect(plateMin + new Vector2(1.5f * scale), plateMax - new Vector2(1.5f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.07f)),
            MathF.Max(1f, plateRadius - 1.5f * scale), ImDrawFlags.None, MathF.Max(1f, scale));

        // Right zone: the Files/Textures buttons dock here · content stays left.
        var textRightEdge = fMax.X - pad - navW - 6f * scale;

        // Name row: entity label in its class color, type + file count muted.
        var textX = plateMin.X + plate + 10f * scale;
        var nameY = fMin.Y + pad;
        drawList.AddText(new Vector2(textX, nameY), SpheneUI.ColorUtils.ToU32(kindColor), entry.Label);
        var nameEndX = textX + ImGui.CalcTextSize(entry.Label).X;
        var sub = entry.SubLabel != null
            ? $"{entry.SubLabel} · {files.Count} files in use"
            : $"{files.Count} files in use";
        drawList.AddText(new Vector2(nameEndX + 8f * scale, nameY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), sub);
        var subEndX = nameEndX + 8f * scale + ImGui.CalcTextSize(sub).X;

        // Live marker · the entity currently summoned in-game gets a green
        // "summoned" tag so it stands out among multiple minions/mounts.
        if (entry.Summoned)
        {
            var tag = "summoned";
            var tagW = ImGui.CalcTextSize(tag).X + 14f * scale;
            var tagMin = new Vector2(subEndX + 8f * scale, nameY - 1f * scale);
            var tagMax = tagMin + new Vector2(tagW, lineH + 2f * scale);
            var tagColor = SpheneUI.Colors.Success;
            drawList.AddRectFilled(tagMin, tagMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(tagColor, 0.16f)),
                (lineH + 2f * scale) * 0.5f);
            drawList.AddRect(tagMin, tagMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(tagColor, 0.45f)),
                (lineH + 2f * scale) * 0.5f);
            var dotR = 2f * scale;
            var dotC = tagMin + new Vector2(7f * scale, (lineH + 2f * scale) * 0.5f);
            drawList.AddCircleFilled(dotC, dotR, SpheneUI.ColorUtils.ToU32(tagColor));
            drawList.AddText(tagMin + new Vector2(11f * scale, 1f * scale),
                SpheneUI.ColorUtils.ToU32(tagColor), tag);
            subEndX = tagMax.X;
        }

        // Debuff icons · warning squares right after the name row.
        var debuff = subEndX + 8f * scale;
        if (hasUncomputed)
        {
            debuff += DrawDebuffIcon(drawList, debuff, nameY, lineH,
                FontAwesomeIcon.Question, "Some files have no computed size yet · run an analysis.");
        }
        if (vramOver)
        {
            debuff += DrawDebuffIcon(drawList, debuff, nameY, lineH,
                FontAwesomeIcon.Microchip,
                $"VRAM usage exceeds the configured threshold by {UiSharedService.ByteToString((long)(vram - vramThreshold))}.");
        }
        if (trisOver)
        {
            DrawDebuffIcon(drawList, debuff, nameY, lineH,
                FontAwesomeIcon.DrawPolygon,
                $"Triangle count exceeds the configured threshold of {_playerPerformanceConfig.Current.TrisWarningThresholdThousands * 1000:N0} by {actualTris - (long)trisThreshold:N0}.");
        }

        // File-type breakdown · which formats the entity is made of.
        var infoY = nameY + lineH + rowGap;
        var breakdown = string.Join("  ·  ", groupedFiles.Take(5).Select(g => $"{g.Key} ×{g.Count()}"));
        var sizesText = $"{UiSharedService.ByteToString(totalSize)} actual · {UiSharedService.ByteToString(totalCompressed)} packed · {actualTris:N0} triangles";
        drawList.AddText(new Vector2(textX, infoY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), breakdown);
        var breakdownEndX = textX + ImGui.CalcTextSize(breakdown).X;
        if (breakdownEndX + 12f * scale + ImGui.CalcTextSize(sizesText).X < textRightEdge)
        {
            drawList.AddText(new Vector2(textRightEdge - ImGui.CalcTextSize(sizesText).X, infoY),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), sizesText);
        }

        // Resource bars against the configured thresholds (or nothing · the
        // stat line above already carries the numbers).
        var barsX = textX;
        var barsW = MathF.Max(60f * scale, textRightEdge - barsX);
        var barY = infoY + lineH + rowGap;
        if (showVramBar)
        {
            var ratio = Math.Clamp(vram / vramThreshold, 0f, 1f);
            DrawUnitBar(drawList, new Vector2(barsX, barY),
                barsW, barH, "VRAM", ratio, ThresholdColor(ratio),
                $"{UiSharedService.ByteToString(vram)} / {_playerPerformanceConfig.Current.VRAMSizeWarningThresholdMiB} MiB");
            barY += barH + barGap;
        }
        if (showTrisBar)
        {
            var ratio = Math.Clamp(actualTris / trisThreshold, 0f, 1f);
            DrawUnitBar(drawList, new Vector2(barsX, barY),
                barsW, barH, "TRIANGLES", ratio, ThresholdColor(ratio),
                $"{actualTris:N0} / {_playerPerformanceConfig.Current.TrisWarningThresholdThousands}k");
        }
        if (barCount == 0)
        {
            drawList.AddText(new Vector2(barsX, barY),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), sizesText);
        }

        // Navigation: two stacked icon buttons docked at the row's right edge  · 
        // they win hover over the frame-wide hit zone.
        var navRows = texGroup != null ? 2 : 1;
        var navBlockH = navRows * navBtnH + (navRows - 1) * navGap;
        var navX = fMax.X - pad - navW;
        var navY = fMin.Y + (frameH - navBlockH) * 0.5f;
        var navBtnSize = new Vector2(navW, navBtnH);
        ImGui.SetCursorScreenPos(new Vector2(navX, navY));
        var filesClicked = SpheneUI.Controls.SegmentButton("Files",
            FontAwesomeIcon.FolderOpen, navBtnSize, kindColor);
        var navHovered = ImGui.IsItemHovered();
        var texClicked = false;
        if (texGroup != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(navX, navY + navBtnH + navGap));
            texClicked = SpheneUI.Controls.SegmentButton("Textures",
                FontAwesomeIcon.Images, navBtnSize, SpheneUI.Palette.EtherealPurple);
            navHovered |= ImGui.IsItemHovered();
        }

        if (frameHovered && !navHovered)
        {
            SpheneUI.AttachTooltip(
                $"{entry.Label} · {files.Count} files, {UiSharedService.ByteToString(totalSize)} actual, {UiSharedService.ByteToString(totalCompressed)} packed");
        }
        if (filesClicked || (frameClicked && !navHovered))
        {
            SelectObjectTab(entry.Kind);
            SetPage(DataAnalysisPage.Files);
        }
        if (texClicked)
        {
            SelectObjectTab(entry.Kind);
            SetPage(DataAnalysisPage.Textures);
        }
        ImGui.SetCursorScreenPos(resumePos);
        ImGui.PopID();
    }

    /// <summary>Class-color per entity kind · the roster tints each unit frame
    /// like an MMO party list.</summary>
    private static Vector4 ObjectKindColor(ObjectKind kind) => kind switch
    {
        ObjectKind.Player => SpheneUI.Palette.CrystalBlue,
        ObjectKind.MinionOrMount => SpheneUI.Palette.SpheneGold,
        ObjectKind.Companion => SpheneUI.Palette.EtherealPurple,
        ObjectKind.Pet => ImGuiColors.ParsedGreen,
        _ => SpheneTheme.Current.AccentBlue,
    };

    private static Vector4 ThresholdColor(float ratio) => ratio < 0.7f
        ? ImGuiColors.ParsedGreen
        : ratio < 1f ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudRed;

    /// <summary>A warning "debuff" square: tinted rounded tile with a glyph  · 
    /// hovered it explains itself like a status effect.</summary>
    private static float DrawDebuffIcon(ImDrawListPtr drawList, float x, float y, float h,
        FontAwesomeIcon icon, string tooltip)
    {
        var scale = SpheneUI.Scale;
        var size = h + 2f * scale;
        var min = new Vector2(x, y - 1f * scale);
        var warn = ImGuiColors.DalamudYellow;
        drawList.AddRectFilled(min, min + new Vector2(size),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(warn, 0.14f)), 3f * scale);
        // FFXIV debuff cue: the accent edge sits under the tile, not around it.
        drawList.AddRectFilled(min + new Vector2(2f * scale, size - 2.5f * scale),
            min + new Vector2(size - 2f * scale, size - 0.5f * scale),
            SpheneUI.ColorUtils.ToU32(warn), 1f * scale);
        drawList.AddRect(min, min + new Vector2(size),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(warn, 0.35f)),
            3f * scale, ImDrawFlags.None, MathF.Max(1f, scale));
        var glyph = icon.ToIconString();
        using (SpheneUI.Icons.PushFont())
        {
            var gSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(min + (new Vector2(size) - gSize) * 0.5f,
                SpheneUI.ColorUtils.ToU32(warn), glyph);
        }
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##debuff", new Vector2(size));
        if (ImGui.IsItemHovered())
        {
            SpheneUI.AttachTooltip(tooltip);
        }
        return size + 4f * scale;
    }

    /// <summary>Unit-frame resource bar: dark trough, ratio fill with a top
    /// sheen line, label left and "used / limit" readout right · both inside
    /// the bar like an MMO cast/health bar.</summary>
    private static void DrawUnitBar(ImDrawListPtr drawList, Vector2 min, float width,
        float height, string label, float ratio, Vector4 fillColor, string valueText)
    {
        var scale = SpheneUI.Scale;
        var theme = SpheneTheme.Current;
        var max = min + new Vector2(width, height);
        var radius = MathF.Min(theme.ProgressBarRounding, height * 0.5f);

        drawList.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
            theme.CompactProgressBarBackground), radius);
        if (ratio > 0.005f)
        {
            var fillEnd = min.X + MathF.Max(radius * 2f, width * ratio);
            var fillMax = new Vector2(fillEnd, max.Y);
            var bright = SpheneUI.ColorUtils.LerpColor(fillColor, Vector4.One, 0.28f);
            SpheneUI.Chrome.RoundedHorizontalGradient(drawList, min, fillMax, radius,
                SpheneUI.ColorUtils.WithAlpha(fillColor, 0.5f),
                SpheneUI.ColorUtils.WithAlpha(bright, 0.9f));
            // bright tip · the light at the fill's leading edge, cast-bar style
            drawList.AddLine(new Vector2(fillEnd - 1f, min.Y + 1.5f * scale),
                new Vector2(fillEnd - 1f, max.Y - 1.5f * scale),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.55f)),
                MathF.Max(1f, scale));
        }
        drawList.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            theme.CompactProgressBarBorder), radius,
            ImDrawFlags.None, MathF.Max(1f, scale));

        var labelColor = SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.TextSecondary, 0.95f));
        var valueColor = SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary);
        var shadow = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.Zero, 0.55f));
        var textY = min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;
        drawList.PushClipRect(min, max, true);
        SpheneUI.Text.OutlinedOn(drawList, label,
            new Vector2(min.X + 6f * scale, textY), labelColor, shadow);
        var valueW = ImGui.CalcTextSize(valueText).X;
        SpheneUI.Text.OutlinedOn(drawList, valueText,
            new Vector2(max.X - 6f * scale - valueW, textY), valueColor, shadow);
        drawList.PopClipRect();
    }

    private void DrawFilesPage()
    {
        DrawPageTitle(
            "Browse every file currently in use · pick an entity type and file type, then click a row for details.");
        if (!EnsureAnalysisData()) return;
        var analysis = _cachedAnalysis!;

        EnsureObjectSelection();
        DrawObjectKindChips(analysis.Keys, SpheneTheme.Current.AccentBlue);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var groupedfiles = analysis[_selectedObjectTab].Values
            .GroupBy(f => f.FileType, StringComparer.Ordinal)
            .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
        if (!groupedfiles.Any(g => string.Equals(g.Key, _selectedFileTypeTab, StringComparison.Ordinal)))
        {
            _selectedFileTypeTab = groupedfiles[0].Key;
        }
        DrawFileTypeChips(groupedfiles);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        var fileGroup = groupedfiles.First(g => string.Equals(g.Key, _selectedFileTypeTab, StringComparison.Ordinal));
        SpheneUI.Text.Muted($"{fileGroup.Key} files: {fileGroup.Count()} · " +
            $"actual: {UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize))} · " +
            $"compressed: {UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize))}");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // The detail card is pinned above the table so the table can fill the
        // remaining height exactly · no fixed reserve needed.
        DrawSelectedFileCard();
        DrawTable("analysisfiles", fileGroup, SpheneUI.Layout.FillToViewBottomHeight());
    }

    private void DrawTexturesPage()
    {
        DrawPageTitle(
            "BC7 conversion, backups and storage for the texture files your entities use.");
        if (!EnsureAnalysisData()) return;
        var analysis = _cachedAnalysis!;

        var texKinds = analysis.Keys
            .Where(k => analysis[k].Values.Any(f => string.Equals(f.FileType, "tex", StringComparison.Ordinal)))
            .ToList();
        if (texKinds.Count == 0)
        {
            SpheneUI.Layout.Callout("No texture files found in the current analysis.",
                FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudGrey);
            return;
        }
        if (!texKinds.Contains(_selectedObjectTab))
        {
            _selectedObjectTab = texKinds[0];
            _selectedHash = string.Empty;
            _selectedFileTypeTab = "tex";
            _texturesToConvert.Clear();
        }
        DrawObjectKindChips(texKinds, SpheneUI.Palette.EtherealPurple);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var fileGroup = analysis[_selectedObjectTab].Values
            .GroupBy(f => f.FileType, StringComparer.Ordinal)
            .First(g => string.Equals(g.Key, "tex", StringComparison.Ordinal));

        SpheneUI.Text.Muted($"tex files: {fileGroup.Count()} · " +
            $"actual: {UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize))} · " +
            $"compressed: {UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize))}");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);

        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Controls.Toggle("Enable BC7 conversion mode", ref _enableBc7ConversionMode);
            if (!_enableBc7ConversionMode)
            {
                SpheneUI.Text.MutedWrapped("Enable BC7 mode to manage texture backups, see backup storage usage, and convert loaded textures to the BC7 format.");
                return;
            }
            SpheneUI.Controls.Toggle("Create backup before conversion", ref _enableBackupBeforeConversion);
            SpheneUI.AttachTooltip("Stores original textures prior to conversion. Smaller storage footprint than full-mod PMP but only per-file restore.");
            if (_enableBackupBeforeConversion)
            {
                SpheneUI.Text.ColoredWrapped("Backups will be created in the mod folder under 'sphene_backups' before conversion.", ImGuiColors.ParsedGreen);
                SpheneUI.Text.ColoredWrapped("Using full-mod PMP backups increases required storage, but enables safer complete mod restoration.", ImGuiColors.DalamudYellow);
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            SpheneUI.Layout.Separator();
            SpheneUI.Text.SectionHeader("Backup Management", icon: FontAwesomeIcon.Undo);
            var availableBackups = GetCachedBackupsForCurrentAnalysis();
            if (availableBackups.Count > 0)
            {
                SpheneUI.Text.Muted($"{availableBackups.Count} backed up texture(s) from the current analysis can be restored.");
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Undo, $"Revert current textures ({availableBackups.Count})"))
                {
                    StartTextureRevert(availableBackups);
                }
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.FolderOpen, "Open backup folder"))
                {
                    _ = OpenBackupFolderAsync();
                }
            }
            else
            {
                SpheneUI.Layout.EmptyState(FontAwesomeIcon.Database, "No backups",
                    "Texture backups created during conversion land here.");
            }

            SpheneUI.Layout.Separator();
            SpheneUI.Text.SectionHeader("Storage", icon: FontAwesomeIcon.Hdd);
            DrawStorageInfo();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout("Converting textures to BC7 reduces their size drastically and is recommended for large (4k+) textures. "
                + "Textures utilizing colorsets may produce visual artifacts. Originals are backed up automatically before conversion. "
                + "All texture duplicates (entries with more than one file path) are converted · this is an expensive operation that can take a while.",
                FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudYellow, "BC7 conversion");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            var nonBc7Textures = GetNonBc7Textures(fileGroup);
            if (nonBc7Textures.Count > 0 && SpheneUI.Icons.TextButton(FontAwesomeIcon.Compress, $"Convert all {nonBc7Textures.Count} non-BC7 textures"))
            {
                _logger.LogDebug("One-click conversion button clicked. Found {Count} non-BC7 textures", nonBc7Textures.Count);
                foreach (var texture in nonBc7Textures)
                {
                    var primaryPath = texture.FilePaths[0];
                    var duplicatePaths = texture.FilePaths.Skip(1).ToArray();
                    _texturesToConvert[primaryPath] = duplicatePaths;
                }
            }

            if (_texturesToConvert.Count > 0 && SpheneUI.Controls.SegmentButton(
                    $"Start conversion of {_texturesToConvert.Count} texture(s)", FontAwesomeIcon.PlayCircle,
                    accent: SpheneUI.Palette.EtherealPurple))
            {
                _conversionTask = Task.Run(async () =>
                {
                    var dict = await BuildFullModConversionDictionaryFromSelection().ConfigureAwait(false);
                    await _shrinkuConversionService.StartConversionAsync(dict).ConfigureAwait(false);
                });
                _ = _conversionTask.ContinueWith(t => { }, TaskScheduler.Default);
            }
        }, title: "BC7 Conversion", accentColor: SpheneUI.Palette.EtherealPurple);

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        DrawTable("analysistex", fileGroup, SpheneUI.Layout.FillToViewBottomHeight());
    }

    private void DrawStorageInfo()
    {
        // Refresh storage info every 5 seconds once the previous task finished.
        var now = DateTime.UtcNow;
        if (_storageInfoTask == null || (((now - _lastStorageInfoUpdate).TotalSeconds > 5) && _storageInfoTask.IsCompleted))
        {
            _storageInfoTask = GetShrinkUStorageInfoAsync();
            _lastStorageInfoUpdate = now;
        }

        if (_storageInfoTask != null && _storageInfoTask.IsCompleted)
        {
            _cachedStorageInfo = _storageInfoTask.Result;
        }

        var (totalSize, fileCount) = _cachedStorageInfo;
        if (fileCount > 0)
        {
            var sizeInMB = totalSize / (1024.0 * 1024.0);
            var updating = _storageInfoTask?.IsCompleted == false ? " · updating…" : string.Empty;
            SpheneUI.Text.Muted($"Total backup storage: {sizeInMB:F2} MB ({fileCount} files){updating}");
        }
        else
        {
            SpheneUI.Text.Muted(_storageInfoTask?.IsCompleted == false
                ? "Calculating storage usage…" : "No backup files found.");
        }
    }

    private void DrawTransientsPage()
    {
        DrawPageTitle(
            "Files that cannot be resolved to your character permanently · gathered in the background.");
        DrawStoredData();
    }

    private void DrawRecordingPage()
    {
        DrawPageTitle(
            "Capture transient data for mods that do not sync correctly · especially modded models and animations.");
        DrawRecording();
    }

    private bool EnsureAnalysisData()
    {
        if (_cachedAnalysis != null && _cachedAnalysis.Count > 0)
        {
            return true;
        }
        SpheneUI.Layout.Callout("No analysis data yet · start an analysis from the Overview page first.",
            FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudGrey);
        return false;
    }

    private void EnsureObjectSelection()
    {
        if (_cachedAnalysis == null || _cachedAnalysis.Count == 0) return;
        if (!_cachedAnalysis.ContainsKey(_selectedObjectTab))
        {
            SelectObjectTab(_cachedAnalysis.Keys.First());
        }
    }

    private void SelectObjectTab(ObjectKind kind)
    {
        if (_selectedObjectTab == kind) return;
        _selectedObjectTab = kind;
        _selectedHash = string.Empty;
        _selectedFileTypeTab = string.Empty;
        _texturesToConvert.Clear();
    }

    private void DrawObjectKindChips(IEnumerable<ObjectKind> kinds, Vector4 accent)
    {
        var first = true;
        foreach (var kind in kinds)
        {
            if (!first) ImGui.SameLine();
            first = false;
            var uncomputed = _cachedAnalysis![kind].Values.Any(f => !f.IsComputed);
            var label = uncomputed ? $"{kind} (!)" : kind.ToString();
            var chipAccent = uncomputed ? ImGuiColors.DalamudYellow : accent;
            if (SpheneUI.Controls.SegmentButton(label, ObjectKindIcon(kind),
                    accent: chipAccent, active: _selectedObjectTab == kind))
            {
                SelectObjectTab(kind);
            }
        }
    }

    private void DrawFileTypeChips(List<IGrouping<string, CharacterAnalyzer.FileDataEntry>> groups)
    {
        var first = true;
        foreach (var group in groups)
        {
            if (!first) ImGui.SameLine();
            first = false;
            var requiresCompute = group.Any(k => !k.IsComputed);
            var label = $"{group.Key} [{group.Count()}]" + (requiresCompute ? " (!)" : string.Empty);
            Vector4? accent = requiresCompute ? ImGuiColors.DalamudYellow : null;
            if (SpheneUI.Controls.SegmentButton(label, null, accent: accent,
                    active: string.Equals(_selectedFileTypeTab, group.Key, StringComparison.Ordinal)))
            {
                _selectedFileTypeTab = group.Key;
                _selectedHash = string.Empty;
                _texturesToConvert.Clear();
            }
        }
    }

    private void DrawSelectedFileCard()
    {
        if (_cachedAnalysis == null
            || !_cachedAnalysis.TryGetValue(_selectedObjectTab, out var selectedEntries)
            || !selectedEntries.TryGetValue(_selectedHash, out var item) || item == null)
        {
            return;
        }

        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Muted("Selected file:");
            ImGui.SameLine();
            SpheneUI.Text.Colored(_selectedHash, ImGuiColors.DalamudYellow);

            var filePaths = item.FilePaths;
            if (filePaths.Count > 0)
            {
                SpheneUI.Text.Muted("Local file path:");
                ImGui.SameLine();
                SpheneUI.Text.Wrapped(filePaths[0]);
                if (filePaths.Count > 1)
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Plain($"(and {filePaths.Count - 1} more)");
                    ImGui.SameLine();
                    SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle);
                    SpheneUI.AttachTooltip(string.Join(Environment.NewLine, filePaths.Skip(1)));
                }
            }

            var gamepaths = item.GamePaths;
            if (gamepaths.Count > 0)
            {
                SpheneUI.Text.Muted("Used by game path:");
                ImGui.SameLine();
                SpheneUI.Text.Wrapped(gamepaths[0]);
                if (gamepaths.Count > 1)
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Plain($"(and {gamepaths.Count - 1} more)");
                    ImGui.SameLine();
                    SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle);
                    SpheneUI.AttachTooltip(string.Join(Environment.NewLine, gamepaths.Skip(1)));
                }
            }
        }, title: "Selected File");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    private bool _showAlreadyAddedTransients = false;
    private bool _acknowledgeReview = false;
    private const string StoredAllJobsKey = "alljobs";
    private const string StoredPetPrefix = "pet:";
    private const string StoredMinionMountSessionKey = "minionmount:session";
    private string _selectedStoredCharacter = string.Empty;
    private string _selectedJobEntry = string.Empty;
    private readonly List<string> _storedPathsToRemove = [];
    private int _storedPathsAnchor = -1;
    private readonly Dictionary<string, string> _filePathResolve = [];
    private string _filterGamePath = string.Empty;
    private string _filterFilePath = string.Empty;

    private void DrawStoredData()
    {
        SpheneUI.Layout.HelpExtra(() =>
        {
            SpheneUI.Text.Wrapped("This tab allows you to see which transient files are attached to your character.");
            SpheneUI.Text.Wrapped("Transient files are files that cannot be resolved to your character permanently. Sphene gathers these files in the background while you execute animations, VFX, sound effects, etc.");
            SpheneUI.Text.Wrapped("When sending your character data to others, Sphene will combine the files listed in \"All Jobs\" and the corresponding currently used job.");
            SpheneUI.Text.Wrapped("The purpose of this tab is primarily informational for you to see which files you are carrying with you. You can remove added game paths, however if you are using the animations etc. again, "
                + "Sphene will automatically attach these after using them. If you disable associated mods in Penumbra, the associated entries here will also be deleted automatically.");
        });

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        var config = _transientConfigService.Current.TransientConfigs;
        if (!string.IsNullOrWhiteSpace(_selectedStoredCharacter) && !config.ContainsKey(_selectedStoredCharacter))
        {
            _selectedStoredCharacter = string.Empty;
            _selectedJobEntry = string.Empty;
            _storedPathsToRemove.Clear();
            _filePathResolve.Clear();
            _filterFilePath = string.Empty;
            _filterGamePath = string.Empty;
        }
        config.TryGetValue(_selectedStoredCharacter ?? string.Empty, out var transientStorage);
        bool selectedData = !string.IsNullOrWhiteSpace(_selectedStoredCharacter) && transientStorage != null;

        var scale = SpheneUI.Scale;
        var accent = SpheneUI.Palette.SpheneGold;

        // Character picker · a compact dropdown keeps the vertical space for
        // the actual content instead of a permanent list column.
        SpheneUI.Text.Muted("Character");
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        var preview = "Select a character…";
        foreach (var entry in config)
        {
            if (string.Equals(_selectedStoredCharacter, entry.Key, StringComparison.Ordinal))
            {
                preview = StoredCharacterLabel(entry.Key) ?? preview;
            }
        }
        ImGui.SetNextItemWidth(MathF.Min(340f * scale, SpheneUI.Controls.BoundedAvailX() * 0.55f));
        using (ImRaii.Disabled(config.Count == 0))
        {
            if (SpheneUI.Controls.BeginComboBox("##charpick", preview))
            {
                foreach (var entry in config)
                {
                    var label = StoredCharacterLabel(entry.Key);
                    if (string.IsNullOrEmpty(label)) continue;
                    if (SpheneUI.Controls.Selectable(label,
                        string.Equals(_selectedStoredCharacter, entry.Key, StringComparison.Ordinal)))
                    {
                        _selectedStoredCharacter = entry.Key;
                        _selectedJobEntry = string.Empty;
                        _storedPathsToRemove.Clear();
                        _filePathResolve.Clear();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
                SpheneUI.Controls.EndComboBox();
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // Side-by-side via sibling groups · a layout table wraps cells in
        // CellPadding and pushes the fill-height children past the page
        // bottom, creating a phantom scroll range. The rail floats without
        // a border; both boxes get the same precomputed fill height so
        // their bottom edges align.
        var railW = 132f * scale;
        var fillH = MathF.Max(60f * scale, SpheneUI.Layout.FillToViewBottomHeight()
            - ImGui.GetTextLineHeight() - SpheneUI.Spacing.X4.Scaled);
        var railMin = ImGui.GetCursorScreenPos();

        using (var rail = ImRaii.Group())
        {
            using var jobs = SpheneUI.Lists.Begin("##jobs", width: railW, height: fillH,
                title: "Job", border: false);
            if (jobs.Success)
            {
                if (!selectedData)
                {
                    SpheneUI.Lists.EmptyHint("Select a character.");
                }
                else
                {
                    if (SpheneUI.Lists.Item("alljobs", "ALL",
                        string.Equals(_selectedJobEntry, StoredAllJobsKey, StringComparison.Ordinal), accent,
                        icon: FontAwesomeIcon.Infinity, tooltip: "All Jobs"))
                    {
                        _selectedJobEntry = StoredAllJobsKey;
                    }
                    var renderedPetJobs = new HashSet<uint>();
                    foreach (var job in transientStorage!.JobSpecificCache)
                    {
                        if (!_uiSharedService.JobData.TryGetValue(job.Key, out var jobName)) continue;
                        var badge = JobBadge(jobName);
                        if (SpheneUI.Lists.Item("job" + job.Key, badge.Abbrev,
                            string.Equals(_selectedJobEntry, job.Key.ToString(), StringComparison.Ordinal), accent,
                            gameIcon: badge.IconId > 0 ? badge.IconId : null, tooltip: jobName))
                        {
                            _selectedJobEntry = job.Key.ToString();
                            _storedPathsToRemove.Clear();
                            _filePathResolve.Clear();
                            _filterFilePath = string.Empty;
                            _filterGamePath = string.Empty;
                        }

                        if (transientStorage.JobSpecificPetCache.TryGetValue(job.Key, out var petList) && petList != null && petList.Count > 0)
                        {
                            renderedPetJobs.Add(job.Key);
                            var petKeyForJob = string.Concat(StoredPetPrefix, job.Key.ToString());
                            if (SpheneUI.Lists.Item(petKeyForJob, badge.Abbrev,
                                string.Equals(_selectedJobEntry, petKeyForJob, StringComparison.Ordinal), accent,
                                icon: FontAwesomeIcon.Paw, tooltip: $"Pets · {jobName}"))
                            {
                                _selectedJobEntry = petKeyForJob;
                                _storedPathsToRemove.Clear();
                                _filePathResolve.Clear();
                                _filterFilePath = string.Empty;
                                _filterGamePath = string.Empty;
                            }
                        }
                    }
                    foreach (var petJob in transientStorage.JobSpecificPetCache)
                    {
                        if (renderedPetJobs.Contains(petJob.Key))
                            continue;
                        if (!_uiSharedService.JobData.TryGetValue(petJob.Key, out var jobName)) continue;
                        var petKey = string.Concat(StoredPetPrefix, petJob.Key.ToString());
                        var petBadge = JobBadge(jobName);
                        if (SpheneUI.Lists.Item(petKey, petBadge.Abbrev,
                            string.Equals(_selectedJobEntry, petKey, StringComparison.Ordinal), accent,
                            icon: FontAwesomeIcon.Paw, tooltip: $"Pets · {jobName}"))
                        {
                            _selectedJobEntry = petKey;
                            _storedPathsToRemove.Clear();
                            _filePathResolve.Clear();
                            _filterFilePath = string.Empty;
                            _filterGamePath = string.Empty;
                        }
                    }
                    if (SpheneUI.Lists.Item("minionmount", "MIN",
                        string.Equals(_selectedJobEntry, StoredMinionMountSessionKey, StringComparison.Ordinal), accent,
                        icon: FontAwesomeIcon.Horse, tooltip: "Minion/Mount (Session)"))
                    {
                        _selectedJobEntry = StoredMinionMountSessionKey;
                        _storedPathsToRemove.Clear();
                        _filePathResolve.Clear();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
            }
        }

        List<string> selectedList;
        var isMinionMountSession = string.Equals(_selectedJobEntry, StoredMinionMountSessionKey, StringComparison.Ordinal);
        if (selectedData && string.Equals(_selectedJobEntry, StoredAllJobsKey, StringComparison.Ordinal))
        {
            selectedList = transientStorage!.GlobalPersistentCache;
        }
        else if (selectedData && !string.IsNullOrEmpty(_selectedJobEntry) && _selectedJobEntry.StartsWith(StoredPetPrefix, StringComparison.Ordinal))
        {
            var parsed = uint.TryParse(_selectedJobEntry[StoredPetPrefix.Length..], out var jobId);
            if (parsed && transientStorage!.JobSpecificPetCache.TryGetValue(jobId, out var petList) && petList != null)
                selectedList = petList;
            else
                selectedList = [];
        }
        else if (isMinionMountSession)
        {
            selectedList = _transientResourceManager.GetTransientResourcesSnapshot(ObjectKind.MinionOrMount)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (selectedData && !string.IsNullOrEmpty(_selectedJobEntry) && uint.TryParse(_selectedJobEntry, out var selectedJobId)
            && transientStorage!.JobSpecificCache.TryGetValue(selectedJobId, out var jobList) && jobList != null)
        {
            selectedList = jobList;
        }
        else
        {
            selectedList = [];
        }

        // Middle hairline between the rail and the files area · the pane
        // itself stays borderless so the line carries the separation.
        var colGap = SpheneUI.Spacing.X8.Scaled;
        var lineX = railMin.X + railW + colGap * 0.5f;
        var colH = ImGui.GetTextLineHeight() + SpheneUI.Spacing.X4.Scaled + fillH;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(lineX, railMin.Y), new Vector2(lineX, railMin.Y + colH),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Border with { W = 0.35f }),
            MathF.Max(1f, scale));

        ImGui.SameLine(0f, colGap);
        using (var files = ImRaii.Group())
        {
            using var pane = SpheneUI.Layout.BeginPane("##storedFilesPane",
                $"Attached Files · {selectedList.Count}", size: new Vector2(0f, fillH),
                border: false);
            if (pane.Success)
            {
                DrawStoredFilesPane(selectedList, isMinionMountSession, accent);
            }
        }
    }

    private void DrawStoredFilesPane(List<string> selectedList, bool isMinionMountSession, Vector4 accent)
    {
        var canModify = !isMinionMountSession;
        SpheneUI.Buttons.Row("storedToolbar",
            new SpheneUI.Buttons.Spec(FontAwesomeIcon.ArrowRight, "Resolve",
                "Resolve Game Paths to their used File Paths",
                () => StartResolvePaths(selectedList),
                Enabled: !string.IsNullOrEmpty(_selectedJobEntry)),
            new SpheneUI.Buttons.Spec(FontAwesomeIcon.Trash, "Remove",
                "Remove the selected Game Paths",
                () => RemoveSelectedPaths(selectedList),
                Enabled: _storedPathsToRemove.Count > 0 && canModify),
            new SpheneUI.Buttons.Spec(FontAwesomeIcon.Trash, "Clear All",
                isMinionMountSession
                    ? "Minion/Mount Session entries are runtime transients (read-only in this view)."
                    : "Hold CTRL to delete all game paths from the displayed list"
                        + SpheneUI.TooltipSeparator + "You usually do not need to do this. All animation and VFX data will be automatically handled through Sphene.",
                () => ClearAllPaths(selectedList),
                Enabled: UiSharedService.CtrlPressed() && canModify,
                Danger: true));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedJobEntry)))
        {

            var restContent = ImGui.GetContentRegionAvail().X;
            var filterW = (restContent - ImGui.GetStyle().ItemSpacing.X) / 2f;
            ImGui.SetNextItemWidth(filterW);
            SpheneUI.Controls.InputTextWithHint("##filterGamePath", "Filter by Game Path", ref _filterGamePath, 255);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(filterW);
            SpheneUI.Controls.InputTextWithHint("##filterFilePath", "Filter by File Path", ref _filterFilePath, 255);

            using var dataTable = SpheneUI.Tables.Begin("##table", 2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                    | ImGuiTableFlags.BordersInnerH,
                new Vector2(0, SpheneUI.Layout.FillToViewBottomHeight(SpheneUI.Spacing.X4.Scaled)), accent);
            if (dataTable.Success)
            {
                SpheneUI.Tables.Column("Game Path", ImGuiTableColumnFlags.WidthStretch, 1f);
                SpheneUI.Tables.Column("File Path", ImGuiTableColumnFlags.WidthStretch, 1f);
                SpheneUI.Tables.HeaderRow();
                if (selectedList.Count == 0)
                {
                    SpheneUI.Tables.EmptyRow(string.IsNullOrEmpty(_selectedJobEntry)
                        ? "Select a character and a job on the left."
                        : "No attached files for this selection.");
                }
                var visible = new List<string>(selectedList.Count);
                foreach (var entry in selectedList)
                {
                    if (!string.IsNullOrWhiteSpace(_filterGamePath) && !entry.Contains(_filterGamePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (_filePathResolve.TryGetValue(entry, out var resolved)
                        && !string.IsNullOrEmpty(_filterFilePath)
                        && !resolved!.Contains(_filterFilePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    visible.Add(entry);
                }
                for (var index = 0; index < visible.Count; index++)
                {
                    var entry = visible[index];
                    var hasFileResolve = _filePathResolve.TryGetValue(entry, out var filePath);
                    var isSelected = _storedPathsToRemove.Contains(entry, StringComparer.Ordinal);

                    using var imguiid = ImRaii.PushId(index);
                    if (SpheneUI.Tables.SelectableRowSpan("row", isSelected, accent: accent))
                    {
                        SelectStoredPath(entry, index, visible);
                    }
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        ImGui.SetClipboardText(entry);
                    }
                    SpheneUI.AttachTooltip(entry
                        + SpheneUI.TooltipSeparator
                        + "Click to select · Ctrl+Click toggles · Shift+Click selects a range · Double-click copies");

                    SpheneUI.Tables.PathText(entry, muted: false);
                    if (hasFileResolve)
                    {
                        SpheneUI.Tables.NextColumn();
                        SpheneUI.Tables.PathText(filePath ?? "Unknown");
                    }
                    else
                    {
                        SpheneUI.Tables.NextColumn();
                        SpheneUI.Text.Muted("-");
                        SpheneUI.AttachTooltip("Resolve Game Paths to used File Paths to display the associated file paths.");
                    }
                }
            }
        }
    }

    private void StartResolvePaths(List<string> selectedList)
    {
        _ = Task.Run(async () =>
        {
            var paths = selectedList.ToArray();
            var resolved = await _ipcManager.Penumbra.ResolvePathsAsync(paths, []).ConfigureAwait(false);
            _filePathResolve.Clear();

            for (int i = 0; i < resolved.forward.Length; i++)
            {
                _filePathResolve[paths[i]] = resolved.forward[i];
            }
        });
    }

    /// <summary>
    /// Standard list-view selection: plain click selects the row, Ctrl+click
    /// toggles it into the multi selection, Shift+click fills the range from
    /// the last clicked anchor over the currently visible (filtered) rows.
    /// </summary>
    private void SelectStoredPath(string entry, int index, IReadOnlyList<string> visible)
    {
        var io = ImGui.GetIO();
        if (io.KeyShift && _storedPathsAnchor >= 0 && _storedPathsAnchor < visible.Count)
        {
            var from = Math.Min(_storedPathsAnchor, index);
            var to = Math.Max(_storedPathsAnchor, index);
            if (!io.KeyCtrl) _storedPathsToRemove.Clear();
            for (var k = from; k <= to; k++)
            {
                if (!_storedPathsToRemove.Contains(visible[k], StringComparer.Ordinal))
                {
                    _storedPathsToRemove.Add(visible[k]);
                }
            }
        }
        else if (io.KeyCtrl)
        {
            if (_storedPathsToRemove.Contains(entry, StringComparer.Ordinal))
            {
                _storedPathsToRemove.Remove(entry);
            }
            else
            {
                _storedPathsToRemove.Add(entry);
            }
            _storedPathsAnchor = index;
        }
        else
        {
            _storedPathsToRemove.Clear();
            _storedPathsToRemove.Add(entry);
            _storedPathsAnchor = index;
        }
    }

    private void RemoveSelectedPaths(List<string> selectedList)
    {
        foreach (var item in _storedPathsToRemove)
        {
            selectedList.Remove(item);
        }

        _storedPathsToRemove.Clear();
        _storedPathsAnchor = -1;
        _transientConfigService.Save();
        _transientResourceManager.RebuildSemiTransientResources();
        _filterFilePath = string.Empty;
        _filterGamePath = string.Empty;
    }

    private void ClearAllPaths(List<string> selectedList)
    {
        selectedList.Clear();
        _storedPathsToRemove.Clear();
        _storedPathsAnchor = -1;
        _transientConfigService.Save();
        _transientResourceManager.RebuildSemiTransientResources();
        _filterFilePath = string.Empty;
        _filterGamePath = string.Empty;
    }

    private string? StoredCharacterLabel(string key)
    {
        var parts = key.Split('_');
        if (parts.Length < 2 || !ushort.TryParse(parts[1], out var worldId))
        {
            return null;
        }
        if (!_uiSharedService.WorldData.TryGetValue(worldId, out var worldName))
        {
            return null;
        }
        return $"{parts[0]} ({worldName})";
    }

    // Game UI icon ids for the glowing job-icon set (ui/icon/062000/062xxx)  · 
    // 623xx are base classes, 624xx jobs, in the game's journal-quest style.
    private static (uint IconId, string Abbrev) JobBadge(string jobName) => jobName switch
    {
        "Paladin" => (62401u, "PLD"),
        "Monk" => (62402u, "MNK"),
        "Warrior" => (62403u, "WAR"),
        "Dragoon" => (62404u, "DRG"),
        "Bard" => (62405u, "BRD"),
        "White Mage" => (62406u, "WHM"),
        "Black Mage" => (62407u, "BLM"),
        "Summoner" => (62408u, "SMN"),
        "Scholar" => (62409u, "SCH"),
        "Ninja" => (62410u, "NIN"),
        "Machinist" => (62411u, "MCH"),
        "Dark Knight" => (62412u, "DRK"),
        "Astrologian" => (62413u, "AST"),
        "Samurai" => (62414u, "SAM"),
        "Red Mage" => (62415u, "RDM"),
        "Blue Mage" => (62416u, "BLU"),
        "Gunbreaker" => (62417u, "GNB"),
        "Dancer" => (62418u, "DNC"),
        "Reaper" => (62419u, "RPR"),
        "Sage" => (62420u, "SGE"),
        "Viper" => (62421u, "VPR"),
        "Pictomancer" => (62422u, "PCT"),
        "Beastmaster" => (62423u, "BST"),
        "Gladiator" => (62301u, "GLD"),
        "Pugilist" => (62302u, "PGL"),
        "Marauder" => (62303u, "MRD"),
        "Lancer" => (62304u, "LNC"),
        "Archer" => (62305u, "ARC"),
        "Conjurer" => (62306u, "CNJ"),
        "Thaumaturge" => (62307u, "THM"),
        "Arcanist" => (62308u, "ACN"),
        "Rogue" => (62309u, "ROG"),
        _ => (0u,
            jobName.Length >= 3 ? jobName[..3].ToUpperInvariant() : jobName.ToUpperInvariant()),
    };

    private void DrawRecording()
    {
        SpheneUI.Layout.HelpExtra(() =>
        {
            SpheneUI.Text.Wrapped("This tab allows you to attempt to fix mods that do not sync correctly, especially those with modded models and animations." + Environment.NewLine + Environment.NewLine
                + "To use this, start the recording, execute one or multiple emotes/animations you want to attempt to fix and check if new data appears in the table below." + Environment.NewLine
                + "If it doesn't, Sphene is not able to catch the data or already has recorded the animation files (check 'Show previously added transient files' to see if not all is already present)." + Environment.NewLine + Environment.NewLine
                + "For most animations, vfx, etc. it is enough to just run them once unless they have random variations. Longer animations do not require to play out in their entirety to be captured.");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout("If you need to fix an animation that should apply across multiple jobs, you need to repeat this process with at least one additional job, " +
                "otherwise the animation will only be fixed for the currently active job. This goes primarily for emotes that are used across multiple jobs.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout("While recording transient data, do not change your appearance, enabled mods or anything else. Just do the animation(s) you need and stop the recording.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed, "Important");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        });
        SpheneUI.Buttons.Row("recordToolbar",
            new SpheneUI.Buttons.Spec(FontAwesomeIcon.Play, "Start Recording",
                "Start capturing transient file data in the background",
                () =>
                {
                    _transientRecordCts.Cancel();
                    _transientRecordCts.Dispose();
                    _transientRecordCts = new();
                    _transientResourceManager.StartRecording(_transientRecordCts.Token);
                    _acknowledgeReview = false;
                },
                Enabled: !_transientResourceManager.IsTransientRecording),
            new SpheneUI.Buttons.Spec(FontAwesomeIcon.Stop, "Stop Recording",
                "Stop the active transient recording",
                () => _transientRecordCts.Cancel(),
                Enabled: _transientResourceManager.IsTransientRecording,
                Danger: true));
        if (_transientResourceManager.IsTransientRecording)
        {
            ImGui.SameLine();
            SpheneUI.Text.Colored($"RECORDING - Time Remaining: {_transientResourceManager.RecordTimeRemaining.Value}", ImGuiColors.DalamudYellow);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout("Do not change your appearance or mods while recording · you can accidentally make some of your appearance related mods permanent.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed, "Recording active");
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Controls.Toggle("Show previously added transient files in the recording", ref _showAlreadyAddedTransients);
        SpheneUI.Controls.HelpText("Use this only if you want to see what was previously already caught by Sphene");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        using (ImRaii.Disabled(_transientResourceManager.IsTransientRecording || _transientResourceManager.RecordedTransients.All(k => !k.AddTransient) || !_acknowledgeReview))
        {
            if (SpheneUI.Controls.PrimaryButton("Save Recorded Transient Data", FontAwesomeIcon.Save))
            {
                _transientResourceManager.SaveRecording();
                _acknowledgeReview = false;
            }
        }
        ImGui.SameLine();
        SpheneUI.Controls.Toggle("I acknowledge I have reviewed the recorded data", ref _acknowledgeReview);
        if (_transientResourceManager.RecordedTransients.Any(k => !k.AlreadyTransient))
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout("Please review the recorded mod files before saving and deselect files that got into the recording on accident.",
                FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudYellow);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        var width = ImGui.GetContentRegionAvail();
        using var table = SpheneUI.Tables.Begin("Recorded Transients", 4,
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg,
            new Vector2(0, SpheneUI.Layout.FillToViewBottomHeight()));
        if (table.Success)
        {
            int id = 0;
            var checkW = 36f * SpheneUI.Scale;
            var ownerW = 100f * SpheneUI.Scale;
            SpheneUI.Tables.Column("", ImGuiTableColumnFlags.WidthFixed, checkW);
            SpheneUI.Tables.Column("Owner", ImGuiTableColumnFlags.WidthFixed, ownerW);
            SpheneUI.Tables.Column("Game Path", ImGuiTableColumnFlags.WidthFixed, (width.X - checkW - ownerW) / 2f);
            SpheneUI.Tables.Column("File Path", ImGuiTableColumnFlags.WidthFixed, (width.X - checkW - ownerW) / 2f);
            SpheneUI.Tables.HeaderRow();
            var transients = _transientResourceManager.RecordedTransients.ToList();
            transients.Reverse();
            if (transients.Count == 0)
            {
                SpheneUI.Tables.EmptyRow("No transients recorded yet · enable recording and load into a zone.");
            }
            foreach (var value in transients)
            {
                if (value.AlreadyTransient && !_showAlreadyAddedTransients)
                    continue;

                using var imguiid = ImRaii.PushId(id++);
                using var dim = value.AlreadyTransient
                    ? ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey)
                    : null;
                SpheneUI.Tables.BeginRow();
                bool addTransient = value.AddTransient;
                if (SpheneUI.Controls.Checkbox("##add", ref addTransient))
                {
                    value.AddTransient = addTransient;
                }
                SpheneUI.Tables.Cell(value.Owner.Name);
                SpheneUI.Tables.PathCell(value.GamePath);
                SpheneUI.Tables.PathCell(value.FilePath);
            }
        }
    }

    public override void OnOpen()
    {
        _hasUpdate = true;
        _selectedHash = string.Empty;
        _texturesToConvert.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _conversionProgress.ProgressChanged -= ConversionProgress_ProgressChanged;
        try { if (_onShrinkuConversionProgress != null) _shrinkuConversionService.OnConversionProgress -= _onShrinkuConversionProgress; } catch (Exception ex) { _logger.LogDebug(ex, "Unsubscribe OnConversionProgress failed"); }
        try { if (_onShrinkuModProgress != null) _shrinkuConversionService.OnModProgress -= _onShrinkuModProgress; } catch (Exception ex) { _logger.LogDebug(ex, "Unsubscribe OnModProgress failed"); }
        _onShrinkuConversionProgress = null;
        _onShrinkuModProgress = null;
        _revertCancellationTokenSource.Cancel();
        _revertCancellationTokenSource.Dispose();
        _transientRecordCts.Cancel();
        _transientRecordCts.Dispose();
    }

    private void ConversionProgress_ProgressChanged(object? sender, (string, int) e)
    {
        _conversionCurrentFileName = e.Item1;
        _conversionCurrentFileProgress = e.Item2;
    }

    private async Task OpenBackupFolderAsync()
    {
        try
        {
            var overview = await _shrinkuBackupService.GetBackupOverviewAsync().ConfigureAwait(false);
            var firstPath = overview.FirstOrDefault()?.SourcePath;
            var backupDirectory = string.IsNullOrEmpty(firstPath)
                ? _textureBackupService.GetBackupDirectory()
                : Path.GetDirectoryName(firstPath)!;
            if (!string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory))
            {
                System.Diagnostics.Process.Start("explorer.exe", backupDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve backup folder via ShrinkU, falling back to local directory");
            try
            {
                var fallback = _textureBackupService.GetBackupDirectory();
                if (Directory.Exists(fallback))
                    System.Diagnostics.Process.Start("explorer.exe", fallback);
            }
            catch (Exception ex2)
            {
                _logger.LogDebug(ex2, "Failed to open backup folder");
            }
        }
    }

    private async Task<Dictionary<string, string[]>> BuildFullModConversionDictionaryFromSelection()
    {
        var dict = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            var root = _ipcManager.Penumbra.ModDirectory ?? string.Empty;
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _texturesToConvert)
            {
                var source = kvp.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(source)) continue;
                var rel = !string.IsNullOrWhiteSpace(root) ? Path.GetRelativePath(root, source) : source;
                rel = rel.Replace('/', '\\');
                var idx = rel.IndexOf('\\');
                var mod = idx >= 0 ? rel.Substring(0, idx) : rel;
                if (!string.IsNullOrWhiteSpace(mod)) mods.Add(mod);
            }
            foreach (var mod in mods)
            {
                var files = await _shrinkuConversionService.GetModTextureFilesAsync(mod).ConfigureAwait(false);
                foreach (var f in files)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    if (!dict.ContainsKey(f)) dict[f] = Array.Empty<string>();
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to build full mod conversion dictionary"); }
        return dict;
    }

    private void DrawTable(string tableId, IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup, float height)
    {
        var isTex = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal);
        var isMdl = string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal);
        var accent = isTex ? SpheneUI.Palette.EtherealPurple : SpheneTheme.Current.AccentBlue;
        var tableColumns = isTex ? (_enableBc7ConversionMode ? 7 : 6) : (isMdl ? 6 : 5);
        using var table = SpheneUI.Tables.Begin(tableId, tableColumns,
            ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, height), accent);
        if (!table.Success) return;
        var scale = SpheneUI.Scale;
        SpheneUI.Tables.Column("Hash", ImGuiTableColumnFlags.WidthStretch);
        SpheneUI.Tables.Column("Files", ImGuiTableColumnFlags.WidthFixed, 48f * scale);
        SpheneUI.Tables.Column("Game Paths", ImGuiTableColumnFlags.WidthFixed, SpheneUI.Tables.HeaderFitWidth("Game Paths"));
        SpheneUI.Tables.Column("Size", ImGuiTableColumnFlags.WidthFixed, 76f * scale);
        SpheneUI.Tables.Column("Compressed", ImGuiTableColumnFlags.WidthFixed, SpheneUI.Tables.HeaderFitWidth("Compressed"));
        if (isTex)
        {
            SpheneUI.Tables.Column("Format", ImGuiTableColumnFlags.WidthFixed, 64f * scale);
            if (_enableBc7ConversionMode)
                SpheneUI.Tables.Column("BC7", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 40f * scale);
        }
        if (isMdl)
        {
            SpheneUI.Tables.Column("Triangles", ImGuiTableColumnFlags.WidthFixed, SpheneUI.Tables.HeaderFitWidth("Triangles"));
        }
        SpheneUI.Tables.HeaderRow();

        var sortSpecs = SpheneUI.Tables.SortSpecs();
        if (sortSpecs.SpecsDirty)
        {
            var idx = sortSpecs.Specs.ColumnIndex;

            if (idx == 0 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 0 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 1 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.FilePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 1 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.FilePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 2 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.GamePaths.Count).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 3 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.OriginalSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (idx == 4 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.CompressedSize).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (isMdl && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (isMdl && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (isTex && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (isTex && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

            sortSpecs.SpecsDirty = false;
        }

        if (!fileGroup.Any())
        {
            SpheneUI.Tables.EmptyRow("No files in this category.");
        }
        foreach (var item in fileGroup)
        {
            if (SpheneUI.Tables.SelectableRow(item.Hash, item.Hash,
                    string.Equals(item.Hash, _selectedHash, StringComparison.Ordinal),
                    !item.IsComputed, accent))
            {
                _selectedHash = item.Hash;
            }
            SpheneUI.Tables.MutedCell(item.FilePaths.Count.ToString());
            SpheneUI.Tables.MutedCell(item.GamePaths.Count.ToString());
            SpheneUI.Tables.MutedCell(UiSharedService.ByteToString(item.OriginalSize));
            SpheneUI.Tables.MutedCell(UiSharedService.ByteToString(item.CompressedSize));
            if (isTex)
            {
                SpheneUI.Tables.Cell(item.Format.Value);
                if (_enableBc7ConversionMode)
                {
                    SpheneUI.Tables.NextColumn();
                    if (string.Equals(item.Format.Value, "BC7", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var filePath = item.FilePaths[0];
                    bool toConvert = _texturesToConvert.ContainsKey(filePath);
                    if (SpheneUI.Controls.Checkbox("###convert" + item.Hash, ref toConvert))
                    {
                        if (toConvert && !_texturesToConvert.ContainsKey(filePath))
                        {
                            _texturesToConvert[filePath] = item.FilePaths.Skip(1).ToArray();
                        }
                        else if (!toConvert && _texturesToConvert.ContainsKey(filePath))
                        {
                            _texturesToConvert.Remove(filePath);
                        }
                    }
                }
            }
            if (isMdl)
            {
                SpheneUI.Tables.Cell(item.Triangles.ToString());
            }
        }
    }

    private static List<CharacterAnalyzer.FileDataEntry> GetNonBc7Textures(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        return fileGroup.Where(item => !string.Equals(item.Format.Value, "BC7", StringComparison.Ordinal)).ToList();
    }



    private void StartTextureRevert(Dictionary<string, List<string>> availableBackups)
    {
        _revertCancellationTokenSource = _revertCancellationTokenSource.CancelRecreate();

        _ = Task.Run(async () =>
        {
            try
            {
                // Prefer restoring via ShrinkU if sessions/zips exist
                try
                {
                    var overview = await _shrinkuBackupService.GetBackupOverviewAsync().ConfigureAwait(false);
                    if (overview != null && overview.Count > 0)
                    {
                        _logger.LogDebug("ShrinkU backups found: {count}. Restoring latest.", overview.Count);
                        await _shrinkuBackupService.RestoreLatestAsync(_revertProgress, _revertCancellationTokenSource.Token).ConfigureAwait(false);
                        _storageInfoTask = null;
                        _cachedBackupsForAnalysis = null;
                        try { _shrinkuBackupService.RedrawPlayer(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to redraw player after restore-latest"); }
                            // ShrinkU service is responsible for external texture change notifications on restore
                        return;
                    }
                }
                catch (Exception shrEx)
                {
                    _logger.LogWarning(shrEx, "ShrinkU restore failed, falling back to Sphene per-file restore.");
                }
                // Create mapping of backup files to their target locations
                var backupToTargetMap = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var kvp in availableBackups)
                {
                    var originalFileName = kvp.Key;
                    var backupFiles = kvp.Value;

                    // Use the most recent backup (first in the list since they're ordered by creation time)
                    if (backupFiles.Count > 0)
                    {
                        var mostRecentBackup = backupFiles[0];

                        // Try to find the current location of this texture in the loaded data
                        var targetPath = FindCurrentTextureLocation(originalFileName);
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            backupToTargetMap[mostRecentBackup] = targetPath;
                            _logger.LogDebug("Will restore {backup} to {target}", mostRecentBackup, targetPath);
                        }
                        else
                        {
                            _logger.LogWarning("Could not find current location for texture: {originalFileName}", originalFileName);
                        }
                    }
                }

                if (backupToTargetMap.Count > 0)
                {
                    _logger.LogDebug("Starting selective revert for {count} texture(s) from current analysis", backupToTargetMap.Count);
                    var results = await _textureBackupService.RestoreTexturesAsync(backupToTargetMap, deleteBackupsAfterRestore: true, _revertProgress, _revertCancellationTokenSource.Token).ConfigureAwait(false);

                    var successCount = results.Values.Count(success => success);
                    _logger.LogInformation("Selective texture revert completed: {successCount}/{totalCount} textures from current analysis restored successfully. Backup files have been automatically deleted.", successCount, results.Count);

                    // Refresh storage information and analysis data after successful restore
                    if (successCount > 0)
                    {
                        _storageInfoTask = null; // Force refresh of storage info
                        _cachedBackupsForAnalysis = null; // Invalidate backup cache after restore

                        // Trigger a new analysis to show updated values
                        _logger.LogDebug("Refreshing analysis data after successful texture restore");
                        _hasUpdate = true; // Trigger analysis data refresh

                        // Trigger character redraw to reload restored textures in Penumbra
                        try
                        {
                            await _ipcManager.Penumbra.RedrawPlayerAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to trigger character redraw after texture restore");
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("No textures from current analysis could be mapped for restoration");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during texture revert process");
            }
        }, _revertCancellationTokenSource.Token);
    }

    private Dictionary<string, List<string>> GetCachedBackupsForCurrentAnalysis()
    {
        // Cache for 5 seconds to avoid excessive recalculation
            if (_cachedBackupsForAnalysis != null &&
            DateTime.UtcNow - _lastBackupAnalysisUpdate < TimeSpan.FromSeconds(5))
        {
            return _cachedBackupsForAnalysis;
        }

        _cachedBackupsForAnalysis = GetBackupsForCurrentAnalysis();
        _lastBackupAnalysisUpdate = DateTime.UtcNow;
        return _cachedBackupsForAnalysis;
    }

    private Dictionary<string, List<string>> GetBackupsForCurrentAnalysis()
    {
        try
        {
            if (_cachedAnalysis == null) return new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // Get all available backups
            var allBackups = _textureBackupService.GetBackupsByOriginalFile();
            var filteredBackups = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // Get all texture filenames from current analysis
            var currentTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var objectKindData in _cachedAnalysis.Values)
            {
                foreach (var fileData in objectKindData.Values)
                {
                    if (fileData.FilePaths != null)
                    {
                        foreach (var filePath in fileData.FilePaths)
                        {
                            var fileName = Path.GetFileName(filePath);
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                currentTextureNames.Add(fileName);
                            }
                        }
                    }
                }
            }

            // Filter backups to only include those for currently loaded textures
            foreach (var backup in allBackups)
            {
                if (currentTextureNames.Contains(backup.Key))
                {
                    filteredBackups[backup.Key] = backup.Value;
                }
            }

            // Only log when there are actually backups to avoid spam
            if (filteredBackups.Count > 0)
            {
                _logger.LogDebug("Filtered backups: {filteredCount} out of {totalCount} available backups match current analysis",
                    filteredBackups.Count, allBackups.Count);
            }

            return filteredBackups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error filtering backups for current analysis");
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }

    private string FindCurrentTextureLocation(string originalFileName)
    {
        try
        {
            if (_cachedAnalysis == null) return string.Empty;

            // Search through all loaded texture data to find a file with matching name
            foreach (var objectKindData in _cachedAnalysis.Values)
            {
                foreach (var fileData in objectKindData.Values)
                {
                    if (fileData.FilePaths != null)
                    {
                        foreach (var filePath in fileData.FilePaths)
                        {
                            var fileName = Path.GetFileName(filePath);
                            if (string.Equals(fileName, originalFileName, StringComparison.OrdinalIgnoreCase))
                            {
                                return filePath;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding current texture location for {fileName}", originalFileName);
        }

        return string.Empty;
    }

    // ShrinkU helpers for storage info and cleanup
    private async Task<(long totalSize, int fileCount)> GetShrinkUStorageInfoAsync()
    {
        try
        {
            var overview = await _shrinkuBackupService.GetBackupOverviewAsync().ConfigureAwait(false);
            long total = 0;
            int count = 0;
            foreach (var sess in overview)
            {
                if (sess == null) continue;
                if (sess.IsZip)
                {
                    if (File.Exists(sess.SourcePath))
                    {
                        var fi = new FileInfo(sess.SourcePath);
                        total += fi.Length;
                        count += 1;
                    }
                }
                else
                {
                    if (Directory.Exists(sess.SourcePath))
                    {
                        foreach (var f in Directory.EnumerateFiles(sess.SourcePath, "*", SearchOption.AllDirectories))
                        {
                            try { total += new FileInfo(f).Length; count++; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to read file size for {file}", f); }
                        }
                    }
                }
            }
            try
            {
                string backupDirectory = string.Empty;
                var firstPath = overview.FirstOrDefault()?.SourcePath;
                if (!string.IsNullOrWhiteSpace(firstPath))
                {
                    if (File.Exists(firstPath))
                    {
                        var modDir = Path.GetDirectoryName(firstPath) ?? string.Empty;
                        backupDirectory = Directory.GetParent(modDir)?.FullName ?? string.Empty;
                    }
                    else if (Directory.Exists(firstPath))
                    {
                        backupDirectory = Path.GetDirectoryName(firstPath) ?? string.Empty;
                    }
                }
                if (string.IsNullOrWhiteSpace(backupDirectory))
                {
                    backupDirectory = _textureBackupService.GetBackupDirectory();
                }
                if (!string.IsNullOrWhiteSpace(backupDirectory) && Directory.Exists(backupDirectory))
                {
                    foreach (var modDir in Directory.EnumerateDirectories(backupDirectory))
                    {
                        foreach (var pmp in Directory.EnumerateFiles(modDir, "mod_backup_*.pmp"))
                        {
                            try { total += new FileInfo(pmp).Length; count++; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to read file size for {file}", pmp); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to compute backup size from ShrinkU directories");
            }
            return (total, count);
        }
        catch
        {
            // Fallback to Sphene service
            return await _textureBackupService.GetBackupStorageInfoAsync().ConfigureAwait(false);
        }
    }
}
