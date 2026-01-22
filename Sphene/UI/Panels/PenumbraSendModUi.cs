using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
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

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500f, 0f),
            MaximumSize = new Vector2(500f, 1000f),
        };
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
        IsOpen = false;

        Mediator.Subscribe<OpenSendPenumbraModWindow>(this, OnOpenMessage);
    }

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

        using (ImRaii.Child(
                   "SendModsRoot" + idSuffix,
                   new Vector2(0f, 0f),
                   false,
                   isEmbedded ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None))
        {
            UiSharedService.ColorText("Send Mods", ImGuiColors.DalamudWhite);
            UiSharedService.TextWrapped("Select one or more Penumbra mods, then choose who should receive them.");
            ImGui.Separator();

            if (!_shrinkuPenumbraIpc.APIAvailable)
            {
                UiSharedService.ColorText("Penumbra API not available.", ImGuiColors.DalamudRed);
                return;
            }

            if (_penumbraMods.Count == 0)
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

            string selectedLabel;
            if (_selectedPenumbraModFolders.Count == 0)
            {
                selectedLabel = "None";
            }
            else if (_selectedPenumbraModFolders.Count == 1)
            {
                var only = _selectedPenumbraModFolders.First();
                selectedLabel = _penumbraMods.FirstOrDefault(m => string.Equals(m.ModFolderName, only, StringComparison.OrdinalIgnoreCase)).DisplayName ?? only;
            }
            else
            {
                selectedLabel = $"{_selectedPenumbraModFolders.Count} selected";
            }

            var filtered = GetFilteredPenumbraMods();

            if (ImGui.BeginTable("SendModLayout" + idSuffix, 3, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Mods", ImGuiTableColumnFlags.WidthStretch, 0.34f);
                ImGui.TableSetupColumn("Recipients", ImGuiTableColumnFlags.WidthStretch, 0.33f);
                ImGui.TableSetupColumn("Summary", ImGuiTableColumnFlags.WidthStretch, 0.33f);

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                UiSharedService.ColorText("Mods", ImGuiColors.DalamudWhite);
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##penumbraModFilter" + idSuffix, "Filter mods…", ref _sendPenumbraModFilter, 128);
                ImGui.TextDisabled("Hold Ctrl to select multiple mods.");

                if (_selectedPenumbraModFolders.Count > 0 && ImGui.Button("Clear selection##clearMods" + idSuffix))
                {
                    _selectedPenumbraModFolders.Clear();
                }

                using (ImRaii.Child("ModList" + idSuffix, new Vector2(0f, 0f), true))
                {
                    if (filtered.Count == 0)
                    {
                        UiSharedService.ColorText("No mods found.", ImGuiColors.DalamudGrey);
                    }
                    else
                    {
                        foreach (var mod in filtered)
                        {
                            var isSelected = _selectedPenumbraModFolders.Contains(mod.ModFolderName);
                            if (ImGui.Selectable($"{mod.DisplayName}##{mod.ModFolderName}{idSuffix}", isSelected))
                            {
                                _selectedPenumbraModFolder = mod.ModFolderName;
                                if (UiSharedService.CtrlPressed())
                                {
                                    if (!_selectedPenumbraModFolders.Add(mod.ModFolderName))
                                    {
                                        _selectedPenumbraModFolders.Remove(mod.ModFolderName);
                                    }
                                }
                                else
                                {
                                    _selectedPenumbraModFolders.Clear();
                                    _selectedPenumbraModFolders.Add(mod.ModFolderName);
                                }
                            }
                        }
                    }
                }

                ImGui.TableSetColumnIndex(1);
                UiSharedService.ColorText("Recipients", ImGuiColors.DalamudWhite);
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##recipientFilter" + idSuffix, "Filter recipients…", ref _recipientFilter, 64);
                UiSharedService.ColorTextWrapped("Only individually paired users can receive mod packages.", ImGuiColors.DalamudGrey);

                var candidatePairs = _pairManager.DirectPairs
                    .Where(p => p.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
                    .Select(p =>
                    {
                        var uid = p.UserData.UID ?? string.Empty;
                        var aliasOrUid = p.UserData.AliasOrUID;
                        var displayName = _uiSharedService.GetPreferredUserDisplayName(uid, aliasOrUid);
                        return (Pair: p, Uid: uid, AliasOrUid: aliasOrUid, DisplayName: displayName);
                    })
                    .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                using (ImRaii.Child("RecipientList" + idSuffix, new Vector2(0f, 0f), true))
                {
                    if (candidatePairs.Count == 0)
                    {
                        UiSharedService.ColorText("No paired users available for mod delivery.", ImGuiColors.DalamudGrey);
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
                                displayName += " (mods disabled)";
                            }

                            var isSelected = _selectedPenumbraRecipients.Contains(uid);
                            using (ImRaii.Disabled(!allowsMods))
                            {
                                if (ImGui.Checkbox(displayName + "##" + uid + idSuffix, ref isSelected))
                                {
                                    if (isSelected)
                                    {
                                        _selectedPenumbraRecipients.Add(uid);
                                    }
                                    else
                                    {
                                        _selectedPenumbraRecipients.Remove(uid);
                                    }
                                }
                            }

                            if (!allowsMods)
                            {
                                _selectedPenumbraRecipients.Remove(uid);
                            }
                        }
                    }
                }

                ImGui.TableSetColumnIndex(2);
                UiSharedService.ColorText("Summary", ImGuiColors.DalamudWhite);

                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    if (ImGui.Button(FontAwesomeIcon.History.ToIconString() + "##openModPackageHistorySend" + idSuffix))
                    {
                        if (isEmbedded)
                        {
                            Mediator.Publish(new OpenModSharingWindow(ModSharingTab.History));
                        }
                        else
                        {
                            Mediator.Publish(new UiToggleMessage(typeof(ModPackageHistoryUi)));
                        }
                    }
                }
                ImGui.SameLine();
                ImGui.TextUnformatted("History & backups");

                ImGui.Spacing();
                ImGui.TextUnformatted("Selected mods");
                ImGui.SameLine();
                ImGui.TextUnformatted(selectedLabel);

                ImGui.TextUnformatted("Selected recipients");
                ImGui.SameLine();
                ImGui.TextUnformatted(_selectedPenumbraRecipients.Count.ToString());

                ImGui.Separator();

                _uiSharedService.IconText(FontAwesomeIcon.ExclamationCircle);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Mods will be uploaded to the Sphene server network and stored as backups.");
                }
                ImGui.SameLine();
                ImGui.TextWrapped("Mods will be uploaded to the Sphene server network and will be shared with the users you selected.");

                ImGui.Spacing();
                var shareButtonText = _selectedPenumbraModFolders.Count == 1 ? "Share Mod" : "Share Mods";
                var sendCurrent = _sendCurrentModState;
                if (ImGui.Checkbox("Send current mod state (temporary)##sendCurrent" + idSuffix, ref sendCurrent))
                {
                    _sendCurrentModState = sendCurrent;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("When enabled, Sphene packages the current Penumbra mod folder contents into a temporary .pmp, uploads it, then deletes the temporary file. When disabled, Sphene prefers existing ShrinkU backups.");
                }

                using (ImRaii.Disabled(_isSendingPenumbraMod || _selectedPenumbraModFolders.Count == 0 || _selectedPenumbraRecipients.Count == 0))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Share, shareButtonText))
                    {
                        StartSendPenumbraMods(_selectedPenumbraModFolders.ToList(), _sendCurrentModState);
                    }
                }

                if (_isSendingPenumbraMod && _sendPenumbraModCts != null)
                {
                    ImGui.SameLine();
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.TimesCircle, "Cancel"))
                    {
                        try { _sendPenumbraModCts.Cancel(); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to cancel SendPenumbraMod"); }
                    }
                }

                ImGui.Separator();
                UiSharedService.ColorText("Status", ImGuiColors.DalamudWhite);

                if (!string.IsNullOrWhiteSpace(_sendPenumbraModStatusText))
                {
                    var color = _sendPenumbraModStatusIsError ? ImGuiColors.DalamudRed : ImGuiColors.DalamudGrey;
                    UiSharedService.ColorTextWrapped(_sendPenumbraModStatusText, color);
                }

                if (_isSendingPenumbraMod)
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

                    if (currentUploads.Any())
                    {
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

                        var barWidth = ImGui.GetContentRegionAvail().X;
                        var cursor = ImGui.GetCursorPos();
                        var barSize = new Vector2(barWidth, 20f * scale);
                        ImGui.ProgressBar(uploadProgress, barSize);
                        ImGui.SetCursorPos(cursor + new Vector2(0f, barSize.Y + 4f * scale));
                        UiSharedService.ColorText(uploadText, ImGuiColors.DalamudGrey);
                    }
                }

                ImGui.EndTable();
            }
        }
    }

    private void LoadPenumbraMods()
    {
        try
        {
            var dict = _shrinkuPenumbraIpc.GetModList();
            _penumbraMods.Clear();
            _penumbraMods.AddRange(dict
                .Select(kvp => (kvp.Key ?? string.Empty, kvp.Value ?? string.Empty))
                .Where(m => !string.IsNullOrWhiteSpace(m.Item1))
                .Select(m => (ModFolderName: m.Item1, DisplayName: string.IsNullOrWhiteSpace(m.Item2) ? m.Item1 : m.Item2))
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _penumbraMods.Clear();
            _sendPenumbraModStatusIsError = true;
            _sendPenumbraModStatusText = "Failed to query Penumbra mod list.";
            _logger.LogWarning(ex, "Failed to query Penumbra mod list");
        }
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
}
