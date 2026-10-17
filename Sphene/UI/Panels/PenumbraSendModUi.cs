using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.API.Data.Comparer;
using Sphene.FileCache;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.WebAPI.Files;
using Sphene.API.Dto.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class PenumbraSendModUi : WindowMediatorSubscriberBase
{
    private const char WebsiteBacktick = '`';
    private static readonly byte[] _newLineUtf8 = [(byte)'\n'];

    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly SpheneConfigService _configService;
    private readonly ShrinkU.Services.PenumbraIpc _shrinkuPenumbraIpc;
    private readonly ShrinkU.Services.TextureBackupService _shrinkuBackupService;
    private readonly FileUploadManager _fileUploadManager;
    private readonly FileCacheManager _fileCacheManager;
    private readonly SpheneProfileManager _spheneProfileManager;

    private string _sendPenumbraModFilter = string.Empty;
    private string _recipientFilter = string.Empty;
    private string? _selectedPenumbraModFolder;
    private readonly List<(string ModFolderName, string DisplayName)> _penumbraMods = new();
    private readonly HashSet<string> _selectedPenumbraModFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedPenumbraRecipients = new(StringComparer.Ordinal);
    private string _sendPenumbraModStatusText = string.Empty;
    private bool _sendPenumbraModStatusIsError;
    private bool _isSendingPenumbraMod;
    private CancellationTokenSource? _sendPenumbraModCts;
    private DateTime? _uploadStartTime;
    private readonly HashSet<string> _currentUploadHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _uploadStateLock = new();
    private bool _sendCurrentModState;
    private int _reloadPenumbraModsRequested = 1;
    private DateTime _nextPenumbraModListValidationUtc = DateTime.MinValue;
    private readonly Dictionary<string, string> _lastKnownPenumbraModMap = new(StringComparer.OrdinalIgnoreCase);
    // Lazily resolved per visible mod row: meta.json details + optional artwork.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<ShrinkU.Services.PenumbraIpc.ModMetadata?>> _modMetaTasks
        = new(StringComparer.OrdinalIgnoreCase);
    // Last decoded avatar bytes per user — a new array instance means the
    // profile was (re)fetched and the cached texture must be evicted.
    private readonly Dictionary<string, byte[]> _avatarBytes = new(StringComparer.Ordinal);
    private float _sendTailReserve = -1f;
    private float _debugHostScroll;
    private float _debugHostScrollMax = -1f;

    public PenumbraSendModUi(
        ILogger<PenumbraSendModUi> logger,
        SpheneMediator mediator,
        UiSharedService uiSharedService,
        PairManager pairManager,
        SpheneConfigService configService,
        ShrinkU.Services.PenumbraIpc shrinkuPenumbraIpc,
        ShrinkU.Services.TextureBackupService shrinkuBackupService,
        FileUploadManager fileUploadManager,
        FileCacheManager fileCacheManager,
        SpheneProfileManager spheneProfileManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Send Mods###SpheneSendMods", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _configService = configService;
        _shrinkuPenumbraIpc = shrinkuPenumbraIpc;
        _shrinkuBackupService = shrinkuBackupService;
        _fileUploadManager = fileUploadManager;
        _fileCacheManager = fileCacheManager;
        _spheneProfileManager = spheneProfileManager;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500f, 0f),
            MaximumSize = new Vector2(500f, 1000f),
        };
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
        IsOpen = false;

        Mediator.Subscribe<OpenSendPenumbraModWindow>(this, OnOpenMessage);
        _shrinkuPenumbraIpc.ModAdded += OnPenumbraModAdded;
        _shrinkuPenumbraIpc.ModDeleted += OnPenumbraModDeleted;
        _shrinkuPenumbraIpc.ModMoved += OnPenumbraModMoved;
        _shrinkuPenumbraIpc.ModPathChanged += OnPenumbraModPathChanged;
    }

    // AlwaysAutoResize fights the fold animation — the window would not shrink.
    protected override bool ChromeCollapsible => false;

    private void OnOpenMessage(OpenSendPenumbraModWindow message)
    {
        var initialRecipientUid = message.Pair?.UserData.UID;
        _sendPenumbraModFilter = string.Empty;
        _recipientFilter = string.Empty;
        _selectedPenumbraModFolder = message.PreselectedModFolderName;
        _sendCurrentModState = false;
        _penumbraMods.Clear();
        _selectedPenumbraModFolders.Clear();
        _selectedPenumbraRecipients.Clear();
        if (!string.IsNullOrWhiteSpace(initialRecipientUid))
        {
            _selectedPenumbraRecipients.Add(initialRecipientUid);
        }
        if (!string.IsNullOrWhiteSpace(_selectedPenumbraModFolder))
        {
            _selectedPenumbraModFolders.Add(_selectedPenumbraModFolder);
        }

        _sendPenumbraModStatusText = string.Empty;
        _sendPenumbraModStatusIsError = false;
        _isSendingPenumbraMod = false;
        lock (_uploadStateLock)
        {
            _uploadStartTime = null;
            _currentUploadHashes.Clear();
        }

        try { _sendPenumbraModCts?.Cancel(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel SendPenumbraMod on open"); }
        try { _sendPenumbraModCts?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose SendPenumbraModCTS on open"); }
        _sendPenumbraModCts = null;
        RequestPenumbraModReload();
    }

    protected override void DrawInternal()
    {
        DrawContent(isEmbedded: false);
    }

    internal void DrawEmbedded()
    {
        DrawContent(isEmbedded: true);
    }

    private void DrawContent(bool isEmbedded)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var idSuffix = isEmbedded ? "Embedded" : "Window";
        var accent = SpheneUI.Palette.EtherealPurple;

        var style = ImGui.GetStyle();
        var navAvail = ImGui.GetContentRegionAvail().Y - ImGui.GetScrollY();
        // Diagnostics: a nonzero host scroll range here is the residual scroll
        // behind the wheel jitter — logged on change so /xllog shows it.
        var hostScroll = ImGui.GetScrollY();
        var hostMax = ImGui.GetScrollMaxY();
        if (MathF.Abs(hostScroll - _debugHostScroll) > 0.5f
            || MathF.Abs(hostMax - _debugHostScrollMax) > 0.5f)
        {
            _debugHostScroll = hostScroll;
            _debugHostScrollMax = hostMax;
            _logger.LogDebug("SendMods host scroll {Scroll:F1}/{Max:F1} navAvail {Avail:F1} tailReserve {Reserve:F1}",
                hostScroll, hostMax, navAvail, _sendTailReserve);
        }
        // The bottom reserve is measured, not estimated: after the first frame
        // _sendTailReserve holds the real footer chrome height plus the host
        // scroll area's trailing inset, so no residual scrollMax can survive.
        var footerReserve = _sendTailReserve > 0f
            ? _sendTailReserve
            : ImGui.GetFrameHeightWithSpacing() * 2f
                + style.ItemSpacing.Y * 6f
                + SpheneUI.Layout.ScrollAreaContentInset * scale;
        var regionH = MathF.Max(ImGui.GetTextLineHeightWithSpacing() * 2f,
            navAvail - footerReserve);
        // The page is a fixed clip region, not a scroll area: the cards fill it
        // exactly and their inner lists own the scrolling. NoScrollWithMouse
        // keeps stray wheel deltas from jittering the host for a frame.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var regionBegun = ImGui.BeginChild("SendModsRegion" + idSuffix,
            new Vector2(0f, regionH), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (regionBegun)
        {
            // EndTable appends ItemSpacing after the last row — keep the bound
            // that far inside the clip edge or the region's own scrollMax
            // stays a few px positive and chaining can nudge it.
            var colBottom = ImGui.GetCursorScreenPos().Y + regionH - style.ItemSpacing.Y - scale;

            if (!isEmbedded)
            {
                SpheneUI.Text.PageHeader("Send Mods",
                    "Pick the Penumbra mods to package, choose the recipients, then share.");
            }
            else
            {
                // Embedded: the page title already lives in the hub breadcrumb —
                // the uniform page intro is a single muted line.
                SpheneUI.Text.PageIntro(
                    "Pick the Penumbra mods to package, choose the recipients, then share.");
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
            }

            if (!_shrinkuPenumbraIpc.APIAvailable)
            {
                SpheneUI.Layout.Callout("Penumbra API not available.", FontAwesomeIcon.ExclamationCircle, SpheneUI.Colors.Danger);
            }
            else
            {
                DrawSendBody(idSuffix, accent, scale, colBottom);
            }
        }
        ImGui.EndChild();

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        DrawSendFooterBar(scale, accent, idSuffix);

        // Self-calibrating reserve: the leftover under the last row must cover
        // the host scroll area's trailing inset — anything less leaves a few
        // px of scrollMax that shows as a scroll/snap jitter on wheel input.
        var leftover = ImGui.GetContentRegionAvail().Y - ImGui.GetScrollY();
        var targetTail = style.ItemSpacing.Y
            + SpheneUI.Layout.ScrollAreaContentInset * scale + 2f * scale;
        _sendTailReserve = Math.Clamp(
            footerReserve + (targetTail - leftover), 0f, MathF.Max(0f, navAvail));
    }

    /// <summary>Library picker on the left, recipients + share panel stacked on the right.</summary>
    private void DrawSendBody(string idSuffix, Vector4 accent, float scale, float colBottom)
    {
        ValidatePenumbraModListSnapshot();

        if (ShouldReloadPenumbraMods() || _penumbraMods.Count == 0)
        {
            LoadPenumbraMods();
        }

        var existingModFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _penumbraMods)
        {
            existingModFolderNames.Add(mod.ModFolderName);
        }

        if (_selectedPenumbraModFolder != null && !existingModFolderNames.Contains(_selectedPenumbraModFolder))
        {
            _selectedPenumbraModFolder = null;
        }

        if (_selectedPenumbraModFolder == null && _penumbraMods.Count > 0)
        {
            _selectedPenumbraModFolder = _penumbraMods[0].ModFolderName;
        }

        if (_selectedPenumbraModFolders.Count > 0)
        {
            var toRemove = new List<string>();
            foreach (var selected in _selectedPenumbraModFolders)
            {
                if (!existingModFolderNames.Contains(selected))
                {
                    toRemove.Add(selected);
                }
            }

            foreach (var remove in toRemove)
            {
                _selectedPenumbraModFolders.Remove(remove);
            }
        }

        var filtered = GetFilteredPenumbraMods();

        var candidatePairs = _pairManager.DirectPairs
            .Where(p => p.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
            .Select(p =>
            {
                var uid = p.UserData.UID ?? string.Empty;
                var aliasOrUid = p.UserData.AliasOrUID;
                var displayName = _uiSharedService.GetPreferredUserDisplayName(p.UserData);
                return (Pair: p, Uid: uid, AliasOrUid: aliasOrUid, DisplayName: displayName);
            })
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ImGui.BeginTable("SendModLayout" + idSuffix, 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Library", ImGuiTableColumnFlags.WidthStretch, 0.55f);
            ImGui.TableSetupColumn("Send", ImGuiTableColumnFlags.WidthStretch, 0.45f);

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            SpheneUI.Layout.SectionCard(
                () => DrawModLibrary(filtered, accent, idSuffix, colBottom),
                title: "1 · Mod Library", accentColor: accent);

            ImGui.TableSetColumnIndex(1);
            var recipientsCardBottom = MathF.Max(ImGui.GetCursorScreenPos().Y + 170f * scale, colBottom);
            SpheneUI.Layout.SectionCard(
                () => DrawRecipientPicker(candidatePairs, recipientsCardBottom, accent, idSuffix),
                title: "2 · Recipients", accentColor: accent);

            ImGui.EndTable();
            SpheneUI.Lint.CheckFillBottom(colBottom, "SendMods.Table");
        }
    }

    /// <summary>
    /// The user's Sphene profile picture as a cached texture — null while the
    /// profile or texture is still loading. Evicts the cache entry when the
    /// profile data instance changes (avatar updated/refetched).
    /// </summary>
    private IDalamudTextureWrap? AvatarFor(UserData userData)
    {
        var uid = userData.UID;
        if (string.IsNullOrWhiteSpace(uid)) return null;
        var bytes = _spheneProfileManager.GetSpheneProfile(userData).ImageData.Value;
        if (bytes.Length == 0) return null;
        var key = $"modshare:avatar:{uid}";
        if (!_avatarBytes.TryGetValue(uid, out var cached) || !ReferenceEquals(cached, bytes))
        {
            _avatarBytes[uid] = bytes;
            SpheneUI.Media.Evict(key);
        }
        return SpheneUI.Media.Texture(key, bytes);
    }

    /// <summary>Requests mod metadata once per folder; returns it once the task resolved.</summary>
    private ShrinkU.Services.PenumbraIpc.ModMetadata? MetaOf(string modFolderName)
    {
        var task = _modMetaTasks.GetOrAdd(modFolderName,
            f => Task.Run(() => _shrinkuPenumbraIpc.GetModMetadataAsync(f)));
        return task is { IsCompletedSuccessfully: true } ? task.Result : null;
    }

    /// <summary>Loads the mod's artwork bytes for the texture cache; null when absent/unreadable.</summary>
    private static byte[]? ReadModImage(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reserve below a list child so its card border closes flush on the bound.</summary>
    private static float FillReserve
        => SpheneUI.Spacing.CardPadding.Scaled + SpheneUI.Scale;

    /// <summary>The pickable mod library: filter, selection toolbar, scroll-faded rich rows.</summary>
    private void DrawModLibrary(List<(string ModFolderName, string DisplayName)> filtered,
        Vector4 accent, string idSuffix, float colBottom)
    {
        ImGui.SetNextItemWidth(-1);
        SpheneUI.Controls.InputTextWithHint("##penumbraModFilter" + idSuffix,
            "Filter mods…", ref _sendPenumbraModFilter, 128);
        DrawCardToolbar(
            $"{_selectedPenumbraModFolders.Count} selected · click a row to toggle",
            _selectedPenumbraModFolders.Count > 0 ? "Clear" : null,
            () => _selectedPenumbraModFolders.Clear());

        var listH = MathF.Max(80f * SpheneUI.Scale,
            colBottom - ImGui.GetCursorScreenPos().Y - FillReserve);
        if (SpheneUI.Layout.BeginScrollArea("ModList" + idSuffix, new Vector2(0f, listH)))
        {
            if (filtered.Count == 0)
            {
                SpheneUI.Text.Muted(_penumbraMods.Count == 0
                    ? "No Penumbra mods found."
                    : "No mods match the filter.");
            }
            else
            {
                foreach (var mod in filtered)
                {
                    var meta = MetaOf(mod.ModFolderName);
                    var metaText = meta == null
                        ? (string.Equals(mod.DisplayName, mod.ModFolderName, StringComparison.Ordinal) ? null : mod.ModFolderName)
                        : ModListRows.Meta(meta.Author,
                            string.IsNullOrWhiteSpace(meta.Version) ? null : $"v{meta.Version}");
                    var thumb = meta?.ImagePath is { } imgPath
                        ? SpheneUI.Media.Texture($"modthumb:{mod.ModFolderName}", () => ReadModImage(imgPath))
                        : null;

                    var isSelected = _selectedPenumbraModFolders.Contains(mod.ModFolderName);
                    var row = ModListRows.Draw(mod.ModFolderName + idSuffix, mod.DisplayName, metaText,
                        thumb, FontAwesomeIcon.PuzzlePiece, isSelected, accent, checkIndicator: true);
                    if (row.Clicked)
                    {
                        _selectedPenumbraModFolder = mod.ModFolderName;
                        if (!_selectedPenumbraModFolders.Add(mod.ModFolderName))
                        {
                            _selectedPenumbraModFolders.Remove(mod.ModFolderName);
                        }
                    }
                }
            }
        }
        SpheneUI.Layout.EndScrollArea(SpheneUI.Layout.NavRailBandColor());
    }

    /// <summary>The recipient picker: filter, selection toolbar, scroll-faded rich rows.</summary>
    private void DrawRecipientPicker(
        List<(Pair Pair, string Uid, string AliasOrUid, string DisplayName)> candidatePairs,
        float colBottom, Vector4 accent, string idSuffix)
    {
        ImGui.SetNextItemWidth(-1);
        SpheneUI.Controls.InputTextWithHint("##recipientFilter" + idSuffix,
            "Filter recipients…", ref _recipientFilter, 64);
        DrawCardToolbar(
            $"{_selectedPenumbraRecipients.Count} selected · paired users only",
            _selectedPenumbraRecipients.Count > 0 ? "Clear" : null,
            () => _selectedPenumbraRecipients.Clear());

        var listH = MathF.Max(80f * SpheneUI.Scale,
            colBottom - ImGui.GetCursorScreenPos().Y - FillReserve);
        if (SpheneUI.Layout.BeginScrollArea("RecipientList" + idSuffix, new Vector2(0f, listH)))
        {
            if (candidatePairs.Count == 0)
            {
                SpheneUI.Text.Muted("No paired users available for mod delivery.");
            }
            else
            {
                foreach (var entry in candidatePairs)
                {
                    var uid = entry.Uid;
                    if (string.IsNullOrWhiteSpace(uid))
                    {
                        continue;
                    }

                    var displayName = entry.DisplayName;
                    if (!string.IsNullOrWhiteSpace(_recipientFilter) &&
                        !displayName.Contains(_recipientFilter, StringComparison.OrdinalIgnoreCase) &&
                        !entry.AliasOrUid.Contains(_recipientFilter, StringComparison.OrdinalIgnoreCase) &&
                        !uid.Contains(_recipientFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var allowsMods = entry.Pair.OtherAllowsReceivingPenumbraMods;
                    if (!allowsMods)
                    {
                        _selectedPenumbraRecipients.Remove(uid);
                    }

                    var isSelected = _selectedPenumbraRecipients.Contains(uid);
                    var metaText = allowsMods ? entry.AliasOrUid : "receiving disabled";
                    var row = ModListRows.Draw(uid + idSuffix, displayName, metaText,
                        thumb: AvatarFor(entry.Pair.UserData), placeholderIcon: FontAwesomeIcon.User,
                        selected: isSelected, accent,
                        checkIndicator: true, enabled: allowsMods);
                    if (row.Clicked && !_selectedPenumbraRecipients.Add(uid))
                    {
                        _selectedPenumbraRecipients.Remove(uid);
                    }
                }
            }
        }
        SpheneUI.Layout.EndScrollArea(SpheneUI.Layout.NavRailBandColor());
    }

    /// <summary>Label summarizing the current mod selection for the footer bar.</summary>
    private string SelectedModsLabel()
    {
        if (_selectedPenumbraModFolders.Count == 0)
        {
            return "None";
        }
        if (_selectedPenumbraModFolders.Count == 1)
        {
            var only = _selectedPenumbraModFolders.First();
            return _penumbraMods.FirstOrDefault(m => string.Equals(m.ModFolderName, only, StringComparison.OrdinalIgnoreCase)).DisplayName ?? only;
        }
        return $"{_selectedPenumbraModFolders.Count} selected";
    }

    /// <summary>Page-level status bar pinned to the bottom: share context, status/progress and actions.</summary>
    private void DrawSendFooterBar(float scale, Vector4 accent, string idSuffix)
    {
        SpheneUI.Text.Muted("Mods:");
        ImGui.SameLine();
        SpheneUI.Text.Plain(SelectedModsLabel());
        ImGui.SameLine();
        SpheneUI.Text.Muted("· Recipients:");
        ImGui.SameLine();
        SpheneUI.Text.Plain(_selectedPenumbraRecipients.Count.ToString());
        ImGui.SameLine();
        SpheneUI.Icons.Icon(FontAwesomeIcon.InfoCircle, accent);
        SpheneUI.AttachTooltip("Packages upload to the Sphene server network and are shared with the selected users.");

        const string sendStateLabel = "Send current mod state (temporary)";
        var checkboxW = ImGui.CalcTextSize(sendStateLabel).X + ImGui.GetFrameHeight();
        ImGui.SameLine();
        SpheneUI.Controls.DockToCardEdge(checkboxW);
        var sendCurrent = _sendCurrentModState;
        if (SpheneUI.Controls.Checkbox(sendStateLabel + "##sendCurrent" + idSuffix, ref sendCurrent))
        {
            _sendCurrentModState = sendCurrent;
        }
        SpheneUI.AttachTooltip("When enabled, Sphene packages the current Penumbra mod folder contents into a temporary .pmp, uploads it, then deletes the temporary file. When disabled, Sphene prefers existing ShrinkU backups.");

        var canShare = !_isSendingPenumbraMod && _selectedPenumbraModFolders.Count > 0 && _selectedPenumbraRecipients.Count > 0;
        var shareButtonText = _selectedPenumbraModFolders.Count <= 1
            ? "Share Mod"
            : $"Share {_selectedPenumbraModFolders.Count} Mods";

        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var cancelW = _isSendingPenumbraMod ? ImGui.CalcTextSize("Cancel").X + style.FramePadding.X * 4f : 0f;
        var shareW = ImGui.CalcTextSize(shareButtonText).X + ImGui.GetFrameHeight() + style.FramePadding.X * 4f;

        ImGui.AlignTextToFramePadding();
        if (_isSendingPenumbraMod)
        {
            DrawUploadProgress(scale);
        }
        else if (!string.IsNullOrWhiteSpace(_sendPenumbraModStatusText))
        {
            var color = _sendPenumbraModStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
            SpheneUI.Text.Colored(_sendPenumbraModStatusText, color);
        }
        else
        {
            SpheneUI.Text.Muted("Select mods and recipients, then share.");
        }

        ImGui.SameLine();
        SpheneUI.Controls.DockToCardEdge(shareW + cancelW + (cancelW > 0f ? spacing : 0f));
        if (_isSendingPenumbraMod && _sendPenumbraModCts != null)
        {
            if (SpheneUI.Controls.SmallButton("Cancel"))
            {
                try { _sendPenumbraModCts.Cancel(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel SendPenumbraMod"); }
            }
            ImGui.SameLine();
        }
        if (SpheneUI.Controls.SegmentButton(shareButtonText, FontAwesomeIcon.Share,
                accent: accent, enabled: canShare))
        {
            StartSendPenumbraMods(_selectedPenumbraModFolders.ToList(), _sendCurrentModState);
        }
    }

    /// <summary>Muted status line plus an optional right-aligned text action inside a card header.</summary>
    private static void DrawCardToolbar(string status, string? actionLabel, Action? onAction)
    {
        SpheneUI.Text.Muted(status);
        if (actionLabel != null && onAction != null)
        {
            ImGui.SameLine();
            SpheneUI.Controls.DockToCardEdge(
                SpheneUI.Controls.SmallButtonWidth(actionLabel));
            if (SpheneUI.Controls.SmallButton(actionLabel))
            {
                onAction();
            }
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    private void DrawUploadProgress(float scale)
    {
        var currentUploads = _fileUploadManager.CurrentUploads.ToList();
        HashSet<string>? uploadHashes = null;
        DateTime? uploadStartTime;
        lock (_uploadStateLock)
        {
            uploadStartTime = _uploadStartTime;
            if (_currentUploadHashes.Count > 0)
            {
                uploadHashes = new HashSet<string>(_currentUploadHashes, StringComparer.OrdinalIgnoreCase);
            }
        }

        if (uploadHashes != null && uploadHashes.Count > 0)
        {
            currentUploads = currentUploads
                .Where(c => uploadHashes.Contains(c.Hash))
                .ToList();
        }

        if (!currentUploads.Any())
        {
            if (!string.IsNullOrWhiteSpace(_sendPenumbraModStatusText))
            {
                SpheneUI.Text.Colored(_sendPenumbraModStatusText,
                    _sendPenumbraModStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey);
            }
            else
            {
                SpheneUI.Text.Muted("Preparing packages…");
            }
            return;
        }

        var totalUploaded = currentUploads.Sum(c => c.Transferred);
        var totalToUpload = currentUploads.Sum(c => c.Total);
        var uploadProgress = totalToUpload > 0 ? (float)totalUploaded / totalToUpload : 0f;
        var uploadText = $"{UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)}";

        if (uploadStartTime.HasValue)
        {
            var elapsed = DateTime.UtcNow - uploadStartTime.Value;
            if (elapsed.TotalSeconds > 0)
            {
                var bytesPerSecond = totalUploaded / elapsed.TotalSeconds;
                if (bytesPerSecond > 0)
                {
                    uploadText += $" ({UiSharedService.ByteToString((long)bytesPerSecond)}/s)";
                }
            }
        }

        var rowY = ImGui.GetCursorPos().Y;
        var barH = ImGui.GetFrameHeight() * 0.5f;
        var barW = MathF.Min(160f * scale, ImGui.GetContentRegionAvail().X * 0.35f);
        ImGui.SetCursorPosY(rowY + (ImGui.GetFrameHeight() - barH) * 0.5f);
        SpheneUI.Controls.ProgressBar(uploadProgress, new Vector2(barW, barH));
        ImGui.SameLine();
        ImGui.SetCursorPosY(rowY);
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Colored(uploadText, ImGuiColors.DalamudGrey);
    }

    private void LoadPenumbraMods()
    {
        try
        {
            var dict = QueryNormalizedPenumbraModMap();
            ApplyPenumbraModMap(dict);
        }
        catch (Exception ex)
        {
            _penumbraMods.Clear();
            _sendPenumbraModStatusIsError = true;
            _sendPenumbraModStatusText = "Failed to query Penumbra mod list.";
            _logger.LogWarning(ex, "Failed to query Penumbra mod list");
        }
    }

    private void OnPenumbraModAdded(string _)
    {
        RequestPenumbraModReload();
    }

    private void OnPenumbraModDeleted(string _)
    {
        RequestPenumbraModReload();
    }

    private void OnPenumbraModMoved(string _, string __)
    {
        RequestPenumbraModReload();
    }

    private void OnPenumbraModPathChanged(string _, string __)
    {
        RequestPenumbraModReload();
    }

    private void ValidatePenumbraModListSnapshot()
    {
        if (DateTime.UtcNow < _nextPenumbraModListValidationUtc)
        {
            return;
        }

        _nextPenumbraModListValidationUtc = DateTime.UtcNow.AddSeconds(2);

        try
        {
            var current = QueryNormalizedPenumbraModMap();
            if (IsSamePenumbraModMap(current))
            {
                return;
            }

            ApplyPenumbraModMap(current);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to validate Penumbra mod list snapshot");
        }
    }

    private Dictionary<string, string> QueryNormalizedPenumbraModMap()
    {
        var raw = _shrinkuPenumbraIpc.GetModList();
        var normalized = new Dictionary<string, string>(raw.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in raw)
        {
            var key = kvp.Key ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized[key] = string.IsNullOrWhiteSpace(kvp.Value) ? key : kvp.Value!;
        }

        return normalized;
    }

    private bool IsSamePenumbraModMap(IReadOnlyDictionary<string, string> current)
    {
        if (current.Count != _lastKnownPenumbraModMap.Count)
        {
            return false;
        }

        foreach (var kvp in current)
        {
            if (!_lastKnownPenumbraModMap.TryGetValue(kvp.Key, out var existing))
            {
                return false;
            }

            if (!string.Equals(existing, kvp.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyPenumbraModMap(Dictionary<string, string> dict)
    {
        _lastKnownPenumbraModMap.Clear();
        foreach (var kvp in dict)
        {
            _lastKnownPenumbraModMap[kvp.Key] = kvp.Value;
        }

        _penumbraMods.Clear();
        _penumbraMods.AddRange(dict
            .Select(kvp => (ModFolderName: kvp.Key, DisplayName: kvp.Value))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase));
    }

    private void RequestPenumbraModReload()
    {
        Interlocked.Exchange(ref _reloadPenumbraModsRequested, 1);
    }

    private bool ShouldReloadPenumbraMods()
    {
        return Interlocked.Exchange(ref _reloadPenumbraModsRequested, 0) == 1;
    }

    private List<(string ModFolderName, string DisplayName)> GetFilteredPenumbraMods()
    {
        if (_penumbraMods.Count == 0) return new List<(string ModFolderName, string DisplayName)>();
        if (string.IsNullOrWhiteSpace(_sendPenumbraModFilter)) return _penumbraMods;

        var filter = _sendPenumbraModFilter.Trim();
        return _penumbraMods
            .Where(m => m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) || m.ModFolderName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void StartSendPenumbraMods(IReadOnlyCollection<string> modFolderNames, bool sendCurrentModState)
    {
        if (_isSendingPenumbraMod) return;

        _sendPenumbraModStatusIsError = false;
        _sendPenumbraModStatusText = "Starting…";
        _isSendingPenumbraMod = true;

        try { _sendPenumbraModCts?.Cancel(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel prior SendPenumbraMod"); }
        try { _sendPenumbraModCts?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose prior SendPenumbraMod cancellation token source"); }

        _sendPenumbraModCts = new CancellationTokenSource();
        var token = _sendPenumbraModCts.Token;
        var recipients = _selectedPenumbraRecipients
            .Select(uid => _pairManager.GetPairByUID(uid)?.UserData)
            .Where(u => u != null && !string.IsNullOrWhiteSpace(u.UID))
            .Select(u => u!)
            .Distinct(UserDataComparer.Instance)
            .ToList();

        if (recipients.Count == 0)
        {
            _sendPenumbraModStatusIsError = true;
            _sendPenumbraModStatusText = "No recipients selected for upload.";
            _isSendingPenumbraMod = false;
            return;
        }
        
        var selectedMods = modFolderNames
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedMods.Count == 0)
        {
            _sendPenumbraModStatusIsError = true;
            _sendPenumbraModStatusText = "No mods selected for upload.";
            _isSendingPenumbraMod = false;
            return;
        }

        var sendCurrentSnapshot = sendCurrentModState;
        var sendTask = Task.Run(async () =>
        {
            var createdCacheEntries = new List<Sphene.FileCache.FileCacheEntity>();
            try
            {
                var hashesToUpload = new List<string>();
                var hashToModName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var modInfoByHash = new Dictionary<string, ModInfoDto>(StringComparer.OrdinalIgnoreCase);
                
                for (int i = 0; i < selectedMods.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var modFolderName = selectedMods[i];
                    _sendPenumbraModStatusIsError = false;
                    _sendPenumbraModStatusText = $"Preparing {i + 1}/{selectedMods.Count}: {modFolderName}";

                    string? tempPmpToDelete = null;
                    string? pmpPath;
                    try
                    {
                        if (sendCurrentSnapshot)
                        {
                            _sendPenumbraModStatusText = $"Creating temporary package {i + 1}/{selectedMods.Count}: {modFolderName}";
                            pmpPath = await CreateTemporaryPmpFromCurrentModStateAsync(modFolderName, token).ConfigureAwait(false);
                            tempPmpToDelete = pmpPath;
                        }
                        else
                        {
                            var pmpBackups = await _shrinkuBackupService.GetPmpBackupsForModAsync(modFolderName).ConfigureAwait(false);
                            pmpPath = GetLatestExistingFile(pmpBackups);
                            if (string.IsNullOrWhiteSpace(pmpPath))
                            {
                                _sendPenumbraModStatusText = $"No backup found, creating temporary package {i + 1}/{selectedMods.Count}: {modFolderName}";
                                pmpPath = await CreateTemporaryPmpFromCurrentModStateAsync(modFolderName, token).ConfigureAwait(false);
                                tempPmpToDelete = pmpPath;
                            }
                        }

                        token.ThrowIfCancellationRequested();

                        if (string.IsNullOrWhiteSpace(pmpPath))
                        {
                            _sendPenumbraModStatusIsError = true;
                            _sendPenumbraModStatusText = $"No .pmp file was found for '{modFolderName}'.";
                            return;
                        }

                        var hash = Sphene.Utils.Crypto.GetFileHash(pmpPath);
                        if (!hashesToUpload.Contains(hash, StringComparer.OrdinalIgnoreCase))
                        {
                            hashesToUpload.Add(hash);
                        }
                        if (!hashToModName.ContainsKey(hash))
                        {
                            hashToModName[hash] = modFolderName;
                        }

                        if (!modInfoByHash.ContainsKey(hash))
                        {
                            string? folderHash = null;
                            try
                            {
                                var modAbsolutePath = _shrinkuBackupService.GetModAbsolutePath(modFolderName);
                                if (!string.IsNullOrWhiteSpace(modAbsolutePath))
                                {
                                    folderHash = ComputeModFolderHash(modAbsolutePath, token);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to compute folder hash for mod {modFolderName}", modFolderName);
                            }

                            var metadata = await _shrinkuPenumbraIpc.GetModMetadataAsync(modFolderName).ConfigureAwait(false);
                            if (metadata != null)
                            {
                                var normalizedName = NormalizeRequiredText(metadata.Name, modFolderName, removeBackticks: false);
                                modInfoByHash[hash] = new ModInfoDto(
                                    hash,
                                    normalizedName,
                                    NormalizeOptionalText(metadata.Author, removeBackticks: false),
                                    NormalizeOptionalText(metadata.Version, removeBackticks: false),
                                    NormalizeOptionalText(metadata.Description, removeBackticks: false),
                                    NormalizeOptionalText(metadata.Website, removeBackticks: true),
                                    folderHash);
                            }
                            else
                            {
                                modInfoByHash[hash] = new ModInfoDto(hash, NormalizeRequiredText(modFolderName, modFolderName, removeBackticks: false), null, null, null, null, folderHash);
                            }
                        }

                        if (_fileCacheManager.GetFileCacheByHash(hash) == null)
                        {
                            var cacheTarget = Path.Combine(_configService.Current.CacheFolder, hash + ".pmp");
                            Directory.CreateDirectory(_configService.Current.CacheFolder);
                            File.Copy(pmpPath, cacheTarget, overwrite: true);

                            token.ThrowIfCancellationRequested();

                            var entry = _fileCacheManager.CreateCacheEntry(cacheTarget);
                            if (entry == null)
                            {
                                _sendPenumbraModStatusIsError = true;
                                _sendPenumbraModStatusText = "Failed to register the backup in cache.";
                                return;
                            }

                            createdCacheEntries.Add(entry);
                        }
                    }
                    finally
                    {
                        if (!string.IsNullOrWhiteSpace(tempPmpToDelete))
                        {
                            try { File.Delete(tempPmpToDelete); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete temporary PMP {path}", tempPmpToDelete); }
                        }
                    }
                }

                token.ThrowIfCancellationRequested();

                _sendPenumbraModStatusIsError = false;
                _sendPenumbraModStatusText = "Uploading…";

                var uploadProgress = new Progress<string>(s =>
                {
                    _sendPenumbraModStatusIsError = false;
                    _sendPenumbraModStatusText = s ?? string.Empty;
                });

                lock (_uploadStateLock)
                {
                    _currentUploadHashes.Clear();
                    _currentUploadHashes.UnionWith(hashesToUpload);
                    _uploadStartTime = DateTime.UtcNow;
                }

                var uploadResult = await _fileUploadManager.UploadFilesForUsers(
                    hashesToUpload,
                    recipients,
                    uploadProgress,
                    token,
                    hashToModName,
                    modInfoByHash.Values.ToList()).ConfigureAwait(false);

                if (uploadResult.LocallyMissingHashes.Count > 0 || uploadResult.ForbiddenHashes.Count > 0 || uploadResult.FailedHashes.Count > 0)
                {
                    var all = uploadResult.LocallyMissingHashes
                        .Concat(uploadResult.ForbiddenHashes)
                        .Concat(uploadResult.FailedHashes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    _sendPenumbraModStatusIsError = true;
                    _sendPenumbraModStatusText = "Upload failed or forbidden for: " + string.Join(", ", all);
                    return;
                }

                if (uploadResult.SkippedTooLargeHashes.Count > 0)
                {
                    _sendPenumbraModStatusIsError = true;
                    _sendPenumbraModStatusText = "Some mod packages exceed 1GB and were skipped (cannot be uploaded): " + string.Join(", ", uploadResult.SkippedTooLargeHashes);
                    return;
                }

                _sendPenumbraModStatusIsError = false;
                _sendPenumbraModStatusText = "Upload finished. Recipients will be notified automatically.";
            }
            catch (OperationCanceledException)
            {
                _sendPenumbraModStatusIsError = false;
                _sendPenumbraModStatusText = "Cancelled.";
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("FilesSend failed", StringComparison.Ordinal))
            {
                _sendPenumbraModStatusIsError = true;
                _sendPenumbraModStatusText = "Upload request failed: " + ex.Message;
                _logger.LogWarning(ex, "FilesSend failed during Send Penumbra Mod");
            }
            catch (Exception ex)
            {
                _sendPenumbraModStatusIsError = true;
                _sendPenumbraModStatusText = "Unexpected error during send: " + ex.Message;
                _logger.LogWarning(ex, "Unexpected error during Send Penumbra Mod for multiple recipients");
            }
            finally
            {
                if (createdCacheEntries.Count > 0)
                {
                    foreach (var entry in createdCacheEntries)
                    {
                        if (entry == null || !entry.IsCacheEntry)
                        {
                            continue;
                        }

                        var resolvedPath = entry.ResolvedFilepath;
                        var safeToRemoveDb = false;

                        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                        {
                            safeToRemoveDb = true;
                        }
                        else
                        {
                            try
                            {
                                File.Delete(resolvedPath);
                                safeToRemoveDb = true;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to delete cached PMP {path}", resolvedPath);
                            }
                        }

                        if (safeToRemoveDb)
                        {
                            try
                            {
                                _fileCacheManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to remove cached PMP entry {hash}:{path}", entry.Hash, entry.PrefixedFilePath);
                            }
                        }
                    }

                    try
                    {
                        _fileCacheManager.WriteOutFullCsv();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to persist cache database after cached PMP cleanup");
                    }
                }

                _isSendingPenumbraMod = false;
                lock (_uploadStateLock)
                {
                    _uploadStartTime = null;
                    _currentUploadHashes.Clear();
                }
                try { _sendPenumbraModCts?.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose SendPenumbraMod cancellation token source"); }
                _sendPenumbraModCts = null;
            }
        }, token);

        _ = sendTask.ContinueWith(t =>
        {
            _logger.LogError(t.Exception, "Unobserved exception in StartSendPenumbraMods task");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task<string?> CreateTemporaryPmpFromCurrentModStateAsync(string modFolderName, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            var root = _shrinkuPenumbraIpc.ModDirectory;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(modFolderName))
            {
                return null;
            }

            var modAbs = Path.Combine(root, modFolderName);
            if (!Directory.Exists(modAbs))
            {
                return null;
            }

            var cacheFolder = _configService.Current.CacheFolder;
            if (string.IsNullOrWhiteSpace(cacheFolder))
            {
                return null;
            }

            var tempDir = Path.Combine(cacheFolder, "temp_mod_send");
            Directory.CreateDirectory(tempDir);

            var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.pmp");
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                CreateDeterministicZip(modAbs, tempPath, token);
            }, token).ConfigureAwait(false);

            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static void CreateDeterministicZip(string sourceDirectory, string destinationArchive, CancellationToken token)
    {
        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);

        using var zipToOpen = new FileStream(destinationArchive, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipToOpen, ZipArchiveMode.Create);

        var sourceDirLen = sourceDirectory.Length;
        if (!sourceDirectory.EndsWith(Path.DirectorySeparatorChar) && !sourceDirectory.EndsWith(Path.AltDirectorySeparatorChar))
        {
            sourceDirLen++;
        }

        var fixedTime = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relPath = file.Length > sourceDirLen ? file.Substring(sourceDirLen) : Path.GetFileName(file);
            relPath = relPath.Replace('\\', '/');

            var entry = archive.CreateEntry(relPath, CompressionLevel.Fastest);
            entry.LastWriteTime = fixedTime;

            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            fileStream.CopyTo(entryStream);
        }
    }

    private static string? GetLatestExistingFile(IEnumerable<string> filePaths)
    {
        string? best = null;
        DateTime bestTime = DateTime.MinValue;

        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            var writeTime = File.GetLastWriteTimeUtc(path);
            if (best == null || writeTime > bestTime)
            {
                best = path;
                bestTime = writeTime;
            }
        }

        return best;
    }

    private static string? NormalizeOptionalText(string? value, bool removeBackticks)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (removeBackticks && normalized.IndexOf(WebsiteBacktick, StringComparison.Ordinal) >= 0)
        {
            normalized = normalized.Replace(WebsiteBacktick.ToString(), string.Empty, StringComparison.Ordinal).Trim();
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeRequiredText(string? value, string fallback, bool removeBackticks)
    {
        var normalized = NormalizeOptionalText(value, removeBackticks);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        normalized = NormalizeOptionalText(fallback, removeBackticks: false);
        return normalized ?? string.Empty;
    }

    private static string? ComputeModFolderHash(string modAbsolutePath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(modAbsolutePath))
        {
            return null;
        }

        var root = Path.GetFullPath(modAbsolutePath);
        if (!Directory.Exists(root))
        {
            return null;
        }

        var files = new List<string>(4096);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files.Add(file);
        }

        files.Sort(StringComparer.Ordinal);

        using var sha1 = SHA1.Create();
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var file = files[i];
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch
            {
                length = 0;
            }

            var fileHash = ComputeFileSha1HexUpper(file, token);
            var line = $"{relative}|{length}|{fileHash}";
            var bytes = Encoding.UTF8.GetBytes(line);
            sha1.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha1.TransformBlock(_newLineUtf8, 0, _newLineUtf8.Length, null, 0);
        }

        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha1.Hash ?? Array.Empty<byte>());
    }

    private static string ComputeFileSha1HexUpper(string filePath, CancellationToken token)
    {
        using var sha1 = SHA1.Create();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            sha1.TransformBlock(buffer, 0, read, null, 0);
        }

        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha1.Hash ?? Array.Empty<byte>());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shrinkuPenumbraIpc.ModAdded -= OnPenumbraModAdded;
            _shrinkuPenumbraIpc.ModDeleted -= OnPenumbraModDeleted;
            _shrinkuPenumbraIpc.ModMoved -= OnPenumbraModMoved;
            _shrinkuPenumbraIpc.ModPathChanged -= OnPenumbraModPathChanged;
        }

        base.Dispose(disposing);
    }
}
