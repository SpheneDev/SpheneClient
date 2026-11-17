using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text;

namespace Sphene.Services;

/// <summary>Reads all per-plate state from AddonNamePlate: screen rects, node
/// transforms, text layout, visibility flags, icon nodes and the bake hooks used
/// to capture the game's own text rasterization. Owns nothing else.</summary>
internal sealed unsafe class NameplateReader : IDisposable
{
    internal const int MaxNamePlateObjects = 50;
    // image-node mask slots per plate: NameIcon, MarkerIcon, plus extra
    // nodes found inside the plate component (job icon, …)
    internal const int IconSlots = 8;
    private static readonly PlateTune DefaultTune = new(Vector2.Zero, Vector2.Zero, 1f, 1f, Vector2.Zero,
        1f, 1f, 1f, Vector2.Zero);
    internal const byte MacroColor = 0x13;
    internal const byte MacroEdgeColor = 0x14;
    internal const byte ExprStackColor = 0xEC;
    internal const byte MacroUIForeground = 0x48;
    internal const byte MacroUIGlow = 0x49;

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly IGameInteropProvider _gameInterop;
    private readonly ILogger _logger;
    private bool _disposed;

    private readonly Vector4[] _plateRects = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateChrome = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateInkRect = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateTextRect = new Vector4[MaxNamePlateObjects];
    private readonly string?[] _plateChainDump = new string?[MaxNamePlateObjects];
    private readonly Vector4[] _plateNameRect = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateTitleRect = new Vector4[MaxNamePlateObjects];
    private readonly string?[] _plateTitle = new string?[MaxNamePlateObjects];
    private readonly string?[] _plateName = new string?[MaxNamePlateObjects];
    private readonly int[] _plateArrayIndex = new int[MaxNamePlateObjects];
    private readonly float[] _plateObjDist = new float[MaxNamePlateObjects];
    private readonly Vector3[] _plateWorldPos = new Vector3[MaxNamePlateObjects];
    private readonly Vector2[] _plateNodePos = new Vector2[MaxNamePlateObjects];
    private readonly Vector2[] _plateNodeScale = new Vector2[MaxNamePlateObjects];
    private readonly float[] _plateRenderScale = new float[MaxNamePlateObjects];
    private readonly Vector4[] _plateBakeDbg = new Vector4[MaxNamePlateObjects];
    private readonly float[] _plateBakeTy = new float[MaxNamePlateObjects];
    private readonly int[] _plateBakeAlpha = new int[MaxNamePlateObjects];
    private readonly bool[] _plateIsBaked = new bool[MaxNamePlateObjects];
    private readonly bool[] _plateIsMinion = new bool[MaxNamePlateObjects];
    // UI3D ObjectKind == PlayerCharacter - geo/scale instrumentation stays
    // scoped to real players instead of bells, boards and battle NPCs
    private readonly bool[] _plateIsPlayer = new bool[MaxNamePlateObjects];
    private readonly bool[] _plateIsTarget = new bool[MaxNamePlateObjects];
    private int _selfPlate = -1;
    // world-anchored chrome that renders over nameplates lives in separate
    // addons - the target cursor arrow, chat bubbles (MiniTalk) and enemy
    // cast bars all occlude our overlay draws, so their image nodes join the
    // mask set like plate icons (real pixels, not bounds)
    private const int CursorSlots = 16;
    private readonly Vector4[] _cursorRects = new Vector4[CursorSlots];
    private readonly nint[] _cursorSrv = new nint[CursorSlots];
    private readonly uint[] _cursorTexKey = new uint[CursorSlots];
    private readonly Vector4[] _cursorUv = new Vector4[CursorSlots];
    private readonly float[] _cursorAlpha = new float[CursorSlots];
    private readonly byte[] _cursorPartCnt = new byte[CursorSlots];
    // node pointers behind the captured slots - the underscore/underscore-less
    // addon spellings resolve to the same unit, so pointer dedupe stops each
    // element joining the occluder set twice
    private readonly nint[] _cursorNodePtrs = new nint[CursorSlots];
    // HUD units above the nameplate depth layer - chat, menus, hotbars and
    // dialogs all draw over plates, so their real image pixels occlude our
    // overlays wherever they overlap; solid rects carry units whose nodes
    // resolve no texture (text bodies, pure containers)
    private const int HudSlots = 160;
    private const int NineGridCells = 9;
    // component nodes report NodeType 10xx at runtime (xx = component type),
    // not the NodeType.Component sentinel
    private const int ComponentNodeTypeBase = 1000;
    // AtkUnitManager keeps 13 depth-layer draw groups; AtkUnitBase.DepthLayer
    // (Flags198 bits 16-19) names each loaded unit's group directly, so the
    // scan never touches the AtkUnitList arrays themselves
    private const int HudDepthLayers = 13;
    private readonly Vector4[] _hudRects = new Vector4[HudSlots];
    private readonly nint[] _hudSrv = new nint[HudSlots];
    private readonly uint[] _hudTexKey = new uint[HudSlots];
    private readonly Vector4[] _hudUv = new Vector4[HudSlots];
    private readonly float[] _hudAlpha = new float[HudSlots];
    private readonly byte[] _hudPartCnt = new byte[HudSlots];
    private readonly byte[] _hudPartIdx = new byte[HudSlots];
    private readonly byte[] _hudLayer = new byte[HudSlots];
    private int _hudCurLayer = -1;
    // current unit's draw alpha folded into every emitted mask - a show/hide
    // tween ramps it while the unit still paints, letting occlusions fade
    // with the real window animation instead of popping
    private float _hudUnitA = 1f;
    // linked-unit group for docked/hosted panels that paint inside their
    // host's pass instead of sitting in a depth layer themselves
    private const int HudLinkedLayer = 13;
    // AllLoadedUnitsList holds up to 256 entries - the drawn set must fit
    // every loaded unit or the front-first sort never sees the tail
    private const int HudDrawnMax = 256;
    private readonly nint[] _hudDrawnUnits = new nint[HudDrawnMax];
    private readonly ushort[] _hudDrawnIds = new ushort[HudDrawnMax];
    private readonly int[] _hudDrawnKeys = new int[HudDrawnMax];
    private int _hudDrawnCount;
    // emitted node pointers - the list-item tree walk overlaps the renderer's
    // flat UldManager list, so re-visiting a node must not double-emit
    private readonly nint[] _hudNodePtrs = new nint[HudSlots];
    private int _hudCount;
    private int _hudScanUnits;
    private int _hudScanNodes;
    private int _hudRejState;
    private int _hudRejChain;
    private int _hudRejBound;
    private int _hudRejSrv;
    public int HudCount => _hudCount;
    public int HudScanUnits => _hudScanUnits;
    public int HudScanNodes => _hudScanNodes;
    public int HudRejState => _hudRejState;
    public int HudRejChain => _hudRejChain;
    public int HudRejBound => _hudRejBound;
    public int HudRejSrv => _hudRejSrv;
    public string? HudSourceName => _hudSrcName;
    public bool HudSourceSolid => _hudSrcSolid;
    public Vector4[] HudRects => _hudRects;
    public nint[] HudSrv => _hudSrv;
    public uint[] HudTexKey => _hudTexKey;
    public Vector4[] HudUv => _hudUv;
    public float[] HudAlpha => _hudAlpha;
    public byte[] HudPartCnt => _hudPartCnt;
    public byte[] HudPartIdx => _hudPartIdx;
    public byte[] HudLayer => _hudLayer;
    /// <summary>Bitmask of depth layers included in the scan - bits 0-12 are
    /// the game's draw layers, bit 13 the docked/hosted linked-unit group.</summary>
    public int HudLayerMask = 0x3FFF;
    // set from SupporterNameplateDebugLog - geo/scale instrumentation stays
    // silent unless the diagnostics toggle is on
    public bool DebugLog;
    /// <summary>Cell mapping used for multi-part nine-grids - 0 row-major,
    /// 1 corners-first, 2 corners-then-sides, 3 column-major. Diagnostic
    /// switch while the authored part order is being identified.</summary>
    public int HudNineOrder;
    private int _cursorCount;
    // addons rendered above the nameplate layer, scanned for image nodes
    // every collect - name variants cover the runtime's underscore prefixes
    private static readonly string[] OccluderAddons =
        { "_TargetCursor", "TargetCursor", "_MiniTalk", "MiniTalk",
          "_CastBarEnemy", "CastBarEnemy", "_TargetInfo", "TargetInfo" };
    private readonly int[] _plateVisFlags = new int[MaxNamePlateObjects];
    private nint _bakeSrv;
    private Vector2 _bakeSize;
    private int _namePlateSize;
    private bool _namePlateFixedRes;
    private readonly int[] _plateTextW = new int[MaxNamePlateObjects];
    private readonly int[] _plateTextH = new int[MaxNamePlateObjects];
    private readonly bool[] _plateDrawOriginal = new bool[MaxNamePlateObjects];
    private readonly byte[] _plateKind = new byte[MaxNamePlateObjects];
    private readonly Vector4[] _plateGameRect = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _platePrevGame = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateDrawnGame = new Vector4[MaxNamePlateObjects];
    private readonly Vector2[] _plateRenderOff = new Vector2[MaxNamePlateObjects];
    private readonly Vector2[] _plateEffAnchor = new Vector2[MaxNamePlateObjects];
    private readonly Vector2[] _plateMeasured = new Vector2[MaxNamePlateObjects];
    private readonly byte[] _plateAlignByte = new byte[MaxNamePlateObjects];
    private readonly Vector2[] _plateTitleSize = new Vector2[MaxNamePlateObjects];
    private readonly int[] _plateFontSize = new int[MaxNamePlateObjects];
    private readonly int[] _plateCharSpacing = new int[MaxNamePlateObjects];
    private readonly int[] _plateFontType = new int[MaxNamePlateObjects];
    // the node's own layout/style bytes: TextFlags carries the Edge (outline)
    // pass, LineSpacing the real line pitch the renderer was configured with,
    // EdgeColor/BackgroundColor whether extra layers draw at all
    private readonly int[] _plateTextFlags = new int[MaxNamePlateObjects];
    private readonly byte[] _plateLineSpacing = new byte[MaxNamePlateObjects];
    private readonly byte[] _plateEdgeAlpha = new byte[MaxNamePlateObjects];
    private readonly byte[] _plateBgAlpha = new byte[MaxNamePlateObjects];
    private readonly short[] _plateIconXAdj = new short[MaxNamePlateObjects];
    private readonly short[] _plateIconYAdj = new short[MaxNamePlateObjects];
    // nameplate image-node icons (duty/party/marker), captured per plate so
    // the mask pass can cut them by their real texture pixels, not node bounds
    // - slot 0 holds NameIcon, slot 1 holds MarkerIcon
    private readonly Vector4[] _plateIconRects = new Vector4[MaxNamePlateObjects * IconSlots];
    private readonly nint[] _plateIconSrv = new nint[MaxNamePlateObjects * IconSlots];
    private readonly uint[] _plateIconTexKey = new uint[MaxNamePlateObjects * IconSlots];
    private readonly Vector4[] _plateIconUv = new Vector4[MaxNamePlateObjects * IconSlots];
    private readonly float[] _plateIconAlpha = new float[MaxNamePlateObjects * IconSlots];
    private readonly byte[] _plateIconWrap = new byte[MaxNamePlateObjects * IconSlots];
    private readonly byte[] _plateIconPartCnt = new byte[MaxNamePlateObjects * IconSlots];
    private readonly uint[] _plateIconId = new uint[MaxNamePlateObjects * IconSlots];
    // ui/icon/210000/210205 - the generic badge the addon pre-loads into the
    // dynamic status nodes; loaded and visible-flagged but never rasterized,
    // so it must not emit masks (a real status replaces the id and captures)
    private const uint StatusPlaceholderIconId = 210205;
    private readonly byte[] _plateIconLive = new byte[MaxNamePlateObjects];
    private readonly int[] _plateIconSig = new int[MaxNamePlateObjects * IconSlots];
    // sig for the part-resolve diagnostic - one log line per change so a
    // stable plate layout stays quiet
    private readonly int[] _iconResolveSig = new int[MaxNamePlateObjects * IconSlots];
    private readonly HashSet<uint> _iconPlaceholderSeen = new();
    // offset into the uld parts list for the alternate icon set - the
    // job-icon layout draws different parts through it (0x48A on the addon)
    private ushort _alternatePartId;
    // final computed layout for the live info window: pitch, blockH, top, drawBottom
    private readonly Vector4[] _plateDbgGeom = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateFontInfo = new Vector4[MaxNamePlateObjects];
    private readonly int[]?[] _plateGlyphSeq = new int[]?[MaxNamePlateObjects];
    // payload byte offset after each seq element plus the node's measured
    // width through each element - diagnostic comparison against the
    // reconstructed pens so real layout steps can be pinned down
    private readonly int[]?[] _plateGlyphEnds = new int[]?[MaxNamePlateObjects];
    private readonly float[]?[] _plateCells = new float[]?[MaxNamePlateObjects];
    // GetTextDrawSize answers in distance-scaled units - cells are only valid
    // at the line width they were measured at, so keep it for rescaling
    private readonly float[] _plateCellsW = new float[MaxNamePlateObjects * 2];
    private readonly long[] _plateMeasureHash = new long[MaxNamePlateObjects];
    private readonly byte[]?[] _platePayload = new byte[]?[MaxNamePlateObjects];
    private readonly byte[]?[] _plateGlyphAlpha = new byte[]?[MaxNamePlateObjects];
    private readonly byte[] _plateNodeAlpha = new byte[MaxNamePlateObjects];
    private readonly Vector4[] _plateLineBytes = new Vector4[MaxNamePlateObjects];
    private readonly Vector2[] _plateLineSize = new Vector2[MaxNamePlateObjects * 2];
    // each line's slot step at render scale - lineH×s stored per line slot at
    // emission: [i*2] is the top line's step, [i*2+1] the bottom line's
    private readonly float[] _plateLineStep = new float[MaxNamePlateObjects * 2];
    private readonly Vector4[] _plateNameLine = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateTitleLine = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateNodeRect = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateContainerRect = new Vector4[MaxNamePlateObjects];
    private readonly Vector4[] _plateCollisionRect = new Vector4[MaxNamePlateObjects];
    // block anchor captured from NameText->ScreenX at OnDataUpdate time -
    // ScreenX still holds the previously rendered frame's position, so this
    // lives in the same render space the icon/test-rect anchor (platePos via
    // collision->ScreenX) uses. Bands and masks anchored here move exactly
    // like the rect instead of riding the newer number-array position.
    private readonly Vector2[] _plateSBlock = new Vector2[MaxNamePlateObjects];
    // the sb value from the previous frame - the title currently on screen was
    // drawn with it, so masks anchored to the fresh sb lead the text by a frame
    private readonly Vector2[] _plateSBlockDrawn = new Vector2[MaxNamePlateObjects];
    // the node's bottom-center anchor captured inside BakePlateRenderer.Draw -
    // the exact state the visible text was drawn with, no frame-phase guessing
    private readonly Vector4[] _plateDrawnState = new Vector4[MaxNamePlateObjects];
    private readonly int[] _plateDrawnFrame = new int[MaxNamePlateObjects];
    private int _drawFrameCounter;
    // quantized-geometry signature per plate - distance stepping shows up as
    // abrupt changes in node scale, bake alpha and measured line width
    private readonly Vector4[] _plateGeoSig = new Vector4[MaxNamePlateObjects];
    private readonly float[] _plateScaleSig = new float[MaxNamePlateObjects];
    private long _geoLogTicks;
    private unsafe AddonNamePlate* _namePlateAddon;
    private Hook<BakeDrawDelegate>? _bakeDrawHook;
    private Hook<BakeBlitDelegate>? _bakeBlitHook;
    private Hook<BakePrepareDelegate>? _bakePrepareHook;
    private readonly Vector4[] _plateAreaRect = new Vector4[MaxNamePlateObjects];
    private readonly int[] _plateAreaFrame = new int[MaxNamePlateObjects];
    private readonly byte[] _plateAreaAlign = new byte[MaxNamePlateObjects];
    private readonly float[] _plateOrder = new float[MaxNamePlateObjects];
    private readonly bool[] _plateHasRect = new bool[MaxNamePlateObjects];
    // whether the name text bit in VisibilityFlags is set - Sphene-owned
    // drawables anchor to the name line, so a gauge-only plate must not
    // carry our icon/label/FX either
    private readonly bool[] _plateNameVisible = new bool[MaxNamePlateObjects];
    // effective draw alpha (root × name node alpha) - plate fade transitions
    // move these bytes, so our overlays multiply their tint by it
    private readonly float[] _plateContentAlpha = new float[MaxNamePlateObjects];
    private readonly System.Text.StringBuilder _plateOrderDebug = new(96);
    private string? _plateDebug;

    private const string BakeBlitSignature =
        "48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48 89 78 20 41 54 41 56 41 57 48 83 EC 50 F3 0F 10 42 ??";

    private unsafe delegate void BakeDrawDelegate(AddonNamePlate.BakePlateRenderer* self,
        AtkTextNode* node);
    private unsafe delegate void BakeBlitDelegate(AddonNamePlate.BakePlateRenderer* self,
        AtkTextNode* node, AddonNamePlate.BakeData* bakeData);
    private unsafe delegate void BakePrepareDelegate(AddonNamePlate.BakePlateRenderer* self,
        AddonNamePlate.TextDrawArea* outArea, AtkFontManager.FontSet* fontSet, AtkTextNode* node);

    public NameplateReader(IGameGui gameGui, IObjectTable objectTable, IGameInteropProvider gameInterop, ILogger logger)
    {
        _gameGui = gameGui;
        _objectTable = objectTable;
        _gameInterop = gameInterop;
        _logger = logger;
    }

