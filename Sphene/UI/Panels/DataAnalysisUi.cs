using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.API.Data.Enum;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.SpheneConfiguration;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class DataAnalysisUi : WindowMediatorSubscriberBase
{
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly Progress<(string, int)> _conversionProgress = new();
    private readonly IpcManager _ipcManager;
    private readonly UiSharedService _uiSharedService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfig;
    private readonly TransientResourceManager _transientResourceManager;
    private readonly TransientConfigService _transientConfigService;
    private readonly TextureBackupService _textureBackupService;
    private readonly ShrinkU.Services.TextureBackupService _shrinkuBackupService;
    private readonly ShrinkU.Services.TextureConversionService _shrinkuConversionService;
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private readonly CancellationTokenSource _conversionCancellationTokenSource = new();
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

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, SpheneMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService, UiSharedService uiSharedService,
        PlayerPerformanceConfigService playerPerformanceConfig, TransientResourceManager transientResourceManager,
        TransientConfigService transientConfigService, TextureBackupService textureBackupService,
        ShrinkU.Services.TextureBackupService shrinkuBackupService,
        ShrinkU.Services.TextureConversionService shrinkuConversionService)
        : base(logger, mediator, "Sphene Character Data Analysis", performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _ipcManager = ipcManager;
        _uiSharedService = uiSharedService;
        _playerPerformanceConfig = playerPerformanceConfig;
        _transientResourceManager = transientResourceManager;
        _transientConfigService = transientConfigService;
        _textureBackupService = textureBackupService;
        _shrinkuBackupService = shrinkuBackupService;
        _shrinkuConversionService = shrinkuConversionService;
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) =>
        {
            _hasUpdate = true;
        });
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

    protected override void DrawInternal()
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

        if (_hasUpdate)
        {
            _cachedAnalysis = _characterAnalyzer.LastAnalysis.DeepClone();
            _cachedBackupsForAnalysis = null; // Invalidate backup cache when analysis changes
            _hasUpdate = false;
        }

        SpheneUI.Text.PageHeader("Character Data Analysis",
            "Inspect the files attached to your character — analysis, transient data, and recordings.");

        if (SpheneUI.Layout.BeginScrollArea("analysis-content", ImGui.GetContentRegionAvail(),
                contentPadding: Vector2.Zero))
        {
            DrawAnalysisTabs();
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawAnalysisTabs()
    {
        using var tabBar = ImRaii.TabBar("analysisRecordingTabBar");
        using (var tabItem = ImRaii.TabItem("Analysis"))
        {
            if (tabItem)
            {
                using var id = ImRaii.PushId("analysis");
                DrawAnalysis();
            }
        }
        using (var tabItem = ImRaii.TabItem("Transient Files"))
        {
            if (tabItem)
            {
                using var tabbar = ImRaii.TabBar("transientData");

                using (var transientData = ImRaii.TabItem("Stored Transient File Data"))
                {
                    using var id = ImRaii.PushId("data");

                    if (transientData)
                    {
                        DrawStoredData();
                    }
                }
                using (var transientRecord = ImRaii.TabItem("Record Transient Data"))
                {
                    using var id = ImRaii.PushId("recording");

                    if (transientRecord)
                    {
                        DrawRecording();
                    }
                }
            }
        }
    }

    private bool _showAlreadyAddedTransients = false;
    private bool _acknowledgeReview = false;
    private const string StoredAllJobsKey = "alljobs";
    private const string StoredPetPrefix = "pet:";
    private const string StoredMinionMountSessionKey = "minionmount:session";
    private string _selectedStoredCharacter = string.Empty;
    private string _selectedJobEntry = string.Empty;
    private readonly List<string> _storedPathsToRemove = [];
    private readonly Dictionary<string, string> _filePathResolve = [];
    private string _filterGamePath = string.Empty;
    private string _filterFilePath = string.Empty;

    private void DrawStoredData()
    {
        SpheneUI.Layout.Collapsible("What is this? (Explanation / Help)", () =>
        {
            SpheneUI.Text.Wrapped("This tab allows you to see which transient files are attached to your character.");
            SpheneUI.Text.Wrapped("Transient files are files that cannot be resolved to your character permanently. Sphene gathers these files in the background while you execute animations, VFX, sound effects, etc.");
            SpheneUI.Text.Wrapped("When sending your character data to others, Sphene will combine the files listed in \"All Jobs\" and the corresponding currently used job.");
            SpheneUI.Text.Wrapped("The purpose of this tab is primarily informational for you to see which files you are carrying with you. You can remove added game paths, however if you are using the animations etc. again, "
                + "Sphene will automatically attach these after using them. If you disable associated mods in Penumbra, the associated entries here will also be deleted automatically.");
        }, icon: FontAwesomeIcon.QuestionCircle);

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
        Vector2 availableContentRegion = Vector2.Zero;
        var scale = SpheneUI.Scale;
        using (ImRaii.Group())
        {
            SpheneUI.Text.Muted("Character");
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            availableContentRegion = ImGui.GetContentRegionAvail();
            using (ImRaii.ListBox("##characters", new Vector2(200 * scale, availableContentRegion.Y)))
            {
                foreach (var entry in config)
                {
                    var name = entry.Key.Split("_");
                    if (!_uiSharedService.WorldData.TryGetValue(ushort.Parse(name[1]), out var worldname))
                    {
                        continue;
                    }
                    if (SpheneUI.Controls.Selectable(name[0] + " (" + worldname + ")", string.Equals(_selectedStoredCharacter, entry.Key, StringComparison.Ordinal)))
                    {
                        _selectedStoredCharacter = entry.Key;
                        _selectedJobEntry = string.Empty;
                        _storedPathsToRemove.Clear();
                        _filePathResolve.Clear();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
            }
        }
        ImGui.SameLine();
        config.TryGetValue(_selectedStoredCharacter ?? string.Empty, out var transientStorage);
        bool selectedData = !string.IsNullOrWhiteSpace(_selectedStoredCharacter) && transientStorage != null;
        using (ImRaii.Group())
        {
            SpheneUI.Text.Muted("Job");
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            using (ImRaii.ListBox("##data", new Vector2(150 * scale, availableContentRegion.Y)))
            {
                if (selectedData)
                {
                    if (SpheneUI.Controls.Selectable("All Jobs", string.Equals(_selectedJobEntry, StoredAllJobsKey, StringComparison.Ordinal)))
                    {
                        _selectedJobEntry = StoredAllJobsKey;
                    }
                    var renderedPetJobs = new HashSet<uint>();
                    foreach (var job in transientStorage!.JobSpecificCache)
                    {
                        if (!_uiSharedService.JobData.TryGetValue(job.Key, out var jobName)) continue;
                        if (SpheneUI.Controls.Selectable(jobName, string.Equals(_selectedJobEntry, job.Key.ToString(), StringComparison.Ordinal)))
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
                            if (SpheneUI.Controls.Selectable(string.Concat("  Pets - ", jobName), string.Equals(_selectedJobEntry, petKeyForJob, StringComparison.Ordinal)))
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
                        if (SpheneUI.Controls.Selectable(string.Concat("Pets - ", jobName), string.Equals(_selectedJobEntry, petKey, StringComparison.Ordinal)))
                        {
                            _selectedJobEntry = petKey;
                            _storedPathsToRemove.Clear();
                            _filePathResolve.Clear();
                            _filterFilePath = string.Empty;
                            _filterGamePath = string.Empty;
                        }
                    }
                    if (SpheneUI.Controls.Selectable("Minion/Mount (Session)", string.Equals(_selectedJobEntry, StoredMinionMountSessionKey, StringComparison.Ordinal)))
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
        ImGui.SameLine();
        using (ImRaii.Group())
        {
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
            SpheneUI.Text.Muted($"Attached Files · {selectedList.Count}");
            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedJobEntry)))
            {

                var restContent = ImGui.GetContentRegionAvail().X;
                using var group = ImRaii.Group();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Resolve Game Paths to used File Paths"))
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
                ImGui.SameLine();
                SpheneUI.Layout.Space(SpheneUI.Spacing.X20, SpheneUI.Spacing.Hairline);
                ImGui.SameLine();
                using (ImRaii.Disabled(!_storedPathsToRemove.Any() || isMinionMountSession))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Remove selected Game Paths"))
                    {
                        foreach (var item in _storedPathsToRemove)
                        {
                            selectedList.Remove(item);
                        }

                        _transientConfigService.Save();
                        _transientResourceManager.RebuildSemiTransientResources();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!UiSharedService.CtrlPressed() || isMinionMountSession))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Clear ALL Game Paths"))
                    {
                        selectedList.Clear();
                        _transientConfigService.Save();
                        _transientResourceManager.RebuildSemiTransientResources();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
                if (isMinionMountSession)
                {
                    SpheneUI.AttachTooltip("Minion/Mount Session entries are runtime transients and currently read-only in this view.");
                }
                SpheneUI.AttachTooltip("Hold CTRL to delete all game paths from the displayed list"
                    + SpheneUI.TooltipSeparator + "You usually do not need to do this. All animation and VFX data will be automatically handled through Sphene.");
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                var checkW = 30f * scale;
                var filterW = (restContent - checkW - ImGui.GetStyle().ItemSpacing.X) / 2f;
                ImGui.SetNextItemWidth(filterW);
                SpheneUI.Controls.InputTextWithHint("##filterGamePath", "Filter by Game Path", ref _filterGamePath, 255);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(filterW);
                SpheneUI.Controls.InputTextWithHint("##filterFilePath", "Filter by File Path", ref _filterFilePath, 255);

                using (var dataTable = ImRaii.Table("##table", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg))
                {
                    if (dataTable)
                    {
                        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, checkW);
                        ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthFixed, (restContent - checkW) / 2f);
                        ImGui.TableSetupColumn("File Path", ImGuiTableColumnFlags.WidthFixed, (restContent - checkW) / 2f);
                        ImGui.TableSetupScrollFreeze(0, 1);
                        ImGui.TableHeadersRow();
                        int id = 0;
                        foreach (var entry in selectedList)
                        {
                            if (!string.IsNullOrWhiteSpace(_filterGamePath) && !entry.Contains(_filterGamePath, StringComparison.OrdinalIgnoreCase))
                                continue;
                            bool hasFileResolve = _filePathResolve.TryGetValue(entry, out var filePath);

                            if (hasFileResolve && !string.IsNullOrEmpty(_filterFilePath) && !filePath!.Contains(_filterFilePath, StringComparison.OrdinalIgnoreCase))
                                continue;

                            using var imguiid = ImRaii.PushId(id++);
                            ImGui.TableNextColumn();
                            bool isSelected = _storedPathsToRemove.Contains(entry, StringComparer.Ordinal);
                            if (SpheneUI.Controls.Checkbox("##", ref isSelected))
                            {
                                if (isSelected)
                                    _storedPathsToRemove.Add(entry);
                                else
                                    _storedPathsToRemove.Remove(entry);
                            }
                            ImGui.TableNextColumn();
                            SpheneUI.Text.Plain(entry);
                            SpheneUI.AttachTooltip(entry + SpheneUI.TooltipSeparator + "Click to copy to clipboard");
                            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                            {
                                ImGui.SetClipboardText(entry);
                            }
                            ImGui.TableNextColumn();
                            if (hasFileResolve)
                            {
                                SpheneUI.Text.Plain(filePath ?? "Unk");
                                SpheneUI.AttachTooltip(filePath ?? "Unk" + SpheneUI.TooltipSeparator + "Click to copy to clipboard");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                                {
                                    ImGui.SetClipboardText(filePath);
                                }
                            }
                            else
                            {
                                SpheneUI.Text.Plain("-");
                                SpheneUI.AttachTooltip("Resolve Game Paths to used File Paths to display the associated file paths.");
                            }
                        }
                    }
                }
            }
        }
    }

    private void DrawRecording()
    {
        SpheneUI.Layout.Collapsible("What is this? (Explanation / Help)", () =>
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
        }, icon: FontAwesomeIcon.QuestionCircle);
        using (ImRaii.Disabled(_transientResourceManager.IsTransientRecording))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Play, "Start Transient Recording"))
            {
                _transientRecordCts.Cancel();
                _transientRecordCts.Dispose();
                _transientRecordCts = new();
                _transientResourceManager.StartRecording(_transientRecordCts.Token);
                _acknowledgeReview = false;
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!_transientResourceManager.IsTransientRecording))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Stop, "Stop Transient Recording"))
            {
                _transientRecordCts.Cancel();
            }
        }
        if (_transientResourceManager.IsTransientRecording)
        {
            ImGui.SameLine();
            SpheneUI.Text.Colored($"RECORDING - Time Remaining: {_transientResourceManager.RecordTimeRemaining.Value}", ImGuiColors.DalamudYellow);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Layout.Callout("Do not change your appearance or mods while recording — you can accidentally make some of your appearance related mods permanent.",
                FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudRed, "Recording active");
        }

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Controls.Toggle("Show previously added transient files in the recording", ref _showAlreadyAddedTransients);
        SpheneUI.Controls.HelpText("Use this only if you want to see what was previously already caught by Sphene");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        using (ImRaii.Disabled(_transientResourceManager.IsTransientRecording || _transientResourceManager.RecordedTransients.All(k => !k.AddTransient) || !_acknowledgeReview))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Save, "Save Recorded Transient Data"))
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
        using var table = ImRaii.Table("Recorded Transients", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);
        if (table)
        {
            int id = 0;
            var checkW = 30f * SpheneUI.Scale;
            var ownerW = 100f * SpheneUI.Scale;
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, checkW);
            ImGui.TableSetupColumn("Owner", ImGuiTableColumnFlags.WidthFixed, ownerW);
            ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthFixed, (width.X - checkW - ownerW) / 2f);
            ImGui.TableSetupColumn("File Path", ImGuiTableColumnFlags.WidthFixed, (width.X - checkW - ownerW) / 2f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            var transients = _transientResourceManager.RecordedTransients.ToList();
            transients.Reverse();
            foreach (var value in transients)
            {
                if (value.AlreadyTransient && !_showAlreadyAddedTransients)
                    continue;

                using var imguiid = ImRaii.PushId(id++);
                if (value.AlreadyTransient)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                }
                ImGui.TableNextColumn();
                bool addTransient = value.AddTransient;
                if (SpheneUI.Controls.Checkbox("##add", ref addTransient))
                {
                    value.AddTransient = addTransient;
                }
                ImGui.TableNextColumn();
                SpheneUI.Text.Plain(value.Owner.Name);
                ImGui.TableNextColumn();
                SpheneUI.Text.Plain(value.GamePath);
                SpheneUI.AttachTooltip(value.GamePath);
                ImGui.TableNextColumn();
                SpheneUI.Text.Plain(value.FilePath);
                SpheneUI.AttachTooltip(value.FilePath);
                if (value.AlreadyTransient)
                {
                    ImGui.PopStyleColor();
                }
            }
        }
    }

    private void DrawAnalysis()
    {
        SpheneUI.Layout.Collapsible("What is this? (Explanation / Help)", () =>
        {
            SpheneUI.Text.Wrapped("This tab shows you all files and their sizes that are currently in use through your character and associated entities in Sphene");
        }, icon: FontAwesomeIcon.QuestionCircle);

        if (_cachedAnalysis!.Count == 0) return;

        bool isAnalyzing = _characterAnalyzer.IsAnalysisRunning;
        if (isAnalyzing)
        {
            SpheneUI.Text.ColoredWrapped($"Analyzing {_characterAnalyzer.CurrentFile}/{_characterAnalyzer.TotalFiles}",
                ImGuiColors.DalamudYellow);
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.StopCircle, "Cancel analysis"))
            {
                _characterAnalyzer.CancelAnalyze();
            }
        }
        else
        {
            if (_cachedAnalysis!.Any(c => c.Value.Any(f => !f.Value.IsComputed)))
            {
                SpheneUI.Text.ColoredWrapped("Some entries in the analysis have file size not determined yet, press the button below to analyze your current data",
                    ImGuiColors.DalamudYellow);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.PlayCircle, "Start analysis (missing entries)"))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false);
                }
            }
            else
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.PlayCircle, "Start analysis (recalculate all entries)"))
                {
                    _ = _characterAnalyzer.ComputeAnalysis(print: false, recalculate: true);
                }
            }
        }

        SpheneUI.Layout.Separator();

        SpheneUI.Text.Plain("Total files:");
        ImGui.SameLine();
        SpheneUI.Text.Plain(_cachedAnalysis!.Values.Sum(c => c.Values.Count).ToString());
        ImGui.SameLine();
        using (var font = ImRaii.PushFont(UiBuilder.IconFont))
        {
            SpheneUI.Text.Plain(FontAwesomeIcon.InfoCircle.ToIconString());
        }
        if (ImGui.IsItemHovered())
        {
            string text = "";
            var groupedfiles = _cachedAnalysis.Values.SelectMany(f => f.Values).GroupBy(f => f.FileType, StringComparer.Ordinal);
            text = string.Join(Environment.NewLine, groupedfiles.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => f.Key + ": " + f.Count() + " files, size: " + UiSharedService.ByteToString(f.Sum(v => v.OriginalSize))
                + ", compressed: " + UiSharedService.ByteToString(f.Sum(v => v.CompressedSize))));
            ImGui.SetTooltip(text);
        }
        SpheneUI.Text.Plain("Total size (actual):");
        ImGui.SameLine();
        SpheneUI.Text.Plain(UiSharedService.ByteToString(_cachedAnalysis!.Sum(c => c.Value.Sum(c => c.Value.OriginalSize))));
        SpheneUI.Text.Plain("Total size (compressed for up/download only):");
        ImGui.SameLine();
        SpheneUI.Text.Plain(UiSharedService.ByteToString(_cachedAnalysis!.Sum(c => c.Value.Sum(c => c.Value.CompressedSize))));
        SpheneUI.Text.Plain($"Total modded model triangles: {_cachedAnalysis.Sum(c => c.Value.Sum(f => f.Value.Triangles))}");
        SpheneUI.Layout.Separator();
        using var tabbar = ImRaii.TabBar("objectSelection");
        foreach (var kvp in _cachedAnalysis)
        {
            using var id = ImRaii.PushId(kvp.Key.ToString());
            string tabText = kvp.Key.ToString();
            if (kvp.Value.Any(f => !f.Value.IsComputed)) tabText += " (!)";
            using var tab = ImRaii.TabItem(tabText + "###" + kvp.Key.ToString());
            if (tab.Success)
            {
                var groupedfiles = kvp.Value.Select(v => v.Value).GroupBy(f => f.FileType, StringComparer.Ordinal)
                    .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();

                SpheneUI.Text.Plain("Files for " + kvp.Key);
                ImGui.SameLine();
                SpheneUI.Text.Plain(kvp.Value.Count.ToString());
                ImGui.SameLine();

                using (var font = ImRaii.PushFont(UiBuilder.IconFont))
                {
                    SpheneUI.Text.Plain(FontAwesomeIcon.InfoCircle.ToIconString());
                }
                if (ImGui.IsItemHovered())
                {
                    string text = "";
                    text = string.Join(Environment.NewLine, groupedfiles
                        .Select(f => f.Key + ": " + f.Count() + " files, size: " + UiSharedService.ByteToString(f.Sum(v => v.OriginalSize))
                        + ", compressed: " + UiSharedService.ByteToString(f.Sum(v => v.CompressedSize))));
                    ImGui.SetTooltip(text);
                }
                SpheneUI.Text.Plain($"{kvp.Key} size (actual):");
                ImGui.SameLine();
                SpheneUI.Text.Plain(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.OriginalSize)));
                SpheneUI.Text.Plain($"{kvp.Key} size (compressed for up/download only):");
                ImGui.SameLine();
                SpheneUI.Text.Plain(UiSharedService.ByteToString(kvp.Value.Sum(c => c.Value.CompressedSize)));
                SpheneUI.Layout.Separator();

                var vramUsage = groupedfiles.SingleOrDefault(v => string.Equals(v.Key, "tex", StringComparison.Ordinal));
                if (vramUsage != null)
                {
                    var actualVramUsage = vramUsage.Sum(f => f.OriginalSize);
                    SpheneUI.Text.Plain($"{kvp.Key} VRAM usage:");
                    ImGui.SameLine();
                    SpheneUI.Text.Plain(UiSharedService.ByteToString(actualVramUsage));
                    if (_playerPerformanceConfig.Current.WarnOnExceedingThresholds
                        || _playerPerformanceConfig.Current.ShowPerformanceIndicator)
                    {
                        using var _ = ImRaii.PushIndent(10f);
                        var currentVramWarning = _playerPerformanceConfig.Current.VRAMSizeWarningThresholdMiB;
                        SpheneUI.Text.Plain($"Configured VRAM warning threshold: {currentVramWarning} MiB.");
                        if (currentVramWarning * 1024 * 1024 < actualVramUsage)
                        {
                            SpheneUI.Text.Colored($"You exceed your own threshold by " +
                                $"{UiSharedService.ByteToString(actualVramUsage - (currentVramWarning * 1024 * 1024))}.",
                                ImGuiColors.DalamudYellow);
                        }
                    }
                }

                var actualTriCount = kvp.Value.Sum(f => f.Value.Triangles);
                SpheneUI.Text.Plain($"{kvp.Key} modded model triangles: {actualTriCount}");
                if (_playerPerformanceConfig.Current.WarnOnExceedingThresholds
                    || _playerPerformanceConfig.Current.ShowPerformanceIndicator)
                {
                    using var _ = ImRaii.PushIndent(10f);
                    var currentTriWarning = _playerPerformanceConfig.Current.TrisWarningThresholdThousands;
                    SpheneUI.Text.Plain($"Configured triangle warning threshold: {currentTriWarning * 1000} triangles.");
                    if (currentTriWarning * 1000 < actualTriCount)
                    {
                        SpheneUI.Text.Colored($"You exceed your own threshold by " +
                            $"{actualTriCount - (currentTriWarning * 1000)} triangles.",
                            ImGuiColors.DalamudYellow);
                    }
                }

                SpheneUI.Layout.Separator();
                if (_selectedObjectTab != kvp.Key)
                {
                    _selectedHash = string.Empty;
                    _selectedObjectTab = kvp.Key;
                    _selectedFileTypeTab = string.Empty;
                    _texturesToConvert.Clear();
                }

                using var fileTabBar = ImRaii.TabBar("fileTabs");

                foreach (IGrouping<string, CharacterAnalyzer.FileDataEntry>? fileGroup in groupedfiles)
                {
                    string fileGroupText = fileGroup.Key + " [" + fileGroup.Count() + "]";
                    var requiresCompute = fileGroup.Any(k => !k.IsComputed);
                    using var tabcol = ImRaii.PushColor(ImGuiCol.Tab, SpheneUI.ColorUtils.U32(ImGuiColors.DalamudYellow), requiresCompute);
                    if (requiresCompute)
                    {
                        fileGroupText += " (!)";
                    }
                    using var textcol = ImRaii.PushColor(ImGuiCol.Text, SpheneUI.ColorUtils.U32(new(0, 0, 0, 1)),
                        requiresCompute && !string.Equals(_selectedFileTypeTab, fileGroup.Key, StringComparison.Ordinal));
                    using var fileTab = ImRaii.TabItem(fileGroupText + "###" + fileGroup.Key);

                    if (!fileTab) { continue; }

                    if (!string.Equals(fileGroup.Key, _selectedFileTypeTab, StringComparison.Ordinal))
                    {
                        _selectedFileTypeTab = fileGroup.Key;
                        _selectedHash = string.Empty;
                        _texturesToConvert.Clear();
                    }

                    SpheneUI.Text.Plain($"{fileGroup.Key} files");
                    ImGui.SameLine();
                    SpheneUI.Text.Plain(fileGroup.Count().ToString());

                    SpheneUI.Text.Plain($"{fileGroup.Key} files size (actual):");
                    ImGui.SameLine();
                    SpheneUI.Text.Plain(UiSharedService.ByteToString(fileGroup.Sum(c => c.OriginalSize)));

                    SpheneUI.Text.Plain($"{fileGroup.Key} files size (compressed for up/download only):");
                    ImGui.SameLine();
                    SpheneUI.Text.Plain(UiSharedService.ByteToString(fileGroup.Sum(c => c.CompressedSize)));

                    if (string.Equals(_selectedFileTypeTab, "tex", StringComparison.Ordinal))
                    {
                        SpheneUI.Controls.Toggle("Enable BC7 Conversion Mode", ref _enableBc7ConversionMode);
                        if (_enableBc7ConversionMode)
                        {
                            SpheneUI.Controls.Toggle("Create backup before conversion", ref _enableBackupBeforeConversion);
                            SpheneUI.AttachTooltip("Stores original textures prior to conversion. Smaller storage footprint than full-mod PMP but only per-file restore.");
                            if (_enableBackupBeforeConversion)
                            {
                                SpheneUI.Text.ColoredWrapped("Backups will be created in the mod folder under 'sphene_backups' before conversion.", ImGuiColors.ParsedGreen);
                                SpheneUI.Text.ColoredWrapped("Using full-mod PMP backups increases required storage, but enables safer complete mod restoration.", ImGuiColors.DalamudYellow);
                            }
                            
                            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                            SpheneUI.Layout.SectionCard(() =>
                            {
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
                                    SpheneUI.Text.Muted("No texture backups found.");
                                }
                            }, title: "Backup Management");

                            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                            SpheneUI.Layout.SectionCard(() =>
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
                                    var updating = _storageInfoTask?.IsCompleted == false ? " — updating…" : string.Empty;
                                    SpheneUI.Text.Muted($"Total backup storage: {sizeInMB:F2} MB ({fileCount} files){updating}");
                                }
                                else
                                {
                                    SpheneUI.Text.Muted(_storageInfoTask?.IsCompleted == false
                                        ? "Calculating storage usage…" : "No backup files found.");
                                }
                            }, title: "Storage");

                            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
                            SpheneUI.Layout.Callout("Converting textures to BC7 reduces their size drastically and is recommended for large (4k+) textures. "
                                + "Textures utilizing colorsets may produce visual artifacts. Originals are backed up automatically before conversion. "
                                + "All texture duplicates (entries with more than one file path) are converted — this is an expensive operation that can take a while.",
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
                        }
                    }

                    SpheneUI.Layout.Separator();
                    DrawTable(fileGroup);

                    fileTab.Dispose();
                }
            }
        }

        SpheneUI.Layout.Separator();

        SpheneUI.Text.Plain("Selected file:");
        ImGui.SameLine();
        SpheneUI.Text.Colored(_selectedHash, ImGuiColors.DalamudYellow);

        if (_cachedAnalysis.TryGetValue(_selectedObjectTab, out var selectedEntries)
            && selectedEntries.TryGetValue(_selectedHash, out CharacterAnalyzer.FileDataEntry? item))
        {
            var filePaths = item.FilePaths;
            SpheneUI.Text.Plain("Local file path:");
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

            var gamepaths = item.GamePaths;
            SpheneUI.Text.Plain("Used by game path:");
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
        _conversionCancellationTokenSource.Cancel();
        _conversionCancellationTokenSource.Dispose();
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

    private void DrawTable(IGrouping<string, CharacterAnalyzer.FileDataEntry> fileGroup)
    {
        var tableColumns = string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal)
            ? (_enableBc7ConversionMode ? 7 : 6)
            : (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) ? 6 : 5);
        using var table = ImRaii.Table("Analysis", tableColumns, ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, 300));
        if (!table.Success) return;
        ImGui.TableSetupColumn("Hash");
        ImGui.TableSetupColumn("Filepaths");
        ImGui.TableSetupColumn("Gamepaths");
        ImGui.TableSetupColumn("Original Size");
        ImGui.TableSetupColumn("Compressed Size");
        if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn("Format");
            if (_enableBc7ConversionMode) ImGui.TableSetupColumn("Convert to BC7");
        }
        if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
        {
            ImGui.TableSetupColumn("Triangles");
        }
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sortSpecs = ImGui.TableGetSortSpecs();
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
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Triangles).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Ascending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderBy(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal) && idx == 5 && sortSpecs.Specs.SortDirection == ImGuiSortDirection.Descending)
                _cachedAnalysis![_selectedObjectTab] = _cachedAnalysis[_selectedObjectTab].OrderByDescending(k => k.Value.Format.Value, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);

            sortSpecs.SpecsDirty = false;
        }

        foreach (var item in fileGroup)
        {
            using var text = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0, 0, 0, 1), string.Equals(item.Hash, _selectedHash, StringComparison.Ordinal));
            using var text2 = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1), !item.IsComputed);
            ImGui.TableNextColumn();
            if (!item.IsComputed)
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, SpheneUI.ColorUtils.U32(ImGuiColors.DalamudRed));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, SpheneUI.ColorUtils.U32(ImGuiColors.DalamudRed));
            }
            if (string.Equals(_selectedHash, item.Hash, StringComparison.Ordinal))
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, SpheneUI.ColorUtils.U32(ImGuiColors.DalamudYellow));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, SpheneUI.ColorUtils.U32(ImGuiColors.DalamudYellow));
            }
            SpheneUI.Text.Plain(item.Hash);
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            SpheneUI.Text.Plain(item.FilePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            SpheneUI.Text.Plain(item.GamePaths.Count.ToString());
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            SpheneUI.Text.Plain(UiSharedService.ByteToString(item.OriginalSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            ImGui.TableNextColumn();
            SpheneUI.Text.Plain(UiSharedService.ByteToString(item.CompressedSize));
            if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
            if (string.Equals(fileGroup.Key, "tex", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                SpheneUI.Text.Plain(item.Format.Value);
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
                if (_enableBc7ConversionMode)
                {
                    ImGui.TableNextColumn();
                    if (string.Equals(item.Format.Value, "BC7", StringComparison.Ordinal))
                    {
                        SpheneUI.Text.Plain("");
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
            if (string.Equals(fileGroup.Key, "mdl", StringComparison.Ordinal))
            {
                ImGui.TableNextColumn();
                SpheneUI.Text.Plain(item.Triangles.ToString());
                if (ImGui.IsItemClicked()) _selectedHash = item.Hash;
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

    // Time-based backup cleanup helper removed
}
