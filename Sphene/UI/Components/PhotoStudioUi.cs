using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Design;
using Sphene.Utils;
using System.Numerics;

namespace Sphene.UI.Components;

/// <summary>
/// Image studio: a two-stage window. The pick stage offers two sources  · 
/// a snipping-tool style screen capture (drag a region straight off the game,
/// optionally aspect-locked to the upload format and with the game HUD
/// hidden) and an embedded file browser with a folder tree and lazy
/// thumbnails · then the edit stage shows the image on a canvas with a
/// draggable crop locked to the upload target's format, plus brightness/
/// saturation/vignette adjustments over a live preview. Opened via
/// <see cref="OpenPhotoStudioMessage"/>; only the rendered
/// <see cref="ImageUploadProcessor.Prepared"/> payload is handed back.
/// </summary>
public sealed class PhotoStudioUi : WindowMediatorSubscriberBase
{
    /// <summary>Which image is being edited; fixes output limits + default aspect.</summary>
    private enum CropKind
    {
        Avatar,
        Banner,
        RowImage,
        CommunityBanner,
        CommunityAvatar,
        CommunityPhoto,
        EventBanner,
        EventPreview,
    }

    /// <summary>Per-kind edit spec.</summary>
    private readonly record struct KindSpec(float Aspect, int TargetWidth, int MaxBytes,
        bool UseWebp, int MinRecommendedWidth, string Title, string Noun);

    private enum Stage
    {
        Pick,
        Edit,
    }

    private enum DragMode
    {
        None,
        Move,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    /// <summary>One folder or image file in the browser listing.</summary>
    private sealed class DirEntry
    {
        public required string Path;
        public required string Name;
        public bool IsDir;
    }

    /// <summary>Decoded thumbnail + source metadata for a browser tile.</summary>
    private sealed class Thumb
    {
        public required byte[] Png;
        public int Width;
        public int Height;
        public bool Animated;
    }

    /// <summary>One node in the browser's folder tree · children load lazily.</summary>
    private sealed class FolderNode
    {
        public required string Path;
        public required string Name;
        public bool Expanded;
        public bool Queried;
        public List<FolderNode>? Children;
    }

    /// <summary>Async decode result tagged with the bytes it belongs to.</summary>
    private sealed class PendingPreview
    {
        public Image<Rgba32>? Image;
        public bool Animated;
        public byte[]? ForBytes;
    }

    /// <summary>A selectable crop preset · aspect 0 means free-form.</summary>
    private readonly record struct CropPreset(string Name, float Aspect, FontAwesomeIcon Icon);

    private static readonly CropPreset[] CropPresets =
    [
        new("Avatar", 1f, FontAwesomeIcon.UserCircle),
        new("Banner", 3f, FontAwesomeIcon.Image),
        new("Community", 3.5f, FontAwesomeIcon.Users),
        new("Event", 16f / 9f, FontAwesomeIcon.CalendarAlt),
        new("List row", SpheneUI.Profile.RowImageAspect, FontAwesomeIcon.Bars),
        new("Free crop", 0f, FontAwesomeIcon.CropAlt),
    ];

    private const string TextureKey = "photostudio:source";
    private const float HandleHitPixels = 14f;
    private const float MinSourcePixels = 32f;
    private const int PreviewMaxDimension = 768;
    private const int ThumbMaxDimension = 160;
    private const int MaxThumbEntries = 500;
    private const int MaxThumbTasks = 6;
    private const double PreviewRegenInterval = 0.066;
    private const float CanvasZoomMax = 12f;

    // Pick stage layout (unscaled)
    private const float TileWidth = 112f;
    private const float TileThumbHeight = 84f;
    private const float TreePanelWidth = 200f;

    // Edit stage layout (unscaled)
    private const float SidebarWidth = 236f;
    private const float PresetRowHeight = 30f;

    // Capture overlay (unscaled)
    private const float SnipPillPadX = 12f;
    private const float SnipPillPadY = 6f;
    private const float SnipPillGap = 8f;
    private const float SnipBarHeight = 40f;
    private const float SnipMinSide = 24f;
    private const float SnipActionHeight = 30f;
    private const double SnipUiSettleSeconds = 0.6;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    private static readonly char[] DirSeparators =
    [
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
    ];

    private const ImGuiWindowFlags SnipWindowFlags = ImGuiWindowFlags.NoDecoration
        | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ScreenCaptureService _captureService;
    private readonly GameUiVisibility _gameUiVisibility;
    private readonly WindowSystem _windowSystem;
    private readonly List<IWindow> _hiddenWindows = [];

    // ── Edit-stage state ────────────────────────────────────────────────
    private Stage _stage = Stage.Pick;
    private CropKind _kind;
    private KindSpec _spec;
    private float _aspect = 1f;
    private int _preset;
    private byte[] _imageBytes = [];
    private int _imageWidth;
    private int _imageHeight;
    private Vector4 _crop; // normalized x, y, w, h over the image
    private Action<byte[]>? _onConfirm;
    private byte[]? _pendingResult;
    private bool _cropFinished;
    private bool _cropFailed;
    private bool _applyBusy;
    private DragMode _drag = DragMode.None;
    private Vector2 _grabOffset;
    private Image<Rgba32>? _previewSrc;
    private byte[]? _previewBytes;
    private string _previewKey = string.Empty;
    private string? _previewShownKey;
    private readonly List<string> _previewStaleKeys = [];
    private double _lastPreviewRegen;
    private bool _previewDirty;
    private bool _previewReady;
    private bool _animated;
    private bool _zoomToCrop;
    private Vector2 _zoomMin;
    private Vector2 _zoomSize;
    private bool _zoomValid;
    private float _bright;      // -100..100
    private float _sat = 100f;  // 0..200
    private float _vig;         // 0..100
    private PendingPreview? _pendingPreview;

    // File flow: yields an ImageUploadProcessor.Prepared payload on confirm.
    private string? _filePath;
    private string _sourceLabel = string.Empty;
    private Action<ImageUploadProcessor.Prepared>? _onPrepared;
    private ImageUploadProcessor.Prepared? _pendingPrepared;
    private bool _loadingFile;
    private bool _loadFailed;
    private Action? _pendingOpen;

    // ── Pick-stage browser state ────────────────────────────────────────
    private string _browseDir = string.Empty;
    private string _pathEdit = string.Empty;
    private List<DirEntry>? _entries;
    private string? _listingError;
    private int _browseVersion;
    private List<FolderNode>? _treeRoots;
    private string? _selectedPath;
    private long _selectedSize;
    private byte[]? _selectionBytes;
    private readonly ConcurrentDictionary<string, Thumb?> _thumbs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _thumbBusy =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Capture overlay state ───────────────────────────────────────────
    private bool _captureOverlay;
    private Vector4 _snip = Vector4.Zero;   // x, y, w, h in display px
    private Vector4 _snipBarRect = Vector4.Zero; // top bar rect, screen px · swallows clicks
    private DragMode _snipDrag = DragMode.None;
    private Vector2 _snipGrab;
    private bool _snipLockAspect;
    private bool _snipHideUi;
    private double _snipUiToggledAt;
    private string? _captureError;

    public PhotoStudioUi(ILogger<PhotoStudioUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        IDalamudPluginInterface pluginInterface,
        ScreenCaptureService captureService, GameUiVisibility gameUiVisibility,
        WindowSystem windowSystem)
        : base(logger, mediator, "Image Studio###SphenePhotoStudio", performanceCollectorService)
    {
        _pluginInterface = pluginInterface;
        _captureService = captureService;
        _gameUiVisibility = gameUiVisibility;
        _windowSystem = windowSystem;
        IsOpen = false;
        Size = new Vector2(1240, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(1100, 620),
            MaximumSize = new Vector2(1700, 1200),
        };

        Mediator.Subscribe<OpenPhotoStudioMessage>(this, (msg) =>
            OpenStudio(msg.FilePath, msg.Target, msg.OnPrepared));
    }

    protected override FontAwesomeIcon? ChromeIcon => FontAwesomeIcon.Camera;

    private static KindSpec SpecFor(CropKind kind) => kind switch
    {
        CropKind.Avatar => new(1f, 256, 250 * 1024, false, 128,
            "profile picture", "profile picture"),
        CropKind.Banner => new(3f, 1200, 400 * 1024, false, 600,
            "profile banner", "profile banner"),
        CropKind.RowImage => new(SpheneUI.Profile.RowImageAspect, 512, 400 * 1024, false, 512,
            "list row image", "list row image"),
        CropKind.CommunityBanner => new(3.5f, 1400, 850 * 1024, true, 1200,
            "community banner", "community banner"),
        CropKind.CommunityAvatar => new(1f, 256, 250 * 1024, true, 128,
            "community avatar", "community avatar"),
        CropKind.EventBanner => new(3f, 1200, 400 * 1024, false, 600,
            "event banner", "event banner"),
        CropKind.EventPreview => new(16f / 9f, 800, 300 * 1024, false, 400,
            "event preview", "event preview"),
        _ => new(0f, 1600, 850 * 1024, true, 800,
            "gallery photo", "gallery photo"),
    };

    private static CropKind KindFor(PhotoStudioTarget target) => target switch
    {
        PhotoStudioTarget.CommunityBanner => CropKind.CommunityBanner,
        PhotoStudioTarget.CommunityAvatar => CropKind.CommunityAvatar,
        PhotoStudioTarget.ProfileAvatar => CropKind.Avatar,
        PhotoStudioTarget.ProfileBanner => CropKind.Banner,
        PhotoStudioTarget.ProfileRowImage => CropKind.RowImage,
        PhotoStudioTarget.EventBanner => CropKind.EventBanner,
        PhotoStudioTarget.EventPreview => CropKind.EventPreview,
        _ => CropKind.CommunityPhoto,
    };

    private static int PresetIndexFor(float aspect)
    {
        for (var i = 0; i < CropPresets.Length; i++)
        {
            if (MathF.Abs(CropPresets[i].Aspect - aspect) < 0.01f)
            {
                return i;
            }
        }
        return CropPresets.Length - 1;
    }

    /// <summary>Entry point for the mediator message · null path opens the pick stage.</summary>
    private void OpenStudio(string? filePath, PhotoStudioTarget target,
        Action<ImageUploadProcessor.Prepared> onPrepared)
    {
        _onPrepared = onPrepared;
        _kind = KindFor(target);
        _spec = SpecFor(_kind);
        _aspect = _spec.Aspect;
        _preset = PresetIndexFor(_aspect);
        IsOpen = true;
        if (filePath == null)
        {
            EnterPick(string.IsNullOrEmpty(_browseDir) ? DefaultBrowseDir() : _browseDir);
            return;
        }
        BeginLoadFile(filePath);
    }

    // ─────────────────────────── Pick stage ─────────────────────────────

    private void EnterPick(string dir)
    {
        _stage = Stage.Pick;
        WindowName = "Image Studio###SphenePhotoStudio";
        _loadFailed = false;
        _captureError = null;
        NavigateTo(dir);
    }

    private void DrawPick()
    {
        var scale = SpheneUI.Scale;
        DrawPickHeader();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        DrawBrowser(scale);
    }