    public Vector4[] Rects => _plateRects;
    public Vector4[] Chrome => _plateChrome;
    public Vector4[] InkRect => _plateInkRect;
    public Vector4[] TextRect => _plateTextRect;
    public string?[] ChainDump => _plateChainDump;
    public Vector4[] NameRect => _plateNameRect;
    public Vector4[] TitleRect => _plateTitleRect;
    public string?[] Title => _plateTitle;
    public string?[] Name => _plateName;
    public int[] ArrayIndex => _plateArrayIndex;
    public float[] ObjDist => _plateObjDist;
    public Vector3[] WorldPos => _plateWorldPos;
    public Vector2[] NodePos => _plateNodePos;
    public Vector2[] NodeScale => _plateNodeScale;
    public float[] RenderScale => _plateRenderScale;
    public Vector4[] BakeDbg => _plateBakeDbg;
    public float[] BakeTy => _plateBakeTy;
    public int[] BakeAlpha => _plateBakeAlpha;
    public bool[] IsBaked => _plateIsBaked;
    public bool[] IsMinion => _plateIsMinion;
    public bool[] IsTarget => _plateIsTarget;
    public int[] VisFlags => _plateVisFlags;
    public int[] TextW => _plateTextW;
    public int[] TextH => _plateTextH;
    public bool[] DrawOriginal => _plateDrawOriginal;
    public byte[] Kind => _plateKind;
    public Vector4[] GameRect => _plateGameRect;
    public Vector4[] PrevGame => _platePrevGame;
    public Vector4[] DrawnGame => _plateDrawnGame;
    public Vector2[] RenderOff => _plateRenderOff;
    public Vector2[] EffAnchor => _plateEffAnchor;
    public Vector2[] Measured => _plateMeasured;
    public byte[] AlignByte => _plateAlignByte;
    public Vector2[] TitleSize => _plateTitleSize;
    public int[] FontSize => _plateFontSize;
    public int[] CharSpacing => _plateCharSpacing;
    public int[] FontType => _plateFontType;
    public int[] TextFlags => _plateTextFlags;
    public byte[] LineSpacing => _plateLineSpacing;
    public byte[] EdgeAlpha => _plateEdgeAlpha;
    public byte[] BgAlpha => _plateBgAlpha;
    public short[] IconXAdj => _plateIconXAdj;
    public short[] IconYAdj => _plateIconYAdj;
    public Vector4[] IconRects => _plateIconRects;
    public nint[] IconSrv => _plateIconSrv;
    public uint[] IconTexKey => _plateIconTexKey;
    public Vector4[] IconUv => _plateIconUv;
    public float[] IconAlpha => _plateIconAlpha;
    public byte[] IconWrap => _plateIconWrap;
    public byte[] IconPartCnt => _plateIconPartCnt;
    public uint[] IconIds => _plateIconId;
    public ushort AlternatePartId => _alternatePartId;
    /// <summary>Extra tuning factor on icon mask quads - non-stretch icon
    /// draws can come out smaller than the node/part dims the uld reports.</summary>
    public float IconMaskScale { get; set; } = 1f;
    public byte[] IconLive => _plateIconLive;
    public int[] IconSig => _plateIconSig;
    public Vector4[] DbgGeom => _plateDbgGeom;
    public Vector4[] FontInfo => _plateFontInfo;
    public int[]?[] GlyphSeq => _plateGlyphSeq;
    public int[]?[] GlyphEnds => _plateGlyphEnds;
    public float[]?[] Cells => _plateCells;
    public float[] CellsW => _plateCellsW;
    public long[] MeasureHash => _plateMeasureHash;
    public byte[]?[] Payload => _platePayload;
    public byte[]?[] GlyphAlpha => _plateGlyphAlpha;
    public byte[] NodeAlpha => _plateNodeAlpha;
    public Vector4[] LineBytes => _plateLineBytes;
    public Vector2[] LineSize => _plateLineSize;
    public float[] LineStep => _plateLineStep;
    public Vector4[] NameLine => _plateNameLine;
    public Vector4[] TitleLine => _plateTitleLine;
    public Vector4[] NodeRect => _plateNodeRect;
    public Vector4[] ContainerRect => _plateContainerRect;
    public Vector4[] CollisionRect => _plateCollisionRect;
    public Vector2[] SBlock => _plateSBlock;
    public Vector2[] SBlockDrawn => _plateSBlockDrawn;
    public Vector4[] DrawnState => _plateDrawnState;
    public int[] DrawnFrame => _plateDrawnFrame;
    public Vector4[] AreaRect => _plateAreaRect;
    public int[] AreaFrame => _plateAreaFrame;
    public byte[] AreaAlign => _plateAreaAlign;
    public float[] Order => _plateOrder;
    public int SelfPlate => _selfPlate;
    public Vector4[] CursorRects => _cursorRects;
    public nint[] CursorSrv => _cursorSrv;
    public uint[] CursorTexKey => _cursorTexKey;
    public Vector4[] CursorUv => _cursorUv;
    public float[] CursorAlpha => _cursorAlpha;
    public byte[] CursorPartCnt => _cursorPartCnt;
    public int CursorCount => _cursorCount;
    public bool[] HasRect => _plateHasRect;
    public bool[] NameVisible => _plateNameVisible;
    public float[] ContentAlpha => _plateContentAlpha;
    public nint BakeSrv => _bakeSrv;
    public Vector2 BakeSize => _bakeSize;
    public int NamePlateSize => _namePlateSize;
    public bool FixedRes => _namePlateFixedRes;
    public AddonNamePlate* Addon => _namePlateAddon;
    public int DrawFrame => _drawFrameCounter;
    public string? DebugText => _plateDebug;
    public System.Text.StringBuilder OrderDebug => _plateOrderDebug;
    public int DiagPlate { get; set; } = -1;
    public Func<int, PlateTune>? TuneProvider { get; set; }
    public Func<float>? AnchorExtrapolation { get; set; }
    public bool BakeHooked => _bakeDrawHook != null;

