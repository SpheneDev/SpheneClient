using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.User;
using Sphene.Interop.Ipc;
using Sphene.PlayerData.Pairs;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.UI.Design;
using Sphene.WebAPI;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Sphene.Services;

public sealed class SupporterNameplateService : IDisposable, IHostedService
{
    private const float IconSize = 20f;
    private const float IconGap = 4f;
    /// <summary>Embedded default icon is edge-to-edge opaque while other
    /// icons keep glyph padding (the halo's solid core fills only ~69% of its
    /// box) - draw it at this factor so the options read the same size.</summary>
    internal const float DefaultIconScale = 0.7f;
    // the nameplate bake renders glyphs a third larger than the font's own
    // units - the fixed font resolution the plates' text is baked at
    private const float NameplateBakeFactor = 4f / 3f;
    private const int MaxNamePlateObjects = NameplateReader.MaxNamePlateObjects;
    private const int IconSlots = NameplateReader.IconSlots;
    private const float OcclusionProbeHeight = 2.2f;
    private const float OcclusionProbeMargin = 0.6f;
    private const int OccGridCols = 4;
    private const int OccGridRows = 2;
    private const int DepthGridMaxCols = 7;
    private const int DepthGridMaxRows = 7;
    private const float DepthCellPx = 3f;
    private const float DepthTolerance = 0.15f;
    private const float DepthRectPadLeft = 24f;
    private const float DepthRectPadRight = 420f;
    private const float DepthRectPadY = 48f;
    private const bool SceneDepthOcclusionReady = false;

    private readonly record struct IconDrawPosition(Vector2 RelAnchor, Vector3 TargetWorldPos, Vector3 CharPos, int PlateIndex, float DrawOrder, nint Owner, string IconLabel, uint IconLabelColor, string IconId, uint IconLabelEdgeColor, string FxId);

    private readonly record struct ResolvedStyle(
        bool IndicatorsEnabled,
        bool SymbolEnabled, string Symbol,
        bool TextEnabled, string LabelText,
        SupporterLabelOrder LabelOrder, SupporterSymbolPosition Position,
        ushort SymbolColorKey, ushort LabelColorKey,
        bool SymbolGlowEnabled, ushort SymbolGlowKey,
        bool LabelGlowEnabled, ushort LabelGlowKey,
        bool IconEnabled, string IconId, bool IconLabelEnabled, string IconLabelText, uint IconLabelColor,
        bool IconLabelEdgeEnabled, uint IconLabelEdgeColor,
        // names a registered NameplateFx id - supporter-picked and synced
        // through SupporterStyleDto; empty disables the plate's effect
        string FxId);

    private readonly INamePlateGui _namePlateGui;
    private readonly IObjectTable _objectTable;
    private readonly IGameGui _gameGui;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ITextureProvider _textureProvider;
    private readonly IDataManager _dataManager;
    private readonly SupporterIconService _iconService;
    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly IpcCallerHonorific _honorificIpc;
    private readonly ILogger<SupporterNameplateService> _logger;
    private readonly NameplateReader _plates;
    private ISharedImmediateTexture? _iconTexture;
    private IReadOnlyList<IconDrawPosition> _iconPositions = [];
    // per-plate effect assignment resolved from the matched supporter style -
    // the server-prepared path; debug toggles bypass it and apply to all plates
    private readonly string?[] _plateFxId = new string?[MaxNamePlateObjects];
    private readonly int[] _fxPlateOrder = new int[MaxNamePlateObjects];
    private bool _hasPlateFx;
    private readonly float[] _maskAlpha = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly Vector4[] _maskScreen = new Vector4[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly Vector4[] _maskUv = new Vector4[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly int[] _maskTex = new int[GpuNameplateImGuiMask.MaxPlateMasks];
    // per-region list of distinct texture SRVs the emitted masks reference -
    // _atlasTextures slots get rebound per plate, so masks carry the resolved
    // SRV's index here instead of the fleeting slot number
    private readonly nint[] _maskTexSrvs = new nint[64];
    private readonly uint[] _maskTexKeys = new uint[64];
    private int _maskTexSrvCount;
    private readonly float[] _maskChannel = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly float[] _maskTexSel = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    // outline bleed radius per mask in screen px, packed x*64+y - the game's
    // edge pass dilates glyph ink past the ink box, so the cutout must too
    private readonly float[] _maskEdge = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly float[] _plateEdgePx = new float[MaxNamePlateObjects];
    private readonly Vector4[] _maskDebugQuads = new Vector4[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly Vector4[] _maskDebugUv = new Vector4[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly float[] _maskDebugChan = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly int[] _maskPlateIdx = new int[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly int[] _maskDebugPlate = new int[GpuNameplateImGuiMask.MaxPlateMasks];
    private int _maskDebugCount;
    // which image-node icon bits (1<<slot) a mask entry carries - chrome
    // subtraction skips a node's rect only while its pixel mask survives
    private readonly byte[] _maskIcon = new byte[GpuNameplateImGuiMask.MaxPlateMasks];
    // masks flagged 1 skip the coverage bake and pin their own texture slot -
    // the big status field (ic0) fades by real alpha like the target cursor,
    // which the baked smoothstep path can't express
    private readonly byte[] _maskBypass = new byte[GpuNameplateImGuiMask.MaxPlateMasks];
    // the frame's emitted mask set - emitted once per draw pass covering every
    // visible plate, then re-filtered per element (order + bounds) into the
    // _mask* arrays the shader consumes; identical output to collecting per
    // element at a fraction of the glyph-emission cost
    private readonly Vector4[] _frmScreen = new Vector4[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly Vector4[] _frmUv = new Vector4[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly float[] _frmChannel = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly float[] _frmAlpha = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly float[] _frmEdge = new float[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly int[] _frmTex = new int[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly int[] _frmPlate = new int[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly byte[] _frmIcon = new byte[GpuNameplateImGuiMask.MaxPlateMasks];
    private readonly byte[] _frmBypass = new byte[GpuNameplateImGuiMask.MaxPlateMasks];
    private int _frameMaskCount;
    private bool _frameMasksEmitted;
    private readonly System.Text.StringBuilder _frameEmitDebug = new(256);
    private NameplateCoverageBake? _coverageBake;
    private readonly Vector4[] _covRect = new Vector4[MaxNamePlateObjects];
    private readonly bool[] _covSeen = new bool[MaxNamePlateObjects];
    private readonly int[] _covHash = new int[MaxNamePlateObjects];
    private readonly bool[] _covBaked = new bool[MaxNamePlateObjects];
    private nint _covBakeSrv;
    private readonly List<Vector4> _covEmitScreen = new(MaxNamePlateObjects);
    private readonly List<Vector4> _covEmitUv = new(MaxNamePlateObjects);
    private readonly List<int> _covEmitPlate = new(MaxNamePlateObjects);
    private readonly List<float> _covEmitAlpha = new(MaxNamePlateObjects);
    private readonly List<byte> _covEmitIcon = new(MaxNamePlateObjects);
    private readonly nint[] _atlasTextures = new nint[16];
    private readonly List<IFontHandle> _fontHandles = new();
    private nint _maskTexture;
    private nint _mask2Texture;
    private nint _mask3Texture;
    private nint _mask4Texture;
    private nint _mask5Texture;
    private nint _mask6Texture;
    private nint _mask7Texture;
    private nint _mask8Texture;
    private char _glyphFail;
    private Vector4 _glyphDbgRect;
    private Vector2 _glyphDbgSpan;
    private float _glyphDbgBaseline;
    private int _glyphNulls;
    private int _glyphBounds;
    private int _glyphTex;
    private readonly System.Text.StringBuilder _occRejectDebug = new(160);
    private long _maskCapLoggedAt;
    private string? _occDebug;
    private bool _disposed;
    private readonly SceneDepthSampler _depthSampler;
    private readonly GpuNameplateImGuiMask _gpuMask;
    private string? _depthStatus;
    private Vector3 _depthCamPos;
    private Vector3 _depthCamForward;
    private float _depthNear;
    private float _depthFar;
    private bool _depthStandardZ;
    private HashSet<string> _supporterNameCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SupporterStyleDto> _supporterStyleMap = new(StringComparer.OrdinalIgnoreCase);
    private string _supporterStyleFingerprint = string.Empty;
    private string? _lastPublishedStyleKey;
    private readonly Dictionary<int, string> _appliedPlateNames = new();
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private DateTime _lastTraceLog = DateTime.MinValue;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private string _startupGateSig = string.Empty;
    private DateTime _lastStartupLog = DateTime.MinValue;
    private bool _iconsActiveLogged;
    private bool _cutoutsActiveLogged;
    private string? _lastSupporterSettingsKey;
    private readonly TimeSpan _cacheRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TraceInterval = TimeSpan.FromSeconds(5);
    private static readonly GameFontFamilyAndSize[] MaskFontCandidates =
    [
        GameFontFamilyAndSize.Axis36,
        GameFontFamilyAndSize.MiedingerMid36,
        GameFontFamilyAndSize.Jupiter45,
        GameFontFamilyAndSize.Axis18,
    ];

    /// <summary>
    /// Places a measured text box inside a node rect the same way the text
    /// renderer does: alignment nibble picks the anchor edge or center.
    /// </summary>
    private static Vector4 AlignToNode(Vector4 node, float w, float h, int align)
    {
        var x = (align % 3) switch
        {
            0 => node.X,
            2 => node.Z - w,
            _ => (node.X + node.Z - w) * 0.5f,
        };
        var y = (align / 3) switch
        {
            0 => node.Y,
            2 => node.W - h,
            _ => (node.Y + node.W - h) * 0.5f,
        };
        return new Vector4(x, y, x + w, y + h);
    }

    public SupporterNameplateService(INamePlateGui namePlateGui,
        IObjectTable objectTable,
        IGameGui gameGui,
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        SupporterIconService iconService,
        ApiController apiController,
        SpheneConfigService configService,
        PairManager pairManager,
        IpcCallerHonorific honorificIpc,
        IGameInteropProvider gameInterop,
        ILogger<SupporterNameplateService> logger)
    {
        _namePlateGui = namePlateGui;
        _objectTable = objectTable;
        _gameGui = gameGui;
        _pluginInterface = pluginInterface;
        _textureProvider = textureProvider;
        _dataManager = dataManager;
        _iconService = iconService;
        _apiController = apiController;
        _configService = configService;
        _pairManager = pairManager;
        _honorificIpc = honorificIpc;
        _logger = logger;
        _plates = new NameplateReader(gameGui, objectTable, gameInterop, logger)
        {
            TuneProvider = ResolvePlateTune,
            AnchorExtrapolation = () => _configService.Current.SupporterNameplateAnchorExtrapolate,
        };
        _depthSampler = new SceneDepthSampler(logger);
        _gpuMask = new GpuNameplateImGuiMask(logger, pluginInterface.ConfigDirectory.FullName);
        // post-data-update reads every plate's FINAL state for this pass -
        // other plugins (Honorific title swaps etc.) mutate nameplate fields
        // during the per-plate handlers, so collecting in OnDataUpdate would
        // reconstruct masks from pre-mutation text that disagrees with the
        // rendered glyphs by a frame every time the content changes
        _namePlateGui.OnPostDataUpdate += OnPostDataUpdate;
        _namePlateGui.OnNamePlateUpdate += OnNamePlateTextUpdate;
        _pluginInterface.UiBuilder.Draw += DrawSupporterIcons;
        _iconTexture = LoadIconTexture();
        foreach (var size in MaskFontCandidates)
        {
            try
            {
                _fontHandles.Add(_pluginInterface.UiBuilder.FontAtlas
                    .NewGameFontHandle(new GameFontStyle(size)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Nameplate] Game font handle {size} unavailable", size);
            }
        }
        _logger.LogDebug("[Nameplate] Service started, subscribed to OnPostDataUpdate + OnNamePlateUpdate");
    }

    private ISharedImmediateTexture? LoadIconTexture()
    {
        try
        {
            var assembly = GetType().Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("images.icon.png", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                _logger.LogDebug("[Nameplate] Embedded supporter icon resource not found");
                return null;
            }
            return _textureProvider.GetFromManifestResource(assembly, resourceName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] Failed to load supporter icon");
            return null;
        }
    }

    private bool ShouldLogTrace()
    {
        if (!_configService.Current.SupporterNameplateDebugLog)
            return false;
        var now = DateTime.UtcNow;
        if (now - _lastTraceLog < TraceInterval)
            return false;
        _lastTraceLog = now;
        return true;
    }

    /// <summary>Startup gating diagnostic - logs every prerequisite of the
    /// cutout pipeline as a compact signature whenever it changes (and every
    /// 10s while startup is incomplete), so cold-start latency shows which
    /// gate actually kept cutouts from working.</summary>
    private unsafe void LogStartupGates()
    {
        if (!_configService.Current.SupporterNameplateDebugLog)
            return;
        var now = DateTime.UtcNow;
        var fontsReady = 0;
        foreach (var h in _fontHandles)
        {
            if (h.Available)
                fontsReady++;
        }
        var sig = $"conn{(_apiController.IsConnected ? 1 : 0)}"
            + $" feat{(_apiController.SupporterFeaturesEnabled ? 1 : 0)}"
            + $" supp{(_apiController.IsSupporterEffective ? 1 : 0)}"
            + $" show{(_configService.Current.ShowSupporterNameplate ? 1 : 0)}"
            + $" cache{_supporterNameCache.Count}"
            + $" icons{_iconPositions.Count}"
            + $" addon{(_plates.Addon != null ? 1 : 0)}"
            + $" f{fontsReady}/{_fontHandles.Count}"
            + $" gpu={_gpuMask.Status}"
            + $" bakeHk{(_plates.BakeHooked ? 1 : 0)}"
            + $" cov{(_coverageBake?.Status ?? "-")}";
        var changed = !string.Equals(sig, _startupGateSig, StringComparison.Ordinal);
        if (!changed && (_cutoutsActiveLogged || now - _lastStartupLog < TimeSpan.FromSeconds(10)))
            return;
        _startupGateSig = sig;
        _lastStartupLog = now;
        _logger.LogDebug("[Nameplate] startup +{elapsed:0.0}s {sig}", (now - _startedUtc).TotalSeconds, sig);
    }

    private void LogMilestone(ref bool flag, string what)
    {
        if (flag || !_configService.Current.SupporterNameplateDebugLog)
            return;
        flag = true;
        _logger.LogDebug("[Nameplate] {what} active +{elapsed:0.0}s", what, (DateTime.UtcNow - _startedUtc).TotalSeconds);
    }

    private void RefreshSupporterNameCache()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCacheRefresh < _cacheRefreshInterval)
            return;

        _lastCacheRefresh = now;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styles = new Dictionary<string, SupporterStyleDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _pairManager.PairsWithGroups.Keys)
        {
            if (pair.UserData == null || string.IsNullOrEmpty(pair.UserData.UID))
                continue;
            if (!pair.UserData.IsSupporter)
                continue;
            var playerName = pair.PlayerName;
            if (string.IsNullOrEmpty(playerName))
                continue;
            names.Add(playerName);
            if (pair.UserPair?.OtherSupporterStyle != null)
                styles[playerName] = pair.UserPair.OtherSupporterStyle;
        }

        if (_apiController.IsSupporterEffective)
        {
            var localName = _objectTable.LocalPlayer?.Name.TextValue;
            if (!string.IsNullOrEmpty(localName))
                names.Add(localName);
        }

        _supporterNameCache = names;
        _supporterStyleMap = styles;
        _supporterStyleFingerprint = styles.Count == 0
            ? string.Empty
            : string.Join(";", styles.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(k => $"{k.Key}~{k.Value.IndicatorsEnabled}~{k.Value.SymbolChar}~{k.Value.SymbolPosition}~{k.Value.TextEnabled}~{k.Value.LabelText}~{k.Value.LabelOrder}~{k.Value.SymbolColorKey}~{k.Value.LabelColorKey}~{k.Value.SymbolGlowEnabled}~{k.Value.SymbolGlowKey}~{k.Value.LabelGlowEnabled}~{k.Value.LabelGlowKey}~{k.Value.IconEnabled}~{k.Value.IconId}~{k.Value.IconLabelEnabled}~{k.Value.IconLabelText}~{k.Value.IconLabelColor}~{k.Value.FxId}"));
    }

    private bool IsSupporter(string characterName)
    {
        if (string.IsNullOrEmpty(characterName))
            return false;
        return _supporterNameCache.Contains(characterName);
    }

    private ResolvedStyle ResolveStyle(string name, bool isLocalPlayer)
    {
        if (!isLocalPlayer && _supporterStyleMap.TryGetValue(name, out var synced))
            return FromDto(synced);
        return FromConfig();
    }

    private ResolvedStyle FromConfig()
    {
        var c = _configService.Current;
        return new ResolvedStyle(
            c.ShowSupporterNameplate,
            !string.IsNullOrEmpty(c.SupporterSymbolChar), c.SupporterSymbolChar ?? string.Empty,
            c.SupporterTextEnabled && !string.IsNullOrWhiteSpace(c.SupporterLabelText), c.SupporterLabelText ?? string.Empty,
            c.SupporterLabelOrder, c.SupporterSymbolPosition,
            c.SupporterColorKey, c.SupporterLabelColorKey,
            c.SupporterSymbolGlowEnabled, c.SupporterSymbolGlowKey,
            c.SupporterLabelGlowEnabled, c.SupporterLabelGlowKey,
            c.SupporterIconEnabled, c.SupporterIconId ?? string.Empty,
            c.SupporterIconLabelEnabled, c.SupporterIconLabelText ?? string.Empty, c.SupporterIconLabelColor,
            c.SupporterIconLabelEdgeEnabled, c.SupporterIconLabelEdgeColor,
            c.SupporterNameplateFxId ?? string.Empty);
    }

    private static ResolvedStyle FromDto(SupporterStyleDto s)
    {
        var position = Enum.IsDefined(typeof(SupporterSymbolPosition), s.SymbolPosition)
            ? (SupporterSymbolPosition)s.SymbolPosition : SupporterSymbolPosition.Left;
        var order = Enum.IsDefined(typeof(SupporterLabelOrder), s.LabelOrder)
            ? (SupporterLabelOrder)s.LabelOrder : SupporterLabelOrder.SymbolThenText;
        return new ResolvedStyle(
            s.IndicatorsEnabled,
            !string.IsNullOrEmpty(s.SymbolChar), s.SymbolChar ?? string.Empty,
            s.TextEnabled && !string.IsNullOrWhiteSpace(s.LabelText), s.LabelText ?? string.Empty,
            order, position,
            (ushort)Math.Clamp(s.SymbolColorKey, 0, ushort.MaxValue), (ushort)Math.Clamp(s.LabelColorKey, 0, ushort.MaxValue),
            s.SymbolGlowEnabled, (ushort)Math.Clamp(s.SymbolGlowKey, 0, ushort.MaxValue),
            s.LabelGlowEnabled, (ushort)Math.Clamp(s.LabelGlowKey, 0, ushort.MaxValue),
            s.IconEnabled, s.IconId ?? string.Empty,
            s.IconLabelEnabled, s.IconLabelText ?? string.Empty, s.IconLabelColor,
            // Remote styles carry no edge - default dark outline like the
            // nameplate text edge every other supporter part falls back to.
            IconLabelEdgeEnabled: true, IconLabelEdgeColor: 0xFF000000u,
            FxId: s.FxId ?? string.Empty);
    }

    private SupporterStyleDto BuildOwnStyle()
    {
        var c = _configService.Current;
        return new SupporterStyleDto
        {
            IndicatorsEnabled = c.ShowSupporterNameplate,
            SymbolChar = c.SupporterSymbolChar ?? string.Empty,
            SymbolPosition = (int)c.SupporterSymbolPosition,
            TextEnabled = c.SupporterTextEnabled,
            LabelText = c.SupporterLabelText ?? string.Empty,
            LabelOrder = (int)c.SupporterLabelOrder,
            SymbolColorKey = c.SupporterColorKey,
            LabelColorKey = c.SupporterLabelColorKey,
            SymbolGlowEnabled = c.SupporterSymbolGlowEnabled,
            SymbolGlowKey = c.SupporterSymbolGlowKey,
            LabelGlowEnabled = c.SupporterLabelGlowEnabled,
            LabelGlowKey = c.SupporterLabelGlowKey,
            IconEnabled = c.SupporterIconEnabled,
            IconId = c.SupporterIconId ?? string.Empty,
            IconLabelEnabled = c.SupporterIconLabelEnabled,
            IconLabelText = c.SupporterIconLabelText ?? string.Empty,
            IconLabelColor = c.SupporterIconLabelColor,
            FxId = c.SupporterNameplateFxId ?? string.Empty
        };
    }

    /// <summary>Foreground/edge color extracted from a nameplate SeString's
    /// payloads - plugins like Honorific color title, name or FC tag via
    /// UIForeground/UIGlow payloads rather than the plate color fields.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public readonly record struct PlateTextColors(uint? Foreground, uint? Edge);

    /// <summary>
    /// Snapshot of the local player's real nameplate data (name as displayed,
    /// quoted title + FC tag, title flags, name-icon slot id, payload colors
    /// and text colors) captured during nameplate updates. Used by the
    /// settings preview to mirror the actual in-game nameplate.
    /// </summary>
    public readonly record struct LocalPlateInfo(
        string Name,
        PlateTextColors NameColors,
        string QuotedTitle,
        PlateTextColors TitleColors,
        bool DisplayTitle,
        bool IsPrefixTitle,
        string QuotedFreeCompanyTag,
        PlateTextColors FreeCompanyColors,
        uint TextColor,
        uint EdgeColor,
        int NameIconId);

    private LocalPlateInfo? _localPlate;
    private string? _parsedHonorificJson;
    private PlateTextColors? _honorificTitleColors;
    private bool? _honorificTitleIsPrefix;
    private bool _honorificTitleRequested;

    /// <summary>The most recent local-player nameplate snapshot, if the self
    /// plate was present in a recent nameplate update.</summary>
    public LocalPlateInfo? LocalPlate => _localPlate;

    /// <summary>The embedded default supporter icon wrap, if loaded.</summary>
    public bool TryGetDefaultIconWrap(out IDalamudTextureWrap? wrap)
    {
        wrap = null;
        return _iconTexture != null && _iconTexture.TryGetWrap(out wrap, out _);
    }

    /// <summary>
    /// Reads the effective foreground/glow color a nameplate field renders its
    /// text in. Handler fields keep their last-rendered contents, so plugin
    /// colors (e.g. Honorific) are visible here - but so are our own injected
    /// supporter marks, which is why the state is tracked across payloads and
    /// sampled at the payload carrying the field's real text, skipping any
    /// text belonging to our supporter mark.
    /// </summary>
    private PlateTextColors SeStringColors(SeString? s, string needle)
    {
        if (s == null)
        {
            return new PlateTextColors(null, null);
        }
        uint? curFg = null;
        uint? curGlow = null;
        uint? fbFg = null;
        uint? fbGlow = null;
        foreach (var payload in s.Payloads)
        {
            if (payload is UIForegroundPayload fg)
            {
                curFg = fg.IsEnabled && fg.RGBA != 0 ? fg.RGBA : null;
            }
            else if (payload is UIGlowPayload glow)
            {
                curGlow = glow.IsEnabled && glow.RGBA != 0 ? glow.RGBA : null;
            }
            else if (payload is RawPayload raw)
            {
                // Color/EdgeColor macros (Honorific writes these via
                // PushColorRgba) have no dedicated payload class in this
                // Dalamud version - they decode as RawPayload.
                if (NameplateReader.TryDecodeMacroColor(raw, NameplateReader.MacroColor, out var macroFg))
                {
                    curFg = macroFg;
                }
                else if (NameplateReader.TryDecodeMacroColor(raw, NameplateReader.MacroEdgeColor, out var macroEdge))
                {
                    curGlow = macroEdge;
                }
            }
            else if (payload is TextPayload text)
            {
                var t = text.Text ?? string.Empty;
                if (needle.Length > 0 && t.Contains(needle, StringComparison.Ordinal))
                {
                    return new PlateTextColors(curFg, curGlow);
                }
                if (!fbFg.HasValue && !fbGlow.HasValue && !IsSupporterPartText(t))
                {
                    fbFg = curFg;
                    fbGlow = curGlow;
                }
            }
        }
        return new PlateTextColors(fbFg, fbGlow);
    }





    private bool IsSupporterPartText(string text)
    {
        var current = _configService.Current;
        return string.IsNullOrWhiteSpace(text)
            || string.Equals(text, current.SupporterSymbolChar, StringComparison.Ordinal)
            || string.Equals(text, current.SupporterLabelText, StringComparison.Ordinal);
    }

    private void CaptureLocalPlate(IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        var localAddr = _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;
        if (localAddr == IntPtr.Zero)
        {
            return;
        }
        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            if (h.GameObject is IPlayerCharacter pc && pc.Address == localAddr)
            {
                var view = h.InfoView;
                var prev = _localPlate;
                var nameText = view.Name?.TextValue ?? pc.Name.TextValue;
                _localPlate = new LocalPlateInfo(
                    nameText,
                    MergeColors(SeStringColors(h.Name, nameText), prev?.NameColors ?? default),
                    view.QuotedTitle?.TextValue ?? string.Empty,
                    _honorificTitleColors ?? MergeColors(SeStringColors(h.Title, view.Title?.TextValue ?? string.Empty), prev?.TitleColors ?? default),
                    h.DisplayTitle, _honorificTitleIsPrefix ?? h.IsPrefixTitle,
                    view.QuotedFreeCompanyTag?.TextValue ?? string.Empty,
                    MergeColors(SeStringColors(h.FreeCompanyTag, view.FreeCompanyTag?.TextValue ?? string.Empty), prev?.FreeCompanyColors ?? default),
                    h.TextColor, h.EdgeColor, h.NameIconId);
                return;
            }
        }
    }

    /// <summary>Runs after every nameplate handler has been processed for this
    /// pass - plate strings, icon ids and visibility reflect the final mutated
    /// state, so the masks we reconstruct match what the renderer draws.</summary>
    private void OnPostDataUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        RefreshHonorificTitleColors();
        CaptureLocalPlate(handlers);
        RequestRedrawIfSettingsChanged();

        if (!_configService.Current.ShowSupporterNameplate || !_apiController.SupporterFeaturesEnabled)
        {
            _iconPositions = [];
            _appliedPlateNames.Clear();
            if (ShouldLogTrace())
                _logger.LogDebug("[Nameplate] Skipped: show={show} supporterFeatures={features}",
                    _configService.Current.ShowSupporterNameplate, _apiController.SupporterFeaturesEnabled);
            LogStartupGates();
            return;
        }

        var logThisTick = ShouldLogTrace();
        var localAddr = _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;

        RefreshSupporterNameCache();
        if (_supporterNameCache.Count == 0)
        {
            _iconPositions = [];
            _appliedPlateNames.Clear();
            if (logThisTick)
                _logger.LogDebug("[Nameplate] Skipped: supporter cache is empty (handlers={handlers})", handlers.Count);
            LogStartupGates();
            return;
        }

        _plates.Collect(logThisTick);
        Array.Clear(_plateFxId, 0, _plateFxId.Length);
        _hasPlateFx = false;

        var needsRedraw = false;
        var playerCount = 0;
        var selfPlatePresent = false;
        var matchedCount = 0;
        List<IconDrawPosition>? iconPos = null;

        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            var obj = h.GameObject;
            if (obj is not IPlayerCharacter pc)
                continue;

            playerCount++;
            var isSelf = pc.Address == localAddr;
            if (isSelf)
                selfPlatePresent = true;

            var name = pc.Name.TextValue;
            if (!IsSupporter(name))
                continue;

            var style = ResolveStyle(name, isSelf);
            if (!style.IndicatorsEnabled)
                continue;

            matchedCount++;

            if (style.FxId.Length > 0 && NameplateFx.ById(style.FxId) != null)
            {
                _plateFxId[h.NamePlateIndex] = style.FxId;
                _hasPlateFx = true;
            }

            if ((style.SymbolEnabled || style.TextEnabled)
                && (!_appliedPlateNames.TryGetValue(h.NamePlateIndex, out var appliedFor)
                    || !string.Equals(appliedFor, name, StringComparison.OrdinalIgnoreCase)))
            {
                needsRedraw = true;
            }

            if (style.IconEnabled)
            {
                var (iconTitle, iconPrefix) = EffectiveTitleLayout(h);
                var platePos = _plates.GetIconAnchor(h.NamePlateIndex, iconTitle, iconPrefix);
                if (platePos.HasValue)
                {
                    var charPos = pc.Position;
                    iconPos ??= new List<IconDrawPosition>(8);
                    iconPos.Add(new IconDrawPosition(platePos.Value - _plates.BlockAnchor(h.NamePlateIndex),
                        charPos + Vector3.UnitY * OcclusionProbeHeight, charPos,
                        h.NamePlateIndex, _plates.Order[h.NamePlateIndex], pc.Address,
                        style.IconLabelEnabled ? style.IconLabelText : string.Empty,
                        style.IconLabelColor, style.IconId,
                        style.IconLabelEdgeEnabled ? style.IconLabelEdgeColor : 0u, style.FxId));
                }
            }
        }

        if (iconPos is { Count: > 0 })
        {
            // painter's order between players: the game's plate priority
            // decides which overlay wins on overlap - collection order is
            // handler firing order and has no relation to depth
            var sorted = iconPos.ToArray();
            for (var i = 1; i < sorted.Length; i++)
            {
                var e = sorted[i];
                var j = i - 1;
                while (j >= 0 && sorted[j].DrawOrder > e.DrawOrder)
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = e;
            }
            _iconPositions = sorted;
        }
        else
        {
            _iconPositions = [];
        }

        if (needsRedraw)
            RequestRedraw();

        if (logThisTick)
        {
            _plates.OrderDebug.Clear();
            for (var i = 0; i < _iconPositions.Count; i++)
            {
                var e = _iconPositions[i];
                _plates.OrderDebug.Append(" ic").Append(i).Append('=').Append(e.PlateIndex)
                    .Append('o').Append((int)e.DrawOrder);
            }
            _logger.LogDebug("[Nameplate] Tick: players={players} self={self} matched={matched} icons={icons} occ={occ} gpu={gpu} pl={plates}{iconOrder}",
                playerCount, selfPlatePresent, matchedCount, _iconPositions.Count, _occDebug ?? "-",
                _configService.Current.SupporterNameplateGpuOcclusion ? _gpuMask.Status : "off",
                _plates.DebugText ?? "-", _plates.OrderDebug.ToString());
        }
        LogStartupGates();
    }

    /// <summary>
    /// Refreshes the color portion of the local plate snapshot from the
    /// handler's live string fields. Fields at this point carry whatever the
    /// game and earlier-ran plugins (e.g. Honorific) wrote this pass - the
    /// InfoView snapshot taken in OnDataUpdate never contains their payloads.
    /// </summary>
    /// <summary>
    /// Color fields can be pristine in one event and carry plugin payloads in
    /// the other, so an empty extraction never overwrites a previously
    /// captured color.
    /// </summary>
    private static PlateTextColors MergeColors(PlateTextColors next, PlateTextColors fallback)
        => next.Foreground.HasValue || next.Edge.HasValue ? next : fallback;

    /// <summary>
    /// Keeps <see cref="_honorificTitleColors"/> in sync with Honorific's IPC
    /// data. Only re-parses when the reported TitleData JSON changed; kicks off
    /// a one-shot fetch when Honorific is up but nothing was reported yet
    /// (Honorific loaded before this plugin).
    /// </summary>
    private void RefreshHonorificTitleColors()
    {
        if (!_honorificIpc.APIAvailable)
        {
            _honorificTitleColors = null;
            _honorificTitleIsPrefix = null;
            _parsedHonorificJson = null;
            _honorificTitleRequested = false;
            return;
        }
        var json = _honorificIpc.LastLocalTitleJson;
        if (json.Length == 0)
        {
            if (!_honorificTitleRequested)
            {
                _honorificTitleRequested = true;
                _ = FetchHonorificTitleJsonAsync();
            }
            return;
        }
        if (string.Equals(json, _parsedHonorificJson, StringComparison.Ordinal))
            return;
        _parsedHonorificJson = json;
        _honorificTitleColors = ParseHonorificTitleData(json, out var isPrefix);
        _honorificTitleIsPrefix = isPrefix;
    }

    private async Task FetchHonorificTitleJsonAsync()
    {
        try
        {
            await _honorificIpc.GetLocalTitleJsonAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] Honorific title fetch failed");
        }
    }

    private PlateTextColors? ParseHonorificTitleData(string json, out bool? isPrefix)
    {
        isPrefix = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("IsOriginal", out var orig) && orig.ValueKind == JsonValueKind.True)
                return null;
            if (root.TryGetProperty("IsPrefix", out var pref) && pref.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isPrefix = pref.GetBoolean();
            var fg = ReadHonorificColor(root, "Color");
            var edge = ReadHonorificColor(root, "Glow");
            return fg.HasValue || edge.HasValue ? new PlateTextColors(fg, edge) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] Failed to parse Honorific title data");
            return null;
        }
    }

    private static uint? ReadHonorificColor(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var vec) || vec.ValueKind != JsonValueKind.Object)
            return null;
        if (!vec.TryGetProperty("X", out var x) || !vec.TryGetProperty("Y", out var y) || !vec.TryGetProperty("Z", out var z))
            return null;
        static uint Channel(JsonElement e) => (uint)Math.Clamp((int)MathF.Round(e.GetSingle() * 255f), 0, 255);
        return (Channel(x) << 24) | (Channel(y) << 16) | (Channel(z) << 8) | 0xFFu;
    }