    /// <summary>One slim row: capture action left, target badge right.</summary>
    private void DrawPickHeader()
    {
        var lineMin = ImGui.GetCursorScreenPos();
        if (SpheneUI.Controls.PrimaryButton("Capture screen region", FontAwesomeIcon.Camera))
        {
            BeginCapture();
            return;
        }
        SpheneUI.AttachTooltip("Snip a region straight off your screen · "
            + "drag free-form, the game HUD can be hidden");
        var lineH = ImGui.GetItemRectMax().Y - lineMin.Y;

        var badgeText = _aspect > 0f
            ? $"{_spec.Noun} · {_aspect:0.#}:1"
            : $"{_spec.Noun} · free crop";
        var badgeW = SpheneUI.Layout.BadgeWidth(badgeText);
        var gap = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        SpheneUI.Controls.DockToCardEdge(badgeW + gap + SpheneUI.Metrics.ControlHeight);
        var dockPos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(dockPos.X,
            lineMin.Y + MathF.Max(0f, (lineH - SpheneUI.Layout.BadgeHeight) * 0.5f)));
        SpheneUI.Layout.Badge(badgeText, SpheneTheme.Current.AccentCyan);
        ImGui.SameLine(0f, gap);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X,
            lineMin.Y + MathF.Max(0f, (lineH - SpheneUI.Metrics.ControlHeight) * 0.5f)));
        SpheneUI.Layout.PageHelp("imagestudio-pick", () =>
        {
            SpheneUI.Text.MutedWrapped(
                "Pick an image from your disk or capture a screen region - " +
                "then crop it to fit the destination format before uploading.\n\n" +
                "The tree jumps between folders and drives; the right pane previews and confirms.",
                ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });
        ImGui.SetCursorScreenPos(new Vector2(lineMin.X, lineMin.Y + lineH));
    }

    // ─────────────────────────── Browser ────────────────────────────────

    /// <summary>Starts an async directory listing; stale results are dropped by version.</summary>
    private void NavigateTo(string dir)
    {
        var version = ++_browseVersion;
        _browseDir = dir;
        _pathEdit = dir;
        _entries = null;
        _listingError = null;
        ClearSelection();
        _treeRoots ??= BuildTreeRoots();
        MergeDriveRoots();
        _ = Task.Run(() =>
        {
            try
            {
                var di = new DirectoryInfo(dir);
                var list = new List<DirEntry>(256);
                foreach (var d in di.EnumerateDirectories())
                {
                    if (d.Name.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    list.Add(new DirEntry { Path = d.FullName, Name = d.Name, IsDir = true });
                }
                list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var fileStart = list.Count;
                foreach (var f in di.EnumerateFiles())
                {
                    if (f.Name.StartsWith(".", StringComparison.Ordinal)
                        || !ImageExtensions.Contains(Path.GetExtension(f.Name)))
                    {
                        continue;
                    }
                    list.Add(new DirEntry { Path = f.FullName, Name = f.Name });
                }
                list.Sort(fileStart, list.Count - fileStart,
                    Comparer<DirEntry>.Create((a, b) =>
                        File.GetLastWriteTimeUtc(b.Path).CompareTo(File.GetLastWriteTimeUtc(a.Path))));
                if (version == _browseVersion)
                {
                    _entries = list;
                }
            }
            catch (Exception ex)
            {
                if (version == _browseVersion)
                {
                    _listingError = ex.Message;
                    _entries = [];
                }
            }
        });
        ExpandTreeTo(dir);
    }

    private static string DefaultBrowseDir()
    {
        // FFXIV screenshots live under "My Games" next to the game's user data  · 
        // the most likely place an image pick is headed.
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var shots = Path.Combine(docs, "My Games", "FINAL FANTASY XIV - A Realm Reborn",
            "screenshots");
        if (Directory.Exists(shots))
        {
            return shots;
        }
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Directory.Exists(pictures) ? pictures
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Lazily decodes a thumbnail + dimensions for one file · only while visible.</summary>
    private Thumb? ThumbFor(string path)
    {
        if (_thumbs.TryGetValue(path, out var cached))
        {
            return cached;
        }
        if (_thumbBusy.Count >= MaxThumbTasks || !_thumbBusy.TryAdd(path, 0))
        {
            return null;
        }
        _ = Task.Run(() =>
        {
            Thumb? thumb = null;
            try
            {
                var info = Image.Identify(path);
                if (info != null)
                {
                    using var image = Image.Load<Rgba32>(path);
                    using var small = Downscaled(image, ThumbMaxDimension);
                    using var ms = new MemoryStream();
                    small.SaveAsPng(ms);
                    thumb = new Thumb
                    {
                        Png = ms.ToArray(),
                        Width = info.Width,
                        Height = info.Height,
                        Animated = image.Frames.Count > 1,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail decode failed for {File}", path);
            }
            if (_thumbs.Count >= MaxThumbEntries)
            {
                _thumbs.Clear();
            }
            _thumbs[path] = thumb;
            _thumbBusy.TryRemove(path, out _);
        });
        return null;
    }

    private void DrawBrowser(float scale)
    {
        // Navigation row: up + editable path + refresh.
        var parent = Directory.GetParent(_browseDir)?.FullName;
        if (SpheneUI.Controls.IconButton("##nav-up", FontAwesomeIcon.ChevronUp,
                enabled: parent != null) && parent != null)
        {
            NavigateTo(parent);
        }
        SpheneUI.AttachTooltip("Go up one folder");
        ImGui.SameLine(0f, SpheneUI.Spacing.Inline.Scaled);
        var refreshW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Sync, "Refresh");
        var pathW = MathF.Max(80f * scale,
            SpheneUI.Controls.BoundedAvailX() - refreshW - SpheneUI.Spacing.Inline.Scaled);
        if (SpheneUI.Controls.InputText("##browse-path", ref _pathEdit, 512,
                ImGuiInputTextFlags.EnterReturnsTrue, preferredWidth: pathW)
            && Directory.Exists(_pathEdit))
        {
            NavigateTo(_pathEdit);
        }
        SpheneUI.AttachTooltip("Current folder · type or paste a path and press Enter");
        ImGui.SameLine(0f, SpheneUI.Spacing.Inline.Scaled);
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Sync, "Refresh"))
        {
            NavigateTo(_browseDir);
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);

        // Body: folder tree | image grid | preview pane · the classic
        // three-column file browser. Footer is reserved below.
        var footerH = ImGui.GetFrameHeightWithSpacing()
            + SpheneUI.Spacing.X4.Scaled + SpheneUI.Spacing.X8.Scaled;
        var bodyH = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - footerH);
        var avail = MathF.Max(200f * scale, SpheneUI.Controls.BoundedAvailX());
        var gap = SpheneUI.Spacing.CardGap.Scaled;
        var previewW = Math.Clamp(avail * 0.30f, 190f * scale, 380f * scale);
        var treeW = Math.Clamp(TreePanelWidth * scale, 120f * scale,
            MathF.Max(120f * scale, avail * 0.26f));
        var gridW = MathF.Max(140f * scale,
            avail - treeW - previewW - gap * 2f);
        previewW = MathF.Max(140f * scale, avail - treeW - gap * 2f - gridW);
        var bodyMin = ImGui.GetCursorScreenPos();

        // Tree panel: tonal surface background + scrollable nodes.
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(bodyMin, bodyMin + new Vector2(treeW, bodyH),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()),
            8f * scale);
        if (SpheneUI.Layout.BeginScrollArea("##foldertree",
                new Vector2(treeW, bodyH), border: false))
        {
            DrawFolderTree(scale);
            SpheneUI.Layout.EndScrollArea();
        }

        // Grid panel in the middle.
        ImGui.SetCursorScreenPos(bodyMin + new Vector2(treeW + gap, 0f));
        if (SpheneUI.Layout.BeginScrollArea("##photo-browse",
                new Vector2(gridW, bodyH), border: false, noPadding: true))
        {
            DrawBrowsePanel(scale);
            SpheneUI.Layout.EndScrollArea();
        }

        // Preview pane on the right · click tiles to preview, confirm there.
        ImGui.SetCursorScreenPos(bodyMin
            + new Vector2(treeW + gap + gridW + gap, 0f));
        DrawPreviewPane(new Vector2(previewW, bodyH), scale);

        ImGui.SetCursorScreenPos(bodyMin + new Vector2(0f, bodyH));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        DrawBrowserFooter();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    /// <summary>Explorer-style preview column: fitted image, meta, confirm.</summary>
    private void DrawPreviewPane(Vector2 size, float scale)
    {
        var min = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, min + size,
            SpheneUI.ColorUtils.ToU32(SpheneUI.Layout.NavRailBandColor()),
            8f * scale);

        var pad = SpheneUI.Spacing.X8.Scaled;
        var innerMin = min + new Vector2(pad);
        var innerW = size.X - pad * 2f;
        var textH = ImGui.GetTextLineHeight();
        var gap = SpheneUI.Spacing.X4.Scaled;
        var frameH = ImGui.GetFrameHeight();
        var path = _selectedPath != null && File.Exists(_selectedPath)
            ? _selectedPath : null;
        var hasFile = path != null;
        var infoH = hasFile ? textH * 2f + gap : 0f;
        var buttonH = hasFile ? frameH + gap : 0f;
        var imgH = MathF.Max(48f * scale,
            size.Y - pad * 2f - infoH - buttonH - gap);
        var imgMin = innerMin;
        var imgMax = imgMin + new Vector2(innerW, imgH);

        var wrap = hasFile && _selectionBytes != null
            ? SpheneUI.Media.Texture($"photostudio:sel:{path}", _selectionBytes)
            : null;
        if (wrap != null)
        {
            SpheneUI.Media.ImageInRect(dl, wrap, imgMin, imgMax,
                ImageFit.Fit, rounding: 6f * scale);
        }
        else
        {
            SpheneUI.Media.PlaceholderInRect(dl, imgMin, imgMax,
                rounding: 6f * scale);
            var hint = hasFile ? "Loading preview…" : "Click an image to preview";
            var hintW = ImGui.CalcTextSize(hint).X;
            if (hintW <= innerW)
            {
                dl.AddText(new Vector2(
                        innerMin.X + (innerW - hintW) * 0.5f,
                        imgMin.Y + (imgH - textH) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), hint);
            }
        }
        if (!hasFile)
        {
            return;
        }
        var file = path!;

        var textY = imgMax.Y + gap;
        dl.PushClipRect(innerMin, new Vector2(innerMin.X + innerW,
            textY + infoH), true);
        dl.AddText(new Vector2(innerMin.X, textY),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
            SpheneUI.Text.Ellipsize(Path.GetFileName(file), innerW));
        var thumb = ThumbFor(file);
        var meta = thumb != null
            ? $"{thumb.Width}×{thumb.Height} · {FormatSize(_selectedSize)}"
            : FormatSize(_selectedSize);
        if (thumb?.Animated == true)
        {
            meta += " · animated";
        }
        dl.AddText(new Vector2(innerMin.X, textY + textH + gap * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
            SpheneUI.Text.Ellipsize(meta, innerW));
        dl.PopClipRect();

        ImGui.SetCursorScreenPos(new Vector2(innerMin.X,
            min.Y + size.Y - pad - frameH));
        if (SpheneUI.Controls.PrimaryButton("Use image", FontAwesomeIcon.Check))
        {
            _stage = Stage.Edit;
            BeginLoadFile(file);
        }
    }

    /// <summary>Entry count left, cancel action docked to the card edge.</summary>
    private void DrawBrowserFooter()
    {
        var lineMin = ImGui.GetCursorScreenPos();
        var cancelW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Times, "Cancel");
        SpheneUI.Controls.DockToCardEdge(cancelW);
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
        {
            IsOpen = false;
        }
        if (_entries != null)
        {
            var files = 0;
            var dirs = 0;
            foreach (var e in _entries)
            {
                if (e.IsDir) dirs++; else files++;
            }
            var textH = ImGui.GetTextLineHeight();
            ImGui.SetCursorScreenPos(lineMin + new Vector2(0f,
                MathF.Max(0f, (ImGui.GetFrameHeight() - textH) * 0.5f)));
            SpheneUI.Text.Muted($"{files} image{(files == 1 ? string.Empty : "s")}"
                + $" · {dirs} folder{(dirs == 1 ? string.Empty : "s")}");
        }
        ImGui.SetCursorScreenPos(lineMin + new Vector2(0f, ImGui.GetFrameHeight()));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Right pane of the browser: status rows or the tile grid.</summary>
    private void DrawBrowsePanel(float scale)
    {
        if (_listingError != null)
        {
            SpheneUI.Layout.Callout($"Cannot open this folder: {_listingError}",
                FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
            return;
        }
        if (_entries == null)
        {
            SpheneUI.Text.Muted("Loading folder…");
            return;
        }
        if (_entries.Count == 0)
        {
            SpheneUI.Text.MutedWrapped(
                "No images in this folder · pick another folder in the tree.");
            return;
        }
        DrawBrowseGrid(scale);
    }

    // ─────────────────────────── Folder tree ────────────────────────────

    /// <summary>Builds the fixed tree roots · only folders that exist.</summary>
    private static List<FolderNode> BuildTreeRoots()
    {
        var roots = new List<FolderNode>();
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var shots = Path.Combine(docs, "My Games",
            "FINAL FANTASY XIV - A Realm Reborn", "screenshots");
        if (Directory.Exists(shots))
        {
            roots.Add(new FolderNode { Path = shots, Name = "FFXIV Screenshots" });
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        if (Directory.Exists(downloads))
        {
            roots.Add(new FolderNode { Path = downloads, Name = "Downloads" });
        }
        if (Directory.Exists(docs))
        {
            roots.Add(new FolderNode { Path = docs, Name = "Documents" });
        }
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pictures))
        {
            roots.Add(new FolderNode { Path = pictures, Name = "Pictures" });
        }
        if (Directory.Exists(home))
        {
            roots.Add(new FolderNode { Path = home, Name = "Home" });
        }
        // Drive roots are merged asynchronously via MergeDriveRoots -
        // DriveInfo.IsReady probes can stall ~30s on sleeping USB or dead
        // network drives and froze the first open of the studio.
        return roots;
    }

    private Task<List<FolderNode>>? _driveRootsTask;
    private bool _drivesMerged;

    /// <summary>Probes drive letters off-thread · merged into the tree once
    /// ready so a stalling drive can never hitch the UI. Runs exactly once -
    /// without the <see cref="_drivesMerged"/> guard every completed probe
    /// would re-append the same drives forever.</summary>
    private void MergeDriveRoots()
    {
        if (_drivesMerged)
        {
            return;
        }
        _driveRootsTask ??= Task.Run(() =>
        {
            var drives = new List<FolderNode>();
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }
                    var label = drive.VolumeLabel;
                    var letter = drive.Name.TrimEnd(DirSeparators);
                    drives.Add(new FolderNode
                    {
                        Path = drive.RootDirectory.FullName,
                        Name = string.IsNullOrEmpty(label)
                            ? $"Drive {letter}"
                            : $"{label} ({letter})",
                    });
                }
            }
            catch (Exception)
            {
                // best-effort · the known roots still work without drives
            }
            return drives;
        });
        if (_driveRootsTask.IsCompletedSuccessfully)
        {
            _drivesMerged = true;
            _treeRoots?.AddRange(_driveRootsTask.Result);
            _driveRootsTask = null;
        }
    }

    /// <summary>Enumerates a node's subfolders once · off the UI thread.
    /// Called from Draw - a slow or unresponsive directory must never
    /// hitch rendering, children just appear a frame or two later.</summary>
    private static void EnsureChildren(FolderNode node)
    {
        if (node.Queried)
        {
            return;
        }
        node.Queried = true;
        _ = Task.Run(() => LoadChildren(node));
    }

    /// <summary>Blocking variant for the background expansion walk.</summary>
    private static void EnsureChildrenSync(FolderNode node)
    {
        if (node.Queried)
        {
            return;
        }
        node.Queried = true;
        LoadChildren(node);
    }

    private static void LoadChildren(FolderNode node)
    {
        try
        {
            List<FolderNode>? kids = null;
            foreach (var dir in Directory.EnumerateDirectories(node.Path))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                {
                    continue;
                }
                kids ??= [];
                kids.Add(new FolderNode { Path = dir, Name = name });
            }
            kids?.Sort((a, b) => string.Compare(a.Name, b.Name,
                StringComparison.OrdinalIgnoreCase));
            node.Children = kids;
        }
        catch (Exception)
        {
            node.Children = null;
        }
    }

    /// <summary>Expands the tree chain that leads to <paramref name="dir"/>.</summary>
    private void ExpandTreeTo(string dir)
    {
        if (_treeRoots == null || string.IsNullOrEmpty(dir))
        {
            return;
        }
        FolderNode? root = null;
        foreach (var candidate in _treeRoots)
        {
            if (!IsUnder(candidate.Path, dir))
            {
                continue;
            }
            if (root == null || candidate.Path.Length > root.Path.Length)
            {
                root = candidate;
            }
        }
        if (root == null)
        {
            return;
        }

        // the segment walk enumerates every directory on the path - run it
        // on a background thread so deep/slow trees never stall Draw.
        _ = Task.Run(() =>
        {
            var node = root;
            node.Expanded = true;
            var rest = dir[root.Path.Length..].Trim(DirSeparators);
            if (rest.Length == 0)
            {
                return;
            }
            foreach (var segment in rest.Split(DirSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.Length == 0)
                {
                    continue;
                }
                EnsureChildrenSync(node);
                var next = node.Children?.Find(
                    c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    return;
                }
                next.Expanded = true;
                node = next;
            }
        });
    }

    /// <summary>True when <paramref name="dir"/> is <paramref name="root"/> or below it.</summary>
    private static bool IsUnder(string root, string dir)
    {
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (dir.Length == root.Length)
        {
            return true;
        }
        var next = dir[root.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
    }

    /// <summary>Draws every tree node · recursion handles expansion.</summary>
    private void DrawFolderTree(float scale)
    {
        _treeRoots ??= BuildTreeRoots();
        MergeDriveRoots();
        if (_treeRoots.Count == 0)
        {
            SpheneUI.Text.MutedWrapped("No browsable folders found.");
            return;
        }
        foreach (var node in _treeRoots)
        {
            if (DrawFolderNode(node, 0, scale))
            {
                return;
            }
        }
    }

    /// <summary>One tree row: chevron, folder icon, name · children below when open.</summary>
    private bool DrawFolderNode(FolderNode node, int depth, float scale)
    {
        EnsureChildren(node);
        var hasKids = node.Children is { Count: > 0 };
        var indent = depth * 14f * scale;
        var chevronW = 14f * scale;
        var iconW = 16f * scale;
        var rowH = ImGui.GetTextLineHeight() + 6f * scale;
        var rowW = MathF.Max(60f * scale, ImGui.GetContentRegionAvail().X);
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(rowW, rowH);
        var selected = string.Equals(node.Path, _browseDir,
            StringComparison.OrdinalIgnoreCase);

        ImGui.InvisibleButton($"##tree-{node.Path}", new Vector2(rowW, rowH));
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var dl = ImGui.GetWindowDrawList();
        var accent = SpheneTheme.Current.AccentCyan;

        if (selected)
        {
            dl.AddRectFilled(min, max,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.18f)),
                5f * scale);
        }
        else if (hovered)
        {
            dl.AddRectFilled(min, max,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.08f)),
                5f * scale);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var contentX = min.X + 4f * scale + indent;
        var centerY = min.Y + rowH * 0.5f;
        if (hasKids)
        {
            using (SpheneUI.Icons.PushFont())
            {
                var glyph = (node.Expanded ? FontAwesomeIcon.ChevronDown
                    : FontAwesomeIcon.ChevronRight).ToIconString();
                var gSize = ImGui.CalcTextSize(glyph) * 0.8f;
                dl.AddText(ImGui.GetFont(), gSize.Y,
                    new Vector2(contentX + (chevronW - gSize.X) * 0.5f,
                        centerY - gSize.Y * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), glyph);
            }
        }
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = (node.Expanded ? FontAwesomeIcon.FolderOpen
                : FontAwesomeIcon.Folder).ToIconString();
            var gSize = ImGui.CalcTextSize(glyph);
            dl.AddText(ImGui.GetFont(), gSize.Y,
                new Vector2(contentX + chevronW + (iconW - gSize.X) * 0.5f,
                    centerY - gSize.Y * 0.5f),
                SpheneUI.ColorUtils.ToU32(selected ? accent : SpheneUI.Colors.TextSecondary),
                glyph);
        }
        var nameX = contentX + chevronW + iconW + SpheneUI.Spacing.X4.Scaled;
        dl.PushClipRect(new Vector2(nameX, min.Y),
            new Vector2(max.X - 4f * scale, max.Y), true);
        dl.AddText(new Vector2(nameX, centerY - ImGui.GetTextLineHeight() * 0.5f),
            SpheneUI.ColorUtils.ToU32(
                selected ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextSecondary),
            node.Name);
        dl.PopClipRect();

        if (clicked)
        {
            var clickX = ImGui.GetMousePos().X;
            if (hasKids && clickX < contentX + chevronW)
            {
                node.Expanded = !node.Expanded;
            }
            else
            {
                NavigateTo(node.Path);
                return true;
            }
        }

        if (node.Expanded && hasKids)
        {
            foreach (var child in node.Children!)
            {
                if (DrawFolderNode(child, depth + 1, scale))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The scrollable tile grid · folders first, then newest images.</summary>
    private void DrawBrowseGrid(float scale)
    {
        if (_entries == null || _entries.Count == 0)
        {
            return;
        }
        // Tiles stretch between a min and max width so every row fills the
        // grid exactly · no dead strip between the last tile and the pane.
        var gap = SpheneUI.Spacing.X8.Scaled;
        var avail = ImGui.GetContentRegionAvail().X;
        var minTileW = TileWidth * scale;
        var maxTileW = TileWidth * 1.45f * scale;
        var cols = Math.Max(1, (int)MathF.Floor((avail + gap) / (minTileW + gap)));
        var tileW = MathF.Min(maxTileW, (avail - (cols - 1) * gap) / cols);
        var thumbH = tileW * (TileThumbHeight / TileWidth);
        var textH = ImGui.GetTextLineHeight() * 2f + 4f * scale;
        var tileH = thumbH + textH + 10f * scale;
        var rowW = cols * tileW + (cols - 1) * gap;
        var startX = ImGui.GetCursorPos().X + MathF.Max(0f, (avail - rowW) * 0.5f);
        var startY = ImGui.GetCursorPos().Y;
        var dl = ImGui.GetWindowDrawList();

        // Reserve the full grid block so the scroll range covers clipped rows.
        var rows = (_entries.Count + cols - 1) / cols;
        ImGui.SetCursorPos(new Vector2(startX, startY));
        ImGui.Dummy(new Vector2(rowW, rows * (tileH + gap) - gap));

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var x = startX + (i % cols) * (tileW + gap);
            var y = startY + (i / cols) * (tileH + gap);
            ImGui.SetCursorPos(new Vector2(x, y));
            var tileMin = ImGui.GetCursorScreenPos();
            var tileMax = tileMin + new Vector2(tileW, tileH);
            if (!ImGui.IsRectVisible(new Vector2(tileW, tileH)))
            {
                continue;
            }

            ImGui.InvisibleButton($"##browse-tile-{i}", new Vector2(tileW, tileH));
            var hovered = ImGui.IsItemHovered();
            var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            var doubleClicked = hovered
                && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
            var selected = !entry.IsDir && string.Equals(entry.Path, _selectedPath,
                StringComparison.OrdinalIgnoreCase);
            var thumbMin = tileMin + new Vector2(4f * scale);
            var thumbMax = thumbMin + new Vector2(tileW - 8f * scale, thumbH);
            var accent = SpheneTheme.Current.AccentCyan;

            var bg = selected
                ? SpheneUI.ColorUtils.WithAlpha(accent, 0.18f)
                : hovered
                    ? SpheneUI.ColorUtils.WithAlpha(accent, 0.14f)
                    : SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Surface, 0.5f);
            dl.AddRectFilled(tileMin, tileMax, SpheneUI.ColorUtils.ToU32(bg), 6f * scale);
            if (selected || hovered)
            {
                dl.AddRect(tileMin, tileMax,
                    SpheneUI.ColorUtils.ToU32(
                        SpheneUI.ColorUtils.WithAlpha(accent, selected ? 0.8f : 0.7f)),
                    6f * scale, ImDrawFlags.None,
                    (selected ? 2f : 1.5f) * scale);
            }

            if (entry.IsDir)
            {
                DrawFolderTile(dl, thumbMin, thumbMax);
                if (clicked)
                {
                    NavigateTo(entry.Path);
                    return;
                }
            }
            else
            {
                DrawImageTile(dl, entry.Path, thumbMin, thumbMax, scale);
                if (doubleClicked)
                {
                    _stage = Stage.Edit;
                    BeginLoadFile(entry.Path);
                    return;
                }
                if (clicked)
                {
                    SelectFile(entry.Path);
                }
            }
            DrawTileLabel(dl, entry, tileMin, thumbMax, tileW, textH, scale,
                hovered || selected);
        }
    }

    private static void DrawFolderTile(ImDrawListPtr dl,
        Vector2 thumbMin, Vector2 thumbMax)
    {
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = FontAwesomeIcon.Folder.ToIconString();
            var gSize = ImGui.CalcTextSize(glyph) * 1.6f;
            dl.AddText(ImGui.GetFont(), gSize.Y,
                thumbMin + (thumbMax - thumbMin - gSize) * 0.5f,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), glyph);
        }
    }

    private void DrawImageTile(ImDrawListPtr dl, string path,
        Vector2 thumbMin, Vector2 thumbMax, float scale)
    {
        var thumb = ThumbFor(path);
        if (thumb != null)
        {
            var wrap = SpheneUI.Media.Texture($"photostudio:thumb:{path}", thumb.Png);
            if (wrap != null)
            {
                SpheneUI.Media.ImageInRect(dl, wrap, thumbMin, thumbMax,
                    ImageFit.Cover, rounding: 4f * scale);
            }
            else
            {
                SpheneUI.Media.PlaceholderInRect(dl, thumbMin, thumbMax,
                    rounding: 4f * scale);
            }
            DrawTileBadges(dl, thumb, thumbMin, thumbMax, scale);
        }
        else
        {
            SpheneUI.Media.PlaceholderInRect(dl, thumbMin, thumbMax,
                rounding: 4f * scale);
        }
    }

    private void DrawTileBadges(ImDrawListPtr dl, Thumb thumb,
        Vector2 thumbMin, Vector2 thumbMax, float scale)
    {
        var badgeR = 9f * scale;
        var pos = new Vector2(thumbMax.X - badgeR - 3f * scale,
            thumbMin.Y + badgeR + 3f * scale);
        if (thumb.Width < _spec.MinRecommendedWidth)
        {
            dl.AddCircleFilled(pos, badgeR,
                SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.7f)));
            using (SpheneUI.Icons.PushFont())
            {
                var glyph = FontAwesomeIcon.ExclamationTriangle.ToIconString();
                var gSize = ImGui.CalcTextSize(glyph) * 0.7f;
                dl.AddText(ImGui.GetFont(), gSize.Y, pos - gSize * 0.5f,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Palette.NetworkWarning), glyph);
            }
            SpheneUI.AttachTooltip(
                $"Only {thumb.Width}px wide · below the recommended "
                + $"{_spec.MinRecommendedWidth}px for a {_spec.Noun}; the result may look soft.");
        }
        else if (thumb.Animated)
        {
            dl.AddCircleFilled(pos, badgeR,
                SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.7f)));
            var gSize = ImGui.CalcTextSize("GIF") * 0.62f;
            dl.AddText(ImGui.GetFont(), gSize.Y, pos - gSize * 0.5f,
                ImGui.GetColorU32(ImGuiCol.Text), "GIF");
            SpheneUI.AttachTooltip("Animated · cropping renders the first frame; "
                + "use Keep animation in the editor to upload it unchanged.");
        }
    }

    private void DrawTileLabel(ImDrawListPtr dl, DirEntry entry, Vector2 tileMin,
        Vector2 thumbMax, float tileW, float textH, float scale, bool hovered)
    {
        var textMin = new Vector2(tileMin.X + 5f * scale, thumbMax.Y + 3f * scale);
        var textMax = new Vector2(tileMin.X + tileW - 5f * scale, thumbMax.Y + textH);
        dl.PushClipRect(textMin, textMax, true);
        var lineH = ImGui.GetTextLineHeight();
        dl.AddText(textMin,
            ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled), entry.Name);
        var info = entry.IsDir ? "Folder" : ThumbInfo(entry.Path);
        dl.AddText(textMin + new Vector2(0f, lineH + 1f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), info);
        dl.PopClipRect();
    }

    private string ThumbInfo(string path)
    {
        return _thumbs.TryGetValue(path, out var t) && t != null
            ? $"{t.Width}×{t.Height}"
            : "…";
    }

    /// <summary>Drops the picked file and frees its preview texture.</summary>
    private void ClearSelection()
    {
        if (_selectedPath != null)
        {
            SpheneUI.Media.Evict($"photostudio:sel:{_selectedPath}");
        }
        _selectedPath = null;
        _selectionBytes = null;
    }

    /// <summary>Opens the preview view for a tile · bytes prefetch off-thread.</summary>
    private void SelectFile(string path)
    {
        ClearSelection();
        _selectedPath = path;
        _selectedSize = 0L;
        try
        {
            var info = new FileInfo(path);
            _selectedSize = info.Exists ? info.Length : 0L;
        }
        catch (Exception)
        {
            // metadata is nice-to-have · the preview shows what it has
        }
        _ = Task.Run(() =>
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (string.Equals(_selectedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _selectionBytes = bytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Preview read failed for {File}", path);
            }
        });
    }

    // ─────────────────────────── Edit stage ─────────────────────────────

    private void BeginLoadFile(string filePath)
    {
        _stage = Stage.Edit;
        _filePath = filePath;
        _sourceLabel = Path.GetFileName(filePath);
        _loadingFile = true;
        _loadFailed = false;
        WindowName = $"Image Studio · {_spec.Title}###SphenePhotoStudio";
        _ = Task.Run(() =>
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                var info = Image.Identify(bytes);
                if (info == null)
                {
                    _loadFailed = true;
                }
                else
                {
                    _pendingOpen = () => LoadBytes(bytes, info.Width, info.Height);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read studio image {File}", filePath);
                _loadFailed = true;
            }
            _loadingFile = false;
        });
    }

    /// <summary>Seeds the editor with decoded bytes and kicks off the preview decode.</summary>
    private void LoadBytes(byte[] imageBytes, int imageWidth, int imageHeight)
    {
        _imageBytes = imageBytes;
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        _onConfirm = rendered => DeliverPrepared(rendered);
        ResetEditState();

        _ = Task.Run(() =>
        {
            try
            {
                using var image = Image.Load<Rgba32>(imageBytes);
                var pending = new PendingPreview
                {
                    Image = Downscaled(image, PreviewMaxDimension),
                    Animated = image.Frames.Count > 1,
                    ForBytes = imageBytes,
                };
                Interlocked.Exchange(ref _pendingPreview, pending);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Studio preview decode failed · raw view without adjustments");
                Interlocked.Exchange(ref _pendingPreview,
                    new PendingPreview { ForBytes = imageBytes });
            }
        });
    }

    /// <summary>Wraps the rendered bytes as the Prepared payload the uploader expects.</summary>
    private void DeliverPrepared(byte[] rendered)
    {
        var nameBase = _filePath != null
            ? Path.GetFileNameWithoutExtension(_filePath)
            : $"capture-{DateTime.Now:yyyyMMdd-HHmmss}";
        _onPrepared?.Invoke(MakePrepared(rendered, nameBase));
    }

    /// <summary>Detects the encoded format by magic bytes and builds the upload payload.</summary>
    private static ImageUploadProcessor.Prepared MakePrepared(byte[] bytes, string nameBase)
    {
        var (ext, mime) = bytes.Length > 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E'
            && bytes[10] == (byte)'B' && bytes[11] == (byte)'P'
            ? (".webp", "image/webp")
            : bytes.Length > 3 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F'
                ? (".gif", "image/gif")
                : (".png", "image/png");
        var info = Image.Identify(bytes);
        return new ImageUploadProcessor.Prepared(bytes, Convert.ToBase64String(bytes),
            nameBase + ext, mime, info?.Width ?? 0, info?.Height ?? 0, true);
    }

    private void ResetEditState()
    {
        _pendingResult = null;
        _cropFinished = false;
        _cropFailed = false;
        _applyBusy = false;
        _drag = DragMode.None;
        _bright = 0f;
        _sat = 100f;
        _vig = 0f;
        _animated = false;
        _previewBytes = null;
        _previewReady = false;
        _previewDirty = true;
        _pendingPreview = null;
        _previewSrc?.Dispose();
        _previewSrc = null;
        // DrawEdit drains pending decodes via the stale list; the last shown
        // texture can be evicted right away.
        if (!string.IsNullOrEmpty(_previewKey)
            && !string.Equals(_previewKey, _previewShownKey, StringComparison.Ordinal))
        {
            _previewStaleKeys.Add(_previewKey);
        }
        if (_previewShownKey != null)
        {
            SpheneUI.Media.Evict(_previewShownKey);
            _previewShownKey = null;
        }
        _previewKey = string.Empty;
        _zoomValid = false;
        ResetCrop();

        SpheneUI.Media.Evict(TextureKey);
        IsOpen = true;
    }

    private void ResetCrop()
    {
        if (_aspect > 0f)
        {
            var imageAspect = _imageWidth / (float)_imageHeight;
            var cw = imageAspect >= _aspect ? _aspect / imageAspect : 1f;
            var ch = imageAspect >= _aspect ? 1f : imageAspect / _aspect;
            _crop = new Vector4((1f - cw) * 0.5f, (1f - ch) * 0.5f, cw, ch);
        }
        else
        {
            _crop = new Vector4(0f, 0f, 1f, 1f);
        }
    }

    /// <summary>Downscaled clone (first frame for animated sources) used for the live preview.</summary>
    private static Image<Rgba32> Downscaled(Image<Rgba32> image, int maxDimension)
    {
        using var first = image.Frames.Count > 1 ? image.Frames.CloneFrame(0) : image.Clone();
        var longest = Math.Max(first.Width, first.Height);
        if (longest <= maxDimension)
        {
            return first.Clone();
        }
        var f = maxDimension / (float)longest;
        return first.Clone(x => x.Resize(
            Math.Max(1, (int)MathF.Round(first.Width * f)),
            Math.Max(1, (int)MathF.Round(first.Height * f))));
    }

    /// <summary>Regenerates the adjusted preview PNG · runs only when sliders moved.</summary>
    private void RegenPreview()
    {
        if (_previewSrc == null)
        {
            return;
        }
        // The superseded pending decode is tracked instead of evicted on the
        // spot · DrawEdit drains the list once each texture has materialized,
        // so a mid-flight decode is never orphaned.
        if (!string.IsNullOrEmpty(_previewKey)
            && !string.Equals(_previewKey, _previewShownKey, StringComparison.Ordinal))
        {
            _previewStaleKeys.Add(_previewKey);
            if (_previewStaleKeys.Count > 8)
            {
                SpheneUI.Media.Evict(_previewStaleKeys[0]);
                _previewStaleKeys.RemoveAt(0);
            }
        }
        _previewKey = $"photostudio:preview:{Environment.TickCount64}";
        var brightF = 1f + _bright / 100f;
        var satF = _sat / 100f;
        using var work = _previewSrc.Clone(ctx =>
        {
            if (MathF.Abs(_bright) > 0.5f)
            {
                ctx.Brightness(brightF);
            }
            if (MathF.Abs(_sat - 100f) > 0.5f)
            {
                ctx.Saturate(satF);
            }
        });
        // The vignette lives in crop space · identical to the export, where
        // it runs after the frame is cropped.
        if (_vig > 0.5f)
        {
            ApplyVignette(work, _vig / 100f, _crop);
        }
        using var ms = new MemoryStream();
        work.SaveAsPng(ms);
        _previewBytes = ms.ToArray();
    }

    protected override void DrawInternal()
    {
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        if (_stage == Stage.Pick)
        {
            DrawPick();
            return;
        }
        DrawEdit();
    }

    private void DrawEdit()
    {
        // Async hand-offs first · callbacks run on the UI thread.
        if (_pendingOpen is { } pendingOpen)
        {
            _pendingOpen = null;
            pendingOpen();
            return;
        }
        if (_loadingFile)
        {
            SpheneUI.Text.Muted("Loading image…");
            return;
        }
        if (_loadFailed)
        {
            SpheneUI.Layout.Callout("That file is not a readable image.",
                FontAwesomeIcon.ExclamationTriangle, SpheneUI.Colors.Danger);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
            if (SpheneUI.Controls.Button("Back to source"))
            {
                EnterPick(Path.GetDirectoryName(_filePath) ?? _browseDir);
            }
            return;
        }
        if (_cropFinished)
        {
            _cropFinished = false;
            var result = _pendingResult;
            _pendingResult = null;
            if (result != null)
            {
                IsOpen = false;
                _onConfirm?.Invoke(result);
                return;
            }
            _applyBusy = false;
            _cropFailed = true;
        }
        if (_pendingPrepared != null)
        {
            var prepared = _pendingPrepared;
            _pendingPrepared = null;
            IsOpen = false;
            _onPrepared?.Invoke(prepared);
            return;
        }
        if (Interlocked.Exchange(ref _pendingPreview, null) is { } arrived)
        {
            if (ReferenceEquals(arrived.ForBytes, _imageBytes))
            {
                _previewSrc?.Dispose();
                _previewSrc = arrived.Image;
                _animated = arrived.Animated;
                _previewReady = arrived.Image != null;
                _previewDirty = true;
            }
            else
            {
                // a newer load superseded this decode · free it quietly
                arrived.Image?.Dispose();
            }
        }
        // Regen is throttled · cloning + PNG-encodes are synchronous, so a
        // dragged slider must not fire a full rebuild every frame.
        if (_previewDirty && _previewSrc != null
            && ImGui.GetTime() - _lastPreviewRegen > PreviewRegenInterval)
        {
            _previewDirty = false;
            _lastPreviewRegen = ImGui.GetTime();
            RegenPreview();
        }

        var scale = SpheneUI.Scale;
        var frameH = ImGui.GetFrameHeight();

        DrawEditHeader();
        if (_stage != Stage.Edit)
        {
            return;
        }

        // A fresh adjustment decodes asynchronously · keep drawing the last
        // resolved preview until it lands, or the whole editor (including the
        // held slider) would drop out mid-drag.
        IDalamudTextureWrap? wrap;
        if (_previewBytes != null)
        {
            wrap = SpheneUI.Media.Texture(_previewKey, _previewBytes);
            if (wrap != null
                && !string.Equals(_previewShownKey, _previewKey, StringComparison.Ordinal))
            {
                if (_previewShownKey != null)
                {
                    SpheneUI.Media.Evict(_previewShownKey);
                }
                _previewShownKey = _previewKey;
            }
            wrap ??= _previewShownKey != null
                ? SpheneUI.Media.Texture(_previewShownKey, _previewBytes)
                : null;
        }
        else
        {
            wrap = _imageBytes.Length > 0
                ? SpheneUI.Media.Texture(TextureKey, _imageBytes)
                : null;
        }
        // Superseded decodes are evicted once they resolve · evicting an
        // in-flight task would orphan its wrap.
        for (var i = _previewStaleKeys.Count - 1; i >= 0; i--)
        {
            var stale = _previewStaleKeys[i];
            if (SpheneUI.Media.Texture(stale, () => null) != null)
            {
                SpheneUI.Media.Evict(stale);
                _previewStaleKeys.RemoveAt(i);
            }
        }
        if (wrap == null)
        {
            SpheneUI.Text.Muted("Loading image…");
            return;
        }

        // Body: canvas left, tool sidebar right · footer + bottom margin
        // are reserved below.
        var footerH = frameH + ImGui.GetStyle().ItemSpacing.Y
            + SpheneUI.Spacing.X4.Scaled + SpheneUI.Spacing.X8.Scaled;
        var body = new Vector2(SpheneUI.Controls.BoundedAvailX(),
            MathF.Max(120f, ImGui.GetContentRegionAvail().Y - footerH));
        var sidebarW = MathF.Min(SidebarWidth * scale, body.X * 0.38f);
        var gap = SpheneUI.Spacing.CardGap.Scaled;
        var canvasW = MathF.Max(160f, body.X - sidebarW - gap);
        var bodyMin = ImGui.GetCursorScreenPos();

        DrawCanvas(wrap, bodyMin, new Vector2(canvasW, body.Y), scale);

        ImGui.SetCursorScreenPos(bodyMin + new Vector2(canvasW + gap, 0f));
        if (SpheneUI.Layout.BeginScrollArea("##st-tools",
                new Vector2(sidebarW, body.Y), border: false))
        {
            SpheneUI.Layout.SurfaceFlat(() => DrawToolPanel(scale));
            SpheneUI.Layout.EndScrollArea();
        }

        ImGui.SetCursorScreenPos(bodyMin + new Vector2(0f, body.Y));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        DrawEditFooter();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
    }

    /// <summary>Back button, edit title and source info on one row.</summary>
    private void DrawEditHeader()
    {
        var lineMin = ImGui.GetCursorScreenPos();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "Sources"))
        {
            EnterPick(Path.GetDirectoryName(_filePath) ?? _browseDir);
            return;
        }
        ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Colored($"Editing {_spec.Noun}", SpheneUI.Colors.TextPrimary);
        if (!string.IsNullOrEmpty(_sourceLabel))
        {
            ImGui.SameLine(0f, SpheneUI.Spacing.X8.Scaled);
            SpheneUI.Text.Muted($" ·  {_sourceLabel}");
        }
        var info = $"{_imageWidth}×{_imageHeight}";
        var badgeText = _aspect > 0f ? $"{_aspect:0.#}:1" : "free";
        var badgeW = SpheneUI.Layout.BadgeWidth(badgeText);
        var infoW = ImGui.CalcTextSize(info).X + SpheneUI.Spacing.X8.Scaled + badgeW;
        var frameH = ImGui.GetFrameHeight();
        var textY = lineMin.Y + MathF.Max(0f,
            (frameH - ImGui.GetTextLineHeight()) * 0.5f);
        var badgeY = lineMin.Y + MathF.Max(0f,
            (frameH - SpheneUI.Layout.BadgeHeight) * 0.5f);
        var gap = ImGui.GetStyle().ItemSpacing.X;
        SpheneUI.Controls.DockToCardEdge(infoW + gap + SpheneUI.Metrics.ControlHeight);
        var dockPos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(dockPos.X, textY));
        SpheneUI.Text.Muted(info);
        ImGui.SetCursorScreenPos(new Vector2(dockPos.X + infoW - badgeW, badgeY));
        SpheneUI.Layout.Badge(badgeText, SpheneTheme.Current.AccentCyan);
        ImGui.SetCursorScreenPos(new Vector2(dockPos.X + infoW + gap,
            lineMin.Y + MathF.Max(0f, (frameH - SpheneUI.Metrics.ControlHeight) * 0.5f)));
        SpheneUI.Layout.PageHelp("imagestudio-edit", () =>
        {
            SpheneUI.Text.MutedWrapped(
                "Crop the image to the destination format, then confirm.\n\n" +
                "Drag the frame to move it, drag a corner to resize. " +
                "The zoom pill on the crop zooms the view into the selection.",
                ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });
        ImGui.SetCursorScreenPos(new Vector2(lineMin.X,
            MathF.Max(ImGui.GetCursorScreenPos().Y, lineMin.Y + ImGui.GetFrameHeight())));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    /// <summary>The image canvas: matte, fitted preview and the crop overlay.</summary>
    private void DrawCanvas(IDalamudTextureWrap wrap, Vector2 canvasMin,
        Vector2 canvasSize, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var canvasMax = canvasMin + canvasSize;

        // Matte + subtle rounding so the canvas reads as a contained surface.
        dl.AddRectFilled(canvasMin, canvasMax,
            SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.28f)), 8f * scale);

        var imageAspect = _imageWidth / (float)_imageHeight;
        var pad = 6f * scale;
        var dispW = MathF.Max(10f, canvasSize.X - pad * 2f);
        var maxH = MathF.Max(10f, canvasSize.Y - pad * 2f);
        var dispH = dispW / imageAspect;
        if (dispH > maxH)
        {
            dispH = maxH;
            dispW = dispH * imageAspect;
        }
        var dispSize = new Vector2(dispW, dispH);
        var imgMin = canvasMin + (canvasSize - dispSize) * 0.5f;

        // Zoom-to-crop is opt-in via the magnifier toggle. The transform
        // eases toward its target every frame · no snapping · and freezes
        // mid-drag so nothing swims under the cursor.
        var zooming = _zoomToCrop && (_crop.Z < 0.999f || _crop.W < 0.999f);
        var targetSize = dispSize;
        var targetMin = imgMin;
        if (zooming)
        {
            var zoom = MathF.Min(CanvasZoomMax, MathF.Min(
                dispW / MathF.Max(_crop.Z * dispSize.X, 1f),
                maxH / MathF.Max(_crop.W * dispSize.Y, 1f)));
            targetSize = dispSize * zoom;
            var cropCenter = new Vector2(_crop.X + _crop.Z * 0.5f,
                _crop.Y + _crop.W * 0.5f);
            targetMin = canvasMin + canvasSize * 0.5f - cropCenter * targetSize;
        }
        if (_drag == DragMode.None)
        {
            if (!_zoomValid)
            {
                _zoomMin = targetMin;
                _zoomSize = targetSize;
                _zoomValid = true;
            }
            else
            {
                var blend = 1f - MathF.Exp(-ImGui.GetIO().DeltaTime / 0.12f);
                var dMin = targetMin - _zoomMin;
                var dSize = targetSize - _zoomSize;
                if (dMin.LengthSquared() + dSize.LengthSquared() < 0.25f)
                {
                    _zoomMin = targetMin;
                    _zoomSize = targetSize;
                }
                else
                {
                    _zoomMin += dMin * blend;
                    _zoomSize += dSize * blend;
                }
            }
        }
        if (_zoomValid)
        {
            imgMin = _zoomMin;
            dispSize = _zoomSize;
        }

        // The zoom chip rides the crop's top-right corner so the control
        // lives where the eye already is. It is submitted BEFORE the canvas
        // button so it wins hover/activation over its rect, while its
        // visuals go to the upper draw channel the image cannot paint over.
        // Its rect also feeds the crop interaction as a drag swallow zone.
        var cropMin = imgMin + new Vector2(_crop.X * dispSize.X, _crop.Y * dispSize.Y);
        var cropMax = imgMin + new Vector2((_crop.X + _crop.Z) * dispSize.X,
            (_crop.Y + _crop.W) * dispSize.Y);
        var chipLabel = _zoomToCrop ? "Fit image" : "Zoom to crop";
        var chipIcon = _zoomToCrop ? FontAwesomeIcon.SearchMinus : FontAwesomeIcon.SearchPlus;
        var chipW = SpheneUI.Icons.TextButtonWidth(chipIcon, chipLabel);
        var chipH = ImGui.GetFrameHeight();
        var chipGap = 4f * scale;
        var chipX = Math.Clamp(cropMax.X - chipW,
            canvasMin.X + pad, canvasMax.X - pad - chipW);
        var chipY = cropMin.Y - chipH - chipGap;
        if (chipY < canvasMin.Y + pad)
        {
            chipY = MathF.Min(cropMin.Y + chipGap, canvasMax.Y - pad - chipH);
        }
        var chipRect = new Vector4(chipX, chipY, chipW, chipH);

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(new Vector2(chipX, chipY));
        var chipPressed = SpheneUI.Controls.SegmentButton(chipLabel, chipIcon,
            active: _zoomToCrop);
        SpheneUI.AttachTooltip(_zoomToCrop
            ? "Ease back to the whole image"
            : "Zoom the view into the crop area");
        dl.ChannelsSetCurrent(0);

        ImGui.SetCursorScreenPos(canvasMin);
        // Claims the mouse so canvas drags crop instead of dragging the window.
        ImGui.InvisibleButton("##st-canvas", canvasSize);

        HandleCropInteraction(canvasMin, canvasSize, imgMin, dispSize, chipRect);

        dl.PushClipRect(canvasMin, canvasMax, true);
        SpheneUI.Media.ImageInRect(dl, wrap, imgMin, imgMin + dispSize, ImageFit.Fit);
        DrawCropOverlay(dl, imgMin, dispSize);
        dl.PopClipRect();
        dl.ChannelsMerge();

        if (chipPressed)
        {
            _zoomToCrop = !_zoomToCrop;
        }
        ImGui.SetCursorScreenPos(canvasMin + new Vector2(0f, canvasSize.Y));
    }

    /// <summary>Sidebar: required format, adjustments and output info.</summary>
    private void DrawToolPanel(float scale)
    {
        SpheneUI.Text.SectionHeader("Format");
        SpheneUI.Text.MutedWrapped(_aspect > 0f
            ? $"Locked to {_aspect:0.#}:1 · the required shape for a {_spec.Noun}."
            : "Free-form · this upload accepts any shape.",
            ImGui.GetCursorPosX() + SpheneUI.Controls.BoundedAvailX());
        DrawFormatCard(scale);

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
        SpheneUI.Text.SectionHeader("Adjust");
        if (!_previewReady)
        {
            SpheneUI.Text.MutedWrapped("Live adjustments are unavailable for this source · "
                + "the file could not be decoded for preview.",
                ImGui.GetCursorPosX() + SpheneUI.Controls.BoundedAvailX());
        }
        else
        {
            if (SpheneUI.Controls.SliderFloat("Brightness", ref _bright, -100f, 100f, "%.0f"))
            {
                _previewDirty = true;
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Inline);
            if (SpheneUI.Controls.SliderFloat("Saturation", ref _sat, 0f, 200f, "%.0f%%"))
            {
                _previewDirty = true;
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Inline);
            if (SpheneUI.Controls.SliderFloat("Vignette", ref _vig, 0f, 100f, "%.0f%%"))
            {
                _previewDirty = true;
            }
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);
        SpheneUI.Text.SectionHeader("Output");
        var output = _aspect > 0f
            ? $"up to {_spec.TargetWidth}×{Math.Max(1, (int)MathF.Round(_spec.TargetWidth / _aspect))} px"
            : $"up to {_spec.TargetWidth}px wide";
        SpheneUI.Text.Muted($"{output} · ≤ {_spec.MaxBytes / 1024} KB"
            + (_animated ? " · animated" : string.Empty));
        if (_imageWidth < _spec.MinRecommendedWidth)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Inline);
            SpheneUI.Text.ColoredWrapped(
                $"Source is only {_imageWidth}px wide · below the recommended "
                + $"{_spec.MinRecommendedWidth}px for a {_spec.Noun}; the result may look soft.",
                SpheneUI.Palette.NetworkWarning,
                ImGui.GetCursorPosX() + SpheneUI.Controls.BoundedAvailX());
        }
        if (_cropFailed)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Inline);
            SpheneUI.Text.ColoredWrapped(
                "The image could not be processed · pick a different source.",
                SpheneUI.Colors.Danger,
                ImGui.GetCursorPosX() + SpheneUI.Controls.BoundedAvailX());
        }
    }

    /// <summary>Read-only card naming the format this upload requires.</summary>
    private void DrawFormatCard(float scale)
    {
        var locked = _aspect > 0f;
        var preset = CropPresets[_preset];
        var w = MathF.Max(80f * scale, SpheneUI.Controls.BoundedAvailX());
        var h = PresetRowHeight * scale;
        ImGui.Dummy(new Vector2(w, h));
        var min = ImGui.GetItemRectMin();
        var max = min + new Vector2(w, h);
        var accent = SpheneTheme.Current.AccentCyan;
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.14f)),
            8f * scale);
        dl.AddRect(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.45f)),
            8f * scale, ImDrawFlags.None, MathF.Max(1f, scale));

        var pad = SpheneUI.Spacing.X8.Scaled;
        var iconW = 20f * scale;
        using (SpheneUI.Icons.PushFont())
        {
            var glyph = preset.Icon.ToIconString();
            var gSize = ImGui.CalcTextSize(glyph);
            dl.AddText(ImGui.GetFont(), gSize.Y,
                new Vector2(min.X + pad + (iconW - gSize.X) * 0.5f,
                    min.Y + (h - gSize.Y) * 0.5f),
                SpheneUI.ColorUtils.ToU32(accent), glyph);
        }
        dl.AddText(new Vector2(min.X + pad + iconW + SpheneUI.Spacing.X4.Scaled,
                min.Y + (h - ImGui.GetTextLineHeight()) * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), preset.Name);
        var right = locked ? $"{_aspect:0.#}:1" : "free";
        var rightW = ImGui.CalcTextSize(right).X;
        dl.AddText(new Vector2(max.X - pad - rightW,
                min.Y + (h - ImGui.GetTextLineHeight()) * 0.5f),
            SpheneUI.ColorUtils.ToU32(accent), right);
    }

    /// <summary>Hint + secondary actions left; cancel/confirm docked exactly right.</summary>
    private void DrawEditFooter()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var inline = SpheneUI.Spacing.Inline.Scaled;
        var confirmLabel = _applyBusy ? "Applying…" : "Use image";

        // Exact widths for every control on the row · SegCore buttons carry an
        // icon zone + chevron, so measuring via CalcTextSize would clip them.
        var cancelW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Times, "Cancel");
        var confirmW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Check, confirmLabel);
        var rightW = cancelW + spacing + confirmW;
        var captureW = SpheneUI.Icons.TextButtonWidth(FontAwesomeIcon.Camera, "Capture");
        var resetW = SpheneUI.Controls.SmallButtonWidth("Reset");
        var showKeep = _animated && _filePath != null && _onPrepared != null;
        var keepW = showKeep ? SpheneUI.Controls.SmallButtonWidth("Keep animation") : 0f;
        var rowH = ImGui.GetFrameHeight();
        var lineMin = ImGui.GetCursorScreenPos();

        // Right cluster first · docked at the exact content bound so it can
        // never slide past the card edge.
        SpheneUI.Controls.DockToCardEdge(rightW);
        var dockMin = ImGui.GetCursorScreenPos();
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Times, "Cancel"))
        {
            IsOpen = false;
            return;
        }
        ImGui.SameLine();
        if (SpheneUI.Controls.PrimaryButton(confirmLabel, FontAwesomeIcon.Check,
                enabled: !_applyBusy))
        {
            ApplyCrop();
        }

        // Left cluster re-flows from the row start, bounded by the docked edge.
        var leftAvail = dockMin.X - lineMin.X - inline;
        var buttonsW = captureW + inline + resetW + (showKeep ? inline + keepW : 0f);
        var hintW = leftAvail - buttonsW - inline;
        var nextX = lineMin.X;
        if (hintW > 60f * SpheneUI.Scale)
        {
            ImGui.SetCursorScreenPos(lineMin + new Vector2(0f,
                MathF.Max(0f, (rowH - ImGui.GetTextLineHeight()) * 0.5f)));
            SpheneUI.Text.Muted(SpheneUI.Text.Ellipsize(
                "Drag to move · corners resize" + (_applyBusy ? " · Applying…" : string.Empty),
                hintW));
            nextX = ImGui.GetItemRectMax().X + inline;
        }
        ImGui.SetCursorScreenPos(new Vector2(nextX, lineMin.Y));
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Camera, "Capture"))
        {
            BeginCapture();
            return;
        }
        SpheneUI.AttachTooltip("Snip a new region off the screen");
        ImGui.SameLine(0f, inline);
        if (SpheneUI.Controls.SmallButton("Reset"))
        {
            _bright = 0f;
            _sat = 100f;
            _vig = 0f;
            _previewDirty = true;
            ResetCrop();
        }
        if (showKeep)
        {
            ImGui.SameLine(0f, inline);
            if (SpheneUI.Controls.SmallButton("Keep animation"))
            {
                KeepAnimation();
            }
            SpheneUI.AttachTooltip("Animated sources lose their frames when edited · "
                + "this uploads the file unchanged, keeping the animation.");
        }

        ImGui.SetCursorScreenPos(lineMin + new Vector2(0f, rowH));
    }

    /// <summary>Animated sources: skips the editor and uploads via the classic GIF path.</summary>
    private void KeepAnimation()
    {
        var path = _filePath;
        if (path == null)
        {
            return;
        }
        _applyBusy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _pendingPrepared = await ImageUploadProcessor.PrepareAsync(path)
                    .ConfigureAwait(false);
                if (_pendingPrepared == null)
                {
                    _applyBusy = false;
                    _cropFailed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prepare animated image {File}", path);
                _applyBusy = false;
                _cropFailed = true;
            }
        });
    }

    private void ApplyCrop()
    {
        _applyBusy = true;
        _cropFailed = false;
        var (bytes, crop, spec, brightF, satF, vigF) = (_imageBytes, _crop, _spec,
            1f + _bright / 100f, _sat / 100f, _vig / 100f);
        _ = Task.Run(() =>
            {
                _pendingResult = RenderCrop(bytes, crop, spec, brightF, satF, vigF);
                _cropFinished = true;
            })
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _applyBusy = false;
                    _cropFinished = false;
                    _cropFailed = true;
                    _logger.LogWarning(t.Exception, "Failed to render image crop");
                }
            });
    }

    // ─────────────────────── Capture overlay ────────────────────────────

    /// <summary>Hides the studio window (and every other open Sphene window)
    /// and attaches the fullscreen snip overlay. The game HUD starts hidden  · 
    /// the user can opt back in from the overlay bar.</summary>
    private void BeginCapture()
    {
        if (_captureOverlay)
        {
            return;
        }
        _captureOverlay = true;
        _snip = Vector4.Zero;
        _snipBarRect = Vector4.Zero;
        _snipDrag = DragMode.None;
        _snipLockAspect = false;
        _snipHideUi = true;
        _snipUiToggledAt = ImGui.GetTime();
        _gameUiVisibility.Hide();
        _captureError = null;
        _hiddenWindows.Clear();
        foreach (var window in _windowSystem.Windows)
        {
            if (!ReferenceEquals(window, this) && window.IsOpen)
            {
                window.IsOpen = false;
                _hiddenWindows.Add(window);
            }
        }
        IsOpen = false;
        _pluginInterface.UiBuilder.Draw += DrawCaptureOverlay;
    }

    /// <summary>Detaches the overlay and restores the game HUD if it was hidden.</summary>
    private void EndCapture()
    {
        if (!_captureOverlay)
        {
            return;
        }
        _pluginInterface.UiBuilder.Draw -= DrawCaptureOverlay;
        _captureOverlay = false;
        _snipDrag = DragMode.None;
        if (_snipHideUi)
        {
            _snipHideUi = false;
            _gameUiVisibility.Restore();
        }
        foreach (var window in _hiddenWindows)
        {
            try
            {
                window.IsOpen = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to restore window {Window}",
                    window.WindowName);
            }
        }
        _hiddenWindows.Clear();
    }

    private void CancelCapture()
    {
        EndCapture();
        IsOpen = true;
    }

    /// <summary>
    /// Fullscreen snip overlay, drawn through UiBuilder.Draw on the main
    /// viewport: a dim veil, an aspect-preset top bar, a draggable selection
    /// with corner handles and capture/cancel pills. Runs outside the window
    /// chrome · colors are theme-token based, not scope based.
    /// </summary>
    private unsafe void DrawCaptureOverlay()
    {
        var viewport = ImGui.GetMainViewport();
        var vpMin = viewport.Pos;
        var vpSize = viewport.Size;
        var vpMax = vpMin + vpSize;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(vpMin);
        ImGui.SetNextWindowSize(vpSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        var begun = ImGui.Begin("##sphene-snip", SnipWindowFlags);
        if (begun)
        {
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindowRead());
            var dl = ImGui.GetWindowDrawList();
            var scale = SpheneUI.Scale;

            // Dim veil · split around the selection so the cut-out stays lit.
            var dim = SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.5f));
            var selMin = vpMin + new Vector2(_snip.X, _snip.Y);
            var selMax = selMin + new Vector2(_snip.Z, _snip.W);
            var hasSel = _snip.Z >= SnipMinSide && _snip.W >= SnipMinSide;
            if (!hasSel)
            {
                dl.AddRectFilled(vpMin, vpMax, dim);
            }
            else
            {
                dl.AddRectFilled(vpMin, new Vector2(vpMax.X, selMin.Y), dim);
                dl.AddRectFilled(new Vector2(vpMin.X, selMax.Y), vpMax, dim);
                dl.AddRectFilled(new Vector2(vpMin.X, selMin.Y), new Vector2(selMin.X, selMax.Y), dim);
                dl.AddRectFilled(new Vector2(selMax.X, selMin.Y), new Vector2(vpMax.X, selMax.Y), dim);
            }

            DrawSnipTopBar(dl, vpMin, vpSize, scale);
            HandleSnipDrag(vpMin, vpMax, scale);
            if (hasSel)
            {
                DrawSnipSelection(dl, vpMin, vpMax, scale);
            }
            else
            {
                DrawSnipHint(dl, vpMin, vpSize, scale);
            }
            if (_captureError != null)
            {
                var w = ImGui.CalcTextSize(_captureError).X + 24f * scale;
                var pMin = new Vector2(vpMin.X + (vpSize.X - w) * 0.5f,
                    vpMax.Y - 56f * scale - 30f * scale);
                var pMax = pMin + new Vector2(w, 30f * scale);
                dl.AddRectFilled(pMin, pMax, SpheneUI.ColorUtils.ToU32(
                    new Vector4(0f, 0f, 0f, 0.8f)), 8f * scale);
                dl.AddText(pMin + new Vector2(12f * scale,
                        (pMax.Y - pMin.Y - ImGui.GetTextLineHeight()) * 0.5f),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Danger), _captureError);
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                CancelCapture();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    /// <summary>Centered instruction when nothing is selected yet.</summary>
    private static void DrawSnipHint(ImDrawListPtr dl, Vector2 vpMin, Vector2 vpSize, float scale)
    {
        var text = "Drag anywhere to select the capture area · Esc to cancel";
        var tw = ImGui.CalcTextSize(text).X;
        var pos = new Vector2(vpMin.X + (vpSize.X - tw) * 0.5f,
            vpMin.Y + vpSize.Y * 0.5f - ImGui.GetTextLineHeight() * 0.5f);
        var pad = new Vector2(14f, 9f) * scale;
        dl.AddRectFilled(pos - pad, pos + new Vector2(tw, ImGui.GetTextLineHeight()) + pad,
            SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.65f)), 8f * scale);
        dl.AddText(pos, SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), text);
    }

    /// <summary>Top pill bar: format lock, HUD toggle, full-screen and cancel.</summary>
    private void DrawSnipTopBar(ImDrawListPtr dl, Vector2 vpMin, Vector2 vpSize, float scale)
    {
        var padX = SnipPillPadX * scale;
        var padY = SnipPillPadY * scale;
        var gap = SnipPillGap * scale;
        var barH = SnipBarHeight * scale;
        var accent = SpheneTheme.Current.AccentCyan;

        // Measure first so the bar can be centered.
        var lockLabel = _spec.Aspect > 0f
            ? $"Lock {CropPresets[_preset].Name} {_spec.Aspect:0.#}:1"
            : null;
        var hideLabel = _snipHideUi ? "Show game UI" : "Hide game UI";
        var fullLabel = "Full screen";
        var cancelLabel = "Cancel";
        var totalW = 0f;
        if (lockLabel != null)
        {
            totalW += ImGui.CalcTextSize(lockLabel).X + padX * 2f + gap;
        }
        totalW += ImGui.CalcTextSize(hideLabel).X + padX * 2f + gap;
        totalW += ImGui.CalcTextSize(fullLabel).X + padX * 2f + gap;
        totalW += ImGui.CalcTextSize(cancelLabel).X + padX * 2f;

        var barMin = new Vector2(
            MathF.Max(vpMin.X + 8f * scale, vpMin.X + (vpSize.X - totalW) * 0.5f),
            vpMin.Y + 16f * scale);
        var barMax = barMin + new Vector2(totalW, barH);
        _snipBarRect = new Vector4(barMin.X, barMin.Y, totalW, barH);
        dl.AddRectFilled(barMin, barMax,
            SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.72f)), barH * 0.5f);
        dl.AddRect(barMin, barMax,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.25f)),
            barH * 0.5f, ImDrawFlags.None, MathF.Max(1f, scale));

        var pillH = ImGui.GetTextLineHeight() + padY * 2f;
        var pen = barMin + new Vector2(0f, (barH - pillH) * 0.5f);
        if (lockLabel != null)
        {
            if (SnipPill(dl, ref pen, "##snip-lock", lockLabel, _snipLockAspect, false,
                    padX, pillH))
            {
                _snipLockAspect = !_snipLockAspect;
            }
            SpheneUI.AttachTooltip(
                $"Hold the selection at the {_spec.Noun} shape while dragging");
            pen.X += gap;
        }

        if (SnipPill(dl, ref pen, "##snip-hideui", hideLabel, _snipHideUi, false,
                padX, pillH))
        {
            _snipHideUi = !_snipHideUi;
            _snipUiToggledAt = ImGui.GetTime();
            if (_snipHideUi)
            {
                _gameUiVisibility.Hide();
            }
            else
            {
                _gameUiVisibility.Restore();
            }
        }
        SpheneUI.AttachTooltip("Hide the game HUD (hotbars, chat) for a clean capture");
        pen.X += gap;

        if (SnipPill(dl, ref pen, "##snip-full", fullLabel, false, false, padX, pillH))
        {
            CompleteCapture(vpMin, vpMin + vpSize);
        }
        SpheneUI.AttachTooltip("Capture the entire screen");
        pen.X += gap;

        if (SnipPill(dl, ref pen, "##snip-cancel", cancelLabel, false, true, padX, pillH))
        {
            CancelCapture();
        }
    }

    /// <summary>One pill button in the overlay bar · returns true when clicked.</summary>
    private static bool SnipPill(ImDrawListPtr dl, ref Vector2 pen, string id, string text,
        bool active, bool danger, float padX, float h)
    {
        var tw = ImGui.CalcTextSize(text).X;
        var size = new Vector2(tw + padX * 2f, h);
        var min = pen;
        var max = min + size;
        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var accent = danger ? SpheneUI.Colors.Danger : SpheneTheme.Current.AccentCyan;
        var fill = active
            ? SpheneUI.ColorUtils.WithAlpha(accent, 0.55f)
            : hovered
                ? SpheneUI.ColorUtils.WithAlpha(accent, 0.22f)
                : SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.10f);
        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(fill), h * 0.5f);
        dl.AddText(min + new Vector2(padX, (h - ImGui.GetTextLineHeight()) * 0.5f),
            SpheneUI.ColorUtils.ToU32(
                active ? SpheneUI.ColorUtils.ContrastTextColor(accent)
                    : hovered ? SpheneUI.Colors.TextPrimary : SpheneUI.Colors.TextSecondary),
            text);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        pen.X += size.X;
        return clicked;
    }

    /// <summary>Mouse interaction for the snip rect: create, move, corner-resize.</summary>
    private void HandleSnipDrag(Vector2 vpMin, Vector2 vpMax, float scale)
    {
        var vpSize = vpMax - vpMin;
        var mouse = ImGui.GetMousePos();
        var rel = mouse - vpMin;
        var m = new Vector2(Math.Clamp(rel.X, 0f, vpSize.X), Math.Clamp(rel.Y, 0f, vpSize.Y));
        var aspect = _snipLockAspect ? _spec.Aspect : 0f;
        var hasSel = _snip.Z >= SnipMinSide && _snip.W >= SnipMinSide;
        var io = ImGui.GetIO();

        if (_snipDrag == DragMode.None && hasSel)
        {
            ImGui.SetMouseCursor(SnipHitTest(m, scale) switch
            {
                DragMode.Move => ImGuiMouseCursor.Hand,
                DragMode.TopLeft or DragMode.BottomRight => ImGuiMouseCursor.ResizeNwse,
                DragMode.TopRight or DragMode.BottomLeft => ImGuiMouseCursor.ResizeNesw,
                _ => ImGuiMouseCursor.Arrow,
            });
        }
        else if (_snipDrag == DragMode.None)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Arrow);
        }

        // Swallow clicks that land on the overlay's own controls · the bounds
        // are computed from this frame's state so they can never go stale.
        var inBar = mouse.X >= _snipBarRect.X && mouse.X <= _snipBarRect.X + _snipBarRect.Z
            && mouse.Y >= _snipBarRect.Y && mouse.Y <= _snipBarRect.Y + _snipBarRect.W;
        var inActions = hasSel && RectContains(SnipActionsBounds(vpMin, vpMax, scale), mouse);
        if (io.MouseClicked[0] && !inBar && !inActions && !ImGui.IsAnyItemActive())
        {
            var hit = hasSel ? SnipHitTest(m, scale) : DragMode.None;
            _snipDrag = hit;
            if (_snipDrag == DragMode.Move)
            {
                _snipGrab = m - new Vector2(_snip.X, _snip.Y);
            }
            else if (_snipDrag == DragMode.None)
            {
                // fresh drag: anchor on the press point, grow via the bottom-right handle
                _snip = new Vector4(m.X, m.Y, 0f, 0f);
                _snipDrag = DragMode.BottomRight;
            }
            else
            {
                _snipGrab = m;
            }
        }
        if (io.MouseReleased[0])
        {
            _snipDrag = DragMode.None;
            if (_snip.Z < SnipMinSide || _snip.W < SnipMinSide)
            {
                _snip = Vector4.Zero;
            }
        }
        if (!io.MouseDown[0])
        {
            return;
        }

        var minSide = SnipMinSide;
        if (_snipDrag == DragMode.Move)
        {
            var nx = Math.Clamp(m.X - _snipGrab.X, 0f, vpSize.X - _snip.Z);
            var ny = Math.Clamp(m.Y - _snipGrab.Y, 0f, vpSize.Y - _snip.W);
            _snip.X = nx;
            _snip.Y = ny;
            return;
        }
        if (_snipDrag == DragMode.None)
        {
            return;
        }

        var left = _snipDrag is DragMode.TopLeft or DragMode.BottomLeft;
        var top = _snipDrag is DragMode.TopLeft or DragMode.TopRight;
        var anchorX = left ? _snip.X + _snip.Z : _snip.X;
        var anchorY = top ? _snip.Y + _snip.W : _snip.Y;

        if (aspect <= 0f)
        {
            var nx = Math.Clamp(m.X, left ? 0f : anchorX + minSide, left ? anchorX - minSide : vpSize.X);
            var ny = Math.Clamp(m.Y, top ? 0f : anchorY + minSide, top ? anchorY - minSide : vpSize.Y);
            _snip.X = MathF.Min(nx, anchorX);
            _snip.Y = MathF.Min(ny, anchorY);
            _snip.Z = MathF.Abs(nx - anchorX);
            _snip.W = MathF.Abs(ny - anchorY);
            return;
        }

        // Aspect-locked corner resize anchored on the opposite corner.
        var maxW = left ? anchorX : vpSize.X - anchorX;
        var maxH = top ? anchorY : vpSize.Y - anchorY;
        var wByX = MathF.Abs(m.X - anchorX);
        var wByY = MathF.Abs(m.Y - anchorY) * aspect;
        var newW = Math.Clamp(MathF.Max(wByX, wByY), MathF.Min(minSide, maxW),
            MathF.Min(maxW, maxH * aspect));
        var newH = newW / aspect;
        _snip.X = left ? anchorX - newW : anchorX;
        _snip.Y = top ? anchorY - newH : anchorY;
        _snip.Z = newW;
        _snip.W = newH;
    }

    private DragMode SnipHitTest(Vector2 m, float scale)
    {
        var handle = new Vector2(HandleHitPixels, HandleHitPixels) * scale;
        var corners = new (DragMode Mode, Vector2 Pos)[]
        {
            (DragMode.TopLeft, new Vector2(_snip.X, _snip.Y)),
            (DragMode.TopRight, new Vector2(_snip.X + _snip.Z, _snip.Y)),
            (DragMode.BottomLeft, new Vector2(_snip.X, _snip.Y + _snip.W)),
            (DragMode.BottomRight, new Vector2(_snip.X + _snip.Z, _snip.Y + _snip.W)),
        };
        foreach (var (mode, pos) in corners)
        {
            if (MathF.Abs(m.X - pos.X) <= handle.X && MathF.Abs(m.Y - pos.Y) <= handle.Y)
            {
                return mode;
            }
        }
        var inside = m.X >= _snip.X && m.X <= _snip.X + _snip.Z
            && m.Y >= _snip.Y && m.Y <= _snip.Y + _snip.W;
        return inside ? DragMode.Move : DragMode.None;
    }

    /// <summary>Selection chrome: border, thirds, handles, size tag, action pills.</summary>
    private void DrawSnipSelection(ImDrawListPtr dl, Vector2 vpMin, Vector2 vpMax, float scale)
    {
        var accent = SpheneTheme.Current.AccentCyan;
        var selMin = vpMin + new Vector2(_snip.X, _snip.Y);
        var selMax = selMin + new Vector2(_snip.Z, _snip.W);

        var guide = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.16f));
        for (var i = 1; i <= 2; i++)
        {
            var tx = selMin.X + (selMax.X - selMin.X) * i / 3f;
            var ty = selMin.Y + (selMax.Y - selMin.Y) * i / 3f;
            dl.AddLine(new Vector2(tx, selMin.Y), new Vector2(tx, selMax.Y), guide);
            dl.AddLine(new Vector2(selMin.X, ty), new Vector2(selMax.X, ty), guide);
        }

        dl.AddRect(selMin, selMax, SpheneUI.ColorUtils.ToU32(accent), 0f,
            ImDrawFlags.None, MathF.Max(1.5f, 1.5f * scale));

        var handleSize = 5f * scale;
        var handleColor = SpheneUI.ColorUtils.ToU32(Vector4.One);
        foreach (var corner in new[]
        {
            selMin, new Vector2(selMax.X, selMin.Y),
            new Vector2(selMin.X, selMax.Y), selMax,
        })
        {
            dl.AddRectFilled(corner - new Vector2(handleSize), corner + new Vector2(handleSize),
                handleColor, 2f * scale);
        }

        // Pixel size tag above the selection (below if it would clip).
        var sizeText = $"{(int)MathF.Round(_snip.Z)} × {(int)MathF.Round(_snip.W)}";
        var tagW = ImGui.CalcTextSize(sizeText).X + 14f * scale;
        var tagH = ImGui.GetTextLineHeight() + 8f * scale;
        var tagMin = new Vector2(
            Math.Clamp(selMin.X + (_snip.Z - tagW) * 0.5f, vpMin.X + 6f * scale,
                vpMax.X - tagW - 6f * scale),
            selMin.Y - tagH - 6f * scale);
        if (tagMin.Y < vpMin.Y + (SnipBarHeight + 22f) * scale)
        {
            tagMin.Y = MathF.Min(selMax.Y + 6f * scale, vpMax.Y - tagH - 4f * scale);
        }
        var tagMax = tagMin + new Vector2(tagW, tagH);
        dl.AddRectFilled(tagMin, tagMax, SpheneUI.ColorUtils.ToU32(
            new Vector4(0f, 0f, 0f, 0.75f)), tagH * 0.5f);
        dl.AddText(tagMin + new Vector2(7f * scale, 4f * scale),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), sizeText);

        // Action pills under the selection (inside it when near the bottom edge).
        DrawSnipActions(dl, selMin, selMax, vpMin, vpMax, scale);
    }

    private void DrawSnipActions(ImDrawListPtr dl, Vector2 selMin, Vector2 selMax,
        Vector2 vpMin, Vector2 vpMax, float scale)
    {
        var padX = SnipPillPadX * scale;
        var gap = SnipPillGap * scale;
        var pillH = SnipActionHeight * scale;
        var captureLabel = "Capture";
        var clearLabel = "Clear";
        var bounds = SnipActionsBounds(vpMin, vpMax, scale);
        var pen = new Vector2(bounds.X + 4f * scale, bounds.Y + 4f * scale);

        // HUD-hide changes take a frame to reach the back buffer · hold the
        // capture briefly so the first shot never keeps the old HUD.
        var settling = _snipHideUi
            && ImGui.GetTime() - _snipUiToggledAt < SnipUiSettleSeconds;
        using (var dis = ImRaii.Disabled(settling))
        {
            if (SnipPill(dl, ref pen, "##snip-take", captureLabel, true, false,
                    padX, pillH))
            {
                CompleteCapture(selMin, selMax);
            }
        }
        pen.X += gap;
        if (SnipPill(dl, ref pen, "##snip-clear", clearLabel, false, false,
                padX, pillH))
        {
            _snip = Vector4.Zero;
            _snipDrag = DragMode.None;
        }
    }

    /// <summary>
    /// Where the capture/clear pills sit for the current selection · used for
    /// both drawing and the drag swallow test so they can never disagree.
    /// </summary>
    private Vector4 SnipActionsBounds(Vector2 vpMin, Vector2 vpMax, float scale)
    {
        var padX = SnipPillPadX * scale;
        var gap = SnipPillGap * scale;
        var pillH = SnipActionHeight * scale;
        var totalW = ImGui.CalcTextSize("Capture").X + padX * 2f
            + gap + ImGui.CalcTextSize("Clear").X + padX * 2f;
        var selMin = vpMin + new Vector2(_snip.X, _snip.Y);
        var selMax = selMin + new Vector2(_snip.Z, _snip.W);

        var top = selMax.Y + 10f * scale;
        if (top + pillH > vpMax.Y - 10f * scale)
        {
            top = selMin.Y - 10f * scale - pillH;
        }
        if (top < vpMin.Y + (SnipBarHeight + 26f) * scale)
        {
            top = selMax.Y - 10f * scale - pillH;
        }
        var left = Math.Clamp(selMin.X + (_snip.Z - totalW) * 0.5f,
            vpMin.X + 6f * scale, vpMax.X - totalW - 6f * scale);
        return new Vector4(left - 4f * scale, top - 4f * scale,
            totalW + 8f * scale, pillH + 8f * scale);
    }

    /// <summary>Axis-aligned point-in-rect test for an x/y/w/h vector.</summary>
    private static bool RectContains(Vector4 rect, Vector2 p)
        => p.X >= rect.X && p.X <= rect.X + rect.Z
            && p.Y >= rect.Y && p.Y <= rect.Y + rect.W;

    /// <summary>Grabs the picked screen region and lands it in the editor.</summary>
    private void CompleteCapture(Vector2 displayMin, Vector2 displayMax)
    {
        _captureError = null;
        if (!_captureService.TryCapture(displayMin, displayMax,
                out var pixels, out var width, out var height))
        {
            _captureError = "Capture failed · the screen region could not be read.";
            return;
        }

        try
        {
            using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            var bytes = ms.ToArray();
            EndCapture();
            _stage = Stage.Edit;
            _filePath = null;
            _sourceLabel = "Screen capture";
            WindowName = $"Image Studio · {_spec.Title}###SphenePhotoStudio";
            LoadBytes(bytes, width, height);
            IsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Captured pixels failed to encode");
            _captureError = "Capture failed · the region could not be encoded.";
        }
    }

    // ─────────────────────── Crop interaction ───────────────────────────

    private void HandleCropInteraction(Vector2 canvasMin, Vector2 canvasSize,
        Vector2 imgMin, Vector2 dispSize, Vector4 chipRect)
    {
        var mouse = ImGui.GetIO().MousePos;
        var m = (mouse - imgMin) / dispSize;
        var onChip = RectContains(chipRect, mouse);
        var canvasMax = canvasMin + canvasSize;
        var canvasHovered = !onChip
            && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows
                | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            && mouse.X >= canvasMin.X && mouse.Y >= canvasMin.Y
            && mouse.X <= canvasMax.X && mouse.Y <= canvasMax.Y;

        if (canvasHovered)
        {
            ImGui.SetMouseCursor(HitTest(m, dispSize) switch
            {
                DragMode.Move => ImGuiMouseCursor.Hand,
                DragMode.TopLeft or DragMode.BottomRight => ImGuiMouseCursor.ResizeNwse,
                DragMode.TopRight or DragMode.BottomLeft => ImGuiMouseCursor.ResizeNesw,
                _ => ImGuiMouseCursor.Arrow,
            });
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _drag = HitTest(m, dispSize);
                if (_drag == DragMode.Move)
                {
                    _grabOffset = m - new Vector2(_crop.X, _crop.Y);
                }
                else if (_drag == DragMode.None)
                {
                    // click outside: recenter the selection on the click point
                    _crop.X = Math.Clamp(m.X - _crop.Z * 0.5f, 0f, 1f - _crop.Z);
                    _crop.Y = Math.Clamp(m.Y - _crop.W * 0.5f, 0f, 1f - _crop.W);
                }
            }
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            _drag = DragMode.None;
        }
        if (_drag == DragMode.None || !ImGui.IsMouseDown(ImGuiMouseButton.Left)
            || !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            return;
        }

        var minW = MathF.Max(20f / dispSize.X, MinSourcePixels / _imageWidth);
        if (_drag == DragMode.Move)
        {
            _crop.X = Math.Clamp(m.X - _grabOffset.X, 0f, 1f - _crop.Z);
            _crop.Y = Math.Clamp(m.Y - _grabOffset.Y, 0f, 1f - _crop.W);
            return;
        }
        if (_drag == DragMode.None)
        {
            return;
        }

        var left = _drag is DragMode.TopLeft or DragMode.BottomLeft;
        var top = _drag is DragMode.TopLeft or DragMode.TopRight;

        if (_aspect <= 0f)
        {
            // free-form: each corner drags its own edges, opposite corner fixed
            var anchorX = left ? _crop.X + _crop.Z : _crop.X;
            var anchorY = top ? _crop.Y + _crop.W : _crop.Y;
            var nx = Math.Clamp(m.X, left ? 0f : anchorX + minW, left ? anchorX - minW : 1f);
            var ny = Math.Clamp(m.Y, top ? 0f : anchorY + minW, top ? anchorY - minW : 1f);
            _crop.X = MathF.Min(nx, anchorX);
            _crop.Y = MathF.Min(ny, anchorY);
            _crop.Z = MathF.Abs(nx - anchorX);
            _crop.W = MathF.Abs(ny - anchorY);
            return;
        }

        // aspect-locked corner resize anchored on the opposite corner
        var anchor2X = left ? _crop.X + _crop.Z : _crop.X;
        var anchor2Y = top ? _crop.Y + _crop.W : _crop.Y;

        // normalized units: w maps to image width, h to image height
        var widthForHeight = _aspect * _imageHeight / _imageWidth;
        var wByX = MathF.Abs(m.X - anchor2X);
        var wByY = MathF.Abs(m.Y - anchor2Y) * widthForHeight;
        var maxX = left ? anchor2X : 1f - anchor2X;
        var maxY = top ? anchor2Y : 1f - anchor2Y;
        var maxW = MathF.Min(maxX, maxY * widthForHeight);
        var newW = Math.Clamp(MathF.Max(wByX, wByY), MathF.Min(minW, maxW), maxW);
        var newH = newW / widthForHeight;

        _crop.X = left ? anchor2X - newW : anchor2X;
        _crop.Y = top ? anchor2Y - newH : anchor2Y;
        _crop.Z = newW;
        _crop.W = newH;
    }

    private DragMode HitTest(Vector2 m, Vector2 dispSize)
    {
        var handle = new Vector2(HandleHitPixels / dispSize.X, HandleHitPixels / dispSize.Y);
        var corners = new (DragMode Mode, Vector2 Pos)[]
        {
            (DragMode.TopLeft, new Vector2(_crop.X, _crop.Y)),
            (DragMode.TopRight, new Vector2(_crop.X + _crop.Z, _crop.Y)),
            (DragMode.BottomLeft, new Vector2(_crop.X, _crop.Y + _crop.W)),
            (DragMode.BottomRight, new Vector2(_crop.X + _crop.Z, _crop.Y + _crop.W)),
        };
        foreach (var (mode, pos) in corners)
        {
            if (MathF.Abs(m.X - pos.X) <= handle.X && MathF.Abs(m.Y - pos.Y) <= handle.Y)
            {
                return mode;
            }
        }
        var inside = m.X >= _crop.X && m.X <= _crop.X + _crop.Z
            && m.Y >= _crop.Y && m.Y <= _crop.Y + _crop.W;
        return inside ? DragMode.Move : DragMode.None;
    }

    private void DrawCropOverlay(ImDrawListPtr drawList, Vector2 imgMin, Vector2 dispSize)
    {
        var cropMin = imgMin + new Vector2(_crop.X * dispSize.X, _crop.Y * dispSize.Y);
        var cropMax = imgMin + new Vector2((_crop.X + _crop.Z) * dispSize.X,
            (_crop.Y + _crop.W) * dispSize.Y);
        var imgMax = imgMin + dispSize;
        var dim = SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.55f));
        var scale = SpheneUI.Scale;

        // dim everything outside the selection
        drawList.AddRectFilled(imgMin, new Vector2(imgMax.X, cropMin.Y), dim);
        drawList.AddRectFilled(new Vector2(imgMin.X, cropMax.Y), imgMax, dim);
        drawList.AddRectFilled(new Vector2(imgMin.X, cropMin.Y), new Vector2(cropMin.X, cropMax.Y), dim);
        drawList.AddRectFilled(new Vector2(cropMax.X, cropMin.Y), new Vector2(imgMax.X, cropMax.Y), dim);

        // rule-of-thirds guides
        var guide = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(Vector4.One, 0.18f));
        for (var i = 1; i <= 2; i++)
        {
            var tx = cropMin.X + (cropMax.X - cropMin.X) * i / 3f;
            var ty = cropMin.Y + (cropMax.Y - cropMin.Y) * i / 3f;
            drawList.AddLine(new Vector2(tx, cropMin.Y), new Vector2(tx, cropMax.Y), guide);
            drawList.AddLine(new Vector2(cropMin.X, ty), new Vector2(cropMax.X, ty), guide);
        }

        drawList.AddRect(cropMin, cropMax,
            SpheneUI.ColorUtils.ToU32(SpheneTheme.Current.AccentCyan), 0f,
            ImDrawFlags.None, MathF.Max(1.5f, 1.5f * scale));

        // row strips fade into the row background · mark the fading tail
        // inside the selection so the picked area reads as displayed
        if (_kind == CropKind.RowImage)
        {
            var fadeFrom = cropMin.X
                + (cropMax.X - cropMin.X) * SpheneUI.Profile.RowImageFadeStart;
            var transparent = SpheneUI.ColorUtils.ToU32(Vector4.Zero);
            var shade = SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.45f));
            drawList.PushClipRect(cropMin, cropMax, true);
            drawList.AddRectFilledMultiColor(new Vector2(fadeFrom, cropMin.Y),
                cropMax, transparent, shade, shade, transparent);
            var dashX = fadeFrom;
            for (var y = cropMin.Y; y < cropMax.Y - 4f * scale; y += 8f * scale)
            {
                drawList.AddLine(new Vector2(dashX, y),
                    new Vector2(dashX, MathF.Min(y + 4f * scale, cropMax.Y)),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                        SpheneTheme.Current.AccentCyan, 0.55f)), 1f);
            }
            drawList.PopClipRect();
        }

        // corner handles
        var handleSize = 5f * scale;
        var handleColor = SpheneUI.ColorUtils.ToU32(Vector4.One);
        foreach (var corner in new[]
        {
            cropMin, new Vector2(cropMax.X, cropMin.Y),
            new Vector2(cropMin.X, cropMax.Y), cropMax,
        })
        {
            drawList.AddRectFilled(corner - new Vector2(handleSize), corner + new Vector2(handleSize),
                handleColor, 2f * scale);
        }

        // live pixel size of the crop · the exact output footprint
        if (_drag != DragMode.None || _applyBusy)
        {
            var pxW = (int)MathF.Round(_crop.Z * _imageWidth);
            var pxH = (int)MathF.Round(_crop.W * _imageHeight);
            var text = $"{pxW}×{pxH}";
            var tw = ImGui.CalcTextSize(text).X;
            var tagMin = new Vector2(
                Math.Clamp(cropMin.X + (cropMax.X - cropMin.X - tw - 12f * scale) * 0.5f,
                    imgMin.X + 4f * scale, imgMax.X - tw - 16f * scale),
                MathF.Max(imgMin.Y + 4f * scale, cropMin.Y - ImGui.GetTextLineHeight() - 10f * scale));
            var pad = new Vector2(6f, 3f) * scale;
            drawList.AddRectFilled(tagMin - pad,
                tagMin + new Vector2(tw, ImGui.GetTextLineHeight()) + pad,
                SpheneUI.ColorUtils.ToU32(new Vector4(0f, 0f, 0f, 0.7f)), 6f * scale);
            drawList.AddText(tagMin, SpheneUI.ColorUtils.ToU32(Vector4.One), text);
        }
    }

    /// <summary>
    /// Adjusts the full frame (so the live preview matches the export), crops
    /// in source pixels, downscales to the target and retries smaller when over
    /// the byte limit. Multi-frame sources keep their animation · every frame
    /// is adjusted and cropped and the result encodes as GIF.
    /// </summary>
    private static byte[]? RenderCrop(byte[] imageBytes, Vector4 crop, KindSpec spec,
        float brightF, float satF, float vigF)
    {
        try
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            var animated = image.Frames.Count > 1;
            if (MathF.Abs(brightF - 1f) > 0.005f || MathF.Abs(satF - 1f) > 0.005f)
            {
                image.Mutate(x => x.Brightness(brightF).Saturate(satF));
            }

            var px = Math.Clamp((int)MathF.Round(crop.X * image.Width), 0, image.Width - 1);
            var py = Math.Clamp((int)MathF.Round(crop.Y * image.Height), 0, image.Height - 1);
            var pw = Math.Clamp((int)MathF.Round(crop.Z * image.Width), 1, image.Width - px);
            var ph = Math.Clamp((int)MathF.Round(crop.W * image.Height), 1, image.Height - py);
            image.Mutate(x => x.Crop(new SixLabors.ImageSharp.Rectangle(px, py, pw, ph)));

            // Vignette last · its falloff belongs to the output frame's edges,
            // not the source's.
            if (vigF > 0.005f)
            {
                ApplyVignette(image, vigF);
            }

            var outW = Math.Min(spec.TargetWidth, pw);
            var outH = spec.Aspect > 0f
                ? Math.Max(1, (int)MathF.Round(outW / spec.Aspect))
                : Math.Max(1, (int)MathF.Round(outW * ph / (float)pw));
            while (true)
            {
                using var output = image.Clone(x => x.Resize(outW, outH));
                using var ms = new MemoryStream();
                if (animated)
                {
                    output.SaveAsGif(ms);
                }
                else if (spec.UseWebp)
                {
                    output.SaveAsWebp(ms, new WebpEncoder { Quality = 85 });
                }
                else
                {
                    output.SaveAsPng(ms);
                }
                var bytes = ms.ToArray();
                if (bytes.Length <= spec.MaxBytes || outW <= 96)
                {
                    return bytes;
                }
                outW /= 2;
                outH /= 2;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Radial darken toward black · implemented per-pixel so the strength is
    /// fully controllable and identical between preview and export. Applied to
    /// every frame of animated sources. <paramref name="region"/> is an
    /// optional normalized rect the falloff is centered on; pixels outside
    /// stay untouched, so the preview can vignette the crop window while the
    /// export vignettes the already-cropped frame.
    /// </summary>
    private static void ApplyVignette(Image<Rgba32> image, float strength,
        Vector4? region = null)
    {
        foreach (var frame in image.Frames)
        {
            ApplyVignetteFrame(frame, strength, region);
        }
    }

    private static void ApplyVignetteFrame(ImageFrame<Rgba32> frame, float strength,
        Vector4? region)
    {
        var rx = region is { } r
            ? Math.Clamp((int)MathF.Floor(r.X * frame.Width), 0, frame.Width - 1) : 0;
        var ry = region is { } r2
            ? Math.Clamp((int)MathF.Floor(r2.Y * frame.Height), 0, frame.Height - 1) : 0;
        var rw = region is { } r3
            ? Math.Clamp((int)MathF.Ceiling(r3.Z * frame.Width), 1, frame.Width - rx)
            : frame.Width - rx;
        var rh = region is { } r4
            ? Math.Clamp((int)MathF.Ceiling(r4.W * frame.Height), 1, frame.Height - ry)
            : frame.Height - ry;
        var cx = rx + rw * 0.5f;
        var cy = ry + rh * 0.5f;
        var invMax = 1f / MathF.Sqrt(rw * rw + rh * rh) * 2f;
        frame.ProcessPixelRows(accessor =>
        {
            for (var y = ry; y < ry + rh; y++)
            {
                var row = accessor.GetRowSpan(y);
                var dy = y - cy;
                for (var x = rx; x < rx + rw; x++)
                {
                    var dx = x - cx;
                    var d = MathF.Sqrt(dx * dx + dy * dy) * invMax;
                    var t = Math.Clamp((d - 0.45f) / 0.55f, 0f, 1f);
                    if (t <= 0f)
                    {
                        continue;
                    }
                    var f = 1f - strength * t * t * (3f - 2f * t);
                    var p = row[x];
                    row[x] = new Rgba32(
                        (byte)Math.Clamp((int)(p.R * f), 0, 255),
                        (byte)Math.Clamp((int)(p.G * f), 0, 255),
                        (byte)Math.Clamp((int)(p.B * f), 0, 255), p.A);
                }
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            EndCapture();
            _previewSrc?.Dispose();
            _previewSrc = null;
        }
        base.Dispose(disposing);
    }
}