    private PlateTune Tune(int index) => TuneProvider?.Invoke(index) ?? DefaultTune;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _bakeDrawHook?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose bake draw hook"); }
        try { _bakeBlitHook?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose bake blit hook"); }
        try { _bakePrepareHook?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose bake prepare hook"); }
    }

    /// <summary>
    /// Snapshots every visible nameplate's content rect and stack rank (the
    /// game's per-plate <c>Priority</c>) so icons are clipped by plates that
    /// natively render in front of ours.
    /// </summary>
    public unsafe void Collect(bool verbose)
    {
        Array.Clear(_plateHasRect, 0, MaxNamePlateObjects);
        Array.Clear(_plateTextRect, 0, MaxNamePlateObjects);
        Array.Clear(_plateNameRect, 0, MaxNamePlateObjects);
        Array.Clear(_plateTitleRect, 0, MaxNamePlateObjects);
        Array.Clear(_plateOrder, 0, MaxNamePlateObjects);
        Array.Clear(_plateGameRect, 0, MaxNamePlateObjects);
        Array.Clear(_platePrevGame, 0, MaxNamePlateObjects);
        Array.Clear(_plateDrawnGame, 0, MaxNamePlateObjects);
        Array.Clear(_plateEffAnchor, 0, MaxNamePlateObjects);
        Array.Clear(_plateRenderOff, 0, MaxNamePlateObjects);
        Array.Clear(_plateSBlock, 0, MaxNamePlateObjects);
        Array.Clear(_plateSBlockDrawn, 0, MaxNamePlateObjects);
        Array.Clear(_plateDrawnState, 0, MaxNamePlateObjects);
        Array.Clear(_plateDrawnFrame, 0, MaxNamePlateObjects);
        _plateDebug = null;
        var addonPtr = _gameGui.GetAddonByName("NamePlate", 1);
        if (addonPtr.IsNull)
            return;
        var addon = (AddonNamePlate*)addonPtr.Address;
        _alternatePartId = *(ushort*)((byte*)addon + 0x48A);
        if (addon->NamePlateObjectArray == null)
            return;
        var stage = AtkStage.Instance();
        var strings = stage != null ? stage->GetStringArrayData(StringArrayType.NamePlate) : null;
        var plateStrings = strings != null ? (byte**)strings->StringArray : null;
        var numbers = stage != null ? stage->GetNumberArrayData(NumberArrayType.NamePlate) : null;
        var numberStruct = numbers != null ? (NamePlateNumberArray*)numbers->IntArray : null;
        for (var i = 0; i < MaxNamePlateObjects; i++)
        {
            _plateArrayIndex[i] = -1;
            _plateIsMinion[i] = false;
            _plateIsTarget[i] = false;
            _plateIsPlayer[i] = false;
            _plateVisFlags[i] = 0;
            _plateNameVisible[i] = false;
            _plateContentAlpha[i] = 0f;
            _plateObjDist[i] = float.NaN;
            _plateKind[i] = 0xFF;
        }
        _selfPlate = -1;
        if (numberStruct != null)
        {
            _namePlateSize = numberStruct->NamePlateSize;
            _namePlateFixedRes = !numberStruct->DisableFixedFontResolution;
            var active = Math.Min(numberStruct->ActiveNamePlateCount, MaxNamePlateObjects);
            for (var i = 0; i < active; i++)
            {
                ref var data = ref numberStruct->ObjectData[i];
                var objIndex = data.NamePlateObjectIndex;
                if (objIndex is >= 0 and < MaxNamePlateObjects)
                {
                    _plateArrayIndex[objIndex] = i;
                    _plateGameRect[objIndex] = new Vector4(data.X, data.Y, data.Scale, data.DrawFlags);
                    _plateVisFlags[objIndex] = data.VisibilityFlags;
                }
            }
        }
        // UI3DModule's plate→object map reports the real object kind -
        // Companion (the cosmetic follower) is only kept for the debug
        // marker now: whether ANY plate occludes is decided by the game's
        // own VisibilityFlags in the number array, so display-name settings
        // (off / only-when-targeted / per type) are followed automatically.
        // The same map carries each plate's GameObject - cache its distance
        // so the scale-step log can report real yalms
        var ui3d = UIModule.Instance()->GetUI3DModule();
        var lp = _objectTable.LocalPlayer;
        if (ui3d != null)
        {
            var npCount = Math.Min(ui3d->NamePlateObjectInfoCount, MaxNamePlateObjects);
            for (var i = 0; i < npCount; i++)
            {
                var info = ui3d->NamePlateObjectInfoPointers[i].Value;
                if (info == null || info->NamePlateIndex >= MaxNamePlateObjects)
                    continue;
                var go = info->GameObject;
                _plateIsMinion[info->NamePlateIndex] = go != null
                    && go->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Companion;
                _plateIsPlayer[info->NamePlateIndex] = go != null
                    && go->ObjectKind == FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Pc;
                var target = TargetSystem.Instance();
                _plateIsTarget[info->NamePlateIndex] = go != null && target != null && go == target->Target;
                if (go != null && lp != null && (nint)go == lp.Address)
                    _selfPlate = info->NamePlateIndex;
                // the game already resolves each plate's distance in yalms
                // while sorting its infos - cheaper and steadier than
                // subtracting positions ourselves
                _plateObjDist[info->NamePlateIndex] = info->DistanceFromPlayer;
                _plateWorldPos[info->NamePlateIndex] = go != null
                    ? new Vector3(go->Position.X, go->Position.Y, go->Position.Z)
                    : Vector3.Zero;
            }
        }
        if (verbose)
            _plateOrderDebug.Clear();

        for (var i = 0; i < MaxNamePlateObjects; i++)
        {
            var plate = addon->NamePlateObjectArray + i;
            var root = (AtkResNode*)plate->RootComponentNode;
            if (root == null || !root->IsVisible() || root->Alpha_2 == 0)
                continue;
            // the object marks the local player's plate itself - a second
            // signal next to the GameObject pointer comparison
            if (plate->IsLocalPlayer)
                _selfPlate = i;
            _plateKind[i] = (byte)plate->NamePlateKind;
            // the game keeps hidden nameplates updated but transparent - the
            // display-name settings (per type, incl. "when targeted" modes)
            // land in VisibilityFlags; when neither name text nor gauge is
            // drawn this plate occludes nothing.
            if ((_plateVisFlags[i] & 0x3) == 0)
            {
                if (verbose)
                    _plateOrderDebug.Append(i).Append("=hid vf")
                        .Append(_plateVisFlags[i].ToString("X"))
                        .Append(_plateIsMinion[i] ? "m " : " ");
                continue;
            }
            // the game promotes the hard-target plate to the front without
            // touching NamePlateObject.Priority - boost it past every regular
            // plate; IsRenderedOnTop sits above even that
            _plateOrder[i] = plate->Priority
                + (_plateIsTarget[i] ? 1000f : 0f)
                + (root->IsRenderedOnTop ? 10000f : 0f);
            var rect = PlateContentRect(plate, true);
            if (rect == Vector4.Zero)
                continue;
            _plateChrome[i] = PlateChromeRect(plate, i);
            // captured icon rects (named + extra image nodes) union into the
            // plate's content bound so they take part in occlusion
            for (var s = 0; s < IconSlots; s++)
            {
                var ir = _plateIconRects[i * IconSlots + s];
                if (ir != Vector4.Zero)
                    rect = UnionRect(rect, ir);
            }
            _plateRects[i] = rect;
            MapTextRect(plate, i, plateStrings);
            if (_plateTextRect[i] == Vector4.Zero || (_plateName[i] == null && _plateTitle[i] == null))
                _plateChrome[i] = rect;
            _plateHasRect[i] = true;
            _plateNameVisible[i] = (_plateVisFlags[i] & 0x1) != 0;
            _plateContentAlpha[i] = EffectiveAlpha(root, (AtkResNode*)plate->NameText);
            if (verbose)
            {
                _plateOrderDebug.Append(i).Append('=').Append(plate->Priority)
                    .Append('/').Append(root->Depth)
                    .Append('/').Append(root->Priority)
                    .Append('/').Append((int)_plateOrder[i])
                    .Append(root->IsRenderedOnTop ? 'T' : '.')
                    .Append(_plateIsTarget[i] ? 't' : '.')
                    .Append(_plateTextRect[i] != Vector4.Zero ? 'g' : '-')
                    .Append(_plateIsMinion[i] ? 'm' : ' ')
                    .Append("vf").Append(_plateVisFlags[i].ToString("X"));
                var nm = _plateName[i];
                if (!string.IsNullOrEmpty(nm))
                    _plateOrderDebug.Append('[').Append(nm.Length > 12 ? nm[..12] : nm).Append(']');
                _plateOrderDebug.Append(' ');
            }
        }
        CaptureTargetCursor(addon);
        if (verbose)
        {
            _plateOrderDebug.Append("cur").Append(_cursorCount);
            for (var s = 0; s < _cursorCount; s++)
            {
                var cr = _cursorRects[s];
                _plateOrderDebug.Append('(').Append((int)cr.X).Append(',')
                    .Append((int)cr.Y).Append('-').Append((int)cr.Z).Append(',')
                    .Append((int)cr.W).Append(')');
            }
            _plateDebug = _plateOrderDebug.ToString();
        }
    }

    /// <summary>
    /// Stores this plate's text block rect plus its rendered title/name strings
    /// so the GPU mask can clip icon pixels against real glyph coverage.
    /// The text node is drawn through the addon's custom bake path, so its
    /// ScreenX/ScreenY stay stale - the real anchor is the accumulated
    /// parent-chain transform, the same thing DrawBaked uses.
    /// </summary>
    private unsafe void MapTextRect(AddonNamePlate.NamePlateObject* plate, int index, byte** strings)
    {
        _plateTitle[index] = _plateName[index] = null;
        _plateNameRect[index] = _plateTitleRect[index] = Vector4.Zero;
        _plateBakeDbg[index] = new Vector4(plate->BakeData.U, plate->BakeData.V,
            plate->BakeData.Width, plate->BakeData.Height);
        _plateBakeTy[index] = plate->BakeData.TextYOffset;
        _plateBakeAlpha[index] = plate->BakeData.Alpha;
        _plateIsBaked[index] = plate->BakeData.IsBaked && plate->BakeData.Width > 0
            && plate->BakeData.Height > 0;
        _plateTextW[index] = plate->TextW;
        _plateTextH[index] = plate->TextH;
        _plateDrawOriginal[index] = plate->DrawOriginalText;
        var nameNode = plate->NameText;
        // the node's own measured text bounds - GetTextDrawSize is the same
        // measurement the renderer performs with the real font set
        ushort tdW = 0, tdH = 0;
        var alignByte = nameNode != null ? nameNode->AlignmentFontType : (byte)0;
        _plateNodeAlpha[index] = nameNode != null
            ? (byte)Math.Min(255, nameNode->TextColor.A * nameNode->Alpha_2 / 255)
            : (byte)255;
        _plateFontSize[index] = nameNode != null ? nameNode->FontSize : 0;
        _plateCharSpacing[index] = nameNode != null ? (sbyte)nameNode->CharSpacing : 0;
        _plateFontType[index] = nameNode != null ? (int)nameNode->FontType : -1;
        _plateTextFlags[index] = nameNode != null ? (int)nameNode->TextFlags : 0;
        _plateLineSpacing[index] = nameNode != null ? nameNode->LineSpacing : (byte)0;
        _plateEdgeAlpha[index] = nameNode != null ? nameNode->EdgeColor.A : (byte)0;
        _plateBgAlpha[index] = nameNode != null ? nameNode->BackgroundColor.A : (byte)0;
        _plateIconXAdj[index] = plate->IconXAdjust;
        _plateIconYAdj[index] = plate->IconYAdjust;
        if (nameNode != null)
        {
            var tm = nameNode->Transform;
            _plateFontInfo[index] = new Vector4(
                *(float*)((byte*)nameNode + 0x16C),
                plate->BakeData.Width,
                plate->BakeData.Height,
                MathF.Sqrt(tm.M21 * tm.M21 + tm.M22 * tm.M22));
        }
        else
        {
            _plateFontInfo[index] = Vector4.Zero;
        }
        Vector4 lineBytes = Vector4.Zero;
        int[]? ends = null;
        long payloadHash = 0;
        if (nameNode != null)
        {
            var span = nameNode->NodeText.AsSpan();
            unchecked
            {
                for (var k = 0; k < span.Length; k++)
                    payloadHash = payloadHash * 31 + span[k];
            }
            payloadHash = payloadHash * 31 + span.Length;
            _plateGlyphSeq[index] = DecodeGlyphSequence(span, out lineBytes, out ends,
                out var glyphAlpha);
            _plateGlyphAlpha[index] = glyphAlpha;
        }
        else
        {
            _plateGlyphSeq[index] = null;
            _plateGlyphAlpha[index] = null;
        }
        _plateGlyphEnds[index] = ends;
        _plateLineBytes[index] = nameNode != null ? lineBytes : Vector4.Zero;
        _plateLineSize[index * 2] = Vector2.Zero;
        _plateLineSize[index * 2 + 1] = Vector2.Zero;
        payloadHash = (payloadHash * 31 + _plateCharSpacing[index]) * 31
            + _plateFontSize[index] * 31 + _plateFontType[index] * 31;

        if (nameNode != null)
        {
            nameNode->GetTextDrawSize(&tdW, &tdH, null, 0, -1, true);
            var np = nameNode->NodeText.StringPtr;
            var lb = _plateLineBytes[index];
            MeasureLine(nameNode, np, (int)lb.X, (int)lb.Y, index * 2);
            if (lb.Z >= 0)
                MeasureLine(nameNode, np, (int)lb.Z, (int)lb.W, index * 2 + 1);
            // the per-element cell measures only change with the payload or
            // the node's text setup - skip the O(n) native calls otherwise.
            // they also freeze the line width of their build tick, and every
            // ushort of suffix-measure rounding amplifies with the rescale
            // ratio - so rebuild once the live width drifts visibly away
            var cw0 = _plateCellsW[index * 2];
            var cw1 = _plateCellsW[index * 2 + 1];
            var w0 = _plateLineSize[index * 2].X;
            var w1 = _plateLineSize[index * 2 + 1].X;
            var cellsDrift = _plateCells[index] != null
                && ((cw0 > 1f && w0 > 1f && MathF.Abs(w0 / cw0 - 1f) > 0.03f)
                    || (cw1 > 1f && w1 > 1f && MathF.Abs(w1 / cw1 - 1f) > 0.03f));
            if (payloadHash != _plateMeasureHash[index] || _plateCells[index] == null || cellsDrift)
            {
                _plateMeasureHash[index] = payloadHash;
                _platePayload[index] = nameNode->NodeText.AsSpan().ToArray();
                BuildPenCells(nameNode, np, index);
            }
        }
        else
        {
            _plateMeasureHash[index] = 0;
            _plateCells[index] = null;
            _plateCellsW[index * 2] = 0f;
            _plateCellsW[index * 2 + 1] = 0f;
            _platePayload[index] = null;
        }
        _plateMeasured[index] = new Vector2(tdW, tdH);
        _plateAlignByte[index] = alignByte;
        var arrayIndex = _plateArrayIndex[index];
        if (strings != null && arrayIndex >= 0)
        {
            var name = ReadPlateString(strings[arrayIndex]);
            if (!string.IsNullOrEmpty(name))
            {
                var fc = ReadPlateString(strings[100 + arrayIndex]);
                if (!string.IsNullOrEmpty(fc))
                    name = fc.StartsWith('«') ? $"{name} {fc}" : $"{name} «{fc}»";
                _plateName[index] = name;
            }
            _plateTitle[index] = ReadPlateString(strings[50 + arrayIndex]);
        }
        ushort titleW = 0, titleH = 0;
        if (nameNode != null && _plateTitle[index] != null
            && strings != null && arrayIndex >= 0)
            nameNode->GetTextDrawSize(&titleW, &titleH, strings[50 + arrayIndex], 0, -1, true);
        _plateTitleSize[index] = new Vector2(titleW, titleH);
        RefreshNodeRects(plate, index);
        ComputePlateRects(index);
    }

    private unsafe void RefreshNodeRects(AddonNamePlate.NamePlateObject* plate, int index)
    {
        _plateNodeRect[index] = NodeAccumulatedRect((AtkResNode*)plate->NameText, index);
        _plateContainerRect[index] = NodeScreenRect(plate->NameContainer);
        _plateCollisionRect[index] = NodeScreenRect((AtkResNode*)plate->NameplateCollision, true);
        var stn = (AtkResNode*)plate->NameText;
        // the plate's accumulated render scale (size option × distance) -
        // the text node carries an extra 0.5 counter-scale for its 2x bake,
        // so prefer the collision node and halve the text node as fallback
        var colScale = NodeTransformScale((AtkResNode*)plate->NameplateCollision);
        if (colScale.X > 0.01f)
            _plateRenderScale[index] = colScale.X;
        else
        {
            var txtScale = NodeTransformScale(stn).X;
            _plateRenderScale[index] = txtScale > 0.01f ? txtScale * 2f : 1f;
        }
        // block anchor semantics are bottom-center - ScreenY alone is the
        // node's top edge, so hang the anchor at its bottom. Read every frame:
        // ScreenX/Y only become current once the ATK draw ran. The drawn size
        // is Width/Height times the transform matrix magnitude - GetScaleX/Y
        // is the plain local scale (stays ~0.5) while the transform carries
        // the distance resample, so far plates drifted right/down before.
        var tsx = 0f;
        var tsy = 0f;
        if (stn != null)
        {
            var tm = stn->Transform;
            tsx = MathF.Sqrt(tm.M11 * tm.M11 + tm.M12 * tm.M12);
            tsy = MathF.Sqrt(tm.M21 * tm.M21 + tm.M22 * tm.M22);
        }
        _plateSBlockDrawn[index] = _plateSBlock[index];
        _plateSBlock[index] = stn != null && stn->Width > 0 && tsx > 0f
            ? new Vector2(stn->ScreenX + stn->Width * tsx * 0.5f,
                stn->ScreenY + stn->Height * (tsy > 0f ? tsy : tsx))
            : (_plateCollisionRect[index] != Vector4.Zero
                ? new Vector2((_plateCollisionRect[index].X + _plateCollisionRect[index].Z) * 0.5f,
                    _plateCollisionRect[index].W)
                : Vector2.Zero);
        var text = _plateNodeRect[index];
        if (_plateContainerRect[index] != Vector4.Zero)
            text = text == Vector4.Zero ? _plateContainerRect[index] : UnionRect(text, _plateContainerRect[index]);
        if (_plateCollisionRect[index] != Vector4.Zero)
            text = text == Vector4.Zero ? _plateCollisionRect[index] : UnionRect(text, _plateCollisionRect[index]);
        _plateTextRect[index] = text;
    }

    /// <summary>
    /// Recomputes the plate's ink/name/title rects from the game's own plate
    /// position and the cached text measurements. Runs every frame - the number
    /// array position tracks the plate each frame while the camera moves.
    /// </summary>
    private void ComputePlateRects(int index)
    {
        // every readable position source - node ScreenX, number array, and
        // even the state inside the game's own draw call - holds the CURRENT
        // update's data, while the title on screen was drawn from the
        // previous update's. So the previous frame's anchor is primary; the
        // draw-time capture is the fallback for plates without history.
        var game = _plateGameRect[index];
        var drawn = _plateDrawnState[index];
        var hasDrawn = drawn != Vector4.Zero
            && _plateDrawnFrame[index] >= _drawFrameCounter - 1;
        var sb = _plateSBlockDrawn[index] != Vector2.Zero
            ? _plateSBlockDrawn[index]
            : (hasDrawn ? new Vector2(drawn.X, drawn.Y) : _plateSBlock[index]);
        var eff = sb != Vector2.Zero
            ? new Vector4(sb.X, sb.Y, game.Z, game.W)
            : game;
        var beta = AnchorExtrapolation?.Invoke() ?? 0f;
        if (MathF.Abs(beta) > 0.001f && eff != Vector4.Zero)
        {
            var ro = _plateRenderOff[index];
            if (ro != Vector2.Zero)
            {
                var dx = Math.Clamp(ro.X * beta, -40f, 40f);
                var dy = Math.Clamp(ro.Y * beta, -40f, 40f);
                eff = new Vector4(eff.X + dx, eff.Y + dy, eff.Z, eff.W);
            }
        }
        var plateTune = Tune(index);
        _plateEffAnchor[index] = eff != Vector4.Zero ? new Vector2(eff.X, eff.Y) : Vector2.Zero;
        var m = _plateMeasured[index];
        var nodeR = _plateNodeRect[index];
        var nodeH = nodeR != Vector4.Zero ? nodeR.W - nodeR.Y : m.Y;
        var nodeTop = nodeR != Vector4.Zero ? nodeR.Y : eff.Y - m.Y;
        var nodeCenterY = eff != Vector4.Zero
            ? eff.Y - nodeH * 0.5f
            : nodeTop + nodeH * 0.5f;
        // PrepareNode's TextDrawArea is the node's own rect at draw time - but
        // like every other current-frame source it runs one update ahead of the
        // text on screen, so it only fills in where no drawn anchor exists
        var area = _plateAreaRect[index];
        var areaCx = (area.X + area.Z) * 0.5f;
        var areaOk = area != Vector4.Zero && _plateAreaFrame[index] >= _drawFrameCounter - 1
            && area.W - area.Y > 1f && area.W - area.Y < 400f
            && MathF.Abs(areaCx - (eff != Vector4.Zero ? eff.X : (nodeR.X + nodeR.Z) * 0.5f)) < 120f;
        var drawTop = nodeTop;
        var drawBottom = eff != Vector4.Zero ? eff.Y
            : (areaOk ? area.W : nodeTop + nodeH);
        var drawH = MathF.Max(drawBottom - drawTop, 1f);
        // name + title are separate lines inside the same node text; a prefix
        // title renders above the name, a suffix title below it
        var titleSize = _plateTitleSize[index];
        var prefix = ((int)game.W & 0x1) != 0;
        var titleLineH = titleSize.Y > 0 ? titleSize.Y : m.Y * 0.45f;
        var nameLineH = _plateLineSize[index * 2 + (prefix ? 1 : 0)].Y;
        var lastLineH = _plateLineSize[index * 2 + 1].Y;
        var two = _plateName[index] != null && _plateTitle[index] != null
            && m.Y > titleLineH + 2f;
        // direct draws stack line origins at the node's own LineSpacing -
        // authored in node units and scaled by the plate transform it lands
        // between the ScaledSizeY and LineHeight guesses (42u × 0.48 ≈ 20px
        // where 18 sat too high and 24 too low). The last line's slot bottom
        // pins to the draw area bottom because the node is bottom-aligned
        var slotH = _plateLineStep[index * 2 + 1] > 0.5f
            ? _plateLineStep[index * 2 + 1]
            : Math.Max(lastLineH > 0 ? lastLineH : m.Y, 1f);
        var lineSpacing = _plateLineSpacing[index];
        var nodeScaleY = _plateNodeScale[index].Y;
        // GetTextDrawSize is the game's own measurement of the rendered text
        // block - its height already carries the real line packing plus
        // edge/descender overhang, so it anchors the block where the
        // LineSpacing reconstruction kept missing by 3-5px. The last line's
        // slot bottom pins to the block bottom (a7 bottom alignment), which
        // makes the pitch whatever the block leaves above that slot
        var blockH = m.Y > 1f
            ? m.Y
            : (two && lineSpacing > 0 && nodeScaleY > 0.01f
                ? lineSpacing * nodeScaleY * 2f
                : (two ? slotH * 2f : slotH));
        var pitch = two
            ? Math.Max(blockH - slotH, 1f)
            : slotH;
        pitch *= Math.Clamp(plateTune.PitchMul, 0.5f, 1.5f);
        // the block anchor depends on the game's render path: a direct text
        // draw (ib0 / draw-original) honors the node's alignment against the
        // draw area, while a bake blit centers the whole block on the node
        var bkD = _plateBakeDbg[index];
        var baked = _plateIsBaked[index] && !_plateDrawOriginal[index];
        float top;
        if (baked && bkD.W > 0)
        {
            // bake blit - the quad centers on the node; TextYOffset pins the
            // text's top edge inside the bake to the exact screen position
            var bakeS = Math.Clamp(game.Z / 100f, 0.05f, 4f) * 0.5f;
            top = nodeCenterY + (Math.Clamp(_plateBakeTy[index], 0f, bkD.W * 0.5f) - bkD.W * 0.5f) * bakeS;
        }
        else if (baked)
        {
            top = nodeCenterY - m.Y * 0.5f;
        }
        else
        {
            var align = ((areaOk ? _plateAreaAlign[index] : _plateAlignByte[index]) & 0xF);
            top = align <= 2 ? drawTop
                : align <= 5 ? drawTop + (drawH - blockH) * 0.5f
                : drawBottom - blockH;
        }
        _plateDbgGeom[index] = new Vector4(pitch, blockH, top, drawBottom);
        var bounds = m.X > 0 && m.Y > 0 && eff != Vector4.Zero
            ? new Vector4(eff.X - m.X * 0.5f, top, eff.X + m.X * 0.5f, top + m.Y)
            : (nodeR != Vector4.Zero ? nodeR : _plateContainerRect[index]);
        _plateInkRect[index] = bounds;
        if (bounds == Vector4.Zero)
            return;
        // each line is centered on the plate anchor - the title band spans only
        // its own measured width, not the whole block
        var cx = eff != Vector4.Zero ? eff.X : (bounds.X + bounds.Z) * 0.5f;
        var titleHalfW = MathF.Min(
            titleSize.X > 0 ? titleSize.X * 0.5f : (bounds.Z - bounds.X) * 0.5f,
            (bounds.Z - bounds.X) * 0.5f);
        // the measured box excludes the font's edge outline and descender
        // overhang - inflate each band so native glyph pixels can't survive
        var pad = Math.Clamp(titleLineH * 0.2f, 2f, 8f);
        // a narrower name line centers under the block - its band must use its
        // own width, not the widest line's span, or glyphs start too far left
        var nameHalfW = _plateLineSize[index * 2 + (prefix ? 1 : 0)].X > 0
            ? MathF.Min(_plateLineSize[index * 2 + (prefix ? 1 : 0)].X * 0.5f,
                (bounds.Z - bounds.X) * 0.5f)
            : (bounds.Z - bounds.X) * 0.5f;
        if (nameLineH <= 0)
            nameLineH = Math.Max(m.Y - titleLineH, titleLineH);
        var firstY = top;
        Vector4 nameBand, titleBand;
        if (two)
        {
            if (prefix)
            {
                titleBand = new Vector4(cx - titleHalfW, firstY, cx + titleHalfW, firstY + titleLineH);
                nameBand = new Vector4(cx - nameHalfW, firstY + pitch, cx + nameHalfW,
                    firstY + pitch + nameLineH);
            }
            else
            {
                nameBand = new Vector4(cx - nameHalfW, firstY, cx + nameHalfW,
                    firstY + nameLineH);
                titleBand = new Vector4(cx - titleHalfW, firstY + pitch, cx + titleHalfW,
                    firstY + pitch + titleLineH);
            }
        }
        else if (_plateName[index] != null)
        {
            nameBand = new Vector4(bounds.X, firstY, bounds.Z, firstY + Math.Max(m.Y, nameLineH));
            titleBand = Vector4.Zero;
        }
        else
        {
            nameBand = Vector4.Zero;
            titleBand = new Vector4(cx - titleHalfW, firstY, cx + titleHalfW, firstY + titleLineH);
        }
        // per-line cutout nudge - the name band shifts on every plate, the
        // title band only exists on plates with a title line
        var nOff = plateTune.NameOffset;
        if (nameBand != Vector4.Zero
            && (MathF.Abs(nOff.X) > 0.001f || MathF.Abs(nOff.Y) > 0.001f))
        {
            nameBand = new Vector4(nameBand.X + nOff.X, nameBand.Y + nOff.Y,
                nameBand.Z + nOff.X, nameBand.W + nOff.Y);
        }
        var tOff = plateTune.TitleOffset;
        if (titleBand != Vector4.Zero
            && (MathF.Abs(tOff.X) > 0.001f || MathF.Abs(tOff.Y) > 0.001f))
        {
            titleBand = new Vector4(titleBand.X + tOff.X, titleBand.Y + tOff.Y,
                titleBand.Z + tOff.X, titleBand.W + tOff.Y);
        }
        _plateNameLine[index] = nameBand;
        _plateTitleLine[index] = titleBand;
        _plateNameRect[index] = Inflate(nameBand, pad);
        _plateTitleRect[index] = Inflate(titleBand, pad);
    }

    /// <summary>Logs one line whenever a plate's quantized geometry signature
    /// changes - node scale, bake alpha, measured line width and the plate
    /// scale all step at distance buckets, so each line is one step boundary
    /// with the owner distance. Reveals whether the mask inputs jump while
    /// the visible text moves continuously.</summary>
    private void CheckGeoStep(int i)
    {
        var sig = new Vector4(
            MathF.Round(_plateNodeScale[i].X * 1000f),
            _plateBakeAlpha[i],
            MathF.Round(_plateLineSize[i * 2].X * 4f)
                + MathF.Round(_plateLineSize[i * 2 + 1].X * 4f),
            MathF.Round(_plateGameRect[i].Z * 20f));
        var prev = _plateGeoSig[i];
        if (sig == prev)
            return;
        _plateGeoSig[i] = sig;
        if (prev == Vector4.Zero)
            return;
        var now = Environment.TickCount64;
        if (now < _geoLogTicks)
            return;
        _geoLogTicks = now + 200;
        var dist = _plateObjDist[i];
        var nb = _plateNameLine[i];
        var tb = _plateTitleLine[i];
        _logger.LogDebug(
            "[Nameplate] geo p{idx} d{dist}y ns {nsP:0.000}->{nsN:0.000} ba {baP}->{baN} "
            + "lw {lwP:0.0}->{lwN:0.0} pz {pzP:0.0}->{pzN:0.0} band n[{nbX:0}-{nbZ:0},{nbY:0}-{nbW:0}] "
            + "t[{tbX:0}-{tbZ:0},{tbY:0}-{tbW:0}]",
            i, dist.ToString("0.00"),
            prev.X / 1000f, sig.X / 1000f,
            (int)prev.Y, (int)sig.Y,
            prev.Z / 4f, sig.Z / 4f,
            prev.W / 20f, sig.W / 20f,
            nb.X, nb.Z, nb.Y, nb.W, tb.X, tb.Z, tb.Y, tb.W);
    }

    /// <summary>Logs every nameplate scale bucket the node transform steps
    /// into - old/new scale, the ratio, and the object's distance at the
    /// boundary, so the resample distances can be walked out live instead of
    /// being guessed from still screenshots.</summary>
    private void CheckScaleStep(int i)
    {
        var ns = MathF.Round(_plateNodeScale[i].Y * 1000f) / 1000f;
        var prev = _plateScaleSig[i];
        if (MathF.Abs(ns - prev) < 0.0005f)
            return;
        _plateScaleSig[i] = ns;
        if (prev <= 0.0005f)
            return;
        var dist = _plateObjDist[i];
        var nb = _plateNameLine[i];
        var tb = _plateTitleLine[i];
        _logger.LogDebug(
            "[Nameplate] scale p{idx} ns {old:0.000}->{new:0.000} x{ratio:0.00} "
            + "nsx{nsx:0.000} d{dist}y pz{pz:0.0} slotH{sh:0.0} blk{m:0.0} ls{ls} "
            + "top{top:0.0} n[{nbY:0}-{nbW:0}] t[{tbY:0}-{tbW:0}] [{name}]",
            i, prev, ns, ns / prev, _plateNodeScale[i].X, dist.ToString("0.00"),
            _plateGameRect[i].Z, _plateLineStep[i * 2 + 1],
            _plateMeasured[i].Y, _plateLineSpacing[i], _plateInkRect[i].Y,
            nb.Y, nb.W, tb.Y, tb.W, _plateName[i]);
    }

    private static Vector4 Inflate(Vector4 r, float pad)
        => r == Vector4.Zero ? r : new Vector4(r.X - pad, r.Y - pad, r.Z + pad, r.W + pad);

    /// <summary>
    /// The plate's text-block bottom-center - the number array position when
    /// available, else derived from the accumulated ink rect.
    /// </summary>
    /// <summary>The plate's accumulated render scale - captured from the
    /// collision node's transform, which folds the nameplate-size option and
    /// the distance resample (~1.0 at default size up close).</summary>
    public float PlateScale(int index)
    {
        if (index is < 0 or >= MaxNamePlateObjects)
            return 1f;
        var s = _plateRenderScale[index];
        return s is > 0.05f and < 8f ? s : 1f;
    }

    public Vector2 BlockAnchor(int index)
    {
        var eff = _plateEffAnchor[index];
        if (eff != Vector2.Zero)
            return eff;
        var game = _plateGameRect[index];
        if (game != Vector4.Zero)
            return new Vector2(game.X, game.Y);
        var ink = _plateInkRect[index];
        return ink != Vector4.Zero
            ? new Vector2((ink.X + ink.Z) * 0.5f, ink.W)
            : Vector2.Zero;
    }

    /// <summary>
    /// Re-reads the game-computed plate positions (number array X/Y/Scale/DrawFlags)
    /// and recomputes every plate's rects - positions track the camera per frame,
    /// so they can't wait for the next OnDataUpdate pass.
    /// </summary>
    public unsafe void Refresh()
    {
        var addonPtr = _gameGui.GetAddonByName("NamePlate", 1);
        if (addonPtr.IsNull)
            return;
        var addon = (AddonNamePlate*)addonPtr.Address;
        if (addon->NamePlateObjectArray == null)
            return;
        _namePlateAddon = addon;
        EnsureBakeDrawHook();
        _drawFrameCounter++;
        var bakeTexture = addon->BakePlate.Texture;
        _bakeSrv = bakeTexture != null ? (nint)bakeTexture->D3D11ShaderResourceView : 0;
        _bakeSize = bakeTexture != null
            ? new Vector2(bakeTexture->ActualWidth, bakeTexture->ActualHeight)
            : Vector2.Zero;
        var stage = AtkStage.Instance();
        var numbers = stage != null ? stage->GetNumberArrayData(NumberArrayType.NamePlate) : null;
        var numberStruct = numbers != null ? (NamePlateNumberArray*)numbers->IntArray : null;
        if (numberStruct != null)
        {
            var active = Math.Min(numberStruct->ActiveNamePlateCount, MaxNamePlateObjects);
            for (var i = 0; i < active; i++)
            {
                ref var data = ref numberStruct->ObjectData[i];
                var objIndex = data.NamePlateObjectIndex;
                if (objIndex is >= 0 and < MaxNamePlateObjects)
                {
                    _platePrevGame[objIndex] = _plateDrawnGame[objIndex];
                    _plateDrawnGame[objIndex] = new Vector4(data.X, data.Y, data.Scale, data.DrawFlags);
                    _plateGameRect[objIndex] = _plateDrawnGame[objIndex];
                    _plateVisFlags[objIndex] = data.VisibilityFlags;
                    var gp = _platePrevGame[objIndex];
                    _plateRenderOff[objIndex] = gp != Vector4.Zero
                        ? new Vector2(data.X - gp.X, data.Y - gp.Y)
                        : Vector2.Zero;
                }
            }
        }
        for (var i = 0; i < MaxNamePlateObjects; i++)
        {
            if (!_plateHasRect[i])
                continue;
            var plate = addon->NamePlateObjectArray + i;
            var root = (AtkResNode*)plate->RootComponentNode;
            if (root == null || !root->IsVisible() || root->Alpha_2 == 0)
            {
                _plateHasRect[i] = false;
                continue;
            }
            _plateNameVisible[i] = (_plateVisFlags[i] & 0x1) != 0;
            _plateContentAlpha[i] = EffectiveAlpha(root, (AtkResNode*)plate->NameText);
            RefreshNodeRects(plate, i);
            ComputePlateRects(i);
            if (DebugLog && _plateIsPlayer[i])
            {
                CheckGeoStep(i);
                CheckScaleStep(i);
            }
            if (i == DiagPlate)
                _plateChainDump[i] = DumpNodeChain((AtkResNode*)plate->NameText);
        }
    }

    /// <summary>
    /// Recomputes a node's true screen rect from ScreenX/Y and the transform
    /// matrix magnitude - the ATK draw applies the accumulated 2x2 transform,
    /// so plain ScaleX accumulation misses the distance resample baked into
    /// the matrix (verified against the number-array anchor position).
    /// </summary>
    /// <summary>
    /// The node's accumulated draw scale - |M11,M12| / |M21,M22| of the
    /// Transform matrix, which already folds every ancestor's scale (the
    /// nameplate-size option and distance resample live there).
    /// </summary>
    /// <summary>
    /// The plate's effective alpha for our own overlays - the root component's
    /// alpha folds plate fade transitions, the name node's alpha folds
    /// text-specific fades; the product is what the visible text draws at.
    /// </summary>
    private static unsafe float EffectiveAlpha(AtkResNode* root, AtkResNode* nameNode)
    {
        var a = root != null ? root->Alpha_2 : 0;
        if (a == 0 || nameNode == null)
            return a / 255f;
        // groups between the component root and the text node fade or hide it
        // independently - the node's own flags alone overstate visibility
        var chain = AncestorAlpha(nameNode);
        if (chain <= 0f)
            return 0f;
        return a * nameNode->Alpha_2 * chain / 65025f;
    }

    private static unsafe Vector2 NodeTransformScale(AtkResNode* node)
    {
        if (node == null)
            return Vector2.Zero;
        var tm = node->Transform;
        return new Vector2(
            MathF.Sqrt(tm.M11 * tm.M11 + tm.M12 * tm.M12),
            MathF.Sqrt(tm.M21 * tm.M21 + tm.M22 * tm.M22));
    }

    /// <summary>The scale the uld system loads part textures at - nine-grid
    /// offsets are stored in those texel units, so dividing by it returns
    /// them to the authored units the node layout uses.</summary>
    private static unsafe float HudTextureScale()
    {
        var rapture = RaptureAtkModule.Instance();
        if (rapture == null)
            return 1f;
        var s = rapture->AtkModule.AtkTextureResourceManager.DefaultTextureScale;
        return s is >= 1 and <= 4 ? s : 1f;
    }

    private unsafe Vector4 NodeAccumulatedRect(AtkResNode* node, int index)
    {
        _plateNodePos[index] = Vector2.Zero;
        _plateNodeScale[index] = Vector2.One;
        if (node == null || node->Width == 0 || node->Height == 0
            || !node->IsVisible() || node->Alpha_2 == 0)
            return Vector4.Zero;
        var tm = node->Transform;
        var sx = MathF.Sqrt(tm.M11 * tm.M11 + tm.M12 * tm.M12);
        var sy = MathF.Sqrt(tm.M21 * tm.M21 + tm.M22 * tm.M22);
        if (sx <= 0f)
            sx = node->GetScaleX();
        if (sy <= 0f)
            sy = node->GetScaleY();
        _plateNodePos[index] = new Vector2(node->ScreenX, node->ScreenY);
        _plateNodeScale[index] = new Vector2(sx, sy);
        return new Vector4(node->ScreenX, node->ScreenY,
            node->ScreenX + node->Width * sx, node->ScreenY + node->Height * sy);
    }

    private static unsafe Vector4 NodeScreenRect(AtkResNode* node, bool skipVisibility = false)
    {
        if (node == null || node->Width == 0 || node->Height == 0)
            return Vector4.Zero;
        if (!skipVisibility && (!node->IsVisible() || node->Alpha_2 == 0))
            return Vector4.Zero;
        return new Vector4(node->ScreenX, node->ScreenY,
            node->ScreenX + node->Width * node->GetScaleX(),
            node->ScreenY + node->Height * node->GetScaleY());
    }

    private static Vector4 UnionRect(Vector4 a, Vector4 b)
        => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Z, b.Z), MathF.Max(a.W, b.W));

    /// <summary>
    /// Dumps the node's local state and every ancestor's X/Y + draw transform
    /// (m00,m01,m10,m11) - the ATK draw applies the parent chain's matrices, so
    /// the true screen transform can't be read from X/Y/Scale alone.
    /// </summary>
    private static unsafe string DumpNodeChain(AtkResNode* node)
    {
        if (node == null)
            return "-";
        var sb = new System.Text.StringBuilder(160);
        sb.Append('[').Append(node->X.ToString("0.#")).Append(',')
            .Append(node->Y.ToString("0.#")).Append(',')
            .Append(node->Width).Append('x').Append(node->Height).Append(',')
            .Append(node->ScreenX.ToString("0.#")).Append(',')
            .Append(node->ScreenY.ToString("0.#")).Append('|')
            .Append(node->Transform.M11.ToString("0.###")).Append(',')
            .Append(node->Transform.M12.ToString("0.###")).Append(',')
            .Append(node->Transform.M21.ToString("0.###")).Append(',')
            .Append(node->Transform.M22.ToString("0.###")).Append(']');
        for (var p = node->ParentNode; p != null && sb.Length < 240; p = p->ParentNode)
        {
            sb.Append('<').Append(p->X.ToString("0.#")).Append(',')
                .Append(p->Y.ToString("0.#")).Append('|')
                .Append(p->Transform.M11.ToString("0.###")).Append(',')
                .Append(p->Transform.M12.ToString("0.###")).Append(',')
                .Append(p->Transform.M21.ToString("0.###")).Append(',')
                .Append(p->Transform.M22.ToString("0.###")).Append('>');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Screen bounds of the pixels a plate can actually cover - name block,
    /// native icons and gauges, no empty container space.
    /// </summary>
    private static unsafe Vector4 PlateContentRect(AddonNamePlate.NamePlateObject* plate, bool includeText)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        if (includeText)
        {
            UnionNodeRect(ref min, ref max, (AtkResNode*)plate->NameplateCollision, false);
            UnionNodeRect(ref min, ref max, plate->NameContainer);
            UnionNodeRect(ref min, ref max, (AtkResNode*)plate->NameText);
        }
        UnionNodeRect(ref min, ref max, (AtkResNode*)plate->NameIcon);
        UnionNodeRect(ref min, ref max, (AtkResNode*)plate->MarkerIcon);
        UnionNodeRect(ref min, ref max, (AtkResNode*)plate->GaugeContainer);
        UnionNodeRect(ref min, ref max, (AtkResNode*)plate->GaugeFill);
        return min.X > max.X ? Vector4.Zero : new Vector4(min.X, min.Y, max.X, max.Y);
    }

    private static unsafe void UnionNodeRect(ref Vector2 min, ref Vector2 max, AtkResNode* node, bool mustBeVisible = true)
    {
        if (node == null || node->Width == 0 || node->Height == 0)
            return;
        if (mustBeVisible && (!node->IsVisible() || node->Alpha_2 == 0))
            return;
        var s = NodeTransformScale(node);
        var sx = s.X > 0f ? s.X : node->GetScaleX();
        var sy = s.Y > 0f ? s.Y : node->GetScaleY();
        min = Vector2.Min(min, new Vector2(node->ScreenX, node->ScreenY));
        max = Vector2.Max(max, new Vector2(node->ScreenX + node->Width * sx,
            node->ScreenY + node->Height * sy));
    }

    /// <summary>Non-text chrome rect - the gauge parts stay unioned, the icon
    /// nodes are captured individually so the mask pass can cut them by their
    /// real texture pixels instead of the node bounds.</summary>
    private unsafe Vector4 PlateChromeRect(AddonNamePlate.NamePlateObject* plate, int index)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        UnionNodeRect(ref min, ref max, (AtkResNode*)plate->GaugeContainer);
        UnionNodeRect(ref min, ref max, (AtkResNode*)plate->GaugeFill);
        CaptureIconNode(plate->NameIcon, index, 0);
        CaptureIconNode(plate->MarkerIcon, index, 1);
        CaptureExtraIconNodes(plate, index);
        UnionAuxTextNodes(plate, ref min, ref max);
        return min.X > max.X ? Vector4.Zero : new Vector4(min.X, min.Y, max.X, max.Y);
    }

    /// <summary>Snapshots one nameplate image-node icon - screen rect, the
    /// texture it samples and the uld part's texel rect inside it.</summary>
    private unsafe void CaptureIconNode(AtkImageNode* node, int plate, int slot)
    {
        var idx = plate * IconSlots + slot;
        _plateIconRects[idx] = Vector4.Zero;
        _plateIconSrv[idx] = 0;
        _plateIconUv[idx] = Vector4.Zero;
        _plateIconWrap[idx] = 0;
        _plateIconPartCnt[idx] = 0;
        _plateIconId[idx] = 0;
        _plateIconAlpha[idx] = 0f;
        // IsVisible() and Alpha_2 alone are not enough - the addon also blanks
        // icon nodes via IsDrawDisabled, Color.A or a hidden parent group
        // while leaving them loaded and visible-flagged (transparent status
        // placeholders)
        var effA = node != null
            ? node->Alpha_2 * (node->Color.A / 255f) * AncestorAlpha((AtkResNode*)node)
            : 0f;
        if (node == null || node->Width == 0 || node->Height == 0
            || !node->IsVisible() || node->Alpha_2 == 0
            || ((AtkResNode*)node)->IsDrawDisabled || effA <= 0f)
        {
            if (slot < 2 && node != null)
                LogIconFail(plate, slot, node, null, null,
                    node->Width == 0 || node->Height == 0 ? "size"
                    : !node->IsVisible() ? "invis"
                    : ((AtkResNode*)node)->IsDrawDisabled ? "drawdis" : "alpha");
            return;
        }
        var srv = ResolveIconNodeSrv(node, plate, slot, out var uv, out var texKey);
        if (srv == 0)
            return;
        // the draw applies the transform matrix - the plain ScaleX/Y field
        // misses the size option and distance resample folded into it
        var s = NodeTransformScale((AtkResNode*)node);
        var sx = s.X > 0f ? s.X : node->GetScaleX();
        var sy = s.Y > 0f ? s.Y : node->GetScaleY();
        var w = (float)node->Width;
        var h = (float)node->Height;
        // non-stretch wrap modes draw the part at its authored size from the
        // node origin - the small status-icon variant renders inside the
        // same node bounds the large variant fills
        if (node->WrapMode != 1)
        {
            w = MathF.Min(w, MathF.Abs(uv.Z - uv.X));
            h = MathF.Min(h, MathF.Abs(uv.W - uv.Y));
        }
        w *= IconMaskScale;
        h *= IconMaskScale;
        _plateIconRects[idx] = new Vector4(node->ScreenX, node->ScreenY,
            node->ScreenX + w * sx, node->ScreenY + h * sy);
        _plateIconSrv[idx] = srv;
        _plateIconTexKey[idx] = texKey;
        _plateIconUv[idx] = uv;
        _plateIconWrap[idx] = node->WrapMode;
        _plateIconAlpha[idx] = effA / 255f;
    }

    /// <summary>Resolves an image node's drawn part: the uld asset's kernel
    /// texture SRV plus the part's texel rect with flip flags folded in -
    /// <paramref name="uv"/> is in base-resolution texels; uld rects are
    /// authored against the base sheet and only scaled at emission once the
    /// loaded texture's real size is known.</summary>
    private unsafe nint ResolveIconNodeSrv(AtkImageNode* node, int plate, int slot,
        out Vector4 uv, out uint texKey)
    {
        uv = Vector4.Zero;
        texKey = 0;
        var parts = node->PartsList;
        if (parts == null || node->PartId >= parts->PartCount)
        {
            LogIconFail(plate, slot, node, parts, null, "parts");
            return 0;
        }
        _plateIconPartCnt[plate * IconSlots + slot] =
            (byte)Math.Min(parts->PartCount, (uint)byte.MaxValue);
        // the uld parts list carries an alternate icon set after the normal
        // parts; the draw adds the addon's AlternatePartId to every node's
        // PartId unless DrawFlags 0x100 opts the plate out - the job-icon
        // layout lives in that alternate set, so the plain PartId reads the
        // wrong (usually larger) icon
        var partId = node->PartId;
        var drawFlags = (int)_plateGameRect[plate].W;
        if ((drawFlags & 0x100) == 0 && _alternatePartId > 0
            && partId + _alternatePartId < parts->PartCount)
            partId += _alternatePartId;
        var part = parts->Parts + partId;
        var asset = part->UldAsset;
        if (asset == null)
        {
            LogIconFail(plate, slot, node, parts, part, "asset");
            return 0;
        }
        var tex = &asset->AtkTexture;
        var res = tex->TextureType == TextureType.Resource ? tex->Resource : null;
        var fileName = res != null && res->TexFileResourceHandle != null
            ? StdStringText(&res->TexFileResourceHandle->FileName)
            : null;
        var iconId = res != null ? res->IconId : 0u;
        _plateIconId[plate * IconSlots + slot] = iconId;
        if (iconId == StatusPlaceholderIconId)
        {
            // every plate carries a whole row of these never-drawn badge nodes
            // (nid 50002, 50005, …) - acknowledge each once, then stay silent
            if (DebugLog && _iconPlaceholderSeen.Add(node->NodeId))
                _logger.LogDebug("[Nameplate] ic{slot} p{plate} nid{nid} placeholder ic{iconId} suppressed",
                    slot, plate, node->NodeId, iconId);
            return 0;
        }
        var kernel = tex->TextureType == TextureType.KernelTexture
            ? tex->KernelTexture
            : res != null
                ? res->KernelTextureObject
                : null;
        if (kernel == null || kernel->D3D11ShaderResourceView == null)
        {
            LogIconFail(plate, slot, node, parts, part, "kernel");
            return 0;
        }
        uv = PartUv(part, node);
        texKey = res != null ? res->TexPathHash ^ res->IconId : 0;
        LogIconResolve(plate, slot, node, parts, asset, (int)node->PartId,
            partId, (nint)kernel->D3D11ShaderResourceView, drawFlags, fileName, iconId);
        return (nint)kernel->D3D11ShaderResourceView;
    }

    /// <summary>A uld part's texel rect with the node's flip flags folded in,
    /// in the parts list's authored (base) texels.</summary>
    private static unsafe Vector4 PartUv(AtkUldPart* part, AtkImageNode* node)
    {
        var u0 = (float)part->U;
        var v0 = (float)part->V;
        var u1 = u0 + part->Width;
        var v1 = v0 + part->Height;
        if ((node->Flags & ImageNodeFlags.FlipH) == ImageNodeFlags.FlipH)
            (u0, u1) = (u1, u0);
        if ((node->Flags & ImageNodeFlags.FlipV) == ImageNodeFlags.FlipV)
            (v0, v1) = (v1, v0);
        return new Vector4(u0, v0, u1, v1);
    }

    /// <summary>Dumps every plausible drawn-part candidate (raw PartId,
    /// PartId+alt, alt alone) plus the asset/parent identity once per change -
    /// the sampled region can only be wrong when the draw reads a part or a
    /// texture other than the resolved one.</summary>
    private unsafe void LogIconResolve(int plate, int slot, AtkImageNode* node,
        AtkUldPartsList* parts, AtkUldAsset* asset, int rawPartId, int usedPartId,
        nint srv, int drawFlags, string? fileName, uint iconId)
    {
        if (!DebugLog)
            return;
        var idx = plate * IconSlots + slot;
        // no screen position in the sig - a wobbling plate must not re-log
        var sig = HashCode.Combine(node->NodeId, rawPartId, usedPartId,
            node->Width, node->Height, srv, (nint)asset);
        if (_iconResolveSig[idx] == sig)
            return;
        _iconResolveSig[idx] = sig;
        var sb = new StringBuilder(224);
        sb.Append("[Nameplate] ic").Append(slot).Append(" p").Append(plate)
            .Append(" nid").Append(node->NodeId)
            .Append(" pid").Append(rawPartId).Append("->").Append(usedPartId)
            .Append('/').Append(parts->PartCount)
            .Append(" uv*(");
        AppendPartUv(sb, parts->Parts + usedPartId, node);
        sb.Append(')');
        if (usedPartId != rawPartId)
        {
            sb.Append(" base(");
            AppendPartUv(sb, parts->Parts + rawPartId, node);
            sb.Append(')');
        }
        if (_alternatePartId < parts->PartCount && _alternatePartId != usedPartId)
        {
            sb.Append(" alt(");
            AppendPartUv(sb, parts->Parts + _alternatePartId, node);
            sb.Append(')');
        }
        var ls = new Vector2(node->GetScaleX(), node->GetScaleY());
        var ts = NodeTransformScale((AtkResNode*)node);
        sb.Append(" wh").Append(node->Width).Append('x').Append(node->Height)
            .Append(" wm").Append(node->WrapMode)
            .Append(" ls").Append(ls.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append('x').Append(ls.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" ts").Append(ts.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append('x').Append(ts.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" pl").Append(parts->Id)
            .Append(" ast").Append(((nint)asset).ToString("X"))
            .Append(" aid").Append(asset->Id)
            .Append(" pn{");
        AppendParentChain(sb, (AtkResNode*)node);
        sb.Append("} ap").Append(_alternatePartId)
            .Append(" df").Append(drawFlags.ToString("X", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" srv ").Append(srv.ToString("X"));
        if (((AtkResNode*)node)->IsDrawDisabled)
            sb.Append(" dd");
        if (node->Color.A != 255)
            sb.Append(" ca").Append(node->Color.A);
        if (iconId != 0)
            sb.Append(" ic").Append(iconId);
        if (fileName != null)
            sb.Append(" fn").Append(fileName);
        _logger.LogDebug("{Msg}", sb.ToString());
    }

    /// <summary>Reads an MSVC-layout std::string - SSO payloads sit in the
    /// inline buffer, longer names live behind BufferPtr.</summary>
    private static unsafe string? StdStringText(StdString* s)
    {
        var len = (int)s->Length;
        if (len <= 0)
            return null;
        var data = s->Capacity > 15 ? s->BufferPtr : s->Buffer;
        return data == null ? null : Encoding.UTF8.GetString(data, Math.Min(len, 256));
    }

    /// <summary>Node ancestry as nid/type pairs - shows whether an icon node
    /// sits inside a nested component (icon component, …).</summary>
    private static unsafe void AppendParentChain(StringBuilder sb, AtkResNode* node)
    {
        for (var p = node->ParentNode; p != null; p = p->ParentNode)
        {
            sb.Append(p->NodeId).Append('/').Append((int)p->Type);
            if (p->ParentNode != null)
                sb.Append('>');
        }
    }

    /// <summary>Why a visible image node did not resolve to a texture -
    /// distinguishes missing parts lists, empty assets and unloaded
    /// kernel textures on the own plate's icon nodes.</summary>
    private unsafe void LogIconFail(int plate, int slot, AtkImageNode* node,
        AtkUldPartsList* parts, AtkUldPart* part, string why)
    {
        if (!DebugLog)
            return;
        var idx = plate * IconSlots + slot;
        var sig = HashCode.Combine(node->NodeId, node->PartId, parts == null, why);
        if (_iconResolveSig[idx] == sig)
            return;
        _iconResolveSig[idx] = sig;
        var asset = part != null ? part->UldAsset : null;
        var ttype = asset != null ? (int)asset->AtkTexture.TextureType : -1;
        _logger.LogDebug(
            "[Nameplate] ic{slot} p{plate} nid{nid} fail{why} pid{pid} cnt{cnt} ast{ast} aid{aid} tt{tt} wm{wm} wh{w}x{h} pn{{{chain}}}",
            slot, plate, node->NodeId, why, node->PartId,
            parts != null ? (int)parts->PartCount : -1,
            ((nint)asset).ToString("X"), asset != null ? asset->Id : 0,
            ttype, node->WrapMode, node->Width, node->Height,
            ParentChainText((AtkResNode*)node));
    }

    private static unsafe string ParentChainText(AtkResNode* node)
    {
        var sb = new StringBuilder(32);
        AppendParentChain(sb, node);
        return sb.ToString();
    }

    private static unsafe void AppendPartUv(StringBuilder sb, AtkUldPart* part, AtkImageNode* node)
    {
        var uv = PartUv(part, node);
        sb.Append(uv.X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
            .Append(',')
            .Append(uv.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
            .Append(")-(")
            .Append(uv.Z.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
            .Append(',')
            .Append(uv.W.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The plate component can draw more image nodes than the two
    /// named icon fields - the job/class icon on player plates lives in its
    /// own node. Walks the component's node list and captures every other
    /// visible image node into the remaining icon slots.</summary>
    /// <summary>Unions the screen rect of every visible text node in the plate
    /// component other than the main NameText - status prefixes, level
    /// prefixes and target suffixes can render as sibling nodes and would
    /// otherwise punch holes through our occluded overlays.</summary>
    private static unsafe void UnionAuxTextNodes(AddonNamePlate.NamePlateObject* plate,
        ref Vector2 min, ref Vector2 max)
    {
        var comp = plate->RootComponentNode != null ? plate->RootComponentNode->Component : null;
        if (comp == null)
            return;
        var nodes = comp->UldManager.NodeList;
        var count = comp->UldManager.NodeListCount;
        for (var k = 0; k < count; k++)
        {
            var node = nodes[k];
            if (node == null || node->Type != NodeType.Text
                || (AtkTextNode*)node == plate->NameText)
                continue;
            // aux text living inside a hidden group renders nothing - it
            // must not inflate the occluder bound either
            if (AncestorAlpha(node) <= 0f)
                continue;
            UnionNodeRect(ref min, ref max, node);
        }
    }

    private unsafe void CaptureExtraIconNodes(AddonNamePlate.NamePlateObject* plate, int index)
    {
        var slot = 2;
        var comp = plate->RootComponentNode != null ? plate->RootComponentNode->Component : null;
        if (comp != null)
        {
            var nodes = comp->UldManager.NodeList;
            var count = comp->UldManager.NodeListCount;
            for (var k = 0; k < count && slot < IconSlots; k++)
            {
                var node = (AtkImageNode*)nodes[k];
                if (node == null || node->Type != NodeType.Image
                    || node == plate->NameIcon || node == plate->MarkerIcon
                    || node == plate->GaugeBackground || node == plate->GaugeFill
                    || node == plate->GaugeContainer)
                    continue;
                CaptureIconNode(node, index, slot);
                if (_plateIconSrv[index * IconSlots + slot] != 0)
                    slot++;
            }
        }
        while (slot < IconSlots)
        {
            var idx = index * IconSlots + slot++;
            _plateIconRects[idx] = Vector4.Zero;
            _plateIconSrv[idx] = 0;
            _plateIconUv[idx] = Vector4.Zero;
            _plateIconWrap[idx] = 0;
            _plateIconAlpha[idx] = 0f;
        }
    }

    /// <summary>
    /// Snapshots the world-anchored chrome addons' image nodes - the target
    /// cursor arrow, chat bubbles and enemy cast bars all render above the
    /// nameplate layer, so their real pixels join the occluder set alongside
    /// plate text and icons.
    /// </summary>
    private unsafe void CaptureTargetCursor(AddonNamePlate* namePlate)
    {
        _cursorCount = 0;
        for (var s = 0; s < CursorSlots; s++)
        {
            _cursorRects[s] = Vector4.Zero;
            _cursorSrv[s] = 0;
            _cursorUv[s] = Vector4.Zero;
            _cursorAlpha[s] = 0f;
            _cursorPartCnt[s] = 0;
        }
        for (var a = 0; a < OccluderAddons.Length && _cursorCount < CursorSlots; a++)
        {
            var addonPtr = _gameGui.GetAddonByName(OccluderAddons[a], 1);
            if (addonPtr.IsNull)
                continue;
            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon->IsVisible)
                CaptureCursorImages(addon, null);
            // the target arrow may be parented inside the NamePlate addon
            // instead of its own addon - the fallback only applies while no
            // cursor was captured; bubbles/cast bars must not suppress it
            if (a == 1 && _cursorCount == 0 && namePlate != null
                && ((AtkUnitBase*)namePlate)->IsVisible)
                CaptureCursorImages((AtkUnitBase*)namePlate, namePlate);
        }
    }

    /// <summary>Collects image nodes from an addon into the cursor slots. When
    /// <paramref name="namePlate"/> is set, nodes whose top-level ancestor is a
    /// plate component are skipped - only addon-level chrome (the target arrow
    /// lives beside the plate objects) counts.</summary>
    private unsafe void CaptureCursorImages(AtkUnitBase* addon, AddonNamePlate* namePlate)
    {
        var nodes = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;
        for (var k = 0; k < count && _cursorCount < CursorSlots; k++)
        {
            var node = nodes[k];
            if (node == null || node->Type != NodeType.Image)
                continue;
            if (namePlate != null && IsInsidePlateObject(namePlate, node))
                continue;
            var dup = false;
            for (var d = 0; d < _cursorCount; d++)
            {
                if (_cursorNodePtrs[d] == (nint)node)
                {
                    dup = true;
                    break;
                }
            }
            if (dup)
                continue;
            if (CaptureCursorNode((AtkImageNode*)node, _cursorCount))
            {
                _cursorNodePtrs[_cursorCount] = (nint)node;
                _cursorCount++;
            }
        }
    }

    /// <summary>
    /// Snapshots image nodes of every loaded visible HUD unit - our overlay
    /// renders over the whole game, so any UI chrome crossing an element's
    /// bound must cut it like plate text does. <paramref name="bound"/> is the
    /// screen-space union of all drawn element regions, keeping the scan to
    /// nodes that can actually overlap.
    /// </summary>
    private long _hudDumpTick;
    private long _hudCapLoggedAt;
    // nodes already printed during the current DumpHudUnit pass - the flat
    // list, objects table, dup list and WindowNode walk all hit the same
    // node pointers, so each gets logged once per unit
    private readonly HashSet<nint> _hudDumpSeen = new();
    private string? _hudSrcName;
    private bool _hudSrcSolid;

    public unsafe void CaptureHudOccluders(Vector4 bound, bool dump = false)
    {
        _hudCount = 0;
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null)
            return;
        var mgr = &stage->RaptureAtkUnitManager->AtkUnitManager;
        _hudScanUnits = 0;
        _hudScanNodes = 0;
        _hudSrcName = null;
        _hudSrcSolid = false;
        _hudRejState = 0;
        _hudRejChain = 0;
        _hudRejBound = 0;
        _hudRejSrv = 0;
        var dumpNow = dump && Environment.TickCount64 - _hudDumpTick > 2000;
        var dumped = 0;
        _hudDrawnCount = 0;
        // AllLoadedUnitsList is the complete unit set - the depth-layer lists
        // are only draw-order groupings of the same units, so the scan never
        // walks them; each unit's DepthLayer field (Flags198 bits 16-19)
        // names its group directly and costs one bit read per unit
        var loaded = &mgr->AllLoadedUnitsList;
        var entries = (AtkUnitBase**)((byte*)loaded + 0x8);
        _hudScanUnits = loaded->Count;
        // pass A - classify every loaded unit once. Drawn = unit-level gates
        // pass; docked/child windows draw inside their host's paint pass and
        // keep their own draw flags suppressed, so the escape only needs an
        // intersecting unit still reporting shown and opaque
        for (var u = 0; u < loaded->Count; u++)
        {
            var unit = entries[u];
            if (unit == null || unit == (AtkUnitBase*)_namePlateAddon)
                continue;
            var layer = UnitDepthGroup(unit);
            var intersects = UnitIntersects(unit, bound);
            // the applied state is what actually renders - during a show or
            // hide tween the pending VisibilityState already flips while the
            // unit still fades in or out, so treat it as drawn until the
            // applied flags follow and let the node alpha ramp drive the fade
            var appliedShown = (unit->AppliedVisibilityState
                & AtkUnitBaseVisibilityState.Show) != AtkUnitBaseVisibilityState.None;
            var drawn = UnitActuallyVisible(unit)
                || (appliedShown && unit->Alpha > 0 && unit->RootNode != null)
                || (intersects && unit->IsVisible && unit->Alpha > 0
                    && unit->RootNode != null);
            // drawn means painted - a unit in a masked-out layer is not, and
            // its docked children paint inside its pass, so neither side of
            // the link may join the drawn set for off layers
            var layerOn = drawn && (HudLayerMask & (1 << layer)) != 0;
            if (dumpNow)
                DumpHudLoadedUnit(unit, layer, drawn && !layerOn ? -1 : drawn ? 1 : 0, intersects);
            if (!layerOn)
                continue;
            RecordHudDrawn(unit);
        }
        // front-most units get slot priority: depth layer descending, then
        // the game's own draw order inside the layer
        SortHudDrawn();
        _hudCurLayer = -1;
        for (var d = 0; d < _hudDrawnCount && _hudCount < HudSlots; d++)
        {
            var unit = (AtkUnitBase*)_hudDrawnUnits[d];
            _hudCurLayer = UnitDepthGroup(unit);
            ScanHudUnit(unit, bound, dumpNow, UnitIntersects(unit, bound), ref dumped);
        }
        // linked pass - docked/hosted panels paint inside their host unit's
        // draw, so their own draw flags and unit geometry can stay suppressed
        // or stale. Any loaded unit whose ParentId or HostId resolves to a
        // drawn unit gets scanned with no unit-level gates; node-level flags
        // stay authoritative, so an unpainted dock still emits nothing
        if ((HudLayerMask & (1 << HudLinkedLayer)) != 0 && _hudCount < HudSlots)
        {
            _hudCurLayer = HudLinkedLayer;
            // fixpoint sweep - a docked unit can itself host another docked
            // unit, so newly-linked units rejoin the drawn set and the pass
            // repeats until a sweep adds nothing
            var added = true;
            for (var sweep = 0; added && sweep < 4 && _hudCount < HudSlots; sweep++)
            {
                added = false;
                for (var u = 0; u < loaded->Count && _hudCount < HudSlots; u++)
                {
                    var unit = entries[u];
                    if (unit == null || unit == (AtkUnitBase*)_namePlateAddon
                        || WasHudUnitScanned(unit) || unit->RootNode == null)
                        continue;
                    var pid = unit->ParentId;
                    var hid = unit->HostId;
                    if ((pid == 0 || !IsHudDrawnUnit(pid))
                        && (hid == 0 || !IsHudDrawnUnit(hid)))
                        continue;
                    RecordHudDrawn(unit);
                    added = true;
                    if (dumpNow)
                        _logger.LogDebug(
                            "[Nameplate] hudlink '{name}' parent={p} host={h}",
                            unit->NameString ?? "??", pid, hid);
                    ScanHudUnit(unit, bound, dumpNow, true, ref dumped);
                }
            }
            _hudCurLayer = -1;
        }
        // a full slot table means occluders were silently dropped - surface
        // that outside of dumps too, rate-limited so busy screens do not
        // spam the log every frame
        if (_hudCount >= HudSlots
            && Environment.TickCount64 - _hudCapLoggedAt > 60000)
        {
            _hudCapLoggedAt = Environment.TickCount64;
            _logger.LogDebug(
                "[Nameplate] hudfull {used}/{cap} slots - {drawn} drawn units competed for them",
                _hudCount, HudSlots, _hudDrawnCount);
        }
        if (dumpNow)
            _hudDumpTick = Environment.TickCount64;
    }

    /// <summary>The unit's draw group - its own DepthLayer field clamped to
    /// the 13 real layers; units without an assignment (docked panels, stale
    /// bitfield) land in the linked group for masking and debug coloring.</summary>
    private static unsafe int UnitDepthGroup(AtkUnitBase* unit)
        => unit->DepthLayer < HudDepthLayers ? (int)unit->DepthLayer : HudLinkedLayer;

    private unsafe void RecordHudDrawn(AtkUnitBase* unit)
    {
        if (_hudDrawnCount >= HudDrawnMax)
            return;
        _hudDrawnUnits[_hudDrawnCount] = (nint)unit;
        _hudDrawnIds[_hudDrawnCount] = unit->Id;
        _hudDrawnKeys[_hudDrawnCount] =
            (UnitDepthGroup(unit) << 16) | unit->DrawOrderIndex;
        _hudDrawnCount++;
    }

    /// <summary>Insertion-sorts the drawn set by (layer desc, draw-order desc)
    /// - foreground windows keep their masks when the slot budget runs out.</summary>
    private void SortHudDrawn()
    {
        for (var i = 1; i < _hudDrawnCount; i++)
        {
            var pu = _hudDrawnUnits[i];
            var pi = _hudDrawnIds[i];
            var pk = _hudDrawnKeys[i];
            var j = i - 1;
            for (; j >= 0 && _hudDrawnKeys[j] < pk; j--)
            {
                _hudDrawnUnits[j + 1] = _hudDrawnUnits[j];
                _hudDrawnIds[j + 1] = _hudDrawnIds[j];
                _hudDrawnKeys[j + 1] = _hudDrawnKeys[j];
            }
            _hudDrawnUnits[j + 1] = pu;
            _hudDrawnIds[j + 1] = pi;
            _hudDrawnKeys[j + 1] = pk;
        }
    }

    /// <summary>Emits alpha-aware masks for one drawn unit - a unit whose
    /// drawable tree emits nothing paints no pixels, so nothing needs
    /// cutting. Collision rects proved to phantom-cut: on-demand HUD
    /// elements keep them registered with stale geometry while rendering
    /// nothing.</summary>
    private unsafe void ScanHudUnit(AtkUnitBase* unit, Vector4 bound,
        bool dumpNow, bool intersects, ref int dumped)
    {
        var dumpedThis = false;
        if (dumpNow && dumped < 6 && UnitHasDrawables(unit))
        {
            DumpHudUnit(unit, bound);
            dumped++;
            dumpedThis = true;
        }
        var before = _hudCount;
        var rS = _hudRejState;
        var rC = _hudRejChain;
        var rB = _hudRejBound;
        var rV = _hudRejSrv;
        _hudUnitA = unit->Alpha > 0 ? MathF.Min(1f, unit->Alpha / 255f) : 1f;
        CaptureHudImages(unit, bound);
        if (dumpNow && intersects && _hudCount == before)
        {
            // a visible unit overlapping the bound emitting nothing is the
            // signature of a dock subtree gated out - name it plus which
            // stage ate its nodes
            _logger.LogDebug(
                "[Nameplate] hudnone '{name}' rej s{rs} c{rc} b{rb} v{rv}",
                unit->NameString ?? "??",
                _hudRejState - rS, _hudRejChain - rC,
                _hudRejBound - rB, _hudRejSrv - rV);
        }
        if (_hudCount <= before)
            return;
        // first emitting unit of the pass names itself in the compact hud
        // tick - survives dump caps and dedup
        if (_hudSrcName == null)
        {
            _hudSrcName = unit->NameString;
            _hudSrcSolid = _hudSrv[before] < 0;
        }
        if (!dumpNow)
            return;
        if (!dumpedThis)
            DumpHudUnit(unit, bound);
        for (var j = before; j < _hudCount; j++)
        {
            var r = _hudRects[j];
            var puv = _hudUv[j];
            // h{j} matches the debug overlay's quad label, so a phantom cut
            // seen on screen maps straight to the emitting unit here
            _logger.LogDebug(
                "[Nameplate] hudemit h{j} '{name}' {w:0}x{h:0} pos({px:0},{py:0}) effA={a:0.00} {kind} part({u0:0},{v0:0}>{u1:0},{v1:0}) pc{pc} p{pi} srv{srv:X}",
                j, unit->NameString ?? "??", r.Z - r.X, r.W - r.Y,
                r.X, r.Y, _hudAlpha[j],
                _hudSrv[j] < 0 ? "solid" : "tex",
                puv.X, puv.Y, puv.Z, puv.W, _hudPartCnt[j], _hudPartIdx[j], _hudSrv[j]);
        }
    }

    private bool IsHudDrawnUnit(ushort id)
    {
        for (var i = 0; i < _hudDrawnCount; i++)
        {
            if (_hudDrawnIds[i] == id)
                return true;
        }
        return false;
    }

    private unsafe bool WasHudUnitScanned(AtkUnitBase* unit)
    {
        for (var i = 0; i < _hudDrawnCount; i++)
        {
            if (_hudDrawnUnits[i] == (nint)unit)
                return true;
        }
        return false;
    }

    /// <summary>One verdict line per loaded unit - name, draw group, raw
    /// visibility bits, link ids and the drawn/skip outcome. This is the
    /// dump that makes docked/hosted units visible: they previously fell
    /// through every gate and produced no log line at all.</summary>
    private unsafe void DumpHudLoadedUnit(AtkUnitBase* unit, int layer, int verdict, bool intersects)
    {
        _logger.LogDebug(
            "[Nameplate] hudloaded '{name}' g{g} vs{vs} avs{avs} vf{vf:X4} a{a} ix{ix} pid{p} hid{h} do{do} → {v}",
            unit->NameString ?? "??",
            layer == HudLinkedLayer ? "D" : (layer + 1).ToString(),
            (byte)unit->VisibilityState, (byte)unit->AppliedVisibilityState,
            unit->VisibilityFlags, unit->Alpha, intersects ? 1 : 0,
            unit->ParentId, unit->HostId, unit->DrawOrderIndex,
            verdict > 0 ? "drawn" : verdict < 0 ? "offlayer" : "skip");
    }

    /// <summary>KamiToolKit's IsActuallyVisible - a loaded unit that reports
    /// IsVisible can still be draw-suppressed (hidden overlays park their
    /// nodes with stale screen positions). The unit flag, the root node's own
    /// Visible flag, and VisibilityFlags bits 0/2 together describe what the
    /// game really renders.</summary>
    private static unsafe bool UnitActuallyVisible(AtkUnitBase* unit)
        => unit->IsVisible && unit->RootNode != null
            && unit->RootNode->IsVisible() && (unit->VisibilityFlags & 5) == 0
            && unit->Alpha > 0;

    /// <summary>Unit root- or window-collision-rect intersect test for the
    /// dump path only.</summary>
    private static unsafe bool UnitIntersects(AtkUnitBase* unit, Vector4 bound)
    {
        var root = unit->RootNode;
        if (NodeIntersects(root, bound))
            return true;
        if (NodeIntersects((AtkResNode*)unit->WindowCollisionNode, bound))
            return true;
        return NodeIntersects((AtkResNode*)unit->WindowHeaderCollisionNode, bound);
    }

    private static unsafe bool NodeIntersects(AtkResNode* node, Vector4 bound)
    {
        if (node == null)
            return false;
        var s = NodeTransformScale(node);
        var sx = s.X > 0f ? s.X : node->GetScaleX();
        var sy = s.Y > 0f ? s.Y : node->GetScaleY();
        return !(node->ScreenX + node->Width * sx <= bound.X || node->ScreenX >= bound.Z
            || node->ScreenY + node->Height * sy <= bound.Y || node->ScreenY >= bound.W);
    }

    /// <summary>Logs a per-type node count plus every drawable node of one
    /// intersecting HUD unit with its raw draw flags - the dump exists so a
    /// window covering our elements can be diagnosed node by node instead of
    /// guessed.</summary>
    private unsafe void DumpHudUnit(AtkUnitBase* unit, Vector4 bound)
    {
        var name = unit->NameString ?? "??";
        var nodes = unit->UldManager.NodeList;
        var count = unit->UldManager.NodeListCount;
        var imgs = 0;
        var grids = 0;
        var comps = 0;
        var texts = 0;
        for (var k = 0; k < count; k++)
        {
            var n = nodes[k];
            if (n == null)
                continue;
            if (n->Type == NodeType.Image)
                imgs++;
            else if (n->Type == NodeType.NineGrid)
                grids++;
            else if ((int)n->Type >= ComponentNodeTypeBase)
                comps++;
            else if (n->Type == NodeType.Text)
                texts++;
        }
        var root = unit->RootNode;
        var wn = unit->WindowNode;
        _logger.LogDebug("[Nameplate] hudunit '{name}' ptr={ptr:X} nodes={count} img={imgs} ng={grids} cmp={comps} txt={texts} wcol={wcol} hcol={hcol} vs={vs} avs={avs} vf={vf:X4} shf={shf:X2} tween={tween} rootVis={rootVis} rootDD={rootDD} ux={ux} uy={uy} usc={usc:0.##} ual={ual} wnVis={wnVis} wnDD={wnDD} bound({bx:0},{by:0},{bz:0},{bw:0})",
            name, (nint)unit, count, imgs, grids, comps, texts,
            unit->WindowCollisionNode != null ? 1 : 0,
            unit->WindowHeaderCollisionNode != null ? 1 : 0,
            (byte)unit->VisibilityState, (byte)unit->AppliedVisibilityState,
            unit->VisibilityFlags, unit->ShowHideFlags,
            (int)unit->RootNodeTween.State,
            root != null && root->IsVisible(),
            root != null && root->IsDrawDisabled,
            unit->X, unit->Y, unit->Scale, unit->Alpha,
            wn != null && wn->AtkResNode.IsVisible(),
            wn != null && wn->AtkResNode.IsDrawDisabled,
            bound.X, bound.Y, bound.Z, bound.W);
        if (unit->WindowCollisionNode != null)
        {
            var col = (AtkResNode*)unit->WindowCollisionNode;
            _logger.LogDebug(
                "[Nameplate]   wcol {w}x{h} pos({px:0},{py:0}) vis={vis} dd={dd} uses={uses} rootA={rootA}",
                col->Width, col->Height, col->ScreenX, col->ScreenY,
                col->IsVisible(), col->IsDrawDisabled,
                unit->WindowCollisionNode->Uses,
                root != null ? root->Alpha_2 : (byte)0);
        }
        var wnList = wn != null && wn->Component != null ? wn->Component->UldManager.NodeList : null;
        var wnCount = wnList != null ? wn->Component->UldManager.NodeListCount : 0;
        var dr = 0;
        var vi = 0;
        CountHudDrawables(nodes, count, ref dr, ref vi, 0);
        CountHudDrawables(wnList, wnCount, ref dr, ref vi, 0);
        var overlay = dr > 0 && vi == 0;
        _hudDumpSeen.Clear();
        DumpHudManager(&unit->UldManager, bound, unit->RootNode, null,
            (AtkResNode*)wn, 0, overlay, skipAnc: false);
        if (wnList != null)
        {
            _logger.LogDebug("[Nameplate]   wnodes {count} (WindowNode component)", wnCount);
            DumpHudManager(&wn->Component->UldManager, bound, unit->RootNode,
                (AtkResNode*)wn, (AtkResNode*)wn, 0, overlay, skipAnc: true);
        }
    }

    private unsafe void DumpHudManager(AtkUldManager* mgr, Vector4 bound,
        AtkResNode* stopAt, AtkResNode* gate, AtkResNode* visSkip, int depth,
        bool overlayStyle, bool skipAnc)
    {
        if (mgr == null || depth > 6)
            return;
        DumpHudNodeList(mgr->NodeList, mgr->NodeListCount, bound, stopAt, gate,
            visSkip, depth, overlayStyle, skipAnc);
        var objects = mgr->Objects;
        var objStride = mgr->BaseType == AtkUldManagerBaseType.Component ? 0x18
            : mgr->BaseType == AtkUldManagerBaseType.Widget ? 0x20 : 0x10;
        for (var o = 0; objects != null && o < mgr->ObjectCount; o++)
        {
            var obj = (AtkUldObjectInfo*)((byte*)objects + o * objStride);
            if (obj->NodeList == null || obj->NodeCount <= 0)
                continue;
            _logger.LogDebug("[Nameplate]   hudobj {id:X8} cnt={n}",
                obj->Id, obj->NodeCount);
            DumpHudNodeList(obj->NodeList, Math.Min(obj->NodeCount, 512), bound,
                stopAt, gate, visSkip, depth + 1, overlayStyle, skipAnc);
        }
        var dups = mgr->DuplicateObjectsList;
        for (var e = dups.Start; e != null && e != dups.End; e = e->Next)
        {
            var dl = e->Value.Value;
            if (dl == null || dl->NodeList == null)
                continue;
            _logger.LogDebug("[Nameplate]   huddup cnt={n}", dl->NodeCount);
            var n = Math.Min(dl->NodeCount, 64u);
            for (var i = 0u; i < n; i++)
            {
                var cn = dl->NodeList + i;
                var comp = cn->Component;
                if (comp == null)
                    continue;
                _logger.LogDebug(
                    "[Nameplate]   huddupnode {i} t{t} v{v} a{a} pos({px:0},{py:0}) {w:0}x{h:0}",
                    i, (int)((AtkResNode*)cn)->Type, ((AtkResNode*)cn)->IsVisible(),
                    ((AtkResNode*)cn)->Alpha_2, ((AtkResNode*)cn)->ScreenX,
                    ((AtkResNode*)cn)->ScreenY, ((AtkResNode*)cn)->Width,
                    ((AtkResNode*)cn)->Height);
                DumpHudManager(&comp->UldManager, bound, stopAt, (AtkResNode*)cn,
                    visSkip, depth + 1, overlayStyle, skipAnc);
            }
        }
    }

    private unsafe void DumpHudNodeList(AtkResNode** nodes, int count, Vector4 bound,
        AtkResNode* stopAt, AtkResNode* gate, AtkResNode* visSkip, int depth,
        bool overlayStyle, bool skipAnc = false)
    {
        if (nodes == null || depth > 6)
            return;
        var shown = 0;
        for (var k = 0; k < count && shown < 10; k++)
        {
            var n = nodes[k];
            if (n == null)
                continue;
            if ((int)n->Type >= ComponentNodeTypeBase)
            {
                var comp = ((AtkComponentNode*)n)->Component;
                if (comp != null && _hudDumpSeen.Add((nint)n))
                    DumpHudManager(&comp->UldManager, bound, stopAt, n, visSkip,
                        depth + 1, overlayStyle, skipAnc);
                continue;
            }
            if (n->Type != NodeType.Image && n->Type != NodeType.NineGrid
                || !_hudDumpSeen.Add((nint)n))
                continue;
            LogHudNode(n, k, gate, stopAt, visSkip, overlayStyle, skipAnc);
            shown++;
        }
    }

    private unsafe void LogHudNode(AtkResNode* n, int k, AtkResNode* gate,
        AtkResNode* stopAt, AtkResNode* visSkip, bool overlayStyle, bool skipAnc)
    {
        var chainA = HudChainAlpha(n, gate, stopAt, visSkip, overlayStyle, skipAnc);
        var srv = ResolveHudNodeSrv(n, out _, out _, out _, out _);
        var ts = NodeTransformScale(n);
        _logger.LogDebug(
            "[Nameplate]   hudnode {k} t{typ} w{w}x{h} vis={vis} alpha={alpha} dd={dd} colA={colA} chain={chain:0.00} sx={sx:0.#} sy={sy:0.#} tsx={tsx:0.##} tsy={tsy:0.##} pos({px:0},{py:0}) srv={srvOk}{off}",
            k, (int)n->Type, n->Width, n->Height, n->IsVisible(), n->Alpha_2,
            n->IsDrawDisabled, n->Color.A, chainA,
            n->GetScaleX(), n->GetScaleY(), ts.X, ts.Y,
            n->ScreenX, n->ScreenY, srv != 0,
            n->Type == NodeType.NineGrid
                ? $" off({((AtkNineGridNode*)n)->LeftOffset},{((AtkNineGridNode*)n)->TopOffset},{((AtkNineGridNode*)n)->RightOffset},{((AtkNineGridNode*)n)->BottomOffset}){NineGridPartsDesc((AtkNineGridNode*)n)}"
                : "");
        // a self-drawable node rejected by chain=0 names the ancestor that
        // blocks it - type/visible/alpha/drawdisabled per hop until root
        if (chainA <= 0f && n->IsVisible() && n->Alpha_2 > 0 && !n->IsDrawDisabled)
        {
            var chain = new StringBuilder(64);
            var hops = 0;
            for (var p = n->ParentNode; p != null && hops < 8; p = p->ParentNode, hops++)
            {
                chain.Append($" t{(int)p->Type}v{(p->IsVisible() ? 1 : 0)}a{p->Alpha_2}d{(p->IsDrawDisabled ? 1 : 0)}");
                if (p == stopAt)
                {
                    chain.Append("=root");
                    break;
                }
                if (p == gate)
                    chain.Append("=gate");
                if (p == visSkip)
                    chain.Append("=wn");
            }
            _logger.LogDebug("[Nameplate]   hudchain {k} gate={gate}{chain}", k,
                gate == null ? "-"
                    : $"t{(int)gate->Type}v{(gate->IsVisible() ? 1 : 0)}a{gate->Alpha_2}d{(gate->IsDrawDisabled ? 1 : 0)}",
                chain.ToString());
        }
    }

    /// <summary>Compact dump of a nine-grid's nine cell parts (PartId..+8) -
    /// authored rects reveal the real cell order, since corners, edges and
    /// the center have characteristic sizes.</summary>
    private static unsafe string NineGridPartsDesc(AtkNineGridNode* ng)
    {
        var parts = ng->PartsList;
        if (parts == null || ng->PartId + NineGridCells > parts->PartCount)
            return "";
        var sb = new StringBuilder(NineGridCells * 18 + 8);
        sb.Append(" ptr").Append(ng->PartsTypeRenderType).Append(" parts[");
        for (var i = 0; i < NineGridCells; i++)
        {
            var p = parts->Parts + ng->PartId + (uint)i;
            if (i > 0)
                sb.Append(' ');
            sb.Append(i).Append('(').Append(p->U).Append(',').Append(p->V)
                .Append(',').Append(p->Width).Append('x').Append(p->Height).Append(')');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Walks one HUD unit's drawable nodes into the hud slots -
    /// draw-state filters plus dedupe against the cursor set so a cursor
    /// addon's nodes never double-emit.</summary>
    private static unsafe bool UnitHasDrawables(AtkUnitBase* unit)
    {
        var drawables = 0;
        var visible = 0;
        CountHudDrawables(unit->UldManager.NodeList, unit->UldManager.NodeListCount,
            ref drawables, ref visible, 0);
        var wn = unit->WindowNode;
        if (wn != null && wn->Component != null)
            CountHudDrawables(wn->Component->UldManager.NodeList,
                wn->Component->UldManager.NodeListCount, ref drawables, ref visible, 0);
        return drawables > 0;
    }

    private unsafe void CaptureHudImages(AtkUnitBase* unit, Vector4 bound)
    {
        var drawables = 0;
        var visible = 0;
        CountHudDrawables(unit->UldManager.NodeList, unit->UldManager.NodeListCount,
            ref drawables, ref visible, 0);
        var wn = unit->WindowNode;
        var wnList = wn != null && wn->Component != null ? wn->Component->UldManager.NodeList : null;
        var wnCount = wnList != null ? wn->Component->UldManager.NodeListCount : 0;
        CountHudDrawables(wnList, wnCount, ref drawables, ref visible, 0);
        // Overlay addons (KTK overlays) draw nodes whose ancestors report
        // stale invisibility - a unit that is itself visible but whose every
        // drawable node reports not-visible is non-standard, so for it the
        // ancestor chain skips visibility; each node's own flag still gates.
        var overlayStyle = drawables > 0 && visible == 0;
        var visSkip = (AtkResNode*)wn;
        ScanHudManager(&unit->UldManager, bound, unit->RootNode, null,
            visSkip, 0, overlayStyle, skipAnc: false);
        // window chrome (background nine-grid, title bar) lives in the
        // WindowNode component's own node list, outside the unit's flat list -
        // its internal containers can report stale invisibility while the
        // window still renders, so only the nodes' own flags gate them
        ScanHudManager(wnList != null ? &wn->Component->UldManager : null,
            bound, unit->RootNode, (AtkResNode*)wn, visSkip, 0, overlayStyle, skipAnc: true);
    }

    /// <summary>Counts image/nine-grid drawables in a unit including component
    /// internals - used to detect overlay-style units that draw regardless of
    /// the Visible flag.</summary>
    private static unsafe void CountHudDrawables(AtkResNode** nodes, int count,
        ref int drawables, ref int visible, int depth)
    {
        if (nodes == null || depth > 6)
            return;
        for (var k = 0; k < count; k++)
        {
            var n = nodes[k];
            if (n == null)
                continue;
            if ((int)n->Type >= ComponentNodeTypeBase)
            {
                var comp = ((AtkComponentNode*)n)->Component;
                if (comp != null)
                    CountHudDrawables(comp->UldManager.NodeList, comp->UldManager.NodeListCount,
                        ref drawables, ref visible, depth + 1);
                continue;
            }
            if (n->Type != NodeType.Image && n->Type != NodeType.NineGrid)
                continue;
            drawables++;
            if (n->IsVisible())
                visible++;
        }
    }

    /// <summary>Walks a unit's flat node list plus every component node's own
    /// UldManager - HUD chrome (window nine-grids, rows, icons) lives inside
    /// components, whose nodes are not part of the unit's top-level list.
    /// <paramref name="gate"/> is the owning component node for nested lists so
    /// its draw state gates the children it contains.</summary>
    private unsafe void ScanHudNodeList(AtkResNode** nodes, int count, Vector4 bound,
        AtkResNode* stopAt, AtkResNode* gate, AtkResNode* visSkip, int depth,
        bool overlayStyle, bool skipAnc = false)
    {
        if (nodes == null || depth > 6)
            return;
        for (var k = 0; k < count && _hudCount < HudSlots; k++)
        {
            var node = nodes[k];
            if (node == null)
                continue;
            if ((int)node->Type >= ComponentNodeTypeBase)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp != null)
                    ScanHudManager(&comp->UldManager, bound, stopAt, node,
                        visSkip, depth + 1, overlayStyle, skipAnc);
                continue;
            }
            if (node->Type != NodeType.Image && node->Type != NodeType.NineGrid)
                continue;
            _hudScanNodes++;
            var dup = false;
            for (var d = 0; d < _cursorCount; d++)
            {
                if (_cursorNodePtrs[d] == (nint)node)
                {
                    dup = true;
                    break;
                }
            }
            if (dup)
                continue;
            // the Objects-table walk re-visits every node the flat list
            // already emitted - skip emitted pointers or each one costs a
            // second mask slot
            for (var d = 0; d < _hudCount; d++)
            {
                if (_hudNodePtrs[d] == (nint)node)
                {
                    dup = true;
                    break;
                }
            }
            if (dup)
                continue;
            _hudCount += CaptureHudNode(node, bound, stopAt, gate, visSkip,
                _hudCount, overlayStyle, skipAnc);
        }
    }

    /// <summary>Scans one UldManager's flat node list plus its duplicated
    /// objects - runtime-cloned rows/cards (list item renderers, docked
    /// panels) never land in the flat list; the manager keeps them in a
    /// linked list of component-node arrays instead. Type-free: every
    /// duplicated object is an AtkComponentNode whose own manager recurses
    /// the same way.</summary>
    private unsafe void ScanHudManager(AtkUldManager* mgr, Vector4 bound,
        AtkResNode* stopAt, AtkResNode* gate, AtkResNode* visSkip, int depth,
        bool overlayStyle, bool skipAnc)
    {
        if (mgr == null || depth > 6)
            return;
        ScanHudNodeList(mgr->NodeList, mgr->NodeListCount, bound, stopAt, gate,
            visSkip, depth, overlayStyle, skipAnc);
        // the object table groups nodes per ULD object - dock/list/portrait
        // content can sit in an object's own node array without ever being
        // flattened into the manager's flat NodeList
        var objects = mgr->Objects;
        var objStride = mgr->BaseType == AtkUldManagerBaseType.Component ? 0x18
            : mgr->BaseType == AtkUldManagerBaseType.Widget ? 0x20 : 0x10;
        for (var o = 0; objects != null && o < mgr->ObjectCount
            && _hudCount < HudSlots; o++)
        {
            var obj = (AtkUldObjectInfo*)((byte*)objects + o * objStride);
            if (obj->NodeList == null || obj->NodeCount <= 0)
                continue;
            ScanHudNodeList(obj->NodeList, Math.Min(obj->NodeCount, 512), bound,
                stopAt, gate, visSkip, depth + 1, overlayStyle, skipAnc);
        }
        var dups = mgr->DuplicateObjectsList;
        for (var e = dups.Start; e != null && e != dups.End && _hudCount < HudSlots;
             e = e->Next)
        {
            var dl = e->Value.Value;
            if (dl == null || dl->NodeList == null)
                continue;
            var n = Math.Min(dl->NodeCount, 64u);
            for (var i = 0u; i < n && _hudCount < HudSlots; i++)
            {
                var cn = dl->NodeList + i;
                var comp = cn->Component;
                if (comp == null)
                    continue;
                ScanHudManager(&comp->UldManager, bound, stopAt,
                    (AtkResNode*)cn, visSkip, depth + 1, overlayStyle, skipAnc);
            }
        }
    }

    /// <summary>Effective draw alpha along a HUD node's ancestor chain. When
    /// the chain ends inside a component before reaching the unit root, the
    /// owning component node's own flags and ancestry gate the result.
    /// <paramref name="overlayStyle"/> units report stale visibility across
    /// their whole tree - draw-disabled and alpha still gate, visibility does
    /// not. <paramref name="visSkip"/> marks a structural wrapper (the unit's
    /// WindowNode) whose own Visible flag does not gate painted content -
    /// its draw-disabled and alpha still do.</summary>
    private static unsafe float HudChainAlpha(AtkResNode* node, AtkResNode* gate, AtkResNode* stopAt,
        AtkResNode* visSkip, bool overlayStyle, bool skipAnc = false)
    {
        // skipAnc lifts only the ancestor visibility check - window-internal
        // containers report invisible while the window chrome they wrap still
        // renders; alpha and draw-disabled remain authoritative everywhere
        var anc = overlayStyle || skipAnc;
        var a = 1f;
        var sawGate = false;
        var sawStop = false;
        for (var p = node->ParentNode; p != null; p = p->ParentNode)
        {
            if (p == stopAt)
            {
                sawStop = true;
                break;
            }
            if (p == gate)
                sawGate = true;
            if ((!anc && p != visSkip && !p->IsVisible()) || p->IsDrawDisabled || p->Alpha_2 == 0)
                return 0f;
            a *= p->Alpha_2 / 255f;
        }
        if (!sawStop && !sawGate && gate != null)
        {
            if ((!overlayStyle && gate != visSkip && !gate->IsVisible()) || gate->IsDrawDisabled || gate->Alpha_2 == 0)
                return 0f;
            a *= gate->Alpha_2 / 255f;
            a *= AncestorAlpha(gate, stopAt, visSkip, anc);
        }
        return a;
    }

    /// <summary>Captures one HUD drawable node (image or nine-grid) - same
    /// filters and transform handling as <see cref="CaptureCursorNode"/>, plus
    /// an early bound reject so the per-frame scan stays cheap on busy
    /// screens. The ancestor chain stops at the unit root - its flags can stay
    /// unset while the unit's own <see cref="AtkUnitBase.IsVisible"/> governs
    /// what draws.</summary>
    private unsafe int CaptureHudNode(AtkResNode* node, Vector4 bound, AtkResNode* stopAt,
        AtkResNode* gate, AtkResNode* visSkip, int slot, bool overlayStyle, bool skipAnc = false)
    {
        if (node->Width == 0 || node->Height == 0
            || !node->IsVisible()
            || node->Alpha_2 == 0 || node->IsDrawDisabled)
        {
            _hudRejState++;
            return 0;
        }
        // the chain stops before the unit root, but the root's own alpha is
        // what the show/hide tween ramps - fold it plus the unit alpha so
        // mask strength follows the real window fade; a zero report means
        // "not driven", not invisible, so only fold positive values
        var rootA = stopAt != null && stopAt->Alpha_2 > 0
            ? stopAt->Alpha_2 / 255f : 1f;
        var effA = node->Alpha_2 * (node->Color.A / 255f)
            * HudChainAlpha(node, gate, stopAt, visSkip, overlayStyle, skipAnc)
            * rootA * _hudUnitA;
        if (effA <= 0f)
        {
            _hudRejChain++;
            return 0;
        }
        var s = NodeTransformScale(node);
        var sx = s.X > 0f ? s.X : node->GetScaleX();
        var sy = s.Y > 0f ? s.Y : node->GetScaleY();
        var w = (float)node->Width;
        var h = (float)node->Height;
        var rect = new Vector4(node->ScreenX, node->ScreenY,
            node->ScreenX + w * sx, node->ScreenY + h * sy);
        if (node->Type == NodeType.NineGrid)
        {
            var ng = (AtkNineGridNode*)node;
            var parts = ng->PartsList;
            if (parts != null)
            {
                // the parts-type bits select the mode - 0x0C marks authored
                // single-part nine-grids that slice one part, while 0 uses
                // nine consecutive parts (one per cell)
                var sliced = (ng->PartsTypeRenderType & 0x0C) == 0x0C
                    || ng->PartId + NineGridCells > parts->PartCount;
                var written = CaptureNineGridCells(ng, parts, rect, bound,
                    slot, effA, sx, sy, sliced);
                if (written == 0)
                    _hudRejBound++;
                return written;
            }
        }
        if (rect.Z <= bound.X || rect.X >= bound.Z || rect.W <= bound.Y || rect.Y >= bound.W)
        {
            _hudRejBound++;
            return 0;
        }
        var srv = ResolveHudNodeSrv(node, out var uv, out var partCount, out var partIdx,
            out var texKey);
        if (srv == 0 && texKey == 0)
        {
            _hudRejSrv++;
            return 0;
        }
        // Nine-grid parts always stretch to the node rect - the authored-size
        // clamp only applies to plain image nodes; pending entries carry no
        // uv yet and would clamp to zero
        var wrap = node->Type == NodeType.Image ? ((AtkImageNode*)node)->WrapMode : (byte)1;
        if (srv != 0 && wrap != 1)
        {
            w = MathF.Min(w, MathF.Abs(uv.Z - uv.X));
            h = MathF.Min(h, MathF.Abs(uv.W - uv.Y));
            rect = new Vector4(node->ScreenX, node->ScreenY,
                node->ScreenX + w * sx, node->ScreenY + h * sy);
            if (rect.Z <= bound.X || rect.X >= bound.Z || rect.W <= bound.Y || rect.Y >= bound.W)
            {
                _hudRejBound++;
                return 0;
            }
        }
        _hudRects[slot] = rect;
        _hudSrv[slot] = srv;
        _hudTexKey[slot] = texKey;
        _hudUv[slot] = uv;
        _hudPartCnt[slot] = (byte)Math.Min(partCount, byte.MaxValue);
        _hudPartIdx[slot] = (byte)Math.Min(partIdx, byte.MaxValue);
        _hudAlpha[slot] = effA / 255f;
        _hudLayer[slot] = (byte)Math.Max(_hudCurLayer, 0);
        _hudNodePtrs[slot] = (nint)node;
        return 1;
    }

    /// <summary>Emits one slot per nine-grid cell that intersects the bound.
    /// Multi-part grids draw nine authored parts at consecutive part ids;
    /// their cell order is selected via <see cref="HudNineOrder"/> while the
    /// authored layout is being identified. Single-part grids slice one part
    /// at the offsets - the same authored units cut both the texture and the
    /// screen borders. The offsets carve the border cells INSIDE the node
    /// rect (the node bounds are the painted extent; drop shadows live
    /// inside the border parts). Corners keep their authored size, edges
    /// stretch along one axis, the center fills the rest. A single linear
    /// UV map would smear one cell's texels across the whole window.</summary>
    private unsafe int CaptureNineGridCells(AtkNineGridNode* ng, AtkUldPartsList* parts,
        Vector4 nodeRect, Vector4 bound, int slot, float effA, float sx, float sy,
        bool sliced)
    {
        // nine-grid offsets are stored in the loaded sheet's texel units -
        // _hr1 doubles them over the authored part units, so divide by the
        // texture scale or every border cell comes out twice the drawn size
        var texScale = HudTextureScale();
        var l = ng->LeftOffset * sx / texScale;
        var t = ng->TopOffset * sy / texScale;
        var r = ng->RightOffset * sx / texScale;
        var b = ng->BottomOffset * sy / texScale;
        var innerL = nodeRect.X + l;
        var innerR = nodeRect.Z - r;
        var innerT = nodeRect.Y + t;
        var innerB = nodeRect.W - b;
        nint srv = 0;
        var uv = Vector4.Zero;
        uint baseKey = 0;
        float uL = 0f, uR = 0f, vT = 0f, vB = 0f;
        if (sliced)
        {
            srv = ResolveHudPart(parts, ng->PartId, out uv, out baseKey);
            if (srv == 0 && baseKey == 0)
            {
                _hudRejSrv++;
                return 0;
            }
            uL = MathF.Min(uv.X + ng->LeftOffset / texScale, uv.Z);
            uR = MathF.Max(uv.Z - ng->RightOffset / texScale, uL);
            vT = MathF.Min(uv.Y + ng->TopOffset / texScale, uv.W);
            vB = MathF.Max(uv.W - ng->BottomOffset / texScale, vT);
            // a degenerate center while nine real parts exist means the 0x0C
            // bits mislabeled the layout - the offsets cannot fit inside one
            // part's uv rect, so the sheet is authored multi-part after all
            if ((uR <= uL || vB <= vT)
                && ng->PartId + NineGridCells <= parts->PartCount)
                sliced = false;
        }
        var emitted = 0;
        for (var i = 0; i < NineGridCells && slot + emitted < HudSlots; i++)
        {
            var (col, row) = (HudNineOrder, i) switch
            {
                (1, 0) => (0, 0), (1, 1) => (2, 0), (1, 2) => (0, 2),
                (1, 3) => (2, 2), (1, 4) => (1, 0), (1, 5) => (1, 2),
                (1, 6) => (0, 1), (1, 7) => (2, 1), (1, _) => (1, 1),
                (2, 0) => (0, 0), (2, 1) => (2, 0), (2, 2) => (0, 2),
                (2, 3) => (2, 2), (2, 4) => (0, 1), (2, 5) => (2, 1),
                (2, 6) => (1, 0), (2, 7) => (1, 2), (2, _) => (1, 1),
                (3, 0) => (0, 0), (3, 1) => (0, 1), (3, 2) => (0, 2),
                (3, 3) => (1, 0), (3, 4) => (1, 1), (3, 5) => (1, 2),
                (3, 6) => (2, 0), (3, 7) => (2, 1), (3, _) => (2, 2),
                _ => (i % 3, i / 3),
            };
            var minX = col == 0 ? nodeRect.X : col == 1 ? innerL : innerR;
            var maxX = col == 0 ? innerL : col == 1 ? innerR : nodeRect.Z;
            var minY = row == 0 ? nodeRect.Y : row == 1 ? innerT : innerB;
            var maxY = row == 0 ? innerT : row == 1 ? innerB : nodeRect.W;
            var cell = new Vector4(minX, minY, maxX, maxY);
            if (cell.Z - cell.X < 0.5f || cell.W - cell.Y < 0.5f
                || cell.Z <= bound.X || cell.X >= bound.Z
                || cell.W <= bound.Y || cell.Y >= bound.W)
            {
                continue;
            }
            nint cellSrv;
            Vector4 cellUv;
            uint cellKey;
            if (sliced)
            {
                cellSrv = srv;
                cellKey = baseKey;
                cellUv = new Vector4(
                    col == 0 ? uv.X : col == 1 ? uL : uR,
                    row == 0 ? uv.Y : row == 1 ? vT : vB,
                    col == 0 ? uL : col == 1 ? uR : uv.Z,
                    row == 0 ? vT : row == 1 ? vB : uv.W);
            }
            else
            {
                cellSrv = ResolveHudPart(parts, ng->PartId + (uint)i, out cellUv,
                    out cellKey);
                if (cellSrv == 0 && cellKey == 0)
                    continue;
            }
            var s = slot + emitted;
            _hudRects[s] = cell;
            _hudSrv[s] = cellSrv;
            _hudTexKey[s] = cellKey;
            _hudUv[s] = cellUv;
            _hudPartCnt[s] = (byte)Math.Min(parts->PartCount, byte.MaxValue);
            _hudPartIdx[s] = (byte)Math.Min(ng->PartId + (sliced ? 0u : (uint)i), byte.MaxValue);
            _hudAlpha[s] = effA / 255f;
            _hudLayer[s] = (byte)Math.Max(_hudCurLayer, 0);
            _hudNodePtrs[s] = (nint)ng;
            emitted++;
        }
        return emitted;
    }

    /// <summary>SRV + authored texel rect for a HUD node's drawn part - image
    /// and nine-grid nodes share the PartsList/PartId shape; nine-grids carry
    /// no flip flags or wrap mode.</summary>
    private static unsafe nint ResolveHudNodeSrv(AtkResNode* node, out Vector4 uv,
        out uint partCount, out uint partIdx, out uint texKey)
    {
        uv = Vector4.Zero;
        partCount = 0;
        partIdx = 0;
        texKey = 0;
        AtkUldPartsList* parts;
        uint partId;
        var flipH = false;
        var flipV = false;
        if (node->Type == NodeType.Image)
        {
            var img = (AtkImageNode*)node;
            parts = img->PartsList;
            partId = img->PartId;
            flipH = (img->Flags & ImageNodeFlags.FlipH) == ImageNodeFlags.FlipH;
            flipV = (img->Flags & ImageNodeFlags.FlipV) == ImageNodeFlags.FlipV;
        }
        else if (node->Type == NodeType.NineGrid)
        {
            var grid = (AtkNineGridNode*)node;
            parts = grid->PartsList;
            partId = grid->PartId;
        }
        else
        {
            return 0;
        }
        if (parts == null || partId >= parts->PartCount)
            return 0;
        partCount = parts->PartCount;
        partIdx = partId;
        return ResolveHudPart(parts, partId, out uv, out texKey, flipH, flipV);
    }

    /// <summary>Marks a referenced part that intends to draw but has no
    /// content key yet - its asset or resource record is still attaching.
    /// Shared by all such parts; the bake's pending window bounds it.</summary>
    private const uint PendingPartKey = uint.MaxValue;

    /// <summary>SRV + authored texel rect for one part list entry - shared by
    /// the single-part path and the nine-grid cell emission, where every cell
    /// resolves its own consecutive part (and potentially its own asset).
    /// <paramref name="texKey"/> carries the uld resource's path hash - the
    /// pixels behind an SRV stay identical across addon reloads, so the
    /// coverage readback cache keys on this rather than the dying SRV.</summary>
    private static unsafe nint ResolveHudPart(AtkUldPartsList* parts, uint partId,
        out Vector4 uv, out uint texKey, bool flipH = false, bool flipV = false)
    {
        uv = Vector4.Zero;
        texKey = 0;
        if (parts == null || partId >= parts->PartCount)
            return 0;
        var part = parts->Parts + partId;
        var asset = part->UldAsset;
        // a referenced part whose asset, resource record or kernel texture
        // has not landed yet still intends to draw - callers emit a pending
        // cover for it, keyed by the resource's path hash when one exists
        if (asset == null)
        {
            texKey = PendingPartKey;
            return 0;
        }
        var tex = &asset->AtkTexture;
        var res = tex->TextureType == TextureType.Resource ? tex->Resource : null;
        var kernel = tex->TextureType == TextureType.KernelTexture
            ? tex->KernelTexture
            : res != null ? res->KernelTextureObject : null;
        // the resource record exists before its kernel texture finishes
        // streaming - callers use the early key to emit a pending cover
        // rather than dropping the node while first-open pixels load
        if (kernel == null || kernel->D3D11ShaderResourceView == null)
        {
            texKey = res != null ? res->TexPathHash ^ res->IconId : PendingPartKey;
            return 0;
        }
        var u0 = (float)part->U;
        var v0 = (float)part->V;
        var u1 = u0 + part->Width;
        var v1 = v0 + part->Height;
        if (flipH)
            (u0, u1) = (u1, u0);
        if (flipV)
            (v0, v1) = (v1, v0);
        uv = new Vector4(u0, v0, u1, v1);
        texKey = res != null ? res->TexPathHash ^ res->IconId : 0;
        return (nint)kernel->D3D11ShaderResourceView;
    }

    /// <summary>True when the node's top-level ancestor (the direct child of
    /// the addon root) is one of the 50 plate component roots.</summary>
    private static unsafe bool IsInsidePlateObject(AddonNamePlate* addon, AtkResNode* node)
    {
        var top = node;
        while (top->ParentNode != null && top->ParentNode != addon->RootNode)
            top = top->ParentNode;
        var objects = addon->NamePlateObjectArray;
        for (var i = 0; i < AddonNamePlate.NumNamePlateObjects; i++)
            if (objects[i].RootComponentNode == (AtkComponentNode*)top)
                return true;
        return false;
    }

    /// <summary>Effective draw alpha contributed by a node's ancestors - a
    /// node inside a hidden or faded group still reports its own visible flag
    /// and alpha, so the chain decides whether the game actually puts pixels
    /// on screen. Returns 0 when any ancestor hides or fully fades it.
    /// <paramref name="stopAt"/> excludes the unit root itself - the unit's
    /// own visibility is checked at addon level and its root flags may not
    /// reflect what actually draws.</summary>
    private static unsafe float AncestorAlpha(AtkResNode* node, AtkResNode* stopAt = null,
        AtkResNode* visSkip = null, bool skipVisibility = false)
    {
        var a = 1f;
        for (var p = node->ParentNode; p != null && p != stopAt; p = p->ParentNode)
        {
            if ((!skipVisibility && p != visSkip && !p->IsVisible()) || p->IsDrawDisabled || p->Alpha_2 == 0)
                return 0f;
            a *= p->Alpha_2 / 255f;
        }
        return a;
    }

    /// <summary>Captures one cursor image node - same draw-state filters and
    /// transform handling as the plate icon capture, minus the nameplate's
    /// alternate-part remap.</summary>
    private unsafe bool CaptureCursorNode(AtkImageNode* node, int slot)
    {
        var effA = node->Alpha_2 * (node->Color.A / 255f) * AncestorAlpha((AtkResNode*)node);
        if (node->Width == 0 || node->Height == 0 || !node->IsVisible()
            || node->Alpha_2 == 0 || ((AtkResNode*)node)->IsDrawDisabled || effA <= 0f)
            return false;
        var srv = ResolveCursorSrv(node, out var uv, out var texKey);
        if (srv == 0)
            return false;
        var s = NodeTransformScale((AtkResNode*)node);
        var sx = s.X > 0f ? s.X : node->GetScaleX();
        var sy = s.Y > 0f ? s.Y : node->GetScaleY();
        var w = (float)node->Width;
        var h = (float)node->Height;
        if (node->WrapMode != 1)
        {
            w = MathF.Min(w, MathF.Abs(uv.Z - uv.X));
            h = MathF.Min(h, MathF.Abs(uv.W - uv.Y));
        }
        _cursorRects[slot] = new Vector4(node->ScreenX, node->ScreenY,
            node->ScreenX + w * sx, node->ScreenY + h * sy);
        _cursorSrv[slot] = srv;
        _cursorTexKey[slot] = texKey;
        _cursorUv[slot] = uv;
        _cursorPartCnt[slot] = (byte)Math.Min(
            node->PartsList != null ? node->PartsList->PartCount : 0, (uint)byte.MaxValue);
        _cursorAlpha[slot] = effA / 255f;
        return true;
    }

    /// <summary>Resolves a cursor node's drawn part to its kernel texture SRV
    /// and authored texel rect - the shared shape of
    /// <see cref="ResolveIconNodeSrv"/> without the plate-coupled state.</summary>
    private static unsafe nint ResolveCursorSrv(AtkImageNode* node, out Vector4 uv,
        out uint texKey)
    {
        uv = Vector4.Zero;
        texKey = 0;
        var parts = node->PartsList;
        if (parts == null || node->PartId >= parts->PartCount)
            return 0;
        var part = parts->Parts + node->PartId;
        var asset = part->UldAsset;
        if (asset == null)
            return 0;
        var tex = &asset->AtkTexture;
        var res = tex->TextureType == TextureType.Resource ? tex->Resource : null;
        var kernel = tex->TextureType == TextureType.KernelTexture
            ? tex->KernelTexture
            : res != null ? res->KernelTextureObject : null;
        if (kernel == null || kernel->D3D11ShaderResourceView == null)
            return 0;
        uv = PartUv(part, node);
        texKey = res != null ? res->TexPathHash ^ res->IconId : 0;
        return (nint)kernel->D3D11ShaderResourceView;
    }

    /// <summary>
    /// Emits one mask quad sampling the plate's own bake region - the bake atlas
    /// holds the exact pixels the game drew for this plate's text, so every
    /// alpha texel is a real glyph pixel. The bake is rendered at fixed 2x
    /// resolution and DrawBaked centers it on the plate anchor; TextYOffset
    /// locates the text's top edge inside the bake region.
    /// </summary>
    /// <summary>
    /// Hooks BakePlateRenderer.Draw once - the addon calls it for every plate's
    /// text each frame, so the node state we read there is exactly what the
    /// visible title was drawn with. That ends the frame-phase guessing the
    /// number-array and text-node anchors kept losing.
    /// </summary>
    private static readonly TimeSpan BakeHookRetryDelay = TimeSpan.FromSeconds(30);
    private DateTime _bakeDrawHookRetryAt = DateTime.MinValue;
    private DateTime _bakePrepareHookRetryAt = DateTime.MinValue;
    private DateTime _bakeBlitHookRetryAt = DateTime.MinValue;

    private unsafe void EnsureBakeDrawHook()
    {
        if (_namePlateAddon == null)
            return;
        var renderer = new IntPtr(&_namePlateAddon->BakePlate);
        var vtbl = *(nint*)renderer;
        if (vtbl == 0)
            return;
        // Each hook is created independently with a retry cooldown: on
        // heavily-hooked clients Reloaded can fail to place a trampoline
        // inside the ±2GB rel32 window (BufferAllocationException). The
        // bake capture path degrades - it must never throw into render.
        if (_bakeDrawHook == null && DateTime.UtcNow >= _bakeDrawHookRetryAt)
        {
            var addr = ((nint*)vtbl)[2];
            if (addr != 0)
            {
                try
                {
                    _bakeDrawHook = _gameInterop.HookFromAddress<BakeDrawDelegate>(addr, BakeDrawDetour);
                    _bakeDrawHook.Enable();
                    _logger.LogDebug("[Nameplate] BakePlateRenderer.Draw hook at {addr:X}", addr);
                }
                catch (Exception ex)
                {
                    try { _bakeDrawHook?.Dispose(); } catch (Exception d) { _logger.LogDebug(d, "[Nameplate] draw hook cleanup failed"); }
                    _bakeDrawHook = null;
                    _bakeDrawHookRetryAt = DateTime.UtcNow + BakeHookRetryDelay;
                    _logger.LogDebug(ex, "[Nameplate] Draw hook failed - bake capture degraded, retrying in {s}s",
                        (int)BakeHookRetryDelay.TotalSeconds);
                }
            }
        }
        var prep = ((nint*)vtbl)[3];
        if (prep != 0 && _bakePrepareHook == null && DateTime.UtcNow >= _bakePrepareHookRetryAt)
        {
            try
            {
                _bakePrepareHook = _gameInterop.HookFromAddress<BakePrepareDelegate>(prep, BakePrepareDetour);
                _bakePrepareHook.Enable();
                _logger.LogDebug("[Nameplate] BakePlateRenderer.PrepareNode hook at {addr:X}", prep);
            }
            catch (Exception ex)
            {
                try { _bakePrepareHook?.Dispose(); } catch (Exception d) { _logger.LogDebug(d, "[Nameplate] prepare hook cleanup failed"); }
                _bakePrepareHook = null;
                _bakePrepareHookRetryAt = DateTime.UtcNow + BakeHookRetryDelay;
                _logger.LogDebug(ex, "[Nameplate] PrepareNode hook failed, retrying in {s}s",
                    (int)BakeHookRetryDelay.TotalSeconds);
            }
        }
        if (_bakeBlitHook == null && DateTime.UtcNow >= _bakeBlitHookRetryAt)
        {
            try
            {
                _bakeBlitHook = _gameInterop.HookFromSignature<BakeBlitDelegate>(
                    BakeBlitSignature, BakeBlitDetour);
                _bakeBlitHook.Enable();
                _logger.LogDebug("[Nameplate] BakePlateRenderer.DrawBaked hook armed");
            }
            catch (Exception ex)
            {
                try { _bakeBlitHook?.Dispose(); } catch (Exception d) { _logger.LogDebug(d, "[Nameplate] blit hook cleanup failed"); }
                _bakeBlitHook = null;
                _bakeBlitHookRetryAt = DateTime.UtcNow + BakeHookRetryDelay;
                _logger.LogDebug(ex, "[Nameplate] DrawBaked signature hook failed, retrying in {s}s",
                    (int)BakeHookRetryDelay.TotalSeconds);
            }
        }
    }

    private unsafe void BakeDrawDetour(AddonNamePlate.BakePlateRenderer* self, AtkTextNode* node)
    {
        _bakeDrawHook!.Original(self, node);
        // capture AFTER the draw: PrepareNode mutates the node for this draw
        // and the resolved ScreenX is written while it renders - reading
        // before Original returned last frame's state. Bake passes run at
        // scale 1 in bake-texture space, so those must not overwrite it.
        if (self->CurrentBakeData != null)
            return;
        try
        {
            CaptureDrawnPlate(node);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] draw-time capture failed");
        }
    }

    private unsafe void BakeBlitDetour(AddonNamePlate.BakePlateRenderer* self,
        AtkTextNode* node, AddonNamePlate.BakeData* bakeData)
    {
        _bakeBlitHook!.Original(self, node, bakeData);
        try
        {
            CaptureDrawnPlate(node);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] blit-time capture failed");
        }
    }

    private unsafe void BakePrepareDetour(AddonNamePlate.BakePlateRenderer* self,
        AddonNamePlate.TextDrawArea* outArea, AtkFontManager.FontSet* fontSet, AtkTextNode* node)
    {
        _bakePrepareHook!.Original(self, outArea, fontSet, node);
        // bake passes report the bake's atlas rect, not a screen area - only
        // the direct-draw pass answers where text really lands
        if (self->CurrentBakeData != null)
            return;
        try
        {
            CaptureDrawArea(node, outArea);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] prepare-time area capture failed");
        }
    }

    private unsafe void CaptureDrawArea(AtkTextNode* node, AddonNamePlate.TextDrawArea* area)
    {
        if (node == null || area == null || _namePlateAddon == null)
            return;
        var objects = _namePlateAddon->NamePlateObjectArray;
        if (objects == null)
            return;
        for (var i = 0; i < AddonNamePlate.NumNamePlateObjects; i++)
        {
            if (objects[i].NameText != node)
                continue;
            var r = area->Rect;
            _plateAreaRect[i] = new Vector4(r.Left, r.Top, r.Right, r.Bottom);
            _plateAreaAlign[i] = (byte)area->Alignment;
            _plateAreaFrame[i] = _drawFrameCounter;
            break;
        }
    }

    private unsafe void CaptureDrawnPlate(AtkTextNode* node)
    {
        if (node == null || _namePlateAddon == null)
            return;
        var objects = _namePlateAddon->NamePlateObjectArray;
        if (objects == null)
            return;
        for (var i = 0; i < AddonNamePlate.NumNamePlateObjects; i++)
        {
            if (objects[i].NameText != node)
                continue;
            var rn = (AtkResNode*)node;
            var tm = rn->Transform;
            var tsx = MathF.Sqrt(tm.M11 * tm.M11 + tm.M12 * tm.M12);
            var tsy = MathF.Sqrt(tm.M21 * tm.M21 + tm.M22 * tm.M22);
            if (tsx <= 0f)
                tsx = rn->GetScaleX();
            if (tsy <= 0f)
                tsy = rn->GetScaleY();
            _plateDrawnState[i] = new Vector4(
                rn->ScreenX + rn->Width * tsx * 0.5f,
                rn->ScreenY + rn->Height * tsy, tsx, tsy);
            _plateDrawnFrame[i] = _drawFrameCounter;
            break;
        }
    }

    public unsafe Vector2? GetIconAnchor(int plateIndex, bool displayTitle, bool prefixTitle)
    {
        if (plateIndex is < 0 or >= MaxNamePlateObjects)
            return null;
        var addonPtr = _gameGui.GetAddonByName("NamePlate", 1);
        if (addonPtr.IsNull)
            return null;
        var addon = (AddonNamePlate*)addonPtr.Address;
        if (addon->NamePlateObjectArray == null)
            return null;

        var plate = addon->NamePlateObjectArray + plateIndex;
        var rootComponent = plate->RootComponentNode;
        if (rootComponent == null || !rootComponent->AtkResNode.IsVisible() || rootComponent->AtkResNode.Alpha_2 == 0)
            return null;

        var textNode = (AtkResNode*)plate->NameText;
        if (textNode == null || plate->TextW <= 0)
            return null;

        var collision = (AtkResNode*)plate->NameplateCollision;
        var text = plate->NameText;
        // the nameplate-size option lives in the component's accumulated
        // transform (verified: col tm = NamePlateSize × distance / 10000),
        // not in the node's local scale or the number array's Scale field -
        // the transform magnitude is the true rendered extent factor
        var cs = NodeTransformScale(collision);
        var ts = NodeTransformScale(textNode);
        var ps = cs.X > 0.01f ? cs.X
            : ts.X > 0.01f ? ts.X * 2f
            : PlateScale(plateIndex);
        if (cs.X <= 0.01f)
            cs = new Vector2(ps);
        if (ts.X <= 0.01f)
            ts = new Vector2(ps * 0.5f);
        float endX;
        float centerY;
        if (collision != null && collision->Width > 0)
        {
            endX = collision->ScreenX + collision->Width * cs.X;
            var collisionH = collision->Height * cs.Y;
            var nameLineH = text->FontSize > 0
                ? text->FontSize * ts.Y
                : collisionH * 0.5f;
            nameLineH = MathF.Min(nameLineH, collisionH);
            // The game's own name-icon slot tracks the name line through any
            // title layout; its center sits a touch above the visual text
            // center, so nudge down by a fraction of the line height.
            var nameIcon = (AtkResNode*)plate->NameIcon;
            if (nameIcon != null && nameIcon->Height > 0)
            {
                var nis = NodeTransformScale(nameIcon).Y;
                centerY = nameIcon->ScreenY + nameIcon->Height
                    * (nis > 0.01f ? nis : ps) * 0.5f + nameLineH * 0.1f;
            }
            else
            {
                            centerY = !displayTitle
                    ? collision->ScreenY + collisionH * 0.5f
                    : prefixTitle
                        ? collision->ScreenY + collisionH - nameLineH * 0.45f
                        : collision->ScreenY + nameLineH * 0.55f;
            }
        }
        else
        {
            var textW = MathF.Min(textNode->Width, plate->TextW);
            endX = textNode->ScreenX + (textNode->Width + textW) * ts.X * 0.5f;
            centerY = textNode->ScreenY + textNode->Height * ts.Y * 0.5f;
        }
        return new Vector2(endX, centerY);
    }

    /// <summary>Measures one text line's byte range through the node's own
    /// GetTextDrawSize so each line is fitted against its real draw width.
    /// The range is copied into a NUL-terminated buffer and measured to the
    /// end (end=-1): the native end parameter over-reads past the byte count
    /// and lets a trailing line break grow the height into the next line.</summary>
    private unsafe void MeasureLine(AtkTextNode* node, byte* text, int start, int end, int slot)
    {
        if (node == null || text == null || start < 0 || end <= start)
            return;
        var len = end - start;
        ushort lw = 0, lh = 0;
        if (len <= 1024)
        {
            Span<byte> buf = stackalloc byte[len + 1];
            new ReadOnlySpan<byte>(text + start, len).CopyTo(buf);
            fixed (byte* bp = buf)
                node->GetTextDrawSize(&lw, &lh, bp, 0, -1, true);
        }
        _plateLineSize[slot] = new Vector2(lw, lh);
    }

    /// <summary>Recovers every payload element's true pen position through the
    /// node's own GetTextDrawSize: measuring the suffix starting at element k
    /// gives the draw width of everything after its pen, so
    /// penX_k = lineWidth - suffixWidth_k - the game's own layout with all
    /// kerning, spacing and icon advances already inside. Cells come out as
    /// [pen_k, pen_k+1] line-relative pairs (-1 = unmeasured).</summary>
    private unsafe void BuildPenCells(AtkTextNode* node, byte* np, int index)
    {
        var seq = _plateGlyphSeq[index];
        var ends = _plateGlyphEnds[index];
        if (node == null || np == null || seq == null || ends == null
            || ends.Length != seq.Length)
        {
            _plateCells[index] = null;
            _plateCellsW[index * 2] = 0f;
            _plateCellsW[index * 2 + 1] = 0f;
            return;
        }
        var lb = _plateLineBytes[index];
        var cells = new float[seq.Length * 2];
        for (var k = 0; k < cells.Length; k++)
            cells[k] = -1f;
        var lineStart = 0;
        var lineNo = 0;
        Span<byte> lineBuf = stackalloc byte[1025];
        for (var i = 0; i <= seq.Length && lineNo < 2; i++)
        {
            if (i < seq.Length && seq[i] != -3)
                continue;
            var bs = lineNo == 0 ? (int)lb.X : (int)lb.Z;
            var be = lineNo == 0 ? (int)lb.Y : (int)lb.W;
            var lineW = _plateLineSize[index * 2 + lineNo].X;
            if (bs >= 0 && be > bs && lineW > 0.5f)
            {
                // the line is copied into a NUL-terminated buffer once so every
                // suffix measure stops at the real line end - the native end
                // parameter over-reads into the next line (see MeasureLine)
                var len = be - bs;
                if (len <= 1024)
                {
                    new ReadOnlySpan<byte>(np + bs, len).CopyTo(lineBuf);
                    lineBuf[len] = 0;
                    fixed (byte* bp = lineBuf)
                    {
                        var measureW = lineW;
                        var conv = 1f;
                        // suffixes measure in the finer layout domain -
                        // converting back through lineW/lw keeps most of the
                        // sub-pixel precision scaled ushorts would quantize away
                        ushort lw = 0, lh = 0;
                        node->GetTextDrawSize(&lw, &lh, bp, 0, -1, false);
                        var lineScaleless = lw > 0;
                        if (lineScaleless)
                        {
                            measureW = lw;
                            conv = lineW / lw;
                        }
                        for (var k = lineStart; k < i; k++)
                        {
                            var startByte = k == lineStart ? bs : ends[k - 1];
                            var rel = startByte - bs;
                            if (rel < 0 || rel >= len)
                            {
                                cells[k * 2] = lineW;
                                continue;
                            }
                            ushort w = 0, h = 0;
                            node->GetTextDrawSize(&w, &h, bp + rel, 0, -1, !lineScaleless);
                            cells[k * 2] = Math.Max(0f, measureW - w) * conv;
                        }
                    }
                }
                for (var k = lineStart; k < i; k++)
                    cells[k * 2 + 1] = k + 1 < i ? cells[(k + 1) * 2] : lineW;
                _plateCellsW[index * 2 + lineNo] = lineW;
            }
            lineStart = i + 1;
            lineNo++;
        }
        _plateCells[index] = cells;
    }

    /// <summary>Decodes the color argument of a SetColor/EdgeColor/
    /// UIForeground/UIGlow macro into an alpha byte. A stackcolor placeholder
    /// pops the color back to the default (opaque); a bare 0 expression keeps
    /// the current state and returns -1. UI color macros carry a UIColor
    /// sheet row id that resolves through the module's color holder.</summary>
    private static unsafe int DecodeMacroAlpha(ReadOnlySpan<byte> text, int j, int plen, byte type)
    {
        var end = Math.Min(j + plen, text.Length);
        if (j >= end)
            return 255;
        if (text[j] == ExprStackColor)
            return 255;
        if (!TryDecodeExprInt(text, j, end, out var v) || v == 0)
            return -1;
        var c = v;
        if (type is MacroUIForeground or MacroUIGlow)
        {
            if (v >= 4096)
                return -1;
            var module = RaptureAtkModule.Instance();
            c = module != null ? module->AtkUIColorHolder.GetColor(false, v) : 0xFF000000u;
        }
        return (int)(c >> 24 & 0xFF);
    }

    /// <summary>SeString expression integer decode on a span - small ints
    /// store value+1, 0xF0|n prefixes n big-endian payload bytes.</summary>
    private static bool TryDecodeExprInt(ReadOnlySpan<byte> s, int i, int end, out uint value)
    {
        value = 0;
        if (i >= end)
            return false;
        var b = s[i];
        if (b < 0xD0)
        {
            value = (uint)(b - 1);
            return true;
        }
        if (b is >= 0xF0 and <= 0xFE)
        {
            var n = b & 0x0F;
            if (i + 1 + n > end)
                return false;
            for (var k = 0; k < n; k++)
                value = (value << 8) | s[i + 1 + k];
            return true;
        }
        return false;
    }

    private static int ReadSeInt(ReadOnlySpan<byte> s, ref int j)
    {
        if (j >= s.Length)
            return -1;
        var b = s[j++];
        if (b < 0xF0)
            return b - 1;
        if (b is >= 0xF0 and <= 0xFE)
        {
            var extra = b & 0x0F;
            if (j + extra > s.Length)
                return -1;
            var v = 0;
            while (extra-- > 0)
                v = (v << 8) | s[j++];
            return v;
        }
        return -1;
    }

    /// <summary>Decodes the node text into big-endian utf8 glyph keys plus
    /// markers: -2 = unreadable icon payload, -3 = line break,
    /// -1000-id = icon (0x12), -2000-id = icon2 (0x1E, remappable).
    /// <paramref name="lineBytes"/> gets the byte ranges (start, end-exclusive)
    /// of the first two text lines for per-line GetTextDrawSize calls.
    /// <paramref name="elemEnds"/> gets the payload byte offset right after
    /// each sequence element - the input for per-prefix measurements.
    /// <paramref name="elemAlpha"/> gets a per-element ink alpha (0-255, min
    /// of the current fg/edge macro color) once a color macro exists -
    /// companion owner names ride in translucent colors the mask must not
    /// cut at full strength.</summary>
    private static unsafe int[]? DecodeGlyphSequence(ReadOnlySpan<byte> text, out Vector4 lineBytes,
        out int[]? elemEnds, out byte[]? elemAlpha)
    {
        lineBytes = new Vector4(0, -1, -1, -1);
        elemEnds = null;
        elemAlpha = null;
        if (text.IsEmpty)
            return null;
        var seq = new List<int>(64);
        var ends = new List<int>(64);
        List<byte>? alphas = null;
        var fgAlpha = 255;
        var edgeAlpha = 255;
        var i = 0;
        var e0 = -1;
        var s1 = -1;
        var e1 = -1;
        while (i < text.Length)
        {
            var b = text[i];
            if (b == 0x02 && i + 2 < text.Length)
            {
                var type = text[i + 1];
                var j = i + 2;
                var plen = ReadSeInt(text, ref j);
                if (plen < 0 || j + plen > text.Length)
                    break;
                var payloadEnd = j + plen;
                if (payloadEnd < text.Length && text[payloadEnd] == 0x03)
                    payloadEnd++;
                if (type == 0x12 || type == 0x1E)
                {
                    var pj = j;
                    var iconId = ReadSeInt(text, ref pj);
                    // icon2 (0x1E) goes through the controller remap table, a
                    // plain icon (0x12) addresses the gfd sheet directly
                    seq.Add(iconId >= 0
                        ? (type == 0x1E ? -2000 - iconId : -1000 - iconId)
                        : -2);
                    ends.Add(payloadEnd);
                    alphas?.Add((byte)Math.Min(fgAlpha, edgeAlpha));
                }
                else if (type == 0x10)
                {
                    seq.Add(-3);
                    ends.Add(payloadEnd);
                    alphas?.Add((byte)Math.Min(fgAlpha, edgeAlpha));
                    if (e0 < 0) { e0 = i; s1 = payloadEnd; }
                    else if (e1 < 0) { e1 = i; }
                }
                else if (type is MacroColor or MacroEdgeColor or MacroUIForeground or MacroUIGlow)
                {
                    var na = DecodeMacroAlpha(text, j, plen, type);
                    if (na >= 0)
                    {
                        if (type is MacroColor or MacroUIForeground)
                            fgAlpha = na;
                        else
                            edgeAlpha = na;
                        if (alphas == null)
                        {
                            alphas = new List<byte>(64);
                            for (var k = 0; k < seq.Count; k++)
                                alphas.Add(255);
                        }
                    }
                }
                i = payloadEnd;
                continue;
            }
            if (b == 0x0A)
            {
                seq.Add(-3);
                ends.Add(i + 1);
                alphas?.Add((byte)Math.Min(fgAlpha, edgeAlpha));
                if (e0 < 0) { e0 = i; s1 = i + 1; }
                else if (e1 < 0) { e1 = i; }
                i++;
                continue;
            }
            if (b == 0x0D)
            {
                i++;
                continue;
            }
            var len = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : 4;
            uint v = 0;
            for (var k = 0; k < len && i + k < text.Length; k++)
                v = (v << 8) | text[i + k];
            seq.Add((int)v);
            ends.Add(Math.Min(i + len, text.Length));
            alphas?.Add((byte)Math.Min(fgAlpha, edgeAlpha));
            i += len;
        }
        lineBytes = new Vector4(0, e0 >= 0 ? e0 : text.Length, s1, s1 >= 0 ? (e1 >= 0 ? e1 : text.Length) : -1);
        elemEnds = ends.Count > 0 ? ends.ToArray() : null;
        elemAlpha = alphas?.ToArray();
        return seq.Count > 0 ? seq.ToArray() : null;
    }


    internal static unsafe string? ReadPlateString(byte* ptr)
    {
        if (ptr == null)
            return null;
        var span = new ReadOnlySpan<byte>(ptr, 512);
        var len = span.IndexOf((byte)0);
        if (len < 0)
            return null;
        var text = SeString.Parse(span[..len]).TextValue;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Decodes a Color (0x13) / EdgeColor (0x14) macro RawPayload into an
    /// RRGGBBAA color. The macro data is a single integer expression holding
    /// a B8G8R8A8 value; a stackcolor placeholder pops the color (null out).
    /// Returns false when the payload is not the requested macro, or when the
    /// expression means "reuse current" (0) - the caller keeps its state.
    /// </summary>
    internal static bool TryDecodeMacroColor(RawPayload raw, byte macro, out uint? color)
    {
        color = null;
        var data = raw.Data; // 0x02 <macro> <lenExpr> <expr> 0x03
        if (data.Length < 5 || data[0] != 0x02 || data[1] != macro)
        {
            return false;
        }
        var i = 2 + ExpressionLength(data, 2);
        if (i >= data.Length - 1)
        {
            return false;
        }
        if (data[i] == ExprStackColor)
        {
            return true; // pop → color stays null
        }
        if (!TryDecodeIntExpression(data, i, out var v) || v == 0)
        {
            return false;
        }
        // B8G8R8A8 packed uint (0xAARRGGBB) → RRGGBBAA
        color = ((v & 0x00FF0000u) << 8) | (v & 0x0000FF00u)
            | ((v & 0x000000FFu) << 8) | (v >> 24);
        return true;
    }

    internal static int ExpressionLength(byte[] data, int i)
    {
        if (i >= data.Length)
        {
            return 0;
        }
        var b = data[i];
        if (b < 0xD0 || b == NameplateReader.ExprStackColor || (b >= 0xD0 && b <= 0xDF))
        {
            return 1;
        }
        if (b >= 0xF0 && b <= 0xFE)
        {
            return 1 + (b & 0x0F);
        }
        return 1;
    }

    internal static bool TryDecodeIntExpression(byte[] data, int i, out uint value)
    {
        value = 0;
        if (i >= data.Length)
        {
            return false;
        }
        var b = data[i];
        if (b < 0xD0)
        {
            value = (uint)(b - 1);
            return true;
        }
        if (b >= 0xF0 && b <= 0xFE)
        {
            var n = b & 0x0F;
            if (i + 1 + n > data.Length)
            {
                return false;
            }
            for (var k = 0; k < n; k++)
            {
                value = (value << 8) | data[i + 1 + k];
            }
            return true;
        }
        return false;
    }
}