    private void CaptureLocalPlateColors(INamePlateUpdateHandler h)
    {
        if (_localPlate is not { } plate)
        {
            return;
        }
        var view = h.InfoView;
        if (ShouldLogTrace())
        {
            _logger.LogDebug("[Nameplate] Self fields: title=[{title}] name=[{name}] fc=[{fc}]",
                DescribeSeString(h.Title), DescribeSeString(h.Name), DescribeSeString(h.FreeCompanyTag));
        }
        _localPlate = plate with
        {
            NameColors = MergeColors(SeStringColors(h.Name, view.Name?.TextValue ?? string.Empty), plate.NameColors),
            TitleColors = _honorificTitleColors ?? MergeColors(SeStringColors(h.Title, view.Title?.TextValue ?? string.Empty), plate.TitleColors),
            FreeCompanyColors = MergeColors(SeStringColors(h.FreeCompanyTag, view.FreeCompanyTag?.TextValue ?? string.Empty), plate.FreeCompanyColors),
        };
    }

    private static string DescribeSeString(SeString? s)
    {
        if (s == null)
        {
            return "null";
        }
        var sb = new System.Text.StringBuilder();
        foreach (var p in s.Payloads)
        {
            if (sb.Length > 0) sb.Append('|');
            sb.Append(p switch
            {
                TextPayload t => $"T:{t.Text}",
                UIForegroundPayload f => $"FG:{f.RGBA:X8}:{f.IsEnabled}",
                UIGlowPayload g => $"GL:{g.RGBA:X8}:{g.IsEnabled}",
                RawPayload r when NameplateReader.TryDecodeMacroColor(r, NameplateReader.MacroColor, out var c) => $"MC:{c?.ToString("X8") ?? "pop"}",
                RawPayload r when NameplateReader.TryDecodeMacroColor(r, NameplateReader.MacroEdgeColor, out var c) => $"ME:{c?.ToString("X8") ?? "pop"}",
                RawPayload r => $"RAW:{(r.Data.Length > 1 ? r.Data[1] : 0):X2}",
                _ => p.Type.ToString(),
            });
        }
        return sb.ToString();
    }

    private void OnNamePlateTextUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!_configService.Current.ShowSupporterNameplate || !_apiController.SupporterFeaturesEnabled)
            return;

        RefreshSupporterNameCache();
        if (_supporterNameCache.Count == 0)
            return;

        var localAddr = _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;

        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            if (h.GameObject is not IPlayerCharacter pc)
                continue;

            if (pc.Address == localAddr)
            {
                CaptureLocalPlateColors(h);
            }

            var name = pc.Name.TextValue;
            if (!IsSupporter(name))
                continue;

            var style = ResolveStyle(name, pc.Address == localAddr);
            if (!style.IndicatorsEnabled || (!style.SymbolEnabled && !style.TextEnabled))
                continue;

            var symbol = style.Symbol;
            var textEnabled = style.TextEnabled;
            var symbolEnabled = style.SymbolEnabled;
            var labelText = style.LabelText;
            var position = style.Position;
            var order = style.LabelOrder;
            var symbolColorKey = style.SymbolColorKey;
            var labelColorKey = style.LabelColorKey;

            var original = h.InfoView.Name;

            var symbolGlowEnabled = style.SymbolGlowEnabled;
            var symbolGlowKey = style.SymbolGlowKey;
            var labelGlowEnabled = style.LabelGlowEnabled;
            var labelGlowKey = style.LabelGlowKey;

            SeStringBuilder AppendSupporterParts(SeStringBuilder builder)
            {
                var first = true;
                void AddPart(string? partText, ushort partColorKey, bool glowEnabled, ushort glowKey)
                {
                    if (string.IsNullOrEmpty(partText)) return;
                    if (!first) builder.AddText(" ");
                    if (glowEnabled)
                    {
                        builder.AddUiGlow(glowKey).AddUiForeground(partText, partColorKey).AddUiGlowOff();
                    }
                    else
                    {
                        builder.AddUiForeground(partText, partColorKey);
                    }
                    first = false;
                }

                if (order == SupporterLabelOrder.SymbolThenText)
                {
                    if (symbolEnabled) AddPart(symbol, symbolColorKey, symbolGlowEnabled, symbolGlowKey);
                    if (textEnabled) AddPart(labelText, labelColorKey, labelGlowEnabled, labelGlowKey);
                }
                else
                {
                    if (textEnabled) AddPart(labelText, labelColorKey, labelGlowEnabled, labelGlowKey);
                    if (symbolEnabled) AddPart(symbol, symbolColorKey, symbolGlowEnabled, symbolGlowKey);
                }
                return builder;
            }

            var left = new SeStringBuilder().Build();
            var right = new SeStringBuilder().Build();
            if (position == SupporterSymbolPosition.Left)
            {
                left = AppendSupporterParts(new SeStringBuilder()).AddText(" ").Build();
            }
            else if (position == SupporterSymbolPosition.Right)
            {
                right = AppendSupporterParts(new SeStringBuilder().AddText(" ")).Build();
            }
            else
            {
                left = AppendSupporterParts(new SeStringBuilder()).AddText(" ").Build();
                if (symbolEnabled)
                {
                    var rightBuilder = new SeStringBuilder().AddText(" ");
                    right = (symbolGlowEnabled
                        ? rightBuilder.AddUiGlow(symbolGlowKey).AddUiForeground(symbol, symbolColorKey).AddUiGlowOff()
                        : rightBuilder.AddUiForeground(symbol, symbolColorKey)).Build();
                }
            }

            h.NameParts.Text = original;
            h.NameParts.TextWrap = (left, right);
            if (h.IsUpdating)
            {
                _appliedPlateNames[h.NamePlateIndex] = name;
            }
        }
    }

    private void RequestRedrawIfSettingsChanged()
    {
        var current = _configService.Current;
        var key = $"{current.ShowSupporterNameplate}|{current.SupporterSymbolChar}|{current.SupporterTextEnabled}|{current.SupporterLabelText}|{current.SupporterLabelOrder}|{current.SupporterSymbolPosition}|{current.SupporterColorKey}|{current.SupporterLabelColorKey}|{current.SupporterSymbolGlowEnabled}|{current.SupporterSymbolGlowKey}|{current.SupporterLabelGlowEnabled}|{current.SupporterLabelGlowKey}|{current.SupporterIconEnabled}|{current.SupporterIconId}|{current.SupporterIconLabelEnabled}|{current.SupporterIconLabelText}|{current.SupporterIconLabelColor}|{current.SupporterIconLabelEdgeEnabled}|{current.SupporterIconLabelEdgeColor}|{current.SupporterNameplateFxId}|{_apiController.SupporterFeaturesEnabled}|{string.Join(",", _supporterNameCache.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}|{_supporterStyleFingerprint}";

        var publishKey = _apiController.IsConnected && _apiController.IsSupporter && _apiController.SupporterFeaturesEnabled
            ? key
            : "unavailable";
        if (!string.Equals(publishKey, _lastPublishedStyleKey, StringComparison.Ordinal))
        {
            _lastPublishedStyleKey = publishKey;
            _ = _apiController.UserUpdateSupporterStyle(
                _apiController.IsConnected && _apiController.IsSupporter && _apiController.SupporterFeaturesEnabled
                    ? BuildOwnStyle()
                    : null);
        }
        if (string.Equals(key, _lastSupporterSettingsKey, StringComparison.Ordinal))
            return;

        var hadPreviousKey = _lastSupporterSettingsKey != null;
        _lastSupporterSettingsKey = key;
        if (hadPreviousKey)
        {
            _appliedPlateNames.Clear();
            RequestRedraw();
        }
    }

    /// <summary>
    /// Effective title layout for a plate - whatever the game (or Honorific)
    /// last wrote to the handler, so the icon anchors to the line the title
    /// actually renders on.
    /// </summary>
    private static (bool Display, bool Prefix) EffectiveTitleLayout(INamePlateUpdateHandler handler)
        => (handler.DisplayTitle, handler.IsPrefixTitle);




























    /// <summary>
    /// Subtracts the rects of nameplates drawn in front of this entry from the
    /// icon rect, one pass producing both clip sets: <paramref name="plateRects"/>
    /// covers full content (CPU-fallback clipping), <paramref name="chromeRects"/>
    /// only non-text chrome plus icon slots that produced no pixel mask - the
    /// shader masks text per pixel instead. Null means unobstructed, empty
    /// means fully covered.
    /// </summary>
    private void SubtractPlateOccluders(Vector2 min, Vector2 max, in IconDrawPosition entry,
        out List<Vector4>? plateRects, out List<Vector4>? chromeRects)
    {
        plateRects = null;
        chromeRects = null;
        for (var i = 0; i < MaxNamePlateObjects; i++)
        {
            if (i == entry.PlateIndex || _plates.Order[i] <= entry.DrawOrder)
                continue;
            var hasRect = _plates.HasRect[i];
            if (hasRect)
            {
                _occRejectDebug.Append(i).Append(_plates.Chrome[i] == Vector4.Zero && _plates.Rects[i] == Vector4.Zero ? 'z'
                    : _plates.Rects[i].Z <= min.X || _plates.Rects[i].X >= max.X || _plates.Rects[i].W <= min.Y || _plates.Rects[i].Y >= max.Y
                        ? $"r({_plates.Rects[i].X:0},{_plates.Rects[i].Y:0},{_plates.Rects[i].Z:0},{_plates.Rects[i].W:0})" : 'C').Append(' ');
            }
            if (!hasRect)
                continue;
            var cut = _plates.Rects[i];
            if (cut != Vector4.Zero
                && cut.Z > min.X && cut.X < max.X && cut.W > min.Y && cut.Y < max.Y)
            {
                plateRects ??= new List<Vector4>(8) { new Vector4(min.X, min.Y, max.X, max.Y) };
                SubtractRect(plateRects, cut);
            }
            var chr = _plates.Chrome[i];
            if (chr != Vector4.Zero
                && chr.Z > min.X && chr.X < max.X && chr.W > min.Y && chr.Y < max.Y)
            {
                chromeRects ??= new List<Vector4>(8) { new Vector4(min.X, min.Y, max.X, max.Y) };
                SubtractRect(chromeRects, chr);
            }
            // icon nodes carry per-pixel masks in the gpu pass - their rect
            // only subtracts where emission failed or the mask got filtered
            if (chromeRects is not { Count: 0 })
            {
                for (var j = 0; j < IconSlots; j++)
                {
                    var bit = (byte)(1 << j);
                    if ((_plates.IconLive[i] & bit) != 0)
                        continue;
                    var ir = _plates.IconRects[i * IconSlots + j];
                    if (ir == Vector4.Zero
                        || ir.Z <= min.X || ir.X >= max.X
                        || ir.W <= min.Y || ir.Y >= max.Y)
                        continue;
                    chromeRects ??= new List<Vector4>(8) { new Vector4(min.X, min.Y, max.X, max.Y) };
                    _occRejectDebug.Append('i').Append(j).Append(' ');
                    SubtractRect(chromeRects, ir);
                    if (chromeRects.Count is 0 or > MaxPlateClipRects)
                        break;
                }
            }
            if (plateRects is { Count: 0 or > MaxPlateClipRects }
                && chromeRects is { Count: 0 or > MaxPlateClipRects })
                break;
        }
        if (plateRects is { Count: > MaxPlateClipRects })
            plateRects.Clear();
        if (chromeRects is { Count: > MaxPlateClipRects })
            chromeRects.Clear();
    }

    /// <summary>
    /// Emits every visible plate's occluder masks once per frame into the
    /// shared _mask* scratch arrays, then snapshots them into the _frm* store.
    /// Per-element collection afterwards is a cheap filter over that store -
    /// the expensive glyph/icon/bake work below runs a single time no matter
    /// how many icons or effect layers consume it.
    /// </summary>
    private unsafe void EmitFrameMasks()
    {
        _frameEmitDebug.Clear();
        var count = 0;
        _maskTexture = 0;
        _mask2Texture = 0;
        _mask3Texture = 0;
        _mask4Texture = 0;
        _mask5Texture = 0;
        _mask6Texture = 0;
        _mask7Texture = 0;
        _mask8Texture = 0;
        ReleaseMaskTexSrvs();
        _atlasTextures[0] = _plates.BakeSrv;
        _plates.DiagPlate = -1;
        _fontDiagSet = false;
        _glyphTrace.Clear();
        _occRejectDebug.Clear();
        Array.Clear(_maskIcon, 0, _maskIcon.Length);
        Array.Clear(_maskBypass, 0, _maskBypass.Length);
        Array.Clear(_plates.IconLive, 0, _plates.IconLive.Length);
        // emission runs unbounded - every plate's full mask set goes into the
        // store and per-element selection clips by that element's bounds
        var min = new Vector2(-1e6f);
        var max = new Vector2(1e6f);
        count = AppendCursorMasks(min, max, count);
        if (_fontHandles.Count == 0 && _plates.BakeSrv == 0)
        {
            FinishFrameEmission(count);
            return;
        }
        for (var i = 0; i < MaxNamePlateObjects && count < GpuNameplateImGuiMask.MaxPlateMasks; i++)
        {
            // fold the previous plate's masks into coverage slots on the fly -
            // a crowd of front plates would otherwise exhaust the mask budget
            // with raw glyph quads before the final bake pass gets to run
            if (count > 0)
                count = BakeCoverageMasks(count);
            if (!_plates.HasRect[i])
                continue;
            _plateEdgePx[i] = 0f;
            // image-node icons sit outside the text block - emit before the
            // ink-rect check so they still mask when the text misses the icon
            count = AppendIconNodeMasks(i, min, max, count);
            var before = count;
            var block = _plates.InkRect[i];
            if (block == Vector4.Zero || block.Z <= min.X || block.X >= max.X
                || block.W <= min.Y || block.Y >= max.Y)
            {
                if (block != Vector4.Zero)
                    _occRejectDebug.Append(i).Append("t(").Append(block.X.ToString("0"))
                        .Append(',').Append(block.Y.ToString("0")).Append(',')
                        .Append(block.Z.ToString("0")).Append(',').Append(block.W.ToString("0")).Append(") ");
                continue;
            }
            // baked plates: mask with the texture the game itself rendered -
            // pixel-exact letterforms, outlines and icons, no layout guessing.
            // DrawOriginalText plates still hold a valid bake while flagged
            // baked - the atlas region keeps the text until re-baked.
            if (_plates.IsBaked[i] && _plates.BakeSrv != 0
                && _plates.BakeSize.X > 0 && _plates.BakeSize.Y > 0 && _plates.GameRect[i] != Vector4.Zero)
            {
                count = AppendBakeMask(i, min, max, count);
                if (count != before)
                {
                    // the bake already contains the edge pass - reconstructed
                    // quads are the only masks needing the dilation radius
                    for (var k = before; k < count; k++)
                    {
                        _maskPlateIdx[k] = i;
                        _maskEdge[k] = 0f;
                    }
                    _occRejectDebug.Append(i).Append('B');
                    continue;
                }
            }
            // live-text plates: lay the real game font's glyphs over the line
            // bands - the mask samples the same atlas pixels the renderer does
            if ((_plates.Name[i] != null || _plates.Title[i] != null)
                && count < GpuNameplateImGuiMask.MaxPlateMasks)
            {
                count = AppendGameGlyphs(i, min, max, count);
                if (count != before)
                    _occRejectDebug.Append(i).Append('G');
            }
            var glyphs = (_plates.Name[i] != null || _plates.Title[i] != null) && _fontHandles.Count > 0;
            if (glyphs && count == before)
            {
                count = AppendPlateGlyphs(i, min, max, count, block);
                if (count != before)
                    _occRejectDebug.Append(i).Append('P');
            }
            if (glyphs && count == before)
            {
                _occRejectDebug.Append(i).Append('g').Append(_glyphFail == 0 ? '0' : _glyphFail);
                if (_glyphFail is 'x' or 'T' or 'v')
                    _occRejectDebug.Append("n").Append(_glyphNulls)
                        .Append('b').Append(_glyphBounds)
                        .Append('t').Append(_glyphTex)
                        .Append("r(").Append(_glyphDbgRect.X.ToString("0"))
                        .Append(',').Append(_glyphDbgRect.Y.ToString("0")).Append(',')
                        .Append(_glyphDbgRect.Z.ToString("0")).Append(',').Append(_glyphDbgRect.W.ToString("0"))
                        .Append(")s(").Append(_glyphDbgSpan.X.ToString("0"))
                        .Append('-').Append(_glyphDbgSpan.Y.ToString("0"))
                        .Append("b").Append(_glyphDbgBaseline.ToString("0")).Append(')');
                _occRejectDebug.Append(' ');
            }
            if (count == before && count < GpuNameplateImGuiMask.MaxPlateMasks)
            {
                _maskTex[count] = -1;
                _maskScreen[count] = block;
                _maskUv[count] = new Vector4(0f, 0f, -1f, 0f);
                _maskChannel[count] = 3f;
                _maskAlpha[count] = _plates.NodeAlpha[i] / 255f;
                count++;
                _occRejectDebug.Append(i).Append('R');
            }
            for (var k = before; k < count; k++)
            {
                _maskPlateIdx[k] = i;
                if (_maskIcon[k] == 0)
                    _maskEdge[k] = _plateEdgePx[i];
            }
        }
        // tuning post-pass for the cumulative-advance drift case - quads stay
        // tight on the atlas glyph and the outline bleed is a shader-side tap
        for (var k = 0; k < count; k++)
        {
            if (_maskPlateIdx[k] < 0)
                continue;
            var tuneMulX = ResolvePlateTune(_maskPlateIdx[k]).AdvanceMul;
            if (MathF.Abs(tuneMulX - 1f) < 0.0001f)
                continue;
            var br = _plates.InkRect[_maskPlateIdx[k]];
            var ax = br != Vector4.Zero ? (br.X + br.Z) * 0.5f : 0f;
            var q = _maskScreen[k];
            _maskScreen[k] = new Vector4(
                ax + (q.X - ax) * tuneMulX,
                q.Y,
                ax + (q.Z - ax) * tuneMulX,
                q.W);
        }
        if (_plates.DiagPlate >= 0)
        {
            _occRejectDebug.Append(" fnt3(sz").Append(_fontDiag[3].ToString("0.#"))
                .Append('/').Append(_fontDiag[4].ToString("0.#"))
                .Append(" lh").Append(_fontDiag[5].ToString("0.#"))
                .Append(" asc").Append(_fontDiag[6].ToString("0.#"))
                .Append(" ssy").Append(_fontDiag[0].ToString("0.##"))
                .Append(" nsx").Append(_fontDiag[1].ToString("0.###"))
                .Append(" nsy").Append(_fontDiag[2].ToString("0.###"))
                .Append(" adv").Append(_fontDiag[7].ToString("0.#"))
                .Append(" m").Append(_fontDiag[8].ToString("0.#"))
                .Append(" s").Append(_fontDiag[9].ToString("0.###"))
                .Append(" sx").Append(_fontDiag[10].ToString("0.###"))
                .Append(" cs").Append(_fontDiag[11].ToString("0"))
                .Append(" g0(").Append(_fontDiag[12].ToString("0.#"))
                .Append(',').Append(_fontDiag[13].ToString("0.#"))
                .Append('|').Append(_fontDiag[14].ToString("0.#"))
                .Append(") mo").Append(_fontDiag[15].ToString("0.#"))
                .Append(" pz").Append(_fontDiag[16].ToString("0.#"))
                .Append(" lh").Append(_plates.LineSize[_plates.DiagPlate * 2].Y.ToString("0.#"))
                .Append('/').Append(_plates.LineSize[_plates.DiagPlate * 2 + 1].Y.ToString("0.#"))
                .Append(" td").Append(_plates.Measured[_plates.DiagPlate].Y.ToString("0.#"))
                .Append(" al").Append(_plates.AlignByte[_plates.DiagPlate] & 0xF)
                .Append(" ft").Append(_plates.FontType[_plates.DiagPlate])
                .Append(" tf").Append(_plates.TextFlags[_plates.DiagPlate].ToString("X"))
                .Append(" ls").Append(_plates.LineSpacing[_plates.DiagPlate])
                .Append(" ea").Append(_plates.EdgeAlpha[_plates.DiagPlate])
                .Append(" bga").Append(_plates.BgAlpha[_plates.DiagPlate])
                .Append(" ixy").Append(_plates.IconXAdj[_plates.DiagPlate])
                .Append('/').Append(_plates.IconYAdj[_plates.DiagPlate])
                .Append(" nt").Append(_plates.NodeRect[_plates.DiagPlate] != Vector4.Zero
                    ? _plates.NodeRect[_plates.DiagPlate].Y.ToString("0.#") : "0")
                .Append(" nh").Append(_plates.NodeRect[_plates.DiagPlate] != Vector4.Zero
                    ? (_plates.NodeRect[_plates.DiagPlate].W - _plates.NodeRect[_plates.DiagPlate].Y).ToString("0.#") : "0")
                .Append(" nx").Append(_plates.NodeRect[_plates.DiagPlate] != Vector4.Zero
                    ? ((_plates.NodeRect[_plates.DiagPlate].X + _plates.NodeRect[_plates.DiagPlate].Z) * 0.5f).ToString("0.#") : "0")
                .Append(" ax").Append(_plates.EffAnchor[_plates.DiagPlate] != Vector2.Zero
                    ? _plates.EffAnchor[_plates.DiagPlate].X.ToString("0.#") : "0")
                .Append(" bx").Append(_plates.SBlock[_plates.DiagPlate] != Vector2.Zero
                    ? _plates.SBlock[_plates.DiagPlate].X.ToString("0.#") : "0")
                .Append(" dx").Append(_plates.DrawnState[_plates.DiagPlate] != Vector4.Zero
                    ? _plates.DrawnState[_plates.DiagPlate].X.ToString("0.#") : "0")
                .Append('/').Append(_plates.DrawnState[_plates.DiagPlate] != Vector4.Zero
                    ? _plates.DrawnState[_plates.DiagPlate].Y.ToString("0.#") : "0")
                .Append(" gx").Append(_plates.GameRect[_plates.DiagPlate].X.ToString("0.#"))
                .Append('/').Append(_plates.GameRect[_plates.DiagPlate].Y.ToString("0.#"))
                .Append(" nps").Append(_plates.NamePlateSize)
                .Append('/').Append(_plates.FixedRes ? "1" : "0")
                .Append(" pk").Append(_fontDiag[17].ToString("0.###"))
                .Append(" bk").Append(_fontDiag[18].ToString("0.#"))
                .Append('x').Append(_fontDiag[19].ToString("0.#"))
                .Append(" ts").Append(_fontDiag[20].ToString("0.###"))
                .Append(" us").Append(_fontDiag[21].ToString("0.##"))
                .Append(" ps").Append(_fontDiag[22].ToString("0.###"))
                .Append(" tw").Append(_fontDiag[23].ToString("0.#"))
                .Append('/').Append(_fontDiag[24].ToString("0.#"))
                .Append('/').Append(_fontDiag[25].ToString("0.#"))
                .Append(" ba").Append(_plates.BakeAlpha[_plates.DiagPlate])
                .Append('(').Append(MathF.Pow(_plates.BakeAlpha[_plates.DiagPlate] / 255f, 4f).ToString("0.###"))
                .Append(')')
                .Append(" ib").Append(_plates.IsBaked[_plates.DiagPlate] ? 1 : 0)
                .Append(" do").Append(_plates.DrawOriginal[_plates.DiagPlate] ? 1 : 0)
                .Append(" ch").Append(_plates.ChainDump[_plates.DiagPlate] ?? "-")
                .Append(" ar").Append(_plates.AreaRect[_plates.DiagPlate] != Vector4.Zero
                    ? $"{_plates.AreaRect[_plates.DiagPlate].X:0},{_plates.AreaRect[_plates.DiagPlate].Y:0},{_plates.AreaRect[_plates.DiagPlate].Z:0}x{_plates.AreaRect[_plates.DiagPlate].W:0}|a{_plates.AreaAlign[_plates.DiagPlate]}f{_plates.AreaFrame[_plates.DiagPlate]}"
                    : "-")
                .Append(')');
            if (_fontIconCount > 0)
            {
                _occRejectDebug.Append(" ic(");
                for (var k = 0; k < _fontIconCount; k++)
                {
                    if (k > 0)
                        _occRejectDebug.Append(',');
                    _occRejectDebug.Append(_fontIconIds[k]);
                }
                _occRejectDebug.Append(')');
            }
            var dseq = _plates.GlyphSeq[_plates.DiagPlate];
            if (dseq != null)
            {
                _occRejectDebug.Append(" sq").Append(dseq.Length).Append('(');
                var lim = Math.Min(dseq.Length, 40);
                for (var k = 0; k < lim; k++)
                {
                    if (k > 0)
                        _occRejectDebug.Append(',');
                    _occRejectDebug.Append(dseq[k].ToString("X"));
                }
                _occRejectDebug.Append(')');
            }
            var dpl = _plates.Payload[_plates.DiagPlate];
            if (dpl != null)
            {
                // raw payload - macros the seq decode skips stay visible here,
                // so measured widths can be mapped to real byte positions
                _occRejectDebug.Append(" pl(");
                var lim = Math.Min(dpl.Length, 160);
                for (var k = 0; k < lim; k++)
                {
                    if (k > 0)
                        _occRejectDebug.Append(',');
                    _occRejectDebug.Append(dpl[k].ToString("X2"));
                }
                _occRejectDebug.Append(')');
            }
            var dends = _plates.GlyphEnds[_plates.DiagPlate];
            if (dends != null)
            {
                _occRejectDebug.Append(" en(");
                var lim = Math.Min(dends.Length, 40);
                for (var k = 0; k < lim; k++)
                {
                    if (k > 0)
                        _occRejectDebug.Append(',');
                    _occRejectDebug.Append(dends[k]);
                }
                _occRejectDebug.Append(')');
            }
            if (_glyphTrace.Length > 0)
                _occRejectDebug.Append(" gv(").Append(_glyphTrace).Append(')');
            _occRejectDebug.Append(' ');
        }
        for (var i = 0; i < MaxNamePlateObjects; i++)
        {
            var pa = _plates.GlyphAlpha[i];
            if (pa == null || !_plates.HasRect[i])
                continue;
            var lo = 255;
            var hi = 0;
            for (var k = 0; k < pa.Length; k++)
            {
                lo = Math.Min(lo, pa[k]);
                hi = Math.Max(hi, pa[k]);
            }
            _occRejectDebug.Append(' ').Append(i).Append("A(").Append(lo)
                .Append('-').Append(hi).Append("na").Append(_plates.NodeAlpha[i]).Append(')');
            var pp = _plates.Payload[i];
            if (pp != null)
            {
                _occRejectDebug.Append("pl(");
                var lim = Math.Min(pp.Length, 160);
                for (var k = 0; k < lim; k++)
                {
                    if (k > 0)
                        _occRejectDebug.Append(',');
                    _occRejectDebug.Append(pp[k].ToString("X2"));
                }
                _occRejectDebug.Append(')');
            }
        }
        if (count > 0)
            count = BakeCoverageMasks(count);
        FinishFrameEmission(count);
    }

    /// <summary>Snapshots the emitted scratch masks into the frame store and
    /// records which icon-node slots produced real occlusion - chrome
    /// subtraction falls back to node rects for slots without one.</summary>
    private void FinishFrameEmission(int count)
    {
        _frameMaskCount = Math.Min(count, _frmScreen.Length);
        for (var k = 0; k < _frameMaskCount; k++)
        {
            _frmScreen[k] = _maskScreen[k];
            _frmUv[k] = _maskUv[k];
            _frmChannel[k] = _maskChannel[k];
            _frmAlpha[k] = _maskAlpha[k];
            _frmEdge[k] = _maskEdge[k];
            _frmTex[k] = _maskTex[k];
            _frmPlate[k] = _maskPlateIdx[k];
            _frmIcon[k] = _maskIcon[k];
            _frmBypass[k] = _maskBypass[k];
        }
        for (var k = 0; k < _frameMaskCount; k++)
        {
            if (_frmIcon[k] != 0 && _frmPlate[k] >= 0)
                _plates.IconLive[_frmPlate[k]] |= _frmIcon[k];
        }
        _frameEmitDebug.Append(_occRejectDebug);
    }

    /// <summary>Runs the frame emission once per draw pass - every mask a
    /// plate could contribute is baked/kept in the store before the first
    /// element asks for its filtered subset.</summary>
    private void EnsureFrameMasks()
    {
        if (_frameMasksEmitted)
            return;
        _frameMasksEmitted = true;
        EmitFrameMasks();
    }

    /// <summary>
    /// Fills the shader's mask list with the frame's emitted masks filtered to
    /// this element: plates in front of it (or its own when
    /// <paramref name="forceSelf"/>/the self-mask test applies) and masks
    /// intersecting its bounds - per-glyph rects of real letter ink laid out
    /// with the game font, so icon pixels are only occluded by actual ink.
    /// </summary>
    private unsafe int CollectTextMasks(Vector2 min, Vector2 max, in IconDrawPosition entry,
        bool forceSelf = false, bool honorSelfMaskTest = true)
    {
        EnsureFrameMasks();
        var includeSelf = forceSelf || (honorSelfMaskTest
            && _configService.Current.SupporterNameplateTestRectSelfMask);
        var count = 0;
        for (var k = 0; k < _frameMaskCount && count < GpuNameplateImGuiMask.MaxPlateMasks; k++)
        {
            var p = _frmPlate[k];
            var s = _frmScreen[k];
            if (s.Z <= min.X || s.X >= max.X || s.W <= min.Y || s.Y >= max.Y)
                continue;
            if (p == entry.PlateIndex)
            {
                if (!includeSelf)
                    continue;
            }
            else if (p >= 0 && _plates.Order[p] <= entry.DrawOrder)
            {
                continue;
            }
            _maskScreen[count] = s;
            _maskUv[count] = _frmUv[k];
            _maskChannel[count] = _frmChannel[k];
            _maskAlpha[count] = _frmAlpha[k];
            _maskEdge[count] = _frmEdge[k];
            _maskTex[count] = _frmTex[k];
            _maskPlateIdx[count] = p;
            _maskIcon[count] = _frmIcon[k];
            _maskBypass[count] = _frmBypass[k];
            count++;
        }
        // HUD chrome draws over our overlay - capture its image nodes fresh
        // against this element's exact bounds so distant windows cost nothing
        // (the debug full-bound captures everything on screen for the overlay,
        // mask emission stays clipped to this element)
        var bound = new Vector4(min.X, min.Y, max.X, max.Y);
        if (_configService.Current.SupporterNameplateHudFullBound)
        {
            var display = ImGui.GetIO().DisplaySize;
            bound = new Vector4(0f, 0f, display.X, display.Y);
        }
        _plates.CaptureHudOccluders(bound,
            _configService.Current.SupporterNameplateDebugLog);
        count = AppendHudMasks(min, max, count);
        var kept = FilterDominantTexture(count);
        var dbgQuads = _configService.Current.SupporterNameplateDebugOverlay
            && _configService.Current.SupporterNameplateDebugQuads;
        _maskDebugCount = dbgQuads ? kept : 0;
        if (dbgQuads)
        {
            Array.Copy(_maskScreen, _maskDebugQuads, kept);
            Array.Copy(_maskUv, _maskDebugUv, kept);
            Array.Copy(_maskChannel, _maskDebugChan, kept);
            Array.Copy(_maskPlateIdx, _maskDebugPlate, kept);
        }
        _occRejectDebug.Append(_frameEmitDebug);
        return kept;
    }







    private int AppendBakeMask(int plate, Vector2 min, Vector2 max, int count)
    {
        if (count >= GpuNameplateImGuiMask.MaxPlateMasks)
            return count;
        var bk = _plates.BakeDbg[plate];
        var game = _plates.GameRect[plate];
        var ink = _plates.InkRect[plate];
        var scale = Math.Clamp(game.Z / 100f, 0.05f, 4f) * 0.5f;
        var quadW = bk.Z * scale;
        var quadH = bk.W * scale;
        if (quadW < 2f || quadH < 2f)
            return count;
        var ty = Math.Clamp(_plates.BakeTy[plate], 0f, bk.W * 0.5f);
        var ea = _plates.EffAnchor[plate];
        var x0 = (ea != Vector2.Zero ? ea.X : game.X) - quadW * 0.5f;
        var y0 = ink.Y - ty * scale;
        var x1 = x0 + quadW;
        var y1 = y0 + quadH;
        if (x1 <= min.X || x0 >= max.X || y1 <= min.Y || y0 >= max.Y)
            return count;
        _maskTex[count] = MaskTexIndex(0);
        _maskScreen[count] = new Vector4(x0, y0, x1, y1);
        _maskUv[count] = new Vector4(bk.X / _plates.BakeSize.X, bk.Y / _plates.BakeSize.Y,
            (bk.X + bk.Z) / _plates.BakeSize.X, (bk.Y + bk.W) / _plates.BakeSize.Y);
        _maskChannel[count] = 3f;
        _maskAlpha[count] = 1f;
        return count + 1;
    }

    /// <summary>
    /// Resolves the plate's own font from AtkFontManager and emits one mask quad
    /// per glyph over the measured line bands - the shader samples the exact
    /// atlas texels and channel the game draws the letters from.
    /// </summary>
    private unsafe int AppendGameGlyphs(int plate, Vector2 min, Vector2 max, int count)
    {
        var fontType = _plates.FontType[plate];
        var module = RaptureAtkModule.Instance();
        if (fontType is < 0 or >= 6 || module == null)
            return count;
        var fontManager = &module->AtkFontManager;
        if (fontManager->FontSets == null)
            return count;
        var fontSet = fontManager->FontSets + fontType;
        var fontSize = _plates.FontSize[plate];
        if (fontSize > 0)
            fontSet->CurrentFontSize = fontSize;
        var font = fontSet->PickFont(true);
        if (font == null || font->Glyphs == null || font->TextureWidth == 0)
            return count;
        var primaryPages = CaptureGameFontPages(font, 1);
        if (primaryPages == 0)
            return count;
        _secFont = null;
        _secTexBase = 0;
        _secRatio = 1f;
        _altFont = null;
        _altTexBase = 0;
        _altRatio = 1f;
        _altSearched = false;
        var captured = 1 + primaryPages;
        var sec = font->Secondary;
        if (sec != null && sec != font && sec->TextureWidth != 0)
        {
            var secPages = CaptureGameFontPages(sec, captured);
            if (secPages > 0)
            {
                _secFont = sec;
                _secTexBase = captured;
                _secRatio = MathF.Abs(font->SecondaryRatio) > 0.001f
                    ? font->SecondaryRatio
                    : (MathF.Abs(sec->Size) > 0.001f ? font->Size / sec->Size : 1f);
                captured += secPages;
            }
        }
        _altNextSlot = captured;
        _imguiFont = null;
        _imguiTexBase = 0;
        _imguiFontSize = 0f;
        // the supporter preview proves the shared ImGui atlas carries « » -
        // scan every font in it (game-font handles plus the default stack)
        var atlas = ImGui.GetIO().Fonts;
        if (atlas.Handle != null)
        {
            var fonts = atlas.Fonts;
            for (var fi = 0; fi < fonts.Size && _imguiFont == null; fi++)
            {
                var imf = fonts[fi];
                if (imf.Handle == null)
                    continue;
                if (imf.Handle->IndexLookup.Size == 0)
                    imf.Handle->BuildLookupTable();
                var probe = (ImGuiHelpers.ImFontGlyphReal*)imf.FindGlyph(0xAB);
                if (probe == null || !probe->Visible)
                    probe = (ImGuiHelpers.ImFontGlyphReal*)imf.FindGlyph(0xE040);
                if (probe == null || !probe->Visible)
                    continue;
                var pages = CaptureAtlasTexturesAt(imf, _altNextSlot);
                if (pages == 0)
                    continue;
                _imguiFont = imf.Handle;
                _imguiTexBase = _altNextSlot;
                _imguiFontSize = imf.FontSize;
                _altNextSlot += pages;
            }
            if (_imguiFont == null)
            {
                // glyphs rasterize lazily - drawing the characters once with an
                // invisible off-screen color makes the shared atlas build them
                var drawList = ImGui.GetBackgroundDrawList();
                for (var fi = 0; fi < fonts.Size; fi++)
                {
                    var req = fonts[fi];
                    if (req.Handle == null)
                        continue;
                    ImGui.PushFont(req);
                    drawList.AddText(new Vector2(-200f, -200f), 0u, "«»");
                    ImGui.PopFont();
                }
            }
        }
        // the gfd icon sheet - literal « » on plates are icons 54/55, not font
        // glyphs; the shader samples real icon pixels through this slot
        if (EnsureGfd(_altNextSlot) >= 0)
            _altNextSlot++;
        for (var p = _altNextSlot; p < _atlasTextures.Length; p++)
            _atlasTextures[p] = 0;
        // the node's own text - exact characters and icon payloads as rendered
        var seq = _plates.GlyphSeq[plate];
        if (seq != null)
            return AppendGameSequence(fontSet, font, seq, plate, min, max, count);
        var name = _plates.Name[plate];
        var title = _plates.Title[plate];
        var tdW = _plates.Measured[plate].X;
        var titleW = _plates.TitleSize[plate].X;
        var nameW = titleW > 0 && titleW >= tdW * 0.98f ? 0f : tdW;
        if (name != null && _plates.NameLine[plate] != Vector4.Zero)
        {
            var n = StringToSeq(name);
            count = AppendGameSeq(fontSet, font, _lineSeqBuf, 0, n,
                _plates.NameLine[plate], nameW, plate, min, max, count);
        }
        if (title != null && _plates.TitleLine[plate] != Vector4.Zero)
        {
            var n = StringToSeq(title);
            count = AppendGameSeq(fontSet, font, _lineSeqBuf, 0, n,
                _plates.TitleLine[plate], titleW, plate, min, max, count);
        }
        return count;
    }

    private readonly int[] _lineSeqBuf = new int[256];
    private unsafe AtkFontManager.Font* _secFont;
    private int _secTexBase;
    private float _secRatio = 1f;
    private unsafe AtkFontManager.Font* _altFont;
    private int _altTexBase;
    private float _altRatio = 1f;
    private int _altNextSlot;
    private bool _altSearched;
    private unsafe ImFont* _imguiFont;
    private int _imguiTexBase;
    private float _imguiFontSize;
    private GfdEntryLite[] _gfdEntries = [];
    private nint _gfdSrv;
    private int _gfdTexSlot = -1;
    private bool _gfdTried;
    private readonly HashSet<int> _inlineIconSigs = new();
    // inline text-icon mask tuning resolves per plate via ResolvePlateTune -
    // the gfd emit keeps its renderer-derived rect unless values deviate
    private readonly float[] _fontDiag = new float[26];
    private bool _fontDiagSet;
    private readonly int[] _fontIconIds = new int[8];
    private int _fontIconCount;
    private readonly System.Text.StringBuilder _glyphTrace = new(320);

    private int StringToSeq(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length && n < _lineSeqBuf.Length; i++)
            _lineSeqBuf[n++] = (int)PackUtf8Value(text, ref i);
        return n;
    }







    /// <summary>Recovers the unicode codepoint from a packed utf-8 key - the
    /// game stores the utf-8 bytes big-endian in the glyph map key, so the
    /// lead byte sits in the highest used byte position.</summary>
    private static int UnpackUtf8Value(int v)
    {
        var n = (uint)v;
        if (n < 0x80)
            return (int)n;
        if ((n & 0xFFFFE0C0u) == 0x0000C080u)
            return (int)((((n >> 8) & 0x1Fu) << 6) | (n & 0x3Fu));
        if ((n & 0xFFF0C0C0u) == 0x00E08080u)
            return (int)((((n >> 16) & 0x0Fu) << 12) | (((n >> 8) & 0x3Fu) << 6) | (n & 0x3Fu));
        if ((n & 0xF8C0C0C0u) == 0xF0808080u)
            return (int)((((n >> 24) & 0x07u) << 18) | (((n >> 16) & 0x3Fu) << 12)
                | (((n >> 8) & 0x3Fu) << 6) | (n & 0x3Fu));
        return -1;
    }


    /// <summary>
    /// Splits the decoded node text at line breaks and lays each line into its
    /// measured band - document order maps to the vertical order the prefix
    /// flag implies (prefix title on top, suffix title below the name).
    /// </summary>
    private unsafe int AppendGameSequence(AtkFontManager.FontSet* fontSet, AtkFontManager.Font* font,
        int[] seq, int plate, Vector2 min, Vector2 max, int count)
    {
        var prefix = ((int)_plates.GameRect[plate].W & 0x1) != 0;
        var tdW = _plates.Measured[plate].X;
        var titleW = _plates.TitleSize[plate].X;
        var nameW = titleW > 0 && titleW >= tdW * 0.98f ? 0f : tdW;
        var cells = _plates.Cells[plate];
        var start = 0;
        var line = 0;
        for (var i = 0; i <= seq.Length && line < 2 && count < GpuNameplateImGuiMask.MaxPlateMasks; i++)
        {
            if (i < seq.Length && seq[i] != -3)
                continue;
            var band = line == 0
                ? (prefix ? _plates.TitleLine[plate] : _plates.NameLine[plate])
                : (prefix ? _plates.NameLine[plate] : _plates.TitleLine[plate]);
            // the node's own per-line draw width beats any tdW/titleW guessing
            var lineW = _plates.LineSize[plate * 2 + line].X;
            var measuredW = lineW > 1f ? lineW : line == 0
                ? (prefix ? titleW : nameW)
                : (prefix ? nameW : titleW);
            if (band != Vector4.Zero && i > start)
                count = AppendGameSeq(fontSet, font, seq, start, i, band, measuredW, plate, min, max, count,
                    cells, _plates.CellsW[plate * 2 + line], _plates.GlyphAlpha[plate]);
            start = i + 1;
            line++;
        }
        if (line == 0 && _plates.NameLine[plate] != Vector4.Zero)
        {
            var lineW = _plates.LineSize[plate * 2].X;
            count = AppendGameSeq(fontSet, font, seq, 0, seq.Length,
                _plates.NameLine[plate], lineW > 1f ? lineW : nameW, plate, min, max, count,
                cells, _plates.CellsW[plate * 2], _plates.GlyphAlpha[plate]);
        }
        return count;
    }

    /// <summary>Binary search in the font's kerning table - pairs are stored
    /// ordered by (left, right) utf8 key.</summary>
    private static unsafe int FindKerning(AtkFontManager.Font* font, uint left, uint right)
    {
        var kern = font->Kerning;
        if (kern == null)
            return 0;
        var lo = 0;
        var hi = (int)font->KerningCount - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var e = kern[mid];
            if (e.Left < left || (e.Left == left && e.Right < right))
                lo = mid + 1;
            else if (e.Left > left || (e.Left == left && e.Right > right))
                hi = mid - 1;
            else
                return e.Adjustment;
        }
        return 0;
    }

    /// <summary>Binds the font's atlas pages into texture slots
    /// starting at <paramref name="baseSlot"/> - glyph TextureIndex maps to
    /// baseSlot + index.</summary>
    private unsafe int CaptureGameFontPages(AtkFontManager.Font* font, int baseSlot)
    {
        var pages = Math.Min((int)font->TextureCount, _atlasTextures.Length - baseSlot);
        if (pages < 0)
            pages = 0;
        var ready = 0;
        for (var p = 0; p < pages; p++)
        {
            var tex = font->Textures[p].Value;
            _atlasTextures[baseSlot + p] = tex != null ? (nint)tex->D3D11ShaderResourceView : 0;
            if (_atlasTextures[baseSlot + p] != 0)
                ready++;
        }
        return ready;
    }

    /// <summary>Looks the utf8 key up in the primary font, then in the
    /// secondary fallback font the game itself uses for glyphs the primary
    /// lacks (e.g. the « » quotes). Returns the owning font, the metric
    /// scale ratio and the texture-slot base for the glyph's page index.</summary>
    private unsafe AtkFontManager.Glyph* ResolveGameGlyph(AtkFontManager.FontSet* fontSet,
        AtkFontManager.Font* font, uint key, out AtkFontManager.Font* src,
        out float ratio, out int texBase)
    {
        var cp = key > 0x7F ? UnpackUtf8Value((int)key) : (int)key;
        // literal « » in plate text are drawn as the se-icon auto-translate
        // glyphs - PUA codepoints 0xE040/0xE041, not latin-1 0xAB/0xBB
        var iconCp = cp == 0xAB ? 0xE040 : cp == 0xBB ? 0xE041 : -1;
        var iconKey = iconCp >= 0 ? PackUtf8Value(iconCp) : 0u;
        src = font;
        ratio = 1f;
        texBase = 1;
        var g = LookupGameGlyph(fontSet, font, key, cp, iconKey, iconCp);
        var sec = _secFont;
        if (g == null && sec != null)
        {
            g = LookupGameGlyph(fontSet, sec, key, cp, iconKey, iconCp);
            if (g != null)
            {
                src = sec;
                ratio = _secRatio;
                texBase = _secTexBase;
                return g;
            }
        }
        if (g == null && key > 0x7F)
        {
            if (!_altSearched)
            {
                _altSearched = true;
                TryCaptureAltFont(font);
            }
            var alt = _altFont;
            if (alt != null)
            {
                g = LookupGameGlyph(fontSet, alt, key, cp, iconKey, iconCp);
                if (g != null)
                {
                    src = alt;
                    ratio = _altRatio;
                    texBase = _altTexBase;
                    return g;
                }
            }
        }
        return g;
    }

    /// <summary>Every key form a char can take: packed utf-8, raw codepoint,
    /// the se-icon PUA codepoint for « », plus a linear scan as the last word.</summary>
    private static unsafe AtkFontManager.Glyph* LookupGameGlyph(AtkFontManager.FontSet* fontSet,
        AtkFontManager.Font* font, uint key, int cp, uint iconKey, int iconCp)
    {
        var g = fontSet->GetGlyph(key, font);
        if (g == null && key > 0x7F)
            g = fontSet->GetGlyph((uint)cp, font);
        if (g == null && iconKey != 0)
            g = fontSet->GetGlyph(iconKey, font);
        if (g == null && key > 0x7F)
            g = FindGlyphByCodepoint(font, cp);
        if (g == null && iconCp >= 0)
            g = FindGlyphByCodepoint(font, iconCp);
        return g;
    }



    /// <summary>When the plate's own font family lacks a codepoint entirely
    /// (fc « » quotes are not in the nameplate font), search the game's whole
    /// fallback world: first the full Secondary chain (it goes deeper than
    /// one link), then every loaded font in the manager - symbol fonts live
    /// outside the six font sets and are never returned by PickFont.</summary>
    private unsafe void TryCaptureAltFont(AtkFontManager.Font* font)
    {
        var module = RaptureAtkModule.Instance();
        var fm = module != null ? &module->AtkFontManager : null;
        if (fm == null)
            return;
        var depth = 0;
        for (var f = font->Secondary;
            f != null && f != font && f != _secFont && depth++ < 8; f = f->Secondary)
        {
            if (f->TextureWidth == 0 || f->Glyphs == null
                || (FindGlyphByCodepoint(f, 0xAB) == null
                    && FindGlyphByCodepoint(f, 0xE040) == null))
                continue;
            if (TryBindAltFont(f, font))
                return;
        }
        var fonts = fm->Fonts;
        if (fonts == null)
            return;
        var n = Math.Min((int)fm->FontCount, 64);
        for (var i = 0; i < n && _altFont == null; i++)
        {
            var f = fonts + i;
            if (f == font || f == _secFont || f->TextureWidth == 0 || f->Glyphs == null
                || (FindGlyphByCodepoint(f, 0xAB) == null
                    && FindGlyphByCodepoint(f, 0xE040) == null))
                continue;
            TryBindAltFont(f, font);
        }
    }

    /// <summary>Binds an alternate font's atlas pages behind the slots already
    /// used and records it as the cross-font fallback source.</summary>
    private unsafe bool TryBindAltFont(AtkFontManager.Font* alt, AtkFontManager.Font* font)
    {
        var pages = CaptureGameFontPages(alt, _altNextSlot);
        if (pages == 0)
            return false;
        _altFont = alt;
        _altTexBase = _altNextSlot;
        _altRatio = MathF.Abs(alt->Size) > 0.001f ? font->Size / alt->Size : 1f;
        _altNextSlot += pages;
        return true;
    }

    /// <summary>Linear scan of the font's glyph array matching the decoded
    /// codepoint - Utf8Value is the map key the renderer itself uses, so this
    /// finds a glyph regardless of which key form GetGlyph expects.</summary>
    private static unsafe AtkFontManager.Glyph* FindGlyphByCodepoint(AtkFontManager.Font* font, int codepoint)
    {
        var glyphs = font->Glyphs;
        if (glyphs == null || font->GlyphCount <= 0)
            return null;
        var n = Math.Min((int)font->GlyphCount, 4096);
        for (var i = 0; i < n; i++)
        {
            var g = glyphs + i;
            var uv = (int)g->Utf8Value;
            if (uv == codepoint || (uv > 0xFF && UnpackUtf8Value(uv) == codepoint))
                return g;
        }
        return null;
    }

    /// <summary>
    /// Advances a pen through <paramref name="text"/> with the game's own glyph
    /// metrics - advance = glyph width + CharSpacing - centering the line on its
    /// measured band and emitting one screen+UV quad per glyph.
    /// </summary>
    private unsafe int AppendGameSeq(AtkFontManager.FontSet* fontSet, AtkFontManager.Font* font,
        int[] seq, int start, int end, Vector4 band, float measuredW,
        int plate, Vector2 min, Vector2 max, int count, float[]? cells = null,
        float cellsW = 0f, byte[]? alphas = null)
    {
        if (cells != null && cells.Length != seq.Length * 2)
            cells = null;
        if (alphas != null && alphas.Length != seq.Length)
            alphas = null;
        var nodeA = _plates.NodeAlpha[plate] / 255f;
        // the bake is a fixed raster the game resamples linearly - pens scale
        // with it, so stale-at-build-width cells just need the current ratio
        var cellScale = cells != null && cellsW > 0.5f && measuredW > 0.5f
            ? measuredW / cellsW
            : 1f;
        var bandH = band.W - band.Y;
        if (bandH < 2f)
            return count;
        var lineH = font->LineHeight > 0 ? font->LineHeight : Math.Max(4f, font->Size);
        // nameplates lay text out at the node's requested point size
        // (FontSize 36 on these plates - the bake texture renders at 2x
        // layout, bk468 ~= 2*tw226), the fontset resamples the 14pt font up
        // to that request, and the text node's own transform (ns = pz/2)
        // shrinks the bake back to screen. font-nominal -> screen is
        // therefore resample * nodeScale, which lands within ~1% of the
        // measured m/drawRight fit
        var reqSize = _plates.FontSize[plate] > 0
            ? _plates.FontSize[plate]
            : fontSet->CurrentFontSize;
        var resample = reqSize > 0f && font->Size > 0f ? reqSize / font->Size : 1f;
        var nodeScale = _plates.NodeScale[plate].X;
        if (nodeScale <= 0.01f)
            nodeScale = _plates.GameRect[plate].Z / 200f;
        var s = nodeScale > 0.01f ? nodeScale * resample : bandH / lineH;
        // nameplates bake their text once at a fixed font resolution - 4/3
        // over the font's units - and OnRequestedUpdate stores the current
        // resample factor in BakeData.Alpha as factor^(1/4)*255, so the real
        // glyph scale is bake factor × alpha^4 (FontChanger documents this)
        var bakeA4 = _plates.IsBaked[plate] && _plates.BakeAlpha[plate] > 0
            ? MathF.Pow(_plates.BakeAlpha[plate] / 255f, 4f)
            : 0f;
        var sx = bakeA4 > 0f ? NameplateBakeFactor * bakeA4 : s;
        // the line's slot height at render scale - feeds the band/anchor layout:
        // the anchor sits one slot below the last line's origin
        _plates.LineStep[plate * 2 + 1] = lineH * sx;
        var topBand = ((int)_plates.GameRect[plate].W & 0x1) != 0
            ? _plates.TitleLine[plate]
            : _plates.NameLine[plate];
        if (band == topBand)
            // the layout's line-to-line pitch is the fontset's scaled size, not
            // the LineHeight slot - nameplate lines pack tighter than their boxes
            _plates.LineStep[plate * 2] = fontSet->ScaledSizeY * s;
        var charSp = _plates.CharSpacing[plate];
        if (_plates.DiagPlate == -1)
        {
            _plates.DiagPlate = plate;
            _fontIconCount = 0;
        }
        var textAdv = 0f;
        var icons = 0;
        uint prev = 0;
        for (var i = start; i < end; i++)
        {
            var v = seq[i];
            if (v is -2 or <= -1000)
            {
                icons++;
                prev = 0;
                continue;
            }
            if (v < 0)
                continue;
            var g = ResolveGameGlyph(fontSet, font, (uint)v, out _, out var gRatio, out _);
            if (g == null && v > 0x7F)
            {
                // an unresolved wide char is most likely an icon glyph whose
                // advance comes from the measured line, not a half-size guess
                icons++;
                prev = 0;
                continue;
            }
            if (prev != 0)
                textAdv += FindKerning(font, prev, (uint)v);
            // glyph advance is BoundingWidth + NextOffsetX (stored as
            // Glyph.OffsetX) - Width alone is only the ink box
            textAdv += (g != null ? (g->Width + g->OffsetX) * gRatio : font->Size * 0.5f) + charSp;
            prev = (uint)v;
        }
        // icons (fc brackets, status marks) have no glyph metrics - their real
        // advance is whatever the measured line leaves after the text, shared
        // evenly across the icon slots in this line
        var iconW = font->Size * 0.55f;
        if (measuredW > 1f && icons > 0)
        {
            var perIcon = (measuredW / sx - textAdv) / icons;
            if (perIcon > 0.05f && perIcon < font->Size * 3f)
                iconW = perIcon;
        }
        // draw-size span - GetTextDrawSize reports the game's own layout
        // result: max over glyphs of (penX + max(BoundingWidth, AdvanceWidth))
        // per Dalamud's GameFontLayoutPlan, not a pure ink box
        var inkMin = float.MaxValue;
        var inkMax = float.MinValue;
        var drawRight = 0f;
        var minOffY = float.MaxValue;
        var firstOX = 0f;
        var firstOY = 0f;
        var firstW = 0f;
        var sawGlyph = false;
        var pen = 0f;
        prev = 0;
        for (var i = start; i < end; i++)
        {
            var v = seq[i];
            if (v is -2 or <= -1000)
            {
                // icon ink centers inside its cell - treating the whole cell as
                // ink would skew the line center by the icon's bearings
                inkMin = Math.Min(inkMin, pen + iconW * 0.2f);
                inkMax = Math.Max(inkMax, pen + iconW * 0.8f);
                drawRight = Math.Max(drawRight, pen + iconW);
                pen += iconW + charSp;
                prev = 0;
                continue;
            }
            if (v < 0)
                continue;
            var g2 = ResolveGameGlyph(fontSet, font, (uint)v, out _, out var r2, out _);
            if (g2 == null && v > 0x7F)
            {
                inkMin = Math.Min(inkMin, pen + iconW * 0.2f);
                inkMax = Math.Max(inkMax, pen + iconW * 0.8f);
                drawRight = Math.Max(drawRight, pen + iconW);
                pen += iconW + charSp;
                prev = 0;
                continue;
            }
            if (prev != 0)
                pen += FindKerning(font, prev, (uint)v);
            var w2 = g2 != null ? g2->Width * r2 : font->Size * 0.5f;
            var adv2 = g2 != null ? (g2->Width + g2->OffsetX) * r2 : w2;
            if (g2 != null)
            {
                if (!sawGlyph)
                {
                    sawGlyph = true;
                    firstW = g2->Width * r2;
                    firstOX = g2->OffsetX * r2;
                    firstOY = g2->OffsetY * r2;
                }
                minOffY = Math.Min(minOffY, g2->OffsetY * r2);
            }
            inkMin = Math.Min(inkMin, pen);
            inkMax = Math.Max(inkMax, pen + w2);
            drawRight = Math.Max(drawRight, pen + Math.Max(w2, adv2));
            pen += adv2 + charSp;
            prev = (uint)v;
        }
        if (drawRight < 0.5f)
            return count;
        if (minOffY > 900f)
            minOffY = 0f;
        // without a bake alpha the measured span is the next-best estimate -
        // lines rendered in a bigger name font sit far outside the primary
        // font's band ratio, so only an absolute sanity clamp applies here
        if (bakeA4 <= 0f && measuredW > 1f)
            sx = Math.Clamp(measuredW / drawRight, 0.4f, 2.5f);
        // with measured pens the cells carry the layout - sx is only a
        // width-fit fallback then; glyph bitmaps want the true render scale
        // (line-height ratio) or the reconstructed-advance error leaks into
        // every quad's width
        var gs = cells != null ? s : sx;
        // the node's edge pass dilates each glyph's ink ~1 font unit past its
        // box - the shader's cutout taps must reach equally far or a ring of
        // letter pixels survives around every hole
        var edgeU = EdgeUnits(plate);
        _plateEdgePx[plate] = PackEdge(edgeU, gs);
        if (_plates.DiagPlate == plate && _glyphTrace.Length < 3000)
        {
            _glyphTrace.Append('[').Append((int)band.X).Append("..")
                .Append((int)band.Z).Append(" m").Append((int)measuredW)
                .Append(" s").Append(s.ToString("0.00"))
                .Append(" ns").Append(nodeScale.ToString("0.000"))
                .Append(" sx").Append(sx.ToString("0.00"))
                .Append('/').Append(gs.ToString("0.00"))
                .Append(" b").Append((int)band.Y).Append('-').Append((int)band.W)
                .Append(']');
            if (cells != null)
            {
                _glyphTrace.Append("cl(");
                for (var k = start; k < end; k++)
                {
                    if (k > start)
                        _glyphTrace.Append(',');
                    _glyphTrace.Append((int)cells[k * 2]).Append('-')
                        .Append((int)cells[k * 2 + 1]);
                }
                _glyphTrace.Append(')');
            }
            // dump the kerning pairs that could play in this line so real
            // layout steps can be checked against what we apply
            var kc = (int)font->KerningCount;
            if (font->Kerning != null && kc > 0)
            {
                _glyphTrace.Append("kt(");
                var shown = 0;
                for (var k = 0; k < kc && shown < 40; k++)
                {
                    var e = font->Kerning[k];
                    var inLine = false;
                    for (var i = start; i < end; i++)
                    {
                        if (seq[i] == (int)e.Left || seq[i] == (int)e.Right)
                        {
                            inLine = true;
                            break;
                        }
                    }
                    if (!inLine)
                        continue;
                    if (shown > 0)
                        _glyphTrace.Append(',');
                    _glyphTrace.Append(e.Left.ToString("X")).Append('-')
                        .Append(e.Right.ToString("X")).Append(':').Append(e.Adjustment);
                    shown++;
                }
                _glyphTrace.Append(')');
            }
        }
        if (!_fontDiagSet && _plates.DiagPlate == plate)
        {
            _fontDiagSet = true;
            _fontDiag[0] = fontSet->ScaledSizeY;
            _fontDiag[1] = fontSet->NodeScaleX;
            _fontDiag[2] = fontSet->NodeScaleY;
            _fontDiag[3] = fontSet->CurrentFontSize;
            _fontDiag[4] = font->Size;
            _fontDiag[5] = font->LineHeight;
            _fontDiag[6] = font->Ascent;
            _fontDiag[7] = drawRight;
            _fontDiag[8] = measuredW;
            _fontDiag[16] = _plates.GameRect[plate].Z;
            _fontDiag[9] = s;
            _fontDiag[10] = gs;
            _fontDiag[11] = charSp;
            _fontDiag[12] = firstOX;
            _fontDiag[13] = firstOY;
            _fontDiag[14] = firstW;
            _fontDiag[15] = minOffY;
            var fi = _plates.FontInfo[plate];
            _fontDiag[17] = fi.X;
            _fontDiag[18] = fi.Y;
            _fontDiag[19] = fi.Z;
            _fontDiag[20] = fi.W;
            _fontDiag[21] = *(float*)((byte*)fontSet + 0x124);
            _fontDiag[22] = *(float*)((byte*)fontSet + 0x15C);
            _fontDiag[23] = _plates.TextW[plate];
            _fontDiag[24] = _plates.TextH[plate];
            _fontDiag[25] = _plates.Measured[plate].Y;
        }
        // the measured rect's left edge is the first glyph's pen - glyphs
        // carry no left bearing in this font model (box at pen), so anchoring
        // the pen there beats re-centering our independently summed span
        var penX = band.X;
        var firstIconIdx = -1;
        var lastIconIdx = -1;
        for (var i = start; i < end; i++)
        {
            if (seq[i] is -2 or <= -1000)
            {
                if (firstIconIdx < 0)
                    firstIconIdx = i;
                lastIconIdx = i;
            }
        }
        prev = 0;
        var iconGlyphs = 0;
        var iconRects = 0;
        var unresolved = 0;
        for (var i = start; i < end && count < GpuNameplateImGuiMask.MaxPlateMasks; i++)
        {
            var v = seq[i];
            var cell0 = cells != null ? cells[i * 2] : -1f;
            var cell1 = cells != null ? cells[i * 2 + 1] : -1f;
            var maskA = alphas != null ? nodeA * alphas[i] / 255f : nodeA;
            if (v is -2 or <= -1000)
            {
                var cellX = cell0 >= 0f ? band.X + cell0 * cellScale : penX;
                penX = cell1 >= 0f ? band.X + cell1 * cellScale : cellX + (iconW + charSp) * gs;
                if (_plates.DiagPlate == plate && _glyphTrace.Length < 3000)
                {
                    _glyphTrace.Append('I').Append(v.ToString("X")).Append('@')
                        .Append((int)(cellX - min.X));
                    if (cells != null)
                    {
                        _glyphTrace.Append('|')
                            .Append((int)(band.X + Math.Max(0f, cell0) * cellScale - min.X));
                    }
                    _glyphTrace.Append('+').Append((int)(penX - cellX)).Append(',');
                }
                var emitted = false;
                var emitRect = Vector4.Zero;
                var outcome = "skip";
                var iconId = v <= -2000 ? -v - 2000 : v <= -1000 ? -v - 1000 : -1;
                if (iconId >= 0 && _plates.DiagPlate == plate
                    && _fontIconCount < _fontIconIds.Length)
                {
                    _fontIconIds[_fontIconCount++] = iconId;
                }
                var inRange = penX - cellX > 0.5f && penX >= min.X && cellX <= max.X
                    && band.W >= min.Y && band.Y <= max.Y;
                if (iconId >= 0 && inRange)
                {
                    // icon payloads draw from the gfd icon sheet - not from the
                    // font - and only icon2 (0x1E) goes through the remap table
                    if (TryEmitGfdIcon(iconId, plate, cellX, font->Size * gs, band,
                            min, max, ref count, remap: v <= -2000, maskA, out emitRect))
                    {
                        emitted = true;
                        iconGlyphs++;
                        outcome = emitRect != Vector4.Zero ? "gfd" : "oob";
                    }
                    else if (icons == 2)
                    {
                        // a lone icon pair wrapping text is the free-company
                        // « » quotes - fall back to the font's real guillemet
                        // glyphs when the icon sheet is unavailable
                        var quote = i == firstIconIdx ? 0xC2ABu : (i == lastIconIdx ? 0xC2BBu : 0u);
                        AtkFontManager.Font* giFont = null;
                        var giRatio = 1f;
                        var giTexBase = 0;
                        var gi = quote != 0
                            ? ResolveGameGlyph(fontSet, font, quote,
                                out giFont, out giRatio, out giTexBase)
                            : null;
                        var texSlot2 = gi != null && gi->Width > 0 && gi->Height > 0
                            ? giTexBase + gi->TextureIndex
                            : _atlasTextures.Length;
                        if (texSlot2 < _atlasTextures.Length && _atlasTextures[texSlot2] != 0)
                        {
                            // the glyph stands in for an icon payload - center
                            // it in the icon cell the way the icon is drawn
                            var gw2 = gi->Width * giRatio * gs;
                            var ix0 = cellX + (penX - cellX - gw2) * 0.5f;
                            var iy0 = band.Y + gi->OffsetY * giRatio * gs;
                            var ix1 = ix0 + gw2;
                            var iy1 = iy0 + gi->Height * giRatio * gs;
                            if (ix1 > min.X && ix0 < max.X && iy1 > min.Y && iy0 < max.Y)
                            {
                                _maskTex[count] = MaskTexIndex(texSlot2);
                                _maskScreen[count] = emitRect = new Vector4(ix0, iy0, ix1, iy1);
                                _maskUv[count] = new Vector4(
                                    gi->X / (float)giFont->TextureWidth, gi->Y / (float)giFont->TextureHeight,
                                    (gi->X + gi->Width) / (float)giFont->TextureWidth,
                                    (gi->Y + gi->Height) / (float)giFont->TextureHeight);
                                _maskChannel[count] = gi->Channel;
                                _maskAlpha[count] = maskA;
                                count++;
                                emitted = true;
                                iconGlyphs++;
                                outcome = "quote";
                            }
                        }
                    }
                }
                if (!emitted && inRange)
                {
                    _maskTex[count] = -1;
                    _maskScreen[count] = emitRect = new Vector4(cellX, band.Y, penX, band.W);
                    _maskUv[count] = new Vector4(0f, 0f, -1f, 0f);
                    _maskChannel[count] = 3f;
                    _maskAlpha[count] = maskA;
                    count++;
                    iconRects++;
                    outcome = "rect";
                }
                LogInlineIcon(plate, v, iconId, cellX, penX, band, outcome, emitRect);
                prev = 0;
                continue;
            }
            if (v < 0)
                continue;
            var g = ResolveGameGlyph(fontSet, font, (uint)v, out var gFont, out var gRatio, out var gTexBase);
            var kern = prev != 0 ? FindKerning(font, prev, (uint)v) : 0;
            var adv = g != null ? (g->Width + g->OffsetX) * gRatio
                : v > 0x7F ? iconW : font->Size * 0.5f;
            float cellX2;
            if (cell0 >= 0f)
            {
                // the suffix measure recovered the element's true pen - the
                // game's own layout, kerning and icon advances included
                cellX2 = band.X + cell0 * cellScale;
                penX = cell1 >= 0f ? band.X + cell1 * cellScale : cellX2 + (adv + charSp) * gs;
            }
            else
            {
                if (kern != 0)
                    penX += kern * gs;
                cellX2 = penX;
                penX += (adv + charSp) * gs;
            }
            prev = (uint)v;
            if (_plates.DiagPlate == plate && _glyphTrace.Length < 3000)
            {
                _glyphTrace.Append(v.ToString("X")).Append('@')
                    .Append((int)(cellX2 - min.X));
                if (cells != null)
                {
                    _glyphTrace.Append('|')
                        .Append((int)(band.X + Math.Max(0f, cell0) - min.X));
                }
                _glyphTrace.Append('+')
                    .Append((int)(penX - cellX2))
                    .Append(g != null
                        ? gTexBase == 1 ? 'p' : gTexBase == _secTexBase ? 's' : 'a'
                        : 'x');
                if (g != null)
                    _glyphTrace.Append('y').Append((int)g->OffsetY)
                        .Append('x').Append((int)g->Height)
                        .Append('w').Append((int)g->Width)
                        .Append('o').Append((int)g->OffsetX);
                if (kern != 0)
                    _glyphTrace.Append('k').Append(kern);
                _glyphTrace.Append(',');
            }
            if (g == null || g->Width == 0 || g->Height == 0)
            {
                if (v > 0x7F)
                {
                    // literal « » are gfd icons 54/55 - the same auto-translate
                    // chevrons the game draws, not font glyphs
                    var cp2 = UnpackUtf8Value(v);
                    var gfdId = cp2 == 0xAB ? 54 : cp2 == 0xBB ? 55 : -1;
                    if (gfdId > 0 && penX - cellX2 > 0.5f
                        && TryEmitGfdIcon(gfdId, plate, cellX2, font->Size * gs,
                            band, min, max, ref count, remap: false, maskA, out _))
                        continue;
                    if (TryEmitImGuiGlyph(v, plate, cellX2, penX - cellX2, band, min, max, ref count, maskA))
                        continue;
                    unresolved++;
                }
                if (penX - cellX2 > 0.5f && penX >= min.X && cellX2 <= max.X
                    && band.W >= min.Y && band.Y <= max.Y)
                {
                    _maskTex[count] = -1;
                    _maskScreen[count] = new Vector4(cellX2, band.Y, penX, band.W);
                    _maskUv[count] = new Vector4(0f, 0f, -1f, 0f);
                    _maskChannel[count] = 3f;
                    _maskAlpha[count] = maskA;
                    count++;
                }
                continue;
            }
            var texSlot = gTexBase + g->TextureIndex;
            if (texSlot >= _atlasTextures.Length || _atlasTextures[texSlot] == 0)
                continue;
            var texId = MaskTexIndex(texSlot);
            // ink sits at the pen position - the glyph box starts at the pen
            // with no left bearing (FontChanger confirms), and OffsetX is the
            // advance delta. The quad spans exactly W·gs x H·gs: any padding
            // stretches the atlas mapping and skews internal stems. OffsetY is
            // relative to the line's pen origin - band.Y already is that
            // origin. gs is the real render scale
            var x0 = cellX2;
            var y0 = band.Y + g->OffsetY * gRatio * gs;
            var x1 = x0 + g->Width * gRatio * gs;
            var y1 = y0 + g->Height * gRatio * gs;
            if (x1 <= min.X || x0 >= max.X || y1 <= min.Y || y0 >= max.Y)
                continue;
            _maskTex[count] = texId;
            _maskScreen[count] = new Vector4(x0, y0, x1, y1);
            _maskUv[count] = new Vector4(
                g->X / (float)gFont->TextureWidth, g->Y / (float)gFont->TextureHeight,
                (g->X + g->Width) / (float)gFont->TextureWidth,
                (g->Y + g->Height) / (float)gFont->TextureHeight);
            _maskChannel[count] = g->Channel;
            _maskAlpha[count] = maskA;
            count++;
            if (_plates.DiagPlate == plate && _glyphTrace.Length < 3000)
                _glyphTrace.Append('#').Append(v.ToString("X")).Append(':')
                    .Append((int)y0).Append('-').Append((int)y1).Append(';');
        }
        if (icons > 0)
        {
            _occRejectDebug.Append(plate).Append('i').Append(icons)
                .Append('q').Append(iconGlyphs).Append('r').Append(iconRects).Append(' ');
        }
        if (unresolved > 0)
            _occRejectDebug.Append(plate).Append('u').Append(unresolved).Append(' ');
        return count;
    }

    /// <summary>Every distance-tunable value for one plate - when presets exist
    /// the two nearest to the plate's real yalm distance interpolate and
    /// replace the global sliders entirely. Offsets apply per line: the title
    /// band shifts by <see cref="TitleOffset"/>, the name band by
    /// <see cref="NameOffset"/> - resolved from the name-only pair on plates
    /// without a title line, from the name-line pair otherwise. Falls back to
    /// the globals when no presets exist or the plate's distance is unknown -
    /// the own plate always uses the globals; presets only tune other plates.
    /// </summary>
    private PlateTune ResolvePlateTune(int plate)
    {
        var cfg = _configService.Current;
        var nameOnly = _plates.Title[plate] == null;
        var globalTune = new PlateTune(
            new Vector2(cfg.SupporterNameplateTuneOffX, cfg.SupporterNameplateTuneOffY),
            new Vector2(
                nameOnly ? cfg.SupporterNameplateTuneNameOffX : cfg.SupporterNameplateTuneNameLineOffX,
                nameOnly ? cfg.SupporterNameplateTuneNameOffY : cfg.SupporterNameplateTuneNameLineOffY),
            cfg.SupporterNameplateTunePitchMul,
            cfg.SupporterNameplateTuneAdvanceMul,
            new Vector2(cfg.SupporterNameplateTuneGlyphPadX, cfg.SupporterNameplateTuneGlyphPadY),
            Math.Clamp(cfg.SupporterNameplateIconMaskScalePercent, 25, 200) / 100f,
            Math.Clamp(cfg.SupporterIconSizePercent, 25, 400) / 100f,
            Math.Clamp(cfg.SupporterNameplateGfdMaskScalePercent, 25, 200) / 100f,
            new Vector2(
                Math.Clamp(cfg.SupporterNameplateGfdMaskOffsetX, -32, 32),
                Math.Clamp(cfg.SupporterNameplateGfdMaskOffsetY, -32, 32)));
        var presets = cfg.SupporterNameplateDistancePresets;
        // the own plate is exempt - distance presets only tune other plates
        if (plate == _plates.SelfPlate || presets is not { Count: > 0 })
            return globalTune;
        var dist = _plates.ObjDist[plate];
        var below = -1;
        var above = -1;
        if (float.IsFinite(dist))
        {
            for (var i = 0; i < presets.Count; i++)
            {
                var d = presets[i].Distance;
                if (d <= dist && (below < 0 || d > presets[below].Distance))
                    below = i;
                if (d >= dist && (above < 0 || d < presets[above].Distance))
                    above = i;
            }
        }
        var sel = below >= 0 ? below : above;
        if (sel < 0)
            return globalTune;
        var p = presets[sel];
        return new PlateTune(
            new Vector2(p.TitleOffsetX, p.TitleOffsetY),
            nameOnly
                ? new Vector2(p.NameOnlyOffsetX, p.NameOnlyOffsetY)
                : new Vector2(p.NameOffsetX, p.NameOffsetY),
            p.PitchMul,
            p.AdvanceMul,
            new Vector2(p.EdgeX, p.EdgeY),
            p.IconMaskScale / 100f,
            p.IconSizePercent / 100f,
            p.GfdMaskScale / 100f,
            new Vector2(p.GfdMaskOffsetX, p.GfdMaskOffsetY));
    }

    /// <summary>Edge bleed radius in font units per axis - the tuning sliders
    /// override, otherwise the node's edge flag + edge alpha decide (the game
    /// only draws the outline pass when both are present).</summary>
    private Vector2 EdgeUnits(int plate)
    {
        if (!_configService.Current.SupporterNameplateEdgeMask)
            return Vector2.Zero;
        var edge = ResolvePlateTune(plate).Edge;
        var px = edge.X;
        var py = edge.Y;
        var auto = (_plates.TextFlags[plate] & 0x8) != 0 && _plates.EdgeAlpha[plate] > 0
            ? 1.0f : 0f;
        return new Vector2(px > 0.001f ? px : auto, py > 0.001f ? py : auto);
    }

    /// <summary>Packs a screen-px bleed radius into one float for maskChannel.w
    /// - half-pixel steps, x occupying the high digits (hx*512 + hy).</summary>
    private static float PackEdge(Vector2 units, float scale)
    {
        if (units == Vector2.Zero)
            return 0f;
        var hx = MathF.Min(MathF.Round(units.X * scale * 2f), 120f);
        var hy = MathF.Min(MathF.Round(units.Y * scale * 2f), 120f);
        return hx * 512f + hy;
    }

    /// <summary>Packs the UTF-8 bytes of the character at <paramref name="i"/>
    /// big-endian - the key form the game's glyph map uses.</summary>
    private static uint PackUtf8Value(string text, ref int i)
    {
        var code = char.ConvertToUtf32(text, i);
        if (code > 0xFFFF)
            i++;
        return PackUtf8Value(code);
    }

    /// <summary>Packs a codepoint into the big-endian utf-8 key form the
    /// game's glyph map uses - the lead byte ends up in the highest used
    /// byte position (« is C2 AB -> 0xC2AB).</summary>
    private static uint PackUtf8Value(int code)
    {
        var c = (uint)code;
        return c switch
        {
            < 0x80 => c,
            < 0x800 => 0xC080u | ((c << 2) & 0x1F00u) | (c & 0x3Fu),
            < 0x10000 => 0xE08080u | ((c << 4) & 0x0F0000u) | ((c << 2) & 0x3F00u) | (c & 0x3Fu),
            _ => 0xF0808080u | ((c << 6) & 0x07000000u) | ((c << 4) & 0x3F0000u)
                | ((c << 2) & 0x3F00u) | (c & 0x3Fu),
        };
    }

    /// <summary>One record of gfdata.gfd - Left/Top/Width/Height address the
    /// 512x1024 bitmap font icon sheet the game draws se-string icons from.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GfdEntryLite
    {
        public ushort Id;
        public ushort Left;
        public ushort Top;
        public ushort W;
        public ushort H;
        public ushort Redirect;
    }

    /// <summary>Parses gfdata.gfd once via the data manager and binds the gfd
    /// icon sheet (<c>AtkFontManager.Gfd-&gt;Texture</c>) into a free atlas
    /// slot - the same source the game and Dalamud's se-string renderer draw
    /// icons (the FC « » brackets are gfd icons 54/55) from.</summary>
    private unsafe int EnsureGfd(int baseSlot)
    {
        // the offered slot shifts per plate - the gfd sheet has to be re-bound
        // at whatever slot this plate left free
        _gfdTexSlot = -1;
        if (!_gfdTried)
        {
            _gfdTried = true;
            try
            {
                var data = _dataManager.GetFile("common/font/gfdata.gfd")?.Data;
                if (data is { Length: >= 16 })
                {
                    var count = Math.Min(BitConverter.ToInt32(data, 8), (data.Length - 16) / 16);
                    _gfdEntries = new GfdEntryLite[count];
                    for (var i = 0; i < count; i++)
                    {
                        var o = 16 + (i * 16);
                        _gfdEntries[i] = new GfdEntryLite
                        {
                            Id = BitConverter.ToUInt16(data, o),
                            Left = BitConverter.ToUInt16(data, o + 2),
                            Top = BitConverter.ToUInt16(data, o + 4),
                            W = BitConverter.ToUInt16(data, o + 6),
                            H = BitConverter.ToUInt16(data, o + 8),
                            Redirect = BitConverter.ToUInt16(data, o + 12),
                        };
                    }
                }
                var module0 = RaptureAtkModule.Instance();
                var tex0 = module0 != null && module0->AtkFontManager.Gfd != null
                    ? module0->AtkFontManager.Gfd->Texture : null;
                _gfdSrv = tex0 != null ? (nint)tex0->D3D11ShaderResourceView : 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Nameplate] gfd capture failed");
            }
        }
        if (_gfdSrv != 0 && baseSlot < _atlasTextures.Length)
        {
            _atlasTextures[baseSlot] = _gfdSrv;
            _gfdTexSlot = baseSlot;
        }
        return _gfdTexSlot;
    }

    /// <summary>Looks an icon id up in the parsed gfd table - direct index for
    /// the contiguous ids, binary search otherwise, redirects followed.</summary>
    private bool TryGetGfdEntry(int id, out GfdEntryLite entry)
    {
        entry = default;
        var entries = _gfdEntries;
        for (var depth = 0; depth < 4 && id > 0; depth++)
        {
            if (id <= entries.Length && entries[id - 1].Id == id)
            {
                entry = entries[id - 1];
            }
            else
            {
                var lo = 0;
                var hi = entries.Length - 1;
                var found = -1;
                while (lo <= hi)
                {
                    var m = lo + ((hi - lo) >> 1);
                    if (entries[m].Id < id) lo = m + 1;
                    else if (entries[m].Id > id) hi = m - 1;
                    else { found = m; break; }
                }
                if (found < 0)
                    return false;
                entry = entries[found];
            }
            if (entry.Redirect == 0)
                break;
            id = entry.Redirect;
        }
        return entry.W > 0 && entry.H > 0;
    }

    /// <summary>Maps icon2 macro ids through the game's remap table -
    /// controller button icons depend on the button layout.</summary>
    private static unsafe int RemapIcon2(int iconId)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
            return iconId;
        var table = module->AtkFontManager.Icon2RemapTable;
        if (table == null)
            return iconId;
        for (var i = 0; i < 30; i++)
        {
            if (table[i].IconId == iconId)
                return (int)table[i].RemappedIconId;
        }
        return iconId;
    }

    /// <summary>Emits a gfd icon as a real pixel-shaped mask quad. The text
    /// renderer draws icon payloads at a fixed 20px height for fonts up to
    /// 19px and at the font size above that - switching to the sheet's
    /// high-res half past the threshold - pen-aligned horizontally and
    /// vertically centered on the line, never stretched over the band.</summary>
    private bool TryEmitGfdIcon(int iconId, int plate, float cellX, float fontPx,
        Vector4 band, Vector2 min, Vector2 max, ref int count, bool remap, float alpha,
        out Vector4 emitRect)
    {
        emitRect = Vector4.Zero;
        if (_gfdTexSlot < 0 || count >= GpuNameplateImGuiMask.MaxPlateMasks)
            return false;
        if (remap)
            iconId = RemapIcon2(iconId);
        if (!TryGetGfdEntry(iconId, out var e))
            return false;
        var useHq = fontPx > 19f;
        var iconH = useHq ? fontPx : 20f;
        var iconW = e.W * (iconH / e.H);
        var x0 = cellX;
        var x1 = x0 + iconW;
        var iconY0 = band.Y + MathF.Round((band.W - band.Y - iconH) * 0.5f);
        var iconY1 = iconY0 + iconH;
        var gfd = ResolvePlateTune(plate);
        if (MathF.Abs(gfd.GfdMaskScale - 1f) > 0.001f
            || MathF.Abs(gfd.GfdMaskOffset.X) > 0.001f
            || MathF.Abs(gfd.GfdMaskOffset.Y) > 0.001f)
        {
            var cx = (x0 + x1) * 0.5f + gfd.GfdMaskOffset.X;
            var cy = (iconY0 + iconY1) * 0.5f + gfd.GfdMaskOffset.Y;
            var hw = (x1 - x0) * 0.5f * gfd.GfdMaskScale;
            var hh = (iconY1 - iconY0) * 0.5f * gfd.GfdMaskScale;
            x0 = cx - hw; x1 = cx + hw;
            iconY0 = cy - hh; iconY1 = cy + hh;
        }
        if (x1 <= min.X || x0 >= max.X || iconY1 <= min.Y || iconY0 >= max.Y)
            return true;
        _maskTex[count] = MaskTexIndex(_gfdTexSlot);
        emitRect = new Vector4(x0, iconY0, x1, iconY1);
        _maskScreen[count] = emitRect;
        _maskUv[count] = useHq
            ? new Vector4(e.Left / 256f, (e.Top + 170.5f) / 512f,
                (e.Left + e.W) / 256f, (e.Top + e.H + 170.5f) / 512f)
            : new Vector4(e.Left / 512f, e.Top / 1024f,
                (e.Left + e.W) / 512f, (e.Top + e.H) / 1024f);
        // dump the whole sheet once and the exact sampled region per icon -
        // shows precisely which pixels (and alpha edges) the cutout taps
        var cov = GetCoverageBake();
        if (_configService.Current.SupporterNameplateIconDump && cov.EnsureDevice())
        {
            cov.DumpPage(_gfdSrv);
            cov.DumpRegion(_gfdSrv, _maskUv[count],
                $"gfd-icon-{iconId}{(useHq ? "-hq" : "")}");
        }
        _maskChannel[count] = 3f;
        _maskAlpha[count] = alpha;
        _maskPlateIdx[count] = plate;
        count++;
        return true;
    }

    /// <summary>Emits the plate's image-node icons (duty/party/marker) as
    /// textured masks sampling their own uld part - the coverage bake folds
    /// the icon's real pixels into the plate's slot, replacing the old
    /// node-rect cutout. Plates whose chrome already covers everything keep
    /// the rect; unresolved textures keep the rect too.</summary>
    private int AppendIconNodeMasks(int plate, Vector2 min, Vector2 max, int count)
    {
        if (_plates.TextRect[plate] == Vector4.Zero || _plates.Chrome[plate] == _plates.Rects[plate])
            return count;
        var cov = GetCoverageBake();
        if (!cov.EnsureDevice())
            return count;
        // the reader bakes the global mask scale into captured rects - compose
        // the plate's resolved (possibly preset-driven) scale on top of it
        var globalScale = _plates.IconMaskScale;
        var iconScale = globalScale > 0.001f
            ? ResolvePlateTune(plate).IconMaskScale / globalScale : 1f;
        for (var j = 0; j < IconSlots && count < GpuNameplateImGuiMask.MaxPlateMasks; j++)
        {
            var idx = plate * IconSlots + j;
            var srv = _plates.IconSrv[idx];
            var rect = _plates.IconRects[idx];
            if (MathF.Abs(iconScale - 1f) > 0.001f)
                rect = new Vector4(rect.X, rect.Y,
                    rect.X + (rect.Z - rect.X) * iconScale,
                    rect.Y + (rect.W - rect.Y) * iconScale);
            if (srv == 0 || rect == Vector4.Zero
                || rect.Z <= min.X || rect.X >= max.X
                || rect.W <= min.Y || rect.Y >= max.Y)
                continue;
            var page = cov.GetPage(srv, _plates.IconTexKey[idx]);
            if (page == null || page.Width <= 0 || page.Height <= 0)
            {
                LogIconNode(plate, j, srv, rect, null);
                continue;
            }
            var uv = _plates.IconUv[idx];
            var s = IconSampleScale(uv, page.Width, page.Height, _plates.IconPartCnt[idx]);
            var stats = cov.RegionStatsFor(page,
                MathF.Min(uv.X, uv.Z) * s, MathF.Min(uv.Y, uv.W) * s,
                MathF.Max(uv.X, uv.Z) * s, MathF.Max(uv.Y, uv.W) * s);
            var aMax = stats.AMax;
            var aCnt = stats.Covered;
            var hasCov = aMax >= NameplateCoverageBake.CoverageThreshold;
            LogIconNode(plate, j, srv, rect, page, s, hasCov, aMax, aCnt);
            if (_configService.Current.SupporterNameplateIconDump)
                cov.DumpPage(srv, _plates.IconTexKey[idx]);
            if (!hasCov)
            {
                _occRejectDebug.Append('t').Append(j);
                continue;
            }
            _maskTex[count] = MaskTexIndexOf(srv, _plates.IconTexKey[idx]);
            _maskScreen[count] = rect;
            _maskUv[count] = new Vector4(uv.X * s / page.Width, uv.Y * s / page.Height,
                uv.Z * s / page.Width, uv.W * s / page.Height);
            // ic0 (the big status field) fades by real alpha like the cursor -
            // it keeps its plate index for bookkeeping but bypasses the bake
            var bypass = j == 0;
            _maskChannel[count] = bypass ? 7f : 3f;
            _maskAlpha[count] = _plates.IconAlpha[idx];
            _maskEdge[count] = 0f;
            _maskPlateIdx[count] = plate;
            _maskIcon[count] = (byte)(1 << j);
            _maskBypass[count] = (byte)(bypass ? 1 : 0);
            _occRejectDebug.Append(bypass ? 'L' : 'I').Append(j);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Emits texture masks for HUD units captured above the nameplate layer -
    /// chat windows, menus and dialogs render over our overlay, so their real
    /// image pixels cut our elements wherever they overlap.
    /// </summary>
    private int AppendHudMasks(Vector2 min, Vector2 max, int count)
    {
        if (_plates.HudCount == 0)
            return count;
        var cov = GetCoverageBake();
        if (!cov.EnsureDevice())
            return count;
        int j;
        for (j = 0; j < _plates.HudCount && count < GpuNameplateImGuiMask.MaxPlateMasks; j++)
        {
            var srv = _plates.HudSrv[j];
            var rect = _plates.HudRects[j];
            if (rect == Vector4.Zero
                || rect.Z <= min.X || rect.X >= max.X
                || rect.W <= min.Y || rect.Y >= max.Y)
                continue;
            if (srv <= 0)
            {
                // negative srv marks a collision-node occluder - solid cut,
                // no texture to sample. Zero marks the node's resource being
                // present while its kernel texture still streams - the same
                // conservative cover; a node drops only when its pixels are
                // neither cached nor still pending
                if (srv < 0)
                    EmitHudSolidMask(count, rect, _plates.HudAlpha[j], 'h', j);
                else if (cov.HasPage(_plates.HudTexKey[j]))
                    EmitHudSolidMask(count, rect, _plates.HudAlpha[j], 'c', j);
                else if (cov.PagePending(0, _plates.HudTexKey[j]))
                    EmitHudSolidMask(count, rect, _plates.HudAlpha[j], 'p', j);
                else
                {
                    _occRejectDebug.Append('x').Append(j);
                    continue;
                }
                count++;
                continue;
            }
            var page = cov.GetPage(srv, _plates.HudTexKey[j]);
            if (page == null || page.Width <= 0 || page.Height <= 0)
            {
                // a live-SRV node always covers: 'p' while its pixels are
                // still streaming in, 'f' once the readback failed out - a
                // drawn window part that cannot be sampled still occludes,
                // so dropping the mask would let overlays shine through
                EmitHudSolidMask(count, rect, _plates.HudAlpha[j],
                    cov.PagePending(srv, _plates.HudTexKey[j]) ? 'p' : 'f', j);
                count++;
                continue;
            }
            if (!page.Settled)
            {
                // the readback cached a page whose pixels are unproven - a
                // mid-upload snapshot's still-empty regions would quietly
                // release this mask, so hold the solid cover until the
                // confirm read settles it
                EmitHudSolidMask(count, rect, _plates.HudAlpha[j], 'u', j);
                count++;
                continue;
            }
            var uv = _plates.HudUv[j];
            var s = IconSampleScale(uv, page.Width, page.Height, _plates.HudPartCnt[j]);
            var stats = cov.RegionStatsFor(page,
                MathF.Min(uv.X, uv.Z) * s, MathF.Min(uv.Y, uv.W) * s,
                MathF.Max(uv.X, uv.Z) * s, MathF.Max(uv.Y, uv.W) * s);
            var aMax = stats.AMax;
            var aCnt = stats.Covered;
            var aSum = stats.AlphaSum;
            if (aMax < NameplateCoverageBake.CoverageThreshold)
            {
                _occRejectDebug.Append('a').Append(j);
                continue;
            }
            _maskTex[count] = MaskTexIndexOf(srv, _plates.HudTexKey[j]);
            _maskScreen[count] = rect;
            _maskUv[count] = new Vector4(uv.X * s / page.Width, uv.Y * s / page.Height,
                uv.Z * s / page.Width, uv.W * s / page.Height);
            if (_configService.Current.SupporterNameplateIconDump
                && cov.DumpRegion(srv, _maskUv[count], $"hud{j}-{srv:X}", _plates.HudTexKey[j]))
            {
                var texels = Math.Max(1,
                    (int)((MathF.Abs(uv.Z - uv.X) * s) * (MathF.Abs(uv.W - uv.Y) * s)));
                _logger.LogDebug(
                    "[Nameplate] hudmask {j} srv={srv:X} part({u0:0},{v0:0}>{u1:0},{v1:0}) pc{pc} pg{pw}x{ph} amax{aMax} acov{aCnt} mean{mean:0.0}",
                    j, srv, uv.X * s, uv.Y * s, uv.Z * s, uv.W * s,
                    _plates.HudPartCnt[j], page.Width, page.Height,
                    aMax, aCnt, aSum / (double)texels);
                cov.DumpPage(srv, _plates.HudTexKey[j]);
            }
            _maskChannel[count] = 11f; // alpha channel + hard-edge flag
            _maskAlpha[count] = _plates.HudAlpha[j];
            // .w packs three values: the region's max alpha (fraction), the
            // bitmask of sides it ramps on (1|2|4|8 l/r/t/b, *2), and how
            // far in the silhouette sits (*32, screen px). Inside every
            // ramped side's silhouette the shader floors the cut at the
            // plateau so the overlay dims uniformly behind the frame band
            var edgeSide = cov.RegionEdgeSide(page,
                MathF.Min(uv.X, uv.Z) * s, MathF.Min(uv.Y, uv.W) * s,
                MathF.Max(uv.X, uv.Z) * s, MathF.Max(uv.Y, uv.W) * s,
                out var rimTexels);
            var rimPx = 0f;
            if ((edgeSide & 3) != 0)
                rimPx = rimTexels * (rect.Z - rect.X) / MathF.Abs(uv.Z - uv.X) / s;
            else if ((edgeSide & 12) != 0)
                rimPx = rimTexels * (rect.W - rect.Y) / MathF.Abs(uv.W - uv.Y) / s;
            _maskEdge[count] = aMax / 255f + edgeSide * 2f + MathF.Round(rimPx) * 32f;
            _maskPlateIdx[count] = -1;
            _maskIcon[count] = 0;
            _maskBypass[count] = 1;
            _occRejectDebug.Append('h').Append(j);
            count++;
        }
        if (j < _plates.HudCount)
        {
            // the mask array ran full - HUD occluders past the cap drop
            // silently otherwise, which reads as an intermittent window edge
            _occRejectDebug.Append('!').Append(_plates.HudCount - j);
            if (Environment.TickCount64 - _maskCapLoggedAt > 60000)
            {
                _maskCapLoggedAt = Environment.TickCount64;
                _logger.LogDebug(
                    "[Nameplate] mask slots full - {left} HUD occluders dropped",
                    _plates.HudCount - j);
            }
        }
        return count;
    }

    /// <summary>Emits a conservative full-rect mask for a HUD node whose real
    /// pixels are not proven yet - collision occluders, pending resources and
    /// unsettled pages all cover until evidence says otherwise. The marker
    /// distinguishes the reason in the debug string.</summary>
    private void EmitHudSolidMask(int count, Vector4 rect, float alpha, char marker, int j)
    {
        _maskTex[count] = -1;
        _maskScreen[count] = rect;
        _maskUv[count] = new Vector4(0f, 0f, -1f, 0f);
        _maskChannel[count] = 3f;
        _maskAlpha[count] = alpha;
        _maskEdge[count] = 0f;
        _maskPlateIdx[count] = -1;
        _maskIcon[count] = 0;
        _maskBypass[count] = 1;
        _occRejectDebug.Append(marker).Append(j);
    }

    /// <summary>
    /// Emits texture masks for the TargetCursor addon's image nodes - the big
    /// arrow hovers over every plate, so it joins the occluder set with the
    /// sentinel plate index (-1): no plate bookkeeping, just real arrow pixels
    /// cutting whatever we draw underneath.
    /// </summary>
    private int AppendCursorMasks(Vector2 min, Vector2 max, int count)
    {
        if (_plates.CursorCount == 0)
            return count;
        var cov = GetCoverageBake();
        if (!cov.EnsureDevice())
            return count;
        int j;
        for (j = 0; j < _plates.CursorCount && count < GpuNameplateImGuiMask.MaxPlateMasks; j++)
        {
            var srv = _plates.CursorSrv[j];
            var rect = _plates.CursorRects[j];
            if (srv == 0 || rect == Vector4.Zero
                || rect.Z <= min.X || rect.X >= max.X
                || rect.W <= min.Y || rect.Y >= max.Y)
            {
                _occRejectDebug.Append('c').Append(j).Append('r');
                continue;
            }
            var page = cov.GetPage(srv, _plates.CursorTexKey[j]);
            if (page == null || page.Width <= 0 || page.Height <= 0)
            {
                _occRejectDebug.Append('c').Append(j).Append('p');
                continue;
            }
            var uv = _plates.CursorUv[j];
            var s = IconSampleScale(uv, page.Width, page.Height, _plates.CursorPartCnt[j]);
            var aMax = cov.RegionStatsFor(page,
                MathF.Min(uv.X, uv.Z) * s, MathF.Min(uv.Y, uv.W) * s,
                MathF.Max(uv.X, uv.Z) * s, MathF.Max(uv.Y, uv.W) * s).AMax;
            if (aMax < NameplateCoverageBake.CoverageThreshold)
            {
                _occRejectDebug.Append('c').Append(j).Append('a');
                continue;
            }
            _maskTex[count] = MaskTexIndexOf(srv, _plates.CursorTexKey[j]);
            _maskScreen[count] = rect;
            _maskUv[count] = new Vector4(uv.X * s / page.Width, uv.Y * s / page.Height,
                uv.Z * s / page.Width, uv.W * s / page.Height);
            _maskChannel[count] = 7f; // alpha channel + linear-cut flag
            _maskAlpha[count] = _plates.CursorAlpha[j];
            _maskEdge[count] = 0f;
            _maskPlateIdx[count] = -1;
            _maskIcon[count] = 0;
            _occRejectDebug.Append('C').Append(j);
            count++;
        }
        if (j < _plates.CursorCount)
            _occRejectDebug.Append('!').Append(_plates.CursorCount - j);
        return count;
    }

    /// <summary>The texel scale for one icon part on its page - the global
    /// uld scale first, then whole-asset parts (icon files via
    /// LoadIconTexture) snap to whichever scale lands the part's far edge on
    /// the page edge, since authored and loaded resolutions can differ.</summary>
    private static float IconSampleScale(Vector4 uv, int pageW, int pageH, int partCnt)
    {
        var px = MathF.Min(uv.X, uv.Z);
        var py = MathF.Min(uv.Y, uv.W);
        var pw = MathF.Max(uv.X, uv.Z) - px;
        var ph = MathF.Max(uv.Y, uv.W) - py;
        if (pw <= 0f || ph <= 0f)
            return 1f;
        // a one-part list means the part IS the whole authored sheet - the
        // loaded texture maps exactly onto it, no snapping needed
        if (partCnt == 1 && px <= 0.5f && py <= 0.5f)
        {
            var sx = pageW / pw;
            var sy = pageH / ph;
            if (MathF.Abs(sx - sy) <= sx * 0.05f)
                return sx;
        }
        var s = IconTextureScale();
        if (uv.Z * s > pageW + 0.5f || uv.W * s > pageH + 0.5f)
            s = 1f;
        if (px <= 0.5f && py <= 0.5f)
        {
            for (var c = 4f; c >= 0.5f; c *= 0.5f)
            {
                if (MathF.Abs(pw * c - pageW) < 1.5f
                    && MathF.Abs(ph * c - pageH) < 1.5f)
                {
                    s = c;
                    break;
                }
            }
        }
        return s;
    }

    /// <summary>Draws every captured icon slot's sampled texture region right
    /// over its screen rect - the mask's own view of the icon, so a wrong
    /// region or texture is visible instead of guessed.</summary>
    private void DrawIconTextureDebug()
    {
        var cov = _coverageBake;
        if (cov == null || !cov.EnsureDevice())
            return;
        var draw = ImGui.GetForegroundDrawList();
        for (var p = 0; p < MaxNamePlateObjects; p++)
        {
            for (var j = 0; j < IconSlots; j++)
            {
                var idx = p * IconSlots + j;
                var srv = _plates.IconSrv[idx];
                var rect = _plates.IconRects[idx];
                if (srv == 0 || rect == Vector4.Zero)
                    continue;
                var min = new Vector2(rect.X, rect.Y);
                var max = new Vector2(rect.Z, rect.W);
                var uv = _plates.IconUv[idx];
                var page = cov.GetPage(srv, _plates.IconTexKey[idx]);
                var uvMin = Vector2.Zero;
                var uvMax = Vector2.One;
                var emits = false;
                if (page != null && page.Width > 0 && page.Height > 0)
                {
                    var s = IconSampleScale(uv, page.Width, page.Height, _plates.IconPartCnt[idx]);
                    uvMin = new Vector2(MathF.Min(uv.X, uv.Z) * s / page.Width,
                        MathF.Min(uv.Y, uv.W) * s / page.Height);
                    uvMax = new Vector2(MathF.Max(uv.X, uv.Z) * s / page.Width,
                        MathF.Max(uv.Y, uv.W) * s / page.Height);
                    emits = cov.RegionStatsFor(page,
                            MathF.Min(uv.X, uv.Z) * s, MathF.Min(uv.Y, uv.W) * s,
                            MathF.Max(uv.X, uv.Z) * s, MathF.Max(uv.Y, uv.W) * s)
                        .AMax >= NameplateCoverageBake.CoverageThreshold;
                }
                // dimmed = captured but skipped for transparency - no mask,
                // so what shows here is only the indicator, never a cutout
                var boxCol = emits ? 0xFF00FFFF : 0x66FFFFFF;
                var imgCol = emits ? 0x88FF00FF : 0x33FFFFFF;
                draw.AddRect(min - new Vector2(1f), max + new Vector2(1f), boxCol, 0f, ImDrawFlags.None, 1f);
                draw.AddText(min + new Vector2(0f, -13f), boxCol, $"i{j}");
                draw.AddImage(new ImTextureID(srv), min, max, uvMin, uvMax, imgCol);
            }
        }
    }

    /// <summary>Draws each captured HUD occluder's sampled texture over its
    /// emitted mask rect - the mask's own view of the window chrome, so the
    /// nine-grid edge mapping can be eyeballed directly against the painted
    /// window on screen.</summary>
    // one hue per game depth layer (bits 0-12), white for the docked/hosted
    // linked-unit group - the quad color identifies which layer emitted it
    private static readonly uint[] HudLayerColors = BuildHudLayerColors();

    private static uint[] BuildHudLayerColors()
    {
        var colors = new uint[14];
        for (var i = 0; i < 13; i++)
        {
            var h = i / 13f;
            var f = h * 6f;
            var sec = (int)f % 6;
            var c = f - sec;
            var (r, g, b) = sec switch
            {
                0 => (1f, c, 0f),
                1 => (1f - c, 1f, 0f),
                2 => (0f, 1f, c),
                3 => (0f, 1f - c, 1f),
                4 => (c, 0f, 1f),
                _ => (1f, 0f, 1f - c),
            };
            colors[i] = 0xFF000000u | ((uint)(b * 255f) << 16)
                | ((uint)(g * 255f) << 8) | (uint)(r * 255f);
        }
        colors[13] = 0xFFFFFFFFu;
        return colors;
    }

    private void DrawHudTextureDebug()
    {
        var cov = _coverageBake;
        if (cov == null || !cov.EnsureDevice())
            return;
        var draw = ImGui.GetForegroundDrawList();
        for (var j = 0; j < _plates.HudCount; j++)
        {
            var srv = _plates.HudSrv[j];
            var rect = _plates.HudRects[j];
            if (rect == Vector4.Zero)
                continue;
            var min = new Vector2(rect.X, rect.Y);
            var max = new Vector2(rect.Z, rect.W);
            var uvMin = Vector2.Zero;
            var uvMax = Vector2.One;
            var emits = false;
            var solid = srv <= 0;
            var uv = _plates.HudUv[j];
            var page = solid ? null : cov.GetPage(srv, _plates.HudTexKey[j]);
            if (page != null && page.Width > 0 && page.Height > 0)
            {
                var s = IconSampleScale(uv, page.Width, page.Height, _plates.HudPartCnt[j]);
                uvMin = new Vector2(MathF.Min(uv.X, uv.Z) * s / page.Width,
                    MathF.Min(uv.Y, uv.W) * s / page.Height);
                uvMax = new Vector2(MathF.Max(uv.X, uv.Z) * s / page.Width,
                    MathF.Max(uv.Y, uv.W) * s / page.Height);
                emits = cov.RegionStatsFor(page,
                        MathF.Min(uv.X, uv.Z) * s, MathF.Min(uv.Y, uv.W) * s,
                        MathF.Max(uv.X, uv.Z) * s, MathF.Max(uv.Y, uv.W) * s)
                    .AMax >= NameplateCoverageBake.CoverageThreshold;
            }
            var layer = Math.Min(_plates.HudLayer[j], (byte)(HudLayerColors.Length - 1));
            var lc = HudLayerColors[layer];
            if (solid)
            {
                // solid collision occluders emit a rect cut with a 3px inner
                // feather and were previously invisible here - draw them so a
                // soft edge can be attributed to the mask that produces it
                var sc = (lc & 0x00FFFFFFu) | 0xDD000000u;
                draw.AddRect(min - new Vector2(1f), max + new Vector2(1f), sc, 0f, ImDrawFlags.None, 2f);
                draw.AddText(min + new Vector2(0f, -13f), sc,
                    $"h{j} {(layer >= 13 ? "lD" : $"l{layer + 1}")} p{_plates.HudPartIdx[j]} S");
                continue;
            }
            var boxCol = emits ? lc : (lc & 0x00FFFFFFu) | 0x66000000u;
            var imgCol = emits ? 0x88FFFFFF : 0x33FFFFFF;
            draw.AddRect(min - new Vector2(1f), max + new Vector2(1f), boxCol, 0f, ImDrawFlags.None, 1f);
            // 'D' marks masks rescued by the linked/docked pass - their units
            // paint inside a host, so a stale-positioned dock node is the
            // first suspect when an area cuts without drawing pixels
            draw.AddText(min + new Vector2(0f, -13f), boxCol,
                $"h{j} {(layer >= 13 ? "lD" : $"l{layer + 1}")} p{_plates.HudPartIdx[j]}");
            draw.AddImage(new ImTextureID(srv), min, max, uvMin, uvMax, imgCol);
        }
    }

    /// <summary>The scale between uld part texels and the loaded sheet -
    /// the game loads _hr1 textures at 2x while part rects stay authored
    /// against the base resolution.</summary>
    private static unsafe float IconTextureScale()
    {
        var rapture = RaptureAtkModule.Instance();
        if (rapture == null)
            return 1f;
        var s = rapture->AtkModule.AtkTextureResourceManager.DefaultTextureScale;
        return s is >= 1 and <= 4 ? s : 1f;
    }

    /// <summary>Logs an icon node's resolved texture region once per change -
    /// the part rect vs texture size shows when the sampled pixels can't be
    /// the drawn icon (wrong asset, hd scale, stale uv).</summary>
    private void LogIconNode(int plate, int slot, nint srv, Vector4 rect, NameplateCoverageBake.Page? page, float s = 1f, bool cov = true, byte aMax = 0, int aCnt = 0)
    {
        if (!_configService.Current.SupporterNameplateDebugLog)
            return;
        var idx = plate * IconSlots + slot;
        var uv = _plates.IconUv[idx];
        var sig = HashCode.Combine(srv, rect, uv, page?.Width ?? 0, page?.Height ?? 0, cov);
        if (_plates.IconSig[idx] == sig)
            return;
        _plates.IconSig[idx] = sig;
        var df = (int)_plates.GameRect[plate].W;
        _logger.LogDebug(
            "[Nameplate] icon{slot} p{plate} srv {srv} uv({u0:0.#},{v0:0.#})-({u1:0.#},{v1:0.#}) wm{wrap} s{s:0.#} ap{alt} df{df:X} tex {tw}x{th} rect({rx:0},{ry:0},{rz:0},{rw:0}) a{alpha:0.##} cov{cov} am{amax} ac{acnt}",
            slot, plate, srv.ToString("X"), uv.X, uv.Y, uv.Z, uv.W,
            _plates.IconWrap[idx], s, _plates.AlternatePartId, df,
            page?.Width ?? 0, page?.Height ?? 0,
            rect.X, rect.Y, rect.Z, rect.W, _plates.IconAlpha[idx], cov ? 1 : 0, aMax, aCnt);
    }

    /// <summary>Logs what an inline icon payload in a plate's text turned into
    /// - which sheet region it samples and where the quad lands - once per
    /// icon/outcome change so toggling statuses shows each variant exactly
    /// once. Fires per plate even when positions move.</summary>
    private void LogInlineIcon(int plate, int v, int iconId, float cellX, float penX,
        Vector4 band, string outcome, Vector4 emit)
    {
        if (!_configService.Current.SupporterNameplateDebugLog)
            return;
        var sig = HashCode.Combine(plate, v, iconId, outcome);
        if (!_inlineIconSigs.Add(sig))
            return;
        _logger.LogDebug(
            "[Nameplate] nameicon p{plate} v{v:X} id{id} cell({c0:0.#}-{c1:0.#}) band({by:0.#}-{bw:0.#}) -> {outcome} emit({ex:0.#},{ey:0.#},{ez:0.#},{ew:0.#})",
            plate, v, iconId, cellX, penX, band.Y, band.W,
            outcome, emit.X, emit.Y, emit.Z, emit.W);
    }

    /// <summary>Resolves an <see cref="_atlasTextures"/> slot to a stable
    /// per-region texture id. Slots get rebound as each plate captures its own
    /// font pages, so a glyph mask emitted for an earlier plate would sample a
    /// stranger's atlas - the same SRV always maps to the same id here.</summary>
    private int MaskTexIndex(int texSlot)
    {
        var srv = texSlot >= 0 && texSlot < _atlasTextures.Length
            ? _atlasTextures[texSlot] : 0;
        return MaskTexIndexOf(srv);
    }

    private int MaskTexIndexOf(nint srv, uint texKey = 0)
    {
        if (srv == 0)
            return -1;
        for (var t = 0; t < _maskTexSrvCount; t++)
        {
            if (_maskTexSrvs[t] == srv)
                return t;
        }
        if (_maskTexSrvCount >= _maskTexSrvs.Length)
            return int.MaxValue;
        // borrowed game SRV - keep the COM resource alive until the registry
        // resets next frame, otherwise a released texture leaves a dangling
        // pointer bound in the mask shader (driver-level access violation)
        SrvAddRef(srv);
        _maskTexSrvs[_maskTexSrvCount] = srv;
        _maskTexKeys[_maskTexSrvCount] = texKey;
        return _maskTexSrvCount++;
    }

    private void ReleaseMaskTexSrvs()
    {
        for (var t = 0; t < _maskTexSrvCount; t++)
        {
            var srv = _maskTexSrvs[t];
            _maskTexSrvs[t] = 0;
            _maskTexKeys[t] = 0;
            if (srv != 0)
                SrvRelease(srv);
        }
        _maskTexSrvCount = 0;
    }

    private static unsafe void SrvAddRef(nint srv)
    {
        var vt = *(nint*)srv;
        ((delegate* unmanaged[Stdcall]<nint, uint>)((nint*)vt)[1])(srv);
    }

    private static unsafe void SrvRelease(nint srv)
    {
        var vt = *(nint*)srv;
        ((delegate* unmanaged[Stdcall]<nint, uint>)((nint*)vt)[2])(srv);
    }

    /// <summary>Keeps glyphs of the two most common atlas textures - the
    /// shader binds both, so fallback-font glyphs keep their real shape.</summary>
    private int FilterDominantTexture(int count)
    {
        if (count == 0)
            return 0;
        // cursor, bypass-icon and coverage masks pin their textures - the
        // arrow's, the status icon's and the reconstructed-glyph coverage
        // pixels must never degrade to a rect; slots 2..7 are theirs
        Span<int> pins = stackalloc int[6];
        var pinCount = 0;
        for (var i = 0; i < count; i++)
        {
            var t = _maskTex[i];
            // unregistered indices (table full -> int.MaxValue) are not
            // pinnable - _maskTexSrvs[t] would index past the array
            if (t < 0 || t >= _maskTexSrvCount
                || (_maskPlateIdx[i] >= 0 && _maskBypass[i] == 0))
                continue;
            var dup = false;
            for (var n = 0; n < pinCount; n++)
            {
                if (pins[n] == t)
                {
                    dup = true;
                    break;
                }
            }
            if (!dup && pinCount < pins.Length)
                pins[pinCount++] = t;
        }
        Span<int> counts = stackalloc int[_maskTexSrvs.Length];
        var best = -1;
        var second = -1;
        for (var i = 0; i < count; i++)
        {
            var tex = _maskTex[i];
            if (tex < 0 || tex >= _maskTexSrvCount)
                continue;
            var isPin = false;
            for (var n = 0; n < pinCount; n++)
            {
                if (pins[n] == tex)
                {
                    isPin = true;
                    break;
                }
            }
            if (!isPin)
                counts[tex]++;
        }
        for (var t = 0; t < _maskTexSrvCount; t++)
        {
            if (counts[t] <= 0)
                continue;
            if (best < 0 || counts[t] > counts[best])
            {
                second = best;
                best = t;
            }
            else if (second < 0 || counts[t] > counts[second])
            {
                second = t;
            }
        }
        _maskTexture = best >= 0 ? _maskTexSrvs[best] : 0;
        _mask2Texture = second >= 0 ? _maskTexSrvs[second] : 0;
        _mask3Texture = pinCount > 0 ? _maskTexSrvs[pins[0]] : 0;
        _mask4Texture = pinCount > 1 ? _maskTexSrvs[pins[1]] : 0;
        _mask5Texture = pinCount > 2 ? _maskTexSrvs[pins[2]] : 0;
        _mask6Texture = pinCount > 3 ? _maskTexSrvs[pins[3]] : 0;
        _mask7Texture = pinCount > 4 ? _maskTexSrvs[pins[4]] : 0;
        _mask8Texture = pinCount > 5 ? _maskTexSrvs[pins[5]] : 0;
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var tex = _maskTex[i];
            var sel = -1f;
            for (var n = 0; n < pinCount; n++)
            {
                if (pins[n] == tex)
                {
                    sel = 2f + n;
                    break;
                }
            }
            if (sel < 0f && tex == best) sel = 0f;
            if (sel < 0f && tex == second) sel = 1f;
            if (tex != -1 && sel < 0f)
            {
                // its atlas won neither bound slot - keep the plate's rect
                // as a solid mask rather than dropping its occlusion
                _maskScreen[kept] = _maskScreen[i];
                _maskUv[kept] = new Vector4(0f, 0f, -1f, 0f);
                _maskPlateIdx[kept] = _maskPlateIdx[i];
                _maskTexSel[kept] = 0f;
                _maskAlpha[kept] = _maskAlpha[i];
                _maskEdge[kept] = 0f;
                _maskIcon[kept] = _maskIcon[i];
                kept++;
                continue;
            }
            _maskScreen[kept] = _maskScreen[i];
            _maskUv[kept] = _maskUv[i];
            _maskPlateIdx[kept] = _maskPlateIdx[i];
            _maskTexSel[kept] = tex == -1 ? 0f : sel;
            _maskAlpha[kept] = _maskAlpha[i];
            _maskEdge[kept] = _maskEdge[i];
            _maskIcon[kept] = _maskIcon[i];
            kept++;
        }
        return kept;
    }

    /// <summary>Replaces every reconstructed textured mask with one baked
    /// coverage mask per plate - the plate's glyph bitmaps are re-rendered
    /// into a shared R8 texture (pen placement unchanged, coverage exact),
    /// so the shader samples the bake like it samples the game's own bake.
    /// Plates served by the game's bake texture stay untouched.</summary>
    /// <summary>Lazily creates the coverage bake and points its PNG dumps at a
    /// folder inside the plugin config directory - dev builds need the files
    /// somewhere findable, not buried in %TEMP%.</summary>
    private NameplateCoverageBake GetCoverageBake()
    {
        var cov = _coverageBake ??= new NameplateCoverageBake(_logger);
        cov.DumpDirectory ??= Path.Combine(
            _pluginInterface.ConfigDirectory.FullName, "nameplate-icons");
        cov.CacheDirectory ??= Path.Combine(
            _pluginInterface.ConfigDirectory.FullName, "nameplate-cov");
        return cov;
    }

    private int BakeCoverageMasks(int count)
    {
        var cov = GetCoverageBake();
        if (!cov.EnsureDevice() || cov.Srv == 0)
            return count;
        var covTex = MaskTexIndexOf(cov.Srv);
        if (covTex < 0 || covTex == int.MaxValue)
            return count;
        // a recreated bake texture invalidates every cached content hash
        if (_covBakeSrv != cov.Srv)
        {
            _covBakeSrv = cov.Srv;
            Array.Clear(_covBaked, 0, _covBaked.Length);
            Array.Clear(_covHash, 0, _covHash.Length);
        }
        Array.Clear(_covSeen);
        for (var k = 0; k < count; k++)
        {
            var tex = _maskTex[k];
            if (tex < 0 || tex >= _maskTexSrvCount || _maskTexSrvs[tex] == _plates.BakeSrv)
                continue;
            var p = _maskPlateIdx[k];
            if (p < 0 || _maskBypass[k] != 0)
                continue;
            _plates.IconLive[p] |= _maskIcon[k];
            var s = _maskScreen[k];
            _covRect[p] = _covSeen[p]
                ? new Vector4(MathF.Min(_covRect[p].X, s.X), MathF.Min(_covRect[p].Y, s.Y),
                    MathF.Max(_covRect[p].Z, s.Z), MathF.Max(_covRect[p].W, s.W))
                : s;
            _covSeen[p] = true;
        }
        _covEmitScreen.Clear();
        _covEmitUv.Clear();
        _covEmitPlate.Clear();
        _covEmitAlpha.Clear();
        _covEmitIcon.Clear();
        for (var p = 0; p < MaxNamePlateObjects; p++)
        {
            if (!_covSeen[p])
                continue;
            var rect = _covRect[p];
            var w = rect.Z - rect.X;
            var h = rect.W - rect.Y;
            if (w <= 0f || h <= 0f)
            {
                _covSeen[p] = false;
                continue;
            }
            var cs = MathF.Min(1f,
                MathF.Min(NameplateCoverageBake.SlotWidth / w,
                    NameplateCoverageBake.SlotHeight / h));
            var cw = Math.Min((int)MathF.Ceiling(w * cs), NameplateCoverageBake.SlotWidth);
            var ch = Math.Min((int)MathF.Ceiling(h * cs), NameplateCoverageBake.SlotHeight);
            // position-independent signature of what would be blitted - a plate
            // sliding across the screen shifts every dst coord uniformly, so
            // the baked bitmap stays identical and can be reused untouched
            var hash = 17;
            unchecked
            {
                hash = hash * 31 + (int)(w * 8f + 0.5f);
                hash = hash * 31 + (int)(h * 8f + 0.5f);
                hash = hash * 31 + (int)(_plateEdgePx[p] * 64f + 0.5f);
                for (var k = 0; k < count; k++)
                {
                    var tex = _maskTex[k];
                    if (_maskPlateIdx[k] != p || _maskBypass[k] != 0 || tex < 0
                        || tex >= _maskTexSrvCount || _maskTexSrvs[tex] == _plates.BakeSrv)
                        continue;
                    var s = _maskScreen[k];
                    var u = _maskUv[k];
                    hash = hash * 31 + tex;
                    hash = hash * 31 + (int)(u.X * 1024f);
                    hash = hash * 31 + (int)(u.Y * 1024f);
                    hash = hash * 31 + (int)(u.Z * 1024f);
                    hash = hash * 31 + (int)(u.W * 1024f);
                    hash = hash * 31 + (int)_maskChannel[k];
                    hash = hash * 31 + (int)(_maskAlpha[k] * 255f + 0.5f);
                    hash = hash * 31 + (int)((s.X - rect.X) * cs * 8f + 0.5f);
                    hash = hash * 31 + (int)((s.Y - rect.Y) * cs * 8f + 0.5f);
                    hash = hash * 31 + (int)((s.Z - rect.X) * cs * 8f + 0.5f);
                    hash = hash * 31 + (int)((s.W - rect.Y) * cs * 8f + 0.5f);
                }
            }
            if (!(_covBaked[p] && _covHash[p] == hash))
            {
                cov.BeginSlot(p);
                var ok = true;
                for (var k = 0; k < count && ok; k++)
                {
                    var tex = _maskTex[k];
                    if (_maskPlateIdx[k] != p || _maskBypass[k] != 0 || tex < 0
                        || tex >= _maskTexSrvCount || _maskTexSrvs[tex] == _plates.BakeSrv)
                        continue;
                    var page = cov.GetPage(_maskTexSrvs[tex], _maskTexKeys[tex]);
                    if (page == null)
                    {
                        ok = false;
                        break;
                    }
                    var s = _maskScreen[k];
                    var u = _maskUv[k];
                    cov.Blit(p, page, (int)_maskChannel[k] & 3, _maskAlpha[k],
                        u.X * page.Width, u.Y * page.Height,
                        u.Z * page.Width, u.W * page.Height,
                        (s.X - rect.X) * cs, (s.Y - rect.Y) * cs,
                        (s.Z - rect.X) * cs, (s.W - rect.Y) * cs);
                }
                if (!ok)
                {
                    _covSeen[p] = false;
                    _covBaked[p] = false;
                    // bake failed - collapse the plate's raw masks into one
                    // ink-block solid so ~40 quads cannot starve the mask budget
                    // when several front plates stack up
                    var collapsed = false;
                    for (var k = 0; k < count; k++)
                    {
                        if (_maskPlateIdx[k] != p || _maskBypass[k] != 0)
                            continue;
                        var t = _maskTex[k];
                        if (t < 0 || t >= _maskTexSrvCount || _maskTexSrvs[t] == _plates.BakeSrv)
                            continue;
                        if (collapsed)
                        {
                            _maskTex[k] = -9;
                            continue;
                        }
                        _maskScreen[k] = _plates.InkRect[p];
                        _maskUv[k] = new Vector4(0f, 0f, -1f, 0f);
                        _maskTex[k] = -1;
                        _maskChannel[k] = 3f;
                        _maskAlpha[k] = _plates.NodeAlpha[p] / 255f;
                        _maskEdge[k] = 0f;
                        collapsed = true;
                    }
                    continue;
                }
                var packed = _plateEdgePx[p];
                var ex = (int)(MathF.Floor(packed / 512f) * 0.5f * cs + 0.5f);
                var ey = (int)((packed - MathF.Floor(packed / 512f) * 512f) * 0.5f * cs + 0.5f);
                cov.Dilate(p, ex, ey);
                _covBaked[p] = true;
                _covHash[p] = hash;
            }
            _covEmitScreen.Add(new Vector4(rect.X, rect.Y,
                rect.X + cw / cs, rect.Y + ch / cs));
            _covEmitUv.Add(NameplateCoverageBake.SlotUv(p, cw, ch));
            _covEmitPlate.Add(p);
            // per-element alpha is already folded into the coverage bytes
            _covEmitAlpha.Add(1f);
            _covEmitIcon.Add(_plates.IconLive[p]);
            for (var k = 0; k < count; k++)
            {
                var tex = _maskTex[k];
                if (_maskPlateIdx[k] == p && _maskBypass[k] == 0 && tex >= 0
                    && tex < _maskTexSrvCount && _maskTexSrvs[tex] != _plates.BakeSrv)
                    _maskTex[k] = -9;
            }
        }
        var write = 0;
        for (var k = 0; k < count; k++)
        {
            if (_maskTex[k] == -9)
                continue;
            _maskTex[write] = _maskTex[k];
            _maskScreen[write] = _maskScreen[k];
            _maskUv[write] = _maskUv[k];
            _maskChannel[write] = _maskChannel[k];
            _maskAlpha[write] = _maskAlpha[k];
            _maskEdge[write] = _maskEdge[k];
            _maskPlateIdx[write] = _maskPlateIdx[k];
            _maskIcon[write] = _maskIcon[k];
            _maskBypass[write] = _maskBypass[k];
            write++;
        }
        for (var k = 0; k < _covEmitScreen.Count && write < GpuNameplateImGuiMask.MaxPlateMasks; k++)
        {
            _maskTex[write] = covTex;
            _maskScreen[write] = _covEmitScreen[k];
            _maskUv[write] = _covEmitUv[k];
            _maskChannel[write] = 0f;
            _maskAlpha[write] = _covEmitAlpha[k];
            _maskEdge[write] = 0f;
            _maskPlateIdx[write] = _covEmitPlate[k];
            _maskIcon[write] = _covEmitIcon[k];
            // baked coverage carries the plate's merged glyph pixels - it must
            // pin its texture like cursor/bypass masks instead of fighting
            // the two dominant slots and degrading to a solid block
            _maskBypass[write] = 1;
            write++;
        }
        cov.Flush();
        _occRejectDebug.Append("cov").Append(_covEmitScreen.Count).Append(' ');
        return write;
    }

    private unsafe int AppendPlateGlyphs(int plate, Vector2 min, Vector2 max, int count, Vector4 ink)
    {
        var before = count;
        var name = _plates.NameRect[plate] != Vector4.Zero ? _plates.Name[plate] : null;
        var title = _plates.TitleRect[plate] != Vector4.Zero ? _plates.Title[plate] : null;
        if (name == null && title == null)
        {
            _glyphFail = 'n';
            return count;
        }
        var fitText = name ?? title!;
        var fitRect = name != null ? _plates.NameRect[plate] : _plates.TitleRect[plate];

        IFontHandle? best = null;
        var bestErr = float.MaxValue;
        foreach (var handle in _fontHandles)
        {
            if (!handle.Available)
                continue;
            using (handle.Push())
            {
                var font = ImGui.GetFont();
                var err = MathF.Abs(MeasureLine(font, fitText) * (fitRect.W - fitRect.Y) / font.FontSize
                    - (fitRect.Z - fitRect.X));
                if (err < bestErr)
                {
                    bestErr = err;
                    best = handle;
                }
            }
        }
        if (best == null || !best.Available)
        {
            _glyphFail = 'f';
            return count;
        }
        _glyphNulls = 0;
        _glyphBounds = 0;
        _glyphTex = 0;
        using (best.Push())
        {
            var font = ImGui.GetFont();
            if (font.Handle->IndexLookup.Size == 0)
                font.Handle->BuildLookupTable();
            CaptureAtlasTextures(font);
            var nodeA = _plates.NodeAlpha[plate] / 255f;
            var fallbackRect = name != null ? _plates.NameRect[plate] : _plates.TitleRect[plate];
            var fbScale = Math.Max(fallbackRect.W - fallbackRect.Y, 1f)
                / Math.Max(font.FontSize, 1f);
            var efb = EdgeUnits(plate);
            _plateEdgePx[plate] = PackEdge(efb, fbScale);
            if (name != null)
                count = AppendLineGlyphs(font, name, _plates.NameRect[plate], min, max, count, nodeA);
            if (title != null)
                count = AppendLineGlyphs(font, title, _plates.TitleRect[plate], min, max, count, nodeA);
        }
        if (count > before)
        {
            var uMin = new Vector2(float.MaxValue);
            var uMax = new Vector2(float.MinValue);
            for (var q = before; q < count; q++)
            {
                var quad = _maskScreen[q];
                uMin = Vector2.Min(uMin, new Vector2(quad.X, quad.Y));
                uMax = Vector2.Max(uMax, new Vector2(quad.Z, quad.W));
            }
            const float tol = 24f;
            if (uMin.X < ink.X - tol || uMin.Y < ink.Y - tol
                || uMax.X > ink.Z + tol || uMax.Y > ink.W + tol)
            {
                _glyphFail = 'v';
                return before;
            }
        }
        return count;
    }

    /// <summary>Lays <paramref name="text"/> centered inside its rendered node
    /// rect and emits one screen+UV mask quad per glyph overlapping the icon.</summary>
    private unsafe int AppendLineGlyphs(ImFontPtr font, string text, Vector4 rect,
        Vector2 min, Vector2 max, int count, float alpha)
    {
        var width = MeasureLine(font, text);
        var rectH = rect.W - rect.Y;
        if (width < 1f || rectH < 1f)
        {
            _glyphFail = 'w';
            return count;
        }
        var sy = rectH / font.FontSize;
        var sx = Math.Clamp((rect.Z - rect.X) / (width * sy), 0.5f, 1.5f) * sy;
        var penX = (rect.X + rect.Z) * 0.5f - width * sx * 0.5f;
        var baseline = (rect.Y + rect.W) * 0.5f + (font.Ascent + font.Descent) * 0.5f * sy;
        var bandTop = baseline - font.Ascent * sy;
        var bandBottom = baseline - font.Descent * sy;
        _glyphDbgRect = rect;
        _glyphDbgSpan = new Vector2(penX, penX + width * sx);
        _glyphDbgBaseline = baseline;
        var before = count;
        var nullGlyphs = 0;
        var droppedBounds = 0;
        var droppedTex = 0;
        const float pad = 8f;
        for (var c = 0; c < text.Length && count < GpuNameplateImGuiMask.MaxPlateMasks; c++)
        {
            var ch = (ushort)text[c];
            var glyph = (ImGuiHelpers.ImFontGlyphReal*)font.FindGlyph(ch);
            var advance = glyph != null ? glyph->AdvanceX : font.GetCharAdvance(ch);
            var cellX0 = penX;
            penX += advance * sx;
            if (glyph == null || !glyph->Visible || glyph->TextureIndex < 0
                || glyph->TextureIndex + 1 >= _atlasTextures.Length
                || _atlasTextures[glyph->TextureIndex + 1] == 0)
            {
                if (glyph == null || !glyph->Visible)
                    nullGlyphs++;
                else
                    droppedTex++;
                if (penX - cellX0 > 0.5f && penX >= min.X - pad && cellX0 <= max.X + pad
                    && bandBottom >= min.Y - pad && bandTop <= max.Y + pad)
                {
                    _maskTex[count] = -1;
                    _maskScreen[count] = new Vector4(cellX0, bandTop, penX, bandBottom);
                    _maskUv[count] = new Vector4(0f, 0f, -1f, 0f);
                    _maskChannel[count] = 3f;
                    _maskAlpha[count] = alpha;
                    count++;
                }
                continue;
            }
            var x0 = cellX0 + glyph->X0 * sx;
            var y0 = baseline + glyph->Y0 * sy;
            var x1 = cellX0 + glyph->X1 * sx;
            var y1 = baseline + glyph->Y1 * sy;
            if (x1 <= x0 || y1 <= y0 || x1 < min.X - pad || x0 > max.X + pad
                || y1 < min.Y - pad || y0 > max.Y + pad)
            {
                droppedBounds++;
                continue;
            }
            _maskTex[count] = MaskTexIndex(glyph->TextureIndex + 1);
            _maskScreen[count] = new Vector4(x0, y0, x1, y1);
            _maskUv[count] = new Vector4(glyph->U0, glyph->V0, glyph->U1, glyph->V1);
            _maskChannel[count] = 3f;
            _maskAlpha[count] = alpha;
            count++;
        }
        _glyphNulls += nullGlyphs;
        _glyphBounds += droppedBounds;
        _glyphTex += droppedTex;
        if (count != before)
            return count;
        if (rect.Z >= min.X && rect.X <= max.X && rect.W >= min.Y && rect.Y <= max.Y
            && count < GpuNameplateImGuiMask.MaxPlateMasks)
        {
            _maskTex[count] = -1;
            _maskScreen[count] = rect;
            _maskUv[count] = new Vector4(0f, 0f, -1f, 0f);
            _maskChannel[count] = 3f;
            _maskAlpha[count] = alpha;
            return count + 1;
        }
        _glyphFail = droppedTex > 0 ? 'T' : 'x';
        return count;
    }

    private unsafe void CaptureAtlasTextures(ImFontPtr font)
    {
        var n = CaptureAtlasTexturesAt(font, 1);
        for (var i = n + 1; i < _atlasTextures.Length; i++)
            _atlasTextures[i] = 0;
    }

    /// <summary>Binds the ImGui atlas pages into texture slots starting at
    /// <paramref name="baseSlot"/> - glyph TextureIndex maps to baseSlot +
    /// index.</summary>
    private unsafe int CaptureAtlasTexturesAt(ImFontPtr font, int baseSlot)
    {
        var atlas = font.ContainerAtlas;
        var textures = atlas.Textures;
        var n = Math.Min(textures.Size, _atlasTextures.Length - baseSlot);
        if (n < 0)
            n = 0;
        var ready = 0;
        for (var i = 0; i < n; i++)
        {
            _atlasTextures[baseSlot + i] = (nint)textures.Ref<ImFontAtlasTexture>(i).TexID.Handle;
            if (_atlasTextures[baseSlot + i] != 0)
                ready++;
        }
        return ready;
    }

    /// <summary>Emits an unresolved non-ascii char as a real glyph quad sampled
    /// from the ImGui font atlas - the same font stack the supporter preview
    /// uses, which carries « » and accents the game fonts lack.</summary>
    private unsafe bool TryEmitImGuiGlyph(int v, int plate, float cellX, float cellW, Vector4 band,
        Vector2 min, Vector2 max, ref int count, float alpha)
    {
        var imf = _imguiFont;
        if (imf == null || count >= GpuNameplateImGuiMask.MaxPlateMasks)
            return false;
        var cp = UnpackUtf8Value(v);
        if (cp is < 0 or > 0xFFFF)
            return false;
        var ig = (ImGuiHelpers.ImFontGlyphReal*)imf->FindGlyph((ushort)cp);
        if ((ig == null || !ig->Visible)
            && cp is 0xAB or 0xBB)
        {
            // literal « » are drawn as the se-icon auto-translate glyphs
            ig = (ImGuiHelpers.ImFontGlyphReal*)imf->FindGlyph(
                (ushort)(cp == 0xAB ? 0xE040 : 0xE041));
        }
        if (ig == null || !ig->Visible)
            return false;
        var texSlot = _imguiTexBase + ig->TextureIndex;
        if (texSlot >= _atlasTextures.Length || _atlasTextures[texSlot] == 0)
            return false;
        var bandH = band.W - band.Y;
        var isy = bandH / _imguiFontSize;
        var baseline = (band.Y + band.W) * 0.5f + (imf->Ascent + imf->Descent) * 0.5f * isy;
        var gw = (ig->X1 - ig->X0) * isy;
        var x0 = cellX + (cellW - gw) * 0.5f;
        var y0 = baseline + ig->Y0 * isy;
        var x1 = x0 + gw;
        var y1 = baseline + ig->Y1 * isy;
        if (x1 <= min.X || x0 >= max.X || y1 <= min.Y || y0 >= max.Y)
            return true;
        _maskTex[count] = MaskTexIndex(texSlot);
        _maskScreen[count] = new Vector4(x0, y0, x1, y1);
        _maskUv[count] = new Vector4(ig->U0, ig->V0, ig->U1, ig->V1);
        _maskChannel[count] = 3f;
        _maskAlpha[count] = alpha;
        _maskPlateIdx[count] = plate;
        count++;
        return true;
    }

    private static float MeasureLine(ImFontPtr font, string text)
    {
        var width = 0f;
        for (var c = 0; c < text.Length; c++)
            width += font.GetCharAdvance((ushort)text[c]);
        return width;
    }

    private const int MaxPlateClipRects = 24;

    private static void SubtractRect(List<Vector4> rects, Vector4 cut)
    {
        for (var i = rects.Count - 1; i >= 0; i--)
        {
            var r = rects[i];
            if (cut.Z <= r.X || cut.X >= r.Z || cut.W <= r.Y || cut.Y >= r.W)
                continue;
            rects.RemoveAt(i);
            if (cut.Y > r.Y)
                rects.Add(new Vector4(r.X, r.Y, r.Z, MathF.Min(cut.Y, r.W)));
            if (cut.W < r.W)
                rects.Add(new Vector4(r.X, MathF.Max(cut.W, r.Y), r.Z, r.W));
            var midY0 = MathF.Max(r.Y, cut.Y);
            var midY1 = MathF.Min(r.W, cut.W);
            if (midY0 >= midY1)
                continue;
            if (cut.X > r.X)
                rects.Add(new Vector4(r.X, midY0, MathF.Min(cut.X, r.Z), midY1));
            if (cut.Z < r.Z)
                rects.Add(new Vector4(MathF.Max(cut.Z, r.X), midY0, r.Z, midY1));
        }
    }

    /// <summary>
    /// Issues this frame's depth readback and caches the inverse view-projection
    /// matrix used to unproject sampled depth texels back to world space.
    /// Returns true when a usable depth frame exists - cells then use
    /// <see cref="DepthOccluded"/> instead of the raycast chain.
    /// </summary>
    private unsafe bool PrepareDepthFrame(IReadOnlyList<IconDrawPosition> positions)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (var i = 0; i < positions.Count; i++)
        {
            var a = _plates.BlockAnchor(positions[i].PlateIndex) + positions[i].RelAnchor;
            x0 = MathF.Min(x0, a.X - DepthRectPadLeft);
            y0 = MathF.Min(y0, a.Y - DepthRectPadY);
            x1 = MathF.Max(x1, a.X + DepthRectPadRight);
            y1 = MathF.Max(y1, a.Y + DepthRectPadY);
        }
        _depthSampler.BeginFrame(x0, y0, x1, y1);
        if (!_depthSampler.IsActive)
            return false;
        var camera = CameraManager.Instance()->GetActiveCamera();
        var renderCam = camera != null ? camera->SceneCamera.RenderCamera : null;
        if (renderCam == null)
        {
            _depthStatus = "rc";
            return false;
        }
        var cp = camera->SceneCamera.Position;
        _depthCamPos = new Vector3(cp.X, cp.Y, cp.Z);
        var centerRay = camera->SceneCamera.ScreenPointToRay(ImGui.GetIO().DisplaySize * 0.5f);
        var fwd = new Vector3(centerRay.Direction.X, centerRay.Direction.Y, centerRay.Direction.Z);
        if (fwd.LengthSquared() < 1e-6f)
        {
            _depthStatus = "fwd";
            return false;
        }
        _depthCamForward = Vector3.Normalize(fwd);
        _depthNear = renderCam->NearPlane;
        _depthFar = renderCam->FarPlane;
        _depthStandardZ = renderCam->StandardZ;
        if (!_depthSampler.HasSceneDepth(_depthStandardZ))
        {
            _depthStatus = "clear";
            return false;
        }
        _depthStatus = null;
        return true;
    }

    /// <summary>Analytic inverse of the scene depth value. Both standard
    /// (near→0) and reversed (near→1) Z map to viewZ; an (effectively)
    /// infinite far plane degenerates to the near/d form.</summary>
    private float DepthToViewZ(float d)
    {
        var n = MathF.Max(_depthNear, 0.001f);
        var f = _depthFar;
        if (_depthStandardZ)
        {
            if (f > 1e5f)
                return n / MathF.Max(1f - d, 1e-6f);
            return n * f / MathF.Max(f - d * (f - n), 1e-6f);
        }
        if (f > 1e5f)
            return n / MathF.Max(d, 1e-6f);
        return n * f / MathF.Max(d * (f - n) + n, 1e-6f);
    }

    /// <summary>
    /// Compare scene and icon depth at the center of a small screen-space cell.
    /// A missing depth texel uses the legacy collision probe instead.
    /// </summary>
    private bool DepthCellOccluded(float iconViewZ, Vector3 origin, Vector3 cellWorld,
        Vector2 center, IconDrawPosition entry, out string info)
    {
        if (_depthSampler.TrySampleDepth(center.X, center.Y, out var raw) && float.IsFinite(raw))
        {
            var hitViewZ = DepthToViewZ(raw);
            info = $"z h={hitViewZ:F1}/i={iconViewZ:F1}";
            return hitViewZ < iconViewZ - DepthTolerance;
        }
        var occluded = ProbeOccluded(origin, cellWorld, out _, out info);
        if (!occluded)
        {
            occluded = IsIconCoveredByObject(origin, center, iconViewZ, entry.Owner, out info, out var anyPick);
            if (!occluded && !anyPick)
                occluded = IsOccludedByCharacter(origin, cellWorld, entry.Owner, out info);
        }
        return occluded;
    }

    private unsafe ulong ComputeOcclusionCells(IconDrawPosition entry, Vector2 rectMin, Vector2 rectMax,
        bool useDepth, out int cols, out int rows)
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
        cols = OccGridCols;
        rows = OccGridRows;
        if (camera == null)
        {
            _occDebug = "noCam";
            return 0;
        }
        var camPos = camera->SceneCamera.Position;
        var origin = new Vector3(camPos.X, camPos.Y, camPos.Z);
        var target = entry.TargetWorldPos;
        if (useDepth)
        {
            cols = Math.Min(((int)MathF.Ceiling((rectMax.X - rectMin.X) / DepthCellPx)) | 1, DepthGridMaxCols);
            rows = Math.Min(((int)MathF.Ceiling((rectMax.Y - rectMin.Y) / DepthCellPx)) | 1, DepthGridMaxRows);
        }
        var cellW = (rectMax.X - rectMin.X) / cols;
        var cellH = (rectMax.Y - rectMin.Y) / rows;
        var mask = 0UL;
        var cell = 0;
        var firstInfo = string.Empty;
        var depthProbe = string.Empty;
        if (useDepth)
        {
            var probePoint = (rectMin + rectMax) * 0.5f;
            if (_depthSampler.TrySampleDepth(probePoint.X, probePoint.Y, out var raw))
            {
                var probeWorld = GetIconWorldPos(origin, target, probePoint);
                var iconZ = probeWorld.HasValue ? Vector3.Dot(probeWorld.Value - _depthCamPos, _depthCamForward) : float.NaN;
                var hitZ = DepthToViewZ(raw);
                depthProbe = $" r={raw:F5} h={hitZ:F1}/i={iconZ:F1} centerOcc={hitZ < iconZ - DepthTolerance} fmt={_depthSampler.Format} b={_depthSampler.BufW}x{_depthSampler.BufH}";
            }
            else
            {
                depthProbe = $" sample:outside fmt={_depthSampler.Format} b={_depthSampler.BufW}x{_depthSampler.BufH}";
            }
        }
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++, cell++)
            {
                var cellMin = new Vector2(rectMin.X + cellW * col, rectMin.Y + cellH * row);
                var center = new Vector2(cellMin.X + cellW * 0.5f, cellMin.Y + cellH * 0.5f);
                var world = GetIconWorldPos(origin, target, center);
                if (!world.HasValue)
                    continue;
                string info;
                bool occluded;
                if (useDepth)
                {
                    var iconViewZ = Vector3.Dot(world.Value - _depthCamPos, _depthCamForward);
                    occluded = DepthCellOccluded(iconViewZ, origin, world.Value, center, entry, out info);
                    if (firstInfo.Length == 0)
                        firstInfo = info;
                }
                else
                {
                    occluded = ProbeOccluded(origin, world.Value, out _, out info);
                    if (!occluded)
                    {
                        occluded = IsIconCoveredByObject(origin, center, (world.Value - origin).Length(),
                            entry.Owner, out info, out var anyPick);
                        if (!occluded && !anyPick)
                            occluded = IsOccludedByCharacter(origin, world.Value, entry.Owner, out info);
                    }
                }
                if (!occluded)
                    continue;
                mask |= 1UL << cell;
                if (firstInfo.Length == 0)
                    firstInfo = info;
            }
        }
        _occDebug = $"cells={Convert.ToString((long)mask, 2).PadLeft(cols * rows, '0')} {cols}x{rows} {firstInfo}"
            + (useDepth ? depthProbe : $" d:{_depthSampler.Status ?? _depthStatus ?? "off"}");
        return mask;
    }

    private Vector3? GetIconWorldPos(Vector3 origin, Vector3 plateWorld, Vector2 iconCenterScreen)
    {
        if (!_gameGui.WorldToScreen(plateWorld, out var plateScreen))
            return null;
        var forward = Vector3.Normalize(plateWorld - origin);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        if (!_gameGui.WorldToScreen(plateWorld + right, out var offsetScreen))
            return null;
        var pxPerMeter = offsetScreen.X - plateScreen.X;
        if (MathF.Abs(pxPerMeter) < 1f)
            return null;
        var up = Vector3.Cross(right, forward);
        var metersX = (iconCenterScreen.X - plateScreen.X) / pxPerMeter;
        var metersY = (iconCenterScreen.Y - plateScreen.Y) / pxPerMeter;
        return plateWorld + right * metersX - up * metersY;
    }

    private static unsafe bool ProbeOccluded(Vector3 origin, Vector3 target, out string debug, out string hitInfo)
    {
        hitInfo = string.Empty;
        var toTarget = target - origin;
        var distance = toTarget.Length();
        if (distance < 0.01f)
        {
            debug = "close";
            return false;
        }
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        var module = framework != null ? framework->BGCollisionModule : null;
        if (module == null)
        {
            debug = "noModule";
            return false;
        }
        var direction = toTarget / distance;
        var flags = stackalloc int[] { -1, -1, 0, 0 };
        var skipped = 0;
        var rayOrigin = origin;
        var remaining = distance;
        while (true)
        {
            RaycastHit hit;
            if (!module->RaycastMaterialFilter(&hit, &rayOrigin, &direction, remaining, -1, flags))
            {
                debug = $"miss d={distance:F1}{(skipped > 0 ? $" skip{skipped}" : string.Empty)}";
                return false;
            }
            var colType = hit.Object != null ? hit.Object->GetColliderType() : (ColliderType)0;
            var layer = hit.Object != null ? hit.Object->LayerMask : 0UL;
            var layoutId = hit.Object != null ? hit.Object->LayoutObjectId : 0UL;
            hitInfo = $"t{(int)colType} l{layer:X} m{hit.Material:X} id{layoutId:X8}";
            if (IsExcludedCollider(colType) && skipped < 4)
            {
                skipped++;
                rayOrigin = hit.Point + direction * 0.05f;
                remaining = distance - (rayOrigin - origin).Length();
                if (remaining > 0.01f)
                    continue;
                debug = $"miss d={distance:F1} skip{skipped}";
                return false;
            }
            var travelled = (hit.Point - origin).Length();
            var occluded = travelled < distance - OcclusionProbeMargin;
            debug = $"{(occluded ? "OCC" : "pass")} h={travelled:F1}/d={distance:F1} {hitInfo}{(skipped > 0 ? $" skip{skipped}" : string.Empty)}";
            return occluded;
        }
    }

    private static bool IsExcludedCollider(ColliderType type)
        => type is ColliderType.Box or ColliderType.Cylinder or ColliderType.Sphere
            or ColliderType.Plane or ColliderType.PlaneTwoSided;

    private static unsafe bool IsIconCoveredByObject(Vector3 origin, Vector2 iconCenterScreen, float iconDist, nint owner, out string hitInfo, out bool hitAnything)
    {
        hitInfo = string.Empty;
        hitAnything = false;
        var ts = TargetSystem.Instance();
        if (ts == null)
            return false;
        var obj = ts->GetMouseOverObject((int)iconCenterScreen.X, (int)iconCenterScreen.Y);
        if (obj == null)
            return false;
        hitAnything = true;
        if ((nint)obj == owner)
            return false;
        var objDist = (new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z) - origin).Length();
        var inFront = objDist < iconDist - 0.3f;
        hitInfo = $" pick:{obj->NameString} d{objDist:F1}/i{iconDist:F1}{(inFront ? string.Empty : " bg")}";
        return inFront;
    }

    private unsafe bool IsOccludedByCharacter(Vector3 origin, Vector3 target, nint owner, out string hitInfo)
    {
        hitInfo = string.Empty;
        var segment = target - origin;
        var segLen = segment.Length();
        if (segLen < 0.01f)
            return false;
        var dir = segment / segLen;
        for (var i = 0; i < _objectTable.Length; i++)
        {
            var obj = _objectTable[i];
            if (obj == null || obj.Address == owner || obj.IsDead)
                continue;
            if (obj.ObjectKind is not (ObjectKind.Pc or ObjectKind.BattleNpc
                    or ObjectKind.EventNpc or ObjectKind.Companion or ObjectKind.Retainer
                    or ObjectKind.Mount))
                continue;
            var radius = obj.HitboxRadius;
            if (radius < 0.05f)
                continue;
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            var height = go != null && go->Height > 0.1f ? go->Height : 1.8f;
            var pos = obj.Position;
            var t = Vector3.Dot(pos - origin, dir);
            if (t < -radius || t > segLen + radius)
                continue;
            var closest = origin + dir * Math.Clamp(t, 0, segLen);
            var dx = closest.X - pos.X;
            var dz = closest.Z - pos.Z;
            if (dx * dx + dz * dz > radius * radius)
                continue;
            if (closest.Y < pos.Y - 0.3f || closest.Y > pos.Y + height + 0.5f)
                continue;
            hitInfo = $" char:{obj.Name.TextValue} r{radius:F1} h{height:F1}";
            return true;
        }
        return false;
    }

    // development aid: verifies the game-anchor text rects against the real
    // plate text - white = text block, green = name line, magenta = title line
    private void DrawPlateDebugOverlay()
    {
        var draw = ImGui.GetForegroundDrawList();
        var cfg = _configService.Current;
        var showBlock = cfg.SupporterNameplateDebugBlock;
        var showAnchors = cfg.SupporterNameplateDebugAnchors;
        var showLabels = cfg.SupporterNameplateDebugLabels;
        var showBands = cfg.SupporterNameplateDebugBands;
        var showQuads = cfg.SupporterNameplateDebugQuads;
        for (var i = 0; i < MaxNamePlateObjects; i++)
        {
            if (!_plates.HasRect[i])
                continue;
            // bands and masks live in render space (ScreenX at update time,
            // same as the icon/test-rect anchor); node-array geometry is the
            // newer transform space - shift it for comparison only
            var off = _plates.RenderOff[i];
            var sh = new Vector4(off.X, off.Y, off.X, off.Y);
            var node = _plates.InkRect[i];
            if (node != Vector4.Zero)
            {
                if (showBlock)
                {
                    // horizontal edge rulers extending past the plate so the
                    // block bounds stay readable even on tiny far-away plates:
                    // cyan = our mask block (top/bottom), white = block rect
                    var xl = node.X - 60f;
                    var xr = node.Z + 60f;
                    draw.AddLine(new Vector2(xl, node.Y), new Vector2(xr, node.Y), 0xFFFFFF00, 2f);
                    draw.AddLine(new Vector2(xl, node.W), new Vector2(xr, node.W), 0xFFFFFF00, 2f);
                    draw.AddRect(new Vector2(node.X, node.Y), new Vector2(node.Z, node.W), 0xAAFFFFFF, 0f, ImDrawFlags.None, 1f);
                }
                // the TextDrawArea PrepareNode returned this frame - the rect
                // the game's text layout actually draws into; its bottom edge
                // is the candidate anchor the eff.Y capture is compared to
                var ar = _plates.AreaRect[i];
                if (showBlock && ar != Vector4.Zero
                    && _plates.AreaFrame[i] >= _plates.DrawFrame - 1)
                {
                    draw.AddLine(new Vector2(ar.X - 60f, ar.W), new Vector2(ar.Z + 60f, ar.W),
                        0xFF0080FF, 2f);
                    draw.AddRect(new Vector2(ar.X, ar.Y), new Vector2(ar.Z, ar.W),
                        0x880080FF, 0f, ImDrawFlags.None, 1f);
                }
                // alternative anchor from the resnode transform - drawn
                // shifted into render space; coincidence with the white rect
                // shows the transform lags the visible plate by `off`
                var nr = _plates.NodeRect[i];
                var mm = _plates.Measured[i];
                if (showBlock && nr != Vector4.Zero && mm.X > 0 && mm.Y > 0)
                {
                    var alt = AlignToNode(nr, mm.X, mm.Y, _plates.AlignByte[i] & 0xF) - sh;
                    draw.AddRect(new Vector2(alt.X, alt.Y), new Vector2(alt.Z, alt.W),
                        0xFFFFFF00, 0f, ImDrawFlags.None, 1.5f);
                    var ax = (alt.X + alt.Z) * 0.5f;
                    draw.AddRectFilled(new Vector2(ax - 2f, alt.W - 2f),
                        new Vector2(ax + 2f, alt.W + 2f), 0xFFFFFF00);
                }
                var game = _plates.GameRect[i];
                if (showAnchors && game != Vector4.Zero)
                {
                    draw.AddRectFilled(new Vector2(game.X - 3f, game.Y - 3f),
                        new Vector2(game.X + 3f, game.Y + 3f), 0xFF0000FF);
                    var sbp = _plates.SBlock[i];
                    var gx = sbp != Vector2.Zero ? sbp.X : game.X - off.X;
                    var gy = sbp != Vector2.Zero ? sbp.Y : game.Y - off.Y;
                    draw.AddRectFilled(new Vector2(gx - 2f, gy - 2f),
                        new Vector2(gx + 2f, gy + 2f), 0xFF00FF00);
                }
                var bk = _plates.BakeDbg[i];
                if (showBlock && _plates.IsBaked[i] && game != Vector4.Zero && bk.Z > 0)
                {
                    var bs = Math.Clamp(game.Z / 100f, 0.05f, 4f) * 0.5f;
                    var bax = _plates.EffAnchor[i] != Vector2.Zero ? _plates.EffAnchor[i].X : game.X - off.X;
                    var bx0 = bax - bk.Z * bs * 0.5f;
                    var by0 = node.Y - _plates.BakeTy[i] * bs;
                    draw.AddRect(new Vector2(bx0, by0), new Vector2(bx0 + bk.Z * bs, by0 + bk.W * bs),
                        0xFF00A5FF, 0f, ImDrawFlags.None, 1.5f);
                }
                var nm = _plates.NameRect[i];
                if (showLabels)
                    draw.AddText(new Vector2(node.X, node.Y - 11f), 0xFFFFFFFF,
                        $"{i} n({_plates.NodePos[i].X:0},{_plates.NodePos[i].Y:0}|{_plates.NodeScale[i].X:0.00})"
                        + $" d({_plates.GameRect[i].X:0},{_plates.GameRect[i].Y:0}|{_plates.GameRect[i].Z:0}|f{(int)_plates.GameRect[i].W:X})"
                        + $" td({_plates.Measured[i].X}x{_plates.Measured[i].Y}|a{_plates.AlignByte[i]:X})"
                        + $" bk({_plates.BakeDbg[i].X:0},{_plates.BakeDbg[i].Y:0},{_plates.BakeDbg[i].Z:0}x{_plates.BakeDbg[i].W:0}|ty{_plates.BakeTy[i]:0}|{_plates.IsBaked[i]})"
                        + $" nr({nm.X:0},{nm.Y:0},{nm.Z - nm.X:0}x{nm.W - nm.Y:0})");
            }
            var name = _plates.NameRect[i];
            if (showBands && name != Vector4.Zero)
            {
                draw.AddRectFilled(new Vector2(name.X, name.Y), new Vector2(name.Z, name.W), 0x4400FF00);
                draw.AddRect(new Vector2(name.X, name.Y), new Vector2(name.Z, name.W), 0xFF00FF00, 0f, ImDrawFlags.None, 2f);
                draw.AddText(new Vector2(name.X + 2f, name.Y + 1f), 0xFFFFFFFF, "NAME");
            }
            var title = _plates.TitleRect[i];
            if (showBands && title != Vector4.Zero)
            {
                draw.AddRectFilled(new Vector2(title.X, title.Y), new Vector2(title.Z, title.W), 0x44FF00FF);
                draw.AddRect(new Vector2(title.X, title.Y), new Vector2(title.Z, title.W), 0xFFFF00FF, 0f, ImDrawFlags.None, 2f);
                draw.AddText(new Vector2(title.X + 2f, title.Y + 1f), 0xFFFFFFFF, "TITLE");
            }
        }
        // the actual glyph mask quads emitted for the last icon - these are
        // the pixels that cut the icon, drawn so placement can be verified
        // directly against the native letters
        if (showQuads)
        {
            for (var m = 0; m < _maskDebugCount; m++)
            {
                var q = _maskDebugQuads[m];
                var ch = _maskDebugChan[m];
                // color the quad by its cut mode: solid feather (u.z<0) is
                // red, linear alpha (ch7) yellow, HUD hard-edge (ch>=8) cyan,
                // glyph smoothstep (ch0-3) blue - the label prints the raw
                // channel so a soft fade can be attributed to its emitter
                var col = _maskDebugUv[m].Z < 0f ? 0xFF0000D9u
                    : ch >= 8f ? 0xFF00D9D9u
                    : ch >= 4f ? 0xFF00D9FFu
                    : 0xFF0000FFu;
                draw.AddRect(new Vector2(q.X, q.Y), new Vector2(q.Z, q.W), col, 0f, ImDrawFlags.None, 1f);
                draw.AddText(new Vector2(q.X, q.Y + 1f), col, $"c{ch:0}");
            }
            // preview: run the full glyph reconstruction for every plate even
            // when no icon sits below it - each quad lands on the real letters
            // on screen so cells, metrics and scaling can be checked per glyph
            _plates.DiagPlate = -1;
            _fontDiagSet = false;
            _glyphTrace.Clear();
            var pc = 0;
            for (var i = 0; i < MaxNamePlateObjects && pc < GpuNameplateImGuiMask.MaxPlateMasks; i++)
            {
                if (!_plates.HasRect[i])
                    continue;
                var blk = _plates.InkRect[i];
                if (blk == Vector4.Zero)
                    continue;
                var pMin = new Vector2(blk.X - 8f, blk.Y - 8f);
                var pMax = new Vector2(blk.Z + 8f, blk.W + 8f);
                pc = AppendGameGlyphs(i, pMin, pMax, pc);
            }
            for (var m = 0; m < pc; m++)
            {
                var q = _maskScreen[m];
                draw.AddRect(new Vector2(q.X, q.Y), new Vector2(q.Z, q.W), 0xFF00D9FF, 0f, ImDrawFlags.None, 1f);
            }
        }
        if (cfg.SupporterNameplateDebugHudQuads)
            DrawHudTextureDebug();
        if (cfg.SupporterNameplateDebugInfoWindow)
            DrawPlateInfoWindow(draw);
    }

    // live readout pinned to the top-left: the values behind every plate's
    // layout so tuning slider effects can be read directly on screen
    private void DrawPlateInfoWindow(ImDrawListPtr draw)
    {
        const float rowH = 14f;
        const float x0 = 24f;
        var y = 24f;
        var cfg = _configService.Current;
        draw.AddRectFilled(new Vector2(x0 - 6f, y - 4f), new Vector2(x0 + 660f, y + rowH * 12f),
            0xC0000000);
        draw.AddText(new Vector2(x0, y), 0xFFFFFF00,
            $"tune toff({cfg.SupporterNameplateTuneOffX:0.#},{cfg.SupporterNameplateTuneOffY:0.#})"
            + $" nloff({cfg.SupporterNameplateTuneNameLineOffX:0.#},{cfg.SupporterNameplateTuneNameLineOffY:0.#})"
            + $" sonoff({cfg.SupporterNameplateTuneNameOffX:0.#},{cfg.SupporterNameplateTuneNameOffY:0.#})"
            + $" pitchX{cfg.SupporterNameplateTunePitchMul:0.00}"
            + $" advX{cfg.SupporterNameplateTuneAdvanceMul:0.000}"
            + $" gpad({cfg.SupporterNameplateTuneGlyphPadX:0.#},{cfg.SupporterNameplateTuneGlyphPadY:0.#})"
            + $" beta{cfg.SupporterNameplateAnchorExtrapolate:0.00}");
        y += rowH;
        var rows = 0;
        for (var i = 0; i < MaxNamePlateObjects && rows < 10; i++)
        {
            if (!_plates.HasRect[i])
                continue;
            var g = _plates.DbgGeom[i];
            var name = _plates.NameRect[i];
            var title = _plates.TitleRect[i];
            var area = _plates.AreaRect[i];
            var bk = _plates.BakeDbg[i];
            var rt = ResolvePlateTune(i);
            draw.AddText(new Vector2(x0, y), 0xFFFFFFFF,
                $"p{i} o{(int)_plates.Order[i]}{(_plates.IsTarget[i] ? "tg" : "")}"
                + $" tr{(_plates.TextRect[i] != Vector4.Zero ? 1 : 0)}"
                + $" n{(_plates.Name[i] != null ? 1 : 0)} t{(_plates.Title[i] != null ? 1 : 0)}"
                + $" cr{(_plates.Chrome[i] == _plates.Rects[i] && _plates.Rects[i] != Vector4.Zero ? 1 : 0)}"
                + $" ls{_plates.LineSpacing[i]} ns{_plates.NodeScale[i].X:0.000}/{_plates.NodeScale[i].Y:0.000}"
                + $" tf{_plates.TextFlags[i]:X} ea{_plates.EdgeAlpha[i]} bga{_plates.BgAlpha[i]}"
                + $" tw{_plates.TextW[i]}x{_plates.TextH[i]} ixy{_plates.IconXAdj[i]}/{_plates.IconYAdj[i]}"
                + $" bk{bk.Z:0}x{bk.W:0}/a{_plates.BakeAlpha[i]}/i{(_plates.IsBaked[i] ? 1 : 0)}{(_plates.DrawOriginal[i] ? "o" : "")}"
                + $" d{_plates.ObjDist[i]:0.#} to({rt.TitleOffset.X:0.#},{rt.TitleOffset.Y:0.#})"
                + $" no({rt.NameOffset.X:0.#},{rt.NameOffset.Y:0.#})"
                + $" p{rt.PitchMul:0.00} a{rt.AdvanceMul:0.000} e({rt.Edge.X:0.#},{rt.Edge.Y:0.#})"
                + $" | pit{g.X:0.#} blk{g.Y:0.#} top{g.Z:0.#} bot{g.W:0.#}"
                + $" | n[{(name != Vector4.Zero ? $"{name.Y:0}-{name.W:0}" : "-")}]"
                + $" t[{(title != Vector4.Zero ? $"{title.Y:0}-{title.W:0}" : "-")}]"
                + $" | ar{(area != Vector4.Zero ? $"{area.Y:0}-{area.W:0}a{_plates.AreaAlign[i]}" : "-")}");
            y += rowH;
            rows++;
        }
        if (_occDebug != null)
            draw.AddText(new Vector2(x0, y), 0xFFAAAAAA, $"occ {_occDebug}");
    }

    private void DrawSupporterIcons()
    {
        _plates.IconMaskScale = Math.Clamp(
            _configService.Current.SupporterNameplateIconMaskScalePercent, 25, 200) / 100f;
        _plates.HudNineOrder = Math.Clamp(
            _configService.Current.SupporterNameplateHudNineOrder, 0, 3);
        _plates.HudLayerMask = _configService.Current.SupporterNameplateHudLayerMask;
        _plates.DebugLog = _configService.Current.SupporterNameplateDebugLog;
        _plates.Refresh();
        _frameMasksEmitted = false;
        LogStartupGates();
        if (_configService.Current.SupporterNameplateDebugOverlay
            || _configService.Current.SupporterNameplateDebugInfoWindow)
            DrawPlateDebugOverlay();
        if (_hasPlateFx)
            DrawPlateFx();
        if (_configService.Current.SupporterNameplateTestRect
            && _configService.Current.SupporterNameplateIconOverlay)
            DrawIconTextureDebug();
        var positions = _iconPositions;
        if (positions.Count == 0)
            return;
        LogMilestone(ref _iconsActiveLogged, "icons");
        var icon = _iconTexture;
        IDalamudTextureWrap? defaultTex = null;
        var hasDefaultIcon = icon != null && icon.TryGetWrap(out defaultTex, out _) && defaultTex != null;

        var scale = ImGuiHelpers.GlobalScale;
        var baseHeight = IconSize * scale;
        var now = ImGui.GetTime();
        var drawList = ImGui.GetBackgroundDrawList();
        var depthActive = SceneDepthOcclusionReady
            && _configService.Current.SupporterNameplateDepthOcclusion && PrepareDepthFrame(positions);
        _depthStatus = SceneDepthOcclusionReady ? _depthStatus : "paused";
        for (int i = 0; i < positions.Count; i++)
        {
            var entry = positions[i];
            var p = entry.PlateIndex;
            // the entry must disappear with its own plate: hidden by display
            // settings (name bit clear), faded to zero, or the root node went
            // invisible since the collect pass ran
            if (p < 0 || p >= MaxNamePlateObjects
                || !_plates.HasRect[p] || !_plates.NameVisible[p])
                continue;
            var plateAlpha = _plates.ContentAlpha[p];
            if (plateAlpha <= 0.004f)
                continue;
            // the plate's own render scale - nameplate size option folded
            // with distance; icon, gap and label grow with the nameplate
            var ps = _plates.PlateScale(entry.PlateIndex);
            var entryScale = scale * ps;
            var iconSize = baseHeight * ps * ResolvePlateTune(entry.PlateIndex).IconSize;
            ImTextureID handle;
            Vector2 uv0, uv1, size;
            var animated = _iconService.GetIcon(entry.IconId);
            if (animated != null)
            {
                (handle, uv0, uv1) = animated.GetFrame(now);
                size = animated.AspectSize(iconSize);
            }
            else
            {
                if (!hasDefaultIcon)
                    continue;
                handle = defaultTex!.Handle;
                uv0 = Vector2.Zero;
                uv1 = Vector2.One;
                size = new Vector2(iconSize * DefaultIconScale);
            }
            var label = entry.IconLabel;
            var labelColor = entry.IconLabelColor;
            var labelSize = label.Length > 0 ? ImGui.CalcTextSize(label) * ps : Vector2.Zero;
            var anchor = _plates.BlockAnchor(entry.PlateIndex) + entry.RelAnchor;
            var min = new Vector2(anchor.X + IconGap * entryScale, anchor.Y - size.Y * 0.5f);
            var right = min.X + size.X;
            if (labelSize != Vector2.Zero)
                right += IconGap * entryScale + labelSize.X;
            var rectMax = new Vector2(right, min.Y + size.Y);
            // the debug test rect replaces icon+label in the same spot and
            // extends the mask area so glyph cutouts are visible across it
            var testSize = _configService.Current.SupporterNameplateTestRect
                ? new Vector2(_configService.Current.SupporterNameplateTestRectWidth,
                    _configService.Current.SupporterNameplateTestRectHeight) * scale
                : Vector2.Zero;
            if (testSize != Vector2.Zero)
                rectMax = Vector2.Max(rectMax, min + testSize);
            // self-mask test: stretch the rect across the icon owner's own text
            // block so its own glyphs have to cut it - direct visual check of
            // the reconstruction against a plate the user can read up close
            if (testSize != Vector2.Zero
                && _configService.Current.SupporterNameplateTestRectSelfMask
                && _plates.InkRect[entry.PlateIndex] != Vector4.Zero)
            {
                var blk = _plates.InkRect[entry.PlateIndex];
                // reach left across the plate's icon zone (content bound
                // unions every captured image node) so the icon masks show
                var content = _plates.Rects[entry.PlateIndex];
                var left = content != Vector4.Zero ? MathF.Min(blk.X, content.X) : blk.X;
                min = new Vector2(left, blk.Y);
                testSize = new Vector2(blk.Z - left, blk.W - blk.Y);
                rectMax = new Vector2(blk.Z, blk.W);
            }
            _occRejectDebug.Clear();
            SubtractPlateOccluders(min, rectMax, entry, out var plateClips, out var chromeClips);
            if (_configService.Current.SupporterNameplateGpuOcclusion)
            {
                var glyphMasks = CollectTextMasks(min, rectMax, entry);
                var mask = _gpuMask.Begin(drawList, entry.PlateIndex, entry.TargetWorldPos,
                    _maskTexture, _mask2Texture, _mask3Texture, _mask4Texture,
                    _mask5Texture, _mask6Texture, _mask7Texture, _mask8Texture,
                    _maskScreen, _maskUv,
                    _maskChannel, _maskAlpha, _maskTexSel, _maskEdge, glyphMasks);
                if (mask != 0)
                {
                    LogMilestone(ref _cutoutsActiveLogged, "gpu mask");
                    var fontsReady = 0;
                    foreach (var h in _fontHandles)
                    {
                        if (h.Available)
                            fontsReady++;
                    }
                    _occDebug = $"gpu+{chromeClips?.Count ?? 0}np/{glyphMasks}g f{fontsReady}/{_fontHandles.Count} hud{_plates.HudScanUnits}u{_plates.HudScanNodes}n:{_plates.HudCount}{(_plates.HudSourceName != null ? $"[{_plates.HudSourceName}|{(_plates.HudSourceSolid ? "s" : "t")}]" : "")} v{_plates.HudRejState} a{_plates.HudRejChain} b{_plates.HudRejBound} r{_plates.HudRejSrv} ir({min.X:0},{min.Y:0},{rectMax.X:0},{rectMax.Y:0}) {_occRejectDebug}";
                    try
                    {
                        if (chromeClips is { Count: 0 })
                            continue;
                        DrawOverlayClipped(drawList, chromeClips, handle, uv0, uv1, min, size, label,
                            labelColor, entry.IconLabelEdgeColor, labelSize, anchor.Y, entryScale, ps, testSize, plateAlpha);
                    }
                    finally
                    {
                        GpuNameplateImGuiMask.End(drawList, mask);
                    }
                    continue;
                }
            }
            if (plateClips is { Count: 0 })
                continue;
            var occMask = ComputeOcclusionCells(entry, min, rectMax, depthActive, out var occCols, out var occRows);
            var occCells = occCols * occRows;
            var fullMask = occCells >= 64 ? ulong.MaxValue : (1UL << occCells) - 1UL;
            if (occMask == fullMask)
                continue;
            if (occMask == 0)
            {
                DrawOverlayClipped(drawList, plateClips, handle, uv0, uv1, min, size, label, labelColor, entry.IconLabelEdgeColor, labelSize, anchor.Y, entryScale, ps, testSize, plateAlpha);
                continue;
            }
            var cellW = (right - min.X) / occCols;
            var cellH = size.Y / occRows;
            var cell = 0;
            for (var row = 0; row < occRows; row++)
            {
                for (var col = 0; col < occCols; col++, cell++)
                {
                    if ((occMask & (1UL << cell)) != 0)
                        continue;
                    var cellMin = new Vector2(min.X + cellW * col, min.Y + cellH * row);
                    var cellMax = cellMin + new Vector2(cellW, cellH);
                    if (plateClips == null)
                    {
                        drawList.PushClipRect(cellMin, cellMax, true);
                        DrawOverlayUnclipped(drawList, handle, uv0, uv1, min, size, label, labelColor, entry.IconLabelEdgeColor, labelSize, anchor.Y, entryScale, ps, testSize, plateAlpha);
                        drawList.PopClipRect();
                        continue;
                    }
                    for (var c = 0; c < plateClips.Count; c++)
                    {
                        var pr = plateClips[c];
                        drawList.PushClipRect(new Vector2(pr.X, pr.Y), new Vector2(pr.Z, pr.W), true);
                        drawList.PushClipRect(cellMin, cellMax, true);
                        DrawOverlayUnclipped(drawList, handle, uv0, uv1, min, size, label, labelColor, entry.IconLabelEdgeColor, labelSize, anchor.Y, entryScale, ps, testSize, plateAlpha);
                        drawList.PopClipRect();
                        drawList.PopClipRect();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs registered nameplate effects over every visible plate. A plate gets
    /// its supporter-assigned effect (<see cref="_plateFxId"/> - the future
    /// server/tier path) plus anything the debug toggles enable globally.
    /// </summary>
    private void DrawPlateFx()
    {
        var draw = ImGui.GetBackgroundDrawList();
        var time = (float)ImGui.GetTime();
        var fxCount = 0;
        for (var i = 0; i < MaxNamePlateObjects; i++)
        {
            if (!_plates.HasRect[i] || !_plates.NameVisible[i]
                || _plates.ContentAlpha[i] <= 0.004f || _plateFxId[i] == null)
                continue;
            _fxPlateOrder[fxCount++] = i;
        }
        // back-to-front like the icon pass - plate index order is arbitrary
        // and lets a rear plate's effect paint over the front plate's
        for (var i = 1; i < fxCount; i++)
        {
            var v = _fxPlateOrder[i];
            var j = i - 1;
            while (j >= 0 && _plates.Order[_fxPlateOrder[j]] > _plates.Order[v])
            {
                _fxPlateOrder[j + 1] = _fxPlateOrder[j];
                j--;
            }
            _fxPlateOrder[j + 1] = v;
        }
        for (var n = 0; n < fxCount; n++)
        {
            var i = _fxPlateOrder[n];
            var plateAlpha = _plates.ContentAlpha[i];
            var fxId = _plateFxId[i]!;
            var name = _plates.NameRect[i] != Vector4.Zero ? _plates.NameRect[i] : _plates.InkRect[i];
            if (name == Vector4.Zero)
                continue;
            for (var f = 0; f < NameplateFx.All.Length; f++)
            {
                var fx = NameplateFx.All[f];
                if (!string.Equals(fxId, fx.Id, StringComparison.Ordinal))
                    continue;
                fx.Draw(new NameplateFxContext(draw, time, i, name, _plates.TitleRect[i],
                    _plates.InkRect[i], _plates.WorldPos[i] + Vector3.UnitY * OcclusionProbeHeight,
                    _plates.Order[i], _plates.PlateScale(i), plateAlpha, this));
            }
        }
    }

    /// <summary>
    /// Begins an occluded FX draw layer: every layer is scene-depth tested
    /// against the plate's world position, and the mask additionally carries the
    /// glyph/icon coverage of nameplates drawn in front - the same cutout the
    /// supporter icon gets. <see cref="NameplateFxLayer.BehindText"/> also folds
    /// in the plate's own letters so the effect passes behind its text.
    /// </summary>
    internal unsafe nint BeginFxLayer(in NameplateFxContext ctx, NameplateFxLayer layer)
    {
        if (!_configService.Current.SupporterNameplateGpuOcclusion || ctx.WorldPos == Vector3.Zero)
            return 0;
        var (min, max) = ctx.FxBounds;
        var entry = new IconDrawPosition(Vector2.Zero, ctx.WorldPos, ctx.WorldPos, ctx.Plate,
            ctx.Order, 0, string.Empty, 0u, string.Empty, 0u, string.Empty);
        // effect layering is fixed by design - the test-rect self-mask debug
        // toggle must not silently cut front layers by the plate's own glyphs
        var count = CollectTextMasks(min, max, entry,
            layer == NameplateFxLayer.BehindText, honorSelfMaskTest: false);
        return _gpuMask.Begin(ctx.Draw, ctx.Plate, ctx.WorldPos, _maskTexture, _mask2Texture,
            _mask3Texture, _mask4Texture, _mask5Texture, _mask6Texture, _mask7Texture,
            _mask8Texture, _maskScreen, _maskUv, _maskChannel, _maskAlpha,
            _maskTexSel, _maskEdge, count);
    }

    internal static void EndFxLayer(ImDrawListPtr draw, nint token) => GpuNameplateImGuiMask.End(draw, token);

    /// <summary>Scales a packed ABGR color's alpha byte by the plate's
    /// content alpha - our overlays fade exactly with the plate they
    /// belong to instead of popping at the fade boundaries.</summary>
    private static uint ApplyPlateAlpha(uint col, float a)
    {
        if (a >= 0.996f)
            return col;
        var alpha = (uint)Math.Clamp((int)((col >> 24) * a), 0, 255);
        return (col & 0x00FFFFFFu) | (alpha << 24);
    }

    private void DrawOverlayClipped(ImDrawListPtr drawList, List<Vector4>? clips,
        ImTextureID texHandle, Vector2 uv0, Vector2 uv1, Vector2 min, Vector2 size,
        string label, uint labelColor, uint labelEdgeColor, Vector2 labelSize,
        float anchorY, float scale, float fontScale, Vector2 testSize, float plateAlpha)
    {
        if (testSize != Vector2.Zero)
        {
            var color = ApplyPlateAlpha(_configService.Current.SupporterNameplateTestRectColor, plateAlpha);
            if (clips == null)
            {
                drawList.AddRectFilled(min, min + testSize, color);
                return;
            }
            for (var i = 0; i < clips.Count; i++)
            {
                var r = clips[i];
                drawList.PushClipRect(new Vector2(r.X, r.Y), new Vector2(r.Z, r.W), true);
                drawList.AddRectFilled(min, min + testSize, color);
                drawList.PopClipRect();
            }
            return;
        }
        DrawIconAndLabelClipped(drawList, clips, texHandle, uv0, uv1, min, size, label,
            labelColor, labelEdgeColor, labelSize, anchorY, scale, fontScale, plateAlpha);
    }

    private void DrawOverlayUnclipped(ImDrawListPtr drawList,
        ImTextureID texHandle, Vector2 uv0, Vector2 uv1, Vector2 min, Vector2 size,
        string label, uint labelColor, uint labelEdgeColor, Vector2 labelSize,
        float anchorY, float scale, float fontScale, Vector2 testSize, float plateAlpha)
    {
        if (testSize != Vector2.Zero)
        {
            drawList.AddRectFilled(min, min + testSize,
                ApplyPlateAlpha(_configService.Current.SupporterNameplateTestRectColor, plateAlpha));
            return;
        }
        DrawIconAndLabel(drawList, texHandle, uv0, uv1, min, size, label,
            labelColor, labelEdgeColor, labelSize, anchorY, scale, fontScale, plateAlpha);
    }

    private static void DrawIconAndLabelClipped(ImDrawListPtr drawList, List<Vector4>? clips,
        ImTextureID texHandle, Vector2 uv0, Vector2 uv1, Vector2 min, Vector2 size,
        string label, uint labelColor, uint labelEdgeColor, Vector2 labelSize, float anchorY, float scale, float fontScale, float plateAlpha)
    {
        if (clips == null)
        {
            DrawIconAndLabel(drawList, texHandle, uv0, uv1, min, size, label, labelColor,
                labelEdgeColor, labelSize, anchorY, scale, fontScale, plateAlpha);
            return;
        }
        for (var i = 0; i < clips.Count; i++)
        {
            var r = clips[i];
            drawList.PushClipRect(new Vector2(r.X, r.Y), new Vector2(r.Z, r.W), true);
            DrawIconAndLabel(drawList, texHandle, uv0, uv1, min, size, label, labelColor,
                labelEdgeColor, labelSize, anchorY, scale, fontScale, plateAlpha);
            drawList.PopClipRect();
        }
    }

    private static void DrawIconAndLabel(ImDrawListPtr drawList, ImTextureID texHandle,
        Vector2 uv0, Vector2 uv1, Vector2 min, Vector2 size, string label, uint labelColor, uint labelEdgeColor, Vector2 labelSize, float anchorY, float scale, float fontScale, float plateAlpha)
    {
        SpheneUI.Media.SpriteInRect(drawList, texHandle, min, min + size, uv0, uv1,
            new Vector4(1f, 1f, 1f, plateAlpha));
        if (labelSize != Vector2.Zero)
        {
            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize() * fontScale;
            var textPos = new Vector2(min.X + size.X + IconGap * scale, anchorY - labelSize.Y * 0.5f);
            var edgeCol = ApplyPlateAlpha(labelEdgeColor, plateAlpha);
            if ((edgeCol & 0xFF000000u) != 0)
            {
                var edgeOff = MathF.Max(1f, scale);
                drawList.AddText(font, fontSize, textPos + new Vector2(edgeOff, 0f), edgeCol, label);
                drawList.AddText(font, fontSize, textPos + new Vector2(-edgeOff, 0f), edgeCol, label);
                drawList.AddText(font, fontSize, textPos + new Vector2(0f, edgeOff), edgeCol, label);
                drawList.AddText(font, fontSize, textPos + new Vector2(0f, -edgeOff), edgeCol, label);
            }
            drawList.AddText(font, fontSize, textPos, ApplyPlateAlpha(labelColor, plateAlpha), label);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void RequestRedraw()
    {
        if (_disposed)
            return;
        try { _namePlateGui.RequestRedraw(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to request nameplate redraw"); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _iconPositions = [];
        try { _pluginInterface.UiBuilder.Draw -= DrawSupporterIcons; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from UiBuilder.Draw"); }
        try { _namePlateGui.OnPostDataUpdate -= OnPostDataUpdate; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from post data update"); }
        try { _namePlateGui.OnNamePlateUpdate -= OnNamePlateTextUpdate; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from nameplate update"); }
        try { _namePlateGui.RequestRedraw(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to request nameplate redraw on dispose"); }
        ReleaseMaskTexSrvs();
        _depthSampler.Dispose();
        _gpuMask.Dispose();
        _coverageBake?.Dispose();
        _coverageBake = null;
        _plates.Dispose();
        foreach (var handle in _fontHandles)
        {
            try { handle.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose font handle"); }
        }
        _fontHandles.Clear();
        _iconTexture = null;
    }
}
